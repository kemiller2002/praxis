namespace Praxis.Domain.Autonomy

open System

/// The policy evaluator: ALLOW, DENY or REQUIRES_OWNER for one operation on
/// one candidate. It fails closed: anything missing, unreadable, unverified,
/// expired, revoked or out of scope is a denial, and there is no permissive
/// default. Gate findings dominate protected findings, which dominate
/// evidence findings, so a run without valid authority is never told it
/// merely needs the owner.
[<RequireQualifiedAccess>]
module Evaluator =
    let private finding stage code detail = { Stage = stage; Code = code; Detail = detail }
    let private gate = finding FindingStage.Gate
    let private evidence = finding FindingStage.Evidence

    let private same (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    let isCommitSha (value: string) =
        not (isNull value) && value.Length = 40 && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let private policyFindings (input: EvaluationInput) =
        match input.Policy with
        | PolicyState.Unavailable reason -> [ gate "policy-unavailable" reason ]
        | PolicyState.Loaded(policy, _, _) when policy.Mode <> ExecutionMode.Unattended ->
            [ gate "policy-not-unattended" "the policy does not authorize unattended execution" ]
        | PolicyState.Loaded _ -> []

    let private authorizationFindings (input: EvaluationInput) =
        match input.Policy, input.Authorization with
        | _, AuthorizationState.Missing -> [ gate "authorization-missing" "no run authorization was supplied" ]
        | _, AuthorizationState.Invalid reason -> [ gate "authorization-invalid" reason ]
        | PolicyState.Unavailable _, AuthorizationState.Verified _ -> []
        | PolicyState.Loaded(policy, digest, _), AuthorizationState.Verified(authorization, _) ->
            [ if authorization.PolicyDigest <> digest then
                  gate "authorization-policy-mismatch" $"authorized policy {authorization.PolicyDigest}, effective policy {digest}"
              if authorization.PolicyVersion <> policy.PolicyVersion then
                  gate "authorization-policy-version" $"authorized version {authorization.PolicyVersion}, effective version {policy.PolicyVersion}"
              if input.Now < authorization.IssuedAt then
                  gate "authorization-not-yet-valid" $"issued {Authorization.utc authorization.IssuedAt}"
              if input.Now >= authorization.ExpiresAt then
                  gate "authorization-expired" $"expired {Authorization.utc authorization.ExpiresAt}"
              if authorization.ExpiresAt <= authorization.IssuedAt
                 || authorization.ExpiresAt - authorization.IssuedAt > TimeSpan.FromHours(float policy.MaxAuthorizationHours) then
                  gate "authorization-duration" $"the authorization exceeds the policy's {policy.MaxAuthorizationHours} hours"
              if input.RevokedRunIds.Contains authorization.RunId then
                  gate "authorization-revoked" $"run {authorization.RunId} is revoked"
              match ProtectedClass.all |> List.filter (fun c -> not (List.contains c authorization.ProtectedOperations)) with
              | [] -> ()
              | missing ->
                  let names = missing |> List.map ProtectedClass.toWire |> String.concat ", "
                  gate "authorization-protected-incomplete" $"the owner must reserve every protected class; missing {names}" ]

    let private scopeFindings (input: EvaluationInput) =
        match input.Policy, input.Authorization with
        | PolicyState.Loaded(policy, _, _), AuthorizationState.Verified(authorization, _) ->
            let inBoth (policyValues: string list) (authorized: string list) value =
                List.exists (same value) policyValues && List.exists (same value) authorized

            let branchIn (globs: string list) = globs |> List.exists (fun g -> Classification.isMatch g input.TargetBranch)

            [ if not (inBoth policy.Repositories authorization.Repositories input.Repository) then
                  gate "repository-out-of-scope" $"{input.Repository} is not in both the policy and the authorization"
              if not (branchIn policy.TargetBranches && branchIn authorization.TargetBranches) then
                  gate "target-branch-out-of-scope" $"{input.TargetBranch} is not an authorized target branch"
              if not (List.contains input.Operation policy.AllowedOperations && List.contains input.Operation authorization.PermittedOperations) then
                  gate "operation-not-permitted" $"{Operation.toWire input.Operation} is not permitted by both the policy and the authorization" ]
        | _ -> []

    let private candidateFindings (input: EvaluationInput) =
        [ if not (isCommitSha input.CandidateSha) then
              gate "candidate-malformed" "the candidate must be a full 40-character lowercase commit SHA" ]

    let private protectedFindings (input: EvaluationInput) =
        let declared =
            match input.Policy with
            | PolicyState.Loaded(policy, _, _) -> policy.ProtectedPaths
            | PolicyState.Unavailable _ -> []

        Classification.classify declared input.CiReferencedPaths input.ChangedPaths
        |> List.map (fun (path, cls, reason) ->
            finding FindingStage.Protected $"protected-change:{ProtectedClass.toWire cls}" $"{path}: {reason}")

    /// The newest check run of `name` at the candidate from the trusted
    /// producer, or a finding saying why there is none that counts.
    let private requiredCheck (input: EvaluationInput) (producer: string) (code: string) (name: string) =
        let atCandidate = input.Checks |> List.filter (fun c -> c.Name = name && c.HeadSha = input.CandidateSha)
        let trusted = atCandidate |> List.filter (fun c -> c.App = producer)

        match trusted |> List.sortByDescending _.Id |> List.tryHead with
        | None when atCandidate.IsEmpty -> [ evidence $"{code}-missing" $"{name}: no check run at {input.CandidateSha}" ]
        | None ->
            let apps = atCandidate |> List.map _.App |> List.distinct |> String.concat ", "
            [ evidence $"{code}-untrusted-producer" $"{name}: produced by {apps}, not {producer}" ]
        | Some check when check.Status <> "completed" -> [ evidence $"{code}-pending" $"{name}: {check.Status}" ]
        | Some check when check.Conclusion <> Some "success" ->
            [ evidence $"{code}-failed" $"""{name}: {defaultArg check.Conclusion "none"}""" ]
        | Some _ -> []

    let private mergeFindings (input: EvaluationInput) =
        match input.Operation, input.Policy with
        | Operation.MergeEligiblePullRequest, PolicyState.Loaded(policy, _, _) ->
            let head =
                match input.ObservedHeadSha with
                | None -> [ evidence "candidate-unobserved" "the pull request's current head was not observed" ]
                | Some head when head <> input.CandidateSha ->
                    [ evidence "candidate-changed" $"the pull request head is {head}, not the verified candidate {input.CandidateSha}" ]
                | Some _ -> []

            let checks =
                policy.Merge.RequiredChecks
                |> List.collect (requiredCheck input policy.Merge.CheckProducer "required-check")

            let review =
                requiredCheck input policy.Merge.CheckProducer "independent-review" policy.Merge.IndependentReviewCheck

            head @ checks @ review
        | _ -> []

    let evaluate (input: EvaluationInput) : Decision =
        let gates = policyFindings input @ authorizationFindings input @ scopeFindings input @ candidateFindings input
        let protectedChanges = protectedFindings input
        let evidenceFindings = mergeFindings input

        let result =
            if not gates.IsEmpty then DecisionResult.Deny
            elif not protectedChanges.IsEmpty then DecisionResult.RequiresOwner
            elif not evidenceFindings.IsEmpty then DecisionResult.Deny
            else DecisionResult.Allow

        let policyVersion, policyDigest =
            match input.Policy with
            | PolicyState.Loaded(policy, digest, _) -> Some policy.PolicyVersion, Some digest
            | PolicyState.Unavailable _ -> None, None

        let runId, authorizationDigest =
            match input.Authorization with
            | AuthorizationState.Verified(authorization, digest) -> Some authorization.RunId, Some digest
            | _ -> None, None

        { Operation = input.Operation
          Repository = input.Repository
          TargetBranch = input.TargetBranch
          CandidateSha = input.CandidateSha
          PolicyVersion = policyVersion
          PolicyDigest = policyDigest
          RunId = runId
          AuthorizationDigest = authorizationDigest
          Result = result
          Findings = gates @ protectedChanges @ evidenceFindings
          EvaluatedAt = input.Now }

    /// The candidate must be re-verified rather than retried: its head moved,
    /// or the evidence is for another commit.
    let needsReverification (decision: Decision) =
        decision.Findings |> List.exists (fun f -> f.Code = "candidate-changed")
