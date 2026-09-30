namespace Ros.Cli

open System
open System.Diagnostics
open System.IO

type ProcessResult =
    { Exit: int
      Out: string
      Err: string }

/// Runs command lines as child processes. The web interface executes every
/// work operation by invoking this same CLI (`selfCommand`), and the hub by
/// invoking each registered repository's own `./praxis`, so neither HTTP
/// adapter can drift from what the command line does.
[<RequireQualifiedAccess>]
module CliProcess =
    /// How to re-run the current CLI: the executable itself, or -- when it is
    /// hosted by `dotnet` -- the host plus this entry assembly's path.
    let selfCommand (processPath: string) (entryAssemblyPath: string) : string * string list =
        // Either separator, so the decision does not depend on the host OS.
        let fileName = processPath.Substring(processPath.LastIndexOfAny [| '/'; '\\' |] + 1)

        let hostName =
            if fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) then
                fileName.Substring(0, fileName.Length - 4)
            else
                fileName

        if hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && not (String.IsNullOrEmpty entryAssemblyPath) then
            processPath, [ entryAssemblyPath ]
        else
            processPath, []

    let currentSelfCommand () =
        let entry =
            Reflection.Assembly.GetEntryAssembly()
            |> Option.ofObj
            |> Option.map (fun assembly -> assembly.Location)
            |> Option.defaultValue ""

        selfCommand (Environment.ProcessPath |> Option.ofObj |> Option.defaultValue "dotnet") entry

    /// Runs `fileName arguments...` with an argument vector (never a shell), so
    /// nothing in a title or description can be interpreted as shell syntax.
    let run (workingDirectory: string) (fileName: string) (arguments: string list) : Result<ProcessResult, string> =
        try
            let startInfo = ProcessStartInfo(fileName)
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardInput <- true
            startInfo.WorkingDirectory <- workingDirectory
            arguments |> List.iter startInfo.ArgumentList.Add
            use child = Process.Start startInfo
            child.StandardInput.Close()
            let output = child.StandardOutput.ReadToEndAsync()
            let error = child.StandardError.ReadToEndAsync()
            child.WaitForExit()

            Ok
                { Exit = child.ExitCode
                  Out = output.Result
                  Err = error.Result }
        with error ->
            Error error.Message

    /// The message a failed CLI run reports: its stderr without the `ERROR `
    /// prefix, joined across lines.
    let failureMessage (result: ProcessResult) =
        let lines =
            result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun line -> line.TrimEnd('\r'))
            |> Array.filter (fun line -> line.Trim() <> "")

        let errors =
            lines
            |> Array.filter (fun line -> line.StartsWith("ERROR ", StringComparison.Ordinal))
            |> Array.map (fun line -> line.Substring 6)

        match errors, lines with
        | [||], [||] -> $"command exited with status {result.Exit}"
        | [||], _ -> String.Join(" ", lines)
        | _ -> String.Join("; ", errors)

    /// Runs this CLI against `root`.
    let runSelf (root: string) (arguments: string list) =
        let fileName, prefix = currentSelfCommand ()
        run root fileName (prefix @ [ "--root"; root ] @ arguments)
