namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module NativeCompletionEffectsTests =
    let private t name run = { Name = "native completion effects: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe native effects succeeded"
    let private fixture test =
        let root = CliHarness.temporaryDirectory "praxis-native-completion-effects"
        try
            CliHarness.write root "ros.json" """{"repository":{"id":"repo"},"telemetry":{"enabled":false}}"""
            let members : LiveWorkItem list =
                [ "WI-ONE", "task"; "WI-TWO", "research" ] |> List.map (fun (id, kind) ->
                    { Id = id; WorkType = kind; LocalState = "active"; SemanticState = LiveWorkState.Active
                      Evidence = []; BlockReason = None; UpdatedAt = Some "2026-10-11T00:00:00.000Z"
                      CompletedAt = None; TelemetryExecutionIds = [] })
            let contextJson =
                """{"schemaVersion":"1.0.0","repository":"repo","protocolVersion":"1.0.0","actor":"fixture","workItems":[{"id":"WI-ONE","type":"task","state":"active","semanticState":"active","evidence":[]},{"id":"WI-TWO","type":"research","state":"active","semanticState":"active","evidence":[]}]}"""
            CliHarness.write root ".ros/context/current.json" contextJson
            CliHarness.write root ".ros/work/queue.json" """{"schemaVersion":"1.0.0","repository":"repo","nextSeq":3,"items":[{"id":"WI-ONE","title":"one","status":"ready","tags":[],"priority":"medium","attachments":[]},{"id":"WI-TWO","title":"two","status":"ready","tags":[],"priority":"medium","attachments":[]}]}"""
            let ports = FileNativeCompletionEffects.create root
            let context = ports.ReadContext() |> ok
            let request : WorkContextPlanRequest =
                { Context = context; Action = WorkAction.Complete; WorkItemIds = members |> List.map _.Id
                  NewItemType = "task"; TargetLocalState = "complete"; BlockReason = None
                  DefaultRequiredEvidence = Set.empty; RequiredEvidenceByType = Map.empty; ProvidedEvidence = []
                  Repository = "repo"; ProtocolVersion = "1.0.0"; Actor = "fixture"; OccurredAt = "2026-10-11T00:01:00.000Z"
                  MeaningfulChangedPaths = []; ObservedGitPaths = []; TelemetryEnabled = false }
            let plan =
                match WorkOperations.planVerifiedContext { Observe = fun _ -> EvidencePathObservation.Present } request with
                | VerifiedWorkContextPlanOutcome.Planned plan -> plan
                | other -> failwith (sprintf "%A" other)
            let effect : NativeCompletionEffectRequest =
                { ExpectedContext = context; Plan = plan; EventActor = Actor.unknown
                  ReadinessRecords = Map.empty; ExplicitConclusion = None; BeforeEffects = fun () -> Ok() }
            test root ports effect
        finally Directory.Delete(root, true)

    let private events root =
        let path = Path.Combine(root, ".ros/events/events.jsonl")
        if File.Exists path then File.ReadAllLines path else [||]
    let private queue root = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros/work/queue.json")))

    let tests =
        [ t "real native context and event transaction completes all members and synchronizes backlog" (fun () -> fixture (fun root ports effect ->
              let items, ids = FileNativeCompletionEffects.commitLockedWith ports effect |> ok
              Assert.equal 2 items.Count
              Assert.equal 2 ids.Length
              Assert.equal 2 (events root).Length
              for item in (ports.ReadContext() |> ok).WorkItems do Assert.equal LiveWorkState.Complete item.SemanticState
              for item in (queue root).["items"].AsArray() do Assert.equal "complete" (item.["status"].GetValue<string>())
              let context = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros/context/current.json")))
              Assert.equal "inconclusive" (context.["workItems"].AsArray().[1].["conclusion"].GetValue<string>())
              Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "native transaction remained pending"))
          t "explicit conclusions and readiness records survive the shared effect tail" (fun () -> fixture (fun root ports effect ->
              let record = JsonObject()
              record["completionReadiness"] <- JsonValue.Create "fixture-readiness"
              let effect = { effect with ExplicitConclusion = Some "supported"; ReadinessRecords = Map.ofList [ "WI-ONE", record ] }
              let items, _ = FileNativeCompletionEffects.commitLockedWith ports effect |> ok
              Assert.equal "supported" (items.[0].["conclusion"].GetValue<string>())
              Assert.equal "fixture-readiness" (items.[0].["completionReadiness"].GetValue<string>())
              Assert.isTrue ((events root).[0].Contains "fixture-readiness") "event lost readiness"))
          t "boundary guard refusal performs no finalization, context, events or backlog effect" (fun () -> fixture (fun root ports effect ->
              let before = File.ReadAllText(Path.Combine(root, ".ros/context/current.json"))
              let unsafePorts = { ports with FinalizeMember = fun _ -> failwith "finalized despite guard refusal"
                                             Apply = fun _ _ -> failwith "wrote despite guard refusal" }
              rejects (FileNativeCompletionEffects.commitLockedWith unsafePorts { effect with BeforeEffects = fun () -> Error "revoked" })
              Assert.equal before (File.ReadAllText(Path.Combine(root, ".ros/context/current.json")))
              Assert.equal 0 (events root).Length))
          t "changed native context is detected before the guard or any effect" (fun () -> fixture (fun root ports effect ->
              let changed = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros/context/current.json")))
              changed.["workItems"].AsArray().[0].["semanticState"] <- JsonValue.Create "blocked"
              CliHarness.write root ".ros/context/current.json" (changed.ToJsonString())
              rejects (FileNativeCompletionEffects.commitLockedWith ports { effect with BeforeEffects = fun () -> failwith "changed context reached guard" })
              Assert.equal 0 (events root).Length))
          t "context changes during a slow boundary guard refuse before finalization" (fun () -> fixture (fun root ports effect ->
              let guard () =
                  let changed = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros/context/current.json")))
                  changed.["workItems"].AsArray().[0].["semanticState"] <- JsonValue.Create "blocked"
                  CliHarness.write root ".ros/context/current.json" (changed.ToJsonString())
                  Ok()
              rejects (FileNativeCompletionEffects.commitLockedWith { ports with FinalizeMember = fun _ -> failwith "finalized changed native state" } { effect with BeforeEffects = guard })
              Assert.equal 0 (events root).Length))
          t "partial, duplicated and mismatched completion plans refuse before effects" (fun () -> fixture (fun root ports effect ->
              let first = effect.Plan.ItemPlans.Head
              let changedPlans =
                  [ { effect.Plan with ItemPlans = [] }
                    { effect.Plan with ItemPlans = [ first; first ] }
                    { effect.Plan with ItemPlans = [ { first with Event = { first.Event with WorkItemId = "other" } } ] }
                    { effect.Plan with WorkItems = [] }
                    { effect.Plan with ItemPlans = [ { first with Event = { first.Event with EventType = "work.started" } } ] }
                    { effect.Plan with ItemPlans = [ { first with Item = { first.Item with SemanticState = LiveWorkState.Active } } ] }
                    { effect.Plan with UpdatedAt = "later" } ]
              for changed in changedPlans do
                  rejects (FileNativeCompletionEffects.commitLockedWith ports { effect with Plan = changed; BeforeEffects = fun () -> failwith "invalid plan reached guard" })
              Assert.equal 0 (events root).Length))
          t "finalization failure leaves native context active and emits no completion" (fun () -> fixture (fun root ports effect ->
              let mutable finalized = []
              let finalize id = finalized <- finalized @ [ id ]; if id = "WI-TWO" then Error "fixture finalization failure" else Ok()
              rejects (FileNativeCompletionEffects.commitLockedWith { ports with FinalizeMember = finalize } effect)
              Assert.equal [ "WI-ONE"; "WI-TWO" ] finalized
              Assert.equal 0 (events root).Length
              Assert.isTrue ((ports.ReadContext() |> ok).WorkItems |> List.forall (fun item -> item.SemanticState = LiveWorkState.Active)) "partial native completion"))
          t "context-write failure retains the existing journal and native recovery applies no duplicate events" (fun () -> fixture (fun root ports effect ->
              let blocked = Path.Combine(root, ".ros/context/current.json")
              let apply conclusions request =
                  File.Delete blocked
                  Directory.CreateDirectory blocked |> ignore
                  ports.Apply conclusions request
              rejects (FileNativeCompletionEffects.commitLockedWith { ports with Apply = apply } effect)
              Assert.isTrue (File.Exists(WorkStateTransaction.transactionPath root)) "failed native write lost recovery journal"
              Directory.Delete blocked
              WorkStateTransaction.recover root |> ok
              Assert.equal 2 (events root).Length
              WorkStateTransaction.recover root |> ok
              FileNativeCompletionEffects.recoverBacklogLockedWith ports |> ok
              Assert.equal 2 (events root).Length
              for item in (queue root).["items"].AsArray() do Assert.equal "complete" (item.["status"].GetValue<string>())))
          t "lost backlog acknowledgement is reconciled from completed native context without replaying completion" (fun () ->
              for persisted in [ false; true ] do fixture (fun root ports effect ->
                  let sync id at items =
                      if persisted then ports.SyncBacklog id at items |> ok
                      Error "backlog acknowledgement lost"
                  rejects (FileNativeCompletionEffects.commitLockedWith { ports with SyncBacklog = sync } effect)
                  let before = events root
                  FileNativeCompletionEffects.recoverBacklogLockedWith ports |> ok
                  FileNativeCompletionEffects.recoverBacklogLockedWith ports |> ok
                  Assert.equal before (events root)
                  for item in (queue root).["items"].AsArray() do Assert.equal "complete" (item.["status"].GetValue<string>())))
          t "backlog recovery never completes active native items or fabricates missing historical timestamps" (fun () -> fixture (fun root ports _ ->
              let never = { ports with SyncBacklog = fun _ _ _ -> failwith "active item synchronized as complete" }
              FileNativeCompletionEffects.recoverBacklogLockedWith never |> ok
              let context = ports.ReadContext() |> ok
              let legacy = { context with WorkItems = context.WorkItems |> List.map (fun item -> { item with SemanticState = LiveWorkState.Complete; CompletedAt = None }) }
              FileNativeCompletionEffects.recoverBacklogLockedWith { never with ReadContext = fun () -> Ok legacy } |> ok
              Assert.equal 0 (events root).Length)) ]
