namespace Praxis.Tests

open System
open Praxis.Application.Work
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalIntegratedCompletionTests =
    let private t name run = { Name = "local integrated completion: " + name; Run = run }
    let private packet = LocalAgentHandoffTests.packet
    let private target = "refs/heads/integration"
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private rejects value = Assert.isTrue (Result.isError value) "unsafe completion planned"
    let private run ports = LocalIntegratedCompletion.plan ports packet.RepositoryIdentity packet.DispatchId target
    let internal fixture test = LocalIntegrationTests.fixture (fun _ _ journal integration head swaps clock enabled ->
        LocalIntegration.run integration packet.RepositoryIdentity packet.DispatchId target |> ok |> ignore
        let context =
            { WorkItems = packet.Members |> List.map (fun m ->
                  { Id = m.WorkItemId; WorkType = "task"; LocalState = "active"; SemanticState = LiveWorkState.Active
                    Evidence = []; BlockReason = None; UpdatedAt = None; CompletedAt = None; TelemetryExecutionIds = [ m.ExecutionId ] })
              StartedAt = Some(packet.IssuedAt.ToString("O")); BaselineDirtyPaths = [] }
        let request : WorkContextPlanRequest =
            { Context = context; Action = WorkAction.Begin; WorkItemIds = [ "WI-UNRELATED" ]
              NewItemType = "task"; TargetLocalState = "active"; BlockReason = Some "ignore"
              DefaultRequiredEvidence = Set.singleton "tests"; RequiredEvidenceByType = Map.empty
              ProvidedEvidence = [ { Type = "tests"; Path = "reports/untrusted.json" } ]
              Repository = packet.RepositoryIdentity; ProtocolVersion = "1.0"; Actor = "fixture-controller"
              OccurredAt = packet.IssuedAt.ToString("O"); MeaningfulChangedPaths = [ "wrong.fs" ]; ObservedGitPaths = []
              TelemetryEnabled = true }
        let native = ref { Request = request; ActiveExecutions = packet.Members |> List.map (fun m -> m.WorkItemId, [ m.ExecutionId ]) |> Map.ofList }
        let evidence =
            LocalAgentHandoffTests.result.Members |> List.map (fun m ->
                m.WorkItemId, (m.Evidence |> List.map (fun e -> { Type = "tests"; Path = e.ArtifactPath }))) |> Map.ofList
        let ports : LocalIntegratedCompletionPorts =
            { Integration = integration
              ObserveNative = fun () -> Ok native.Value
              CandidateEvidence = fun _ _ -> Ok evidence
              Evidence = { Observe = fun _ -> EvidencePathObservation.Present }
              QualityGate = fun _ _ -> Ok()
              CheckpointGuard = fun _ -> Ok() }
        test ports journal native evidence head swaps clock enabled)

    let tests =
        [ t "applied integration does not waive current candidate checklist evidence at completion" (fun () -> fixture (fun ports _ native _ head swaps _ _ ->
              let before = native.Value
              let baseline = ports.Integration.Intake.Checklists
              let checklist =
                  { baseline with
                      Evaluate = fun checklist ->
                          if checklist.Context.CandidateCommit = head.Value then Ok [] else baseline.Evaluate checklist }
              let integration = { ports.Integration with Intake = { ports.Integration.Intake with Checklists = checklist } }
              rejects (run { ports with Integration = integration })
              Assert.equal before native.Value
              Assert.equal 1 swaps.Value))
          t "uses existing native planner with exact member evidence, paths and finalization intents but makes no writes" (fun () -> fixture (fun ports _ native evidence _ swaps _ _ ->
              let before = native.Value
              let planned = run ports |> ok
              Assert.equal 2 planned.Native.ItemPlans.Length
              Assert.equal before native.Value
              Assert.equal 1 swaps.Value
              for itemPlan in planned.Native.ItemPlans do
                  Assert.equal LiveWorkState.Complete itemPlan.Item.SemanticState
                  Assert.equal evidence[itemPlan.Item.Id] itemPlan.Event.Evidence
                  Assert.equal [ TelemetryIntent.FinalizeExecutions ] itemPlan.Telemetry
                  let result = LocalAgentHandoffTests.result.Members |> List.find (fun m -> m.WorkItemId = itemPlan.Item.Id)
                  Assert.equal result.ChangedPaths itemPlan.Event.Paths
                  Assert.equal [ result.ExecutionId ] itemPlan.Event.TelemetryExecutionIds))
          t "policy changes during native quality checks invalidate candidate evidence before completion planning returns" (fun () -> fixture (fun ports _ native _ _ _ _ _ ->
              let before = native.Value
              let mutable changed = false
              let baseline = ports.Integration.Intake.Checklists
              let checks =
                  { baseline with
                      Observe = fun p r commit -> baseline.Observe p r commit |> Result.map (fun context ->
                          if changed then { context with Policy = { context.Policy with Revision = "new-policy" } } else context) }
              let integration = { ports.Integration with Intake = { ports.Integration.Intake with Checklists = checks } }
              rejects (run { ports with Integration = integration; QualityGate = fun _ _ -> changed <- true; Ok() })
              Assert.equal before native.Value))
          t "missing, unapplied, crosswired or pending integration never reaches native gates" (fun () ->
              for mode in [ "missing"; "unapplied"; "crosswired"; "pending" ] do fixture (fun ports journal _ _ _ _ _ _ ->
                  let original = journal.Load packet.RepositoryIdentity packet.DispatchId |> ok |> Option.get
                  let changed =
                      match mode with
                      | "missing" -> None
                      | "unapplied" -> Some { original with AppliedAt = None }
                      | "crosswired" -> Some { original with Plan = { original.Plan with DispatchId = "other" } }
                      | _ -> Some original
                  let store = { journal with Load = fun _ _ -> Ok changed
                                             Pending = fun _ -> Ok(if mode = "pending" then [ "other" ] else []) }
                  rejects (run { ports with Integration = { ports.Integration with Journal = store }
                                            ObserveNative = fun () -> failwith "invalid integration reached native observation" })))
          t "applied tracking after expiry or revocation is not completion authority" (fun () ->
              for revoked in [ false; true ] do fixture (fun ports _ _ _ _ _ clock enabled ->
                  if revoked then enabled.Value <- false else clock.Value <- packet.ExpiresAt
                  rejects (run ports)))
          t "candidate failure or changed archived submission refuses completion" (fun () ->
              for changedArchive in [ false; true ] do fixture (fun ports _ _ _ _ _ _ _ ->
                  let changed =
                      if changedArchive then
                          let intake = ports.Integration.Intake
                          let archive = { intake.Submissions with Load = fun repo dispatch -> intake.Submissions.Load repo dispatch |> Result.map (fun s -> { s with CapturedAt = s.CapturedAt.AddSeconds(-1.) }) }
                          { ports with Integration = { ports.Integration with Intake = { intake with Submissions = archive } } }
                      else { ports with Integration = { ports.Integration with ValidateCandidate = fun _ _ -> Error "candidate rejected" } }
                  rejects (run changed)))
          t "native repository, telemetry, state and active executions must match original delegation" (fun () -> fixture (fun ports _ native _ _ _ _ _ ->
              let original = native.Value
              let changes =
                  [ { original with Request = { original.Request with Repository = "other" } }
                    { original with Request = { original.Request with TelemetryEnabled = false } }
                    { original with ActiveExecutions = Map.empty }
                    { original with ActiveExecutions = Map.add "WI-1" [ "EXE-NEW" ] original.ActiveExecutions }
                    { original with ActiveExecutions = Map.add "WI-1" [ "EXE-WI1"; "EXE-NEW" ] original.ActiveExecutions }
                    { original with Request = { original.Request with Context = { original.Request.Context with WorkItems = original.Request.Context.WorkItems |> List.map (fun i -> { i with TelemetryExecutionIds = [] }) } } }
                    { original with Request = { original.Request with Context = { original.Request.Context with WorkItems = original.Request.Context.WorkItems |> List.map (fun i -> { i with TelemetryExecutionIds = i.TelemetryExecutionIds @ i.TelemetryExecutionIds }) } } }
                    { original with Request = { original.Request with Context = { original.Request.Context with WorkItems = [] } } }
                    { original with Request = { original.Request with Context = { original.Request.Context with WorkItems = original.Request.Context.WorkItems @ [ original.Request.Context.WorkItems.Head ] } } } ]
              for state in [ LiveWorkState.Ready; LiveWorkState.Blocked; LiveWorkState.Complete; LiveWorkState.Abandoned ] do
                  native.Value <- { original with Request = { original.Request with Context = { original.Request.Context with WorkItems = original.Request.Context.WorkItems |> List.map (fun i -> { i with SemanticState = state }) } } }
                  rejects (run ports)
              for changed in changes do native.Value <- changed; rejects (run ports)))
          t "candidate evidence must cover every member exactly and cannot be borrowed from siblings" (fun () -> fixture (fun ports _ _ evidence _ _ _ _ ->
              for changed in [ Map.empty; Map.remove "WI-2" evidence; Map.add "WI-OTHER" evidence["WI-1"] evidence
                               Map.add "WI-2" [] evidence; Map.add "WI-2" [ { Type = "implementation"; Path = "reports/two.json" } ] evidence
                               Map.add "WI-1" [ { Type = "tests"; Path = "../escape" } ] evidence
                               Map.add "WI-1" (evidence["WI-1"] @ evidence["WI-1"]) evidence ] do
                  rejects (run { ports with CandidateEvidence = fun _ _ -> Ok changed })))
          t "native evidence paths, quality and accepted checkpoint requirements remain mandatory" (fun () -> fixture (fun ports _ _ _ _ _ _ _ ->
              for unavailable in [ EvidencePathObservation.Missing; EvidencePathObservation.Unavailable "cannot read" ] do
                  rejects (run { ports with Evidence = { Observe = fun _ -> unavailable } })
              rejects (run { ports with QualityGate = fun id _ -> if id = "WI-2" then Error "Ordo refused" else Ok() })
              rejects (run { ports with CheckpointGuard = fun _ -> Error "remote checkpoint unavailable" })))
          t "native work-type requirements and dirty-baseline attribution are preserved" (fun () -> fixture (fun ports _ native _ _ _ _ _ ->
              let original = native.Value
              native.Value <- { original with Request = { original.Request with RequiredEvidenceByType = Map.ofList [ "task", Set.singleton "implementation" ] } }
              rejects (run ports)
              native.Value <- { original with Request = { original.Request with Context = { original.Request.Context with BaselineDirtyPaths = [ "src/A.fs" ] } } }
              rejects (run ports)))
          t "slow candidate and quality gates cannot retain expired or revoked authority" (fun () ->
              for atQuality in [ false; true ] do fixture (fun ports _ _ _ _ _ clock enabled ->
                  let changed =
                      if atQuality then { ports with QualityGate = fun _ _ -> enabled.Value <- false; Ok() }
                      else { ports with CandidateEvidence = fun snapshot plan -> let result = ports.CandidateEvidence snapshot plan in clock.Value <- packet.ExpiresAt; result }
                  rejects (run changed)))
          t "target drift and native takeover during quality checks refuse the whole batch" (fun () ->
              for takeover in [ false; true ] do fixture (fun ports _ native _ head _ _ _ ->
                  let quality _ _ =
                      if takeover then native.Value <- { native.Value with ActiveExecutions = Map.empty }
                      else head.Value <- String('d', 40)
                      Ok()
                  rejects (run { ports with QualityGate = quality })))
          t "native recovery, lease failure and observer exceptions do not yield a plan" (fun () -> fixture (fun ports _ _ _ _ _ _ _ ->
              rejects (run { ports with Integration = { ports.Integration with Intake = { ports.Integration.Intake with NativeRecovery = fun () -> Error "pending native transaction" } } })
              rejects (run { ports with Integration = { ports.Integration with Acquire = fun _ -> Ok null } })
              rejects (run { ports with ObserveNative = fun () -> failwith "fixture observer failure" })))
          t "non-UTC or backwards completion clocks refuse even with an applied journal" (fun () ->
              for badClock in [ packet.IssuedAt.AddMinutes 4.; packet.IssuedAt.AddMinutes(5.).ToOffset(TimeSpan.FromHours 1.) ] do fixture (fun ports _ _ _ _ _ clock _ ->
                  clock.Value <- badClock
                  rejects (run ports))) ]
