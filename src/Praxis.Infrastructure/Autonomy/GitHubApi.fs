namespace Praxis.Infrastructure.Autonomy

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Praxis.Domain.Autonomy

/// The GitHub operations the unattended lifecycle needs, as functions, so the
/// lifecycle is tested against a fake and run against GitHub unchanged.
/// Every call returns a classified failure instead of throwing.
type GitHubPort =
    { GetPull: string -> int -> Result<PullRequestObservation, CallFailure>
      GetPullFiles: string -> int -> Result<ChangedPath list, CallFailure>
      GetCheckRuns: string -> string -> Result<CheckObservation list, CallFailure>
      GetBranchRules: string -> string -> Result<BranchRules, CallFailure>
      GetBranchHead: string -> string -> Result<string, CallFailure>
      /// The bytes of `path` at `ref`, or None when it does not exist there.
      GetFile: string -> string -> string -> Result<byte array option, CallFailure>
      /// Every blob path of the tree at a commit.
      GetTreePaths: string -> string -> Result<string list, CallFailure>
      /// Whether `head` contains `commit` (compare commit...head).
      Contains: string -> string -> string -> Result<bool, CallFailure>
      /// Merge pinned to the candidate SHA: GitHub refuses if the head moved.
      Merge: string -> int -> string -> MergeStrategy -> Result<string, CallFailure> }

/// GitHub's REST API over HttpClient. The credential is read from
/// GITHUB_TOKEN or GH_TOKEN when present (GitHub Actions; an approved
/// application installation token) and is never logged or echoed; when
/// neither is set, requests go unauthenticated, which an authenticating
/// egress proxy may complete.
[<RequireQualifiedAccess>]
module GitHubApi =
    let private apiBase () =
        match Environment.GetEnvironmentVariable "PRAXIS_GITHUB_API" with
        | null
        | "" -> "https://api.github.com"
        | value -> value.TrimEnd('/')

    let private token () =
        [ "GITHUB_TOKEN"; "GH_TOKEN" ]
        |> List.tryPick (fun name ->
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> None
            | value -> Some value)

    let private client =
        lazy
            (let c = new HttpClient(Timeout = TimeSpan.FromSeconds 60.)
             c.DefaultRequestHeaders.UserAgent.ParseAdd "praxis-autonomy/1"
             c.DefaultRequestHeaders.Accept.ParseAdd "application/vnd.github+json"
             c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28")
             c)

    let private message (body: string) =
        try
            match JsonNode.Parse(body) with
            | :? JsonObject as o when o.ContainsKey "message" -> o["message"].GetValue<string>()
            | _ -> body
        with _ -> body

    /// Classify a failed response. 5xx, 429, 408 and rate limiting are
    /// transient; everything else is permanent unless it is the merge API's
    /// "head changed" refusal.
    let classify (status: int) (body: string) : CallFailure =
        let detail = message body
        let lower = detail.ToLowerInvariant()
        if status >= 500 || status = 429 || status = 408 then CallFailure.Transient $"HTTP {status}: {detail}"
        elif status = 403 && lower.Contains "rate limit" then CallFailure.Transient $"HTTP {status}: {detail}"
        elif status = 409 && (lower.Contains "head branch was modified" || lower.Contains "sha") then CallFailure.HeadChanged detail
        else CallFailure.Permanent(status, detail)

    let private send (meth: HttpMethod) (path: string) (body: JsonNode option) : Result<int * string, CallFailure> =
        try
            use request = new HttpRequestMessage(meth, apiBase () + path)
            token () |> Option.iter (fun t -> request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", t))
            body |> Option.iter (fun node -> request.Content <- new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"))
            use response = client.Value.Send(request)
            use reader = new IO.StreamReader(response.Content.ReadAsStream())
            Ok(int response.StatusCode, reader.ReadToEnd())
        with
        | :? HttpRequestException as error -> Error(CallFailure.Transient $"network: {error.Message}")
        | :? TaskCanceledException -> Error(CallFailure.Transient "the request timed out")
        | :? IO.IOException as error -> Error(CallFailure.Transient $"network: {error.Message}")

    let private getJson path =
        send HttpMethod.Get path None
        |> Result.bind (fun (status, body) -> if status = 200 then Ok(JsonNode.Parse body) else Error(classify status body))

    let private str (node: JsonNode) (name: string) =
        match node[name] with
        | null -> ""
        | value -> value.ToString()

    let private optStr (node: JsonNode) (name: string) =
        match node[name] with
        | null -> None
        | value -> Some(value.ToString())

    let parsePull (node: JsonNode) =
        { Number = node["number"].GetValue<int>()
          State = str node "state"
          Draft = (match node["draft"] with null -> false | v -> v.GetValue<bool>())
          Merged = (match node["merged"] with null -> false | v -> v.GetValue<bool>())
          MergeCommitSha = optStr node "merge_commit_sha"
          HeadSha = str node["head"] "sha"
          BaseRef = str node["base"] "ref"
          Mergeable = (match node["mergeable"] with null -> None | v -> Some(v.GetValue<bool>()))
          MergeableState = str node "mergeable_state" }

    let private getPull repository (number: int) = getJson $"/repos/{repository}/pulls/{number}" |> Result.map parsePull

    let private pages (path: string) (select: JsonNode -> JsonNode seq) =
        let rec loop page acc =
            // Never a silent prefix: a listing longer than the bound is an error,
            // so a change or a revocation past it cannot go unseen.
            if page > 30 then Error(CallFailure.Permanent(0, $"{path}: more than 3000 entries; refusing a truncated listing"))
            else
                let separator = if path.Contains "?" then "&" else "?"
                getJson $"{path}{separator}per_page=100&page={page}"
                |> Result.bind (fun node ->
                    let items = select node |> Seq.toList
                    if items.Length < 100 then Ok(acc @ items) else loop (page + 1) (acc @ items))
        loop 1 []

    let private getPullFiles repository (number: int) =
        pages $"/repos/{repository}/pulls/{number}/files" (fun node -> node.AsArray() :> JsonNode seq)
        |> Result.map (List.map (fun file ->
            let name = str file "filename"
            { Path = name
              Kind =
                match str file "status" with
                | "added" -> ChangeKind.Added
                | "removed" -> ChangeKind.Removed
                | "renamed" -> ChangeKind.Renamed(str file "previous_filename")
                | _ -> ChangeKind.Modified }))

    let parseCheckRun (node: JsonNode) =
        { Name = str node "name"
          HeadSha = str node "head_sha"
          Status = str node "status"
          Conclusion = optStr node "conclusion"
          App = (match node["app"] with null -> "" | app -> str app "slug")
          Id = node["id"].GetValue<int64>()
          Suite = (match node["check_suite"] with null -> 0L | suite -> (match suite["id"] with null -> 0L | id -> id.GetValue<int64>())) }

    let private getCheckRuns repository (sha: string) =
        pages $"/repos/{repository}/commits/{sha}/check-runs?filter=all" (fun node -> node["check_runs"].AsArray() :> JsonNode seq)
        |> Result.map (List.map parseCheckRun)

    /// Branch protection and rulesets both. A 404 "Branch not protected" is a
    /// visible "none"; any other refusal leaves the rule unknown and visible
    /// = false, never "no requirement".
    let private getBranchRules repository (branch: string) =
        let encoded = Uri.EscapeDataString branch
        let protection =
            send HttpMethod.Get $"/repos/{repository}/branches/{encoded}/protection" None
            |> Result.map (fun (status, body) ->
                match status with
                | 200 ->
                    let node = JsonNode.Parse body
                    match node["required_pull_request_reviews"] with
                    | null -> Some(0, false)
                    | reviews ->
                        Some(
                            (match reviews["required_approving_review_count"] with null -> 0 | v -> v.GetValue<int>()),
                            (match reviews["require_code_owner_reviews"] with null -> false | v -> v.GetValue<bool>()))
                | 404 when (message body).Contains "not protected" -> Some(0, false)
                | _ -> None)

        let rules =
            send HttpMethod.Get $"/repos/{repository}/rules/branches/{encoded}" None
            |> Result.map (fun (status, body) ->
                if status <> 200 then None
                else
                    JsonNode.Parse(body).AsArray()
                    |> Seq.filter (fun rule -> str rule "type" = "pull_request")
                    |> Seq.fold
                        (fun (count, owners) rule ->
                            let p = rule["parameters"]
                            let c = match p with null -> 0 | p -> (match p["required_approving_review_count"] with null -> 0 | v -> v.GetValue<int>())
                            let o = match p with null -> false | p -> (match p["require_code_owner_review"] with null -> false | v -> v.GetValue<bool>())
                            max count c, owners || o)
                        (0, false)
                    |> Some)

        match protection, rules with
        | Error e, _
        | _, Error e -> Error e
        | Ok p, Ok r ->
            // A requirement read from either source is known even when the
            // other is unreadable; "none" is known only when both were read.
            let known = [ p; r ] |> List.choose id
            let visible = known.Length = 2
            let count = known |> List.map fst |> List.fold max 0
            let owners = known |> List.exists snd
            Ok
                { RequiredApprovingReviews = if visible || count > 0 then Some count else None
                  RequiresCodeOwnerReview = if visible || owners then Some owners else None
                  RulesVisible = visible }

    let private getBranchHead repository (branch: string) =
        getJson $"/repos/{repository}/branches/{Uri.EscapeDataString branch}" |> Result.map (fun node -> str node["commit"] "sha")

    let private getFile repository (reference: string) (path: string) =
        send HttpMethod.Get $"/repos/{repository}/contents/{path}?ref={Uri.EscapeDataString reference}" None
        |> Result.bind (fun (status, body) ->
            match status with
            | 200 ->
                let node = JsonNode.Parse body
                if str node "encoding" <> "base64" then Error(CallFailure.Permanent(200, $"{path} is not a file"))
                else Ok(Some(Convert.FromBase64String((str node "content").Replace("\n", ""))))
            | 404 -> Ok None
            | _ -> Error(classify status body))

    /// Blob paths of a recursive tree. A truncated tree is refused: deciding
    /// on a partial view could miss a workflow or a revocation (F9).
    let parseTree (sha: string) (node: JsonNode) =
        if str node "truncated" = "true" then
            Error(CallFailure.Permanent(0, $"the tree of {sha} is truncated; refusing to decide on a partial view"))
        else
            node["tree"].AsArray()
            |> Seq.filter (fun entry -> str entry "type" = "blob")
            |> Seq.map (fun entry -> str entry "path")
            |> Seq.toList
            |> Ok

    let private getTreePaths repository (sha: string) =
        getJson $"/repos/{repository}/git/trees/{sha}?recursive=1" |> Result.bind (parseTree sha)

    let private contains repository (commit: string) (head: string) =
        getJson $"/repos/{repository}/compare/{commit}...{head}"
        |> Result.map (fun node ->
            match str node "status" with
            | "identical"
            | "ahead" -> true
            | _ -> false)

    let private merge repository (number: int) (sha: string) (strategy: MergeStrategy) =
        let body = JsonObject()
        body["sha"] <- JsonValue.Create sha
        body["merge_method"] <- JsonValue.Create(MergeStrategy.toWire strategy)
        send HttpMethod.Put $"/repos/{repository}/pulls/{number}/merge" (Some body)
        |> Result.bind (fun (status, text) ->
            if status = 200 then Ok(str (JsonNode.Parse text) "sha") else Error(classify status text))

    /// Read-only configuration diagnostics for unattended merging: what the
    /// repository and its target branch require, and what this credential
    /// may do. An endpoint the credential cannot read is reported as
    /// "unknown" with its HTTP status, never as "absent".
    let diagnose (repository: string) (branch: string option) : Result<JsonObject, CallFailure> =
        getJson $"/repos/{repository}"
        |> Result.bind (fun repo ->
            let target = branch |> Option.defaultValue (str repo "default_branch")
            let status path = send HttpMethod.Get path None
            let encoded = Uri.EscapeDataString target

            let observed (path: string) =
                match status path with
                | Ok(200, body) -> JsonNode.Parse body
                | Ok(code, body) ->
                    let n = JsonObject()
                    n["unknown"] <- JsonValue.Create true
                    n["httpStatus"] <- JsonValue.Create code
                    n["message"] <- JsonValue.Create(message body)
                    n :> JsonNode
                | Error failure ->
                    let n = JsonObject()
                    n["unknown"] <- JsonValue.Create true
                    n["error"] <- JsonValue.Create(sprintf "%A" failure)
                    n :> JsonNode

            let codeowners =
                [ "CODEOWNERS"; ".github/CODEOWNERS"; "docs/CODEOWNERS" ]
                |> List.filter (fun p -> match getFile repository target p with Ok(Some _) -> true | _ -> false)

            getBranchRules repository target
            |> Result.map (fun rules ->
                let node = JsonObject()
                node["schema"] <- JsonValue.Create "praxis.github-merge-diagnosis/1"
                node["repository"] <- JsonValue.Create(str repo "full_name")
                node["ownerType"] <- JsonValue.Create(str repo["owner"] "type")
                node["visibility"] <- JsonValue.Create(str repo "visibility")
                node["defaultBranch"] <- JsonValue.Create(str repo "default_branch")
                node["targetBranch"] <- JsonValue.Create target
                [ "allow_auto_merge"; "allow_squash_merge"; "allow_merge_commit"; "allow_rebase_merge"; "delete_branch_on_merge"; "allow_update_branch" ]
                |> List.iter (fun name -> node[name] <- (match repo[name] with null -> null | v -> v.DeepClone()))
                node["permissions"] <- (match repo["permissions"] with null -> null | v -> v.DeepClone())
                node["branch"] <- observed $"/repos/{repository}/branches/{encoded}"
                node["branchProtection"] <- observed $"/repos/{repository}/branches/{encoded}/protection"
                node["rulesForBranch"] <- observed $"/repos/{repository}/rules/branches/{encoded}"
                node["rulesets"] <- observed $"/repos/{repository}/rulesets?includes_parents=true"
                let owners = JsonArray()
                codeowners |> List.iter (fun p -> owners.Add(JsonValue.Create p))
                node["codeowners"] <- owners
                node["requiredApprovingReviews"] <- (match rules.RequiredApprovingReviews with Some c -> JsonValue.Create c :> JsonNode | None -> null)
                node["requiresCodeOwnerReview"] <- (match rules.RequiresCodeOwnerReview with Some c -> JsonValue.Create c :> JsonNode | None -> null)
                node["rulesVisible"] <- JsonValue.Create rules.RulesVisible
                node))

    let port: GitHubPort =
        { GetPull = getPull
          GetPullFiles = getPullFiles
          GetCheckRuns = getCheckRuns
          GetBranchRules = getBranchRules
          GetBranchHead = getBranchHead
          GetFile = getFile
          GetTreePaths = getTreePaths
          Contains = contains
          Merge = merge }
