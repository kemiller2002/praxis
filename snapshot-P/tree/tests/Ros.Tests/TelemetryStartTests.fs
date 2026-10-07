namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Telemetry
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module TelemetryStartTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-telemetry-start-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeMetricRegistry root =
        let directory = Path.Combine(root, "telemetry")
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, "metrics.json"), """{"schemaVersion":"1.0.0","metrics":[]}""")

    let private writeContext root (json: string) =
        let contextDirectory = Path.Combine(root, ".ros", "context")
        Directory.CreateDirectory contextDirectory |> ignore
        File.WriteAllText(Path.Combine(contextDirectory, "current.json"), json)

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

    let private readContext root : JsonObject =
        match JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))) with
        | :? JsonObject as context -> context
        | _ -> failwith "expected a JSON object"

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private stringArrayField (node: JsonObject) (name: string) : string list =
        match node[name] with
        | :? JsonArray as array ->
            array
            |> Seq.choose (function
                | :? JsonValue as v -> Some(v.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private itemWith root id state : unit =
        writeContext
            root
            $$"""{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"repo","actor":"actor","updatedAt":"2026-01-01T00:00:00.000Z","workItems":[{"id":"{{id}}","type":"task","state":"{{state}}","semanticState":"{{state}}","evidence":[]}]}"""

    let tests =
        [ { Name = "startTarget creates a fresh execution and links it when the work item is active with no candidates"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-A" "active"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-A" [ "research" ] (Some "manual attach") None IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a created execution"
                      | Ok(Some record) ->
                          Assert.equal (Some "WI-A") (stringField record "workItemId")
                          Assert.equal (Some "active") (stringField record "status")

                          match record["classification"] with
                          | :? JsonObject as classification ->
                              Assert.equal [ "research" ] (stringArrayField classification "types")
                              Assert.equal (Some "manual attach") (stringField classification "rationale")
                          | _ -> failwith "expected a classification object"

                          let context = readContext root
                          let item = (context["workItems"] :?> JsonArray).[0] :?> JsonObject
                          let executionId = stringField record "executionId" |> Option.get
                          Assert.equal [ executionId ] (stringArrayField item "telemetryExecutionIds")) }

          { Name = "startTarget recovers a detached (unlinked) execution instead of creating a new one"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-B" "active"
                      writeExecution root "EXE-DETACHED" "WI-B" "active" "2020-01-01T00:00:00.000Z"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-B" [] None None IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a recovered execution"
                      | Ok(Some record) ->
                          Assert.equal (Some "EXE-DETACHED") (stringField record "executionId")
                          let context = readContext root
                          let item = (context["workItems"] :?> JsonArray).[0] :?> JsonObject
                          Assert.equal [ "EXE-DETACHED" ] (stringArrayField item "telemetryExecutionIds")) }

          { Name = "startTarget creates a new execution when the only candidate is already linked"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root

                      writeContext
                          root
                          """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"repo","actor":"actor","updatedAt":"2026-01-01T00:00:00.000Z","workItems":[{"id":"WI-C","type":"task","state":"active","semanticState":"active","evidence":[],"telemetryExecutionIds":["EXE-LINKED"]}]}"""

                      writeExecution root "EXE-LINKED" "WI-C" "active" "2020-01-01T00:00:00.000Z"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-C" [] None None IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a created execution"
                      | Ok(Some record) ->
                          let newExecutionId = stringField record "executionId" |> Option.get
                          Assert.equal false (newExecutionId = "EXE-LINKED")
                          let context = readContext root
                          let item = (context["workItems"] :?> JsonArray).[0] :?> JsonObject
                          Assert.equal [ "EXE-LINKED"; newExecutionId ] (stringArrayField item "telemetryExecutionIds")) }

          { Name = "startTarget rejects ambiguous detached candidates with production's exact rerun message"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-D" "active"
                      writeExecution root "EXE-DETACHED-1" "WI-D" "active" "2020-01-01T00:00:00.000Z"
                      writeExecution root "EXE-DETACHED-2" "WI-D" "active" "2020-01-01T00:00:01.000Z"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-D" [] None None IdentityInputs.empty with
                      | Ok _ -> failwith "expected a rejection"
                      | Error message ->
                          Assert.equal
                              "multiple detached telemetry executions require explicit selection for 'WI-D'; rerun with --execution-id one of: EXE-DETACHED-1, EXE-DETACHED-2"
                              message) }

          { Name = "startTarget rejects a work item that is not active or blocked with production's exact message"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-E" "ready"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-E" [] None None IdentityInputs.empty with
                      | Ok _ -> failwith "expected a rejection"
                      | Error message ->
                          Assert.equal "work item 'WI-E' must be active or blocked before starting telemetry" message) }

          { Name = "startTarget rejects a work item that does not exist in context, with the same message as an inactive item"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-A" "active"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-NOPE" [] None None IdentityInputs.empty with
                      | Ok _ -> failwith "expected a rejection"
                      | Error message ->
                          Assert.equal "work item 'WI-NOPE' must be active or blocked before starting telemetry" message) }

          { Name = "startTarget returns None without mutating context when telemetry is disabled and there is no candidate"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-F" "active"
                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"telemetry":{"enabled":false}}""")
                      let before = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))

                      match FileTelemetryFinalizationRepository.startTarget root "WI-F" [] None None IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok(Some _) -> failwith "expected no execution to be created"
                      | Ok None ->
                          let after = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))
                          Assert.equal before after) }

          { Name = "startTarget with --execution-id recovers a matching detached candidate instead of creating a new one"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-G" "active"
                      writeExecution root "EXE-DETACHED-1" "WI-G" "active" "2020-01-01T00:00:00.000Z"
                      writeExecution root "EXE-DETACHED-2" "WI-G" "active" "2020-01-01T00:00:01.000Z"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-G" [] None (Some "EXE-DETACHED-2") IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a recovered execution"
                      | Ok(Some record) -> Assert.equal (Some "EXE-DETACHED-2") (stringField record "executionId")) }

          { Name = "startTarget with --execution-id rejects a non-matching id when other detached candidates exist, with production's exact rerun message"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-H" "active"
                      writeExecution root "EXE-DETACHED-1" "WI-H" "active" "2020-01-01T00:00:00.000Z"
                      writeExecution root "EXE-DETACHED-2" "WI-H" "active" "2020-01-01T00:00:01.000Z"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-H" [] None (Some "EXE-NOPE") IdentityInputs.empty with
                      | Ok record -> failwith $"expected a rejection but got {record}"
                      | Error message ->
                          Assert.equal
                              "detached telemetry execution must be linked before creating 'EXE-NOPE' for 'WI-H'; rerun with --execution-id EXE-DETACHED-1"
                              message) }

          { Name = "startTarget with --execution-id becomes the newly created execution's own id when no detached candidate exists"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-I" "active"

                      match FileTelemetryFinalizationRepository.startTarget root "WI-I" [] None (Some "EXE-CUSTOM-ID") IdentityInputs.empty with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a created execution"
                      | Ok(Some record) -> Assert.equal (Some "EXE-CUSTOM-ID") (stringField record "executionId")) }

          { Name = "startTarget threads all 11 identity-override fields into the created record's identity"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeMetricRegistry root
                      itemWith root "WI-J" "active"

                      let identityOverrides: IdentityInputs =
                          { IdentityInputs.empty with
                              Provider = Some "acme"
                              Model = Some "acme-model"
                              ModelVersion = Some "v2"
                              Runtime = Some "acme-cli"
                              RuntimeVersion = Some "9.9.9"
                              SessionId = Some "sess-123"
                              ConversationId = Some "conv-456"
                              RunId = Some "run-789"
                              AgentId = Some "agent-1"
                              SubagentId = Some "subagent-2"
                              ParentExecutionId = Some "EXE-PARENT" }

                      match FileTelemetryFinalizationRepository.startTarget root "WI-J" [] None None identityOverrides with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected a created execution"
                      | Ok(Some record) ->
                          match record["identity"] with
                          | :? JsonObject as identity ->
                              Assert.equal (Some "acme") (stringField identity "provider")
                              Assert.equal (Some "acme-model") (stringField identity "model")
                              Assert.equal (Some "v2") (stringField identity "modelVersion")
                              Assert.equal (Some "acme-cli") (stringField identity "runtime")
                              Assert.equal (Some "9.9.9") (stringField identity "runtimeVersion")
                              Assert.equal (Some "sess-123") (stringField identity "sessionId")
                              Assert.equal (Some "conv-456") (stringField identity "conversationId")
                              Assert.equal (Some "run-789") (stringField identity "runId")
                              Assert.equal (Some "agent-1") (stringField identity "agentId")
                              Assert.equal (Some "subagent-2") (stringField identity "subagentId")
                              Assert.equal (Some "EXE-PARENT") (stringField identity "parentExecutionId")
                          | _ -> failwith "expected an identity object") } ]
