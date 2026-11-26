namespace Praxis.Infrastructure.Governance

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Domain.Governance
open Praxis.Domain.Work

/// Everything governance reads from the protected branch. Each member keeps
/// its own error, so one unreadable document fails closed where it is used
/// without hiding the rest.
type ProtectedState =
    { Ref: string
      Head: string
      Ros: Result<GovernanceJson.RosFacts, string> option
      Profile: GovernanceProfile * bool
      ProfileError: string option
      Trust: Result<VerifierTrustRoot, string> option
      Authorities: GovernanceAuthorities
      AuthoritiesError: string option
      Standing: StandingAuthorization list
      Policy: Result<ProtectedPolicy, string> option
      Exceptions: (string * Result<GovernanceException, string>) list
      Workflows: (string * string list) list
      /// Pull-request workflows (other than the gate) that may write checks.
      WorkflowWriters: (string * string list * bool) list
      WorkflowTexts: string list
      /// Work items a work authorizer authorized on the protected branch
      /// (`.praxis/governance/work-authorizations.json`). `.ros/` state is
      /// not authorization: any change can add to it.
      AuthorizedWorkItems: string list }

type AuthorizeRequest =
    { Root: string
      Repository: string
      BaseRef: string
      Candidate: string
      PullRequest: int option
      WorkItems: string list
      ReceiptDirectories: string list
      JwksFile: string option
      GovernanceReport: string option
      Live: bool }

type AuditRequest =
    { Root: string
      Repository: string option
      BranchRef: string
      Limit: int
      Since: string option
      PreviousHead: string option
      DecisionDirectory: string option
      GateCheck: string }

[<RequireQualifiedAccess>]
module GovernanceOperations =
    let TrustRootPath = ".praxis/trust/verifiers.json"
    let AuthoritiesPath = ".praxis/governance/authorities.json"
    let ExceptionsPrefix = ".praxis/governance/exceptions"
    let StandingPath = ".praxis/standing-authorization.json"
    let PolicyPath = ".praxis/validation-policy.json"
    let ReceiptsPrefix = ".praxis/receipts"
    let WorkAuthorizationsPath = ".praxis/governance/work-authorizations.json"
    let MergeGatePath = ".github/workflows/praxis-merge-gate.yml"

    /// The branch name a ref names: `origin/release/1.x` -> `release/1.x`.
    let branchOf (reference: string) =
        [ "refs/remotes/origin/"; "refs/heads/"; "origin/" ]
        |> List.tryFind (fun prefix -> reference.StartsWith(prefix, StringComparison.Ordinal))
        |> Option.map (fun prefix -> reference.Substring prefix.Length)
        |> Option.defaultValue reference

    /// Whether a workflow could write check runs or statuses: an explicit
    /// write permission, `write-all`, or no permissions block at all (then
    /// the repository default applies; GOV-DEFAULT-TOKEN reports it).
    let writesChecks (text: string) =
        text.Contains "checks: write" || text.Contains "statuses: write" || text.Contains "write-all" || not (text.Contains "permissions:")

    let private now () = DateTimeOffset.UtcNow

    /// The `on:` triggers of a workflow, read line by line. Unknown shapes
    /// yield a sentinel trigger, which the gate control treats as unsafe.
    let workflowTriggers (text: string) =
        let lines = text.Replace("\r", "").Split('\n') |> Array.toList
        let isOn (line: string) = line = "on:" || line.StartsWith "on: " || line = "\"on\":" || line = "'on':" || line = "true:"

        match lines |> List.tryFindIndex (fun l -> isOn (l.TrimEnd())) with
        | None -> [ "unparsed" ]
        | Some index ->
            let line = lines[index].TrimEnd()
            let inline' = line.Substring(line.IndexOf ':' + 1).Trim()

            if inline' <> "" then
                inline'.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun t -> t.Trim().Trim('"', '\'')) |> Array.toList
            else
                let body = lines |> List.skip (index + 1) |> List.takeWhile (fun l -> l.Trim() = "" || l.StartsWith " " || l.StartsWith "\t" || l.TrimStart().StartsWith "#")
                let content = body |> List.filter (fun l -> l.Trim() <> "" && not (l.TrimStart().StartsWith "#"))

                match content with
                | [] -> [ "unparsed" ]
                | first :: _ ->
                    let indent = first.Length - first.TrimStart().Length

                    content
                    |> List.filter (fun l -> l.Length - l.TrimStart().Length = indent)
                    |> List.map (fun l ->
                        let key = l.Trim().TrimStart('-').Trim().Split(':')
                        key[0].Trim().Trim('"', '\''))

    let private terminal = set [ "complete"; "abandoned"; "cancelled"; "superseded"; "withdrawn" ]

    /// Work items that are open (authorized and not finished) in a Praxis
    /// state document: the live context or the backlog queue.
    let openWorkItems (contextJson: string option) (queueJson: string option) =
        let read (text: string option) (collection: string) (stateName: string) =
            match text with
            | None -> []
            | Some text ->
                try
                    match JsonNode.Parse(text)[collection] with
                    | :? JsonArray as items ->
                        items
                        |> Seq.choose (fun item ->
                            let id = match item["id"] with null -> None | v -> Some(v.ToString())
                            let state = match item[stateName] with null -> "" | v -> v.ToString()
                            match id with
                            | Some id when not (terminal.Contains state) && state <> "captured" -> Some id
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []
                with _ -> []

        read contextJson "workItems" "state" @ read queueJson "items" "status" |> List.distinct

    /// Work items whose state is not terminal: (id, state).
    let workItemStates (contextJson: string option) =
        match contextJson with
        | None -> Map.empty
        | Some text ->
            try
                match JsonNode.Parse(text)["workItems"] with
                | :? JsonArray as items ->
                    items |> Seq.choose (fun i -> match i["id"], i["state"] with null, _ | _, null -> None | id, s -> Some(id.ToString(), s.ToString())) |> Map.ofSeq
                | _ -> Map.empty
            with _ -> Map.empty

    let private show root rev path = GovernanceGit.show root rev path |> Result.defaultValue None

    let loadProtected (root: string) (baseRef: string) : Result<ProtectedState, string> =
        GovernanceGit.resolve root baseRef
        |> Result.map (fun head ->
            let read = show root head
            let ros = read "ros.json" |> Option.orElse (read ".echelon/ros.json")
            let profileResult = ros |> Option.map GovernanceJson.parseProfile |> Option.defaultValue (Ok(PlatformGovernance.defaultProfile, false))
            let profile = profileResult |> Result.defaultValue (PlatformGovernance.defaultProfile, false)

            let authorities, authoritiesError =
                match read AuthoritiesPath with
                | None -> GovernanceJson.noAuthorities, None
                | Some text ->
                    match GovernanceJson.parseAuthorities text with
                    | Ok a -> a, None
                    | Error e -> GovernanceJson.noAuthorities, Some e

            let exceptions =
                GovernanceGit.listFiles root head ExceptionsPrefix
                |> Result.defaultValue []
                |> List.map (fun path -> path, (read path |> Option.map GovernanceJson.parseException |> Option.defaultValue (Error "unreadable")))

            let workflowPaths =
                GovernanceGit.listFiles root head ".github/workflows" |> Result.defaultValue [] |> List.filter (fun p -> p.EndsWith ".yml" || p.EndsWith ".yaml")

            let workflowTexts = workflowPaths |> List.map (fun p -> p, read p |> Option.defaultValue "")

            let authorizedWork =
                match read WorkAuthorizationsPath with
                | None -> []
                | Some text ->
                    try
                        match JsonNode.Parse(text)["authorizations"] with
                        | :? JsonArray as items ->
                            items
                            |> Seq.choose (fun item ->
                                let authorization: WorkAuthorization =
                                    { Authorizer = (match item["authorizer"] with null -> "" | v -> v.ToString())
                                      WorkItem = (match item["workItem"] with null -> "" | v -> v.ToString())
                                      AuthorizedAt = DateTimeOffset.MinValue
                                      ViaApplication = None
                                      Edited = false }

                                if authorization.WorkItem <> "" && Authority.workAuthorized authorities authorization then Some authorization.WorkItem else None)
                            |> Seq.toList
                        | _ -> []
                    with _ -> []

            { Ref = baseRef
              Head = head
              Ros = ros |> Option.map GovernanceJson.parseRos
              Profile = profile
              ProfileError = (match profileResult with Error e -> Some e | Ok _ -> None)
              Trust = read TrustRootPath |> Option.map GovernanceJson.parseTrustRoot
              Authorities = authorities
              AuthoritiesError = authoritiesError
              Standing = read StandingPath |> Option.map GovernanceJson.parseStanding |> Option.map (Result.defaultValue []) |> Option.defaultValue []
              Policy =
                GovernanceGit.show root head PolicyPath
                |> Result.defaultValue None
                |> Option.map (fun text -> GovernanceJson.parsePolicy (Encoding.UTF8.GetBytes text))
              Exceptions = exceptions
              Workflows = workflowTexts |> List.map (fun (p, t) -> p, workflowTriggers t)
              WorkflowWriters = workflowTexts |> List.map (fun (p, t) -> p, workflowTriggers t, writesChecks t)
              WorkflowTexts = workflowTexts |> List.map snd
              AuthorizedWorkItems = authorizedWork })

    let praxisConfiguration (state: ProtectedState) =
        { Installed = state.Ros.IsSome
          RequireVerifiedCompletion =
            (fst state.Profile).RequireVerifiedMerge
            || (match state.Ros with
                | Some(Ok ros) -> ros.RequireVerifiedCompletion
                | _ -> false)
          TrustRoot = (match state.Trust with Some(Ok t) -> Some t | _ -> None)
          TrustRootError = (match state.Trust with Some(Error e) -> Some e | _ -> None)
          ValidationPolicyPresent = (match state.Policy with Some(Ok _) -> true | _ -> false)
          GovernanceProfileDeclared = snd state.Profile
          ProfileError = state.ProfileError }

    // ---- inspect and drift --------------------------------------------------

    let snapshot (root: string) (repository: string) (baseRef: string) (branch: string option) =
        let workflows = loadProtected root baseRef |> Result.map _.WorkflowWriters
        GitHubGovernance.capture repository branch workflows (now ())

    let inspect (root: string) (baseRef: string) (raw: JsonNode) (demonstrated: bool * string list) =
        GitHubGovernance.toPlatform raw
        |> Result.bind (fun platform ->
            loadProtected root baseRef
            |> Result.map (fun state -> PlatformGovernance.evaluate (fst state.Profile) (praxisConfiguration state) demonstrated platform))

    // ---- receipts -----------------------------------------------------------

    let private receiptTexts (root: string) (candidate: string) (directories: string list) =
        let committed =
            GovernanceGit.listFiles root candidate ReceiptsPrefix
            |> Result.defaultValue []
            |> List.filter (fun p -> p.EndsWith ".json")
            |> List.choose (fun p -> show root candidate p |> Option.map (fun t -> p, t))

        let supplied =
            directories
            |> List.collect (fun d -> if Directory.Exists d then Directory.GetFiles(d, "*.json", SearchOption.AllDirectories) |> Array.toList else [])
            |> List.sort
            |> List.map (fun p -> p, File.ReadAllText p)

        committed @ supplied

    let private verifyReceipts (trust: VerifierTrustRoot option) (jwks: Lazy<Result<VerifierTrust.IssuerKey list, string>>) (texts: (string * string) list) =
        texts
        |> List.map (fun (source, text) ->
            let check =
                match trust with
                | None -> Error(GovernanceFinding.create "GOV.RECEIPT_UNTRUSTED_VERIFIER" "no trust root" "" Principal.OrganizationOwner)
                | Some trust ->
                    match GovernanceJson.parseReceiptDocument text with
                    | Error message -> Error(GovernanceFinding.create "GOV.RECEIPT_MALFORMED" message "" Principal.VerificationService)
                    | Ok(GovernanceJson.ReceiptDocument.Signed envelope) -> VerifierTrust.verifyEnvelope trust envelope
                    | Ok(GovernanceJson.ReceiptDocument.Workload receipt) ->
                        match jwks.Force() with
                        | Error message -> Error(GovernanceFinding.create "GOV.RECEIPT_UNTRUSTED_VERIFIER" $"issuer keys unavailable: {message}" "retry when the issuer is reachable, or pass --jwks" Principal.VerificationService)
                        | Ok keys -> VerifierTrust.verifyWorkload trust keys receipt

            { Source = source; Check = check })

    let declaredIn (text: string) =
        text.Replace("\r", "").Split('\n')
        |> Array.choose (fun line ->
            let t = line.Trim()
            let prefix = "Praxis-Work-Item:"
            if t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then Some(t.Substring(prefix.Length).Trim()) else None)
        |> Array.collect (fun v -> v.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries))
        |> Array.toList

    // ---- merge authorization ------------------------------------------------

    let authorize (request: AuthorizeRequest) : Result<MergeDecision, string> =
        let root = request.Root

        loadProtected root request.BaseRef
        |> Result.bind (fun state ->
            GovernanceGit.resolve root request.Candidate
            |> Result.bind (fun candidate ->
                GovernanceGit.isAncestor root state.Head candidate
                |> Result.bind (fun containsTarget ->
                    GovernanceGit.candidateChanges root state.Head candidate
                    |> Result.bind (fun changes ->
                        GovernanceGit.listFiles root state.Head ""
                        |> Result.map (fun tracked ->
                            let platform (what: string) (result: Result<'a, string>) =
                                match result with
                                | Ok value -> Some value, []
                                | Error e ->
                                    None,
                                    [ GovernanceFinding.create "MRG.PLATFORM_UNAVAILABLE" $"{what} could not be read: {e}" "re-run the gate when the platform answers" Principal.HostingProvider ]

                            let live f = match request.Live, request.PullRequest with | true, Some number -> Some(f number) | _ -> None
                            let pull, pullErrors = match live (GitHubGovernance.getPull request.Repository) with Some r -> platform "the pull request" r | None -> None, []
                            let approvals, approvalErrors = match live (GitHubGovernance.elevatedApprovals request.Repository) with Some r -> platform "elevated approvals" r | None -> Some [], []
                            let commented, authorizationErrors = match live (GitHubGovernance.workAuthorizations request.Repository) with Some r -> platform "work authorizations" r | None -> Some [], []

                            let declared = (request.WorkItems @ (pull |> Option.map (fun p -> declaredIn p.Body) |> Option.defaultValue [])) |> List.distinct

                            let authorizedByComment =
                                commented |> Option.defaultValue [] |> List.filter (Authority.workAuthorized state.Authorities) |> List.map _.WorkItem

                            let trust = match state.Trust with Some(Ok t) -> Some t | _ -> None

                            let jwks =
                                lazy
                                    (match request.JwksFile with
                                     | Some path -> (try GovernanceJson.parseJwks (File.ReadAllText path) with e -> Error e.Message)
                                     | None when request.Live -> GitHubGovernance.fetchJwks ()
                                     | None -> Error "no --jwks file and live access is off")

                            let receipts = receiptTexts root candidate request.ReceiptDirectories |> verifyReceipts trust jwks

                            let verifiedCommits =
                                receipts
                                |> List.choose (fun r -> match r.Check with Ok v -> Some v.Payload.Commit | Error _ -> None)
                                |> List.distinct
                                |> List.map (fun commit ->
                                    commit,
                                    { IsAncestorOfCandidate = GovernanceGit.isAncestor root commit candidate |> Result.defaultValue false
                                      ChangedSince = GovernanceGit.changedBetween root commit candidate |> Result.defaultValue [ "<unavailable>" ]
                                      TreeId = GovernanceGit.tree root commit |> Result.toOption })
                                |> Map.ofList

                            let candidateExceptions =
                                GovernanceGit.listFiles root candidate ExceptionsPrefix
                                |> Result.defaultValue []
                                |> List.filter (fun p -> not (state.Exceptions |> List.exists (fun (q, _) -> q = p)))
                                |> List.map (fun p -> p, (show root candidate p |> Option.map GovernanceJson.parseException |> Option.defaultValue (Error "unreadable")))

                            let exceptions, exceptionErrors =
                                state.Exceptions @ candidateExceptions
                                |> List.partition (fun (_, r) -> Result.isOk r)
                                |> fun (ok, bad) -> ok |> List.choose (fun (_, r) -> Result.toOption r), bad

                            let weakenings =
                                match trust, show root candidate TrustRootPath with
                                | Some protectedRoot, Some text ->
                                    match GovernanceJson.parseTrustRoot text with
                                    | Ok proposed -> VerifierTrust.weakenings protectedRoot proposed
                                    | Error e -> [ $"the candidate's trust root is unreadable: {e}" ]
                                | _ -> []

                            let platformEnforced =
                                request.GovernanceReport
                                |> Option.map (fun path ->
                                    match GovernanceJson.parseReport (File.ReadAllText path) with
                                    | Ok report -> report.Controls |> List.forall (fun c -> ControlStatus.isSatisfied c.Status || c.Status = ControlStatus.PlatformUnavailable || c.Id.StartsWith "GOV-TRUST" || c.Id = "GOV-VERIFIED-COMPLETION" || c.Id = "GOV-VALIDATION-POLICY")
                                    | Error _ -> false)

                            let ciReferenced = Classification.ciReferencedPaths (show root state.Head) state.WorkflowTexts tracked
                            let ros = match state.Ros with Some(Ok r) -> Some r | _ -> None
                            let expected = branchOf state.Ref

                            let facts =
                                { Repository = ros |> Option.bind _.Repository |> Option.defaultValue request.Repository
                                  RequestedRepository = request.Repository
                                  ExpectedTarget = expected
                                  TargetBranch = pull |> Option.map _.BaseRef |> Option.defaultValue expected
                                  TargetHead = state.Head
                                  CandidateHead = candidate
                                  CandidateContainsTarget = containsTarget
                                  ChangedPaths = changes
                                  CiReferenced = ciReferenced
                                  DeclaredWorkItems = declared
                                  AuthorizedWorkItems = state.AuthorizedWorkItems @ authorizedByComment |> List.distinct
                                  Standing = state.Standing
                                  RequireVerifiedCompletion = (fst state.Profile).RequireVerifiedMerge || (ros |> Option.exists _.RequireVerifiedCompletion)
                                  Policy = (match state.Policy with Some(Ok p) -> Some p | _ -> None)
                                  Trust = trust
                                  Receipts = receipts
                                  VerifiedCommits = verifiedCommits
                                  Authorities = state.Authorities
                                  ElevatedApprovals = approvals |> Option.defaultValue []
                                  Exceptions = exceptions
                                  CandidateExceptionIds = candidateExceptions |> List.choose (fun (_, r) -> match r with Ok e -> Some e.Id | Error _ -> None)
                                  TrustRootWeakenings = weakenings
                                  PlatformEnforced = platformEnforced
                                  EvaluatedAt = now () }

                            let decision = MergeAuthorization.decide facts

                            let unreadable =
                                exceptionErrors
                                |> List.map (fun (path, r) ->
                                    let why = match r with Error e -> e | Ok _ -> ""
                                    GovernanceFinding.create "MRG.EXCEPTION_INVALID" $"{path}: {why}" "fix or remove the exception record" Principal.ExceptionAuthority)

                            let configuration =
                                [ match state.ProfileError with
                                  | Some e -> GovernanceFinding.create "MRG.POLICY_UNAVAILABLE" $"the protected governance profile is malformed: {e}" "fix ros.json governance through an elevated change" Principal.RepositoryAdministrator
                                  | None -> ()
                                  match state.AuthoritiesError with
                                  | Some e -> GovernanceFinding.create "MRG.POLICY_UNAVAILABLE" $"the protected authorities are malformed: {e}" "fix .praxis/governance/authorities.json through an elevated change" Principal.RepositoryAdministrator
                                  | None -> () ]

                            let extra = unreadable @ configuration @ pullErrors @ approvalErrors @ authorizationErrors

                            if extra.IsEmpty then decision
                            else { decision with Outcome = MergeOutcome.Refused; Refusals = decision.Refusals @ extra })))))

    // ---- integration audit --------------------------------------------------

    let private decisionsIn (directory: string option) =
        match directory with
        | Some d when Directory.Exists d ->
            Directory.GetFiles(d, "*.json", SearchOption.AllDirectories)
            |> Array.choose (fun path ->
                try
                    let node = JsonNode.Parse(File.ReadAllText path)
                    if node["schema"].ToString() = "praxis.merge-decision/1" then Some(node["candidateHead"].ToString(), node["outcome"].ToString()) else None
                with _ -> None)
            |> Map.ofArray
        | _ -> Map.empty

    let audit (request: AuditRequest) : Result<IntegrationAuditReport, string> =
        let root = request.Root
        let decisions = decisionsIn request.DecisionDirectory

        GovernanceGit.resolve root request.BranchRef
        |> Result.bind (fun head ->
            GovernanceGit.firstParent root head request.Limit request.Since
            |> Result.map (fun entries ->
                let commits =
                    entries
                    |> List.rev
                    |> List.map (fun entry ->
                        let parent = List.tryHead entry.Parents
                        let mergeCandidate = if entry.Parents.Length > 1 then Some entry.Parents[1] else None

                        let pulls =
                            match request.Repository with
                            | Some repository -> GitHubGovernance.pullsForCommit repository entry.Commit |> Result.defaultValue []
                            | None -> []

                        let pr = pulls |> List.tryHead |> Option.map _.Number |> Option.orElse (IntegrationAudit.pullRequestFromSubject entry.Subject)
                        let candidate = mergeCandidate |> Option.orElse (pulls |> List.tryHead |> Option.map _.HeadSha)

                        let before = parent |> Option.map (fun p -> workItemStates (show root p ".ros/context/current.json")) |> Option.defaultValue Map.empty
                        let after = workItemStates (show root entry.Commit ".ros/context/current.json")

                        let introduced =
                            after
                            |> Map.toList
                            |> List.filter (fun (id, s) -> not (terminal.Contains s) && s <> "blocked" && Map.tryFind id before <> Some s)
                            |> List.map fst

                        let required =
                            parent
                            |> Option.map (fun p ->
                                [ "ros.json"; ".echelon/ros.json" ]
                                |> List.choose (show root p)
                                |> List.exists (fun t ->
                                    (GovernanceJson.parseRos t |> Result.map _.RequireVerifiedCompletion |> Result.defaultValue false)
                                    || (GovernanceJson.parseProfile t |> Result.map (fun (profile, _) -> profile.RequireVerifiedMerge) |> Result.defaultValue false)))
                            |> Option.defaultValue false

                        let mergeTree =
                            match entry.Parents with
                            | [ first; second ] -> GovernanceGit.mergeTreeMatches root entry.Commit first second
                            | _ -> None

                        let confirmed =
                            match request.Repository with
                            | Some _ when pr.IsSome -> Some(pulls |> List.exists (fun p -> p.MergeCommitSha = Some entry.Commit))
                            | _ -> None

                        let gate =
                            match candidate with
                            | None -> GateObservation.NotObserved
                            | Some c ->
                                match decisions |> Map.tryFind c with
                                | Some outcome -> GateObservation.Decision outcome
                                | None ->
                                    match request.Repository with
                                    | Some repository -> GitHubGovernance.gateCheck repository c request.GateCheck |> Result.defaultValue GateObservation.NotObserved
                                    | None -> GateObservation.NotObserved

                        let stranded =
                            match request.Repository, candidate, pulls |> List.tryHead with
                            | Some repository, Some c, Some pull ->
                                match GitHubGovernance.branchHead repository pull.HeadRef with
                                | Ok(Some tip) when tip <> c ->
                                    match GitHubGovernance.aheadBy repository c tip with
                                    | Ok n -> n
                                    | Error _ -> 0
                                | _ -> 0
                            | _ -> 0

                        { Commit = entry.Commit
                          Parents = entry.Parents
                          Committer = entry.Committer
                          Subject = entry.Subject
                          PullRequest = pr
                          Candidate = candidate
                          ActiveWorkItemsIntroduced = introduced
                          VerifiedCompletionRequired = required
                          Gate = gate
                          MergeTreeMatches = mergeTree
                          PlatformConfirmed = confirmed
                          StrandedAfterMerge = stranded })

                let previous =
                    request.PreviousHead |> Option.map (fun p -> GovernanceGit.isAncestor root p head |> Result.defaultValue false)

                IntegrationAudit.audit request.BranchRef head request.GateCheck previous commits))

    // ---- completion authority -------------------------------------------------

    let authorityState (root: string) (baseRef: string) =
        loadProtected root baseRef
        |> Result.map (fun state ->
            let ros = match state.Ros with Some(Ok r) -> Some r | _ -> None
            let env name = match Environment.GetEnvironmentVariable name with null | "" -> false | _ -> true

            { RequireVerifiedCompletion = (fst state.Profile).RequireVerifiedMerge || (ros |> Option.exists _.RequireVerifiedCompletion)
              RequireAttestedEvidence = ros |> Option.exists _.RequireAttestedEvidence
              PinnedKeyIds = ros |> Option.map _.PinnedKeyIds |> Option.defaultValue []
              TrustRoot = (match state.Trust with Some(Ok t) -> Some t | _ -> None)
              TrustRootError = (match state.Trust with Some(Error e) -> Some e | _ -> None)
              ValidationPolicyPresent = (match state.Policy with Some(Ok _) -> true | _ -> false)
              MergeGateWorkflowPresent = state.Workflows |> List.exists (fun (p, _) -> p = MergeGatePath)
              HostAuthorityNamed = env "PRAXIS_COMPLETION_AUTHORITY"
              HostVerifierSocketNamed = env "PRAXIS_VERIFIER_SOCKET" })
