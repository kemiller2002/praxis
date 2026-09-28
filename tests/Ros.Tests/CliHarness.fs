namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes

/// Shared end-to-end harness: runs the real built `ros-fs` CLI (and Git)
/// against disposable repositories, with every ambient identity variable
/// removed so results never depend on the machine running the tests.
[<RequireQualifiedAccess>]
module CliHarness =
    type Run =
        { Exit: int
          Out: string
          Err: string }

    let cli = Path.Combine(AppContext.BaseDirectory, "ros-fs.dll")

    let identityVariables =
        [ "CLAUDE_CODE_SESSION_ID"; "CLAUDECODE"; "CLAUDE_CODE_ENTRYPOINT"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"
          "GEMINI_SESSION_ID"; "GEMINI_CLI"; "COPILOT_SESSION_ID"; "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "OLLAMA_HOST"
          "ROS_ACTOR"; "ROS_ACTOR_KIND"; "ROS_TELEMETRY_PROVIDER"; "ROS_TELEMETRY_RUNTIME"; "ROS_TELEMETRY_MODEL"
          "ROS_TELEMETRY_MODEL_VERSION"; "ROS_TELEMETRY_RUNTIME_VERSION"; "ROS_TELEMETRY_SESSION_ID"
          "ROS_TELEMETRY_CONVERSATION_ID"; "ROS_TELEMETRY_RUN_ID"; "ROS_BASE_REF"; "ROS_PACKAGE_ROOT" ]

    /// Runs a process to completion. `environment` entries are applied after
    /// the identity variables are removed, so a test can set any of them back.
    let runIn (workingDirectory: string option) (fileName: string) (arguments: string list) (environment: (string * string) list) =
        let startInfo = ProcessStartInfo(fileName)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        workingDirectory |> Option.iter (fun directory -> startInfo.WorkingDirectory <- directory)
        arguments |> List.iter startInfo.ArgumentList.Add
        identityVariables |> List.iter (startInfo.Environment.Remove >> ignore)
        environment |> List.iter (fun (name, value) -> startInfo.Environment[name] <- value)
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        { Exit = child.ExitCode
          Out = output.Result
          Err = error.Result }

    let run fileName arguments environment = runIn None fileName arguments environment

    let git (root: string) (arguments: string list) =
        let result = run "git" ([ "-C"; root ] @ arguments) []

        if result.Exit <> 0 then
            failwith $"git {String.Join(' ', arguments)} failed: {result.Err}"

        result.Out.Trim()

    /// `ros-fs --root ROOT ARGS...` with an explicit environment.
    let rosWith (root: string) (environment: (string * string) list) (arguments: string list) =
        run "dotnet" ([ cli; "--root"; root ] @ arguments) environment

    let ros (root: string) (arguments: string list) = rosWith root [] arguments

    /// Like `ros`, but fails the test unless the command exits 0.
    let rosOk (root: string) (arguments: string list) =
        let result = ros root arguments

        if result.Exit <> 0 then
            failwith $"ros {String.Join(' ', arguments)} exited {result.Exit}: {result.Err}{result.Out}"

        result

    let json (text: string) = JsonNode.Parse text

    let now () = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    let write (root: string) (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let read (root: string) (relativePath: string) = File.ReadAllText(Path.Combine(root, relativePath))

    let temporaryDirectory (prefix: string) =
        let path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}")
        Directory.CreateDirectory path |> ignore
        path

    let commitAll (root: string) (message: string) =
        git root [ "add"; "-A" ] |> ignore
        git root [ "-c"; "user.name=Test"; "-c"; "user.email=test@example.invalid"; "commit"; "-q"; "--no-gpg-sign"; "-m"; message ] |> ignore

    /// A fresh Git repository initialized with the real F# `init` (greenfield
    /// unless a profile is given) and committed, ready for work commands.
    let initializedRepository (prefix: string) (profile: string option) =
        let root = temporaryDirectory prefix
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore

        let profileArguments =
            match profile with
            | Some name -> [ "--profile"; name ]
            | None -> []

        rosOk root ([ "init" ] @ profileArguments) |> ignore
        commitAll root "initialize"
        root

    let removeDirectory (root: string) =
        try
            if Directory.Exists root then
                Directory.Delete(root, true)
        with _ ->
            ()

[<RequireQualifiedAccess>]
module CliHarnessTests =
    let tests =
        [ { Name = "cli harness: an initialized repository validates cleanly through the real CLI"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "ros-harness" None

                  try
                      let result = CliHarness.ros root [ "validate" ]
                      Assert.equal 0 result.Exit
                  finally
                      CliHarness.removeDirectory root } ]
