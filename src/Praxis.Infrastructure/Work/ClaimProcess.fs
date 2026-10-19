namespace Praxis.Infrastructure.Work

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text

type ClaimProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

[<RequireQualifiedAccess>]
type ClaimProcessFailure =
    | NotStarted of reason: string
    | TimedOut of after: TimeSpan

/// Runs one child process with arguments passed as a list (never through
/// a shell), optional stdin and a hard timeout. Failure to start and
/// timeouts are values, not exceptions.
[<RequireQualifiedAccess>]
module ClaimProcess =
    let run (executable: string) (workingDirectory: string) (environment: (string * string) list) (stdin: string option) (timeout: TimeSpan) (arguments: string list) =
        let info = ProcessStartInfo(executable)
        arguments |> List.iter info.ArgumentList.Add
        info.WorkingDirectory <- workingDirectory
        info.UseShellExecute <- false
        info.RedirectStandardInput <- stdin.IsSome
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.StandardOutputEncoding <- UTF8Encoding(false)
        info.StandardErrorEncoding <- UTF8Encoding(false)
        environment |> List.iter (fun (name, value) -> info.Environment[name] <- value)

        try
            use child = new Process(StartInfo = info)

            if not (child.Start()) then
                Error(ClaimProcessFailure.NotStarted $"'{executable}' did not start")
            else
                let stdout = child.StandardOutput.ReadToEndAsync()
                let stderr = child.StandardError.ReadToEndAsync()

                stdin
                |> Option.iter (fun text ->
                    child.StandardInput.Write text
                    child.StandardInput.Close())

                if child.WaitForExit(int timeout.TotalMilliseconds) then
                    child.WaitForExit()
                    Ok { ExitCode = child.ExitCode; Stdout = stdout.Result; Stderr = stderr.Result }
                else
                    (try child.Kill true with _ -> ())
                    Error(ClaimProcessFailure.TimedOut timeout)
        with
        | :? Win32Exception as error -> Error(ClaimProcessFailure.NotStarted error.Message)
        | :? FileNotFoundException as error -> Error(ClaimProcessFailure.NotStarted error.Message)
