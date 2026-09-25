namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Telemetry
open Ros.Domain.Work
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module WorkContextEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-work-context-effect-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeContext root (json: string) =
        let contextDirectory = Path.Combine(root, ".ros", "context")
        Directory.CreateDirectory contextDirectory |> ignore
        File.WriteAllText(Path.Combine(contextDirectory, "current.json"), json)

    let private beginItem id telemetryExecutionIds : LiveWorkItem =
        { Id = id
          WorkType = "task"
          LocalState = "active"
          SemanticState = LiveWorkState.Active
          Evidence = []
          BlockReason = None
          UpdatedAt = Some "2026-09-09T18:00:00.000Z"
          CompletedAt = None
          TelemetryExecutionIds = telemetryExecutionIds }

    let private eventFor (item: LiveWorkItem) : WorkEventPlan =
        { EventType = "work.started"
          WorkItemId = item.Id
          Repository = "repo"
          ProtocolVersion = "1.0.0"
          OccurredAt = "2026-09-09T18:00:00.000Z"
          Reason = None
          Evidence = []
          Paths = []
          TelemetryExecutionIds = item.TelemetryExecutionIds }

    let private planFor (item: LiveWorkItem) : WorkContextPlan =
        { WorkItems = [ item ]
          ItemPlans = [ { Item = item; Event = eventFor item; Telemetry = [] } ]
          Repository = "repo"
          ProtocolVersion = "1.0.0"
          Actor = "unknown"
          StartedAt = Some "2026-09-09T18:00:00.000Z"
          BaselineDirtyPaths = []
          UpdatedAt = "2026-09-09T18:00:00.000Z" }

    let tests =
        [ { Name = "applying a begin plan for a brand-new item writes it with telemetryExecutionIds when non-empty"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let plan = planFor (beginItem "WI-NEW" [ "EXE-1" ])

                      match FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent plan with
                      | Error message -> failwith message
                      | Ok(writtenItems, eventIds) ->
                          Assert.equal 1 eventIds.Length
                          Assert.equal 1 writtenItems.Count) }

          { Name = "applying a begin plan preserves an unrelated existing item's unmodeled fields verbatim (a research item's conclusion)"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeContext
                          root
                          """{"schemaVersion":"1.0.0","repository":"repo","workItems":[{"id":"WI-OLD","type":"research","state":"complete","semanticState":"complete","evidence":[],"conclusion":"supported","completedAt":"2026-01-01T00:00:00.000Z"}],"protocolVersion":"1.0.0","actor":"someone","updatedAt":"2026-01-01T00:00:00.000Z"}"""

                      let plan = planFor (beginItem "WI-NEW" [ "EXE-1" ])

                      match FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent plan with
                      | Error message -> failwith message
                      | Ok(writtenItems, _) ->
                          Assert.equal 2 writtenItems.Count
                          let raw = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))
                          Assert.equal true (raw.Contains "\"conclusion\": \"supported\"")
                          Assert.equal true (raw.Contains "WI-OLD")
                          Assert.equal true (raw.Contains "WI-NEW")) }

          { Name = "applying a begin plan with no telemetry execution ids omits the field entirely, matching production's own never-set key"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let plan = planFor (beginItem "WI-NEW" [])

                      match FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent plan with
                      | Error message -> failwith message
                      | Ok _ ->
                          let raw = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))
                          Assert.equal false (raw.Contains "telemetryExecutionIds")) }

          { Name = "re-applying the same event twice does not duplicate the events.jsonl line, matching production's own eventId dedup"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let plan = planFor (beginItem "WI-NEW" [ "EXE-1" ])
                      FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent plan |> ignore
                      FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent plan |> ignore
                      let eventsFile = Path.Combine(root, ".ros", "events", "events.jsonl")

                      let lines =
                          File.ReadAllText(eventsFile).Split('\n') |> Array.filter (fun line -> line.Trim().Length > 0)

                      Assert.equal 1 lines.Length) }

          { Name = "applying a block plan writes blockReason onto the item, matching production's persisted (never-cleared) field"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let beginPlan = planFor (beginItem "WI-BLOCKED" [ "EXE-1" ])
                      FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent beginPlan |> ignore

                      let blockedItem =
                          { (beginItem "WI-BLOCKED" [ "EXE-1" ]) with
                              LocalState = "blocked"
                              SemanticState = LiveWorkState.Blocked
                              BlockReason = Some "waiting on review" }

                      let blockPlan =
                          { (planFor blockedItem) with
                              ItemPlans =
                                [ { Item = blockedItem
                                    Event = { eventFor blockedItem with EventType = "work.blocked"; Reason = Some "waiting on review" }
                                    Telemetry = [] } ] }

                      match FileWorkContextRepository.applyContextPlan root "repo" ProvenanceFixtures.agent blockPlan with
                      | Error message -> failwith message
                      | Ok _ ->
                          let raw = File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))
                          Assert.equal true (raw.Contains "\"blockReason\": \"waiting on review\"")) }

          { Name = "createExecution returns None without writing anything when telemetry is disabled"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"telemetry":{"enabled":false}}""")
                      let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                          { WorkItemId = "WI-NEW"
                            WorkType = "task"
                            Classifications = []
                            ClassificationRationale = None
                            ExecutionId = None
                            IdentityOverrides = IdentityInputs.empty }

                      match FileTelemetryExecutionRepository.createExecution root request with
                      | Error message -> failwith message
                      | Ok result ->
                          Assert.equal None result
                          Assert.equal false (Directory.Exists(Path.Combine(root, ".ros", "telemetry", "executions")))) }

          { Name = "createExecution threads a supplied ParentExecutionId into the created record's identity"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                          { WorkItemId = "WI-NEW"
                            WorkType = "task"
                            Classifications = []
                            ClassificationRationale = None
                            ExecutionId = None
                            IdentityOverrides = { IdentityInputs.empty with ParentExecutionId = Some "EXE-PRIOR" } }

                      match FileTelemetryExecutionRepository.createExecution root request with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected an execution to be created"
                      | Ok(Some executionId) ->
                          let executionFile = Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

                          match JsonNode.Parse(File.ReadAllText executionFile) with
                          | :? JsonObject as record ->
                              match record["identity"] with
                              | :? JsonObject as identity ->
                                  match identity["parentExecutionId"] with
                                  | :? JsonValue as value -> Assert.equal "EXE-PRIOR" (value.GetValue<string>())
                                  | _ -> failwith "expected identity.parentExecutionId to be a string"
                              | _ -> failwith "expected an identity object"
                          | _ -> failwith "expected a JSON object") }

          { Name = "createExecution leaves identity.parentExecutionId absent-valued (null) when none is supplied"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                          { WorkItemId = "WI-NEW"
                            WorkType = "task"
                            Classifications = []
                            ClassificationRationale = None
                            ExecutionId = None
                            IdentityOverrides = IdentityInputs.empty }

                      match FileTelemetryExecutionRepository.createExecution root request with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected an execution to be created"
                      | Ok(Some executionId) ->
                          let executionFile = Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

                          match JsonNode.Parse(File.ReadAllText executionFile) with
                          | :? JsonObject as record ->
                              match record["identity"] with
                              | :? JsonObject as identity -> Assert.equal true (isNull (identity["parentExecutionId"]: JsonNode))
                              | _ -> failwith "expected an identity object"
                          | _ -> failwith "expected a JSON object") }

          { Name = "createExecution uses a supplied ExecutionId verbatim instead of generating a fresh one"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let request: FileTelemetryExecutionRepository.CreateExecutionRequest =
                          { WorkItemId = "WI-NEW"
                            WorkType = "task"
                            Classifications = []
                            ClassificationRationale = None
                            ExecutionId = Some "EXE-CUSTOM-ID"
                            IdentityOverrides = IdentityInputs.empty }

                      match FileTelemetryExecutionRepository.createExecution root request with
                      | Error message -> failwith message
                      | Ok None -> failwith "expected an execution to be created"
                      | Ok(Some executionId) -> Assert.equal "EXE-CUSTOM-ID" executionId) } ]
