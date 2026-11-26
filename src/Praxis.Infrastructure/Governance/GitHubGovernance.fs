namespace Praxis.Infrastructure.Governance

open System
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Domain.Governance
open Praxis.Infrastructure.Autonomy

/// Read-only platform observation for governance (GitHub REST API). A raw
/// `praxis.governance-snapshot/1` keeps the provider's own JSON, so a saved
/// snapshot can be re-evaluated, diffed and used as test input; `toPlatform`
/// turns it into typed facts. No call here changes anything on GitHub.
[<RequireQualifiedAccess>]
module GitHubGovernance =
    [<Literal>]
    let SnapshotSchema = "praxis.governance-snapshot/1"

    let private at (node: JsonNode) (name: string) : JsonNode =
        match node with
        | null -> null
        | n -> n[name]

    let private unknown (reason: string) =
        let node = JsonObject()
        node["unknown"] <- JsonValue.Create true
        node["reason"] <- JsonValue.Create reason
        node :> JsonNode

    let private isUnknown (node: JsonNode) =
        match node with
        | null -> true
        | :? JsonObject as o -> o.ContainsKey "unknown"
        | _ -> false

    let private reasonOf (node: JsonNode) =
        match node with
        | null -> "not captured"
        | n -> match n["reason"] with null -> "not readable" | r -> r.ToString()

    let private message (body: string) =
        try
            match JsonNode.Parse body with
            | :? JsonObject as o when o.ContainsKey "message" -> o["message"].ToString()
            | _ -> body
        with _ ->
            body

    let private observe (path: string) =
        match GitHubApi.getRaw path with
        | Ok(200, body) -> JsonNode.Parse body
        | Ok(404, body) when (message body).Contains("not protected", StringComparison.OrdinalIgnoreCase) ->
            let node = JsonObject()
            node["notProtected"] <- JsonValue.Create true
            node :> JsonNode
        | Ok(status, body) -> unknown $"HTTP {status}: {message body}"
        | Error failure -> unknown (sprintf "%A" failure)

    /// Capture a snapshot of `repository`'s governance on `branch`.
    /// `workflows` are (path, triggers) read from the protected branch by Git.
    let capture (repository: string) (branch: string option) (workflows: Result<(string * string list * bool) list, string>) (capturedAt: DateTimeOffset) =
        let repo = observe $"/repos/{repository}"

        let target =
            match branch with
            | Some b -> b
            | None when not (isUnknown repo) -> repo["default_branch"].ToString()
            | None -> "main"

        let encoded = Uri.EscapeDataString target
        let rules = observe $"/repos/{repository}/rules/branches/{encoded}"

        let rulesetIds =
            match rules with
            | :? JsonArray as items -> items |> Seq.choose (fun r -> match r["ruleset_id"] with null -> None | id -> Some(id.GetValue<int64>())) |> Seq.distinct |> Seq.toList
            | _ -> []

        let details = JsonArray()

        for id in rulesetIds do
            details.Add(observe $"/repos/{repository}/rulesets/{id}")

        let head = observe $"/repos/{repository}/branches/{encoded}"

        let checks =
            if isUnknown head then unknown "the branch head is unknown"
            else
                let sha = (at (at head "commit") "sha").ToString()
                observe $"/repos/{repository}/commits/{sha}/check-runs?per_page=100"

        let node = JsonObject()
        node["schema"] <- JsonValue.Create SnapshotSchema
        node["capturedAt"] <- JsonValue.Create(VerifierTrust.utc capturedAt)
        node["repositoryName"] <- JsonValue.Create repository
        node["branch"] <- JsonValue.Create target
        node["repository"] <- repo
        node["branchProtection"] <- observe $"/repos/{repository}/branches/{encoded}/protection"
        node["rulesForBranch"] <- rules
        node["rulesets"] <- details
        node["rulesetsListing"] <- observe $"/repos/{repository}/rulesets?includes_parents=true"
        node["headChecks"] <- checks
        node["workflowPermissions"] <- observe $"/repos/{repository}/actions/permissions/workflow"

        node["workflows"] <-
            match workflows with
            | Error reason -> unknown reason
            | Ok list ->
                let items = JsonArray()

                for path, triggers, writesChecks in list do
                    let entry = JsonObject()
                    entry["path"] <- JsonValue.Create path
                    entry["writesChecks"] <- JsonValue.Create writesChecks
                    entry["triggers"] <- JsonArray(triggers |> List.map (fun t -> JsonValue.Create t :> JsonNode) |> List.toArray)
                    items.Add entry

                items

        node

    let private enabled (node: JsonNode) (name: string) =
        match node with
        | null -> false
        | n ->
            match n[name] with
            | null -> false
            | v when v.GetValueKind() = JsonValueKind.Object -> (match v["enabled"] with null -> false | e -> e.GetValue<bool>())
            | v when v.GetValueKind() = JsonValueKind.True -> true
            | _ -> false

    let private bypassActor (node: JsonNode) =
        let kind = match node["actor_type"] with null -> "" | v -> v.ToString()
        let id = match node["actor_id"] with null -> 0L | v when v.GetValueKind() = JsonValueKind.Number -> v.GetValue<int64>() | _ -> 0L

        match kind with
        | "RepositoryRole" -> BypassActor.RepositoryRole(string id)
        | "OrganizationAdmin" -> BypassActor.OrganizationAdmin
        | "Integration" -> BypassActor.Integration id
        | "Team" -> BypassActor.Team id
        | "DeployKey" -> BypassActor.DeployKey
        | other -> BypassActor.Other other

    /// Merge classic protection and rulesets into the rules that apply.
    let effectiveRules (protection: JsonNode) (rules: JsonNode) (rulesets: JsonNode) : Observed<EffectiveBranchRules> =
        let classic =
            match protection with
            | p when isUnknown p -> Error(reasonOf p)
            | p when (match p["notProtected"] with null -> false | _ -> true) -> Ok None
            | p -> Ok(Some p)

        let ruleList =
            match rules with
            | :? JsonArray as items -> Ok(items |> Seq.toList)
            | r -> Error(reasonOf r)

        match classic, ruleList with
        | Error reason, _
        | _, Error reason -> Observed.Unknown reason
        | Ok classic, Ok ruleList ->
            let ofType (t: string) = ruleList |> List.filter (fun r -> r["type"].ToString() = t)
            let parameters (r: JsonNode) = match r["parameters"] with null -> JsonObject() :> JsonNode | p -> p

            let pr = ofType "pull_request"
            let statusRules = ofType "required_status_checks"

            let ruleChecks =
                statusRules
                |> List.collect (fun r ->
                    match parameters r with
                    | p ->
                        match p["required_status_checks"] with
                        | :? JsonArray as items ->
                            items
                            |> Seq.map (fun c ->
                                { Context = c["context"].ToString()
                                  IntegrationId = match c["integration_id"] with null -> None | v -> Some(v.GetValue<int64>()) })
                            |> Seq.toList
                        | _ -> [])

            let classicChecks =
                match classic with
                | Some p ->
                    match p["required_status_checks"] with
                    | null -> []
                    | rsc ->
                        match rsc["checks"] with
                        | :? JsonArray as items ->
                            items
                            |> Seq.map (fun c ->
                                { Context = c["context"].ToString()
                                  IntegrationId =
                                    match c["app_id"] with
                                    | null -> None
                                    | v when v.GetValueKind() = JsonValueKind.Number && v.GetValue<int64>() > 0L -> Some(v.GetValue<int64>())
                                    | _ -> None })
                            |> Seq.toList
                        | _ -> []
                | None -> []

            let approvals =
                (pr |> List.map (fun r -> match (parameters r)["required_approving_review_count"] with null -> 0 | v -> v.GetValue<int>()))
                @ (match classic with
                   | Some p ->
                       match p["required_pull_request_reviews"] with
                       | null -> []
                       | rv -> [ match rv["required_approving_review_count"] with null -> 0 | v -> v.GetValue<int>() ]
                   | None -> [])

            let classicStrict =
                match classic with
                | Some p -> (match p["required_status_checks"] with null -> false | rsc -> (match rsc["strict"] with null -> false | v -> v.GetValue<bool>()))
                | None -> false

            let ruleStrict =
                statusRules |> List.exists (fun r -> match (parameters r)["strict_required_status_checks_policy"] with null -> false | v -> v.GetValue<bool>())

            let details =
                match rulesets with
                | :? JsonArray as items -> items |> Seq.toList
                | _ -> []

            let bypassVisible = details |> List.forall (fun d -> not (isUnknown d) && not (isNull d["bypass_actors"]))

            let rulesetBypass =
                details
                |> List.collect (fun d ->
                    match d["bypass_actors"] with
                    | :? JsonArray as actors -> actors |> Seq.map bypassActor |> Seq.toList
                    | _ -> [])

            let classicBypass =
                match classic with
                | Some p when not (enabled p "enforce_admins") -> [ BypassActor.RepositoryRole "admin (classic protection does not apply to administrators)" ]
                | _ -> []

            Observed.Known
                { RequiresPullRequest = not pr.IsEmpty || (classic |> Option.exists (fun p -> not (isNull p["required_pull_request_reviews"])))
                  RequiredApprovals = (0 :: approvals) |> List.max
                  DismissStaleApprovals =
                    pr |> List.exists (fun r -> match (parameters r)["dismiss_stale_reviews_on_push"] with null -> false | v -> v.GetValue<bool>())
                  RequiredChecks = (ruleChecks @ classicChecks) |> List.distinct
                  RequiresUpToDate = classicStrict || ruleStrict
                  BlocksForcePush = not (ofType "non_fast_forward").IsEmpty || (classic |> Option.exists (fun p -> not (enabled p "allow_force_pushes")))
                  BlocksDeletion = not (ofType "deletion").IsEmpty || (classic |> Option.exists (fun p -> not (enabled p "allow_deletions")))
                  RequiresLinearHistory = not (ofType "required_linear_history").IsEmpty || (classic |> Option.exists (fun p -> enabled p "required_linear_history"))
                  UsesMergeQueue = not (ofType "merge_queue").IsEmpty
                  AdminsEnforced = classic |> Option.forall (fun p -> enabled p "enforce_admins")
                  BypassActors = rulesetBypass @ classicBypass
                  BypassVisible = bypassVisible }

    let private releaseTags (listing: JsonNode) =
        match listing with
        | :? JsonArray as items -> Observed.Known(items |> Seq.exists (fun r -> match r["target"] with null -> false | t -> t.ToString() = "tag"))
        | other -> Observed.Unknown(reasonOf other)

    /// Typed platform facts from a raw snapshot.
    let toPlatform (raw: JsonNode) : Result<PlatformSnapshot, string> =
        try
            if raw["schema"].ToString() <> SnapshotSchema then failwith $"schema must be {SnapshotSchema}"
            let repo = raw["repository"]
            let known = not (isUnknown repo)

            let workflows =
                match raw["workflows"] with
                | :? JsonArray as items ->
                    Observed.Known(
                        items
                        |> Seq.map (fun w -> w["path"].ToString(), (w["triggers"].AsArray() |> Seq.map (fun t -> t.ToString()) |> Seq.toList))
                        |> Seq.toList
                    )
                | other -> Observed.Unknown(reasonOf other)

            let checks =
                match raw["headChecks"] with
                | null -> Observed.Unknown "not captured"
                | c when isUnknown c -> Observed.Unknown(reasonOf c)
                | c ->
                    match c["check_runs"] with
                    | :? JsonArray as runs ->
                        Observed.Known(
                            runs
                            |> Seq.map (fun r ->
                                r["name"].ToString(),
                                (match r["app"] with null -> "" | a -> (match a["slug"] with null -> "" | s -> s.ToString())),
                                (match r["conclusion"] with null -> "" | v -> v.ToString()))
                            |> Seq.toList
                        )
                    | _ -> Observed.Unknown "check runs are not in the expected shape"

            let tokenPermission =
                match raw["workflowPermissions"] with
                | null -> Observed.Unknown "not captured"
                | p when isUnknown p -> Observed.Unknown(reasonOf p)
                | p ->
                    match p["default_workflow_permissions"] with
                    | null -> Observed.Unknown "default_workflow_permissions is absent"
                    | v -> Observed.Known(v.ToString())

            let checkWriters =
                match raw["workflows"] with
                | :? JsonArray as items ->
                    Observed.Known(
                        items
                        |> Seq.filter (fun w ->
                            let triggers = w["triggers"].AsArray() |> Seq.map (fun t -> t.ToString()) |> Seq.toList
                            let writes = match w["writesChecks"] with null -> true | v -> v.GetValue<bool>()
                            writes
                            && w["path"].ToString() <> ".github/workflows/praxis-merge-gate.yml"
                            && triggers |> List.exists (fun t -> t = "pull_request" || t = "pull_request_target" || t = "unparsed"))
                        |> Seq.map (fun w -> w["path"].ToString())
                        |> Seq.toList
                    )
                | other -> Observed.Unknown(reasonOf other)

            Ok
                { Repository = raw["repositoryName"].ToString()
                  OwnerKind =
                    (if known && (match repo["owner"] with null -> "" | o -> o["type"].ToString()) = "Organization" then OwnerKind.Organization else OwnerKind.User)
                  Visibility = (if known then (match repo["visibility"] with null -> "unknown" | v -> v.ToString()) else "unknown")
                  DefaultBranch = (if known then repo["default_branch"].ToString() else raw["branch"].ToString())
                  Branch = raw["branch"].ToString()
                  CapturedAt = DateTimeOffset.Parse(raw["capturedAt"].ToString())
                  Rules = effectiveRules raw["branchProtection"] raw["rulesForBranch"] raw["rulesets"]
                  Workflows = workflows
                  HeadChecks = checks
                  ReleaseTagsProtected = releaseTags raw["rulesetsListing"]
                  DefaultTokenPermission = tokenPermission
                  CheckWriters = checkWriters }
        with error ->
            Error $"the snapshot cannot be read: {error.Message}"

    // ---- pull-request facts for merge authorization and audit ----------------

    type PullFacts =
        { Number: int
          Author: string
          BaseRef: string
          HeadRef: string
          HeadSha: string
          Body: string
          MergeCommitSha: string option
          MergedAt: DateTimeOffset option }

    let private pull (node: JsonNode) =
        { Number = node["number"].GetValue<int>()
          Author = (match node["user"] with null -> "" | u -> u["login"].ToString())
          BaseRef = (at (at node "base") "ref").ToString()
          HeadRef = (at (at node "head") "ref").ToString()
          HeadSha = (at (at node "head") "sha").ToString()
          Body = (match node["body"] with null -> "" | b -> b.ToString())
          MergeCommitSha = (match node["merge_commit_sha"] with null -> None | v -> Some(v.ToString()))
          MergedAt = (match node["merged_at"] with null -> None | v -> Some(DateTimeOffset.Parse(v.ToString()))) }

    let getPull (repository: string) (number: int) =
        match GitHubApi.getRaw $"/repos/{repository}/pulls/{number}" with
        | Ok(200, body) -> Ok(pull (JsonNode.Parse body))
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// The pull requests GitHub associates with a commit.
    let pullsForCommit (repository: string) (commit: string) =
        match GitHubApi.getRaw $"/repos/{repository}/commits/{commit}/pulls" with
        | Ok(200, body) -> Ok((JsonNode.Parse body).AsArray() |> Seq.map pull |> Seq.toList)
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// The `/praxis …` command comments on a pull request:
    /// (login, command, argument, rest, createdAt, viaApplication, edited).
    /// `performed_via_github_app` marks a comment an application posted on
    /// the commenter's behalf; `updated_at` later than `created_at` marks an
    /// edit (any writer can edit another's comment). Neither is ever accepted
    /// as a person's approval.
    let private commands (repository: string) (number: int) =
        match GitHubApi.getRaw $"/repos/{repository}/issues/{number}/comments?per_page=100" with
        | Ok(200, body) ->
            (JsonNode.Parse body).AsArray()
            |> Seq.choose (fun c ->
                let text = (match c["body"] with null -> "" | b -> b.ToString()).Trim()

                if text.StartsWith("/praxis ", StringComparison.Ordinal) then
                    let parts = text.Substring(8).Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)
                    let created = DateTimeOffset.Parse((at c "created_at").ToString())
                    let updated = match c["updated_at"] with null -> created | v -> DateTimeOffset.Parse(v.ToString())

                    if parts.Length >= 2 then
                        Some(
                            (at (at c "user") "login").ToString(),
                            parts[0],
                            parts[1],
                            (if parts.Length = 3 then parts[2] else ""),
                            created,
                            (match c["performed_via_github_app"] with null -> None | app -> Some((at app "slug").ToString())),
                            updated > created.AddSeconds 1.0
                        )
                    else None
                else None)
            |> Seq.toList
            |> Ok
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    let elevatedApprovals (repository: string) (number: int) : Result<ElevatedApproval list, string> =
        commands repository number
        |> Result.map (
            List.choose (fun (login, command, argument, rest, created, via, edited) ->
                if command = "approve-elevated" then
                    Some { Approver = login; CandidateHead = argument; ApprovedAt = created; ViaApplication = via; Edited = edited; Reason = rest }
                else None)
        )

    let workAuthorizations (repository: string) (number: int) : Result<WorkAuthorization list, string> =
        commands repository number
        |> Result.map (
            List.choose (fun (login, command, argument, _, created, via, edited) ->
                if command = "authorize-work" then
                    Some { Authorizer = login; WorkItem = argument; AuthorizedAt = created; ViaApplication = via; Edited = edited }
                else None)
        )

    /// Conclusions of this workflow run's jobs (latest attempt): name -> (conclusion, startedAt, completedAt, id).
    let runJobs (repository: string) (runId: string) =
        match GitHubApi.getRaw $"/repos/{repository}/actions/runs/{Uri.EscapeDataString runId}/jobs?filter=latest&per_page=100" with
        | Ok(200, body) ->
            (at (JsonNode.Parse body) "jobs").AsArray()
            |> Seq.map (fun j ->
                let time (name: string) = match j[name] with null -> None | v -> Some(DateTimeOffset.Parse(v.ToString()))
                (at j "name").ToString(), ((match j["conclusion"] with null -> "pending" | v -> v.ToString()), time "started_at", time "completed_at", (at j "id").ToString()))
            |> Seq.toList
            |> Ok
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// The gate check's verdict at a commit: (conclusion, producing app).
    let gateCheck (repository: string) (commit: string) (checkName: string) =
        match GitHubApi.getRaw $"/repos/{repository}/commits/{commit}/check-runs?check_name={Uri.EscapeDataString checkName}" with
        | Ok(200, body) ->
            let runs = (at (JsonNode.Parse body) "check_runs").AsArray() |> Seq.toList

            match runs |> List.sortByDescending (fun r -> match r["completed_at"] with null -> "" | v -> v.ToString()) with
            | [] -> Ok GateObservation.NotObserved
            | latest :: _ ->
                Ok(
                    GateObservation.Check(
                        (match latest["conclusion"] with null -> "pending" | v -> v.ToString()),
                        (match latest["app"] with null -> "" | a -> a["slug"].ToString())
                    )
                )
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// The current head of a branch, or `None` when it no longer exists.
    let branchHead (repository: string) (branch: string) =
        match GitHubApi.getRaw $"/repos/{repository}/branches/{Uri.EscapeDataString branch}" with
        | Ok(200, body) -> Ok(Some((at (at (JsonNode.Parse body) "commit") "sha").ToString()))
        | Ok(404, _) -> Ok None
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// How many commits `tip` has that `base` does not (compare API).
    let aheadBy (repository: string) (``base``: string) (tip: string) =
        match GitHubApi.getRaw $"/repos/{repository}/compare/{``base``}...{tip}" with
        | Ok(200, body) -> Ok((at (JsonNode.Parse body) "ahead_by").GetValue<int>())
        | Ok(status, body) -> Error $"HTTP {status}: {message body}"
        | Error failure -> Error(sprintf "%A" failure)

    /// The issuer's published keys (GitHub Actions OIDC).
    let fetchJwks () =
        match GitHubApi.getRawAbsolute $"{VerifierTrust.GitHubIssuer}/.well-known/jwks" with
        | Ok(200, body) -> GovernanceJson.parseJwks body
        | Ok(status, _) -> Error $"the issuer's keys are unavailable (HTTP {status})"
        | Error failure -> Error(sprintf "%A" failure)
