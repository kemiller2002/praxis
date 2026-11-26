namespace Praxis.Domain.Governance

open System
open Praxis.Domain.Autonomy
open Praxis.Domain.Work

/// The validation policy as read from the protected branch.
type ProtectedPolicy =
    { Id: string
      Digest: string
      /// The repository the policy names; its digest binds receipts to it.
      Repository: string option
      RequiredValidations: string list }

/// One receipt presented with the candidate, after trust-root verification.
type PresentedReceipt =
    { Source: string
      Check: Result<VerifiedReceipt, GovernanceFinding> }

/// How a verified commit relates to the candidate head.
type VerifiedCommitRelation =
    { IsAncestorOfCandidate: bool
      /// Paths that differ between the verified commit and the candidate head.
      ChangedSince: string list
      /// The verified commit's tree, as Git reports it.
      TreeId: string option }

/// Everything `merge authorize` decides from. Every member is gathered by the
/// infrastructure from the protected branch, the candidate's Git objects or
/// the platform; nothing is taken from the candidate's own claims about
/// itself except the receipts, which are verified here.
type MergeFacts =
    { Repository: string
      RequestedRepository: string
      ExpectedTarget: string
      TargetBranch: string
      TargetHead: string
      CandidateHead: string
      CandidateContainsTarget: bool
      ChangedPaths: ChangedPath list
      CiReferenced: string list
      DeclaredWorkItems: string list
      AuthorizedWorkItems: string list
      Standing: StandingAuthorization list
      RequireVerifiedCompletion: bool
      Policy: ProtectedPolicy option
      Trust: VerifierTrustRoot option
      Receipts: PresentedReceipt list
      VerifiedCommits: Map<string, VerifiedCommitRelation>
      Authorities: GovernanceAuthorities
      ElevatedApprovals: ElevatedApproval list
      Exceptions: GovernanceException list
      /// Weakenings of the candidate's trust root against the protected one.
      TrustRootWeakenings: string list
      /// `Some true` when `governance inspect` showed the platform enforces
      /// the profile; `None` when not assessed.
      PlatformEnforced: bool option
      EvaluatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type MergeOutcome =
    /// Every condition held with verified evidence.
    | Verified
    /// Conditions held only because a valid exception waived some; this is
    /// never verified completion.
    | AuthorizedByException of exceptionIds: string list
    | Refused

[<RequireQualifiedAccess>]
module MergeOutcome =
    let toWire outcome =
        match outcome with
        | MergeOutcome.Verified -> "verified"
        | MergeOutcome.AuthorizedByException _ -> "authorized-by-exception"
        | MergeOutcome.Refused -> "refused"

    let permitsMerge outcome = outcome <> MergeOutcome.Refused

type MergeDecision =
    { Outcome: MergeOutcome
      Repository: string
      TargetBranch: string
      TargetHead: string
      CandidateHead: string
      WorkItems: string list
      /// How the work was authorized: work items on the protected branch or
      /// the standing authorization (and category) that covers it.
      AuthorizationBasis: string list
      VerifiedCommit: string option
      Receipts: VerifiedReceipt list
      ProtectedChanges: string list
      ElevatedApprovals: ElevatedApproval list
      Refusals: GovernanceFinding list
      Waived: GovernanceFinding list
      Advisories: string list
      EvaluatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module MergeAuthorization =
    let private refuse code detail remedy who = GovernanceFinding.create code detail remedy who

    let private identity (facts: MergeFacts) =
        [ if not (String.Equals(facts.Repository, facts.RequestedRepository, StringComparison.OrdinalIgnoreCase)) then
              refuse "MRG.REPOSITORY_MISMATCH" $"the request names {facts.RequestedRepository}, the protected configuration {facts.Repository}" "authorize the pull request in its own repository" Principal.WorkCoordinator
          if facts.TargetBranch <> facts.ExpectedTarget then
              refuse "MRG.TARGET_MISMATCH" $"the pull request targets {facts.TargetBranch}, not {facts.ExpectedTarget}" "retarget the pull request" Principal.AgentImplementer ]

    let private authorization (facts: MergeFacts) =
        let paths = facts.ChangedPaths |> List.map _.Path

        if facts.DeclaredWorkItems.IsEmpty then
            [], [ refuse "MRG.WORK_ITEM_UNDECLARED" "the pull request names no work item (a 'Praxis-Work-Item: ID' line in its description)" "declare the work item the change implements" Principal.AgentImplementer ]
        else
            let standing = Authority.standing facts.Standing facts.Repository facts.EvaluatedAt paths

            let basis, refusals =
                facts.DeclaredWorkItems
                |> List.map (fun item ->
                    if List.contains item facts.AuthorizedWorkItems then Ok $"work item {item} authorized on {facts.ExpectedTarget}"
                    else
                        match standing with
                        | Some(policy, category) -> Ok $"work item {item} under standing authorization {policy.Id} ({category.Name})"
                        | None ->
                            Error(
                                refuse
                                    "MRG.WORK_ITEM_UNAUTHORIZED"
                                    $"{item} is not an open work item on {facts.ExpectedTarget} and no standing authorization covers these changes"
                                    "have a work authorizer create the work item on the protected branch, or keep the change inside a standing category"
                                    Principal.WorkAuthorizer
                            ))
                |> List.fold (fun (ok, bad) r -> match r with Ok b -> ok @ [ b ], bad | Error f -> ok, bad @ [ f ]) ([], [])

            basis, refusals

    let private protectedChanges (facts: MergeFacts) =
        let touched = Authority.protectedChanges facts.CiReferenced facts.ChangedPaths
        let described = touched |> List.map (fun (path, cls, reason) -> $"{path} ({ProtectedClass.toWire cls}: {reason})") |> List.distinct

        if described.IsEmpty then
            [], [], []
        else
            let approvals = facts.ElevatedApprovals |> List.map (Authority.elevated facts.Authorities facts.CandidateHead)
            let valid = approvals |> List.choose (function Ok a -> Some a | Error _ -> None)
            let rejected = approvals |> List.choose (function Error e -> Some e | Ok _ -> None)

            if valid.IsEmpty then
                let why = if rejected.IsEmpty then "no elevated approval names this candidate" else String.concat "; " rejected
                let weakenings = String.concat ", " facts.TrustRootWeakenings
                let weakening = if facts.TrustRootWeakenings.IsEmpty then "" else $"; the trust root change {weakenings}"
                let changes = String.concat "; " described

                described,
                [],
                [ refuse
                      "MRG.PROTECTED_CHANGE_UNAUTHORIZED"
                      $"the candidate changes protected governance ({changes}){weakening}; {why}"
                      $"an elevated approver comments '/praxis approve-elevated {facts.CandidateHead}' on the pull request (in person, not through an agent)"
                      Principal.OrganizationOwner ]
            else
                described, valid, []

    let private receiptFinding (finding: GovernanceFinding) =
        match finding.Code with
        | "GOV.RECEIPT_INVALID_SIGNATURE"
        | "GOV.RECEIPT_BINDING_MISMATCH"
        | "GOV.RECEIPT_MALFORMED" -> refuse "MRG.RECEIPT_FORGED" finding.Detail "discard the receipt; only the verifier produces receipts" Principal.VerificationService
        | _ -> refuse "MRG.RECEIPT_UNTRUSTED" finding.Detail finding.Remedy finding.ResolvedBy

    /// The commit at which every required validation has a receipt, chosen
    /// as the candidate head itself when possible, else the verified commit
    /// nearest the head (fewest changed paths).
    let private verification (facts: MergeFacts) =
        match facts.Policy, facts.Trust with
        | _, None ->
            None, [], [ refuse "MRG.TRUST_ROOT_UNAVAILABLE" "no completion authority trust root is provisioned on the protected branch" "provision one (docs/governance/bootstrap.md)" Principal.OrganizationOwner ]
        | None, _ ->
            None, [], [ refuse "MRG.POLICY_UNAVAILABLE" "no validation policy is on the protected branch" "add .praxis/validation-policy.json through an elevated change" Principal.OrganizationOwner ]
        | Some policy, Some _ ->
            let invalid = facts.Receipts |> List.choose (fun r -> match r.Check with Error f -> Some(receiptFinding { f with Detail = $"{r.Source}: {f.Detail}" }) | Ok _ -> None)
            let trusted = facts.Receipts |> List.choose (fun r -> match r.Check with Ok v -> Some v | Error _ -> None)

            let policyMismatch =
                trusted
                |> List.filter (fun r -> r.Payload.ValidationPolicyDigest <> policy.Digest)
                |> List.map (fun r -> refuse "MRG.RECEIPT_POLICY_MISMATCH" $"receipt {r.Digest} was verified under policy {r.Payload.ValidationPolicyDigest}, not the protected {policy.Digest}" "re-verify under the protected policy" Principal.VerificationService)

            let foreignItem =
                trusted
                |> List.filter (fun r -> not (List.contains r.Payload.WorkItemId facts.DeclaredWorkItems))
                |> List.map (fun r -> refuse "MRG.RECEIPT_WORK_ITEM_MISMATCH" $"receipt {r.Digest} verifies {r.Payload.WorkItemId}, which this pull request does not declare" "present receipts for the declared work item" Principal.AgentImplementer)

            let usable =
                trusted
                |> List.filter (fun r -> r.Payload.ValidationPolicyDigest = policy.Digest && List.contains r.Payload.WorkItemId facts.DeclaredWorkItems)

            let repository =
                match policy.Repository with
                | Some named when not (String.Equals(named, facts.Repository, StringComparison.OrdinalIgnoreCase)) ->
                    [ refuse "MRG.POLICY_UNAVAILABLE" $"the protected policy names {named}, not {facts.Repository}" "fix the protected policy" Principal.RepositoryAdministrator ]
                | _ -> []

            let coverage (commit: string) =
                let atCommit = usable |> List.filter (fun r -> r.Payload.Commit = commit)
                let latest name = atCommit |> List.filter (fun r -> r.Payload.Validation = name) |> List.sortByDescending (fun r -> r.Payload.ObservedAt) |> List.tryHead
                let chosen = policy.RequiredValidations |> List.map (fun name -> name, latest name)
                let missing = chosen |> List.filter (fun (_, r) -> r.IsNone) |> List.map fst
                let failed = chosen |> List.choose (fun (name, r) -> match r with Some r when r.Payload.Status <> CheckStatus.Passed -> Some(name, r) | _ -> None)
                atCommit, chosen |> List.choose snd, missing, failed

            let candidates =
                usable
                |> List.map (fun r -> r.Payload.Commit)
                |> List.distinct
                |> List.map (fun commit -> commit, coverage commit)
                |> List.filter (fun (_, (_, _, missing, _)) -> missing.IsEmpty)
                |> List.sortBy (fun (commit, _) ->
                    if commit = facts.CandidateHead then -1
                    else facts.VerifiedCommits |> Map.tryFind commit |> Option.map (fun rel -> rel.ChangedSince.Length) |> Option.defaultValue Int32.MaxValue)

            match candidates with
            | [] ->
                let required = String.concat ", " policy.RequiredValidations
                let incomplete =
                    if policy.RequiredValidations.IsEmpty then
                        refuse "MRG.POLICY_UNAVAILABLE" "the protected policy requires no validation; an empty policy cannot verify anything" "add validations to the protected policy" Principal.RepositoryAdministrator
                    else
                        refuse "MRG.VALIDATION_INCOMPLETE" $"no commit has a trusted receipt for every required validation ({required})" "run the protected verification workflow on the candidate" Principal.VerificationService

                None, [], repository @ invalid @ policyMismatch @ foreignItem @ [ incomplete ]
            | (commit, (_, chosen, _, failed)) :: _ ->
                let notPassed =
                    failed
                    |> List.map (fun (name, r) -> refuse "MRG.RECEIPT_NOT_PASSED" $"validation '{name}' is {CheckStatus.code r.Payload.Status} at {commit} (receipt {r.Digest})" "fix the failure and re-verify" Principal.AgentImplementer)

                let tree =
                    match facts.VerifiedCommits |> Map.tryFind commit |> Option.bind _.TreeId with
                    | Some tree ->
                        chosen
                        |> List.filter (fun r -> r.Payload.TreeId <> tree)
                        |> List.map (fun r -> refuse "MRG.RECEIPT_FORGED" $"receipt {r.Digest} names tree {r.Payload.TreeId}, but {commit} has tree {tree}" "discard the receipt" Principal.VerificationService)
                    | None -> []

                let generations = chosen |> List.map (fun r -> r.Payload.WorkItemId, r.Payload.FencingGeneration) |> List.distinct

                let mixed =
                    if (generations |> List.map fst |> List.distinct).Length < generations.Length then
                        [ refuse "MRG.RECEIPT_STALE_GENERATION" "the receipts span more than one claim generation of a work item" "re-verify under the current claim" Principal.AgentImplementer ]
                    else []

                let artifact =
                    if commit = facts.CandidateHead then []
                    else
                        match facts.VerifiedCommits |> Map.tryFind commit with
                        | None -> [ refuse "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION" $"the verified commit {commit} is not available to compare with the candidate" "re-verify the candidate head" Principal.VerificationService ]
                        | Some rel when not rel.IsAncestorOfCandidate ->
                            [ refuse "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION" $"the verified commit {commit} is not in the candidate's history (force push or rebase after verification)" "re-verify the candidate head" Principal.VerificationService ]
                        | Some rel ->
                            match rel.ChangedSince |> List.filter (GovernancePath.isPostVerificationExempt >> not) with
                            | [] -> []
                            | changed ->
                                let shown = changed |> List.truncate 10 |> String.concat ", "
                                [ refuse "MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION" $"the candidate changed after verification at {commit}: {shown}" "re-verify the candidate head" Principal.VerificationService ]

                Some commit, chosen, repository @ invalid @ policyMismatch @ foreignItem @ notPassed @ tree @ mixed @ artifact

    let private freshness (facts: MergeFacts) =
        if facts.CandidateContainsTarget then []
        else
            [ refuse
                  "MRG.STALE_BASE"
                  $"the candidate does not contain {facts.ExpectedTarget} at {facts.TargetHead}; the merge result was never verified"
                  "update the branch from the target and re-verify"
                  Principal.AgentImplementer ]

    let decide (facts: MergeFacts) : MergeDecision =
        let basis, authorizationRefusals = authorization facts
        let protectedDescribed, approvals, protectedRefusals = protectedChanges facts

        let verifiedCommit, receipts, verificationRefusals =
            if facts.RequireVerifiedCompletion || facts.Policy.IsSome || facts.Trust.IsSome then verification facts
            else None, [], [ refuse "MRG.VERIFIED_COMPLETION_NOT_REQUIRED" "the protected configuration does not require verified completion; a merge here is never verified" "require verified completion on the protected branch" Principal.OrganizationOwner ]

        let refusals =
            identity facts @ authorizationRefusals @ protectedRefusals @ verificationRefusals @ freshness facts
            |> List.distinctBy (fun f -> f.Code, f.Detail)

        let exceptionChecks = facts.Exceptions |> List.map (fun e -> e, Authority.validateException facts.Authorities facts.Repository facts.CandidateHead facts.EvaluatedAt e)
        let validExceptions = exceptionChecks |> List.choose (fun (e, r) -> match r with Ok _ -> Some e | Error _ -> None)

        let invalidExceptions =
            exceptionChecks
            |> List.choose (fun (e, r) ->
                match r with
                | Error why -> Some(refuse "MRG.EXCEPTION_INVALID" $"exception {e.Id}: {why}" "an exception authority grants a complete, bounded exception for this candidate" Principal.ExceptionAuthority)
                | Ok _ -> None)

        let waivedCodes = validExceptions |> List.collect _.Waives |> Set.ofList |> fun codes -> Set.difference codes Authority.nonWaivable

        let waived, remaining =
            refusals |> List.partition (fun f -> waivedCodes.Contains f.Code)

        let remaining = remaining @ invalidExceptions

        let outcome =
            match remaining, waived with
            | [], [] -> MergeOutcome.Verified
            | [], _ ->
                let used = validExceptions |> List.filter (fun e -> e.Waives |> List.exists (fun c -> waived |> List.exists (fun w -> w.Code = c))) |> List.map _.Id
                MergeOutcome.AuthorizedByException used
            | _ -> MergeOutcome.Refused

        let advisories =
            [ match facts.PlatformEnforced with
              | Some true -> ()
              | Some false -> "the platform does not enforce the governance profile: this decision is advisory until the required rules are active (praxis governance inspect)"
              | None -> "platform enforcement was not assessed for this decision"
              if outcome = MergeOutcome.Verified && receipts |> List.exists (fun r -> r.Basis = AuthenticationBasis.WorkloadIdentity) then
                  "receipts are attested by a CI workload identity: they prove the protected workflow ran the protected policy, not that the tests are adequate" ]

        { Outcome = outcome
          Repository = facts.Repository
          TargetBranch = facts.TargetBranch
          TargetHead = facts.TargetHead
          CandidateHead = facts.CandidateHead
          WorkItems = facts.DeclaredWorkItems
          AuthorizationBasis = basis
          VerifiedCommit = verifiedCommit
          Receipts = receipts
          ProtectedChanges = protectedDescribed
          ElevatedApprovals = approvals
          Refusals = remaining
          Waived = waived
          Advisories = advisories
          EvaluatedAt = facts.EvaluatedAt }
