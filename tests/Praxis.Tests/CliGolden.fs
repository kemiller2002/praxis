namespace Praxis.Tests

open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Fixture and golden-master helpers for the end-to-end CLI suites ported
/// from the retired Node `*-fsharp-differential` tests. Every fixture is a
/// real repository installed by the real F# `praxis init` (the retired suites
/// used the legacy Node bootstrap, which installed the same starter payload);
/// every golden master is the literal the Node suite had frozen, kept under
/// `Golden/` with the install work-item id abstracted as `{install}`.
[<RequireQualifiedAccess>]
module CliGolden =
    let private writeOptions =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let rec private findRepositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "tests", "Praxis.Tests")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            findRepositoryRoot directory.Parent

    /// The Praxis checkout the tests run from (they run from its root).
    let repositoryRoot () = findRepositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))

    let private release = lazy (JsonNode.Parse(File.ReadAllText(Path.Combine(repositoryRoot (), "release.json"))))

    /// release.json is the single authoritative version (the retired suites
    /// read package.json).
    let releaseVersion () = release.Value.["version"].GetValue<string>()

    let releasePackage () = release.Value.["name"].GetValue<string>()

    /// The installation work item `praxis init` records: ROS-INSTALL-<version>.
    let installWorkItemId () = "ROS-INSTALL-" + releaseVersion().Replace(".", "-")

    // ---------------------------------------------------------------- JSON

    let parse (text: string) = JsonNode.Parse text

    let render (node: JsonNode) =
        if isNull node then "null" else node.ToJsonString writeOptions

    /// Structural JSON equality (object key order is irrelevant, as it was
    /// for Node's `assert.deepEqual`).
    let jsonEqual (expected: JsonNode) (actual: JsonNode) =
        if not (JsonNode.DeepEquals(expected, actual)) then
            failwith $"JSON differs.\nExpected: {render expected}\nActual:   {render actual}"

    let clone (node: JsonNode) = if isNull node then null else node.DeepClone()

    /// A deep copy with every property named in `keys` removed at any depth.
    let rec without (keys: Set<string>) (node: JsonNode) : JsonNode =
        match node with
        | :? JsonObject as record ->
            let copy = JsonObject()

            for KeyValue(name, value) in record do
                if not (keys.Contains name) then copy.[name] <- without keys value

            copy
        | :? JsonArray as items ->
            let copy = JsonArray()

            for item in items do
                copy.Add(without keys item)

            copy
        | other -> clone other

    let items (node: JsonNode) = node.AsArray() |> Seq.toList

    let text (node: JsonNode) (name: string) =
        match node.[name] with
        | null -> None
        | value -> Some(value.GetValue<string>())

    let find (collection: JsonNode) (id: string) =
        items collection
        |> List.tryFind (fun item -> text item "id" = Some id)
        |> Option.defaultWith (fun () -> failwith $"no entry '{id}' in {render collection}")

    // -------------------------------------------------------------- goldens

    let private goldens = Collections.Concurrent.ConcurrentDictionary<string, JsonNode>()

    /// One frozen golden master, by suite file and key, with the install
    /// work-item placeholder resolved to this release's id.
    let golden (suite: string) (key: string) : JsonNode =
        let document =
            goldens.GetOrAdd(
                suite,
                fun name ->
                    let path = Path.Combine(AppContext.BaseDirectory, "Golden", $"{name}.json")
                    parse (File.ReadAllText(path).Replace("{install}", installWorkItemId ()))
            )

        match document.[key] with
        | null -> failwith $"golden '{suite}/{key}' does not exist"
        | value -> clone value

    // ------------------------------------------------------------- fixtures

    let readJson root relativePath = parse (CliHarness.read root relativePath)

    let writeJson root relativePath (node: JsonNode) =
        CliHarness.write root relativePath (node.ToJsonString writeOptions + "\n")

    let updateJson root relativePath (change: JsonNode -> unit) =
        let node = readJson root relativePath
        change node
        writeJson root relativePath node

    let contextPath = ".ros/context/current.json"
    let queuePath = ".ros/work/queue.json"
    let queueMarkdownPath = ".ros/work/queue.md"

    let context root = readJson root contextPath

    let contextItem root id = find (context root).["workItems"] id

    let updateContextItem root id (change: JsonNode -> unit) =
        updateJson root contextPath (fun document -> change (find document.["workItems"] id))

    let queue root = readJson root queuePath

    let events root =
        let path = Path.Combine(root, ".ros", "events", "events.jsonl")

        if File.Exists path then
            File.ReadAllLines path
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.map parse
            |> Array.toList
        else
            []

    let jsonList (nodes: JsonNode list) =
        let array = JsonArray()

        for node in nodes do
            array.Add(clone node)

        array :> JsonNode

    /// Every execution record, ordered by file name (the execution id).
    let executions root =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")

        if Directory.Exists directory then
            Directory.GetFiles(directory, "*.json")
            |> Array.sortWith (fun left right -> String.CompareOrdinal(left, right))
            |> Array.map (File.ReadAllText >> parse)
            |> Array.toList
        else
            []

    let metricValue (record: JsonNode) (id: string) =
        items record.["metrics"] |> List.tryFind (fun metric -> text metric "id" = Some id) |> Option.map (fun metric -> metric.["value"])

    let capability (record: JsonNode) (id: string) =
        items record.["capabilities"]
        |> List.find (fun entry -> text entry "metricId" = Some id)

    /// Disables telemetry in ros.json, as most retired fixtures did.
    let disableTelemetry root =
        updateJson root "ros.json" (fun config -> config.["telemetry"].["enabled"] <- JsonValue.Create false)

    /// A fresh repository: `git init`, the real `praxis init --project`,
    /// then `prepare`, then (when `commit`) one baseline commit.
    let repository (prefix: string) (project: string) (prepare: string -> unit) (commit: bool) =
        let root = CliHarness.temporaryDirectory prefix

        try
            CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            CliHarness.rosOk root [ "init"; "--project"; project ] |> ignore
            // The goldens pin pre-continuity completion semantics.
            CliHarness.optOutOfDurableCheckpoints root
            prepare root

            if commit then
                CliHarness.commitAll root "baseline"

            root
        with _ ->
            CliHarness.removeDirectory root
            reraise ()

    /// Runs `test` against a fresh repository and always removes it.
    let withRepository prefix project prepare commit (test: string -> unit) =
        let root = repository prefix project prepare commit

        try
            test root
        finally
            CliHarness.removeDirectory root

    let noPreparation (_: string) = ()

    /// The real wall clock, as the retired suites' `at()` used for
    /// transitions whose chronology must follow a real execution start.
    let at () = CliHarness.now ()

    /// Fails unless a CLI run exited as expected, showing its output.
    let expectExit (expected: int) (result: CliHarness.Run) =
        if result.Exit <> expected then
            failwith $"Expected exit {expected} but got {result.Exit}\nstdout: {result.Out}\nstderr: {result.Err}"

    let contains (fragment: string) (value: string) =
        Assert.isTrue (value.Contains(fragment, StringComparison.Ordinal)) $"Expected '{fragment}' in:\n{value}"
