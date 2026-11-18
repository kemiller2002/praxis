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
    | Evaluated of Evaluation
    | Rejected of reasons: string list
    | Approved of evaluation: Evaluation * approver: Actor * at: DateTimeOffset
    | Applied of evaluation: Evaluation * approver: Actor * appliedVersion: string * at: DateTimeOffset
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

    let private finding code detail = { Code = code; Detail = detail }
    let private blank (value: string) = String.IsNullOrWhiteSpace value

    /// Whether a change to `target` needs a human approver (initially: all protected governance).
    let requiresHumanApproval (target: ImprovementTarget) =
        match target with
        | ImprovementTarget.Governance _ -> true
        | ImprovementTarget.Knowledge _
        | ImprovementTarget.Skill _
        | ImprovementTarget.Prompt _ -> false

    /// The nine required parts of a proposal (failure evidence, root cause,
    /// action, expected improvement, cost, rollback, provenance); evaluation
    /// and approval are checked by the transitions.
    let validate (proposal: ImprovementProposal) : ProposalFinding list =
        [ if blank proposal.Id then yield finding IncompleteCode "the proposal has no id"
          if proposal.Version < 1 then yield finding IncompleteCode "the version must be 1 or greater"
          if proposal.Failures.IsEmpty then yield finding IncompleteCode "no originating failure is recorded"
          for failure in proposal.Failures do
              if failure.EvidenceRefs |> List.forall blank then
                  yield finding IncompleteCode $"failure '{failure.Summary}' cites no evidence"
          if blank proposal.CorrectiveAction then yield finding IncompleteCode "no corrective action is stated"
          if blank proposal.ExpectedImprovement then yield finding IncompleteCode "no expected improvement is stated"
          if proposal.EstimatedCost < 0m then yield finding IncompleteCode "the estimated cost cannot be negative"
          if blank proposal.RollbackPlan then yield finding IncompleteCode "no rollback plan is stated"
          if blank proposal.ProposedBy.Id then yield finding IncompleteCode "the proposer is not identified" ]

    /// Every reason `evaluation` does not support `proposal`. A passing
    /// counterfactual alone is memorisation; generalisation needs held-out
    /// cases of the same root cause that are not the original cases.
    let judge (proposal: ImprovementProposal) (evaluation: Evaluation) : ProposalFinding list =
        let counterfactualIds = evaluation.Counterfactual |> List.map _.CaseId |> Set.ofList

        [ if evaluation.Counterfactual.IsEmpty then
              yield finding CounterfactualFailedCode "no counterfactual case re-runs the original failure"
          for case in evaluation.Counterfactual |> List.filter (fun case -> not case.Passed) do
              yield finding CounterfactualFailedCode $"the original failure '{case.CaseId}' still fails with the proposal applied"
          if evaluation.Regression.IsEmpty then
              yield finding RegressionCode "no previously successful case was re-run"
          for case in evaluation.Regression |> List.filter (fun case -> not case.Passed) do
              yield finding RegressionCode $"previously successful case '{case.CaseId}' now fails"
          for case in evaluation.HeldOut |> List.filter (fun case -> counterfactualIds.Contains case.CaseId) do
              yield finding MemorizedCode $"held-out case '{case.CaseId}' is one of the cases the proposal was written from"
          let independent =
              evaluation.HeldOut
              |> List.filter (fun case -> not (counterfactualIds.Contains case.CaseId) && case.RootCause = proposal.RootCause)
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
            | [] -> Ok(ProposalState.Evaluated evaluation)
            | findings -> Ok(ProposalState.Rejected(findings |> List.map (fun item -> $"{item.Code}: {item.Detail}")))
        | other -> illegal other "evaluate"

    /// Evaluated -> Approved. Nobody approves their own proposal; protected
    /// governance needs a human approver.
    let approve (proposal: ImprovementProposal) (state: ProposalState) (approver: Actor) (at: DateTimeOffset) =
        match state with
        | ProposalState.Evaluated evaluation ->
            [ if not (blank approver.Id) && approver.Id = proposal.ProposedBy.Id then
                  yield finding SelfApprovalCode $"'{approver.Id}' proposed this change and cannot approve it"
              if blank approver.Id then
                  yield finding SelfApprovalCode "the approver is not identified"
              if requiresHumanApproval proposal.Target && approver.Kind <> ActorKind.Human then
                  yield
                      finding
                          HumanApprovalRequiredCode
                          $"{proposal.Target} changes protected governance and needs a human approver, not {ActorKind.code approver.Kind}" ]
            |> function
                | [] -> Ok(ProposalState.Approved(evaluation, approver, at))
                | findings -> Error findings
        | other -> illegal other "approve"

    /// Approved -> Applied, recording the version of the target it produced.
    let apply (state: ProposalState) (appliedVersion: string) (at: DateTimeOffset) =
        match state with
        | ProposalState.Approved(evaluation, approver, _) when not (blank appliedVersion) ->
            Ok(ProposalState.Applied(evaluation, approver, appliedVersion, at))
        | ProposalState.Approved _ -> Error [ finding IncompleteCode "an applied proposal must name the version it produced" ]
        | other -> illegal other "apply"

    /// Applied -> RolledBack (the rollback plan is executed by the caller).
    let rollBack (state: ProposalState) (reason: string) (at: DateTimeOffset) =
        match state with
        | ProposalState.Applied _ when not (blank reason) -> Ok(ProposalState.RolledBack(reason, at))
        | ProposalState.Applied _ -> Error [ finding IncompleteCode "a rollback must state its reason" ]
        | other -> illegal other "roll back"
