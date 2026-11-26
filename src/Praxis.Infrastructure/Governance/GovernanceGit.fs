namespace Praxis.Infrastructure.Governance

open System
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Work

/// Read-only Git access for governance decisions. Every read names an exact
/// revision; nothing reads the worktree, so a candidate's uncommitted files
/// and its checkout state never influence a decision.
[<RequireQualifiedAccess>]
module GovernanceGit =
    let private timeout = TimeSpan.FromMinutes 5.0

    let private run (root: string) (arguments: string list) =
        match ClaimProcess.run "git" root [ "GIT_TERMINAL_PROMPT", "0" ] None timeout arguments with
        | Ok result -> Ok result
        | Error(ClaimProcessFailure.NotStarted reason) -> Error $"git is unavailable: {reason}"
        | Error(ClaimProcessFailure.TimedOut after) -> Error $"git timed out after {after.TotalSeconds:F0}s"

    let private text (root: string) (arguments: string list) =
        run root arguments
        |> Result.bind (fun result ->
            if result.ExitCode = 0 then Ok(result.Stdout.TrimEnd('\n', '\r'))
            else Error $"""git {arguments |> List.truncate 3 |> String.concat " "} failed: {result.Stderr.Trim()}""")

    let private lines (value: string) =
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun l -> l.TrimEnd '\r') |> Array.toList

    /// The full object ID of a revision.
    let resolve root (revision: string) = text root [ "rev-parse"; "--verify"; "--quiet"; revision + "^{commit}" ]

    let tree root (commit: string) = text root [ "rev-parse"; "--verify"; commit + "^{tree}" ]

    /// A file at a revision, byte-exact (never trimmed: digests are computed
    /// over it); `None` when the revision has no such path.
    let show root (revision: string) (path: string) =
        run root [ "cat-file"; "-e"; $"{revision}:{path}" ]
        |> Result.bind (fun probe ->
            if probe.ExitCode <> 0 then Ok None
            else
                run root [ "cat-file"; "blob"; $"{revision}:{path}" ]
                |> Result.bind (fun blob -> if blob.ExitCode = 0 then Ok(Some blob.Stdout) else Error $"git cat-file failed: {blob.Stderr.Trim()}"))

    /// Whether `ancestor` is in `descendant`'s history.
    let isAncestor root (ancestor: string) (descendant: string) =
        run root [ "merge-base"; "--is-ancestor"; ancestor; descendant ]
        |> Result.bind (fun result ->
            match result.ExitCode with
            | 0 -> Ok true
            | 1 -> Ok false
            | _ -> Error $"git merge-base failed: {result.Stderr.Trim()}")

    /// Paths that differ between two commits.
    let changedBetween root (a: string) (b: string) = text root [ "diff"; "--name-only"; "--no-renames"; a; b ] |> Result.map lines

    /// The candidate's own changes, from its merge base with the target.
    let candidateChanges root (target: string) (candidate: string) : Result<ChangedPath list, string> =
        text root [ "diff"; "--name-status"; "-M"; $"{target}...{candidate}" ]
        |> Result.map (fun output -> Praxis.Infrastructure.Autonomy.LocalReview.parseNameStatus (output + "\n"))

    /// Tracked paths under `prefix` at a revision.
    let listFiles root (revision: string) (prefix: string) =
        let pathspec = if prefix = "" then [] else [ "--"; prefix ]
        text root ([ "ls-tree"; "-r"; "--name-only"; revision ] @ pathspec) |> Result.map lines

    /// Whether a merge commit's tree equals Git's own merge of its parents.
    /// `None` when the re-merge conflicts (a hand-resolved merge cannot be
    /// re-derived) or Git cannot compute it.
    let mergeTreeMatches root (commit: string) (first: string) (second: string) =
        match run root [ "merge-tree"; "--write-tree"; first; second ], tree root commit with
        | Ok result, Ok actual when result.ExitCode = 0 ->
            let derived = (lines result.Stdout |> List.tryHead |> Option.defaultValue "").Trim()
            Some(derived = actual)
        | _ -> None

    type LogEntry =
        { Commit: string
          Parents: string list
          Committer: string
          Subject: string }

    /// First-parent history of a branch, newest first.
    let firstParent root (revision: string) (limit: int) (since: string option) =
        let range = match since with Some s -> $"{s}..{revision}" | None -> revision

        text root [ "log"; "--first-parent"; $"--max-count={limit}"; "--format=%H%x1f%P%x1f%cn%x1f%s"; range ]
        |> Result.map (fun output ->
            lines output
            |> List.choose (fun line ->
                match line.Split '\u001f' with
                | [| commit; parents; committer; subject |] ->
                    Some
                        { Commit = commit
                          Parents = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                          Committer = committer
                          Subject = subject }
                | _ -> None))

    /// Commits reachable from `tip` and not from `base`.
    let countBetween root (``base``: string) (tip: string) =
        text root [ "rev-list"; "--count"; $"{``base``}..{tip}" ] |> Result.map int
