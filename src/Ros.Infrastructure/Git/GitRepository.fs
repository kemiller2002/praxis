namespace Ros.Infrastructure.Git

open System
open System.ComponentModel
open System.Diagnostics
open Ros.Application.Git
open Ros.Domain.Git
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module GitStatusParser =
    let private failure message =
        { Operation = "parse git status"
          Reason = GitUnavailableReason.MalformedOutput
          Message = message
          ExitCode = None }

    let private delta value =
        match value with
        | ' ' -> GitDelta.Unmodified
        | 'A' -> GitDelta.Added
        | 'M' -> GitDelta.Modified
        | 'D' -> GitDelta.Deleted
        | 'R' -> GitDelta.Renamed
        | 'C' -> GitDelta.Copied
        | 'T' -> GitDelta.TypeChanged
        | 'U' -> GitDelta.Unmerged
        | other -> GitDelta.Unknown other

    let private compareChange (left: GitChange) (right: GitChange) =
        let pathComparison = StringComparer.Ordinal.Compare(left.Path, right.Path)

        if pathComparison <> 0 then
            pathComparison
        else
            StringComparer.Ordinal.Compare(
                left.OriginalPath |> Option.defaultValue "",
                right.OriginalPath |> Option.defaultValue ""
            )

    let parse (output: string) =
        if output.Length = 0 then
            Ok []
        elif output[output.Length - 1] <> '\000' then
            Error(failure "porcelain-v1 -z output did not end with a NUL delimiter")
        else
            let fields = output.Split('\000') |> Array.toList |> List.rev |> List.tail |> List.rev

            let rec read (changes: GitChange list) (remaining: string list) =
                match remaining with
                | [] -> Ok(changes |> List.rev |> List.sortWith compareChange)
                | entry :: rest when entry.Length < 3 || entry[2] <> ' ' ->
                    Error(failure "porcelain-v1 entry did not contain a two-character status and path")
                | entry :: rest ->
                    let code = entry.Substring(0, 2)
                    let path = entry.Substring(3)

                    if path.Length = 0 then
                        Error(failure "porcelain-v1 entry contained an empty path")
                    else
                        let status =
                            match code with
                            | "??" -> GitChangeStatus.Untracked
                            | "!!" -> GitChangeStatus.Ignored
                            | _ -> GitChangeStatus.Tracked(delta code[0], delta code[1])

                        let hasOrigin = code.IndexOfAny([| 'R'; 'C' |]) >= 0

                        match hasOrigin, rest with
                        | true, originalPath :: tail when originalPath.Length > 0 ->
                            read
                                ({ Status = status
                                   Path = path
                                   OriginalPath = Some originalPath }
                                 :: changes)
                                tail
                        | true, _ -> Error(failure $"rename/copy entry '{path}' did not include its original path")
                        | false, _ ->
                            read
                                ({ Status = status
                                   Path = path
                                   OriginalPath = None }
                                 :: changes)
                                rest

            read [] fields

/// Parses `git diff-tree -r -z --raw --no-abbrev -M` output for one commit:
/// `:SRCMODE DSTMODE SRCBLOB DSTBLOB STATUS\0PATH\0`, with a second path
/// (`SRC\0DST\0`) for a rename or copy. Any status reconciliation cannot
/// interpret (an unmerged entry, an unknown letter) is a parse failure, so
/// the caller fails closed instead of attributing a change it misread.
[<RequireQualifiedAccess>]
module GitDiffTreeParser =
    let private failure message =
        { Operation = "parse git diff-tree"
          Reason = GitUnavailableReason.MalformedOutput
          Message = message
          ExitCode = None }

    let private zeroBlob (blob: string) = blob.Length > 0 && blob |> Seq.forall ((=) '0')

    let parse (output: string) : Result<GitCommitChange list, GitFailure> =
        let fields =
            output.Split('\000')
            |> Array.toList
            |> List.filter (fun field -> field.Trim().Length > 0)

        let rec read (changes: GitCommitChange list) (remaining: string list) =
            match remaining with
            | [] -> Ok(List.rev changes)
            | header :: rest when header.StartsWith ":" ->
                match header.Substring(1).Split(' ') with
                | [| _; _; _; destinationBlob; status |] when status.Length > 0 ->
                    let blob = if zeroBlob destinationBlob then None else Some destinationBlob

                    let simple kind tail =
                        match tail with
                        | path :: after ->
                            read
                                ({ Kind = kind
                                   Path = path
                                   OriginalPath = None
                                   Blob = if kind = GitCommitChangeKind.Deleted then None else blob }
                                 :: changes)
                                after
                        | [] -> Error(failure $"diff-tree entry '{header}' has no path")

                    let paired kind tail =
                        match tail with
                        | source :: destination :: after ->
                            read
                                ({ Kind = kind
                                   Path = destination
                                   OriginalPath = Some source
                                   Blob = blob }
                                 :: changes)
                                after
                        | _ -> Error(failure $"diff-tree rename/copy entry '{header}' does not name both paths")

                    match status[0] with
                    | 'A' -> simple GitCommitChangeKind.Added rest
                    | 'M' -> simple GitCommitChangeKind.Modified rest
                    | 'D' -> simple GitCommitChangeKind.Deleted rest
                    | 'T' -> simple GitCommitChangeKind.TypeChanged rest
                    | 'R' -> paired GitCommitChangeKind.Renamed rest
                    | 'C' -> paired GitCommitChangeKind.Copied rest
                    | other -> Error(failure $"diff-tree status '{other}' cannot be attributed")
                | _ -> Error(failure $"diff-tree entry '{header}' is not a raw diff header")
            | unexpected :: _ -> Error(failure $"unexpected diff-tree field '{unexpected}'")

        read [] fields

/// Parses the NUL-separated `git show -s` header reconciliation requests:
/// commit, parents, author name/email/date, committer name/email/date,
/// subject.
[<RequireQualifiedAccess>]
module GitCommitHeaderParser =
    let format = "%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s"

    let parse (output: string) (changes: GitCommitChange list) : Result<GitCommit, GitFailure> =
        match output.TrimEnd('\n', '\r').Split('\000') with
        | [| sha; parents; authorName; authorEmail; authorDate; committerName; committerEmail; committerDate; subject |] when
            sha.Length > 0
            ->
            Ok
                { Sha = sha
                  Parents = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                  Author =
                    { Name = authorName
                      Email = authorEmail
                      Date = authorDate }
                  Committer =
                    { Name = committerName
                      Email = committerEmail
                      Date = committerDate }
                  Subject = subject
                  Changes = changes }
        | _ ->
            Error
                { Operation = "parse git show"
                  Reason = GitUnavailableReason.MalformedOutput
                  Message = "commit header did not contain the nine expected fields"
                  ExitCode = None }

type private ProcessResult =
    { ExitCode: int
      Output: string
      Error: string }

[<RequireQualifiedAccess>]
module ProcessGitRepository =
    let private unavailable reason message exitCode =
        GitStatusObservation.Unavailable
            { Operation = "git status"
              Reason = reason
              Message = message
              ExitCode = exitCode }

    let private runGit executable root (operation: string) (arguments: string list) : Result<ProcessResult, GitFailure> =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root

            for argument in arguments do
                startInfo.ArgumentList.Add argument

            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                Error
                    { Operation = operation
                      Reason = GitUnavailableReason.ToolUnavailable
                      Message = "git process did not start"
                      ExitCode = None }
            else
                let standardOutput = child.StandardOutput.ReadToEndAsync()
                let standardError = child.StandardError.ReadToEndAsync()
                child.WaitForExit()

                Ok
                    { ExitCode = child.ExitCode
                      Output = standardOutput.Result
                      Error = standardError.Result.Trim() }
        with
        | :? Win32Exception as error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.ToolUnavailable
                  Message = error.Message
                  ExitCode = None }
        | error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.CommandFailed
                  Message = error.Message
                  ExitCode = None }

    let private observe executable root () =
        match runGit executable root "git status" [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ] with
        | Error failure -> GitStatusObservation.Unavailable failure
        | Ok result when result.ExitCode <> 0 ->
            let reason =
                if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                    GitUnavailableReason.NotRepository
                else
                    GitUnavailableReason.CommandFailed

            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            unavailable reason message (Some result.ExitCode)
        | Ok result ->
            match GitStatusParser.parse result.Output with
            | Error failure -> GitStatusObservation.Unavailable failure
            | Ok [] -> GitStatusObservation.Clean
            | Ok changes -> GitStatusObservation.Changed changes

    /// Mirrors production's optional `ROS_BASE_REF` extension in `gitPaths`:
    /// a ref that does not resolve to an existing commit is a silent
    /// `RefUnavailable` (a CI base ref can be absent in nested fixture
    /// repositories), never a hard failure; only a resolvable ref whose
    /// `diff --name-only` itself fails is `Unavailable`.
    let private compareBase executable root (ref: string option) : GitBaseComparisonOutcome =
        match ref with
        | None -> GitBaseComparisonOutcome.NotConfigured
        | Some baseRef ->
            match runGit executable root "git cat-file" [ "cat-file"; "-e"; $"{baseRef}^{{commit}}" ] with
            | Error _ -> GitBaseComparisonOutcome.RefUnavailable
            | Ok existsResult when existsResult.ExitCode <> 0 -> GitBaseComparisonOutcome.RefUnavailable
            | Ok _ ->
                match runGit executable root "git diff" [ "diff"; "--name-only"; $"{baseRef}...HEAD" ] with
                | Error failure -> GitBaseComparisonOutcome.Unavailable failure
                | Ok diffResult when diffResult.ExitCode <> 0 ->
                    let message =
                        if diffResult.Error.Length = 0 then
                            $"git exited with code {diffResult.ExitCode}"
                        else
                            diffResult.Error

                    GitBaseComparisonOutcome.Unavailable
                        { Operation = "git diff"
                          Reason = GitUnavailableReason.CommandFailed
                          Message = message
                          ExitCode = Some diffResult.ExitCode }
                | Ok diffResult ->
                    diffResult.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.toList
                    |> GitBaseComparisonOutcome.Committed

    let createWithExecutable executable root : GitRepository =
        { ObserveStatus = observe executable (IO.Path.GetFullPath root) }

    let create root = createWithExecutable "git" root

    let createBaseComparisonWithExecutable executable root : GitBaseComparison =
        { Compare = compareBase executable (IO.Path.GetFullPath root) }

    let createBaseComparison root = createBaseComparisonWithExecutable "git" root

    /// Mirrors production `gitSnapshot`'s branch/commit reads
    /// (`tools/ros_telemetry.mjs`): `git branch --show-current` and `git
    /// rev-parse HEAD`, each folded to `None` on any failure (a non-zero
    /// exit, or the tool itself being unavailable) rather than surfaced as
    /// an error -- telemetry capture never blocks on a broken worktree.
    let private readText executable root arguments =
        match runGit executable root "git" arguments with
        | Ok result when result.ExitCode = 0 ->
            let value = result.Output.Trim()
            if value.Length > 0 then Some value else None
        | _ -> None

    let readBranchAndCommitWithExecutable executable root : string option * string option =
        let fullRoot = IO.Path.GetFullPath root
        readText executable fullRoot [ "branch"; "--show-current" ], readText executable fullRoot [ "rev-parse"; "HEAD" ]

    let readBranchAndCommit root = readBranchAndCommitWithExecutable "git" root

    /// Mirrors production `runGitText`'s default (trimmed) success/failure
    /// shape for the git-diff-derived change-summary reads
    /// `cleanBaselineChanges` performs: `git diff --name-status`, `git diff
    /// --numstat`, and `git rev-list --count`.
    let private runGitTextTrimmed executable root operation (arguments: string list) : Result<string, GitFailure> =
        match runGit executable root operation arguments with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 ->
            let reason =
                if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                    GitUnavailableReason.NotRepository
                else
                    GitUnavailableReason.CommandFailed

            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            Error { Operation = operation; Reason = reason; Message = message; ExitCode = Some result.ExitCode }
        | Ok result -> Ok(result.Output.Trim())

    let readNameStatusDiffWithExecutable executable root (startCommit: string) : Result<string, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git diff" [ "diff"; "--name-status"; "--find-renames"; startCommit ]

    let readNameStatusDiff root startCommit = readNameStatusDiffWithExecutable "git" root startCommit

    let readNumstatDiffWithExecutable executable root (startCommit: string) : Result<string, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git diff" [ "diff"; "--numstat"; "--find-renames"; startCommit ]

    let readNumstatDiff root startCommit = readNumstatDiffWithExecutable "git" root startCommit

    /// `-z`-delimited output is never trimmed of a real trailing NUL, matching
    /// production's own `{trim: false}` override for this one read.
    let readUntrackedFilesWithExecutable executable root : Result<string, GitFailure> =
        match runGit executable (IO.Path.GetFullPath root) "git ls-files" [ "ls-files"; "--others"; "--exclude-standard"; "-z" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 ->
            let message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
            Error { Operation = "git ls-files"; Reason = GitUnavailableReason.CommandFailed; Message = message; ExitCode = Some result.ExitCode }
        | Ok result -> Ok result.Output

    let readUntrackedFiles root = readUntrackedFilesWithExecutable "git" root

    let readCommitCountWithExecutable executable root (startCommit: string) (endCommit: string) : Result<int, GitFailure> =
        runGitTextTrimmed executable (IO.Path.GetFullPath root) "git rev-list" [ "rev-list"; "--count"; $"{startCommit}..{endCommit}" ]
        |> Result.map int

    let readCommitCount root startCommit endCommit = readCommitCountWithExecutable "git" root startCommit endCommit

    /// `git show REVISION:PATH` verbatim, or `None` when the path did not
    /// exist at that revision (or Git is unavailable). Used to compare an
    /// artifact's provenance with its base-revision version.
    let readFileAtRevision root (revision: string) (relativePath: string) : string option =
        match runGit "git" (IO.Path.GetFullPath root) "git show" [ "show"; $"{revision}:{relativePath.Replace('\\', '/')}" ] with
        | Ok result when result.ExitCode = 0 -> Some result.Output
        | _ -> None

    // ---- committed history (attribution reconciliation evidence) ----

    let private commandFailure operation (result: ProcessResult) =
        let reason =
            if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                GitUnavailableReason.NotRepository
            else
                GitUnavailableReason.CommandFailed

        { Operation = operation
          Reason = reason
          Message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
          ExitCode = Some result.ExitCode }

    /// `rev-parse --verify` resolves exactly one object or fails; a short
    /// SHA or ref name Git could read more than one way is reported as
    /// ambiguous (Git warns and still picks one for an ambiguous ref name,
    /// which reconciliation refuses to rely on).
    let private resolveRevision executable root (reference: string) : GitRefResolution =
        match runGit executable root "git rev-parse" [ "rev-parse"; "--verify"; "--end-of-options"; $"{reference}^{{commit}}" ] with
        | Error failure -> GitRefResolution.Unavailable failure
        | Ok result when result.Error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) ->
            GitRefResolution.Ambiguous result.Error
        | Ok result when result.ExitCode = 0 && result.Output.Trim().Length > 0 -> GitRefResolution.Resolved(result.Output.Trim())
        | Ok result when result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ->
            GitRefResolution.Unavailable(commandFailure "git rev-parse" result)
        | Ok result ->
            GitRefResolution.NotFound(if result.Error.Length = 0 then "no such commit" else result.Error)

    let private isAncestor executable root (ancestor: string) (descendant: string) : Result<bool, GitFailure> =
        match runGit executable root "git merge-base" [ "merge-base"; "--is-ancestor"; ancestor; descendant ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode = 0 -> Ok true
        | Ok result when result.ExitCode = 1 -> Ok false
        | Ok result -> Error(commandFailure "git merge-base" result)

    let private listRange executable root (baseCommit: string) (headCommit: string) : Result<string list, GitFailure> =
        match runGit executable root "git rev-list" [ "rev-list"; "--reverse"; "--topo-order"; $"{baseCommit}..{headCommit}" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 -> Error(commandFailure "git rev-list" result)
        | Ok result -> Ok(result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList)

    /// A shallow clone grafts its boundary commits as parentless, so `diff-tree
    /// --root` would report their entire tree as added. They are recorded in
    /// the repository's `shallow` file.
    let private isShallowBoundary executable root (sha: string) =
        match runGit executable root "git rev-parse" [ "rev-parse"; "--git-path"; "shallow" ] with
        | Ok result when result.ExitCode = 0 ->
            let path = result.Output.Trim()
            let file = if IO.Path.IsPathRooted path then path else IO.Path.Combine(root, path)
            IO.File.Exists file && (IO.File.ReadAllLines file |> Array.exists (fun line -> line.Trim() = sha))
        | _ -> false

    /// A merge commit's diff against one parent would silently attribute the
    /// other side's work, so its changes are never read: it is returned with
    /// no changes and every parent, and the domain rejects it.
    let private readCommit executable root (sha: string) : Result<GitCommit, GitFailure> =
        match runGit executable root "git show" [ "show"; "-s"; "--no-show-signature"; $"--format={GitCommitHeaderParser.format}"; sha ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode <> 0 -> Error(commandFailure "git show" result)
        | Ok header ->
            match GitCommitHeaderParser.parse header.Output [] with
            | Error failure -> Error failure
            | Ok commit when commit.Parents.Length > 1 -> Ok commit
            | Ok commit when commit.Parents.IsEmpty && isShallowBoundary executable root commit.Sha ->
                Error
                    { Operation = "git history"
                      Reason = GitUnavailableReason.CommandFailed
                      Message =
                        $"commit {commit.Sha} is the boundary of a shallow clone, so its parent and therefore its changes are unknown; fetch full history (git fetch --unshallow) before reconciling"
                      ExitCode = None }
            | Ok commit ->
                let arguments =
                    [ "diff-tree"; "-r"; "-z"; "--raw"; "--no-abbrev"; "-M"; "--root"; "--no-commit-id"; "--no-ext-diff"; sha ]

                match runGit executable root "git diff-tree" arguments with
                | Error failure -> Error failure
                | Ok result when result.ExitCode <> 0 -> Error(commandFailure "git diff-tree" result)
                | Ok result -> GitDiffTreeParser.parse result.Output |> Result.map (fun changes -> { commit with Changes = changes })

    let createHistoryWithExecutable executable root : GitHistory =
        let fullRoot = IO.Path.GetFullPath root

        { Head = fun () -> resolveRevision executable fullRoot "HEAD"
          ResolveCommit = resolveRevision executable fullRoot
          IsAncestor = isAncestor executable fullRoot
          ListRange = listRange executable fullRoot
          ReadCommit = readCommit executable fullRoot }

    let createHistory root = createHistoryWithExecutable "git" root

    /// The current working-tree content of each path as Git would store it
    /// (`git hash-object`, which applies the path's clean filters), or
    /// `Absent` when nothing exists there. Anything Git cannot hash (a
    /// directory, a submodule, an I/O failure) is `Unreadable`, which never
    /// matches recorded evidence, so validation fails closed.
    let readPathStatesWithExecutable executable root (paths: string list) : Map<string, PathContentState> =
        let fullRoot = IO.Path.GetFullPath root
        let exists (path: string) = IO.File.Exists(IO.Path.Combine(fullRoot, path))
        let present, absent = paths |> List.distinct |> List.partition exists
        let absentStates = absent |> List.filter (fun path -> not (IO.Directory.Exists(IO.Path.Combine(fullRoot, path))))

        let presentStates =
            match present with
            | [] -> []
            | _ ->
                match runGit executable fullRoot "git hash-object" ([ "hash-object"; "--" ] @ present) with
                | Ok result when result.ExitCode = 0 ->
                    match result.Output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList with
                    | hashes when hashes.Length = present.Length -> List.zip present (hashes |> List.map PathContentState.Present)
                    | _ -> present |> List.map (fun path -> path, PathContentState.Unreadable)
                | _ -> present |> List.map (fun path -> path, PathContentState.Unreadable)

        (absentStates |> List.map (fun path -> path, PathContentState.Absent))
        @ presentStates
        @ (absent |> List.except absentStates |> List.map (fun path -> path, PathContentState.Unreadable))
        |> Map.ofList

    let readPathStates root paths = readPathStatesWithExecutable "git" root paths

/// Parsers for the Git reads durability depends on. Kept separate from the
/// process runner so every accepted and refused output form is testable.
[<RequireQualifiedAccess>]
module GitDurabilityParser =
    let private malformed operation message =
        { Operation = operation
          Reason = GitUnavailableReason.MalformedOutput
          Message = message
          ExitCode = None }

    /// A remote URL with any user information (which can carry a token, as
    /// `https://x-access-token:...@host/...` does in CI) removed. An
    /// scp-like `user@host:path` names an SSH login, not a secret, and is
    /// kept as Git shows it.
    let sanitizeUrl (url: string) =
        let schemeUserInfo = System.Text.RegularExpressions.Regex(@"^([A-Za-z][A-Za-z0-9+.\-]*://)[^/@]*@")
        schemeUserInfo.Replace(url.Trim(), "$1")

    /// `git ls-remote REMOTE refs/heads/BRANCH` output: exactly one line for
    /// that ref, `SHA<TAB>refs/heads/BRANCH`. No line means the branch does
    /// not exist on the remote.
    let parseLsRemote (branch: string) (output: string) : Result<CommitId option, GitFailure> =
        let target = $"refs/heads/{branch}"

        let lines =
            output.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line -> line.TrimEnd('\r'))
            |> Array.toList

        let matching =
            lines
            |> List.choose (fun line ->
                match line.Split('\t') with
                | [| sha; reference |] when reference = target -> Some sha
                | _ -> None)

        match matching with
        | [] when lines |> List.forall (fun line -> line.Split('\t').Length = 2) -> Ok None
        | [] -> Error(malformed "git ls-remote" "ls-remote output was not SHA<TAB>REF lines")
        | [ sha ] ->
            match CommitId.tryParse sha with
            | Some commit -> Ok(Some commit)
            | None -> Error(malformed "git ls-remote" $"'{sha}' is not a full commit ID")
        | _ -> Error(malformed "git ls-remote" $"ls-remote reported {matching.Length} heads for {target}")

    /// `git rev-list --left-right --count A...B` output: `LEFT<TAB>RIGHT`.
    let parseLeftRightCount (output: string) : Result<CommitRelation, GitFailure> =
        match output.Trim().Split([| '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries) with
        | [| left; right |] ->
            match Int32.TryParse left, Int32.TryParse right with
            | (true, ahead), (true, behind) when ahead >= 0 && behind >= 0 -> Ok(CommitRelation.ofCounts ahead behind)
            | _ -> Error(malformed "git rev-list" $"'{output.Trim()}' is not two counts")
        | _ -> Error(malformed "git rev-list" $"'{output.Trim()}' is not two counts")

    /// `git diff --name-only -z` output.
    let parseNameList (output: string) : string list =
        output.Split('\000', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun path -> path.Trim('\n', '\r'))
        |> Array.filter (fun path -> path.Length > 0)
        |> Array.distinct
        |> Array.toList

/// The process-backed `GitDurability` port. Reads only: nothing here
/// fetches, commits, pushes, or moves a ref. Remote reads run with prompts
/// disabled and a bounded timeout, so an unreachable or credential-demanding
/// remote is reported as unreachable instead of hanging.
[<RequireQualifiedAccess>]
module ProcessGitDurability =
    type private Run =
        { ExitCode: int
          Output: string
          Error: string
          TimedOut: bool }

    let defaultRemoteTimeout = TimeSpan.FromSeconds 30.0

    /// `ROS_GIT_REMOTE_TIMEOUT_SECONDS` bounds a remote read; absent or
    /// invalid values use the default.
    let remoteTimeout () =
        match Environment.GetEnvironmentVariable "ROS_GIT_REMOTE_TIMEOUT_SECONDS" with
        | null -> defaultRemoteTimeout
        | value ->
            match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, seconds when seconds > 0.0 -> TimeSpan.FromSeconds seconds
            | _ -> defaultRemoteTimeout

    let private run executable (root: string) (operation: string) (timeout: TimeSpan option) (arguments: string list) : Result<Run, GitFailure> =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- true
            startInfo.Environment["GIT_TERMINAL_PROMPT"] <- "0"
            startInfo.Environment["GCM_INTERACTIVE"] <- "never"
            startInfo.Environment["GIT_ASKPASS"] <- ""
            startInfo.Environment["SSH_ASKPASS"] <- ""
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root
            arguments |> List.iter startInfo.ArgumentList.Add

            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                Error
                    { Operation = operation
                      Reason = GitUnavailableReason.ToolUnavailable
                      Message = "git process did not start"
                      ExitCode = None }
            else
                child.StandardInput.Close()
                let standardOutput = child.StandardOutput.ReadToEndAsync()
                let standardError = child.StandardError.ReadToEndAsync()

                let exited =
                    match timeout with
                    | Some limit -> child.WaitForExit(int limit.TotalMilliseconds)
                    | None ->
                        child.WaitForExit()
                        true

                if not exited then
                    try
                        child.Kill true
                    with _ ->
                        ()

                    Ok
                        { ExitCode = -1
                          Output = ""
                          Error = $"timed out after {timeout.Value.TotalSeconds:F0}s"
                          TimedOut = true }
                else
                    child.WaitForExit()

                    Ok
                        { ExitCode = child.ExitCode
                          Output = standardOutput.Result
                          Error = standardError.Result.Trim()
                          TimedOut = false }
        with
        | :? Win32Exception as error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.ToolUnavailable
                  Message = error.Message
                  ExitCode = None }
        | error ->
            Error
                { Operation = operation
                  Reason = GitUnavailableReason.CommandFailed
                  Message = error.Message
                  ExitCode = None }

    let private failureOf operation (result: Run) =
        let reason =
            if result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                GitUnavailableReason.NotRepository
            else
                GitUnavailableReason.CommandFailed

        { Operation = operation
          Reason = reason
          Message = if result.Error.Length = 0 then $"git exited with code {result.ExitCode}" else result.Error
          ExitCode = Some result.ExitCode }

    let private configValue executable root (key: string) : GitRead<string option> =
        match run executable root "git config" None [ "config"; "--get"; key ] with
        | Error failure -> GitRead.Unavailable failure
        | Ok result when result.ExitCode = 0 -> GitRead.Observed(Some(result.Output.Trim()))
        | Ok result when result.ExitCode = 1 -> GitRead.Observed None
        | Ok result -> GitRead.Unavailable(failureOf "git config" result)

    let private head executable root () : GitRead<HeadState> =
        match run executable root "git symbolic-ref" None [ "symbolic-ref"; "-q"; "HEAD" ] with
        | Error failure -> GitRead.Unavailable failure
        | Ok symbolic when symbolic.ExitCode <> 0 && symbolic.ExitCode <> 1 -> GitRead.Unavailable(failureOf "git symbolic-ref" symbolic)
        | Ok symbolic ->
            let branch =
                if symbolic.ExitCode = 0 then
                    let reference = symbolic.Output.Trim()
                    Some(if reference.StartsWith "refs/heads/" then reference.Substring "refs/heads/".Length else reference)
                else
                    None

            match run executable root "git rev-parse" None [ "rev-parse"; "--verify"; "-q"; "HEAD^{commit}" ] with
            | Error failure -> GitRead.Unavailable failure
            | Ok resolved when resolved.ExitCode = 0 ->
                match CommitId.tryParse (resolved.Output.Trim()), branch with
                | Some commit, Some name -> GitRead.Observed(HeadState.OnBranch(name, commit))
                | Some commit, None -> GitRead.Observed(HeadState.Detached commit)
                | None, _ ->
                    GitRead.Unavailable
                        { Operation = "git rev-parse"
                          Reason = GitUnavailableReason.MalformedOutput
                          Message = $"'{resolved.Output.Trim()}' is not a full commit ID"
                          ExitCode = None }
            | Ok resolved when resolved.ExitCode = 1 -> GitRead.Observed(HeadState.Unborn branch)
            | Ok resolved -> GitRead.Unavailable(failureOf "git rev-parse" resolved)

    let private remote executable root (name: string) : GitRead<RemoteIdentity option> =
        match configValue executable root $"remote.{name}.url" with
        | GitRead.Unavailable failure -> GitRead.Unavailable failure
        | GitRead.Observed None -> GitRead.Observed None
        | GitRead.Observed(Some url) ->
            GitRead.Observed(
                Some
                    { Name = name
                      Url = Some(GitDurabilityParser.sanitizeUrl url) }
            )

    let private upstream executable root (branch: string) : GitRead<UpstreamState> =
        match configValue executable root $"branch.{branch}.remote", configValue executable root $"branch.{branch}.merge" with
        | GitRead.Unavailable failure, _
        | _, GitRead.Unavailable failure -> GitRead.Unavailable failure
        | GitRead.Observed None, _
        | _, GitRead.Observed None
        | GitRead.Observed(Some "."), _ -> GitRead.Observed UpstreamState.NoUpstream
        | GitRead.Observed(Some remoteName), GitRead.Observed(Some merge) ->
            let remoteBranch = if merge.StartsWith "refs/heads/" then merge.Substring "refs/heads/".Length else merge

            match remote executable root remoteName with
            | GitRead.Unavailable failure -> GitRead.Unavailable failure
            | GitRead.Observed None -> GitRead.Observed(UpstreamState.RemoteNotConfigured(remoteName, remoteBranch))
            | GitRead.Observed(Some identity) -> GitRead.Observed(UpstreamState.Tracking(identity, remoteBranch))

    let private remoteBranch executable root (identity: RemoteIdentity) (branch: string) : RemoteBranchObservation =
        let arguments = [ "ls-remote"; "--heads"; "--"; identity.Name; $"refs/heads/{branch}" ]

        match run executable root "git ls-remote" (Some(remoteTimeout ())) arguments with
        | Error failure when failure.Reason = GitUnavailableReason.ToolUnavailable -> RemoteBranchObservation.Unavailable failure
        | Error failure -> RemoteBranchObservation.Unreachable failure
        | Ok result when result.TimedOut ->
            RemoteBranchObservation.Unreachable
                { Operation = "git ls-remote"
                  Reason = GitUnavailableReason.CommandFailed
                  Message = $"remote '{identity.Name}' did not answer: {result.Error}"
                  ExitCode = None }
        | Ok result when result.ExitCode = 0 ->
            match GitDurabilityParser.parseLsRemote branch result.Output with
            | Ok(Some commit) -> RemoteBranchObservation.At commit
            | Ok None -> RemoteBranchObservation.Missing
            | Error failure -> RemoteBranchObservation.Unavailable failure
        | Ok result when result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ->
            RemoteBranchObservation.Unavailable(failureOf "git ls-remote" result)
        | Ok result -> RemoteBranchObservation.Unreachable(failureOf "git ls-remote" result)

    let private exists executable root (commit: CommitId) =
        match run executable root "git cat-file" None [ "cat-file"; "-e"; $"{commit.Value}^{{commit}}" ] with
        | Error failure -> Error failure
        | Ok result when result.ExitCode = 0 -> Ok true
        | Ok result when result.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) -> Error(failureOf "git cat-file" result)
        | Ok _ -> Ok false

    let private relation executable root (left: CommitId) (right: CommitId) : CommitRelationObservation =
        match exists executable root left, exists executable root right with
        | Error failure, _
        | _, Error failure -> CommitRelationObservation.Unavailable failure
        | Ok false, _ -> CommitRelationObservation.ObjectMissing left
        | _, Ok false -> CommitRelationObservation.ObjectMissing right
        | Ok true, Ok true ->
            match run executable root "git rev-list" None [ "rev-list"; "--left-right"; "--count"; $"{left.Value}...{right.Value}" ] with
            | Error failure -> CommitRelationObservation.Unavailable failure
            | Ok result when result.ExitCode <> 0 -> CommitRelationObservation.Unavailable(failureOf "git rev-list" result)
            | Ok result ->
                match GitDurabilityParser.parseLeftRightCount result.Output with
                | Ok value -> CommitRelationObservation.Related value
                | Error failure -> CommitRelationObservation.Unavailable failure

    let private changedPaths executable root (left: CommitId) (right: CommitId) : GitRead<string list> =
        match run executable root "git diff" None [ "diff"; "--name-only"; "-z"; "--no-renames"; "--no-ext-diff"; left.Value; right.Value; "--" ] with
        | Error failure -> GitRead.Unavailable failure
        | Ok result when result.ExitCode <> 0 -> GitRead.Unavailable(failureOf "git diff" result)
        | Ok result -> GitRead.Observed(GitDurabilityParser.parseNameList result.Output)

    let createWithExecutable executable (root: string) : GitDurability =
        let fullRoot = IO.Path.GetFullPath root
        let status = ProcessGitRepository.createWithExecutable executable fullRoot

        { Head = head executable fullRoot
          Upstream = upstream executable fullRoot
          Remote = remote executable fullRoot
          RemoteBranch = remoteBranch executable fullRoot
          Relation = relation executable fullRoot
          ChangedPaths = changedPaths executable fullRoot
          Status = status.ObserveStatus }

    let create root = createWithExecutable "git" root
