namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work

/// Host configuration only. Select must keep pinned requests stable across
/// repeated observations, including fresh output paths provisioned before use.
type LocalChecklistValidators =
    { PolicyRevision: unit -> Result<string, string>
      Select: LocalWorkerPacket -> WorkerChecklistRequirement -> string -> Result<LocalValidatorRequest, string> }

type LocalChecklistEvidenceSession =
    { Checks: WorkerChecklistPorts
      Acceptance: LocalWorkerPacket -> LocalWorkerResult -> Result<Map<string, LocalAcceptanceObservation>, string> }

/// A scoped controller adapter. Only reports returned by the concrete runner
/// enter its in-memory cache; persisted worker claims cannot populate it.
[<RequireQualifiedAccess>]
module FileWorkerChecklist =
    let private require = function Ok value -> value | Error error -> invalidOp error
    let private awaitResult (operation: System.Threading.Tasks.Task<'a>) = operation.GetAwaiter().GetResult()
    let private normalize path = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    /// Holds an actual evidence-store lease until the entire caller operation
    /// finishes. The caller also owns native/policy and candidate input custody.
    /// Ports retained after this scope closes refuse rather than reuse passes.
    let withEvidenceSession repositoryRoot evidenceRoot (access: LocalHostFileAccess) validators now (cancellation: CancellationToken) operation =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot evidenceRoot
            access.CheckDirectory root |> require
            let lockPath = Path.Combine(root, "checklist.lock")
            LocalHostFileAccess.file lockPath |> require
            access.CheckFile lockPath |> require
            let options = FileStreamOptions(Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None)
            access.ConfigureCreate options
            use lease = new FileStream(lockPath, options)
            access.CheckFile lockPath |> require
            let mutable active = true
            let gate = obj()
            let mutable prepared : (WorkerChecklist * Map<string, LocalValidatorRequest>) option = None
            let mutable cached : Map<string * WorkerCheckValidator, LocalValidatorReport> = Map.empty
            let protectedCall action =
                let guarded () =
                    try
                        if not active then Error "checklist-session-closed"
                        elif cancellation.IsCancellationRequested then Error "checklist-session-cancelled"
                        else
                            access.CheckDirectory root |> require
                            access.CheckFile lockPath |> require
                            action()
                    with error -> Error("concrete checklist refused: " + error.GetType().Name + ": " + error.Message)
                lock gate guarded
            let select packet candidate (row: WorkerChecklistRequirement) =
                let request = validators.Select packet row candidate |> require
                if request.CandidateCommit <> candidate || normalize request.EvidenceRoot <> root then
                    invalidOp "checklist request candidate or evidence custody differs"
                LocalHostJournalFiles.rootOutside repositoryRoot request.Process.WorkingDirectory |> ignore
                if row.Kind = WorkerCheckKind.Tests && request.TrxPath.IsNone then
                    invalidOp "checklist test validator requires a fresh TRX artifact"
                let inputs = FileLocalValidator.observe request cancellation |> awaitResult |> require
                let validator = { ValidatorId = inputs.ValidatorId; InputDigest = inputs.ValidatorInputDigest }
                if row.Validator |> Option.exists ((<>) validator) then invalidOp "signed acceptance validator pin differs"
                row, request, inputs, validator
            let observe packet (_result: LocalWorkerResult) candidate = protectedCall (fun () ->
                prepared <- None
                let revision = validators.PolicyRevision() |> require
                let requirements = WorkerChecklist.requirements packet |> Result.mapError (String.concat "; ") |> require
                if requirements.Length > 2048 then invalidOp "checklist rows exceed the controller bound"
                let selected = requirements |> List.map (select packet candidate)
                let manifests = selected |> List.map (fun (_, _, inputs, _) -> inputs.CandidateManifestDigest) |> Set.ofList
                if manifests.Count <> 1 then invalidOp "checklist validators observed different candidate inputs"
                let systems =
                    WorkerChecklist.requiredKinds |> List.map (fun kind ->
                        let values = selected |> List.filter (fun (row, _, _, _) -> row.Kind = kind)
                                     |> List.map (fun (_, _, _, validator) -> validator) |> Set.ofList
                        if values.Count <> 1 then invalidOp "system validator pins differ between assigned members"
                        kind, Set.minElement values) |> Map.ofList
                let context =
                    { PacketDigest = LocalAgentHandoff.packetDigest packet; CandidateCommit = candidate
                      CandidateManifestDigest = Set.minElement manifests
                      Policy = { Revision = revision; Validators = systems } }
                let checklist = WorkerChecklist.generate packet context |> Result.mapError (String.concat "; ") |> require
                prepared <- Some(checklist, selected |> List.map (fun (row, request, _, _) -> row.CheckId, request) |> Map.ofList)
                Ok context)
            let intact (report: LocalValidatorReport) =
                if Path.GetDirectoryName(normalize report.EvidencePath) <> root || not (LocalHostJournalFiles.noLinks report.EvidencePath) then
                    invalidOp "checklist evidence escaped custody"
                access.CheckFile report.EvidencePath |> require
                use input = new FileStream(report.EvidencePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                if input.Length > 8388608L then invalidOp "oversized checklist evidence"
                let hash = "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()
                if hash <> report.EvidenceDigest then invalidOp "checklist evidence changed after observation"
            let evaluate checklist = protectedCall (fun () ->
                match prepared with
                | Some(observed, requests) when observed = checklist ->
                    if (validators.PolicyRevision() |> require) <> checklist.Context.Policy.Revision then
                        invalidOp "checklist policy changed before evaluation"
                    let identity = WorkerChecklist.digest checklist
                    let runs =
                        checklist.Rows |> List.map (fun row ->
                            let request = requests[row.CheckId]
                            let current = FileLocalValidator.observe request cancellation |> awaitResult |> require
                            if current.CandidateCommit <> checklist.Context.CandidateCommit
                               || current.CandidateManifestDigest <> checklist.Context.CandidateManifestDigest
                               || current.ValidatorId <> row.Validator.ValidatorId
                               || current.ValidatorInputDigest <> row.Validator.InputDigest then
                                invalidOp "checklist inputs changed before evaluation"
                            let key = identity, row.Validator
                            let report =
                                match cached |> Map.tryFind key with
                                | Some report -> report
                                | None ->
                                    if cached.Count >= 2048 then invalidOp "checklist evidence cache exceeds bound"
                                    let report = FileLocalValidator.executeWithFileAccess access request cancellation |> awaitResult |> require
                                    if report.CandidateCommit <> checklist.Context.CandidateCommit
                                       || report.CandidateManifestDigest <> checklist.Context.CandidateManifestDigest
                                       || report.ValidatorId <> row.Validator.ValidatorId
                                       || report.ValidatorInputDigest <> row.Validator.InputDigest then
                                        invalidOp "concrete validator report differs from checklist inputs"
                                    cached <- cached.Add(key, report)
                                    report
                            intact report
                            { ChecklistDigest = identity; CheckId = row.CheckId; Validator = row.Validator
                              StartedAt = report.StartedAt; FinishedAt = report.FinishedAt
                              Verdict = match report.Verdict with Ok() -> WorkerCheckVerdict.Passed | Error error -> WorkerCheckVerdict.Failed error
                              TestsExecuted = report.TestsExecuted; EvidenceDigest = report.EvidenceDigest })
                    Ok runs
                | _ -> Error "checklist was not freshly observed by this controller session")
            let ports : WorkerChecklistPorts = { Observe = observe; Evaluate = evaluate; Now = now }
            let acceptance packet result =
                observe packet result result.OutputCommit |> Result.bind (fun context -> protectedCall (fun () ->
                    let checklist = WorkerChecklist.generate packet context |> Result.mapError (String.concat "; ") |> require
                    let identity = WorkerChecklist.digest checklist
                    packet.Acceptance |> List.map (fun obligation ->
                        let validator = { ValidatorId = obligation.ValidatorId; InputDigest = obligation.InputDigest }
                        let verdict =
                            match cached |> Map.tryFind (identity, validator) with
                            | Some report -> intact report; report.Verdict
                            | None -> Error "acceptance validator has not been independently executed"
                        obligation.ObligationId, { LocalAcceptanceObservation.ValidatorId = validator.ValidatorId
                                                   InputDigest = validator.InputDigest; Verdict = verdict })
                    |> Map.ofList |> Ok))
            try operation { Checks = ports; Acceptance = acceptance }
            finally lock gate (fun () -> active <- false; prepared <- None; cached <- Map.empty)
        with error -> Error("checklist custody scope refused: " + error.GetType().Name + ": " + error.Message)

    let withSession repositoryRoot evidenceRoot access validators now cancellation operation =
        withEvidenceSession repositoryRoot evidenceRoot access validators now cancellation (fun session -> operation session.Checks)
