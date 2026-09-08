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

[<RequireQualifiedAccess>]
module ProcessGitRepository =
    let private unavailable reason message exitCode =
        GitStatusObservation.Unavailable
            { Operation = "git status"
              Reason = reason
              Message = message
              ExitCode = exitCode }

    let private observe executable root () =
        try
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root
            startInfo.ArgumentList.Add "status"
            startInfo.ArgumentList.Add "--porcelain=v1"
            startInfo.ArgumentList.Add "-z"
            startInfo.ArgumentList.Add "--untracked-files=all"

            use child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                unavailable GitUnavailableReason.ToolUnavailable "git process did not start" None
            else
                let standardOutput = child.StandardOutput.ReadToEndAsync()
                let standardError = child.StandardError.ReadToEndAsync()
                child.WaitForExit()
                let output = standardOutput.Result
                let error = standardError.Result.Trim()

                if child.ExitCode <> 0 then
                    let reason =
                        if error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) then
                            GitUnavailableReason.NotRepository
                        else
                            GitUnavailableReason.CommandFailed

                    let message = if error.Length = 0 then $"git exited with code {child.ExitCode}" else error
                    unavailable reason message (Some child.ExitCode)
                else
                    match GitStatusParser.parse output with
                    | Error failure -> GitStatusObservation.Unavailable failure
                    | Ok [] -> GitStatusObservation.Clean
                    | Ok changes -> GitStatusObservation.Changed changes
        with
        | :? Win32Exception as error -> unavailable GitUnavailableReason.ToolUnavailable error.Message None
        | error -> unavailable GitUnavailableReason.CommandFailed error.Message None

    let createWithExecutable executable root : GitRepository =
        { ObserveStatus = observe executable (IO.Path.GetFullPath root) }

    let create root = createWithExecutable "git" root
