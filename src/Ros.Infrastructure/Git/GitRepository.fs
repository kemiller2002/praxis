namespace Ros.Infrastructure.Git

open System
open System.ComponentModel
open System.Diagnostics
open Ros.Application.Git
open Ros.Domain.Git

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
