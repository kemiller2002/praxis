namespace Praxis.Domain.Governance

open System

/// A status check the platform requires, and the integration that must
/// produce it. `IntegrationId = None` means any app or token with status
/// permission can satisfy it, so a similarly named check can stand in.
type RequiredCheck = { Context: string; IntegrationId: int64 option }

[<RequireQualifiedAccess>]
type BypassActor =
    | RepositoryRole of name: string
    | OrganizationAdmin
    | Integration of id: int64
    | Team of id: int64
    | DeployKey
    | Other of kind: string

/// The rules that actually apply to the protected branch, merged from classic
/// branch protection and every active ruleset (GitHub's
/// `rules/branches/{branch}` view plus the ruleset details).
type EffectiveBranchRules =
    { RequiresPullRequest: bool
      RequiredApprovals: int
      DismissStaleApprovals: bool
      RequiredChecks: RequiredCheck list
      /// The candidate must contain the target head (`strict`): the tested
      /// result is the merge result.
      RequiresUpToDate: bool
      BlocksForcePush: bool
      BlocksDeletion: bool
      RequiresLinearHistory: bool
      UsesMergeQueue: bool
      /// Classic protection's `enforce_admins`; rulesets have no such flag
      /// and apply to administrators unless they are bypass actors.
      AdminsEnforced: bool
      BypassActors: BypassActor list
      /// Whether some ruleset's bypass list could not be read (it is visible
      /// only to administrators): bypass is then unknown, not absent.
      BypassVisible: bool }

[<RequireQualifiedAccess>]
type OwnerKind =
    | User
    | Organization

/// Everything `governance inspect` read, as typed facts.
type PlatformSnapshot =
    { Repository: string
      OwnerKind: OwnerKind
      Visibility: string
      DefaultBranch: string
      Branch: string
      CapturedAt: DateTimeOffset
      Rules: Observed<EffectiveBranchRules>
      /// Workflow files on the protected branch with their trigger names.
      Workflows: Observed<(string * string list) list>
      /// Check runs on the protected branch head: (name, app slug, conclusion).
      HeadChecks: Observed<(string * string * string) list>
      /// Tag rulesets protecting release tags.
      ReleaseTagsProtected: Observed<bool>
      /// The repository's default `GITHUB_TOKEN` permission (`read`/`write`).
      DefaultTokenPermission: Observed<string>
      /// Other protected-branch workflows that run for pull requests and can
      /// write check runs or statuses (so could post the gate's check name).
      CheckWriters: Observed<string list> }

/// The governance a repository requires of its platform. Read only from the
/// protected branch (`ros.json` `governance`), with Praxis defaults.
type GovernanceProfile =
    { RequiredChecks: RequiredCheck list
      RequirePullRequest: bool
      MinimumApprovals: int
      RequireUpToDate: bool
      BlockForcePush: bool
      BlockDeletion: bool
      AllowAdminBypass: bool
      /// The protected workflow that runs `praxis merge authorize`.
      MergeGateWorkflow: string
      RequireReleaseTagProtection: bool
      /// Every merge to the protected branch needs a verified merge-gate
      /// decision (the CI verification path); `ros.json`
      /// `workProtocol.requireVerifiedCompletion` is the host-verifier path.
      RequireVerifiedMerge: bool }

/// What Praxis's own configuration on the protected branch provides.
type PraxisConfiguration =
    { Installed: bool
      RequireVerifiedCompletion: bool
      TrustRoot: VerifierTrustRoot option
      TrustRootError: string option
      ValidationPolicyPresent: bool
      GovernanceProfileDeclared: bool
      /// The protected `ros.json` governance block is malformed (defaults
      /// are then not silently assumed to be the owner's intent).
      ProfileError: string option }

[<RequireQualifiedAccess>]
type ControlStatus =
    | Enforced
    | NotEnforced
    /// The provider would not show the rule; Praxis cannot show it holds.
    | Unknown
    /// The platform cannot provide it here (plan or account type).
    | PlatformUnavailable
    | NotRequired

[<RequireQualifiedAccess>]
module ControlStatus =
    let toWire status =
        match status with
        | ControlStatus.Enforced -> "enforced"
        | ControlStatus.NotEnforced -> "not-enforced"
        | ControlStatus.Unknown -> "unknown"
        | ControlStatus.PlatformUnavailable -> "platform-unavailable"
        | ControlStatus.NotRequired -> "not-required"

    /// Only `Enforced` and `NotRequired` count as satisfied.
    let isSatisfied status = status = ControlStatus.Enforced || status = ControlStatus.NotRequired

type ControlResult =
    { Id: string
      Title: string
      Status: ControlStatus
      Detail: string
      Remedy: string
      ResolvedBy: Principal }

/// The installation readiness ladder (brief §19). Each rung requires the
/// previous one, so a repository is never reported as governed merely
/// because Praxis files exist.
[<RequireQualifiedAccess>]
type ReadinessLevel =
    | NotInstalled
    | Installed
    | Configured
    | Validated
    | PlatformEnforced
    | VerifiedCompletionEnabled
    | OperationallyDemonstrated

[<RequireQualifiedAccess>]
module ReadinessLevel =
    let ordered =
        [ ReadinessLevel.NotInstalled
          ReadinessLevel.Installed
          ReadinessLevel.Configured
          ReadinessLevel.Validated
          ReadinessLevel.PlatformEnforced
          ReadinessLevel.VerifiedCompletionEnabled
          ReadinessLevel.OperationallyDemonstrated ]

    let toWire level =
        match level with
        | ReadinessLevel.NotInstalled -> "not-installed"
        | ReadinessLevel.Installed -> "installed"
        | ReadinessLevel.Configured -> "configured"
        | ReadinessLevel.Validated -> "validated"
        | ReadinessLevel.PlatformEnforced -> "platform-enforced"
        | ReadinessLevel.VerifiedCompletionEnabled -> "verified-completion-enabled"
        | ReadinessLevel.OperationallyDemonstrated -> "operationally-demonstrated"

type ReadinessRung = { Level: ReadinessLevel; Reached: bool; Blockers: string list }

type GovernanceReport =
    { Repository: string
      Branch: string
      CapturedAt: DateTimeOffset
      Controls: ControlResult list
      Ladder: ReadinessRung list
      Level: ReadinessLevel
      /// Limits stated so the report never over-claims.
      ResidualTrustBoundaries: string list }

[<RequireQualifiedAccess>]
module PlatformGovernance =
    /// GitHub Actions' integration (app) ID, which produces workflow checks.
    [<Literal>]
    let GitHubActionsIntegrationId = 15368L

    let defaultProfile =
        { RequiredChecks = [ { Context = "praxis-merge-gate"; IntegrationId = Some GitHubActionsIntegrationId } ]
          RequirePullRequest = true
          MinimumApprovals = 0
          RequireUpToDate = true
          BlockForcePush = true
          BlockDeletion = true
          AllowAdminBypass = false
          MergeGateWorkflow = ".github/workflows/praxis-merge-gate.yml"
          RequireReleaseTagProtection = false
          RequireVerifiedMerge = false }

    let private control id title status detail remedy who =
        { Id = id; Title = title; Status = status; Detail = detail; Remedy = remedy; ResolvedBy = who }

    let private ruleset = "the default-branch ruleset (import .praxis/governance/ruleset-main.json, written by `praxis governance bootstrap`, under Settings > Rules > Rulesets)"

    let private ruleControls (profile: GovernanceProfile) (snapshot: PlatformSnapshot) =
        match snapshot.Rules with
        | Observed.Unknown reason when reason.Contains("Upgrade to GitHub Pro", StringComparison.OrdinalIgnoreCase) ->
            [ control
                  "GOV-RULES"
                  "Branch rules are available"
                  ControlStatus.PlatformUnavailable
                  $"the platform does not enforce branch rules on this repository under its current plan ({reason})"
                  "make the repository public, or decide on a plan that enforces rules on private repositories (no purchase is implied)"
                  Principal.OrganizationOwner
              control "GOV-PR" "Pull request required" ControlStatus.NotEnforced "branch rules are unavailable, so nothing is enforced" "" Principal.OrganizationOwner ]
        | Observed.Unknown reason ->
            [ control
                  "GOV-RULES"
                  "Branch rules are readable"
                  ControlStatus.Unknown
                  $"the branch rules could not be read: {reason}"
                  "inspect with a credential that can read branch protection and rulesets (repository administration: read)"
                  Principal.RepositoryAdministrator ]
        | Observed.Known rules ->
            let anything = rules.RequiresPullRequest || not rules.RequiredChecks.IsEmpty || rules.BlocksForcePush || rules.BlocksDeletion

            let flag id title required actual detail =
                if not required then control id title ControlStatus.NotRequired detail "" Principal.RepositoryAdministrator
                elif actual then control id title ControlStatus.Enforced detail "" Principal.RepositoryAdministrator
                else control id title ControlStatus.NotEnforced $"not enforced, so it does not hold that {detail}" $"enable it in {ruleset}" Principal.RepositoryAdministrator

            let checks =
                profile.RequiredChecks
                |> List.map (fun required ->
                    match rules.RequiredChecks |> List.tryFind (fun c -> c.Context = required.Context) with
                    | None ->
                        control
                            $"GOV-CHECK:{required.Context}"
                            $"Required check '{required.Context}'"
                            ControlStatus.NotEnforced
                            "the platform does not require this check; a merge can proceed without it"
                            $"add '{required.Context}' as a required status check in {ruleset}"
                            Principal.RepositoryAdministrator
                    | Some actual ->
                        match required.IntegrationId, actual.IntegrationId with
                        | Some expected, Some observed when expected = observed ->
                            control $"GOV-CHECK:{required.Context}" $"Required check '{required.Context}'" ControlStatus.Enforced $"required and pinned to integration {observed}" "" Principal.RepositoryAdministrator
                        | Some expected, observed ->
                            let actualText = observed |> Option.map string |> Option.defaultValue "any source"
                            control
                                $"GOV-CHECK:{required.Context}"
                                $"Required check '{required.Context}'"
                                ControlStatus.NotEnforced
                                $"required but accepted from {actualText}, not integration {expected}: another app or token could post a check of the same name"
                                $"pin the check's source to GitHub Actions (integration {expected}) in {ruleset}"
                                Principal.RepositoryAdministrator
                        | None, _ ->
                            control $"GOV-CHECK:{required.Context}" $"Required check '{required.Context}'" ControlStatus.Enforced "required" "" Principal.RepositoryAdministrator)

            let bypass =
                let admins = rules.BypassActors |> List.isEmpty |> not

                if profile.AllowAdminBypass then
                    control "GOV-BYPASS" "No administrative bypass" ControlStatus.NotRequired "the profile allows bypass" "" Principal.RepositoryAdministrator
                elif not anything then
                    control "GOV-BYPASS" "No administrative bypass" ControlStatus.NotEnforced "no rule applies to the branch, so there is nothing to bypass: every writer can do anything" $"create {ruleset}" Principal.RepositoryAdministrator
                elif not rules.BypassVisible then
                    control "GOV-BYPASS" "No administrative bypass" ControlStatus.Unknown "a ruleset's bypass list is not visible to this credential" "inspect with an administrator credential" Principal.RepositoryAdministrator
                elif admins then
                    let actors = rules.BypassActors |> List.map (sprintf "%A") |> String.concat ", "
                    control "GOV-BYPASS" "No administrative bypass" ControlStatus.NotEnforced $"bypass actors: {actors}" "remove bypass actors from the default-branch ruleset; use a recorded governance exception instead" Principal.RepositoryAdministrator
                else
                    control "GOV-BYPASS" "No administrative bypass" ControlStatus.Enforced "rulesets apply to administrators (no bypass actors)" "" Principal.RepositoryAdministrator

            [ flag "GOV-PR" "Pull request required" profile.RequirePullRequest rules.RequiresPullRequest "direct pushes to the protected branch are refused"
              flag "GOV-UP-TO-DATE" "Candidate must contain the target head" profile.RequireUpToDate rules.RequiresUpToDate "the merged result is the tested result"
              flag "GOV-FORCE-PUSH" "Force push blocked" profile.BlockForcePush rules.BlocksForcePush "history on the protected branch cannot be rewritten"
              flag "GOV-DELETION" "Branch deletion blocked" profile.BlockDeletion rules.BlocksDeletion "the protected branch cannot be deleted"
              (if profile.MinimumApprovals <= 0 then
                   control "GOV-APPROVALS" "Required approvals" ControlStatus.NotRequired "routine work is authorized by standing policy, not per-change approval" "" Principal.RepositoryAdministrator
               elif rules.RequiredApprovals >= profile.MinimumApprovals then
                   control "GOV-APPROVALS" "Required approvals" ControlStatus.Enforced $"{rules.RequiredApprovals} approval(s) required" "" Principal.RepositoryAdministrator
               else
                   control "GOV-APPROVALS" "Required approvals" ControlStatus.NotEnforced $"{rules.RequiredApprovals} of {profile.MinimumApprovals} approval(s) required" $"raise required approvals in {ruleset}" Principal.RepositoryAdministrator)
              bypass ]
            @ checks

    let private workflowControl (profile: GovernanceProfile) (snapshot: PlatformSnapshot) =
        match snapshot.Workflows with
        | Observed.Unknown reason ->
            control "GOV-GATE-WORKFLOW" "Merge gate runs from the protected branch" ControlStatus.Unknown reason "" Principal.RepositoryAdministrator
        | Observed.Known workflows ->
            match workflows |> List.tryFind (fun (path, _) -> path = profile.MergeGateWorkflow) with
            | None ->
                control
                    "GOV-GATE-WORKFLOW"
                    "Merge gate runs from the protected branch"
                    ControlStatus.NotEnforced
                    $"'{profile.MergeGateWorkflow}' is not on the protected branch"
                    "merge the workflow `praxis governance bootstrap` writes, through a reviewed change"
                    Principal.RepositoryAdministrator
            | Some(_, triggers) when triggers |> List.exists (fun t -> t <> "pull_request_target" && t <> "merge_group") ->
                let names = String.concat ", " triggers
                control
                    "GOV-GATE-WORKFLOW"
                    "Merge gate runs from the protected branch"
                    ControlStatus.NotEnforced
                    $"the gate also runs on {names}; a candidate can redefine a workflow under those triggers and post the gate's check itself"
                    "trigger the gate only on pull_request_target (and merge_group)"
                    Principal.RepositoryAdministrator
            | Some(_, triggers) when not (List.contains "pull_request_target" triggers || List.contains "merge_group" triggers) ->
                control "GOV-GATE-WORKFLOW" "Merge gate runs from the protected branch" ControlStatus.NotEnforced "the gate never runs for pull requests" "trigger the gate on pull_request_target" Principal.RepositoryAdministrator
            | Some _ ->
                control "GOV-GATE-WORKFLOW" "Merge gate runs from the protected branch" ControlStatus.Enforced "the gate's definition is read from the protected branch" "" Principal.RepositoryAdministrator

    let private tokenControls (profile: GovernanceProfile) (snapshot: PlatformSnapshot) =
        let gate = profile.RequiredChecks |> List.map _.Context |> String.concat ", "

        [ (match snapshot.OwnerKind with
           | OwnerKind.User ->
               control
                   "GOV-GATE-EXCLUSIVE"
                   "Only the protected gate can produce the required check"
                   ControlStatus.PlatformUnavailable
                   $"any Actions job named {gate} in a workflow the candidate adds produces a same-named check from GitHub Actions; personal accounts have no required-workflow rule to prevent it"
                   "give agent identities no workflow-write permission (they then cannot add workflows); `governance audit` reports gate checks from any other workflow (AUD.GATE_SOURCE_UNTRUSTED); an organization account can require the workflow itself"
                   Principal.OrganizationOwner
           | OwnerKind.Organization ->
               control
                   "GOV-GATE-EXCLUSIVE"
                   "Only the protected gate can produce the required check"
                   ControlStatus.Unknown
                   $"a same-named job in a candidate-added workflow can produce {gate}; Praxis does not read organization required-workflow rulesets"
                   "require the merge-gate workflow through an organization ruleset; give agents no workflow-write permission"
                   Principal.OrganizationOwner)
          (match snapshot.DefaultTokenPermission with
           | Observed.Known "read" -> control "GOV-DEFAULT-TOKEN" "Workflow tokens are read-only by default" ControlStatus.Enforced "the default GITHUB_TOKEN permission is read" "" Principal.RepositoryAdministrator
           | Observed.Known other ->
               control
                   "GOV-DEFAULT-TOKEN"
                   "Workflow tokens are read-only by default"
                   ControlStatus.NotEnforced
                   $"the default GITHUB_TOKEN permission is '{other}': candidate code in any pull-request workflow without a permissions block can post a check named {gate}"
                   "Settings > Actions > General > Workflow permissions: read repository contents"
                   Principal.RepositoryAdministrator
           | Observed.Unknown reason -> control "GOV-DEFAULT-TOKEN" "Workflow tokens are read-only by default" ControlStatus.Unknown reason "" Principal.RepositoryAdministrator)
          (match snapshot.CheckWriters with
           | Observed.Known [] -> control "GOV-CHECK-WRITERS" "No other pull-request workflow can write checks" ControlStatus.Enforced "no other pull-request workflow requests checks, statuses or write-all permission" "" Principal.RepositoryAdministrator
           | Observed.Known writers ->
               let names = String.concat ", " writers
               control "GOV-CHECK-WRITERS" "No other pull-request workflow can write checks" ControlStatus.NotEnforced $"{names} run for pull requests with permission to write checks or statuses: they could post {gate}" "remove checks/statuses write permission from pull-request workflows" Principal.RepositoryAdministrator
           | Observed.Unknown reason -> control "GOV-CHECK-WRITERS" "No other pull-request workflow can write checks" ControlStatus.Unknown reason "" Principal.RepositoryAdministrator) ]

    let private praxisControls (praxis: PraxisConfiguration) =
        let trust =
            match praxis.TrustRoot, praxis.TrustRootError with
            | _, Some error ->
                control "GOV-TRUST-ROOT" "Completion authority trust root" ControlStatus.NotEnforced $"the protected trust root is unusable: {error}" "fix .praxis/trust/verifiers.json through an elevated change (docs/governance/bootstrap.md)" Principal.RepositoryAdministrator
            | None, None ->
                control
                    "GOV-TRUST-ROOT"
                    "Completion authority trust root"
                    ControlStatus.NotEnforced
                    "no completion authority is provisioned: .praxis/trust/verifiers.json is absent on the protected branch"
                    "run the bootstrap in docs/governance/bootstrap.md (the owner merges one generated change; no key is created or held by anyone)"
                    Principal.OrganizationOwner
            | Some root, None when root.Verifiers |> List.forall (fun v -> v.RevokedAt.IsSome) ->
                control "GOV-TRUST-ROOT" "Completion authority trust root" ControlStatus.NotEnforced "every pinned verifier is revoked" "provision a new verifier (docs/governance/bootstrap.md, rotation)" Principal.OrganizationOwner
            | Some root, None ->
                control "GOV-TRUST-ROOT" "Completion authority trust root" ControlStatus.Enforced $"configVersion {root.ConfigVersion}, {root.Verifiers.Length} verifier(s), approved by {root.ApprovedBy}" "" Principal.OrganizationOwner

        [ match praxis.ProfileError with
          | Some e -> control "GOV-PROFILE" "Governance profile is readable" ControlStatus.NotEnforced $"the protected governance profile is malformed: {e}" "fix ros.json governance through an elevated change" Principal.RepositoryAdministrator
          | None -> ()
          (if praxis.RequireVerifiedCompletion then
               control "GOV-VERIFIED-COMPLETION" "Verified completion required" ControlStatus.Enforced "verified completion (workProtocol.requireVerifiedCompletion or governance.requireVerifiedMerge) is required on the protected branch" "" Principal.RepositoryAdministrator
           else
               control "GOV-VERIFIED-COMPLETION" "Verified completion required" ControlStatus.NotEnforced "the protected ros.json requires neither verified merges nor verified completion" "run `praxis governance bootstrap`, which sets governance.requireVerifiedMerge (the CI path); workProtocol.requireVerifiedCompletion is the host-verifier path" Principal.OrganizationOwner)
          trust
          (if praxis.ValidationPolicyPresent then
               control "GOV-VALIDATION-POLICY" "Protected validation policy" ControlStatus.Enforced "the validation policy is read from the protected branch" "" Principal.RepositoryAdministrator
           else
               control "GOV-VALIDATION-POLICY" "Protected validation policy" ControlStatus.NotEnforced "no validation policy on the protected branch" "run `praxis governance bootstrap` (it writes the policy), or add .praxis/validation-policy.json through an elevated change" Principal.OrganizationOwner) ]

    let private releaseControl (profile: GovernanceProfile) (snapshot: PlatformSnapshot) =
        match profile.RequireReleaseTagProtection, snapshot.ReleaseTagsProtected with
        | false, _ -> control "GOV-RELEASE-TAGS" "Release tags protected" ControlStatus.NotRequired "" "" Principal.ReleaseAuthority
        | true, Observed.Known true -> control "GOV-RELEASE-TAGS" "Release tags protected" ControlStatus.Enforced "a tag ruleset protects release tags" "" Principal.ReleaseAuthority
        | true, Observed.Known false -> control "GOV-RELEASE-TAGS" "Release tags protected" ControlStatus.NotEnforced "release tags can be created, moved or deleted by any writer" "add a tag ruleset for release tags" Principal.ReleaseAuthority
        | true, Observed.Unknown reason -> control "GOV-RELEASE-TAGS" "Release tags protected" ControlStatus.Unknown reason "" Principal.ReleaseAuthority

    let private auditControl (snapshot: PlatformSnapshot) =
        match snapshot.OwnerKind with
        | OwnerKind.User ->
            control
                "GOV-AUDIT-LOG"
                "Platform audit log"
                ControlStatus.PlatformUnavailable
                "personal accounts have no organization audit log; bypass and settings changes cannot be read from the provider"
                "compensate with `praxis governance audit` (history) and scheduled `praxis governance drift`; an organization account provides the audit log"
                Principal.OrganizationOwner
        | OwnerKind.Organization ->
            control "GOV-AUDIT-LOG" "Platform audit log" ControlStatus.Enforced "the organization audit log is available to its owners" "" Principal.OrganizationOwner

    let residualBoundaries (snapshot: PlatformSnapshot) =
        [ "A principal with administrative rights on the repository can change or remove these rules; Praxis detects the change afterwards (drift, audit) and cannot prevent it."
          "Agents that push with the owner's platform identity act as the owner to the platform: a platform approval by that identity is not separation of duties."
          "An agent credential with workflow-write permission can add a workflow whose job is named like the gate, producing a same-named check from GitHub Actions; grant agent identities no workflow permission (audit detects such checks afterwards)."
          "Validation results are only as trustworthy as the isolation of the job that runs candidate code: the gate removes sudo and Docker access first, but a kernel-level escape is outside what Praxis controls."
          if snapshot.OwnerKind = OwnerKind.User then
              "Personal-account repositories cannot restrict who may push or merge beyond rulesets, and have no audit log." ]

    let private rung level reached blockers = { Level = level; Reached = reached; Blockers = blockers }

    let evaluate (profile: GovernanceProfile) (praxis: PraxisConfiguration) (demonstrated: bool * string list) (snapshot: PlatformSnapshot) =
        let platform = ruleControls profile snapshot @ [ workflowControl profile snapshot ] @ tokenControls profile snapshot @ [ releaseControl profile snapshot; auditControl snapshot ]
        let own = praxisControls praxis
        let controls = platform @ own

        let failing (results: ControlResult list) =
            results
            |> List.filter (fun c -> not (ControlStatus.isSatisfied c.Status) && c.Status <> ControlStatus.PlatformUnavailable)
            |> List.map (fun c -> $"{c.Id}: {c.Detail}")

        let validated =
            match snapshot.HeadChecks with
            | Observed.Unknown reason -> false, [ $"the protected branch head's checks are unknown: {reason}" ]
            | Observed.Known checks ->
                // The gate runs on pull-request heads, never on the protected
                // head itself: here "validated" means the protected state's own
                // CI (GitHub Actions) passed and nothing failed.
                let actions = checks |> List.filter (fun (_, app, _) -> app = "github-actions")
                let failed = actions |> List.filter (fun (_, _, c) -> c <> "success" && c <> "skipped" && c <> "neutral") |> List.map (fun (name, _, c) -> $"'{name}' concluded {c} on the protected branch head")

                if actions.IsEmpty then false, [ "no GitHub Actions check ran on the protected branch head" ]
                else failed.IsEmpty, failed

        let installed = praxis.Installed, (if praxis.Installed then [] else [ "Praxis is not installed on the protected branch" ])

        let configured =
            let blockers =
                [ if not praxis.GovernanceProfileDeclared then "ros.json declares no governance profile (Praxis defaults apply)"
                  if not praxis.ValidationPolicyPresent then "no protected validation policy" ]

            blockers.IsEmpty, blockers

        let enforced = let f = failing platform in f.IsEmpty, f
        let verified = let f = failing own in f.IsEmpty, f

        let steps =
            [ ReadinessLevel.Installed, installed
              ReadinessLevel.Configured, configured
              ReadinessLevel.Validated, validated
              ReadinessLevel.PlatformEnforced, enforced
              ReadinessLevel.VerifiedCompletionEnabled, verified
              ReadinessLevel.OperationallyDemonstrated, demonstrated ]

        let ladder, level =
            steps
            |> List.fold
                (fun (rungs, level, open') (lvl, (ok, blockers)) ->
                    let reached = open' && ok
                    let blockers = if open' || ok then blockers else blockers @ [ "a lower readiness level is not reached" ]
                    rungs @ [ rung lvl reached blockers ], (if reached then lvl else level), reached)
                ([], ReadinessLevel.NotInstalled, true)
            |> fun (rungs, level, _) -> rungs, level

        { Repository = snapshot.Repository
          Branch = snapshot.Branch
          CapturedAt = snapshot.CapturedAt
          Controls = controls
          Ladder = ladder
          Level = level
          ResidualTrustBoundaries = residualBoundaries snapshot }

    /// Governance drift: every control that was satisfied in `baseline` and
    /// is not now. Improvements are not drift.
    let drift (baseline: GovernanceReport) (current: GovernanceReport) =
        let before = baseline.Controls |> List.map (fun c -> c.Id, c) |> Map.ofList

        current.Controls
        |> List.choose (fun now ->
            match before |> Map.tryFind now.Id with
            | Some was when
                (ControlStatus.isSatisfied was.Status && not (ControlStatus.isSatisfied now.Status))
                || (was.Status = ControlStatus.Enforced && now.Status = ControlStatus.NotRequired) ->
                Some(GovernanceFinding.create "GOV.DRIFT" $"{now.Id} was {ControlStatus.toWire was.Status} and is now {ControlStatus.toWire now.Status}: {now.Detail}" now.Remedy now.ResolvedBy)
            | _ -> None)
        |> fun weakened ->
            let removed =
                baseline.Controls
                |> List.filter (fun was -> ControlStatus.isSatisfied was.Status && not (current.Controls |> List.exists (fun c -> c.Id = was.Id)))
                |> List.map (fun was -> GovernanceFinding.create "GOV.DRIFT" $"{was.Id} is no longer evaluated" "restore the governance profile on the protected branch" Principal.RepositoryAdministrator)

            weakened @ removed
