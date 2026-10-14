namespace Praxis.Domain.Work

open System
open System.Text
open System.Security.Cryptography

[<RequireQualifiedAccess>]
type WorkerCheckKind = Build | Tests | Architecture | Scope | Acceptance
                     /// Host-implemented construction-package scope verification
                     /// (GH-234). Mandatory exactly when the host observes that the
                     /// delegated package is a construction package.
                     | ConstructionScope

/// Host-selected validator identity and the digest of its complete input closure.
type WorkerCheckValidator = { ValidatorId: string; InputDigest: string }
type WorkerChecklistPolicy =
    { Revision: string; Validators: Map<WorkerCheckKind, WorkerCheckValidator> }

/// Independently observed candidate inputs, never supplied by the worker.
/// `ConstructionScope` is Some exactly when the protected host identified the
/// package as a construction package; its input digest pins the host facts.
type WorkerChecklistContext =
    { PacketDigest: string; CandidateCommit: string; CandidateManifestDigest: string
      Policy: WorkerChecklistPolicy; ConstructionScope: WorkerCheckValidator option }
type WorkerChecklistRow =
    { CheckId: string; Kind: WorkerCheckKind; WorkItemId: string; ExecutionId: string
      RequirementKeys: string list; Validator: WorkerCheckValidator }
type WorkerChecklistRequirement =
    { CheckId: string; Kind: WorkerCheckKind; WorkItemId: string; ExecutionId: string
      RequirementKeys: string list; Validator: WorkerCheckValidator option }
type WorkerChecklist =
    { Context: WorkerChecklistContext; Rows: WorkerChecklistRow list }

[<RequireQualifiedAccess>]
type WorkerCheckVerdict = Passed | Failed of string | Skipped of string | Unavailable of string

/// Host observations only. There is deliberately no worker JSON decoder.
type WorkerCheckRun =
    { ChecklistDigest: string; CheckId: string; Validator: WorkerCheckValidator
      StartedAt: DateTimeOffset; FinishedAt: DateTimeOffset
      Verdict: WorkerCheckVerdict; TestsExecuted: int option; EvidenceDigest: string }
type WorkerChecklistRowStatus =
    { Row: WorkerChecklistRow; BlockingReasons: string list }
type WorkerChecklistAssessment =
    { ChecklistDigest: string; AssessedAt: DateTimeOffset
      Rows: WorkerChecklistRowStatus list; BlockingReasons: string list }

[<RequireQualifiedAccess>]
module WorkerChecklist =
    let requiredKinds = [ WorkerCheckKind.Build; WorkerCheckKind.Tests; WorkerCheckKind.Architecture; WorkerCheckKind.Scope ]
    let kindCode = function
        | WorkerCheckKind.Build -> "build"
        | WorkerCheckKind.Tests -> "tests"
        | WorkerCheckKind.Architecture -> "architecture"
        | WorkerCheckKind.Scope -> "scope"
        | WorkerCheckKind.Acceptance -> "acceptance"
        | WorkerCheckKind.ConstructionScope -> "construction-scope"
    /// The only validator identity a construction-scope row accepts. It names
    /// the host's own implementation, never a configurable worker command.
    [<Literal>]
    let ConstructionScopeValidatorId = "praxis.construction-scope/1"
    let private clean (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 1024 && value = value.Trim()
        && not (value |> Seq.exists Char.IsControl)
    let private validValidator value = clean value.ValidatorId && LocalAgentHandoff.isDigest value.InputDigest

    /// Read-only projection; system validators are unobserved until host policy
    /// is loaded. Displaying these requirements never establishes a pass.
    let requirements (packet: LocalWorkerPacket) =
        match LocalAgentHandoff.packetProblems packet with
        | [] ->
            let packet = LocalAgentHandoff.canonicalPacket packet
            [ for memberItem in packet.Members do
                  for kind in requiredKinds do
                      yield { CheckId = "system/" + memberItem.WorkItemId + "/" + kindCode kind
                              Kind = kind; WorkItemId = memberItem.WorkItemId; ExecutionId = memberItem.ExecutionId
                              RequirementKeys = memberItem.RequirementKeys; Validator = None }
                  for obligation in packet.Acceptance |> List.filter (fun row -> row.WorkItemId = memberItem.WorkItemId) do
                      yield { CheckId = "acceptance/" + obligation.ObligationId
                              Kind = WorkerCheckKind.Acceptance; WorkItemId = memberItem.WorkItemId; ExecutionId = memberItem.ExecutionId
                              RequirementKeys = obligation.RequirementKeys
                              Validator = Some { ValidatorId = obligation.ValidatorId; InputDigest = obligation.InputDigest } } ]
            |> List.sortBy _.CheckId |> Ok
        | errors -> Error errors

    /// Rows are generated from the assigned requirements, rather than an agent's
    /// chosen list. Every member owes every system check and every acceptance row.
    let generate (packet: LocalWorkerPacket) (context: WorkerChecklistContext) =
        let errors =
            [ yield! LocalAgentHandoff.packetProblems packet
              if context.PacketDigest <> LocalAgentHandoff.packetDigest packet then yield "checklist-packet-mismatch"
              if not (LocalAgentHandoff.isCommit context.CandidateCommit) then yield "checklist-candidate-commit-invalid"
              if not (LocalAgentHandoff.isDigest context.CandidateManifestDigest) then yield "checklist-candidate-manifest-invalid"
              if not (clean context.Policy.Revision) then yield "checklist-policy-revision-invalid"
              if context.Policy.Validators |> Map.keys |> Set.ofSeq <> Set.ofList requiredKinds then yield "checklist-required-validators-not-exact"
              if context.Policy.Validators |> Map.exists (fun _ validator -> not (validValidator validator)) then yield "checklist-validator-invalid"
              match context.ConstructionScope with
              | Some validator when validator.ValidatorId <> ConstructionScopeValidatorId || not (validValidator validator) ->
                  yield "checklist-construction-scope-validator-invalid"
              | _ -> () ]
        if not errors.IsEmpty then Error errors
        else
            requirements packet |> Result.map (fun rows ->
                // A construction package owes one host scope row per member; it
                // is added to, never substituted for, the system and acceptance rows.
                let construction =
                    match context.ConstructionScope with
                    | None -> []
                    | Some validator ->
                        packet.Members |> List.map (fun memberItem ->
                            { CheckId = "system/" + memberItem.WorkItemId + "/" + kindCode WorkerCheckKind.ConstructionScope
                              Kind = WorkerCheckKind.ConstructionScope; WorkItemId = memberItem.WorkItemId; ExecutionId = memberItem.ExecutionId
                              RequirementKeys = List.sort memberItem.RequirementKeys; Validator = Some validator })
                { Context = context
                  Rows = rows @ construction |> List.sortBy _.CheckId |> List.map (fun row ->
                      { CheckId = row.CheckId; Kind = row.Kind; WorkItemId = row.WorkItemId; ExecutionId = row.ExecutionId
                        RequirementKeys = row.RequirementKeys
                        Validator = row.Validator |> Option.defaultWith (fun () -> context.Policy.Validators[row.Kind]) }) })

    /// Length framing prevents ambiguous identities; packet identity covers
    /// requirements, decisions, scope, member executions and authority revision.
    let digest (checklist: WorkerChecklist) =
        let buffer = StringBuilder()
        let frame (value: string) = buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('|') |> ignore
        let frames values = frame (string (List.length values)); List.iter frame values
        frames [ "praxis.worker-checklist/1"; checklist.Context.PacketDigest; checklist.Context.CandidateCommit
                 checklist.Context.CandidateManifestDigest; checklist.Context.Policy.Revision ]
        // Present only for construction packages, so other identities are unchanged.
        checklist.Context.ConstructionScope |> Option.iter (fun validator ->
            frames [ kindCode WorkerCheckKind.ConstructionScope; validator.ValidatorId; validator.InputDigest ])
        frame (string checklist.Context.Policy.Validators.Count)
        for kind, validator in checklist.Context.Policy.Validators |> Map.toList do
            frames [ kindCode kind; validator.ValidatorId; validator.InputDigest ]
        frame (string checklist.Rows.Length)
        for row in checklist.Rows |> List.sortBy _.CheckId do
            frames [ row.CheckId; kindCode row.Kind; row.WorkItemId; row.ExecutionId; row.Validator.ValidatorId; row.Validator.InputDigest ]
            frames (List.sort row.RequirementKeys)
        "sha256:" + (Encoding.UTF8.GetBytes(buffer.ToString()) |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let assess (packet: LocalWorkerPacket) (now: DateTimeOffset) (checklist: WorkerChecklist) (runs: WorkerCheckRun list) =
        let identity = digest checklist
        let expected = checklist.Rows |> List.map _.CheckId |> Set.ofList
        let problems =
            [ if now.Offset <> TimeSpan.Zero || now < packet.IssuedAt || now >= packet.ExpiresAt then yield "checklist-clock-or-assignment-expired"
              match generate packet checklist.Context with
              | Error problems -> yield! problems
              | Ok generated when generated <> checklist -> yield "checklist-not-generated-from-assignment"
              | Ok _ -> ()
              if runs.Length > 2048 then yield "checklist-observations-exceed-bound"
              if runs |> List.exists (fun run -> not (expected.Contains run.CheckId)) then yield "checklist-unexpected-observation" ]
        let rows = checklist.Rows |> List.map (fun row ->
            let problems =
                match runs |> List.filter (fun run -> run.CheckId = row.CheckId) with
                | [] -> [ "checklist-evidence-missing" ]
                | [ run ] ->
                    [ if run.ChecklistDigest <> identity then yield "checklist-evidence-stale"
                      if run.Validator <> row.Validator then yield "checklist-validator-mismatch"
                      if not (LocalAgentHandoff.isDigest run.EvidenceDigest) then yield "checklist-evidence-digest-invalid"
                      if run.StartedAt.Offset <> TimeSpan.Zero || run.FinishedAt.Offset <> TimeSpan.Zero
                         || run.StartedAt < packet.IssuedAt || run.FinishedAt < run.StartedAt || run.FinishedAt > now then
                          yield "checklist-evidence-time-invalid"
                      if row.Kind = WorkerCheckKind.Tests && (run.TestsExecuted |> Option.forall (fun count -> count <= 0)) then
                          yield "checklist-no-tests-executed"
                      match run.Verdict with
                      | WorkerCheckVerdict.Passed -> ()
                      | WorkerCheckVerdict.Failed reason when row.Kind = WorkerCheckKind.ConstructionScope ->
                          // Host-observed refusal reasons, so the operator sees every one.
                          yield "checklist-check-failed: " + reason
                      | WorkerCheckVerdict.Failed _ -> yield "checklist-check-failed"
                      | WorkerCheckVerdict.Skipped _ -> yield "checklist-check-skipped"
                      | WorkerCheckVerdict.Unavailable _ -> yield "checklist-check-unavailable" ]
                | _ -> [ "checklist-evidence-ambiguous" ]
            { Row = row; BlockingReasons = problems })
        { ChecklistDigest = identity; AssessedAt = now; Rows = rows; BlockingReasons = problems }

    let isReady (assessment: WorkerChecklistAssessment) = assessment.BlockingReasons.IsEmpty && (assessment.Rows |> List.forall (fun row -> row.BlockingReasons.IsEmpty))
    let blockingReasons (assessment: WorkerChecklistAssessment) =
        assessment.BlockingReasons @ (assessment.Rows |> List.collect (fun row -> row.BlockingReasons |> List.map (fun reason -> row.Row.CheckId + ":" + reason)))
