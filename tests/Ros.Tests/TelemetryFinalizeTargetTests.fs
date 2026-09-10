namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryFinalizeTargetTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-finalize-target-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private executionFile root executionId =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private writeExecution root executionId workItemId status startedAt =
        let directory = Path.Combine(root, ".ros", "telemetry", "executions")
        Directory.CreateDirectory directory |> ignore

        let json =
            $"""{{"schemaVersion":"1.0.0","executionId":"{executionId}","workItemId":"{workItemId}","status":"{status}","startedAt":"{startedAt}","events":[],"metrics":[],"capabilities":[],"repository":{{"start":{{"available":false}}}},"links":{{}}}}"""

        File.WriteAllText(executionFile root executionId, json)

    let private writeContext root (workItems: (string * string) list) =
        let directory = Path.Combine(root, ".ros", "context")
        Directory.CreateDirectory directory |> ignore

        let items =
            workItems
            |> List.map (fun (id, semanticState) -> $"""{{"id":"{id}","semanticState":"{semanticState}"}}""")
            |> String.concat ","

        File.WriteAllText(Path.Combine(directory, "current.json"), $"""{{"schemaVersion":"1.0.0","workItems":[{items}]}}""")

    let private stringField (node: JsonObject) (name: string) : string =
        match node[name] with
        | :? JsonValue as value -> value.GetValue<string>()
        | _ -> failwith $"expected '{name}' to be a string field"

    let tests =
        [ { Name = "finalizeTarget with an EXE-prefixed target resolves by exact execution id"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"

                    match FileTelemetryFinalizationRepository.finalizeTarget root (Some "EXE-1") with
                    | Error message -> failwith message
                    | Ok record ->
                        Assert.equal "EXE-1" (stringField record "executionId")
                        Assert.equal "finalized" (stringField record "status")) }

          { Name = "finalizeTarget with a work-item target matches by workItemId regardless of status, taking the latest startedAt"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-OLD" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-NEW" "WI-A" "active" "2026-01-01T00:05:00.000Z"

                    match FileTelemetryFinalizationRepository.finalizeTarget root (Some "WI-A") with
                    | Error message -> failwith message
                    | Ok record -> Assert.equal "EXE-NEW" (stringField record "executionId")) }

          { Name = "finalizeTarget with no target and exactly one active-or-blocked work item resolves that item's latest execution"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeContext root [ "WI-A", "active" ]

                    match FileTelemetryFinalizationRepository.finalizeTarget root None with
                    | Error message -> failwith message
                    | Ok record -> Assert.equal "EXE-1" (stringField record "executionId")) }

          { Name = "finalizeTarget with no target and zero active-or-blocked work items rejects with production's exact ambiguity message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeContext root [ "WI-A", "ready" ]

                    match FileTelemetryFinalizationRepository.finalizeTarget root None with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry target is ambiguous; provide a work-item or execution ID" message) }

          { Name = "finalizeTarget with no target and multiple active-or-blocked work items rejects with the same ambiguity message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-A" "active" "2026-01-01T00:00:00.000Z"
                    writeExecution root "EXE-2" "WI-B" "active" "2026-01-01T00:00:01.000Z"
                    writeContext root [ "WI-A", "active"; "WI-B", "blocked" ]

                    match FileTelemetryFinalizationRepository.finalizeTarget root None with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry target is ambiguous; provide a work-item or execution ID" message) }

          { Name = "finalizeTarget rejects an unknown target with production's exact message"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    match FileTelemetryFinalizationRepository.finalizeTarget root (Some "WI-GHOST") with
                    | Ok record -> failwith $"expected a rejection but got {record}"
                    | Error message -> Assert.equal "telemetry execution 'WI-GHOST' was not found" message) }

          { Name = "finalizeTarget returns an already-finalized resolved execution untouched"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    writeExecution root "EXE-1" "WI-A" "finalized" "2026-01-01T00:00:00.000Z"
                    let before = File.ReadAllText(executionFile root "EXE-1")

                    match FileTelemetryFinalizationRepository.finalizeTarget root (Some "EXE-1") with
                    | Error message -> failwith message
                    | Ok _ ->
                        let after = File.ReadAllText(executionFile root "EXE-1")
                        Assert.equal before after) } ]
