namespace Praxis.Infrastructure.Tutela

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Git

/// Tutela security assessments ingested into an append-only store and
/// queried as metrics over time (TUT-1..3). `.ros/telemetry/tutela/
/// observations.jsonl` holds one observation per ingested assessment;
/// lines are never rewritten, and re-ingesting the same document is a no-op.
[<RequireQualifiedAccess>]
module TutelaStore =
    [<Literal>]
    let RelativePath = ".ros/telemetry/tutela/observations.jsonl"

    let private path root = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar))

    let private str (node: JsonNode) =
        match node with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None

    let private field (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as o -> o[name] |> Option.ofObj
        | _ -> None

    let private text name node = field name node |> Option.bind str

    let private array name node =
        match field name node with
        | Some(:? JsonArray as items) -> items |> Seq.choose Option.ofObj |> List.ofSeq
        | _ -> []

    let private strings name node = array name node |> List.choose str

    let private time (raw: string) =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, at -> Some at
        | _ -> None

    let private stamp (at: DateTimeOffset) = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let sha256 (text: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    /// Read a Tutela security assessment (schemaVersion 1). Only identifiers,
    /// states and times are kept; evidence content and rationale are not.
    let parseAssessment (raw: string) (collectedAt: DateTimeOffset) : Result<TutelaObservation, string> =
        let document =
            try
                Ok(JsonNode.Parse raw)
            with :? JsonException as error ->
                Error $"the assessment is not valid JSON: {error.Message}"

        document
        |> Result.bind (fun node ->
            match node with
            | :? JsonObject as root ->
                let version =
                    match root["schemaVersion"] with
                    | :? JsonValue as v ->
                        match v.TryGetValue<int>() with
                        | true, n -> Some n
                        | _ -> None
                    | _ -> None

                match version, field "subject" root |> Option.bind (text "repository"), field "subject" root |> Option.bind (text "ref") with
                | Some 1, Some repository, Some reference when repository <> "" && reference <> "" ->
                    let invariants = array "invariantResults" root

                    let requiring =
                        invariants
                        |> List.filter (fun i ->
                            match field "requiresIndependentVerification" i with
                            | Some(:? JsonValue as v) -> v.GetValueKind() = JsonValueKind.True
                            | _ -> false)

                    let evidence =
                        array "evidence" root
                        |> List.choose (fun e ->
                            match text "id" e, text "observedAt" e |> Option.bind time with
                            | Some id, Some at -> Some(id, at)
                            | _ -> None)

                    Ok
                        { Repository = repository
                          Ref = reference
                          Commit = None
                          CollectedAt = collectedAt
                          SourceSha256 = sha256 raw
                          Invariants = invariants |> List.choose (fun i -> Option.map2 (fun id state -> id, state) (text "id" i) (text "state" i))
                          UnknownEffects = strings "unknownSecurityEffects" root
                          Findings = strings "findings" root
                          Evidence = evidence
                          StaleEvidence = strings "staleEvidence" root
                          Exceptions =
                            array "exceptions" root
                            |> List.choose (fun e ->
                                text "id" e
                                |> Option.map (fun id ->
                                    { Id = id
                                      CreatedAt = text "createdAt" e |> Option.bind time
                                      ExpiresAt = text "expiresAt" e |> Option.bind time }))
                          IndependentRequired = Some requiring.Length
                          IndependentAttested = Some(requiring |> List.filter (fun i -> not (array "verifierAttestations" i).IsEmpty) |> List.length)
                          TrustRootChanges = field "trustRootChange" root |> Option.bind (text "id") |> Option.toList
                          SensitiveChanges = None }
                | Some 1, _, _ -> Error "the assessment has no subject repository and ref (TUT-3 provenance is required)"
                | _ -> Error "only Tutela security assessments with schemaVersion 1 are supported"
            | _ -> Error "the assessment must be a JSON object")

    let private toJson (o: TutelaObservation) =
        let node = JsonObject()
        let list (items: string list) = JsonArray(items |> List.map (fun s -> JsonValue.Create s :> JsonNode) |> Array.ofList)
        let opt (value: 'a option) (f: 'a -> JsonNode) = value |> Option.map f |> Option.toObj
        node["schema"] <- JsonValue.Create "praxis.tutela-observation/1"
        node["repository"] <- JsonValue.Create o.Repository
        node["ref"] <- JsonValue.Create o.Ref
        node["commit"] <- opt o.Commit (fun c -> JsonValue.Create c :> JsonNode)
        node["collectedAt"] <- JsonValue.Create(stamp o.CollectedAt)
        node["sourceSha256"] <- JsonValue.Create o.SourceSha256
        node["invariants"] <- JsonArray(o.Invariants |> List.map (fun (id, state) -> JsonObject(dict [ "id", JsonValue.Create id :> JsonNode; "state", JsonValue.Create state ]) :> JsonNode) |> Array.ofList)
        node["unknownEffects"] <- list o.UnknownEffects
        node["findings"] <- list o.Findings
        node["evidence"] <- JsonArray(o.Evidence |> List.map (fun (id, at) -> JsonObject(dict [ "id", JsonValue.Create id :> JsonNode; "observedAt", JsonValue.Create(stamp at) ]) :> JsonNode) |> Array.ofList)
        node["staleEvidence"] <- list o.StaleEvidence

        node["exceptions"] <-
            JsonArray(
                o.Exceptions
                |> List.map (fun e ->
                    let x = JsonObject()
                    x["id"] <- JsonValue.Create e.Id
                    x["createdAt"] <- opt e.CreatedAt (fun at -> JsonValue.Create(stamp at) :> JsonNode)
                    x["expiresAt"] <- opt e.ExpiresAt (fun at -> JsonValue.Create(stamp at) :> JsonNode)
                    x :> JsonNode)
                |> Array.ofList
            )

        node["independentRequired"] <- opt o.IndependentRequired (fun n -> JsonValue.Create n :> JsonNode)
        node["independentAttested"] <- opt o.IndependentAttested (fun n -> JsonValue.Create n :> JsonNode)
        node["trustRootChanges"] <- list o.TrustRootChanges
        node["sensitiveChanges"] <- opt o.SensitiveChanges (fun items -> list items :> JsonNode)
        node

    let private int name node =
        match field name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<int>() with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    let private ofJson (node: JsonNode) : Result<TutelaObservation, string> =
        match text "repository" node, text "ref" node, text "collectedAt" node |> Option.bind time, text "sourceSha256" node with
        | Some repository, Some reference, Some collectedAt, Some digest ->
            Ok
                { Repository = repository
                  Ref = reference
                  Commit = text "commit" node
                  CollectedAt = collectedAt
                  SourceSha256 = digest
                  Invariants = array "invariants" node |> List.choose (fun i -> Option.map2 (fun a b -> a, b) (text "id" i) (text "state" i))
                  UnknownEffects = strings "unknownEffects" node
                  Findings = strings "findings" node
                  Evidence = array "evidence" node |> List.choose (fun e -> Option.map2 (fun a b -> a, b) (text "id" e) (text "observedAt" e |> Option.bind time))
                  StaleEvidence = strings "staleEvidence" node
                  Exceptions =
                    array "exceptions" node
                    |> List.choose (fun e ->
                        text "id" e
                        |> Option.map (fun id ->
                            { Id = id
                              CreatedAt = text "createdAt" e |> Option.bind time
                              ExpiresAt = text "expiresAt" e |> Option.bind time }))
                  IndependentRequired = int "independentRequired" node
                  IndependentAttested = int "independentAttested" node
                  TrustRootChanges = strings "trustRootChanges" node
                  SensitiveChanges =
                    match field "sensitiveChanges" node with
                    | Some(:? JsonArray) -> Some(strings "sensitiveChanges" node)
                    | _ -> None }
        | _ -> Error "a stored Tutela observation lacks repository, ref, collectedAt or sourceSha256"

    /// Every stored observation, in append order. A damaged line is an error
    /// naming its line number, never skipped.
    let read (root: string) : Result<TutelaObservation list, string> =
        let file = path root

        if not (File.Exists file) then
            Ok []
        else
            File.ReadAllLines file
            |> Array.toList
            |> List.indexed
            |> List.filter (fun (_, line) -> line.Trim() <> "")
            |> List.fold
                (fun acc (index, line) ->
                    acc
                    |> Result.bind (fun items ->
                        let parsed =
                            try
                                JsonNode.Parse line |> Option.ofObj |> Option.map Ok |> Option.defaultValue (Error "empty")
                            with :? JsonException as error ->
                                Error error.Message

                        parsed
                        |> Result.bind ofJson
                        |> Result.mapError (fun message -> $"{RelativePath} line {index + 1}: {message}")
                        |> Result.map (fun o -> o :: items)))
                (Ok [])
            |> Result.map List.rev

    /// Security-sensitive path globs from ros.json `tutela.sensitivePaths`.
    let sensitivePaths (root: string) =
        let config = Path.Combine(root, "ros.json")

        if not (File.Exists config) then
            []
        else
            match JsonNode.Parse(File.ReadAllText config) |> Option.ofObj |> Option.bind (field "tutela") with
            | Some tutela -> strings "sensitivePaths" tutela
            | None -> []

    let private resolveCommit root (reference: string) =
        match ProcessGitRepository.readLines root [ "rev-parse"; "--verify"; "--quiet"; reference + "^{commit}" ] with
        | Ok [ commit ] -> Some commit
        | _ -> None

    let private changedBetween root (patterns: string list) (before: string) (after: string) =
        match ProcessGitRepository.readLines root [ "diff"; "--name-only"; before; after ] with
        | Ok paths -> Some(paths |> List.filter (fun p -> patterns |> List.exists (fun g -> Glob.isMatch g p)))
        | Error _ -> None

    type IngestOutcome =
        | Recorded of TutelaObservation
        | AlreadyRecorded of TutelaObservation

    /// Ingest one assessment: resolve its ref to a commit, measure
    /// security-sensitive churn since the repository's previous observation,
    /// and append it. Idempotent by source digest.
    let ingest (root: string) (raw: string) (collectedAt: DateTimeOffset) : Result<IngestOutcome, string> =
        parseAssessment raw collectedAt
        |> Result.bind (fun parsed ->
            read root
            |> Result.map (fun existing ->
                match existing |> List.tryFind (fun o -> o.SourceSha256 = parsed.SourceSha256 && o.Repository = parsed.Repository) with
                | Some already -> AlreadyRecorded already
                | None ->
                    let commit = resolveCommit root parsed.Ref
                    let patterns = sensitivePaths root

                    let previous =
                        existing
                        |> List.filter (fun o -> o.Repository = parsed.Repository && o.CollectedAt < collectedAt)
                        |> List.sortBy _.CollectedAt
                        |> List.tryLast

                    let changes =
                        match patterns, previous |> Option.bind _.Commit, commit with
                        | [], _, _ -> None
                        | _, Some before, Some after -> changedBetween root patterns before after
                        | _ -> None

                    let observation = { parsed with Commit = commit; SensitiveChanges = changes }
                    let file = path root
                    Directory.CreateDirectory(Path.GetDirectoryName file |> Option.ofObj |> Option.defaultValue ".") |> ignore
                    File.AppendAllText(file, (toJson observation).ToJsonString() + "\n")
                    Recorded observation))

    /// Praxis execution scope expansions (boundary/capability changes) whose
    /// record falls in (from, until].
    let scopeExpansionsBetween (root: string) (from: DateTimeOffset) (until: DateTimeOffset) =
        ExecutionStore.list root
        |> List.collect (fun id -> ExecutionStore.readRecords root id "scope-expanded")
        |> List.choose (text "at" >> Option.bind time)
        |> List.filter (fun at -> at > from && at <= until)
        |> List.length

    /// Metrics over every stored observation.
    let metrics (root: string) : Result<SecurityMetricPoint list, string> =
        read root
        |> Result.map (TutelaHistory.derive (not (sensitivePaths root).IsEmpty) (scopeExpansionsBetween root))

    let private valueNode (value: MetricValue) =
        match value with
        | MetricValue.Measured v -> JsonValue.Create v :> JsonNode
        | MetricValue.Unknown _ -> null

    /// `praxis.tutela-metrics/1`.
    let render (points: SecurityMetricPoint list) =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create "praxis.tutela-metrics/1"
        root["disclaimer"] <- JsonValue.Create TutelaHistory.Disclaimer

        root["observations"] <-
            JsonArray(
                points
                |> List.map (fun p ->
                    let node = JsonObject()
                    node["repository"] <- JsonValue.Create p.Repository
                    node["ref"] <- JsonValue.Create p.Ref
                    node["commit"] <- p.Commit |> Option.map (fun c -> JsonValue.Create c :> JsonNode) |> Option.toObj
                    node["collectedAt"] <- JsonValue.Create(stamp p.CollectedAt)
                    node["sourceSha256"] <- JsonValue.Create p.SourceSha256

                    node["metrics"] <-
                        JsonArray(
                            p.Metrics
                            |> List.map (fun m ->
                                let x = JsonObject()
                                x["id"] <- JsonValue.Create m.Id
                                x["dimensions"] <- JsonObject(m.Dimensions |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, JsonValue.Create v :> JsonNode)))
                                x["unit"] <- JsonValue.Create m.Unit
                                x["value"] <- valueNode m.Value

                                match m.Value with
                                | MetricValue.Measured _ -> x["status"] <- JsonValue.Create "measured"
                                | MetricValue.Unknown reason ->
                                    x["status"] <- JsonValue.Create "unknown"
                                    x["reason"] <- JsonValue.Create reason

                                x :> JsonNode)
                            |> Array.ofList
                        )

                    node :> JsonNode)
                |> Array.ofList
            )

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let renderText (points: SecurityMetricPoint list) =
        let lines = ResizeArray<string>()
        lines.Add TutelaHistory.Disclaimer

        for p in points do
            let commit = p.Commit |> Option.map (fun c -> " @ " + c.Substring(0, min 12 c.Length)) |> Option.defaultValue ""
            lines.Add ""
            lines.Add $"{p.Repository} {p.Ref}{commit} collected {stamp p.CollectedAt}"

            for m in p.Metrics do
                let dims = m.Dimensions |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat ","
                let label = if dims = "" then m.Id else $"{m.Id}{{{dims}}}"

                match m.Value with
                | MetricValue.Measured v -> lines.Add $"  {label} = {v.ToString(CultureInfo.InvariantCulture)} {m.Unit}"
                | MetricValue.Unknown reason -> lines.Add $"  {label} = unknown ({reason})"

        String.Join(Environment.NewLine, lines)

    /// Ingest an assessment file. The collection time is the supplied
    /// `--collected-at`, else the current time.
    let ingestFile (root: string) (input: string) (collectedAt: DateTimeOffset option) =
        let file = if Path.IsPathRooted input then input else Path.Combine(root, input)

        if not (File.Exists file) then
            Error $"assessment file not found: {input}"
        else
            ingest root (File.ReadAllText file) (collectedAt |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow))
