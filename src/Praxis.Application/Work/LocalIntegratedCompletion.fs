namespace Praxis.Application.Work

open System
open System.Text
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// Native observations belong to the protected host, not the worker response.
type LocalCompletionNativeObservation =
    { Request: WorkContextPlanRequest
      ActiveExecutions: Map<string, string list> }

type LocalIntegratedCompletionPorts =
    { Integration: LocalIntegrationPorts
      ObserveNative: unit -> Result<LocalCompletionNativeObservation, string>
      CandidateEvidence: LocalSubmissionInspection -> LocalIntegrationPlan -> Result<Map<string, WorkEvidence list>, string>
      Evidence: WorkEvidenceRepository
      QualityGate: string -> WorkEvidence list -> Result<unit, string>
      CheckpointGuard: string list -> Result<unit, string> }

/// Ephemeral planning output only. It cannot authorize a later write or replace
/// native telemetry finalization, journal recovery, quality or checkpoint gates.
type LocalIntegratedCompletionPlan =
    { Integration: LocalIntegrationJournal
      Native: WorkContextPlan
      MemberExecutions: Map<string, string> }

[<RequireQualifiedAccess>]
module LocalIntegratedCompletion =
    let private protect action =
        try action() with e -> Error("completion observation failed: " + e.GetType().Name)

    let private matches (plan: LocalIntegrationPlan) (snapshot: LocalSubmissionInspection) =
        let digest = LocalWorkerSubmissionJson.render snapshot.Submission |> Encoding.UTF8.GetBytes |> LocalWorkerSubmission.digest
        plan.RepositoryIdentity = snapshot.Packet.RepositoryIdentity && plan.DispatchId = snapshot.Packet.DispatchId
        && plan.AttemptId = snapshot.Packet.AttemptId && plan.PacketDigest = LocalAgentHandoff.packetDigest snapshot.Packet
        && plan.SubmissionDigest = digest && snapshot.Disposition = LocalResultDisposition.AwaitingIntegration

    let private fresh (ports: LocalIntegratedCompletionPorts) (journal: LocalIntegrationJournal) =
        let plan = journal.Plan
        LocalSubmissionIntake.readChecked ports.Integration.Intake plan.RepositoryIdentity plan.DispatchId
        |> Result.bind (fun snapshot ->
            let now = ports.Integration.Intake.Now()
            if not (matches plan snapshot) || now.Offset <> TimeSpan.Zero
               || journal.AppliedAt |> Option.exists (fun at -> now < at) then Error "completion integration input or clock differs"
            else Ok snapshot)

    let private nativeMatches (packet: LocalWorkerPacket) (observed: LocalCompletionNativeObservation) =
        let ids = packet.Members |> List.map _.WorkItemId
        let contextIds = observed.Request.Context.WorkItems |> List.map _.Id
        observed.Request.Repository = packet.RepositoryIdentity && observed.Request.TelemetryEnabled
        && contextIds.Length = (Set.ofList contextIds).Count
        && packet.Members |> List.forall (fun memberItem ->
            observed.Request.Context.WorkItems |> List.exists (fun item ->
                item.Id = memberItem.WorkItemId && item.SemanticState = LiveWorkState.Active
                && item.TelemetryExecutionIds.Length = (Set.ofList item.TelemetryExecutionIds).Count
                && List.contains memberItem.ExecutionId item.TelemetryExecutionIds)
            && Map.tryFind memberItem.WorkItemId observed.ActiveExecutions = Some [ memberItem.ExecutionId ])
        && ids.Length = (Set.ofList ids).Count

    let private validEvidence ids (evidence: Map<string, WorkEvidence list>) =
        Set.ofList ids = (evidence |> Map.keys |> Set.ofSeq)
        && evidence |> Map.forall (fun _ entries ->
            not entries.IsEmpty && entries.Length <= 256 && entries.Length = (Set.ofList entries).Count
            && entries |> List.forall (fun item ->
                not (String.IsNullOrWhiteSpace item.Type) && item.Type.Length <= 1024
                && item.Type = item.Type.Trim() && not (item.Type |> Seq.exists Char.IsControl)
                && LocalAgentHandoff.isResourcePath item.Path))

    let private nativePlan (ports: LocalIntegratedCompletionPorts) (snapshot: LocalSubmissionInspection)
                           (journal: LocalIntegrationJournal) (observed: LocalCompletionNativeObservation)
                           (evidence: Map<string, WorkEvidence list>) =
        let ids = snapshot.Packet.Members |> List.map _.WorkItemId
        if not (nativeMatches snapshot.Packet observed) then Error "native member state or active execution differs from delegation"
        elif not (validEvidence ids evidence) then Error "candidate evidence does not cover exactly the native members"
        elif snapshot.Result.ChangedPaths |> List.exists (fun path -> List.contains path observed.Request.Context.BaselineDirtyPaths) then
            Error "member attribution overlaps the native dirty baseline"
        else
            let occurredAt = ports.Integration.Intake.Now().ToString("O")
            // Each native planner sees only its member's independently observed
            // evidence and changed paths. A sibling cannot satisfy its obligations.
            snapshot.Result.Members
            |> List.fold (fun acc memberResult ->
                acc |> Result.bind (fun (context, plans) ->
                    let provided = evidence[memberResult.WorkItemId]
                    ports.QualityGate memberResult.WorkItemId provided |> Result.bind (fun () ->
                        let request =
                            { observed.Request with Context = context; Action = WorkAction.Complete
                                                    WorkItemIds = [ memberResult.WorkItemId ]; TargetLocalState = "complete"
                                                    BlockReason = None; ProvidedEvidence = provided
                                                    OccurredAt = occurredAt
                                                    MeaningfulChangedPaths = memberResult.ChangedPaths }
                        match WorkOperations.planVerifiedContext ports.Evidence request with
                        | VerifiedWorkContextPlanOutcome.ContextRejected reason -> Error(sprintf "native completion refused: %A" reason)
                        | VerifiedWorkContextPlanOutcome.EvidenceRejected _ -> Error "native completion evidence unavailable"
                        | VerifiedWorkContextPlanOutcome.Planned plan ->
                            Ok({ context with WorkItems = plan.WorkItems }, plans @ [ plan ]))))
                (Ok(observed.Request.Context, []))
            |> Result.map (fun (_, plans) ->
                let last = List.last plans
                { Integration = journal
                  Native = { last with ItemPlans = plans |> List.collect _.ItemPlans }
                  MemberExecutions = snapshot.Packet.Members |> List.map (fun m -> m.WorkItemId, m.ExecutionId) |> Map.ofList })

    /// No native writes are exposed. A future effect adapter must repeat these
    /// checks inside the existing protected native completion transaction.
    let plan (ports: LocalIntegratedCompletionPorts) repositoryIdentity dispatchId targetRef = protect (fun () ->
        if not (LocalIntegrationJournal.isTargetRef targetRef) then Error "invalid completion target ref"
        else ports.Integration.Acquire repositoryIdentity |> Result.bind (fun lease ->
            if isNull lease then invalidOp "missing completion observation lease"
            use held = lease
            ports.Integration.Intake.NativeRecovery() |> Result.bind (fun () ->
                ports.Integration.Journal.Pending repositoryIdentity |> Result.bind (fun pending ->
                    if not pending.IsEmpty then Error "integration recovery precedes completion planning"
                    else ports.Integration.Journal.Load repositoryIdentity dispatchId |> Result.bind (function
                        | None -> Error "completion requires an applied integration journal"
                        | Some journal ->
                            let integration = journal.Plan
                            LocalIntegrationJournal.validate journal |> Result.bind (fun () ->
                                if integration.RepositoryIdentity <> repositoryIdentity || integration.DispatchId <> dispatchId
                                   || integration.TargetRef <> targetRef || journal.AppliedAt.IsNone then Error "completion integration journal differs or is not applied"
                                else fresh ports journal |> Result.bind (fun snapshot ->
                                    ports.Integration.ValidateCandidate snapshot integration |> Result.bind (fun () ->
                                        WorkerChecklistOperations.verifyCandidate { ports.Integration.Intake.Checklists with Now = ports.Integration.Intake.Now }
                                            snapshot.Packet snapshot.Result integration.CandidateCommit |> Result.bind (fun verified ->
                                        ports.CandidateEvidence snapshot integration |> Result.bind (fun evidence ->
                                            ports.ObserveNative() |> Result.bind (fun observed ->
                                                ports.CheckpointGuard (snapshot.Packet.Members |> List.map _.WorkItemId) |> Result.bind (fun () ->
                                                    nativePlan ports snapshot journal observed evidence |> Result.bind (fun planned ->
                                                        // Every slow observer/gate precedes final state, target
                                                        // and authority reads. Still not a reusable approval.
                                                        ports.ObserveNative() |> Result.bind (fun current ->
                                                            if current <> observed then Error "native completion state changed during observation"
                                                            else ports.Integration.ReadTarget targetRef |> Result.bind (fun actual ->
                                                                if actual <> integration.CandidateCommit then Error "completion target no longer equals candidate"
                                                                else fresh ports journal |> Result.bind (fun _ ->
                                                                    WorkerChecklistOperations.recheckCandidate { ports.Integration.Intake.Checklists with Now = ports.Integration.Intake.Now }
                                                                        snapshot.Packet snapshot.Result integration.CandidateCommit verified |> Result.bind (fun () ->
                                                                            if ports.Integration.Intake.Now() < DateTimeOffset.Parse(planned.Native.UpdatedAt) then Error "completion clock moved backwards"
                                                                            else Ok planned)))))))))))))))))
