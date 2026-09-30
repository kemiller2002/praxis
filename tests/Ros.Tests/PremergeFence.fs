namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes

/// Shared harness for the PR #92 pre-merge regression fence
/// (PRAXIS-PR92-PREMERGE-REGRESSION-FENCE). These tests characterize main's
/// behavior that the ROS-to-Praxis reconciliation could silently drop, so the
/// harness deliberately depends on nothing the rename changes: it runs
/// whichever CLI assembly the build produced (`praxis.dll` after the rename,
/// `ros-fs.dll` before it) and locates the repository by `Ros.slnx`, not by
/// `package.json` (which PR #92 deletes).
module PremergeFence =
    type Result =
        { ExitCode: int
          Output: string
          Error: string }

        member this.Json = JsonNode.Parse this.Output :?> JsonObject

    /// The actor an executor declares, as a remote request or environment carries it.
    type Actor =
        { Kind: string
          Id: string
          Provider: string
          Runtime: string
          Session: string }

    let actor id provider runtime session =
        { Kind = "agent"
          Id = id
          Provider = provider
          Runtime = runtime
          Session = session }

    /// Identity discovery whitelists whichever agent or CI environment the
    /// test itself runs in; every child process starts with none of it, under
    /// either the ROS_ or the PRAXIS_ prefix.
    let private identityKeys =
        [ "CLAUDE_CODE_SESSION_ID"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"; "GEMINI_SESSION_ID"
          "COPILOT_SESSION_ID"; "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "OLLAMA_HOST" ]
        @ ([ "ACTOR"; "ACTOR_KIND"; "TELEMETRY_PROVIDER"; "TELEMETRY_RUNTIME"; "TELEMETRY_MODEL"
             "TELEMETRY_MODEL_VERSION"; "TELEMETRY_RUNTIME_VERSION"; "TELEMETRY_SESSION_ID"
             "TELEMETRY_CONVERSATION_ID"; "TELEMETRY_RUN_ID"; "BASE_REF" ]
           |> List.collect (fun suffix -> [ "ROS_" + suffix; "PRAXIS_" + suffix ]))

    /// The built CLI, under its post-rename name first.
    let cliAssembly =
        [ "praxis.dll"; "ros-fs.dll" ]
        |> List.map (fun name -> Path.Combine(AppContext.BaseDirectory, name))
        |> List.tryFind File.Exists
        |> Option.defaultWith (fun () -> failwith $"no Praxis CLI assembly next to {AppContext.BaseDirectory}")

    let rec private findRoot (directory: DirectoryInfo) =
        match directory with
        | null -> failwith "could not locate the repository root (Ros.slnx)"
        | current when File.Exists(Path.Combine(current.FullName, "Ros.slnx")) && Directory.Exists(Path.Combine(current.FullName, "schemas")) ->
            current.FullName
        | current -> findRoot current.Parent

    let repositoryRoot = lazy (findRoot (DirectoryInfo AppContext.BaseDirectory))

    let repositoryFile (relative: string) =
        Path.Combine(repositoryRoot.Force(), relative)

    let readRepositoryFile = repositoryFile >> File.ReadAllText

    let now () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    /// Runs `program arguments` in `directory` with a clean identity
    /// environment plus `environment`.
    let exec (program: string) (directory: string) (environment: (string * string) list) (arguments: string list) =
        let startInfo = ProcessStartInfo(program)
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- directory
        identityKeys |> List.iter (startInfo.Environment.Remove >> ignore)
        startInfo.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
        environment |> List.iter (fun (key, value) -> startInfo.Environment[key] <- value)
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        { ExitCode = child.ExitCode
          Output = output.Result
          Error = error.Result }

    /// The real CLI entry point, exactly as a user's launcher runs it.
    let cliWith (root: string) (environment: (string * string) list) (arguments: string list) =
        exec "dotnet" root environment ([ cliAssembly; "--root"; root ] @ arguments)

    let cli root arguments = cliWith root [] arguments

    let ok (result: Result) =
        if result.ExitCode <> 0 then
            failwith $"command failed ({result.ExitCode}): {result.Error}\n{result.Output}"

        result

    let git (directory: string) (arguments: string list) =
        let result = exec "git" directory [] arguments

        if result.ExitCode <> 0 then
            failwith $"""git {String.concat " " arguments} failed: {result.Error}"""

        result.Output.Trim()

    let temporaryDirectory (label: string) =
        Path.Combine(Path.GetTempPath(), $"praxis-fence-{label}-{Guid.NewGuid():N}")
        |> Directory.CreateDirectory
        |> fun info -> info.FullName

    let write (directory: string) (relative: string) (content: string) =
        let path = Path.Combine(directory, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let configureGitIdentity directory =
        [ [ "config"; "user.email"; "fence@example.invalid" ]
          [ "config"; "user.name"; "Fence" ]
          [ "config"; "commit.gpgsign"; "false" ] ]
        |> List.iter (git directory >> ignore)

    let commitAll directory (message: string) =
        git directory [ "add"; "-A" ] |> ignore
        git directory [ "commit"; "-q"; "--allow-empty"; "-m"; message ] |> ignore
        git directory [ "rev-parse"; "HEAD" ]

    let readJson (path: string) =
        JsonNode.Parse(File.ReadAllText path) :?> JsonObject

    let writeJson (path: string) (node: JsonNode) =
        File.WriteAllText(path, node.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n")

    /// Applies `change` to the parsed `ros.json` of `root`.
    let editConfig (root: string) (change: JsonObject -> unit) =
        let path = Path.Combine(root, "ros.json")
        let config = readJson path
        change config
        writeJson path config

    let text (node: JsonNode) = node.GetValue<string>()

    let boolean (node: JsonNode) = node.GetValue<bool>()

    let strings (node: JsonNode) =
        node.AsArray() |> Seq.map text |> List.ofSeq

    let array (node: JsonNode) = node.AsArray() |> List.ofSeq

    let workItem (root: string) (id: string) =
        (readJson (Path.Combine(root, ".ros", "context", "current.json"))).["workItems"]
        |> array
        |> List.find (fun item -> text item.["id"] = id)

    let events (root: string) =
        File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl"))
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
        |> List.ofArray

    let contains (needle: string) (haystack: string) message =
        Assert.isTrue (haystack.Contains(needle, StringComparison.Ordinal)) $"{message}: expected to find '{needle}'"

    let excludes (needle: string) (haystack: string) message =
        Assert.isTrue (not (haystack.Contains(needle, StringComparison.Ordinal))) $"{message}: did not expect '{needle}'"

    let matches (pattern: string) (value: string) message =
        Assert.isTrue (Text.RegularExpressions.Regex.IsMatch(value, pattern, Text.RegularExpressions.RegexOptions.Multiline)) $"{message}: /{pattern}/ did not match"
