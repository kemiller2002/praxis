namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// One generated file's intended content at a repository-relative path, or
/// its removal (a stale generated file the previous construction produced).
type ConstructionTreeChange = { Path: string; Content: string option }

/// Git object effects for host-applied construction (Praxis #242). Nothing
/// here touches a working tree or follows a filesystem path: existing content
/// is read from a commit's tree, candidates are built in a private index
/// outside the repository, and only `update-ref NEW OLD` moves the target.
[<RequireQualifiedAccess>]
module ConstructionTree =
    let private maxBlobBytes = 8388608

    let digest (content: string) =
        "sha256:" + (UTF8Encoding(false).GetBytes content |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let private run (git: LocalGitTool) cwd (environment: Map<string, string>) (input: string) (arguments: string list) =
        let spec : LocalWorkerProcessSpec =
            { Executable = git.Executable; ExecutableDigest = git.ExecutableDigest
              Arguments = [ "--no-optional-locks"; "-c"; "core.fsmonitor=false"; "-c"; "core.autocrlf=false"; "-c"; "core.safecrlf=false" ] @ arguments
              WorkingDirectory = cwd; Environment = environment; Input = input
              TimeoutMilliseconds = git.TimeoutMilliseconds; MaxOutputBytes = maxBlobBytes + 1 }
        let observed = (LocalWorkerProcess.runControllerTool spec CancellationToken.None).GetAwaiter().GetResult()
        match observed.Outcome with
        | LocalWorkerProcessOutcome.Exited code when observed.RootExitObserved && observed.ProcessStarted = Some true ->
            Ok(code, observed.StandardOutput, observed.StandardError)
        | other -> Error(sprintf "controller Git unavailable: %A" other)

    let private succeed git cwd environment input arguments =
        run git cwd environment input arguments |> Result.bind (fun (code, output, error) ->
            if code = 0 then Ok output
            else Error(sprintf "git %s exited %d: %s" (List.head arguments) code (error.Trim() |> fun e -> e.Substring(0, min 512 e.Length))))

    /// The repository-relative directory a generated ownership root lands in.
    let placed (mount: string) (path: string) = if mount = "." then path else mount + "/" + path

    let private isObject (value: string) = LocalAgentHandoff.isCommit value

    /// Every ancestor of the ownership root must be a real tree (or absent) in
    /// the target commit; a symlink or submodule there is refused, never followed.
    let private ancestorsAreTrees git repository commit (directory: string) =
        let parts = directory.TrimEnd('/').Split('/')
        [ 1 .. parts.Length ]
        |> List.map (fun count -> String.Join("/", parts |> Array.take count))
        |> List.fold (fun acc prefix ->
            acc |> Result.bind (fun () ->
                run git repository Map.empty "" [ "cat-file"; "-t"; commit + ":" + prefix ] |> Result.bind (function
                    | 0, kind, _ when kind.Trim() = "tree" -> Ok()
                    | 0, kind, _ -> Error(sprintf "construction root ancestor %s is a %s, not a directory" prefix (kind.Trim()))
                    | _ -> Ok()))) (Ok())

    /// Contents of every regular file under the placed ownership root at the
    /// commit, keyed by Ordo-relative path. Links, submodules and executable
    /// or non-UTF-8 entries refuse the whole read.
    let existing git repository commit (mount: string) (ownershipRoot: string) : Result<Map<string, string>, string> =
        let directory = placed mount (ownershipRoot.TrimEnd '/')
        let first = directory.Split('/').[0]
        if not (LocalAgentHandoff.isResourcePath directory) then Error "construction root is not a portable repository path"
        elif [ ".github"; ".claude"; ".echelon"; ".gitattributes"; ".gitmodules" ] |> List.exists (fun denied -> String.Equals(first, denied, StringComparison.OrdinalIgnoreCase)) then
            Error "construction root may not be placed under CI, agent or repository configuration"
        else
            ancestorsAreTrees git repository commit directory |> Result.bind (fun () ->
                succeed git repository Map.empty "" [ "ls-tree"; "-r"; "-z"; "--full-tree"; commit; "--"; ":(literal)" + directory ]
                |> Result.bind (fun listing ->
                    listing.Split('\u0000', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                    |> List.fold (fun acc entry ->
                        acc |> Result.bind (fun (found: Map<string, string>) ->
                            match entry.Split('\t', 2) with
                            | [| header; path |] ->
                                match header.Split(' ') with
                                | [| "100644"; "blob"; objectId |] when isObject objectId ->
                                    succeed git repository Map.empty "" [ "cat-file"; "blob"; objectId ] |> Result.map (fun content ->
                                        let relative = if mount = "." then path else path.Substring(mount.Length + 1)
                                        found.Add(relative, content))
                                | [| mode; _; _ |] -> Error(sprintf "construction root entry %s has mode %s; only regular files are accepted" path mode)
                                | _ -> Error "unexpected Git tree listing"
                            | _ -> Error "unexpected Git tree listing")) (Ok Map.empty)))

    /// Build a candidate commit from `before` plus exactly the changes, in a
    /// private index outside the repository. The commit date is the journal's
    /// preparation time, so a replayed build yields the same object.
    let stage git repository (scratch: string) (committer: LocalIntegrationCommitter) (before: string) (changes: ConstructionTreeChange list) (message: string) (at: DateTimeOffset) =
        let index = Path.Combine(scratch, "index-" + Guid.NewGuid().ToString("N"))
        let date = at.ToUnixTimeSeconds().ToString() + " +0000"
        let environment =
            Map.ofList [ "GIT_INDEX_FILE", index
                         "GIT_AUTHOR_NAME", committer.Name; "GIT_AUTHOR_EMAIL", committer.Email; "GIT_AUTHOR_DATE", date
                         "GIT_COMMITTER_NAME", committer.Name; "GIT_COMMITTER_EMAIL", committer.Email; "GIT_COMMITTER_DATE", date ]
        try
            succeed git repository environment "" [ "read-tree"; before ]
            |> Result.bind (fun _ ->
                changes |> List.fold (fun acc change ->
                    acc |> Result.bind (fun () ->
                        match change.Content with
                        | Some content ->
                            succeed git repository environment content [ "hash-object"; "-w"; "--no-filters"; "--stdin" ]
                            |> Result.bind (fun blob ->
                                succeed git repository environment "" [ "update-index"; "--add"; "--cacheinfo"; "100644," + blob.Trim() + "," + change.Path ])
                            |> Result.map ignore
                        | None -> succeed git repository environment "" [ "update-index"; "--force-remove"; "--"; change.Path ] |> Result.map ignore)) (Ok()))
            |> Result.bind (fun () -> succeed git repository environment "" [ "write-tree" ])
            |> Result.bind (fun tree -> succeed git repository environment "" [ "commit-tree"; tree.Trim(); "-p"; before; "-m"; message ])
            |> Result.bind (fun created ->
                let commit = created.Trim()
                if isObject commit then Ok commit else Error "Git did not create a construction candidate")
        finally
            if File.Exists index then File.Delete index

    /// The candidate differs from `before` only at the planned paths, each
    /// holding exactly its planned content (or absent when removed).
    let verify git repository (before: string) (candidate: string) (changes: ConstructionTreeChange list) =
        succeed git repository Map.empty "" [ "rev-list"; "--parents"; "-n"; "1"; candidate ] |> Result.bind (fun line ->
            if line.Trim().Split(' ') |> Array.toList <> [ candidate; before ] then Error "construction candidate is not a single commit on the target"
            else
                succeed git repository Map.empty "" [ "diff"; "--name-only"; "-z"; "--no-renames"; "--no-ext-diff"; "--no-textconv"; before; candidate; "--" ]
                |> Result.bind (fun names ->
                    let changed = names.Split('\u0000', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
                    let planned = changes |> List.map _.Path |> Set.ofList
                    if not (Set.isSubset changed planned) then Error "construction candidate changes paths outside the plan"
                    else
                        changes |> List.fold (fun acc change ->
                            acc |> Result.bind (fun () ->
                                match change.Content, run git repository Map.empty "" [ "cat-file"; "blob"; candidate + ":" + change.Path ] with
                                | Some content, Ok(0, actual, _) when actual = content -> Ok()
                                | None, Ok(code, _, _) when code <> 0 -> Ok()
                                | _, Error error -> Error error
                                | _ -> Error("construction candidate content differs at " + change.Path))) (Ok())))

    let compareAndSwap git repository (targetRef: string) (candidate: string) (before: string) (reason: string) =
        if not (LocalIntegrationJournal.isTargetRef targetRef) then Error "invalid construction target ref"
        else succeed git repository Map.empty "" [ "update-ref"; "-m"; reason; targetRef; candidate; before ] |> Result.map ignore

    let readRef git repository (targetRef: string) =
        if not (LocalIntegrationJournal.isTargetRef targetRef) then Error "invalid construction target ref"
        else
            succeed git repository Map.empty "" [ "rev-parse"; "--verify"; "--quiet"; targetRef + "^{commit}" ]
            |> Result.bind (fun output -> let commit = output.Trim() in if isObject commit then Ok commit else Error "target ref does not name a commit")

    /// Exact committed bytes of a source document, with their digest.
    let readCommitted git repository (commit: string) (path: string) =
        if not (LocalAgentHandoff.isResourcePath path) then Error "source document path is not portable"
        else
            succeed git repository Map.empty "" [ "cat-file"; "-t"; commit + ":" + path ] |> Result.bind (fun kind ->
                if kind.Trim() <> "blob" then Error("source document is not a committed file: " + path)
                else succeed git repository Map.empty "" [ "cat-file"; "blob"; commit + ":" + path ])
            |> Result.map (fun content -> content, digest content)

    /// The entry at an exact path in a commit's tree: None when absent,
    /// otherwise its mode and, for a regular file, its content. Nothing is
    /// followed; a link, submodule or tree is reported by its mode.
    let entryAt git repository (commit: string) (path: string) : Result<(string * string option) option, string> =
        if not (LocalAgentHandoff.isResourcePath path) then Error("not a portable repository path: " + path)
        else
            succeed git repository Map.empty "" [ "ls-tree"; "-z"; "--full-tree"; commit; "--"; ":(literal)" + path ] |> Result.bind (fun listing ->
                match listing.Split('\u0000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
                | [] -> Ok None
                | [ entry ] ->
                    match entry.Split('\t', 2) with
                    | [| header; listed |] when listed = path ->
                        match header.Split(' ') with
                        | [| "100644"; "blob"; objectId |] when isObject objectId ->
                            succeed git repository Map.empty "" [ "cat-file"; "blob"; objectId ] |> Result.map (fun content -> Some("100644", Some content))
                        | [| mode; _; _ |] -> Ok(Some(mode, None))
                        | _ -> Error "unexpected Git tree listing"
                    | _ -> Error "unexpected Git tree listing"
                | _ -> Error "unexpected Git tree listing")

    /// The most recent commit reachable from `commit` whose message carries
    /// every one of `trailers` as an exact line, or None.
    let findCommitWithTrailers git repository (commit: string) (trailers: string list) : Result<string option, string> =
        let greps = trailers |> List.map (fun trailer -> "--grep=" + trailer)
        succeed git repository Map.empty "" ([ "rev-list"; "--fixed-strings"; "--all-match" ] @ greps @ [ commit; "--" ])
        |> Result.bind (fun listing ->
            listing.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
            |> List.fold (fun acc candidate ->
                acc |> Result.bind (function
                    | Some found -> Ok(Some found)
                    | None ->
                        let candidate = candidate.Trim()
                        succeed git repository Map.empty "" [ "log"; "-1"; "--format=%B"; candidate ] |> Result.map (fun body ->
                            let lines = body.Split('\n') |> Array.map _.TrimEnd('\r') |> Set.ofArray
                            if trailers |> List.forall lines.Contains then Some candidate else None))) (Ok None))

    /// The first parent of a commit, or None for a root commit.
    let parentOf git repository (commit: string) =
        succeed git repository Map.empty "" [ "rev-list"; "--parents"; "-n"; "1"; commit ] |> Result.map (fun line ->
            match line.Trim().Split(' ') |> Array.toList with
            | _ :: parent :: _ -> Some parent
            | _ -> None)
