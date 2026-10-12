namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

/// All ports belong to the protected controller. Observe includes the current
/// input closure and validator policy. Evaluate runs or retrieves independently
/// authenticated, immutable results; it must not decode worker claims as passes.
type WorkerChecklistPorts =
    { Observe: LocalWorkerPacket -> LocalWorkerResult -> string -> Result<WorkerChecklistContext, string>
      Evaluate: WorkerChecklist -> Result<WorkerCheckRun list, string>
      Now: unit -> DateTimeOffset }

[<RequireQualifiedAccess>]
module WorkerChecklistOperations =
    /// Admission only for this call. No reusable approval token is returned.
    /// The caller must hold its integration/native leases while using the result.
    let verifyCandidate (ports: WorkerChecklistPorts) packet (result: LocalWorkerResult) candidateCommit =
        let observe () = ports.Observe packet result candidateCommit |> Result.bind (fun context ->
            if context.CandidateCommit <> candidateCommit then Error "checklist-candidate-differs-from-request"
            else WorkerChecklist.generate packet context |> Result.mapError (String.concat "; "))
        try
            observe() |> Result.bind (fun checklist ->
                let before = ports.Now()
                if before.Offset <> TimeSpan.Zero || before < packet.IssuedAt || before >= packet.ExpiresAt then Error "checklist-clock-or-assignment-expired"
                else ports.Evaluate checklist |> Result.bind (fun runs ->
                    observe() |> Result.bind (fun current ->
                        let after = ports.Now()
                        if current <> checklist then Error "checklist-inputs-changed-during-verification"
                        elif after < before then Error "checklist-clock-moved-backwards"
                        else
                            let assessment = WorkerChecklist.assess packet after checklist runs
                            if WorkerChecklist.isReady assessment then Ok assessment
                            else Error(WorkerChecklist.blockingReasons assessment |> String.concat "; "))))
        with error -> Error("checklist-host-observation-failed:" + error.GetType().Name)

    let verify ports packet (result: LocalWorkerResult) = verifyCandidate ports packet result result.OutputCommit

    /// Repeat after any subsequent slow observer, still inside the same lease.
    /// An assessment is comparison evidence, never independent write authority.
    let recheckCandidate (ports: WorkerChecklistPorts) packet result candidateCommit (assessment: WorkerChecklistAssessment) =
        try
            ports.Observe packet result candidateCommit |> Result.bind (fun context ->
                if context.CandidateCommit <> candidateCommit then Error "checklist-candidate-differs-from-request"
                else WorkerChecklist.generate packet context |> Result.mapError (String.concat "; ")
                     |> Result.bind (fun current ->
                         let now = ports.Now()
                         if not (WorkerChecklist.isReady assessment) || WorkerChecklist.digest current <> assessment.ChecklistDigest then
                             Error "checklist-inputs-changed-before-effects"
                         elif now.Offset <> TimeSpan.Zero || now < assessment.AssessedAt || now < packet.IssuedAt || now >= packet.ExpiresAt then
                             Error "checklist-clock-or-assignment-expired"
                         else Ok()))
        with error -> Error("checklist-host-observation-failed:" + error.GetType().Name)
