namespace Praxis.Domain.Execution

open System
open Microsoft.FSharp.Reflection

// Praxis's host projection of Ordo's execution-governance contract
// (`ordo.execution/1`, Ordo `method/EXECUTION-GOVERNANCE-REQUIREMENTS.md`,
// ORD-EXEC-*). Ordo owns the meaning of roles, capabilities, evaluator
// identity and mutation boundaries, and Praxis consumes it from the released
// Ordo.Core package (EchelonFoundry.Ordo.Core, pinned in
// vendor/nuget/ordo-core.lock; ORDO-CORE-PACKAGE, PRAXIS-FND-01,
// PRX-ARCH-001). The host types here keep Praxis's persisted wire shapes;
// every rule that decides authority, a fingerprint or a boundary
// classification is Ordo.Core's, reached through `OrdoSemantics` by wire
// name. Nothing here may invent conflicting semantics (ORD-EXEC-141);
// tests/Praxis.Tests/ExecutionGovernanceTests.fs asserts the vocabularies
// stay one-to-one with Ordo's.

/// The semantic role an execution performs. Provider identity is never a
/// role (ORD-EXEC-075).
[<RequireQualifiedAccess>]
type ExecutionRole =
    | Specification
    | Implementation
    | Verification
    | Review
    | Integration
    | Administration

[<RequireQualifiedAccess>]
module ExecutionRole =
    let all =
        [ ExecutionRole.Specification
          ExecutionRole.Implementation
          ExecutionRole.Verification
          ExecutionRole.Review
          ExecutionRole.Integration
          ExecutionRole.Administration ]

    let toWire role =
        match role with
        | ExecutionRole.Specification -> "specification"
        | ExecutionRole.Implementation -> "implementation"
        | ExecutionRole.Verification -> "verification"
        | ExecutionRole.Review -> "review"
        | ExecutionRole.Integration -> "integration"
        | ExecutionRole.Administration -> "administration"

    let tryParse (raw: string) = all |> List.tryFind (fun r -> toWire r = raw.Trim().ToLowerInvariant())

/// The closed execution-capability vocabulary of `ordo.execution/1`.
[<RequireQualifiedAccess>]
type Capability =
    | ElaborateAuthorizedScope
    | CreateGoverningPromise
    | ApproveSpecification
    | ModifyImplementation
    | ModifyImplementationTests
    | ModifyAcceptanceCriteria
    | ModifyEvaluationAuthority
    | InvokeEvaluator
    | ObserveOutcome
    | RecordEvidence
    | RecordVerdict
    | InspectEvidence
    | RecordFindings
    | RequestRework
    | AcceptReview
    | RejectReview
    | CombineAuthorizedCandidates
    | ResolveIntegrationConflict
    | ExpandMutationBoundary
    | RegisterInstallation
    | RemoveInstallation
    | AdministerExecutionPolicy

[<RequireQualifiedAccess>]
module Capability =
    let toWire capability =
        match capability with
        | Capability.ElaborateAuthorizedScope -> "specification.elaborate"
        | Capability.CreateGoverningPromise -> "specification.create-promise"
        | Capability.ApproveSpecification -> "specification.approve"
        | Capability.ModifyImplementation -> "implementation.modify"
        | Capability.ModifyImplementationTests -> "implementation.modify-tests"
        | Capability.ModifyAcceptanceCriteria -> "acceptance.modify"
        | Capability.ModifyEvaluationAuthority -> "evaluator.modify"
        | Capability.InvokeEvaluator -> "evaluator.invoke"
        | Capability.ObserveOutcome -> "verification.observe"
        | Capability.RecordEvidence -> "evidence.record"
        | Capability.RecordVerdict -> "verification.verdict"
        | Capability.InspectEvidence -> "review.inspect"
        | Capability.RecordFindings -> "review.findings"
        | Capability.RequestRework -> "review.request-rework"
        | Capability.AcceptReview -> "review.accept"
        | Capability.RejectReview -> "review.reject"
        | Capability.CombineAuthorizedCandidates -> "integration.combine"
        | Capability.ResolveIntegrationConflict -> "integration.resolve-conflict"
        | Capability.ExpandMutationBoundary -> "scope.expand"
        | Capability.RegisterInstallation -> "installation.register"
        | Capability.RemoveInstallation -> "installation.remove"
        | Capability.AdministerExecutionPolicy -> "policy.administer"

    let all =
        FSharpType.GetUnionCases typeof<Capability>
        |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> Capability)
        |> List.ofArray

    let ofWire (raw: string) = all |> List.tryFind (fun c -> toWire c = raw)

/// The translation between Praxis's host types and Ordo.Core's by wire name
/// (ORDO-CORE-PACKAGE). A name Ordo does not know is a defect in the pin, not
/// an operational condition, so it fails loudly.
[<RequireQualifiedAccess>]
module OrdoSemantics =
    let private defect what (name: string) : 'a =
        invalidOp $"the pinned Ordo.Core and Praxis disagree on {what} '{name}'"

    let role (role: ExecutionRole) =
        let name = ExecutionRole.toWire role

        match Ordo.Core.ExecutionRole.ExecutionRole.fromWire name with
        | Some r -> r
        | None -> defect "the execution role" name

    let capability (capability: Ordo.Core.ExecutionRole.ExecutionCapability) =
        let name = Ordo.Core.ExecutionRole.ExecutionCapability.toWire capability

        match Capability.ofWire name with
        | Some c -> c
        | None -> defect "the capability" name

    let ordoCapabilities () =
        Ordo.Core.ExecutionRole.ExecutionCapability.all |> List.map Ordo.Core.ExecutionRole.ExecutionCapability.toWire

    let evaluatorKinds () =
        FSharpType.GetUnionCases typeof<Ordo.Core.Evaluator.EvaluatorInputKind>
        |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> Ordo.Core.Evaluator.EvaluatorInputKind |> Ordo.Core.Evaluator.EvaluatorInputKind.toWire)
        |> List.ofArray

/// A role's authority: Ordo's grants and prohibitions for that role.
type RoleAuthority =
    { Role: ExecutionRole
      Grants: Set<Capability>
      Prohibits: Set<Capability> }

[<RequireQualifiedAccess>]
module RoleAuthority =
    /// Ordo.Core's default authority matrix (ORD-EXEC-081..085), never a
    /// Praxis copy of it (PRX-EXEC-002).
    let defaultFor role =
        let ordo = Ordo.Core.ExecutionRole.RoleAuthority.defaultFor (OrdoSemantics.role role)

        { Role = role
          Grants = ordo.Grants |> Set.map OrdoSemantics.capability
          Prohibits = ordo.Prohibits |> Set.map OrdoSemantics.capability }

    let effective (authority: RoleAuthority) = Set.difference authority.Grants authority.Prohibits
    let allows capability authority = effective authority |> Set.contains capability

/// One member of an evaluator closure, with its host-observed digest.
type EvaluatorInput =
    { Kind: string
      Reference: string
      Digest: string }

/// The effective evaluator's content-derived identity, computed by Ordo.Core
/// (`EvaluatorIdentity.create`, ORD-EXEC-092).
type EvaluatorIdentity = { Inputs: EvaluatorInput list; Fingerprint: string }

[<RequireQualifiedAccess>]
module EvaluatorIdentity =
    let kinds = OrdoSemantics.evaluatorKinds ()

    let create (inputs: EvaluatorInput list) : Result<EvaluatorIdentity, string> =
        let toOrdo (i: EvaluatorInput) =
            Ordo.Core.Evaluator.EvaluatorInputKind.fromWire i.Kind
            |> Option.map (fun k ->
                let input: Ordo.Core.Evaluator.EvaluatorInput = { Kind = k; Reference = i.Reference; Digest = i.Digest }
                input)

        let converted = inputs |> List.choose toOrdo

        if not inputs.IsEmpty && inputs |> List.exists (fun i -> i.Kind = "gate-code") && converted.Length <> inputs.Length then
            Error("unknown evaluator input kind; expected one of " + String.concat ", " kinds)
        else
            // Without gate code Ordo refuses whatever else is wrong, so an
            // unknown kind cannot mask the missing gate.
            Ordo.Core.Evaluator.EvaluatorIdentity.create converted
            |> Result.mapError (fun error ->
                match error with
                | Ordo.Core.Evaluator.NoEvaluatorInputs when not inputs.IsEmpty -> "an evaluator closure needs gate code"
                | Ordo.Core.Evaluator.NoEvaluatorInputs -> "an evaluator closure needs at least one input"
                | Ordo.Core.Evaluator.NoGateCode -> "an evaluator closure needs gate code"
                | Ordo.Core.Evaluator.InputWithoutDigest _ -> "every evaluator input needs a digest"
                | Ordo.Core.Evaluator.DuplicateInputReference _ -> "evaluator input references must be unique")
            |> Result.map (fun identity ->
                { Inputs =
                    identity.Inputs
                    |> List.map (fun i ->
                        { Kind = Ordo.Core.Evaluator.EvaluatorInputKind.toWire i.Kind
                          Reference = i.Reference
                          Digest = i.Digest })
                  Fingerprint = identity.Fingerprint })

    /// References whose digest differs, appeared or disappeared.
    let changes (baseline: EvaluatorIdentity) (current: EvaluatorIdentity) =
        let index xs = xs |> List.map (fun (i: EvaluatorInput) -> i.Reference, i) |> Map.ofList
        let before, after = index baseline.Inputs, index current.Inputs

        Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
        |> Set.toList
        |> List.filter (fun r -> Map.tryFind r before <> Map.tryFind r after)

/// What an evaluation may honestly claim (ORD-EXEC-094/095/050).
[<RequireQualifiedAccess>]
type EvaluationOutcome =
    | Passed of evaluator: string
    | Failed of evaluator: string * reason: string
    | EvaluatorChanged of baseline: string * current: string * changed: string list
    | EvaluatorUnavailable of reason: string

[<RequireQualifiedAccess>]
module EvaluationOutcome =
    let judge (baseline: EvaluatorIdentity) (atVerdict: Result<EvaluatorIdentity, string>) (passed: bool) (reason: string) =
        match atVerdict with
        | Error why -> EvaluationOutcome.EvaluatorUnavailable why
        | Ok current when current.Fingerprint <> baseline.Fingerprint ->
            EvaluationOutcome.EvaluatorChanged(baseline.Fingerprint, current.Fingerprint, EvaluatorIdentity.changes baseline current)
        | Ok current when passed -> EvaluationOutcome.Passed current.Fingerprint
        | Ok current -> EvaluationOutcome.Failed(current.Fingerprint, reason)

    let isCurrent (current: EvaluatorIdentity) outcome =
        match outcome with
        | EvaluationOutcome.Passed fp
        | EvaluationOutcome.Failed(fp, _) -> fp = current.Fingerprint
        | _ -> false

    let toWire outcome =
        match outcome with
        | EvaluationOutcome.Passed _ -> "passed"
        | EvaluationOutcome.Failed _ -> "failed"
        | EvaluationOutcome.EvaluatorChanged _ -> "evaluator-changed"
        | EvaluationOutcome.EvaluatorUnavailable _ -> "evaluator-unavailable"

/// Typed expected receipts (ORD-EXEC-100/101).
[<RequireQualifiedAccess>]
type ExpectedReceipt =
    | ArtifactExists of reference: string
    | ArtifactIdentity of reference: string * digest: string
    | ArtifactAbsent of reference: string
    | CommandSucceeded of command: string
    | StateEquals of key: string * value: string
    | ConformsToContract of reference: string * contract: string
    | VerificationSatisfied of evaluator: string
    | TransitionObserved of transition: string
    | AllOf of ExpectedReceipt list

/// Observed facts; a narrative is never one of them (ORD-EXEC-107).
[<RequireQualifiedAccess>]
type ObservedFact =
    | ArtifactObserved of reference: string * digest: string option
    | ArtifactNotFound of reference: string
    | CommandExited of command: string * exitCode: int
    | CommandOutcomeUnknown of command: string * reason: string
    | StateObserved of key: string * value: string
    | ContractChecked of reference: string * contract: string * conforms: bool
    | VerificationObserved of evaluator: string * passed: bool
    | TransitionRecorded of transition: string
    | Unobservable of subject: string * reason: string

[<RequireQualifiedAccess>]
type ObservationSource =
    | Host of string
    | Independent of string
    | SelfReported of string

type ObservedReceipt =
    { Source: ObservationSource
      Facts: ObservedFact list
      Narrative: string option }

[<RequireQualifiedAccess>]
type ReceiptOutcome =
    | Match
    | Mismatch
    | Indeterminate

[<RequireQualifiedAccess>]
module ReceiptOutcome =
    let toWire outcome =
        match outcome with
        | ReceiptOutcome.Match -> "match"
        | ReceiptOutcome.Mismatch -> "mismatch"
        | ReceiptOutcome.Indeterminate -> "indeterminate"

/// A comparison result that carries its reason and justifying facts, and
/// keeps every constituent of a composite (ORD-EXEC-105/106).
type ReceiptResult =
    { Expected: ExpectedReceipt
      Outcome: ReceiptOutcome
      Reason: string option
      Evidence: ObservedFact list
      Constituents: ReceiptResult list }

[<RequireQualifiedAccess>]
module Receipt =
    let private leaf expected outcome reason evidence =
        { Expected = expected
          Outcome = outcome
          Reason = reason
          Evidence = evidence
          Constituents = [] }

    let private artifactFacts reference facts =
        facts
        |> List.filter (fun f ->
            match f with
            | ObservedFact.ArtifactObserved(r, _)
            | ObservedFact.ArtifactNotFound r -> r = reference
            | _ -> false)

    let private present facts =
        facts |> List.exists (fun f -> match f with ObservedFact.ArtifactObserved _ -> true | _ -> false)

    let private unobservable subject facts =
        facts |> List.filter (fun f -> match f with ObservedFact.Unobservable(s, _) -> s = subject | _ -> false)

    let private unknown expected subject facts fallback =
        match unobservable subject facts with
        | [] -> leaf expected ReceiptOutcome.Indeterminate (Some fallback) []
        | why -> leaf expected ReceiptOutcome.Indeterminate (Some "not observable") why

    let rec private compareFacts (facts: ObservedFact list) (expected: ExpectedReceipt) : ReceiptResult =
        match expected with
        | ExpectedReceipt.ArtifactExists r ->
            match artifactFacts r facts with
            | [] -> unknown expected r facts "no observation of the artifact"
            | seen when present seen -> leaf expected ReceiptOutcome.Match None seen
            | seen -> leaf expected ReceiptOutcome.Mismatch (Some "artifact not found") seen
        | ExpectedReceipt.ArtifactAbsent r ->
            match artifactFacts r facts with
            | [] -> unknown expected r facts "no observation of the artifact"
            | seen when present seen -> leaf expected ReceiptOutcome.Mismatch (Some "artifact still present") seen
            | seen -> leaf expected ReceiptOutcome.Match None seen
        | ExpectedReceipt.ArtifactIdentity(r, digest) ->
            match artifactFacts r facts with
            | [] -> unknown expected r facts "no observation of the artifact"
            | seen ->
                let digests = seen |> List.choose (fun f -> match f with ObservedFact.ArtifactObserved(_, d) -> Some d | _ -> None)

                if digests.IsEmpty then leaf expected ReceiptOutcome.Mismatch (Some "artifact not found") seen
                elif digests |> List.contains (Some digest) then leaf expected ReceiptOutcome.Match None seen
                elif digests |> List.forall Option.isNone then leaf expected ReceiptOutcome.Indeterminate (Some "artifact observed without a digest") seen
                else leaf expected ReceiptOutcome.Mismatch (Some "artifact digest differs") seen
        | ExpectedReceipt.CommandSucceeded command ->
            let seen =
                facts
                |> List.filter (fun f ->
                    match f with
                    | ObservedFact.CommandExited(c, _)
                    | ObservedFact.CommandOutcomeUnknown(c, _) -> c = command
                    | _ -> false)

            match List.tryLast seen with
            | None -> unknown expected command facts "command outcome not observed"
            | Some(ObservedFact.CommandExited(_, 0)) -> leaf expected ReceiptOutcome.Match None seen
            | Some(ObservedFact.CommandExited(_, code)) -> leaf expected ReceiptOutcome.Mismatch (Some $"exit code {code}") seen
            | Some _ -> leaf expected ReceiptOutcome.Indeterminate (Some "command outcome unknown") seen
        | ExpectedReceipt.StateEquals(key, value) ->
            let seen = facts |> List.filter (fun f -> match f with ObservedFact.StateObserved(k, _) -> k = key | _ -> false)

            match List.tryLast seen with
            | None -> unknown expected key facts "state not observed"
            | Some(ObservedFact.StateObserved(_, v)) when v = value -> leaf expected ReceiptOutcome.Match None seen
            | Some _ -> leaf expected ReceiptOutcome.Mismatch (Some "state differs") seen
        | ExpectedReceipt.ConformsToContract(r, contract) ->
            let seen = facts |> List.filter (fun f -> match f with ObservedFact.ContractChecked(x, c, _) -> x = r && c = contract | _ -> false)

            match List.tryLast seen with
            | None -> unknown expected r facts "contract conformance not observed"
            | Some(ObservedFact.ContractChecked(_, _, true)) -> leaf expected ReceiptOutcome.Match None seen
            | Some _ -> leaf expected ReceiptOutcome.Mismatch (Some "artifact does not conform") seen
        | ExpectedReceipt.VerificationSatisfied fingerprint ->
            let seen = facts |> List.filter (fun f -> match f with ObservedFact.VerificationObserved _ -> true | _ -> false)

            match List.tryLast seen with
            | None -> unknown expected "verification" facts "verification not observed"
            | Some(ObservedFact.VerificationObserved(fp, _)) when fp <> fingerprint ->
                leaf expected ReceiptOutcome.Indeterminate (Some "verification observed under a different evaluator") seen
            | Some(ObservedFact.VerificationObserved(_, true)) -> leaf expected ReceiptOutcome.Match None seen
            | Some _ -> leaf expected ReceiptOutcome.Mismatch (Some "verification failed") seen
        | ExpectedReceipt.TransitionObserved t ->
            match facts |> List.filter (fun f -> f = ObservedFact.TransitionRecorded t) with
            | [] -> unknown expected t facts "transition not observed"
            | seen -> leaf expected ReceiptOutcome.Match None seen
        | ExpectedReceipt.AllOf parts ->
            let constituents = parts |> List.map (compareFacts facts)
            let outcomes = constituents |> List.map _.Outcome

            let outcome =
                if outcomes |> List.contains ReceiptOutcome.Mismatch then ReceiptOutcome.Mismatch
                elif outcomes |> List.contains ReceiptOutcome.Indeterminate then ReceiptOutcome.Indeterminate
                else ReceiptOutcome.Match

            { Expected = expected
              Outcome = outcome
              Reason = None
              Evidence = []
              Constituents = constituents }

    let rec private demote (result: ReceiptResult) =
        let constituents = result.Constituents |> List.map demote

        let outcome =
            match result.Outcome, constituents with
            | ReceiptOutcome.Match, [] -> ReceiptOutcome.Indeterminate
            | _, [] -> result.Outcome
            | _, parts ->
                let outcomes = parts |> List.map _.Outcome

                if outcomes |> List.contains ReceiptOutcome.Mismatch then ReceiptOutcome.Mismatch
                elif outcomes |> List.contains ReceiptOutcome.Indeterminate then ReceiptOutcome.Indeterminate
                else ReceiptOutcome.Match

        { result with
            Outcome = outcome
            Reason =
                (if outcome <> result.Outcome && constituents.IsEmpty then
                     Some "self-reported evidence is insufficient"
                 else
                     result.Reason)
            Constituents = constituents }

    /// Compare expected with observed. Self-reported observations never yield
    /// a match (ORD-EXEC-043); narrative is never consulted.
    let compare (expected: ExpectedReceipt) (observed: ObservedReceipt) =
        let raw = compareFacts observed.Facts expected

        match observed.Source with
        | ObservationSource.SelfReported _ -> demote raw
        | _ -> raw

/// Glob matching for boundary projections, Ordo.Core's: `*`/`?` within a
/// segment, `**` across segments.
[<RequireQualifiedAccess>]
module Glob =
    let isMatch (pattern: string) (value: string) = Ordo.Core.MutationBoundary.Glob.isMatch pattern value

/// A semantic scope (`feature:x`, `cluster:x`, `authority:x`,
/// `capability:x`) and its physical projection (ORD-EXEC-120..122).
type BoundaryProjection = { Scope: string; Patterns: string list }

type MutationBoundary =
    { Scopes: string list
      Projections: BoundaryProjection list
      EvaluatorReferences: string list }

[<RequireQualifiedAccess>]
type MutationClass =
    | Within of scope: string
    | Outside
    | EvaluatorAuthority

type ScopeEffect =
    { Resource: string
      Classification: MutationClass
      Explanation: string option }

type ScopeExpansion =
    { ExpansionId: string
      Scopes: string list
      Projections: BoundaryProjection list
      Justification: string
      AuthorizedBy: string }

[<RequireQualifiedAccess>]
module MutationBoundary =
    let empty = { Scopes = []; Projections = []; EvaluatorReferences = [] }

    let isScope (raw: string) = Ordo.Core.MutationBoundary.SemanticScope.fromWire raw |> Option.isSome

    let private scopeOf raw = Ordo.Core.MutationBoundary.SemanticScope.fromWire raw
    let private scopeName scope = Ordo.Core.MutationBoundary.SemanticScope.toWire scope

    let private projections (items: BoundaryProjection list) : Ordo.Core.MutationBoundary.PhysicalProjection list =
        items |> List.choose (fun p -> scopeOf p.Scope |> Option.map (fun s -> { Scope = s; Patterns = p.Patterns }))

    let private toOrdo (boundary: MutationBoundary) : Ordo.Core.MutationBoundary.MutationBoundary =
        { Scopes = boundary.Scopes |> List.choose scopeOf
          Projections = projections boundary.Projections
          EvaluatorReferences = boundary.EvaluatorReferences }

    let private ofOrdo (boundary: Ordo.Core.MutationBoundary.MutationBoundary) : MutationBoundary =
        { Scopes = boundary.Scopes |> List.map scopeName
          Projections = boundary.Projections |> List.map (fun p -> { Scope = scopeName p.Scope; Patterns = p.Patterns })
          EvaluatorReferences = boundary.EvaluatorReferences }

    /// Ordo.Core's classification of one mutated resource (ORD-EXEC-123..126).
    let classify (boundary: MutationBoundary) (resource: string) =
        match Ordo.Core.MutationBoundary.MutationBoundary.classify (toOrdo boundary) resource with
        | Ordo.Core.MutationBoundary.WithinBoundary scope -> MutationClass.Within(scopeName scope)
        | Ordo.Core.MutationBoundary.OutsideBoundary -> MutationClass.Outside
        | Ordo.Core.MutationBoundary.EvaluatorAuthorityMutation -> MutationClass.EvaluatorAuthority

    /// Out-of-bound mutations become explicit effects; explanations are kept
    /// and change nothing (ORD-EXEC-123/124).
    let scopeEffects boundary (mutated: (string * string option) list) =
        mutated
        |> List.choose (fun (resource, explanation) ->
            match classify boundary resource with
            | MutationClass.Within _ -> None
            | c -> Some { Resource = resource; Classification = c; Explanation = explanation })

    /// The legal scope-expansion transition, decided by Ordo.Core (ORD-EXEC-125).
    let expand (expansion: ScopeExpansion) (boundary: MutationBoundary) : Result<MutationBoundary, string> =
        if expansion.Scopes |> List.exists (isScope >> not) then
            Error "scopes must be feature:|cluster:|authority:|capability:<id>"
        else
            match expansion.Projections |> List.tryFind (fun p -> not (isScope p.Scope)) with
            | Some p -> Error $"projection is not traceable to a held scope: {p.Scope}"
            | None ->
                let ordoExpansion: Ordo.Core.MutationBoundary.ScopeExpansion =
                    { ExpansionId = expansion.ExpansionId
                      AddedScopes = expansion.Scopes |> List.choose scopeOf
                      AddedProjections = projections expansion.Projections
                      Justification = expansion.Justification
                      AuthorizedBy = expansion.AuthorizedBy }

                Ordo.Core.MutationBoundary.MutationBoundary.expand ordoExpansion (toOrdo boundary)
                |> Result.map ofOrdo
                |> Result.mapError (fun error ->
                    match error with
                    | Ordo.Core.MutationBoundary.ExpansionWithoutJustification -> "a scope expansion needs a justification"
                    | Ordo.Core.MutationBoundary.ExpansionWithoutAuthorizer -> "a scope expansion needs an authorizing actor"
                    | Ordo.Core.MutationBoundary.ExpansionIntoEvaluatorAuthority hits ->
                        "expansion would make evaluator authority writable: " + String.concat ", " hits
                    | Ordo.Core.MutationBoundary.ProjectionWithoutScope scope -> $"projection is not traceable to a held scope: {scopeName scope}")

/// Containment strength (ORD-EXEC-033/060/061). A worktree is semantic-only.
[<RequireQualifiedAccess>]
type Containment =
    | Unknown
    | SemanticOnly of mechanism: string
    | HostEnforced of mechanism: string * restrictions: string list * evidence: string list

[<RequireQualifiedAccess>]
module Containment =
    let toWire c =
        match c with
        | Containment.Unknown -> "unknown"
        | Containment.SemanticOnly _ -> "semantic-only"
        | Containment.HostEnforced _ -> "host-enforced"

    let isSecuritySandbox c =
        match c with
        | Containment.HostEnforced _ -> true
        | _ -> false

/// Where an execution stands.
[<RequireQualifiedAccess>]
type ExecutionState =
    | Active
    | Blocked of reason: string
    | Completed
    | Failed of reason: string
    | Abandoned of reason: string
    | Interrupted

[<RequireQualifiedAccess>]
module ExecutionState =
    let toWire s =
        match s with
        | ExecutionState.Active -> "active"
        | ExecutionState.Blocked _ -> "blocked"
        | ExecutionState.Completed -> "completed"
        | ExecutionState.Failed _ -> "failed"
        | ExecutionState.Abandoned _ -> "abandoned"
        | ExecutionState.Interrupted -> "interrupted"

    let isTerminal s =
        match s with
        | ExecutionState.Active
        | ExecutionState.Blocked _ -> false
        | _ -> true

/// The actor performing an execution; provider/model/runtime are attributes.
type ExecutionActor =
    { Id: string
      Kind: string
      Provider: string option
      Model: string option
      Runtime: string option }

/// Workspace binding. `Path` is local bookkeeping only; it is never sent to
/// another system.
type Workspace =
    { Id: string
      Branch: string option
      Path: string option
      Mechanism: string }

/// The first-class execution envelope (ORD-EXEC-070..076).
type ExecutionEnvelope =
    { ExecutionId: string
      WorkItem: string
      Actor: ExecutionActor
      Authority: RoleAuthority
      BaselineRevision: string
      CandidateRevision: string option
      Workspace: Workspace option
      Containment: Containment
      Boundary: MutationBoundary
      Evaluator: EvaluatorIdentity option
      HumanOnlyTransitions: string list
      Parent: string option
      StartedAt: DateTimeOffset
      State: ExecutionState }

[<RequireQualifiedAccess>]
module ExecutionEnvelope =
    /// Build an envelope. The evaluator closure is excluded from the writable
    /// boundary; a boundary admitting it, or a judged role holding evaluator
    /// authority, is refused (ORD-EXEC-001/126).
    let create executionId workItem actor authority baseline boundary (evaluator: EvaluatorIdentity option) startedAt =
        let refs = evaluator |> Option.map (fun e -> e.Inputs |> List.map _.Reference) |> Option.defaultValue []

        let admitted =
            refs
            |> List.filter (fun r ->
                match MutationBoundary.classify { boundary with EvaluatorReferences = [] } r with
                | MutationClass.Within _ -> true
                | _ -> false)

        if String.IsNullOrWhiteSpace baseline then
            Error "an execution needs a baseline revision"
        elif evaluator.IsSome && RoleAuthority.allows Capability.ModifyEvaluationAuthority authority then
            Error "a judged execution cannot hold evaluator-modification authority"
        elif not admitted.IsEmpty then
            Error("the mutation boundary admits evaluator inputs: " + String.concat ", " admitted)
        else
            Ok
                { ExecutionId = executionId
                  WorkItem = workItem
                  Actor = actor
                  Authority = authority
                  BaselineRevision = baseline
                  CandidateRevision = None
                  Workspace = None
                  Containment = Containment.Unknown
                  Boundary = { boundary with EvaluatorReferences = boundary.EvaluatorReferences @ refs |> List.distinct }
                  Evaluator = evaluator
                  HumanOnlyTransitions = []
                  Parent = None
                  StartedAt = startedAt
                  State = ExecutionState.Active }

/// Reconciliation findings (ORD-EXEC-113).
[<RequireQualifiedAccess>]
type Reconciliation =
    | Occurred of evidence: string
    | DidNotOccur of evidence: string
    | StillUnknown of reason: string

/// Append-only step ledger entries (ORD-EXEC-114). Telemetry references are
/// optional evidence links; unknown usage is left unset, never zero.
[<RequireQualifiedAccess>]
type StepEntry =
    | Declared of step: string * sequence: int * name: string * dependsOn: string list * expected: ExpectedReceipt * retrySafe: string option * at: DateTimeOffset
    | Started of step: string * attempt: int * at: DateTimeOffset
    | Observed of step: string * attempt: int * observed: ObservedReceipt * result: ReceiptResult * at: DateTimeOffset
    | Reconciled of step: string * attempt: int * finding: Reconciliation * at: DateTimeOffset

[<RequireQualifiedAccess>]
type StepStatus =
    | NotStarted
    | Satisfied
    | Mismatched
    | EffectUnknown
    | ReconciledNotOccurred
    | ReconciledOccurred

[<RequireQualifiedAccess>]
type StepAction =
    | Execute
    | Reuse
    | Retry
    | Reconcile
    | Observe
    | AwaitDependencies of string list
    | ResolveMismatch

type StepView =
    { StepId: string
      Sequence: int
      Name: string
      DependsOn: string list
      Expected: ExpectedReceipt
      RetrySafe: string option
      Attempts: int
      Status: StepStatus
      Entries: StepEntry list }

[<RequireQualifiedAccess>]
module StepLedger =
    let private stepOf entry =
        match entry with
        | StepEntry.Declared(s, _, _, _, _, _, _)
        | StepEntry.Started(s, _, _)
        | StepEntry.Observed(s, _, _, _, _)
        | StepEntry.Reconciled(s, _, _, _) -> s

    let private status (entries: StepEntry list) =
        match entries |> List.filter (fun e -> match e with StepEntry.Declared _ -> false | _ -> true) |> List.tryLast with
        | None -> StepStatus.NotStarted
        | Some(StepEntry.Started _) -> StepStatus.EffectUnknown
        | Some(StepEntry.Observed(_, _, _, r, _)) ->
            match r.Outcome with
            | ReceiptOutcome.Match -> StepStatus.Satisfied
            | ReceiptOutcome.Mismatch -> StepStatus.Mismatched
            | ReceiptOutcome.Indeterminate -> StepStatus.EffectUnknown
        | Some(StepEntry.Reconciled(_, _, Reconciliation.Occurred _, _)) -> StepStatus.ReconciledOccurred
        | Some(StepEntry.Reconciled(_, _, Reconciliation.DidNotOccur _, _)) -> StepStatus.ReconciledNotOccurred
        | Some(StepEntry.Reconciled(_, _, Reconciliation.StillUnknown _, _)) -> StepStatus.EffectUnknown
        | Some(StepEntry.Declared _) -> StepStatus.NotStarted

    /// Reconstruct every step from durable entries alone (ORD-EXEC-110/115).
    let reconstruct (entries: StepEntry list) : StepView list =
        entries
        |> List.choose (fun e ->
            match e with
            | StepEntry.Declared(s, seq, name, deps, expected, safe, _) -> Some(s, seq, name, deps, expected, safe)
            | _ -> None)
        |> List.map (fun (s, seq, name, deps, expected, safe) ->
            let mine = entries |> List.filter (fun e -> stepOf e = s)

            { StepId = s
              Sequence = seq
              Name = name
              DependsOn = deps
              Expected = expected
              RetrySafe = safe
              Attempts = mine |> List.filter (fun e -> match e with StepEntry.Started _ -> true | _ -> false) |> List.length
              Status = status mine
              Entries = mine })
        |> List.sortBy _.Sequence

    let nextAction (steps: StepView list) (step: StepView) =
        let unsatisfied =
            step.DependsOn
            |> List.filter (fun d -> steps |> List.tryFind (fun s -> s.StepId = d) |> Option.forall (fun s -> s.Status <> StepStatus.Satisfied))

        match step.Status with
        | StepStatus.Satisfied -> StepAction.Reuse
        | _ when not unsatisfied.IsEmpty -> StepAction.AwaitDependencies unsatisfied
        | StepStatus.NotStarted -> StepAction.Execute
        | StepStatus.Mismatched -> StepAction.ResolveMismatch
        | StepStatus.ReconciledNotOccurred -> StepAction.Retry
        | StepStatus.ReconciledOccurred -> StepAction.Observe
        | StepStatus.EffectUnknown ->
            match step.RetrySafe with
            | Some basis when not (String.IsNullOrWhiteSpace basis) -> StepAction.Retry
            | _ -> StepAction.Reconcile

    /// Decide the entry a requested step operation appends, or refuse it.
    let declare (entries: StepEntry list) step sequence name deps expected safe at =
        if reconstruct entries |> List.exists (fun s -> s.StepId = step) then
            Error $"step {step} is already declared"
        else
            Ok(StepEntry.Declared(step, sequence, name, deps, expected, safe, at))

    let private find entries step =
        reconstruct entries |> List.tryFind (fun s -> s.StepId = step) |> Option.map (fun s -> s, reconstruct entries)

    let start (entries: StepEntry list) step at =
        match find entries step with
        | None -> Error $"step {step} is not declared"
        | Some(view, all) ->
            match nextAction all view with
            | StepAction.Execute
            | StepAction.Retry -> Ok(StepEntry.Started(step, view.Attempts + 1, at))
            | StepAction.Reconcile ->
                Error $"step {step} has an unknown effect and is not retry-safe; reconcile it before retrying"
            | other -> Error $"step {step} cannot start now (legal action: %A{other})"

    let observe (entries: StepEntry list) step (observed: ObservedReceipt) at =
        match find entries step with
        | None -> Error $"step {step} is not declared"
        | Some(view, _) ->
            match view.Status with
            | StepStatus.EffectUnknown when view.Attempts > 0 ->
                Ok(StepEntry.Observed(step, view.Attempts, observed, Receipt.compare view.Expected observed, at))
            | StepStatus.ReconciledOccurred -> Ok(StepEntry.Observed(step, view.Attempts, observed, Receipt.compare view.Expected observed, at))
            | _ -> Error $"step {step} has no attempt awaiting observation"

    let reconcile (entries: StepEntry list) step finding at =
        match find entries step with
        | None -> Error $"step {step} is not declared"
        | Some(view, _) when view.Status = StepStatus.EffectUnknown -> Ok(StepEntry.Reconciled(step, view.Attempts, finding, at))
        | Some _ -> Error $"step {step} has no unknown effect to reconcile"

/// A legal action with its availability and the reasons it is unavailable
/// (ORD-EXEC-020/021). Every presentation consumes this list.
type LegalAction =
    { Transition: string
      Target: string option
      Available: bool
      Reasons: string list
      ActorRequirement: string }

[<RequireQualifiedAccess>]
module LegalActions =
    /// `uncommitted` is the host's observation that the workspace holds
    /// changes no commit captures; completing then would record a candidate
    /// revision that does not contain the execution's output.
    let compute (envelope: ExecutionEnvelope) (steps: StepView list) (effects: ScopeEffect list) (verification: EvaluationOutcome option) (uncommitted: bool) (actorKind: string) =
        let requirement transition = if List.contains transition envelope.HumanOnlyTransitions then "human-required" else "any"

        let action transition target blockers =
            let req = requirement transition
            let authz = if req = "human-required" && actorKind <> "human" then [ "requires a human actor" ] else []
            let reasons = blockers @ authz

            { Transition = transition
              Target = target
              Available = reasons.IsEmpty
              Reasons = reasons
              ActorRequirement = req }

        let stateBlockers =
            match envelope.State with
            | ExecutionState.Active -> []
            | ExecutionState.Blocked reason -> [ "execution is blocked: " + reason ]
            | s -> [ "execution is " + ExecutionState.toWire s ]

        let stepActions =
            steps
            |> List.choose (fun s ->
                match StepLedger.nextAction steps s with
                | StepAction.Execute -> Some(action "step.start" (Some s.StepId) stateBlockers)
                | StepAction.Retry -> Some(action "step.retry" (Some s.StepId) stateBlockers)
                | StepAction.Reconcile -> Some(action "step.reconcile" (Some s.StepId) stateBlockers)
                | StepAction.Observe -> Some(action "step.observe" (Some s.StepId) stateBlockers)
                | StepAction.AwaitDependencies deps -> Some(action "step.start" (Some s.StepId) (stateBlockers @ [ "waiting on " + String.concat ", " deps ]))
                | StepAction.ResolveMismatch -> Some(action "step.start" (Some s.StepId) (stateBlockers @ [ "receipt mismatch must be resolved by governed rework" ]))
                | StepAction.Reuse -> None)

        let completion =
            [ if steps |> List.exists (fun s -> s.Status <> StepStatus.Satisfied) then
                  yield "not every step receipt matches"
              if steps |> List.exists (fun s -> s.Status = StepStatus.EffectUnknown) then
                  yield "unknown effects require reconciliation"
              if not effects.IsEmpty then
                  yield "out-of-boundary mutations are unresolved"
              if uncommitted then
                  yield "the workspace has uncommitted changes; commit them so the candidate revision captures the execution's output"
              match envelope.Evaluator, verification with
              | Some current, Some outcome when not (EvaluationOutcome.isCurrent current outcome) ->
                  yield "verification is stale or its evaluator changed"
              | Some _, Some(EvaluationOutcome.Failed(_, reason)) -> yield "verification failed: " + reason
              | _ -> () ]

        let expandBlockers =
            if envelope.Authority.Prohibits |> Set.contains Capability.ExpandMutationBoundary then
                [ $"role {ExecutionRole.toWire envelope.Authority.Role} may not expand its mutation boundary" ]
            else
                []

        let lifecycle =
            [ if not (ExecutionState.isTerminal envelope.State) then
                  yield action "execution.checkpoint" None []
                  yield action "scope.expand" None (stateBlockers @ expandBlockers)
                  yield action "execution.complete" None (stateBlockers @ completion)
                  yield action "execution.abandon" None []
              match envelope.State with
              | ExecutionState.Blocked _ -> yield action "execution.resume" None []
              | ExecutionState.Active -> yield action "execution.block" None []
              | _ -> ()
              if ExecutionState.isTerminal envelope.State && envelope.Workspace |> Option.exists (fun w -> w.Path.IsSome) then
                  yield action "workspace.cleanup" None [] ]

        stepActions
        @ (effects |> List.map (fun e -> action "scope.resolve" (Some e.Resource) stateBlockers))
        @ lifecycle
