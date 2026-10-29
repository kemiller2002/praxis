namespace Praxis.Domain.Autonomy

open System

// Autonomous (unattended) execution: the owner authorizes a run once, and
// Praxis decides every merge-related operation of that run against a
// versioned policy (docs/autonomous-execution/EXECUTION-POLICY.md). GitHub's
// permission to merge is necessary but never sufficient: an operation is
// allowed only when Praxis's own evaluation allows it. Everything in this
// namespace is pure; reading files, verifying signatures and calling GitHub
// are Praxis.Infrastructure.Autonomy's.

/// Category A: operations an owner may preauthorize for an unattended run.
[<RequireQualifiedAccess>]
type Operation =
    | CreateBranch
    | Commit
    | CreatePullRequest
    | RunVerification
    | RequestAutomatedReview
    | MergeEligiblePullRequest
    | Deploy

[<RequireQualifiedAccess>]
module Operation =
    let all =
        [ Operation.CreateBranch
          Operation.Commit
          Operation.CreatePullRequest
          Operation.RunVerification
          Operation.RequestAutomatedReview
          Operation.MergeEligiblePullRequest
          Operation.Deploy ]

    let toWire operation =
        match operation with
        | Operation.CreateBranch -> "create_branch"
        | Operation.Commit -> "commit"
        | Operation.CreatePullRequest -> "create_pull_request"
        | Operation.RunVerification -> "run_verification"
        | Operation.RequestAutomatedReview -> "request_automated_review"
        | Operation.MergeEligiblePullRequest -> "merge_eligible_pull_request"
        | Operation.Deploy -> "deploy"

    let tryParse (raw: string) = all |> List.tryFind (fun o -> toWire o = raw)

/// Category B: owner-controlled classes of change. A candidate that touches
/// one is never merged unattended, whatever else holds.
[<RequireQualifiedAccess>]
type ProtectedClass =
    | FrozenRequirements
    | AcceptanceCriteria
    | VerificationPolicy
    | SecurityPolicy
    | ExecutionPolicy
    | GitHubWorkflows
    | RepositoryRules
    | Credentials
    | PermissionExpansion
    | Infrastructure

[<RequireQualifiedAccess>]
module ProtectedClass =
    let all =
        [ ProtectedClass.FrozenRequirements
          ProtectedClass.AcceptanceCriteria
          ProtectedClass.VerificationPolicy
          ProtectedClass.SecurityPolicy
          ProtectedClass.ExecutionPolicy
          ProtectedClass.GitHubWorkflows
          ProtectedClass.RepositoryRules
          ProtectedClass.Credentials
          ProtectedClass.PermissionExpansion
          ProtectedClass.Infrastructure ]

    let toWire value =
        match value with
        | ProtectedClass.FrozenRequirements -> "frozen_requirements"
        | ProtectedClass.AcceptanceCriteria -> "acceptance_criteria"
        | ProtectedClass.VerificationPolicy -> "verification_policy"
        | ProtectedClass.SecurityPolicy -> "security_policy"
        | ProtectedClass.ExecutionPolicy -> "execution_policy"
        | ProtectedClass.GitHubWorkflows -> "github_workflows"
        | ProtectedClass.RepositoryRules -> "repository_rules"
        | ProtectedClass.Credentials -> "credentials"
        | ProtectedClass.PermissionExpansion -> "permission_expansion"
        | ProtectedClass.Infrastructure -> "infrastructure"

    let tryParse (raw: string) = all |> List.tryFind (fun c -> toWire c = raw)

[<RequireQualifiedAccess>]
type ExecutionMode =
    | Unattended
    | Attended

[<RequireQualifiedAccess>]
type MergeStrategy =
    | Squash
    | Merge
    | Rebase

[<RequireQualifiedAccess>]
module MergeStrategy =
    let toWire value =
        match value with
        | MergeStrategy.Squash -> "squash"
        | MergeStrategy.Merge -> "merge"
        | MergeStrategy.Rebase -> "rebase"

    let tryParse (raw: string) =
        [ MergeStrategy.Squash; MergeStrategy.Merge; MergeStrategy.Rebase ]
        |> List.tryFind (fun s -> toWire s = raw)

type ProtectedPath = { Pattern: string; Class: ProtectedClass }

type MergeRequirements =
    { /// Check-run names that must have concluded `success` at exactly the
      /// candidate SHA.
      RequiredChecks: string list
      /// The GitHub App that must have produced every required check run
      /// (`github-actions`). A check run another app or token wrote, such as
      /// the implementing agent's own, never counts.
      CheckProducer: string
      /// The independent-review check run. It must be distinct from the
      /// implementer's own checks and conclude `success` at the candidate.
      IndependentReviewCheck: string
      /// The most critical review findings a mergeable candidate may carry.
      /// Version 1 admits only 0.
      MaxCriticalDefects: int }

type FailureHandling =
    { /// Bounded retries of a transient GitHub or CI-infrastructure failure.
      TransientRetryLimit: int
      RetryBaseDelaySeconds: int
      /// How long the lifecycle waits for required checks before stopping.
      ChecksTimeoutMinutes: int }

/// `praxis.autonomous-execution-policy/1`.
type ExecutionPolicy =
    { PolicyVersion: int
      Mode: ExecutionMode
      Repositories: string list
      /// Target-branch globs a merge may land on.
      TargetBranches: string list
      AllowedOperations: Operation list
      MergeStrategy: MergeStrategy
      Merge: MergeRequirements
      /// Repository-specific protected paths, added to the built-in floor
      /// (Classification.floor), which a policy can never remove.
      ProtectedPaths: ProtectedPath list
      Failure: FailureHandling
      /// The longest run authorization this policy accepts.
      MaxAuthorizationHours: int }

/// `praxis.run-authorization/1`: the owner's one-time authorization of a
/// run. It carries authority only once its signature verifies against a
/// signer the trust store (outside the agent-editable repository) names.
type RunAuthorization =
    { RunId: string
      Owner: string
      KeyId: string
      IssuedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      PolicyVersion: int
      PolicyDigest: string
      Repositories: string list
      TargetBranches: string list
      PermittedOperations: Operation list
      /// Classes the owner keeps for themselves for this run. Always at least
      /// every class; listing them makes the reservation explicit and signed.
      ProtectedOperations: ProtectedClass list
      Signature: string }

/// The verified state of the authorization a decision rests on.
[<RequireQualifiedAccess>]
type AuthorizationState =
    | Verified of authorization: RunAuthorization * digest: string
    | Missing
    | Invalid of reason: string

[<RequireQualifiedAccess>]
type PolicyState =
    | Loaded of policy: ExecutionPolicy * digest: string * source: string
    | Unavailable of reason: string

/// One check run as GitHub reports it.
type CheckObservation =
    { Name: string
      HeadSha: string
      Status: string
      Conclusion: string option
      App: string
      Id: int64
      /// The check suite the run belongs to. Runs of one name from two suites
      /// (two workflows, or a run posted through the API) are ambiguous.
      Suite: int64 }

[<RequireQualifiedAccess>]
type ChangeKind =
    | Added
    | Modified
    | Removed
    | Renamed of previous: string

type ChangedPath = { Path: string; Kind: ChangeKind }

[<RequireQualifiedAccess>]
type DecisionResult =
    | Allow
    | Deny
    | RequiresOwner

[<RequireQualifiedAccess>]
module DecisionResult =
    let toWire result =
        match result with
        | DecisionResult.Allow -> "ALLOW"
        | DecisionResult.Deny -> "DENY"
        | DecisionResult.RequiresOwner -> "REQUIRES_OWNER"

/// What stage of the evaluation a finding belongs to. Gate findings
/// (authorization, policy, scope) dominate: a run without valid authority
/// is denied before anything else is considered.
[<RequireQualifiedAccess>]
type FindingStage =
    | Gate
    | Protected
    | Evidence

type Finding =
    { Stage: FindingStage
      Code: string
      Detail: string }

/// Everything a decision is computed from. Every member is already read and
/// verified by the caller; the evaluator performs no effect.
type EvaluationInput =
    { Operation: Operation
      Repository: string
      TargetBranch: string
      CandidateSha: string
      /// The pull request's head SHA observed at evaluation time, when the
      /// operation concerns a pull request.
      ObservedHeadSha: string option
      ChangedPaths: ChangedPath list
      /// Paths the protected base's CI workflows invoke (Classification.ciReferencedPaths).
      CiReferencedPaths: string list
      Policy: PolicyState
      Authorization: AuthorizationState
      RevokedRunIds: Set<string>
      /// Revocation files that could not be read. A revocation that cannot be
      /// read cannot name its run, so it denies every run (fail closed).
      RevocationProblems: string list
      Checks: CheckObservation list
      Now: DateTimeOffset }

type Decision =
    { Operation: Operation
      Repository: string
      TargetBranch: string
      CandidateSha: string
      PolicyVersion: int option
      PolicyDigest: string option
      RunId: string option
      AuthorizationDigest: string option
      Result: DecisionResult
      Findings: Finding list
      EvaluatedAt: DateTimeOffset }
