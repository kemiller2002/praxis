namespace Praxis.Infrastructure.Work

open System
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work

/// Trusted host bindings, never decoded from worker output. Guard must verify
/// the live assignment and held lifecycle/custody leases. It does not acquire them.
type FileWorkerChecklistBindings =
    { Packet: LocalWorkerPacket
      Result: LocalWorkerResult
      Policy: unit -> Result<WorkerChecklistPolicy, string>
      Request: string -> WorkerChecklistRow -> Result<LocalValidatorRequest, string>
      Guard: unit -> Result<unit, string>
      Cancellation: CancellationToken
      Now: unit -> DateTimeOffset }

[<RequireQualifiedAccess>]
module FileWorkerChecklist =
    let private wait (operation: System.Threading.Tasks.Task<'a>) = operation.GetAwaiter().GetResult()

    /// One-shot concrete evaluation. Identical requests share an observation only
    /// inside this call, with fresh inputs and evidence rechecked for every row.
    /// Repeated gates need fresh host-owned slots
    /// or a separately qualified immutable cache; preexisting TRX paths refuse.
    let create (bindings: FileWorkerChecklistBindings) : WorkerChecklistPorts =
        let guarded operation =
            bindings.Cancellation.ThrowIfCancellationRequested()
            bindings.Guard() |> Result.bind (fun () ->
                operation() |> Result.bind (fun value -> bindings.Guard() |> Result.map (fun () -> value)))
        let request candidate (row: WorkerChecklistRow) =
            guarded (fun () -> bindings.Request candidate row) |> Result.bind (fun request ->
                if request.CandidateCommit <> candidate || request.ValidatorId <> row.Validator.ValidatorId then Error "checklist-request-identity-mismatch"
                elif row.Kind = WorkerCheckKind.Tests && request.TrxPath.IsNone then Error "checklist-test-artifact-required"
                else guarded (fun () -> FileLocalValidator.observe request bindings.Cancellation |> wait)
                     |> Result.bind (fun inputs ->
                         if inputs.ValidatorInputDigest <> row.Validator.InputDigest then Error "checklist-validator-input-pin-mismatch"
                         else Ok(request, inputs)))
        let observe packet result candidate =
            try
                if LocalAgentHandoff.canonicalPacket packet <> LocalAgentHandoff.canonicalPacket bindings.Packet || result <> bindings.Result then
                    Error "checklist-unbound-assignment"
                else guarded bindings.Policy |> Result.bind (fun policy ->
                    if policy.Validators |> Map.keys |> Set.ofSeq <> Set.ofList WorkerChecklist.requiredKinds then Error "checklist-required-validators-not-exact"
                    else WorkerChecklist.requirements packet |> Result.mapError (String.concat "; ") |> Result.bind (fun requirements ->
                        let rec gather remaining manifests =
                            match remaining with
                            | [] ->
                                match List.distinct manifests with
                                | [manifest] ->
                                    let context = { PacketDigest = LocalAgentHandoff.packetDigest packet; CandidateCommit = candidate
                                                    CandidateManifestDigest = manifest; Policy = policy }
                                    WorkerChecklist.generate packet context |> Result.mapError (String.concat "; ") |> Result.map (fun _ -> context)
                                | _ -> Error "checklist-candidate-manifests-differ"
                            | (requirement: WorkerChecklistRequirement) :: rest ->
                                let row: WorkerChecklistRow =
                                    { CheckId = requirement.CheckId; Kind = requirement.Kind; WorkItemId = requirement.WorkItemId
                                      ExecutionId = requirement.ExecutionId; RequirementKeys = requirement.RequirementKeys
                                      Validator = requirement.Validator |> Option.defaultWith (fun () -> policy.Validators[requirement.Kind]) }
                                request candidate row |> Result.bind (fun (_, inputs) -> gather rest (inputs.CandidateManifestDigest :: manifests))
                        gather requirements []))
            with error -> Error("checklist-concrete-observation-refused:" + error.GetType().Name)
        let evaluate (checklist: WorkerChecklist) =
            try
                let candidate = checklist.Context.CandidateCommit
                observe bindings.Packet bindings.Result candidate |> Result.bind (fun context ->
                    WorkerChecklist.generate bindings.Packet context |> Result.mapError (String.concat "; ") |> Result.bind (fun current ->
                        if current <> checklist then Error "checklist-concrete-inputs-changed"
                        else
                            let reports = System.Collections.Generic.Dictionary<LocalValidatorRequest, LocalValidatorReport>()
                            let rec execute remaining runs =
                                match remaining with
                                | [] -> observe bindings.Packet bindings.Result candidate |> Result.bind (fun after ->
                                    if after <> context then Error "checklist-concrete-inputs-changed" else Ok(List.rev runs))
                                | row :: rest ->
                                    request candidate row |> Result.bind (fun (request, inputs) ->
                                        if inputs.CandidateManifestDigest <> context.CandidateManifestDigest then Error "checklist-candidate-manifests-differ"
                                        else guarded (fun () ->
                                                 match reports.TryGetValue request with
                                                 | true, report -> Ok report
                                                 | _ -> FileLocalValidator.execute request bindings.Cancellation |> wait |> Result.map (fun report ->
                                                     reports.Add(request, report)
                                                     report))
                                             |> Result.bind (fun report ->
                                                 if report.CandidateManifestDigest <> inputs.CandidateManifestDigest
                                                    || report.ValidatorInputDigest <> row.Validator.InputDigest then Error "checklist-executed-inputs-differ"
                                                 else guarded (fun () -> FileLocalValidator.verifyEvidence request report) |> Result.bind (fun () ->
                                                     let run = { ChecklistDigest = WorkerChecklist.digest checklist; CheckId = row.CheckId; Validator = row.Validator
                                                                 StartedAt = report.StartedAt; FinishedAt = report.FinishedAt; TestsExecuted = report.TestsExecuted
                                                                 EvidenceDigest = report.EvidenceDigest
                                                                 Verdict = match report.Verdict with Ok() -> WorkerCheckVerdict.Passed | Error reason -> WorkerCheckVerdict.Failed reason }
                                                     execute rest (run :: runs))))
                            execute checklist.Rows []))
            with error -> Error("checklist-concrete-evaluation-refused:" + error.GetType().Name)
        { Observe = observe; Evaluate = evaluate; Now = bindings.Now }
