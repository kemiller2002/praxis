namespace Praxis.Infrastructure.Autonomy

open System
open System.Text
open Praxis.Domain.Autonomy

type LifecycleEnvironment =
    { GitHub: GitHubPort
      Journal: AutonomyStore.Journal
      Now: unit -> DateTimeOffset
      Sleep: TimeSpan -> unit
      PollInterval: TimeSpan
      Log: string -> unit }

type MergeRequest =
    { Repository: string
      PullRequest: int
      Candidate: string
      Authorization: AuthorizationState
      RevokedRunIds: Set<string> }

/// What the lifecycle read from GitHub for one evaluation.
type MergeContext =
    { Pull: PullRequestObservation
      BaseHead: string
      Policy: PolicyState
      Changes: ChangedPath list
      CiReferenced: string list
      Checks: CheckObservation list
      Rules: BranchRules
      /// Runs revoked by a revocation committed to the target repository.
      Revoked: Set<string> }

/// The unattended pull-request lifecycle from "checks running" to "receipt
/// recorded": reconcile, evaluate, check GitHub eligibility, submit a merge
/// pinned to the candidate SHA, verify the result on GitHub, record the
/// receipt. Every step is journaled before it is taken, so a restart resumes
/// instead of repeating, and a merge is claimed only once GitHub shows it.
[<RequireQualifiedAccess>]
module MergeLifecycle =
    [<Literal>]
    let PolicyPath = ".praxis/autonomy/policy.json"

    /// Retry bound for the reads made before the policy (and its own bound)
    /// is known. It bounds retries; it grants nothing.
    let bootstrapFailure =
        { TransientRetryLimit = 3
          RetryBaseDelaySeconds = 2
          ChecksTimeoutMinutes = 30 }

    let private event (env: LifecycleEnvironment) (request: MergeRequest) kind (fields: (string * string) list) =
        AutonomyStore.appendEvent env.Journal (env.Now()) kind
            ([ "repository", request.Repository; "pullRequest", string request.PullRequest; "candidateSha", request.Candidate ] @ fields)
        env.Log $"""{kind}: {String.Join("; ", fields |> List.map (fun (k, v) -> $"{k}={v}"))}"""

    /// Call with bounded exponential backoff on transient failure. Each retry
    /// is recorded.
    let rec private retrying env request (failure: FailureHandling) (label: string) (attempt: int) (call: unit -> Result<'a, CallFailure>) =
        match call () with
        | Error(CallFailure.Transient detail) as error ->
            match Lifecycle.retryDelay failure attempt with
            | Some delay ->
                event env request "retry" [ "call", label; "attempt", string (attempt + 1); "detail", detail ]
                env.Sleep delay
                retrying env request failure label (attempt + 1) call
            | None ->
                event env request "retries-exhausted" [ "call", label; "detail", detail ]
                error
        | other -> other

    let private failed (failure: CallFailure) =
        match failure with
        | CallFailure.Transient detail -> LifecycleOutcome.Failed("github-unavailable", detail)
        | CallFailure.HeadChanged detail -> LifecycleOutcome.Reverify("candidate-changed", detail)
        | CallFailure.Permanent(status, detail) -> LifecycleOutcome.Failed("github-error", $"HTTP {status}: {detail}")

    let private isWorkflow (path: string) =
        path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal))

    [<Literal>]
    let RevocationDirectory = ".praxis/autonomy/revocations/"

    let private isRevocation (path: string) =
        path.StartsWith(RevocationDirectory, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)

    let private readTexts env request failure (reference: string) (paths: string list) =
        paths
        |> List.fold
            (fun acc path ->
                acc |> Result.bind (fun texts ->
                    retrying env request failure "get-file" 0 (fun () -> env.GitHub.GetFile request.Repository reference path)
                    |> Result.map (fun bytes -> texts @ (bytes |> Option.map Encoding.UTF8.GetString |> Option.toList))))
            (Ok [])

    /// A revocation removes authority, so one is honoured wherever it is
    /// committed, on the protected base or in the candidate. An unreadable
    /// file there cannot name its run, so it revokes nothing; the trust store
    /// and `--revocations` remain the owner's direct channels.
    let private revokedIn (texts: string list) =
        texts
        |> List.choose (fun text -> match AutonomyJson.readRevocation text with Ok r -> Some r.RunId | Error _ -> None)
        |> Set.ofList

    /// Read everything one evaluation needs. The policy and the CI workflows
    /// are read from the target branch's current head (the protected base),
    /// never from the candidate, so a candidate cannot authorize itself.
    let gather env request (failure: FailureHandling) : Result<MergeContext, CallFailure> =
        let r label call = retrying env request failure label 0 call
        let gh = env.GitHub

        r "get-pull" (fun () -> gh.GetPull request.Repository request.PullRequest)
        |> Result.bind (fun pull ->
            r "get-base-head" (fun () -> gh.GetBranchHead request.Repository pull.BaseRef)
            |> Result.bind (fun baseHead ->
                r "get-policy" (fun () -> gh.GetFile request.Repository baseHead PolicyPath)
                |> Result.bind (fun policyBytes ->
                    r "get-files" (fun () -> gh.GetPullFiles request.Repository request.PullRequest)
                    |> Result.bind (fun changes ->
                        r "get-tree" (fun () -> gh.GetTreePaths request.Repository baseHead)
                        |> Result.bind (fun tree ->
                            r "get-candidate-tree" (fun () -> gh.GetTreePaths request.Repository request.Candidate)
                            |> Result.bind (fun candidateTree ->
                                let texts reference paths = readTexts env request failure reference paths
                                texts baseHead (tree |> List.filter isWorkflow)
                                |> Result.bind (fun workflows ->
                                    texts baseHead (tree |> List.filter isRevocation)
                                    |> Result.bind (fun baseRevocations ->
                                        texts request.Candidate (candidateTree |> List.filter isRevocation)
                                        |> Result.bind (fun candidateRevocations ->
                                            r "get-checks" (fun () -> gh.GetCheckRuns request.Repository request.Candidate)
                                            |> Result.bind (fun checks ->
                                                r "get-rules" (fun () -> gh.GetBranchRules request.Repository pull.BaseRef)
                                                |> Result.map (fun rules ->
                                                    { Pull = pull
                                                      BaseHead = baseHead
                                                      Policy =
                                                        match policyBytes with
                                                        | None -> PolicyState.Unavailable $"no {PolicyPath} on {pull.BaseRef} at {baseHead}"
                                                        | Some bytes ->
                                                            match AutonomyJson.readPolicy bytes with
                                                            | Ok(policy, digest) -> PolicyState.Loaded(policy, digest, $"{request.Repository}@{baseHead}:{PolicyPath}")
                                                            | Error reason -> PolicyState.Unavailable reason
                                                      Changes = changes
                                                      CiReferenced = Classification.ciReferencedPaths workflows tree
                                                      Checks = checks
                                                      Rules = rules
                                                      Revoked = revokedIn (baseRevocations @ candidateRevocations) })))))))))))

    let evaluate (env: LifecycleEnvironment) (request: MergeRequest) (context: MergeContext) =
        Evaluator.evaluate
            { Operation = Operation.MergeEligiblePullRequest
              Repository = request.Repository
              TargetBranch = context.Pull.BaseRef
              CandidateSha = request.Candidate
              ObservedHeadSha = Some context.Pull.HeadSha
              ChangedPaths = context.Changes
              CiReferencedPaths = context.CiReferenced
              Policy = context.Policy
              Authorization = request.Authorization
              RevokedRunIds = Set.union request.RevokedRunIds context.Revoked
              Checks = context.Checks
              Now = env.Now() }

    /// Findings that may still resolve by waiting: checks not yet finished or
    /// not yet created.
    let private onlyWaiting (decision: Decision) =
        not decision.Findings.IsEmpty
        && decision.Findings
           |> List.forall (fun f ->
               f.Stage = FindingStage.Evidence
               && (f.Code.EndsWith("-pending", StringComparison.Ordinal)
                   || f.Code.EndsWith("-missing", StringComparison.Ordinal)
                   || f.Code = "candidate-unobserved"))

    let private policyOf (context: MergeContext) =
        match context.Policy with
        | PolicyState.Loaded(policy, _, _) -> Some policy
        | PolicyState.Unavailable _ -> None

    /// Verify the merge on GitHub and record the receipt. The authorization
    /// and policy bindings come from the stored ALLOW decision the merge was
    /// submitted under.
    let private finish env request (failure: FailureHandling) (context: MergeContext) (decisionDigest: string) (mergeSha: string) (attempts: int) (reconciled: bool) =
        let r label call = retrying env request failure label 0 call
        let gh = env.GitHub

        match AutonomyStore.readDecision env.Journal decisionDigest with
        | None -> LifecycleOutcome.Failed("decision-missing", $"the journal has no decision {decisionDigest}")
        | Some decision ->
            let field (name: string) = decision[name] |> Option.ofObj |> Option.map (fun n -> n.ToString())

            r "get-pull" (fun () -> gh.GetPull request.Repository request.PullRequest)
            |> Result.bind (fun pull ->
                if not pull.Merged || pull.MergeCommitSha <> Some mergeSha then
                    Ok(LifecycleOutcome.Failed("merge-not-observed", $"GitHub does not report pull request {request.PullRequest} merged as {mergeSha}"))
                else
                    r "get-target-head" (fun () -> gh.GetBranchHead request.Repository pull.BaseRef)
                    |> Result.bind (fun head ->
                        r "verify-target" (fun () -> gh.Contains request.Repository mergeSha head)
                        |> Result.map (fun contained ->
                            if not contained then
                                LifecycleOutcome.Failed("merge-not-on-target", $"{pull.BaseRef} at {head} does not contain {mergeSha}")
                            else
                                let strategy = policyOf context |> Option.map _.MergeStrategy |> Option.defaultValue MergeStrategy.Squash
                                let used =
                                    policyOf context
                                    |> Option.map (fun p -> p.Merge.IndependentReviewCheck :: p.Merge.RequiredChecks)
                                    |> Option.defaultValue []

                                let receipt =
                                    { RunId = field "runId" |> Option.defaultValue ""
                                      AuthorizationDigest = field "authorizationDigest" |> Option.defaultValue ""
                                      PolicyVersion = field "policyVersion" |> Option.map int |> Option.defaultValue 0
                                      PolicyDigest = field "policyDigest" |> Option.defaultValue ""
                                      Repository = request.Repository
                                      PullRequest = request.PullRequest
                                      TargetBranch = pull.BaseRef
                                      CandidateSha = request.Candidate
                                      MergeMethod = strategy
                                      DecisionDigest = decisionDigest
                                      Checks = context.Checks |> List.filter (fun c -> List.contains c.Name used && c.HeadSha = request.Candidate)
                                      MergeCommitSha = mergeSha
                                      TargetHeadSha = head
                                      TargetContainsMerge = true
                                      Attempts = attempts
                                      Reconciled = reconciled
                                      PreviousReceiptDigest = AutonomyStore.lastReceiptDigest env.Journal
                                      RecordedAt = env.Now() }

                                let digest = AutonomyStore.recordReceipt env.Journal receipt
                                AutonomyStore.writePhase env.Journal request.Repository request.PullRequest (JournalPhase.Completed(mergeSha, digest)) [] (env.Now())
                                event env request "merged" [ "mergeCommitSha", mergeSha; "receiptDigest", digest; "reconciled", string reconciled ]
                                LifecycleOutcome.Merged(mergeSha, digest))))
            |> Result.defaultWith failed

    let run (env: LifecycleEnvironment) (request: MergeRequest) : LifecycleOutcome =
        let started = env.Now()
        event env request "lifecycle-started" []

        let rec loop (failure: FailureHandling) (attempt: int) =
            let phase = AutonomyStore.readPhase env.Journal request.Repository request.PullRequest
            let waitedTooLong () = env.Now() - started > TimeSpan.FromMinutes(float failure.ChecksTimeoutMinutes)

            match retrying env request failure "get-pull" 0 (fun () -> env.GitHub.GetPull request.Repository request.PullRequest) with
            | Error error -> failed error
            | Ok pull ->
                match Lifecycle.reconcile phase request.Candidate pull with
                | Reconciliation.AlreadyReceipted(commit, digest) -> LifecycleOutcome.AlreadyMerged(commit, digest)
                | Reconciliation.ForeignMerge reason -> LifecycleOutcome.Failed("foreign-merge", reason)
                | Reconciliation.ClosedUnmerged -> LifecycleOutcome.Blocked("pull-request-closed", "the pull request was closed without merging")
                | Reconciliation.AdoptRemoteMerge commit ->
                    event env request "reconcile-remote-merge" [ "mergeCommitSha", commit ]
                    match gather env request failure, AutonomyStore.readField env.Journal request.Repository request.PullRequest "decisionDigest" with
                    | Error error, _ -> failed error
                    | Ok _, None -> LifecycleOutcome.Failed("decision-missing", "the journal records a submission without its decision")
                    | Ok context, Some digest -> finish env request failure context digest commit (attempt + 1) true
                | Reconciliation.Proceed ->
                    match gather env request failure with
                    | Error error -> failed error
                    | Ok context ->
                        let failure = policyOf context |> Option.map _.Failure |> Option.defaultValue failure
                        let decision = evaluate env request context
                        let digest = AutonomyStore.recordDecision env.Journal decision
                        event env request "decision" [ "result", DecisionResult.toWire decision.Result; "decisionDigest", digest
                                                       "findings", decision.Findings |> List.map _.Code |> String.concat "," ]

                        match decision.Result with
                        | DecisionResult.RequiresOwner -> LifecycleOutcome.RequiresOwner decision
                        | DecisionResult.Deny when Evaluator.needsReverification decision ->
                            LifecycleOutcome.Reverify("candidate-changed", "the pull request head is not the verified candidate")
                        | DecisionResult.Deny when onlyWaiting decision && not (waitedTooLong ()) ->
                            env.Sleep env.PollInterval
                            loop failure attempt
                        | DecisionResult.Deny -> LifecycleOutcome.Denied decision
                        | DecisionResult.Allow ->
                            match Lifecycle.eligibility context.Rules context.Pull with
                            | Eligibility.Pending _ when not (waitedTooLong ()) ->
                                env.Sleep env.PollInterval
                                loop failure attempt
                            | Eligibility.Pending reason -> LifecycleOutcome.Failed("mergeability-timeout", reason)
                            | Eligibility.NeedsNewCandidate(code, detail) -> LifecycleOutcome.Reverify(code, detail)
                            | Eligibility.Blocked(code, detail) -> LifecycleOutcome.Blocked(code, detail)
                            | Eligibility.Eligible ->
                                let strategy = (policyOf context |> Option.get).MergeStrategy
                                AutonomyStore.writePhase env.Journal request.Repository request.PullRequest
                                    (JournalPhase.Submitted(request.Candidate, attempt + 1)) [ "decisionDigest", digest ] (env.Now())
                                event env request "merge-submitted" [ "attempt", string (attempt + 1); "method", MergeStrategy.toWire strategy ]

                                match env.GitHub.Merge request.Repository request.PullRequest request.Candidate strategy with
                                | Ok mergeSha -> finish env request failure context digest mergeSha (attempt + 1) false
                                | Error(CallFailure.Transient detail) ->
                                    // The merge may have happened even though the
                                    // response was lost: the next loop reconciles first.
                                    match Lifecycle.retryDelay failure attempt with
                                    | Some delay ->
                                        event env request "retry" [ "call", "merge"; "attempt", string (attempt + 1); "detail", detail ]
                                        env.Sleep delay
                                        loop failure (attempt + 1)
                                    | None -> LifecycleOutcome.Failed("merge-retries-exhausted", detail)
                                | Error(CallFailure.HeadChanged detail) -> LifecycleOutcome.Reverify("candidate-changed", detail)
                                | Error(CallFailure.Permanent(status, detail)) ->
                                    match env.GitHub.GetPull request.Repository request.PullRequest with
                                    | Ok after when after.Merged -> loop failure (attempt + 1)
                                    | _ -> LifecycleOutcome.Blocked("merge-refused", $"HTTP {status}: {detail}")

        let outcome = loop bootstrapFailure 0
        event env request "lifecycle-finished" [ "outcome", LifecycleOutcome.code outcome ]
        outcome
