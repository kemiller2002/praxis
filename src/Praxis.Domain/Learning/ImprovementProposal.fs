namespace Praxis.Domain.Learning

open System
open Praxis.Domain.Autonomy
open Praxis.Domain.Provenance

// Governed self-improvement (PRX-LEARN-001). A lesson learned from a failure
// becomes a versioned, evidence-bound proposal that must survive a
// counterfactual test against the original failure, a held-out test against
// other failures of the same root cause, and a regression test against cases
// that already succeeded, before anyone may approve it. Changes to protected
// governance (verification policy, security policy, execution policy, ...)
// need a human approver who is not the proposer, so an agent can never
// silently rewrite its own rules. Pure: every input is already read.

/// What produced the lesson.
[<RequireQualifiedAccess>]
type FailureSource =
    | FailedBuild
    | FailedVerification
    | HumanCorrection
    | RejectedPullRequest
    | RepeatedAgentMistake
    | RequirementMisunderstanding
    | SecurityFinding
    | IncorrectArchitecturalAssumption
    | ExcessiveTokenConsumption
    | UnnecessaryHumanIntervention

/// Root-cause class. Held-out cases must share it with the original failure:
/// a rule that fixes only the instance it was written from is memorised, not learned.
[<RequireQualifiedAccess>]
type RootCause =
    | MissingKnowledge
    | AmbiguousRequirement
    | ContradictoryInstruction
    | ProcessGap
    | VerificationGap
    | ToolDefect
    | PolicyGap
    | ModelLimitation

/// What the proposal changes. Protected governance reuses Praxis's own
/// protected classes, so the learning loop and autonomous execution agree on
/// what an agent may not change by itself.
[<RequireQualifiedAccess>]
type ImprovementTarget =
    | Knowledge of path: string
    | Skill of name: string
    | Prompt of name: string
    | Governance of ProtectedClass

/// One observed failure, bound to evidence (paths, receipts, run ids).
type ObservedFailure =
    { Source: FailureSource
      Summary: string
      EvidenceRefs: string list
      ObservedAt: DateTimeOffset }

/// One evaluation case and its observed result with the proposal applied.
type CaseResult =
    { CaseId: string
      RootCause: RootCause
      Passed: bool
      EvidenceRef: string }

type Evaluation =
    { /// The original failure(s) the proposal was written from.
      Counterfactual: CaseResult list
      /// Other failures of the same root cause, not used to write the proposal.
      HeldOut: CaseResult list
      /// Cases that succeeded before the proposal; they must still succeed.
      Regression: CaseResult list
      EvaluatedAt: DateTimeOffset }

type ImprovementProposal =
    { Id: string
      Version: int
      Failures: ObservedFailure list
      RootCause: RootCause
      CorrectiveAction: string
      Target: ImprovementTarget
      ExpectedImprovement: string
      /// Estimated execution plus maintenance cost, in the unit the caller records (e.g. minutes per month).
      EstimatedCost: decimal
      RollbackPlan: string
      ProposedBy: Actor
      /// Earlier proposal versions or records this one derives from.
      DerivedFrom: string list }

[<RequireQualifiedAccess>]
type ProposalState =
    | Proposed
    /// The proposal is snapshotted at evaluation: approval of any other
    /// proposal (changed target, proposer or action) is refused.
    | Evaluated of proposal: ImprovementProposal * evaluation: Evaluation
    | Rejected of reasons: string list
    | Approved of proposal: ImprovementProposal * evaluation: Evaluation * approver: Actor * at: DateTimeOffset
    | Applied of proposal: ImprovementProposal * evaluation: Evaluation * approver: Actor * appliedVersion: string * at: DateTimeOffset
    | RolledBack of reason: string * at: DateTimeOffset

type ProposalFinding = { Code: string; Detail: string }

[<RequireQualifiedAccess>]
module ImprovementProposal =
    let IncompleteCode = "PRX.LEARN.INCOMPLETE"
    let IllegalTransitionCode = "PRX.LEARN.ILLEGAL_TRANSITION"
    let CounterfactualFailedCode = "PRX.LEARN.COUNTERFACTUAL_FAILED"
    let RegressionCode = "PRX.LEARN.REGRESSION"
    let GeneralizationUnprovenCode = "PRX.LEARN.GENERALIZATION_UNPROVEN"
    let MemorizedCode = "PRX.LEARN.MEMORIZED"
    let SelfApprovalCode = "PRX.LEARN.SELF_APPROVAL"
    let HumanApprovalRequiredCode = "PRX.LEARN.HUMAN_APPROVAL_REQUIRED"
    let ProposalChangedCode = "PRX.LEARN.PROPOSAL_CHANGED"

    let private finding code detail = { Code = code; Detail = detail }
    let private blank (value: string) = String.IsNullOrWhiteSpace value

    /// Identities and case ids compare trimmed and case-insensitively, so a
    /// re-spelling ("d-t4 " for "D-T4", "Agent" for "agent") is the same thing.
    let private canonical (value: string) = if isNull value then "" else value.Trim().ToLowerInvariant()

    /// Repository files that govern agents. A "knowledge" change to one of
    /// these is a governance change.
    let isGovernancePath (path: string) =
        let rec relative (value: string) =
            if value.StartsWith("./", StringComparison.Ordinal) then relative (value.Substring 2)
            elif value.StartsWith("/", StringComparison.Ordinal) then relative (value.Substring 1)
            else value

        let normal = relative ((canonical path).Replace('\\', '/'))
        let name = normal.Split('/') |> Array.last
        List.contains name [ "agents.md"; "claude.md"; "gemini.md"; "ros.json"; "conditor.json" ]
        || [ ".github/"; ".claude/"; ".ros/"; "docs/00-governance/" ]
           |> List.exists (fun prefix -> normal.StartsWith(prefix, StringComparison.Ordinal))

    /// Whether `target` changes the rules that govern agents.
    let isGovernance (target: ImprovementTarget) =
        match target with
        | ImprovementTarget.Governance _ -> true
        | ImprovementTarget.Knowledge path -> isGovernancePath path
        | ImprovementTarget.Skill _
        | ImprovementTarget.Prompt _ -> false

    /// Initially every change needs a human approver: prompts and skills
    /// steer agents as much as policy does. Relaxing this for non-governance
    /// targets is itself a governance decision, made by changing this rule
    /// through review.
    let requiresHumanApproval (_: ImprovementTarget) = true

    /// The nine required parts of a proposal (failure evidence, root cause,
    /// action, expected improvement, cost, rollback, provenance); evaluation
    /// and approval are checked by the transitions.
    let validate (proposal: ImprovementProposal) : ProposalFinding list =
        [ if blank proposal.Id then yield finding IncompleteCode "the proposal has no id"
          if proposal.Version < 1 then yield finding IncompleteCode "the version must be 1 or greater"
          if proposal.Failures.IsEmpty then yield finding IncompleteCode "no originating failure is recorded"
          for failure in proposal.Failures do
              if failure.EvidenceRefs.IsEmpty || failure.EvidenceRefs |> List.exists blank then
                  yield finding IncompleteCode $"failure '{failure.Summary}' cites no evidence or a blank evidence reference"
          if blank proposal.CorrectiveAction then yield finding IncompleteCode "no corrective action is stated"
          if blank proposal.ExpectedImprovement then yield finding IncompleteCode "no expected improvement is stated"
          if proposal.EstimatedCost < 0m then yield finding IncompleteCode "the estimated cost cannot be negative"
          if blank proposal.RollbackPlan then yield finding IncompleteCode "no rollback plan is stated"
          if blank proposal.ProposedBy.Id then yield finding IncompleteCode "the proposer is not identified" ]

    /// Every reason `evaluation` does not support `proposal`. A passing
    /// counterfactual alone is memorisation; generalisation needs held-out
    /// cases of the same root cause that are not the original cases.
    let judge (proposal: ImprovementProposal) (evaluation: Evaluation) : ProposalFinding list =
        let counterfactualIds = evaluation.Counterfactual |> List.map (fun case -> canonical case.CaseId) |> Set.ofList
        let allCases = evaluation.Counterfactual @ evaluation.HeldOut @ evaluation.Regression

        [ for case in allCases |> List.filter (fun case -> blank case.CaseId || blank case.EvidenceRef) do
              yield finding IncompleteCode $"evaluation case '{case.CaseId}' has no id or no evidence reference"
          if evaluation.Counterfactual.IsEmpty then
              yield finding CounterfactualFailedCode "no counterfactual case re-runs the original failure"
          for case in evaluation.Counterfactual |> List.filter (fun case -> not case.Passed) do
              yield finding CounterfactualFailedCode $"the original failure '{case.CaseId}' still fails with the proposal applied"
          if evaluation.Regression.IsEmpty then
              yield finding RegressionCode "no previously successful case was re-run"
          for case in evaluation.Regression |> List.filter (fun case -> not case.Passed) do
              yield finding RegressionCode $"previously successful case '{case.CaseId}' now fails"
          for case in evaluation.HeldOut |> List.filter (fun case -> counterfactualIds.Contains(canonical case.CaseId)) do
              yield finding MemorizedCode $"held-out case '{case.CaseId}' is one of the cases the proposal was written from"
          let independent =
              evaluation.HeldOut
              |> List.filter (fun case -> not (counterfactualIds.Contains(canonical case.CaseId)) && case.RootCause = proposal.RootCause)
          if independent.IsEmpty then
              yield
                  finding
                      GeneralizationUnprovenCode
                      "no held-out case of the same root cause was evaluated, so the lesson may only memorise the original failure"
          for case in independent |> List.filter (fun case -> not case.Passed) do
              yield finding GeneralizationUnprovenCode $"held-out case '{case.CaseId}' still fails" ]

    let private illegal (state: ProposalState) (action: string) =
        Error [ finding IllegalTransitionCode $"cannot {action} a proposal that is {state}" ]

    /// Proposed -> Evaluated, or Rejected with every reason.
    let evaluate (proposal: ImprovementProposal) (state: ProposalState) (evaluation: Evaluation) =
        match state with
        | ProposalState.Proposed ->
            match validate proposal @ judge proposal evaluation with
            | [] -> Ok(ProposalState.Evaluated(proposal, evaluation))
            | findings -> Ok(ProposalState.Rejected(findings |> List.map (fun item -> $"{item.Code}: {item.Detail}")))
        | other -> illegal other "evaluate"

    /// Evaluated -> Approved. The proposal must be the one that was
    /// evaluated; nobody approves their own proposal; the approver must be an
    /// identified human (initially, for every target). Actor kinds are
    /// self-reported provenance, not authentication: binding approval to an
    /// owner-signed authorization is the next step (see the design note).
    let approve (proposal: ImprovementProposal) (state: ProposalState) (approver: Actor) (at: DateTimeOffset) =
        match state with
        | ProposalState.Evaluated(evaluated, evaluation) ->
            [ if evaluated <> proposal then
                  yield finding ProposalChangedCode "the proposal differs from the one that was evaluated; evaluate it again"
              if blank approver.Id then
                  yield finding SelfApprovalCode "the approver is not identified"
              elif canonical approver.Id = canonical evaluated.ProposedBy.Id then
                  yield finding SelfApprovalCode $"'{approver.Id}' proposed this change and cannot approve it"
              if requiresHumanApproval evaluated.Target && approver.Kind <> ActorKind.Human then
                  yield
                      finding
                          HumanApprovalRequiredCode
                          $"{evaluated.Target} needs a human approver, not {ActorKind.code approver.Kind}" ]
            |> function
                | [] -> Ok(ProposalState.Approved(evaluated, evaluation, approver, at))
                | findings -> Error findings
        | other -> illegal other "approve"

    /// Approved -> Applied, recording the version of the target it produced.
    let apply (state: ProposalState) (appliedVersion: string) (at: DateTimeOffset) =
        match state with
        | ProposalState.Approved(proposal, evaluation, approver, _) when not (blank appliedVersion) ->
            Ok(ProposalState.Applied(proposal, evaluation, approver, appliedVersion, at))
        | ProposalState.Approved _ -> Error [ finding IncompleteCode "an applied proposal must name the version it produced" ]
        | other -> illegal other "apply"

    /// Applied -> RolledBack (the rollback plan is executed by the caller).
    let rollBack (state: ProposalState) (reason: string) (at: DateTimeOffset) =
        match state with
        | ProposalState.Applied _ when not (blank reason) -> Ok(ProposalState.RolledBack(reason, at))
        | ProposalState.Applied _ -> Error [ finding IncompleteCode "a rollback must state its reason" ]
        | other -> illegal other "roll back"
