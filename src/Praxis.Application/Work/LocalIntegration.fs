namespace Praxis.Application.Work

open System
open System.Text
open Praxis.Contracts.Work
open Praxis.Domain.Work

type LocalIntegrationJournalStore =
    { Pending: string -> Result<string list, string>
      Load: string -> string -> Result<LocalIntegrationJournal option, string>
      Prepare: LocalIntegrationPlan -> Result<LocalIntegrationPrepareOutcome, string>
      RecordApplied: LocalIntegrationPlan -> DateTimeOffset -> Result<unit, string> }
type LocalIntegrationPorts =
    { Journal: LocalIntegrationJournalStore; Intake: LocalSubmissionIntakePorts
      Acquire: string -> Result<IDisposable, string>
      ReadTarget: string -> Result<string, string>
      Stage: LocalSubmissionInspection -> string -> string -> Result<string, string>
      ValidateCandidate: LocalSubmissionInspection -> LocalIntegrationPlan -> Result<unit, string>
      CompareAndSwap: LocalIntegrationPlan -> Result<unit, string> }

/// Isolated protocol only. The qualified host owns locks, staging, independent
/// candidate acceptance, policy protection and atomic expected-head updates.
/// Target observations never complete native members or authorize future work.
[<RequireQualifiedAccess>]
module LocalIntegration =
    let private protect operation = try operation() with e -> Error("integration host operation failed: " + e.GetType().Name)
    let private submissionDigest (snapshot: LocalSubmissionInspection) = LocalWorkerSubmissionJson.render snapshot.Submission |> Encoding.UTF8.GetBytes |> LocalWorkerSubmission.digest
    let private inspectReady ports repositoryIdentity dispatchId =
        LocalSubmissionIntake.readChecked ports.Intake repositoryIdentity dispatchId |> Result.bind (fun snapshot ->
            if snapshot.Disposition <> LocalResultDisposition.AwaitingIntegration then Error "partial or failed result cannot integrate"
            else Ok snapshot)
    let private matches (plan: LocalIntegrationPlan) (snapshot: LocalSubmissionInspection) =
        plan.RepositoryIdentity = snapshot.Packet.RepositoryIdentity && plan.DispatchId = snapshot.Packet.DispatchId
        && plan.AttemptId = snapshot.Packet.AttemptId && plan.PacketDigest = LocalAgentHandoff.packetDigest snapshot.Packet
        && plan.SubmissionDigest = submissionDigest snapshot
    let private fresh (ports: LocalIntegrationPorts) (plan: LocalIntegrationPlan) =
        inspectReady ports plan.RepositoryIdentity plan.DispatchId |> Result.bind (fun snapshot ->
            if not (matches plan snapshot) then Error "prepared integration submission changed"
            else ports.ValidateCandidate snapshot plan |> Result.bind (fun () ->
                WorkerChecklistOperations.verifyCandidate { ports.Intake.Checklists with Now = ports.Intake.Now }
                    snapshot.Packet snapshot.Result plan.CandidateCommit |> Result.bind (fun verified ->
                // Candidate checks can take time. Refresh input/authority again
                // afterward, including during recovery of a prepared journal.
                inspectReady ports plan.RepositoryIdentity plan.DispatchId |> Result.bind (fun current ->
                    let now = ports.Intake.Now()
                    if not (matches plan current) || now.Offset <> TimeSpan.Zero || now < plan.PreparedAt
                       || now < current.Packet.IssuedAt || now >= current.Packet.ExpiresAt then Error "integration snapshot or clock changed"
                    else WorkerChecklistOperations.recheckCandidate { ports.Intake.Checklists with Now = ports.Intake.Now }
                             current.Packet current.Result plan.CandidateCommit verified))))
    let private finish ports plan =
        ports.ReadTarget plan.TargetRef |> Result.bind (fun actual ->
            if actual <> plan.CandidateCommit then Error "integration target update remains uncertain or changed"
            else ports.Journal.RecordApplied plan (ports.Intake.Now()) |> Result.map (fun () -> LocalIntegrationDisposition.TargetApplied))
    let private recover ports journal =
        let plan = journal.Plan
        LocalIntegrationJournal.validate journal |> Result.bind (fun () ->
            ports.ReadTarget plan.TargetRef |> Result.bind (fun actual ->
                if actual = plan.CandidateCommit then
                    // Historical observation closes tracking without new mutation
                    // of the target, even if the old delegation has since expired.
                    match journal.AppliedAt with
                    | Some _ -> Ok LocalIntegrationDisposition.TargetAlreadyObserved
                    | None -> finish ports plan
                elif actual <> plan.BeforeCommit || journal.AppliedAt.IsSome then Error "integration target diverged; automatic update refused"
                else fresh ports plan |> Result.bind (fun () ->
                    // The atomic expected-head update covers drift during checks.
                    // Avoid another potentially slow observation after fresh authority.
                    ports.CompareAndSwap plan |> Result.bind (fun () -> finish ports plan))))
    let private prepare ports repositoryIdentity dispatchId targetRef =
        inspectReady ports repositoryIdentity dispatchId |> Result.bind (fun snapshot ->
            ports.ReadTarget targetRef |> Result.bind (fun before ->
                if not (LocalAgentHandoff.isCommit before) then Error "invalid observed destination commit"
                else ports.Stage snapshot targetRef before |> Result.bind (fun candidate ->
                    let plan =
                        { SchemaVersion = "praxis.local-integration-plan/1"; RepositoryIdentity = repositoryIdentity
                          DispatchId = dispatchId; AttemptId = snapshot.Packet.AttemptId
                          PacketDigest = LocalAgentHandoff.packetDigest snapshot.Packet; SubmissionDigest = submissionDigest snapshot
                          TargetRef = targetRef; BeforeCommit = before; CandidateCommit = candidate; PreparedAt = ports.Intake.Now() }
                    let journal = { Plan = plan; AppliedAt = None }
                    LocalIntegrationJournal.validate journal |> Result.bind (fun () ->
                        fresh ports plan |> Result.bind (fun () ->
                            ports.Journal.Prepare plan |> Result.bind (function
                                | LocalIntegrationPrepareOutcome.Existing -> Error "integration appeared during preparation; reconciliation required"
                                | LocalIntegrationPrepareOutcome.Created -> recover ports journal))))))

    let run (ports: LocalIntegrationPorts) repositoryIdentity dispatchId targetRef = protect (fun () ->
        if not (LocalIntegrationJournal.isTargetRef targetRef) then Error "invalid integration target ref"
        else ports.Acquire repositoryIdentity |> Result.bind (fun lease ->
            if isNull lease then invalidOp "missing integration lease"
            use held = lease
            // Pending native state is reconciled even for historical observation.
            ports.Intake.NativeRecovery() |> Result.bind (fun () ->
                ports.Journal.Pending repositoryIdentity |> Result.bind (fun pending ->
                    if pending |> List.exists ((<>) dispatchId) then Error "another integration requires recovery before new mutation"
                    else ports.Journal.Load repositoryIdentity dispatchId |> Result.bind (function
                        | Some journal when journal.Plan.RepositoryIdentity <> repositoryIdentity || journal.Plan.DispatchId <> dispatchId || journal.Plan.TargetRef <> targetRef -> Error "integration journal differs from request"
                        | Some journal -> recover ports journal
                        | None -> prepare ports repositoryIdentity dispatchId targetRef)))))
