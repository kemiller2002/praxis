namespace Praxis.Domain.Work

open System
open System.Globalization
open System.Text.RegularExpressions
open Praxis.Domain.Provenance

/// What a `work.evidence.recorded` event states about how the work was
/// carried out (WI-0103). Execution start and checkpoints are not here: the
/// existing `work.started` and `work.checkpointed` events already record them,
/// and a verifier reads them from the same log.
[<RequireQualifiedAccess>]
type ProcessEvidenceKind =
    | Assignment
    | DependencyDecision
    | ScopeChange
    | OwnerApproval
    | IndependentReview
    | ReleaseSelection
    | TestResult
    | AcceptanceReference
    | FinalDisposition

[<RequireQualifiedAccess>]
module ProcessEvidenceKind =
    let all =
        [ ProcessEvidenceKind.Assignment
          ProcessEvidenceKind.DependencyDecision
          ProcessEvidenceKind.ScopeChange
          ProcessEvidenceKind.OwnerApproval
          ProcessEvidenceKind.IndependentReview
          ProcessEvidenceKind.ReleaseSelection
          ProcessEvidenceKind.TestResult
          ProcessEvidenceKind.AcceptanceReference
          ProcessEvidenceKind.FinalDisposition ]

    let code kind =
        match kind with
        | ProcessEvidenceKind.Assignment -> "assignment"
        | ProcessEvidenceKind.DependencyDecision -> "dependency-decision"
        | ProcessEvidenceKind.ScopeChange -> "scope-change"
        | ProcessEvidenceKind.OwnerApproval -> "owner-approval"
        | ProcessEvidenceKind.IndependentReview -> "independent-review"
        | ProcessEvidenceKind.ReleaseSelection -> "release-selection"
        | ProcessEvidenceKind.TestResult -> "test-result"
        | ProcessEvidenceKind.AcceptanceReference -> "acceptance-reference"
        | ProcessEvidenceKind.FinalDisposition -> "final-disposition"

    let tryParse (value: string) = all |> List.tryFind (fun kind -> code kind = value)

    /// Kinds whose statement is about one release candidate, so the candidate
    /// commit is part of what is recorded.
    let concernsCandidate kind =
        match kind with
        | ProcessEvidenceKind.IndependentReview
        | ProcessEvidenceKind.ReleaseSelection
        | ProcessEvidenceKind.TestResult
        | ProcessEvidenceKind.AcceptanceReference
        | ProcessEvidenceKind.FinalDisposition -> true
        | _ -> false

    /// Kinds whose statement a verifier anchors outside the log, in an external
    /// attestation such as a pull-request review: owner decisions, independent
    /// reviews and the final disposition.
    let attestable kind =
        match kind with
        | ProcessEvidenceKind.OwnerApproval
        | ProcessEvidenceKind.IndependentReview
        | ProcessEvidenceKind.FinalDisposition -> true
        | _ -> false

    /// The closed outcome vocabulary of a kind, or `None` when it has none.
    let outcomes kind =
        match kind with
        | ProcessEvidenceKind.TestResult -> Some [ "pass"; "fail"; "not-run" ]
        | ProcessEvidenceKind.IndependentReview -> Some [ "approved"; "changes-requested"; "rejected" ]
        | ProcessEvidenceKind.OwnerApproval -> Some [ "approved"; "rejected" ]
        | ProcessEvidenceKind.FinalDisposition -> Some [ "accepted"; "not-accepted"; "incomplete"; "abandoned" ]
        | _ -> None

/// The provenance class of a record. A record is only ever one of the first
/// four when Praxis writes it; `VerifiedAcceptance` exists so the vocabulary is
/// shared with verifiers, which alone may compute it.
[<RequireQualifiedAccess>]
type EvidenceProvenance =
    | ToolObserved
    | AgentAssertion
    | ReviewerFinding
    | OwnerDecision
    | VerifiedAcceptance

[<RequireQualifiedAccess>]
module EvidenceProvenance =
    let code provenance =
        match provenance with
        | EvidenceProvenance.ToolObserved -> "tool-observed"
        | EvidenceProvenance.AgentAssertion -> "agent-assertion"
        | EvidenceProvenance.ReviewerFinding -> "reviewer-finding"
        | EvidenceProvenance.OwnerDecision -> "owner-decision"
        | EvidenceProvenance.VerifiedAcceptance -> "verified-acceptance"

/// How the acting identity was established. Praxis identities are
/// self-reported; only the discovery mechanism of an undeclared CI automation
/// counts as observed by tooling.
type IdentityBasis =
    { Mechanism: string
      ActorKindSource: string
      /// The CI run the identity names (GITHUB_RUN_ID or ROS_TELEMETRY_RUN_ID), so a verifier can require that a
      /// CI record was written by the run it trusts. Self-reported like the rest of the identity.
      RunId: string option }

/// An external attestation of an owner decision, such as a pull-request
/// review. Praxis records the reference; it never treats it as checked.
type Attestation = { Type: string; Reference: string }

[<RequireQualifiedAccess>]
module Attestation =
    let types = [ "github-review"; "signed-commit"; "signed-tag" ]

    let private sep = ", "

    let tryParse (value: string) =
        match value.IndexOf ':' with
        | index when index > 0 && index < value.Length - 1 ->
            let kind = value.Substring(0, index)
            let reference = value.Substring(index + 1)

            if List.contains kind types then
                Ok { Type = kind; Reference = reference }
            else
                Error $"attestation type '{kind}' is not one of {String.Join(sep, types)}"
        | _ -> Error $"attestation '{value}' must have the form TYPE:REFERENCE"

    let render attestation = $"{attestation.Type}:{attestation.Reference}"

/// The asserted part of a record, exactly as the caller stated it.
type ProcessStatement =
    { Kind: ProcessEvidenceKind
      Summary: string
      Subjects: string list
      Candidate: string option
      References: string list
      Decision: string option
      Outcome: string option
      Attestation: Attestation option }

/// Why a record was classified as it was, and whether an owner decision
/// carries an external attestation a verifier can check.
type ProvenanceAssessment =
    { Class: EvidenceProvenance
      Basis: string
      Authenticity: string option }

type ProcessEvidenceRequest =
    { WorkItemId: string
      OccurredAt: string
      Kind: string option
      Summary: string option
      Subjects: string list
      Candidate: string option
      References: string list
      Decision: string option
      Outcome: string option
      Attestation: string option }

[<RequireQualifiedAccess>]
module ProcessEvidence =
    let eventType = "work.evidence.recorded"

    let private commitPattern = Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private blank (value: string option) =
        value |> Option.forall String.IsNullOrWhiteSpace

    /// Every argument problem, or the statement to record. Nothing about the
    /// actor is decided here.
    let validate (request: ProcessEvidenceRequest) : Result<ProcessStatement, string list> =
        let kind = request.Kind |> Option.bind ProcessEvidenceKind.tryParse
        let attestation = request.Attestation |> Option.map Attestation.tryParse

        let errors =
            [ if not (WorkItemId.isValid request.WorkItemId) then
                  yield $"'{request.WorkItemId}' is not a valid work-item ID"
              if not (isTimestamp request.OccurredAt) then
                  yield $"--occurred-at '{request.OccurredAt}' is not a timestamp"
              match request.Kind, kind with
              | None, _ -> yield "work evidence requires --kind"
              | Some value, None ->
                  let known = ProcessEvidenceKind.all |> List.map ProcessEvidenceKind.code
                  yield $"""--kind '{value}' is not one of {String.Join(", ", known)}"""
              | _ -> ()
              if blank request.Summary then
                  yield "work evidence requires a non-empty --summary"
              match request.Candidate with
              | Some candidate when not (commitPattern.IsMatch candidate) ->
                  yield $"--candidate '{candidate}' must be a full 40-character lowercase commit SHA"
              | _ -> ()
              match attestation with
              | Some(Error message) -> yield message
              | _ -> ()
              match kind with
              | Some k ->
                  let label = ProcessEvidenceKind.code k

                  if ProcessEvidenceKind.concernsCandidate k && request.Candidate.IsNone then
                      yield $"--kind {label} states something about one release candidate; pass --candidate SHA"

                  match ProcessEvidenceKind.outcomes k, request.Outcome with
                  | Some allowed, Some outcome when not (List.contains outcome allowed) ->
                      yield $"""--outcome '{outcome}' is not valid for {label}; expected {String.Join(", ", allowed)}"""
                  | Some allowed, None -> yield $"""--kind {label} requires --outcome ({String.Join(", ", allowed)})"""
                  | None, Some _ -> yield $"--kind {label} takes no --outcome"
                  | _ -> ()

                  match k with
                  | ProcessEvidenceKind.ScopeChange
                  | ProcessEvidenceKind.DependencyDecision
                  | ProcessEvidenceKind.Assignment when request.Subjects.IsEmpty ->
                      yield $"--kind {label} requires at least one --subject naming what it concerns"
                  | _ -> ()

                  if request.Attestation.IsSome && not (ProcessEvidenceKind.attestable k) then
                      yield "--attestation applies only to --kind owner-approval, independent-review or final-disposition"
              | None -> () ]

        match errors, kind with
        | [], Some k ->
            Ok
                { Kind = k
                  Summary = request.Summary.Value.Trim()
                  Subjects = request.Subjects
                  Candidate = request.Candidate
                  References = request.References
                  Decision = request.Decision
                  Outcome = request.Outcome
                  Attestation = attestation |> Option.bind Result.toOption }
        | errors, _ -> Error errors

    /// An identity is observed by tooling only when CI was discovered from its
    /// own environment and nothing declared the actor kind.
    let identityObserved (basis: IdentityBasis) =
        basis.Mechanism = "whitelisted-github-actions-environment"
        && basis.ActorKindSource = "discovery-mechanism"

    /// The provenance class of a statement made by `actor`. Writing a record
    /// never makes it independent evidence: an agent's statement stays an
    /// assertion whatever it claims, an owner decision is only a decision when
    /// a human made it, and only undeclared CI automation is tool-observed.
    let classify (actor: Actor) (basis: IdentityBasis) (statement: ProcessStatement) : ProvenanceAssessment =
        let human = actor.Kind = ActorKind.Human

        match statement.Kind with
        | ProcessEvidenceKind.OwnerApproval
        | ProcessEvidenceKind.FinalDisposition when human ->
            { Class = EvidenceProvenance.OwnerDecision
              Basis = $"recorded by a human actor (identity {basis.Mechanism}, kind from {basis.ActorKindSource}); self-reported"
              Authenticity = Some(if statement.Attestation.IsSome then "attestation-referenced" else "unattested") }
        | ProcessEvidenceKind.OwnerApproval
        | ProcessEvidenceKind.FinalDisposition ->
            { Class = EvidenceProvenance.AgentAssertion
              Basis = $"an owner decision transcribed by a {ActorKind.code actor.Kind} actor is that actor's assertion, not the owner's decision"
              Authenticity = Some "transcribed" }
        | ProcessEvidenceKind.IndependentReview when human || actor.Kind = ActorKind.Agent ->
            { Class = EvidenceProvenance.ReviewerFinding
              Basis = "a review finding; its independence from the implementer is for a verifier to establish"
              Authenticity = Some(if statement.Attestation.IsSome then "attestation-referenced" else "unattested") }
        | _ when actor.Kind = ActorKind.Automation && identityObserved basis ->
            { Class = EvidenceProvenance.ToolObserved
              Basis = "recorded by CI automation discovered from its own environment"
              Authenticity = None }
        | _ ->
            { Class = EvidenceProvenance.AgentAssertion
              Basis = $"stated by a {ActorKind.code actor.Kind} actor (identity {basis.Mechanism}, kind from {basis.ActorKindSource})"
              Authenticity = None }
