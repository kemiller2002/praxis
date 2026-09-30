namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Helpers for the end-to-end CLI tests ported from the former Node test
/// suite (telemetry, adapter, provenance, lifecycle and shell-script tests).
/// Everything runs the real built `praxis` CLI through `CliHarness`.
[<RequireQualifiedAccess>]
module CliPort =
    /// The repository checkout this test binary was built from: the nearest
    /// ancestor of the binary that carries `release.json`.
    let repositoryRoot =
        lazy
            (let rec walk (directory: DirectoryInfo) =
                if isNull directory then
                    failwith "Could not locate the repository root (no release.json above the test binary)"
                elif File.Exists(Path.Combine(directory.FullName, "release.json")) then
                    directory.FullName
                else
                    walk directory.Parent

             walk (DirectoryInfo AppContext.BaseDirectory))

    /// `release.json`'s version: the version every installed artifact and
    /// `--version` report.
    let releaseVersion =
        lazy
            (use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot.Value, "release.json")))
             document.RootElement.GetProperty("version").GetString())

    let private relaxed =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private relaxedIndented =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true)

    /// Compact JSON text, key order preserved, without HTML-safe escaping
    /// (the same bytes `JSON.stringify` produces).
    let compact (node: JsonNode) =
        if isNull node then "null" else node.ToJsonString relaxed

    /// Two-space indented JSON text (the same bytes `JSON.stringify(v, null, 2)` produces).
    let indented (json: string) = JsonNode.Parse(json).ToJsonString relaxedIndented

    let parse (text: string) = JsonNode.Parse text

    let readJson (root: string) (relativePath: string) = parse (CliHarness.read root relativePath)

    let writeJson (root: string) (relativePath: string) (node: JsonNode) =
        CliHarness.write root relativePath ((node.ToJsonString relaxedIndented) + "\n")

    /// Structural equality (object key order ignored, array order kept).
    let deepEqual (expectedJson: string) (actual: JsonNode) =
        let expected = JsonNode.Parse expectedJson

        if not (JsonNode.DeepEquals(expected, actual)) then
            failwith $"Expected JSON: {compact expected}\nActual JSON:   {compact actual}"

    let text (node: JsonNode) = node.GetValue<string>()

    let number (node: JsonNode) = node.GetValue<double>()

    let boolean (node: JsonNode) = node.GetValue<bool>()

    /// Follows a chain of property names (`at [ "a"; "b" ] node` is `node["a"]["b"]`).
    let at (names: string list) (node: JsonNode) =
        names |> List.fold (fun (current: JsonNode) (name: string) -> current[name]) node

    /// Replaces each `{{name}}` placeholder in a (non-interpolated) template.
    /// JSON templates use this rather than F# string interpolation, whose
    /// brace escaping collides with nested JSON objects.
    let fill (values: (string * string) list) (template: string) =
        values |> List.fold (fun (text: string) (name, value) -> text.Replace("{{" + name + "}}", value)) template

    let items (node: JsonNode) =
        node.AsArray() |> Seq.toList

    /// A string property, or None when it is absent or null.
    let stringOf (node: JsonNode) (name: string) =
        match (node[name]) with
        | null -> None
        | value -> Some(value.GetValue<string>())

    let contains (fragment: string) (text: string) =
        if not (text.Contains fragment) then
            failwith $"Expected text containing '{fragment}' but received:\n{text}"

    let matches (pattern: string) (text: string) =
        if not (Text.RegularExpressions.Regex.IsMatch(text, pattern, Text.RegularExpressions.RegexOptions.Multiline)) then
            failwith $"Expected text matching /{pattern}/ but received:\n{text}"

    let exitCode (expected: int) (result: CliHarness.Run) =
        if result.Exit <> expected then
            failwith $"Expected exit {expected} but received {result.Exit}\nstdout: {result.Out}\nstderr: {result.Err}"

    /// Runs `praxis --root ROOT ARGS` with the given text on standard input.
    let rosWithInput (root: string) (arguments: string list) (input: string) : CliHarness.Run =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        [ CliHarness.cli; "--root"; root ] @ arguments |> List.iter startInfo.ArgumentList.Add
        CliHarness.identityVariables |> List.iter (startInfo.Environment.Remove >> ignore)
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEndAsync()
        let error = child.StandardError.ReadToEndAsync()
        child.StandardInput.Write input
        child.StandardInput.Close()
        child.WaitForExit()

        { Exit = child.ExitCode
          Out = output.Result
          Err = error.Result }

    /// A fresh Git repository initialized with the real F# `init` under the
    /// given project name (which also fixes the repository id to its slug,
    /// independent of the temporary folder name) and committed.
    let initializedProject (prefix: string) (project: string) =
        let root = CliHarness.temporaryDirectory prefix
        CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        CliHarness.rosOk root [ "init"; "--project"; project ] |> ignore
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "baseline"
        root

    /// A committed, F#-initialized greenfield repository named `project` for
    /// the duration of `run`, removed afterwards.
    let withRepository (project: string) (run: string -> unit) =
        let root = initializedProject "ros-cli-port" project

        try
            run root
        finally
            CliHarness.removeDirectory root

    /// An empty temporary directory for the duration of `run`.
    let withDirectory (prefix: string) (run: string -> unit) =
        let root = CliHarness.temporaryDirectory prefix

        try
            run root
        finally
            CliHarness.removeDirectory root

    /// Runs `body` with a factory of labelled temporary directories, every
    /// one of which is removed afterwards, however `body` ends.
    let withTemporaries (body: (string -> string) -> 'result) : 'result =
        let created = Collections.Generic.List<string>()

        let temporary (label: string) =
            let path = CliHarness.temporaryDirectory label
            created.Add path
            path

        try
            body temporary
        finally
            created |> Seq.iter CliHarness.removeDirectory

    /// A temporary file outside any repository, removed afterwards.
    let withInputFile (content: string) (run: string -> 'result) : 'result =
        let path = Path.Combine(Path.GetTempPath(), $"ros-input-{Guid.NewGuid():N}.json")
        File.WriteAllText(path, content)

        try
            run path
        finally
            if File.Exists path then File.Delete path

    let executionPath (executionId: string) =
        $".ros/telemetry/executions/{executionId}.json"

    let readExecution (root: string) (executionId: string) = readJson root (executionPath executionId)

    let writeExecution (root: string) (executionId: string) (json: string) =
        CliHarness.write root (executionPath executionId) json

    /// The value of the first metric with this id, if any.
    let metricValue (record: JsonNode) (metricId: string) =
        items (record["metrics"])
        |> List.tryFind (fun metric -> stringOf metric "id" = Some metricId)
        |> Option.map (fun metric -> number (metric["value"]))

    /// Starts work item ID (type `task` unless given) through the real CLI.
    let startWork (root: string) (id: string) (occurredAt: string) (workType: string) =
        CliHarness.rosOk root [ "work"; "start"; "--id"; id; "--occurred-at"; occurredAt; "--type"; workType ]
        |> ignore

    /// Sets the file mode on Unix; a no-op elsewhere.
    let makeExecutable (path: string) =
        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute
            )

    let sha256Text (content: string) =
        Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes content)).ToLowerInvariant()

    /// Content-addressed snapshot of a directory tree (paths and file hashes),
    /// used to prove a command changed nothing.
    let snapshot (root: string) =
        let rec walk (directory: string) (prefix: string) =
            [ for entry in Directory.GetFileSystemEntries directory |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) do
                  let name = Path.GetFileName entry
                  let relative = if prefix = "" then name else $"{prefix}/{name}"

                  if Directory.Exists entry then
                      yield $"D:{relative}"
                      yield! walk entry relative
                  else
                      let hash = Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes entry))
                      yield $"F:{relative}:{hash}" ]

        String.Join("\n", walk root "")
