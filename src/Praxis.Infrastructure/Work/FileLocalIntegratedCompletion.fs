namespace Praxis.Infrastructure.Work

open Praxis.Application.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// Connects the independently checked local plan to the existing native effect
/// transaction. No serialized planning record is accepted as write authority.
[<RequireQualifiedAccess>]
module FileLocalIntegratedCompletion =
    let commitLockedWith nativePorts (ports: LocalIntegratedCompletionPorts) repositoryIdentity dispatchId targetRef
                         (actor: Actor) readinessRecords conclusion =
        LocalIntegratedCompletion.planLocked ports repositoryIdentity dispatchId targetRef |> Result.bind (fun planned ->
            ports.ObserveNative() |> Result.bind (fun observed ->
                let expected = observed.Request.Context
                LocalSubmissionIntake.readChecked ports.Integration.Intake repositoryIdentity dispatchId |> Result.bind (fun snapshot ->
                ports.CandidateEvidence snapshot planned.Integration.Plan |> Result.bind (fun evidence ->
                let boundary () =
                    LocalIntegratedCompletion.recheckLocked ports repositoryIdentity dispatchId targetRef planned
                    |> Result.bind (fun () -> ports.ObserveNative() |> Result.bind (fun current ->
                        if current <> observed then Error "native completion observation changed at effect boundary"
                        else Ok()))
                let beforeApply () =
                    let intake = ports.Integration.Intake
                    let checks = { intake.Checklists with Now = intake.Now }
                    WorkerChecklistOperations.verifyCandidate checks snapshot.Packet snapshot.Result planned.Integration.Plan.CandidateCommit
                    |> Result.bind (fun verified ->
                        ports.CandidateEvidence snapshot planned.Integration.Plan |> Result.bind (fun current ->
                            if current <> evidence then Error "native candidate evidence changed during finalization"
                            else ports.Integration.ReadTarget targetRef |> Result.bind (fun target ->
                                if target <> planned.Integration.Plan.CandidateCommit then Error "completion target changed during finalization"
                                else WorkerChecklistOperations.recheckCandidate checks snapshot.Packet snapshot.Result target verified
                                     |> Result.bind (fun () -> intake.Observe snapshot.Packet snapshot.Result)
                                     |> Result.bind (fun observation -> intake.Authority() |> Result.bind (fun authority ->
                                         intake.Prerequisites() |> Result.bind (fun prerequisites ->
                                             LocalAgentHandoff.validateResult authority (intake.Now()) prerequisites snapshot.Packet observation snapshot.Result
                                             |> Result.mapError (String.concat "; ") |> Result.bind (function
                                                 | LocalResultDisposition.AwaitingIntegration -> Ok()
                                                 | _ -> Error "completion result no longer authorizes integration")))))))
                FileNativeCompletionEffects.commitLockedWith nativePorts
                    { ExpectedContext = expected; Plan = planned.Native; EventActor = actor
                      ReadinessRecords = readinessRecords; ExplicitConclusion = conclusion
                      BeforeEffects = boundary; BeforeApply = beforeApply }))))

    /// Caller holds the real ordered host/native leases for this whole effect.
    let commitLocked root ports repositoryIdentity dispatchId targetRef actor readinessRecords conclusion =
        commitLockedWith (FileNativeCompletionEffects.create root) ports repositoryIdentity dispatchId targetRef actor readinessRecords conclusion
