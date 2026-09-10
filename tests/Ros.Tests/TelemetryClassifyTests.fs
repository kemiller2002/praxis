namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryClassifyTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-classify-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeMetricRegistry root =
        let directory = Path.Combine(root, "telemetry")
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, "metrics.json"), """{"schemaVersion":"1.0.0","metrics":[]}""")

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status (startedAt: string) =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"{startedAt}",
                "identity":{{"provider":"anthropic","runtime":"claude-code"}},"provenance":{{"collector":"ros","collectorVersion":"1.0.0","discoveredAt":"{startedAt}","sources":[]}},
                "classification":{{"types":["development"],"rationale":null,"evidence":[],"rd":null}},
                "capabilities":[],"metrics":[],"rawTelemetry":[],"events":[],"repository":{{"start":{{"available":false}}}},"scope":{{}},"qualitySignals":[],"links":{{}}}}"""

        File.WriteAllText(executionFile root executionId, json)

    let private readExecution root executionId : JsonObject =
        match JsonNode.Parse(File.ReadAllText(executionFile root executionId)) with
        | :? JsonObject as record -> record
        | _ -> failwith "expected a JSON object"

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private stringArray (node: JsonObject) (name: string) : string list =
        match node[name] with
        | :? JsonArray as array -> array |> Seq.choose (function :? JsonValue as v -> Some(v.GetValue<string>()) | _ -> None) |> Seq.toList
        | _ -> []

    let tests =
        [ { Name = "classifyTarget builds a classification with types/rationale/evidence, no rd, and the CLI-visible shape production prints"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    match
                        FileTelemetryFinalizationRepository.classifyTarget
                            root
                            (Some "EXE-1")
                            [ "research"; "documentation" ]
                            (Some "testing")
                            [ "https://example.com/a" ]
                            None
                    with
                    | Error message -> failwith message
                    | Ok classification ->
                        Assert.equal [ "research"; "documentation" ] (stringArray classification "types")
                        Assert.equal (Some "testing") (stringField classification "rationale")
                        Assert.equal [ "https://example.com/a" ] (stringArray classification "evidence")
                        Assert.equal None (classification["rd"] |> function null -> None | v -> Some(v.ToJsonString()))) }

          { Name = "classifyTarget rejects an empty classification list with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root

                    match FileTelemetryFinalizationRepository.classifyTarget root None [] None [] None with
                    | Ok classification -> failwith $"expected a rejection but got {classification}"
                    | Error message -> Assert.equal "telemetry classify requires at least one --classification" message) }

          { Name = "classifyTarget merges a supplied rd context object verbatim"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    let rd = JsonNode.Parse("""{"decisionId":"DEC-1"}""")

                    match FileTelemetryFinalizationRepository.classifyTarget root (Some "EXE-1") [ "research" ] None [] (Some rd) with
                    | Error message -> failwith message
                    | Ok classification ->
                        match classification["rd"] with
                        | :? JsonObject as r -> Assert.equal (Some "DEC-1") (stringField r "decisionId")
                        | _ -> failwith "expected an rd object") }

          { Name = "classifyTarget does not deduplicate a repeated call, matching production's real-clock (not content-addressed) snapshotId"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"

                    match FileTelemetryFinalizationRepository.classifyTarget root (Some "EXE-1") [ "research" ] None [] None with
                    | Error message -> failwith message
                    | Ok _ ->
                        match FileTelemetryFinalizationRepository.classifyTarget root (Some "EXE-1") [ "documentation" ] None [] None with
                        | Error message -> failwith message
                        | Ok _ ->
                            let record = readExecution root "EXE-1"

                            let ingestionEvents =
                                match record["events"] with
                                | :? JsonArray as events ->
                                    events
                                    |> Seq.filter (function
                                        | :? JsonObject as e -> stringField e "type" = Some "telemetry.snapshot.ingested"
                                        | _ -> false)
                                    |> Seq.length
                                | _ -> 0

                            Assert.equal 2 ingestionEvents

                            match record["classification"] with
                            | :? JsonObject as classification -> Assert.equal [ "documentation" ] (stringArray classification "types")
                            | _ -> failwith "expected a classification object") }

          { Name = "classifyTarget resolves an EXE-prefixed target by exact execution id"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"

                    match FileTelemetryFinalizationRepository.classifyTarget root (Some "EXE-1") [ "research" ] None [] None with
                    | Error message -> failwith message
                    | Ok _ ->
                        let untouched = readExecution root "EXE-2"

                        let ingestionEvents =
                            match untouched["events"] with
                            | :? JsonArray as events -> events.Count
                            | _ -> -1

                        Assert.equal 0 ingestionEvents) } ]
