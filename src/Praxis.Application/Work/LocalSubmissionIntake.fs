namespace Praxis.Application.Work

open System
open System.Text
open Praxis.Contracts.Work
open Praxis.Domain.Work

type LocalWorkerSubmissionStore =
    { Save: LocalWorkerSubmission -> Result<LocalSubmissionSaveOutcome, string>
      Load: string -> string -> Result<LocalWorkerSubmission, string> }
type LocalSubmissionCapturePorts =
    { Journal: LocalDispatchJournalStore; Submissions: LocalWorkerSubmissionStore
      ControllerSessionId: unit -> Result<string, string>; Now: unit -> DateTimeOffset }
type LocalSubmissionIntakePorts =
    { Journal: LocalDispatchJournalStore; Submissions: LocalWorkerSubmissionStore
      NativeRecovery: unit -> Result<unit, string>
      ProcessRecovery: LocalDispatchJournal -> Result<unit, string>
      Authority: unit -> Result<LocalHandoffAuthority, string>
      Prerequisites: unit -> Result<Map<string, string>, string>
      Observe: LocalWorkerPacket -> LocalWorkerResult -> Result<LocalWorkerResultObservation, string>
      Checklists: WorkerChecklistPorts
      Now: unit -> DateTimeOffset }
type LocalSubmissionDispatchReport =
    { Dispatch: LocalJournaledDispatchReport
      Archive: Result<LocalSubmissionSaveOutcome, string> }
/// Checked snapshot for this observation only; never a reusable approval.
type LocalSubmissionInspection =
    { Packet: LocalWorkerPacket; Submission: LocalWorkerSubmission
      Result: LocalWorkerResult; Disposition: LocalResultDisposition }

/// Archive first; interpret only on fresh independent intake. No native work
/// transition, accepted-result marker or integration operation is exposed.
/// Future protected host holds its locks and authenticates all observer ports.
[<RequireQualifiedAccess>]
module LocalSubmissionIntake =
    let private protect action = try action() with e -> Error("submission host operation failed: " + e.GetType().Name)
    let capture (ports: LocalSubmissionCapturePorts) packet (report: LocalJournaledDispatchReport) = protect (fun () ->
        if report.ReconciliationRequired || not report.Problems.IsEmpty then Error "uncertain dispatch cannot archive a confirmed submission"
        else ports.Journal.Load packet.RepositoryIdentity packet.DispatchId |> Result.bind (fun journal ->
            if LocalAgentHandoff.canonicalPacket journal.Reservation.Packet <> LocalAgentHandoff.canonicalPacket packet then Error "submission assignment differs from journal"
            else ports.ControllerSessionId() |> Result.bind (fun session ->
                if session <> journal.Reservation.ControllerSessionId then Error "capture controller incarnation changed"
                else
                    match report.Observation, report.JournalPhase with
                    | Some observation, Some(LocalDispatchPhase.Exited identity) when observation.ProcessStarted = Some true
                        && observation.RootExitObserved && observation.ProcessIdentity = Some identity ->
                        match observation.Outcome with
                        | LocalWorkerProcessOutcome.Exited exitCode ->
                            LocalWorkerSubmission.payloadBytes observation.StandardOutput |> Result.bind (fun bytes ->
                                let total = bytes.Length + UTF8Encoding(false, true).GetByteCount observation.StandardError
                                if total <> observation.CapturedBytes || total > packet.MaxOutputBytes then Error "submission process byte observations differ"
                                else
                                    let submission =
                                        { SchemaVersion = LocalWorkerSubmission.Schema; RepositoryIdentity = packet.RepositoryIdentity
                                          DispatchId = packet.DispatchId; AttemptId = packet.AttemptId; PacketDigest = LocalAgentHandoff.packetDigest packet
                                          ProcessIdentity = identity; CapturedAt = ports.Now(); ExitCode = exitCode
                                          Payload = observation.StandardOutput; PayloadDigest = LocalWorkerSubmission.digest bytes }
                                    LocalWorkerSubmission.validate journal submission |> Result.bind (fun () -> ports.Submissions.Save submission))
                        | _ -> Error "interrupted or invalid worker streams cannot become a complete submission"
                    | _ -> Error "submission requires consistently observed root exit")))

    let dispatchAndCapture dispatchPorts capturePorts packet cancellation = task {
        let! dispatched = LocalJournaledDispatch.run dispatchPorts packet cancellation
        return dispatched |> Result.map (fun report ->
            { Dispatch = report; Archive = capture capturePorts packet report }) }

    let private validateCurrent validation (ports: LocalSubmissionIntakePorts) packet (submission: LocalWorkerSubmission) result observed =
        ports.Authority() |> Result.bind (fun authority ->
            ports.Prerequisites() |> Result.bind (fun prerequisites ->
                let now = ports.Now()
                if now < submission.CapturedAt then Error "submission capture is in the future"
                else validation authority now prerequisites packet observed result
                     |> Result.mapError (String.concat "; ")))

    let private inspectResult (ports: LocalSubmissionIntakePorts) packet submission result =
        let inspection disposition =
            { Packet = packet; Submission = submission; Result = result; Disposition = disposition }
        let checklists = { ports.Checklists with Now = ports.Now }
        ports.Observe packet result |> Result.bind (fun observed ->
            // Reject unassigned or invalid results before invoking validators.
            // Failed/blocked packages remain inspectable but never integrate.
            validateCurrent LocalAgentHandoff.validateResultInputs ports packet submission result observed |> Result.bind (function
                | LocalResultInputDisposition.NoIntegration -> Ok(inspection LocalResultDisposition.NoIntegration)
                | LocalResultInputDisposition.ValidatorsRequired ->
                    WorkerChecklistOperations.verify checklists packet result |> Result.bind (fun verified ->
                        // Slow checks cannot preserve stale paths, policy,
                        // authority or prerequisite observations.
                        ports.Observe packet result |> Result.bind (fun current ->
                            // Acceptance legitimately becomes observed after the
                            // concrete validators run. Every candidate, identity,
                            // path and artifact observation must remain identical.
                            if { current with Acceptance = observed.Acceptance } <> observed then Error "submission observations changed during checklist verification"
                            else WorkerChecklistOperations.recheckCandidate checklists packet result result.OutputCommit verified
                                 |> Result.bind (fun () -> validateCurrent LocalAgentHandoff.validateResult ports packet submission result current |> Result.map inspection)))))

    let readChecked (ports: LocalSubmissionIntakePorts) repositoryIdentity dispatchId = protect (fun () ->
        ports.Journal.Load repositoryIdentity dispatchId |> Result.bind (fun journal ->
            if journal.Reservation.Packet.RepositoryIdentity <> repositoryIdentity || journal.Reservation.Packet.DispatchId <> dispatchId then Error "intake journal differs from requested assignment"
            else ports.Submissions.Load repositoryIdentity dispatchId |> Result.bind (fun submission ->
                LocalWorkerSubmission.validate journal submission |> Result.bind (fun () ->
                    if submission.ExitCode <> 0 then Error "nonzero worker exit cannot enter integration intake"
                    else ports.NativeRecovery() |> Result.bind (fun () ->
                        ports.ProcessRecovery journal |> Result.bind (fun () ->
                            LocalAgentHandoffJson.readResult submission.Payload
                            |> Result.bind (inspectResult ports journal.Reservation.Packet submission)))))))

    let inspect ports repositoryIdentity dispatchId =
        readChecked ports repositoryIdentity dispatchId |> Result.map _.Disposition
