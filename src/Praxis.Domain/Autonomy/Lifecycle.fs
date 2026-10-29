namespace Praxis.Domain.Autonomy

open System

/// A pull request as GitHub reports it at one moment.
type PullRequestObservation =
    { Number: int
      State: string
      Draft: bool
      Merged: bool
      MergeCommitSha: string option
      HeadSha: string
      BaseRef: string
      Mergeable: bool option
      MergeableState: string }

/// What the repository's own rules require of the target branch. `None`
/// means GitHub did not let us read it; that is reported as a visibility
/// limit, never read as "no requirement".
type BranchRules =
    { RequiredApprovingReviews: int option
      RequiresCodeOwnerReview: bool option
      RulesVisible: bool }

[<RequireQualifiedAccess>]
type Eligibility =
    | Eligible
    /// GitHub has not finished computing mergeability; ask again.
    | Pending of reason: string
    /// The candidate cannot merge as it is; a new candidate is needed, and
    /// with it fresh verification and review.
    | NeedsNewCandidate of code: string * detail: string
    /// GitHub itself will not merge without something only a person, or the
    /// owner's configuration change, can supply.
    | Blocked of code: string * detail: string

/// Where the durable merge journal stands for one pull request.
[<RequireQualifiedAccess>]
type JournalPhase =
    | NotStarted
    | Submitted of candidate: string * attempt: int
    | Completed of mergeCommit: string * receiptDigest: string

[<RequireQualifiedAccess>]
type Reconciliation =
    /// Recorded and verified before: a repeated request changes nothing.
    | AlreadyReceipted of mergeCommit: string * receiptDigest: string
    /// We submitted the merge and GitHub merged our candidate, but the result
    /// never reached the journal (lost response, restart): verify and record.
    | AdoptRemoteMerge of mergeCommit: string
    /// Merged, but not by this lifecycle or not with the authorized head.
    /// Never claimed as an authorized merge.
    | ForeignMerge of reason: string
    | ClosedUnmerged
    | Proceed

[<RequireQualifiedAccess>]
type LifecycleOutcome =
    | Merged of mergeCommit: string * receiptDigest: string
    | AlreadyMerged of mergeCommit: string * receiptDigest: string
    | Denied of Decision
    | RequiresOwner of Decision
    | Reverify of code: string * detail: string
    | Blocked of code: string * detail: string
    | Failed of code: string * detail: string

[<RequireQualifiedAccess>]
module LifecycleOutcome =
    let code outcome =
        match outcome with
        | LifecycleOutcome.Merged _ -> "MERGED"
        | LifecycleOutcome.AlreadyMerged _ -> "ALREADY_MERGED"
        | LifecycleOutcome.Denied _ -> "DENY"
        | LifecycleOutcome.RequiresOwner _ -> "REQUIRES_OWNER"
        | LifecycleOutcome.Reverify _ -> "REVERIFY"
        | LifecycleOutcome.Blocked _ -> "BLOCKED"
        | LifecycleOutcome.Failed _ -> "FAILED"

/// The classification of one failed GitHub call.
[<RequireQualifiedAccess>]
type CallFailure =
    /// Retry with backoff, within the policy's bound.
    | Transient of detail: string
    /// GitHub refused because the head is no longer the candidate.
    | HeadChanged of detail: string
    | Permanent of status: int * detail: string

/// `praxis.merge-receipt/1`: what was authorized, verified, submitted and
/// observed for one merge. Receipts are content-addressed and chained by the
/// infrastructure, so a removed or edited receipt breaks the chain.
type MergeReceipt =
    { RunId: string
      AuthorizationDigest: string
      PolicyVersion: int
      PolicyDigest: string
      Repository: string
      PullRequest: int
      TargetBranch: string
      CandidateSha: string
      MergeMethod: MergeStrategy
      /// The content address of the ALLOW decision the merge was submitted
      /// under, kept in the journal's decision log.
      DecisionDigest: string
      Checks: CheckObservation list
      MergeCommitSha: string
      TargetHeadSha: string
      TargetContainsMerge: bool
      Attempts: int
      Reconciled: bool
      PreviousReceiptDigest: string option
      RecordedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module Lifecycle =
    /// GitHub's own mergeability for the candidate (mergeable_state values:
    /// clean, unstable, has_hooks, behind, dirty, blocked, draft, unknown).
    let eligibility (rules: BranchRules) (pull: PullRequestObservation) =
        match rules.RequiredApprovingReviews, rules.RequiresCodeOwnerReview with
        | Some count, _ when count > 0 ->
            Eligibility.Blocked("github-requires-human-review", $"the target branch requires {count} approving review(s)")
        | _, Some true ->
            Eligibility.Blocked("github-requires-human-review", "the target branch requires code-owner review")
        | _ ->
            match pull.State, pull.Draft, pull.Mergeable, pull.MergeableState with
            | state, _, _, _ when state <> "open" -> Eligibility.Blocked("pull-request-not-open", $"the pull request is {state}")
            | _, true, _, _ -> Eligibility.Blocked("pull-request-draft", "a draft pull request is not merged unattended")
            | _, _, _, "dirty" -> Eligibility.NeedsNewCandidate("merge-conflict", "the candidate conflicts with the target branch")
            | _, _, _, "behind" -> Eligibility.NeedsNewCandidate("branch-behind", "the target branch requires an up-to-date candidate")
            | _, _, None, _
            | _, _, _, "unknown" -> Eligibility.Pending "GitHub has not computed mergeability"
            | _, _, Some false, state -> Eligibility.NeedsNewCandidate("not-mergeable", $"GitHub reports the candidate unmergeable ({state})")
            | _, _, _, "blocked" when not rules.RulesVisible ->
                Eligibility.Blocked("github-blocked", "GitHub blocks the merge and the branch rules are not readable with this credential")
            | _, _, _, "blocked" -> Eligibility.Blocked("github-blocked", "GitHub blocks the merge (a required review, check or rule is unmet)")
            | _ -> Eligibility.Eligible

    /// Reconcile the journal with GitHub before doing anything, so a restart,
    /// a lost response or a duplicate request never merges twice and never
    /// claims a merge it did not make.
    let reconcile (phase: JournalPhase) (candidate: string) (pull: PullRequestObservation) =
        match phase, pull.Merged, pull.MergeCommitSha with
        | JournalPhase.Completed(commit, digest), true, Some merged when merged = commit ->
            Reconciliation.AlreadyReceipted(commit, digest)
        | JournalPhase.Completed(commit, _), _, _ ->
            Reconciliation.ForeignMerge $"the journal records merge {commit}, which GitHub no longer reports for this pull request"
        | JournalPhase.Submitted(submitted, _), true, Some merged when submitted = candidate && pull.HeadSha = candidate ->
            Reconciliation.AdoptRemoteMerge merged
        | _, true, _ when pull.HeadSha <> candidate ->
            Reconciliation.ForeignMerge $"merged with head {pull.HeadSha}, not the authorized candidate {candidate}"
        | JournalPhase.NotStarted, true, _ -> Reconciliation.ForeignMerge "merged outside the authorized lifecycle"
        | _, true, _ -> Reconciliation.ForeignMerge "merged without a merge commit GitHub can report"
        | _, false, _ when pull.State <> "open" -> Reconciliation.ClosedUnmerged
        | _ -> Reconciliation.Proceed

    /// Exponential backoff, bounded by the policy: `None` when retries are spent.
    let retryDelay (failure: FailureHandling) (attempt: int) =
        if attempt >= failure.TransientRetryLimit then None
        else Some(TimeSpan.FromSeconds(float failure.RetryBaseDelaySeconds * Math.Pow(2.0, float attempt)))
