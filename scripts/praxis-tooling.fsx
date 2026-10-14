// Repository automation helpers for this repository's maintainer scripts and
// workflows (RQ-ROS-2026-A024: build tooling and executable automation are
// F#/.NET). Run with `dotnet fsi scripts/praxis-tooling.fsx COMMAND ...`.
//
// Commands (exit 0 on success, 1 on a false check, 2 on bad input):
//   json-get FILE KEY                 print a top-level string property
//   json-set FILE KEY VALUE           set a top-level string property, keeping key order
//   json-validate FILE                parse FILE as JSON and print it indented
//   json-array-contains KEY VALUE     stdin is JSON; succeed when array KEY holds VALUE
//   semver-next CURRENT BUMP          print the next X.Y.Z for patch|minor|major|X.Y.Z
//   semver-newer REQUESTED CURRENT    succeed when REQUESTED > CURRENT (X.Y.Z)
//   remote-enable VERSION CAPS        pin .echelon/toolchain.json and set ros.json remote.capabilities
//   record-compatibility VERSION FILE record the published release.json FILE's declared state
//                                     compatibility for VERSION in quality/release-compatibility.json
//                                     and retire that release's exceptions; exit 1 when FILE declares none
//   remote-describe-request ID        print a praxis.describe request document
//   remote-describe-summary FILE      summarize a praxis.remote response; succeed when it succeeded

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

let private options =
    JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private fail (code: int) (message: string) : int =
    eprintfn "praxis-tooling: %s" message
    code

let private readObject (path: string) =
    match JsonNode.Parse(File.ReadAllText path) with
    | :? JsonObject as document -> document
    | _ -> failwith $"{path} is not a JSON object"

let private write (path: string) (document: JsonNode) =
    File.WriteAllText(path, document.ToJsonString options + "\n", UTF8Encoding false)

let private core = Regex(@"^(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant)

let private version (value: string) =
    let found = core.Match value
    if found.Success then Some(int found.Groups[1].Value, int found.Groups[2].Value, int found.Groups[3].Value) else None

let private stringOf (node: JsonNode) =
    match node with
    | :? JsonValue as value ->
        match value.TryGetValue<string>() with
        | true, text -> Some text
        | _ -> None
    | _ -> None

let private run (arguments: string list) : int =
    match arguments with
    | [ "json-get"; file; key ] ->
        let document = readObject file

        match stringOf document[key] with
        | Some value ->
            printfn "%s" value
            0
        | None -> fail 2 $"{file} has no string property '{key}'"
    | [ "json-set"; file; key; value ] ->
        let document = readObject file
        document[key] <- JsonValue.Create value
        write file document
        0
    | [ "json-validate"; file ] ->
        printfn "%s" ((JsonNode.Parse(File.ReadAllText file)).ToJsonString options)
        0
    | [ "json-array-contains"; key; value ] ->
        match JsonNode.Parse(Console.In.ReadToEnd()) with
        | :? JsonObject as document ->
            match document[key] with
            | :? JsonArray as items -> if items |> Seq.exists (fun item -> stringOf item = Some value) then 0 else 1
            | _ -> 1
        | _ -> fail 2 "stdin is not a JSON object"
    | [ "semver-next"; current; bump ] ->
        match version current with
        | None -> fail 2 $"current version {current} is not X.Y.Z"
        | Some(major, minor, patch) ->
            let next =
                match bump with
                | "major" -> $"{major + 1}.0.0"
                | "minor" -> $"{major}.{minor + 1}.0"
                | "patch" -> $"{major}.{minor}.{patch + 1}"
                | explicit -> explicit

            match version next with
            | None -> fail 2 $"\"{bump}\" is not patch, minor, major or X.Y.Z"
            | Some parsed when parsed <= (major, minor, patch) -> fail 2 $"{next} is not newer than {current}"
            | Some _ ->
                printf "%s" next
                0
    | [ "semver-newer"; requested; current ] ->
        match version requested, version current with
        | Some requested, Some current -> if requested > current then 0 else 1
        | _ -> fail 2 "versions must be X.Y.Z"
    | [ "remote-enable"; pinned; capabilities ] ->
        Directory.CreateDirectory ".echelon" |> ignore
        let toolchainPath = Path.Combine(".echelon", "toolchain.json")

        let toolchain =
            if File.Exists toolchainPath then readObject toolchainPath
            else JsonObject(dict [ "schemaVersion", (JsonValue.Create 1 :> JsonNode) ])

        toolchain["praxis"] <- JsonValue.Create pinned
        write toolchainPath toolchain
        let config = readObject "ros.json"
        let remote = JsonObject()
        let granted = JsonArray()

        for capability in capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries) do
            granted.Add(JsonValue.Create capability)

        remote["capabilities"] <- granted
        config["remote"] <- remote

        let protocol =
            match config["workProtocol"] with
            | :? JsonObject as existing -> existing
            | _ ->
                let created = JsonObject()
                config["workProtocol"] <- created
                created

        let ignored =
            match protocol["ignoredPaths"] with
            | :? JsonArray as existing -> existing
            | _ ->
                let created = JsonArray()
                protocol["ignoredPaths"] <- created
                created

        // The request journal is Praxis bookkeeping.
        if not (ignored |> Seq.exists (fun item -> stringOf item = Some ".ros/remote/**")) then
            ignored.Add(JsonValue.Create ".ros/remote/**")

        write "ros.json" config
        0
    | [ "record-compatibility"; pinned; published ] ->
        // PRX-QUAL-010: the pin is checked against the pinned release's own
        // declaration, recorded here when it is pinned.
        match (readObject published)["compatibility"] with
        | :? JsonObject as declared ->
            let path = Path.Combine("quality", "release-compatibility.json")
            let record = readObject path

            let reads =
                match declared["stateSchemas"] with
                | :? JsonObject as schemas ->
                    JsonObject(schemas |> Seq.map (fun entry -> Collections.Generic.KeyValuePair(entry.Key, entry.Value["reads"].DeepClone())))
                | _ -> JsonObject()

            let entry = JsonObject()
            entry["remoteProtocol"] <- declared["remoteProtocol"].DeepClone()
            entry["reads"] <- reads
            entry["recordedFrom"] <- JsonValue.Create $"release.json at tag v{pinned}"

            let releases =
                match record["releases"] with
                | :? JsonObject as existing -> existing
                | _ ->
                    let created = JsonObject()
                    record["releases"] <- created
                    created

            releases[pinned] <- entry

            let kept =
                match record["exceptions"] with
                | :? JsonArray as exceptions ->
                    exceptions |> Seq.filter (fun item -> stringOf item["release"] <> Some pinned) |> Seq.map _.DeepClone() |> Seq.toArray
                | _ -> [||]

            record["exceptions"] <- JsonArray(kept)
            write path record
            0
        | _ -> fail 1 $"{published} declares no state compatibility"
    | [ "remote-describe-request"; requestId ] ->
        let actor = JsonObject()
        actor["kind"] <- JsonValue.Create "human"
        let request = JsonObject()
        request["protocol"] <- JsonValue.Create "praxis.remote"
        request["protocolVersion"] <- JsonValue.Create "1.2"
        request["requestId"] <- JsonValue.Create requestId
        request["operation"] <- JsonValue.Create "praxis.describe"
        request["actor"] <- actor
        printfn "%s" (request.ToJsonString())
        0
    | [ "remote-describe-summary"; file ] ->
        let response = readObject file
        let field (node: JsonNode) (name: string) = match node with :? JsonObject as item -> item[name] | _ -> null
        let text (node: JsonNode) = if isNull node then "None" else node.ToJsonString()
        let count (node: JsonNode) = match node with :? JsonArray as items -> items.Count | _ -> 0
        let result = response["result"]
        let outcome = stringOf response["outcome"] |> Option.defaultValue "unknown"
        printfn "outcome: %s  praxis: %s  executor: %s" outcome (stringOf response["praxisVersion"] |> Option.defaultValue "None") (stringOf (field response["executor"] "kind") |> Option.defaultValue "None")
        printfn "protocol versions: %s  repository capabilities: %s" (text (field result "protocolVersions")) (text (field (field result "repository") "capabilities"))
        printfn "operations: %d  open work: %d" (count (field result "operations")) (count (field result "openWork"))
        if outcome = "succeeded" then 0 else 1
    | _ -> fail 2 "unknown command; see the header of scripts/praxis-tooling.fsx"

exit (
    try
        run (fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray)
    with
    | :? IOException
    | :? JsonException
    | :? UnauthorizedAccessException as error -> fail 2 error.Message
)
