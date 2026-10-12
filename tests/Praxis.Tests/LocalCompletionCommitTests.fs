namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalCompletionCommitTests =
    let private t name run = { Name = "local completion commit: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe completion reached native persistence"
    let internal provision root (observed: LocalCompletionNativeObservation) =
        let context = JsonObject()
        context["schemaVersion"] <- JsonValue.Create "1.0.0"
        context["repository"] <- JsonValue.Create observed.Request.Repository
        context["protocolVersion"] <- JsonValue.Create "1.0.0"
        context["actor"] <- JsonValue.Create "fixture-controller"
        context["startedAt"] <- JsonValue.Create observed.Request.Context.StartedAt.Value
        context["baselineDirtyPaths"] <- JsonArray()
        let items = JsonArray()
        for memberItem in observed.Request.Context.WorkItems do
            let item = JsonObject()
            item["id"] <- JsonValue.Create memberItem.Id
            item["type"] <- JsonValue.Create memberItem.WorkType
            item["state"] <- JsonValue.Create "active"
            item["semanticState"] <- JsonValue.Create "active"
            item["evidence"] <- JsonArray()
            let executions = JsonArray()
            memberItem.TelemetryExecutionIds |> List.iter (fun id -> executions.Add(JsonValue.Create id))
            item["telemetryExecutionIds"] <- executions
            items.Add item
        context["workItems"] <- items
        CliHarness.write root ".ros/context/current.json" (context.ToJsonString())
        // Real native persistence with deterministic telemetry observations;
        // this fixture is not a live model-worker or telemetry qualification.
        CliHarness.write root "ros.json" """{"repository":{"id":"repo-42"},"telemetry":{"enabled":false}}"""
        FileNativeCompletionEffects.create root

    let private fixture test = LocalIntegratedCompletionTests.fixture (fun completion _ native evidence head _ clock enabled ->
        let root = CliHarness.temporaryDirectory "praxis-local-completion-commit"
        try
            let effects = provision root native.Value
            test root effects completion native evidence head clock enabled
        finally Directory.Delete(root, true))
    let private commit effects completion =
        let packet = LocalAgentHandoffTests.packet
        FileLocalIntegratedCompletion.commitLockedWith effects completion packet.RepositoryIdentity packet.DispatchId "refs/heads/integration" Actor.unknown (fun _ -> Ok Map.empty) None
    let private events root =
        let path = Path.Combine(root, ".ros/events/events.jsonl")
        if File.Exists path then File.ReadAllLines path else [||]

    let tests =
        [ t "fresh integrated checklist reaches the real native event/context transaction" (fun () -> fixture (fun root effects completion _ _ _ _ _ ->
              let _, ids = commit effects completion |> ok
              Assert.equal 2 ids.Length
              Assert.equal 2 (events root).Length
              for item in (effects.ReadContext() |> ok).WorkItems do Assert.equal LiveWorkState.Complete item.SemanticState))
          t "clock advancement between planning and effect recheck preserves only equivalent plans" (fun () -> fixture (fun _ effects completion _ _ _ clock _ ->
              let quality id evidence =
                  clock.Value <- clock.Value.AddSeconds 1.
                  completion.QualityGate id evidence
              let advancing = { completion with QualityGate = quality }
              commit effects advancing |> ok |> ignore))
          t "revocation, expiry and target drift during finalization refuse native completion" (fun () ->
              for fault in [ "revocation"; "expiry"; "target" ] do fixture (fun root effects completion _ _ head clock enabled ->
                  let finalizer id =
                      match fault with
                      | "revocation" -> enabled.Value <- false
                      | "expiry" -> clock.Value <- LocalAgentHandoffTests.packet.ExpiresAt
                      | _ -> head.Value <- String('f', 40)
                      effects.FinalizeMember id
                  commit { effects with FinalizeMember = finalizer } completion |> rejects
                  Assert.equal 0 (events root).Length
                  for item in (effects.ReadContext() |> ok).WorkItems do Assert.equal LiveWorkState.Active item.SemanticState))
          t "candidate evidence substitution during finalization refuses the recorded native plan" (fun () -> fixture (fun root effects completion _ evidence _ _ _ ->
              let mutable changed = false
              let finalizer id = changed <- true; effects.FinalizeMember id
              let candidateEvidence _ _ =
                  Ok(if changed then evidence |> Map.map (fun _ rows -> rows |> List.map (fun row -> { row with Path = "substituted.json" })) else evidence)
              let completion = { completion with CandidateEvidence = candidateEvidence }
              commit { effects with FinalizeMember = finalizer } completion |> rejects
              Assert.equal 0 (events root).Length))
          t "missing candidate checklist evidence performs no telemetry or native effect" (fun () -> fixture (fun root effects completion _ _ _ _ _ ->
              let baseline = completion.Integration.Intake.Checklists
              let checks = { baseline with Evaluate = fun _ -> Ok [] }
              let integration = { completion.Integration with Intake = { completion.Integration.Intake with Checklists = checks } }
              commit { effects with FinalizeMember = fun _ -> failwith "unchecked plan finalized telemetry" } { completion with Integration = integration } |> rejects
              Assert.equal 0 (events root).Length)) ]
