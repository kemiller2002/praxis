namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryLifecycleTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-lifecycle-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeMetricRegistry root =
        let directory = Path.Combine(root, "telemetry")
        Directory.CreateDirectory directory |> ignore

        File.WriteAllText(
            Path.Combine(directory, "metrics.json"),
            """{"schemaVersion":"1.0.0","metrics":[
                {"id":"agent.interruptions","unit":"count","aggregation":"sum","collection":"runtime-or-ros"},
                {"id":"agent.resumes","unit":"count","aggregation":"sum","collection":"runtime-or-ros"}
            ]}"""
        )

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"2026-01-01T00:00:00.000Z","events":[],"metrics":[],"capabilities":[]}}"""

        File.WriteAllText(executionFile root executionId, json)

    let private readExecution root executionId : JsonObject =
        match JsonNode.Parse(File.ReadAllText(executionFile root executionId)) with
        | :? JsonObject as record -> record
        | _ -> failwith "expected a JSON object"

    let private stringField (node: JsonObject) (name: string) : string =
        match node[name] with
        | :? JsonValue as value -> value.GetValue<string>()
        | _ -> failwith $"expected '{name}' to be a string field"

    let private eventsOf (record: JsonObject) : JsonObject list =
        match record["events"] with
        | :? JsonArray as events -> events |> Seq.choose (function :? JsonObject as node -> Some node | _ -> None) |> Seq.toList
        | _ -> []

    let private metricValue (record: JsonObject) (metricId: string) : int64 option =
        match record["metrics"] with
        | :? JsonArray as metrics ->
            metrics
            |> Seq.tryPick (function
                | :? JsonObject as node when stringField node "id" = metricId ->
                    match node["value"] with
                    | :? JsonValue as value -> Some(value.GetValue<int64>())
                    | _ -> None
                | _ -> None)
        | _ -> None

    let private capabilityStatus (record: JsonObject) (metricId: string) : string option =
        match record["capabilities"] with
        | :? JsonArray as capabilities ->
            capabilities
            |> Seq.tryPick (function
                | :? JsonObject as node when stringField node "metricId" = metricId -> Some(stringField node "status")
                | _ -> None)
        | _ -> None

    let tests =
        [ { Name = "recordLifecycle appends a work.blocked event and its agent.interruptions metric to an active execution"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active"

                    match FileTelemetryFinalizationRepository.recordLifecycle root "WI-A" "blocked" "2026-01-01T00:05:00.000Z" (Some "waiting on review") with
                    | Error message -> failwith message
                    | Ok() ->
                        let record = readExecution root "EXE-1"
                        let events = eventsOf record
                        Assert.equal 1 events.Length
                        Assert.equal "work.blocked" (stringField events[0] "type")
                        Assert.equal "waiting on review" (stringField events[0] "reason")
                        Assert.equal (Some 1L) (metricValue record "agent.interruptions")
                        Assert.equal (Some "derived") (capabilityStatus record "agent.interruptions")) }

          { Name = "recordLifecycle appends a work.resumed event with a null reason and its agent.resumes metric"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active"

                    match FileTelemetryFinalizationRepository.recordLifecycle root "WI-A" "resumed" "2026-01-01T00:10:00.000Z" None with
                    | Error message -> failwith message
                    | Ok() ->
                        let record = readExecution root "EXE-1"
                        let events = eventsOf record
                        Assert.equal 1 events.Length
                        Assert.equal "work.resumed" (stringField events[0] "type")
                        Assert.isTrue (events[0]["reason"] :? JsonValue |> not) "expected a JSON null reason, not a string"
                        Assert.equal (Some 1L) (metricValue record "agent.resumes")) }

          { Name = "recordLifecycle is a no-op when the linked execution is already finalized"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "finalized"

                    match FileTelemetryFinalizationRepository.recordLifecycle root "WI-A" "blocked" "2026-01-01T00:05:00.000Z" (Some "reason") with
                    | Error message -> failwith message
                    | Ok() -> Assert.empty (eventsOf (readExecution root "EXE-1"))) }

          { Name = "recordLifecycle is a no-op when no execution is linked to the work item at all"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root

                    match FileTelemetryFinalizationRepository.recordLifecycle root "WI-GHOST" "blocked" "2026-01-01T00:05:00.000Z" (Some "reason") with
                    | Error message -> failwith message
                    | Ok() -> ()) }

          { Name = "recordLifecycle deduplicates an identical event rather than appending it twice"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeMetricRegistry root
                    writeExecution root "EXE-1" "WI-A" "active"

                    let callTwice () =
                        FileTelemetryFinalizationRepository.recordLifecycle root "WI-A" "blocked" "2026-01-01T00:05:00.000Z" (Some "waiting on review")

                    match callTwice (), callTwice () with
                    | Ok(), Ok() ->
                        let record = readExecution root "EXE-1"
                        Assert.equal 1 (eventsOf record).Length
                        Assert.equal (Some 1L) (metricValue record "agent.interruptions")
                    | Error message, _
                    | _, Error message -> failwith message) } ]
