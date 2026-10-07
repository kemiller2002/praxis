namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open Ros.Application.Git
open Ros.Domain.Git
open Ros.Infrastructure.Git

[<RequireQualifiedAccess>]
module GitTests =
    let private withTemporaryRoot initializeGit operation =
        let root = Path.Combine(Path.GetTempPath(), $"ros-git-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        let run arguments =
            let startInfo = ProcessStartInfo("git")
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add "-C"
            startInfo.ArgumentList.Add root
            arguments |> List.iter startInfo.ArgumentList.Add
            use child = Process.Start startInfo
            let error = child.StandardError.ReadToEnd()
            child.WaitForExit()
            if child.ExitCode <> 0 then
                let command = String.concat " " arguments
                failwith $"git {command} failed: {error}"

        try
            if initializeGit then
                run [ "init"; "-q" ]
                run [ "config"; "user.email"; "test@example.invalid" ]
                run [ "config"; "user.name"; "ROS Test" ]

            operation root run
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private changed observation =
        match observation with
        | GitStatusObservation.Changed changes -> changes
        | other -> failwith $"Expected changed observation, received {other}"

    let tests =
        [ { Name = "git parser retains both rename and copy paths"
            Run =
              fun () ->
                  let raw = "R  renamed.txt\000original.txt\000C  copied.txt\000source.txt\000"

                  match GitStatusParser.parse raw with
                  | Error failure -> failwith failure.Message
                  | Ok changes ->
                      Assert.equal 2 changes.Length
                      let copied = changes |> List.find (fun change -> change.Path = "copied.txt")
                      let renamed = changes |> List.find (fun change -> change.Path = "renamed.txt")
                      Assert.equal (Some "source.txt") copied.OriginalPath
                      Assert.equal "C " (GitStatus.code copied.Status)
                      Assert.equal (Some "original.txt") renamed.OriginalPath
                      Assert.equal "R " (GitStatus.code renamed.Status) }
          { Name = "git parser rejects a rename without an origin path"
            Run =
              fun () ->
                  match GitStatusParser.parse "R  renamed.txt\000" with
                  | Ok changes -> failwith $"Expected malformed output, received {changes}"
                  | Error failure -> Assert.equal GitUnavailableReason.MalformedOutput failure.Reason }
          { Name = "git repository distinguishes clean from changed"
            Run =
              fun () ->
                  withTemporaryRoot true (fun root run ->
                      File.WriteAllText(Path.Combine(root, "tracked.txt"), "baseline\n")
                      run [ "add"; "tracked.txt" ]
                      run [ "commit"; "-qm"; "baseline" ]

                      Assert.equal GitStatusObservation.Clean (GitOperations.observe (ProcessGitRepository.create root))

                      File.AppendAllText(Path.Combine(root, "tracked.txt"), "changed\n")
                      File.WriteAllText(Path.Combine(root, "untracked.txt"), "new\n")
                      let changes = GitOperations.observe (ProcessGitRepository.create root) |> changed
                      Assert.equal [ "tracked.txt"; "untracked.txt" ] (changes |> List.map _.Path)
                      Assert.equal " M" (GitStatus.code changes[0].Status)
                      Assert.equal "??" (GitStatus.code changes[1].Status)) }
          { Name = "git repository reports a non-repository as unavailable"
            Run =
              fun () ->
                  withTemporaryRoot false (fun root _ ->
                      match GitOperations.observe (ProcessGitRepository.create root) with
                      | GitStatusObservation.Unavailable failure ->
                          Assert.equal GitUnavailableReason.NotRepository failure.Reason
                          Assert.equal (Some 128) failure.ExitCode
                      | other -> failwith $"Expected unavailable observation, received {other}") }
          { Name = "git repository reports a missing executable as unavailable"
            Run =
              fun () ->
                  match GitOperations.observe (ProcessGitRepository.createWithExecutable "ros-missing-git-executable" ".") with
                  | GitStatusObservation.Unavailable failure ->
                      Assert.equal GitUnavailableReason.ToolUnavailable failure.Reason
                      Assert.equal None failure.ExitCode
                  | other -> failwith $"Expected unavailable observation, received {other}" }
          { Name = "base comparison is not configured when no ref is supplied"
            Run =
              fun () ->
                  withTemporaryRoot true (fun root _ ->
                      Assert.equal
                          GitBaseComparisonOutcome.NotConfigured
                          (GitOperations.compareBase (ProcessGitRepository.createBaseComparison root) None)) }
          { Name = "base comparison returns the committed range for a resolvable ref"
            Run =
              fun () ->
                  withTemporaryRoot true (fun root run ->
                      File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
                      run [ "add"; "a.txt" ]
                      run [ "commit"; "-qm"; "first" ]
                      File.WriteAllText(Path.Combine(root, "b.txt"), "two\n")
                      run [ "add"; "b.txt" ]
                      run [ "commit"; "-qm"; "second" ]

                      Assert.equal
                          (GitBaseComparisonOutcome.Committed [ "b.txt" ])
                          (GitOperations.compareBase (ProcessGitRepository.createBaseComparison root) (Some "HEAD~1"))) }
          { Name = "base comparison silently reports an unresolvable ref rather than failing"
            Run =
              fun () ->
                  withTemporaryRoot true (fun root run ->
                      File.WriteAllText(Path.Combine(root, "a.txt"), "one\n")
                      run [ "add"; "a.txt" ]
                      run [ "commit"; "-qm"; "first" ]

                      Assert.equal
                          GitBaseComparisonOutcome.RefUnavailable
                          (GitOperations.compareBase
                              (ProcessGitRepository.createBaseComparison root)
                              (Some "refs/does-not-exist"))) } ]
