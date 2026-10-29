namespace Praxis.Infrastructure.Autonomy

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Planning

/// Where the trust store comes from. A file must lie outside every
/// repository the run edits; an environment variable carries it in CI from
/// a secret the implementing agent cannot read or write (the CI-hosted gate).
[<RequireQualifiedAccess>]
type TrustSource =
    | File of path: string
    | Environment of name: string

type DraftRequest =
    { PolicyPath: string
      RunId: string
      Owner: string
      KeyId: string
      Repositories: string list
      Branches: string list
      Operations: Operation list
      Hours: int
      IssuedAt: DateTimeOffset option
      Out: string }

/// The effects behind `praxis autonomy` (files, clock, environment, GitHub);
/// the CLI only parses arguments and prints what these return.
[<RequireQualifiedAccess>]
module AutonomyOperations =
    let private utcNow () =
        let now = DateTimeOffset.UtcNow
        DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero)

    let private write (path: string) (text: string) =
        match Path.GetDirectoryName(Path.GetFullPath path) with
        | null -> ()
        | directory -> Directory.CreateDirectory directory |> ignore
        File.WriteAllText(path, text, UTF8Encoding false)

    let readPolicyFile path =
        if not (File.Exists path) then Error $"policy file {path} does not exist"
        else AutonomyJson.readPolicy (File.ReadAllBytes path)

    let private protectedRoots (root: string) = [ root; Directory.GetCurrentDirectory() ] |> List.distinct

    let private loadTrust (root: string) (source: TrustSource) =
        match source with
        | TrustSource.File path -> AutonomyStore.loadTrustStore (protectedRoots root) path
        | TrustSource.Environment name ->
            match Environment.GetEnvironmentVariable name with
            | null
            | "" -> Error $"the trust-store variable {name} is empty"
            | json -> AutonomyJson.readTrustStore json

    /// The authority as it is now: trust store, signature and revocations are
    /// all read again at every call (F8).
    let authority (root: string) (authorizationPath: string) (source: TrustSource) (revocationDirectories: string list) () : Authority =
        match loadTrust root source with
        | Error reason -> { Authorization = AuthorizationState.Invalid reason; RevokedRunIds = Set.empty; RevocationProblems = [] }
        | Ok trust ->
            let state =
                if not (File.Exists authorizationPath) then AuthorizationState.Missing
                else
                    match AutonomyJson.readAuthorization (File.ReadAllText authorizationPath) with
                    | Error reason -> AuthorizationState.Invalid reason
                    | Ok authorization -> AutonomyJson.verifySignature trust authorization

            let fromDirectories, problems = AutonomyStore.readRevocations revocationDirectories
            { Authorization = state
              RevokedRunIds = Set.union trust.RevokedRunIds fromDirectories
              RevocationProblems = problems }

    let draft (request: DraftRequest) : Result<string, string> =
        readPolicyFile request.PolicyPath
        |> Result.map (fun (policy, digest) ->
            let issued = request.IssuedAt |> Option.map (fun t -> t.ToUniversalTime()) |> Option.defaultWith utcNow
            let authorization =
                { RunId = request.RunId
                  Owner = request.Owner
                  KeyId = request.KeyId
                  IssuedAt = issued
                  ExpiresAt = issued.AddHours(float request.Hours)
                  PolicyVersion = policy.PolicyVersion
                  PolicyDigest = digest
                  Repositories = request.Repositories
                  TargetBranches = request.Branches
                  PermittedOperations = request.Operations
                  ProtectedOperations = ProtectedClass.all
                  Signature = "" }
            write request.Out (AutonomyJson.pretty (AutonomyJson.authorizationNode authorization))
            File.WriteAllBytes(request.Out + ".payload", Authorization.signingPayload authorization)
            request.Out + ".payload")

    /// Attach an externally produced signature (raw DER/P1363 or base64 text).
    let attach (draftPath: string) (signaturePath: string) (out: string) : Result<string, string> =
        AutonomyJson.readAuthorization (File.ReadAllText draftPath)
        |> Result.bind (fun draft ->
            let raw = File.ReadAllBytes signaturePath
            let bytes = try Convert.FromBase64String(Encoding.ASCII.GetString(raw).Trim()) with _ -> raw
            LocalOperator.normalizeSignature bytes
            |> Result.map (fun signature ->
                let signed = { draft with Signature = Convert.ToBase64String signature }
                write out (AutonomyJson.pretty (AutonomyJson.authorizationNode signed))
                AutonomyJson.authorizationDigest signed))

    /// Verification of the authorization alone (signature, trust, time,
    /// revocation, and optionally the policy digest), as a report node.
    let verify (current: Authority) (policyPath: string option) : JsonObject * string list =
        let now = DateTimeOffset.UtcNow
        let policyFindings =
            match policyPath, current.Authorization with
            | None, _ -> []
            | Some path, state ->
                match readPolicyFile path, state with
                | Error reason, _ -> [ reason ]
                | Ok(_, digest), AuthorizationState.Verified(a, _) when a.PolicyDigest <> digest -> [ $"the authorization covers policy {a.PolicyDigest}, not {digest}" ]
                | _ -> []

        let findings =
            match current.Authorization with
            | AuthorizationState.Missing -> [ "no authorization file" ]
            | AuthorizationState.Invalid reason -> [ reason ]
            | AuthorizationState.Verified(a, _) ->
                [ if current.RevokedRunIds.Contains a.RunId then $"run {a.RunId} is revoked"
                  if now >= a.ExpiresAt then "the authorization has expired"
                  if now < a.IssuedAt then "the authorization is not yet valid" ]
            @ (current.RevocationProblems |> List.map (fun p -> $"unreadable revocation: {p}"))
            @ policyFindings

        let node = JsonObject()
        node["valid"] <- JsonValue.Create findings.IsEmpty
        match current.Authorization with
        | AuthorizationState.Verified(a, digest) ->
            node["runId"] <- JsonValue.Create a.RunId
            node["owner"] <- JsonValue.Create a.Owner
            node["authorizationDigest"] <- JsonValue.Create digest
            node["expiresAt"] <- JsonValue.Create(Authorization.utc a.ExpiresAt)
        | _ -> ()
        let list = JsonArray()
        findings |> List.iter (fun f -> list.Add(JsonValue.Create f))
        node["findings"] <- list
        node, findings

    let revoke (runId: string) (reason: string) (out: string) =
        write out (AutonomyJson.pretty (AutonomyJson.revocationNode { RunId = runId; RevokedAt = utcNow (); Reason = reason }))

    /// The deterministic independent review of `head` against `base`.
    let review (root: string) (baseRev: string) (headRev: string) (policyPath: string option) (eventPath: string option) (allowed: string list option) (out: string option) =
        let declared =
            match policyPath with
            | Some path -> readPolicyFile path |> Result.map (fun (policy, _) -> policy.ProtectedPaths)
            | None -> Ok []

        declared
        |> Result.bind (fun protectedPaths ->
            LocalReview.resolve root baseRev
            |> Result.bind (fun baseSha ->
                LocalReview.resolve root headRev
                |> Result.bind (fun headSha ->
                    LocalReview.inputs root baseSha headSha (LocalReview.eventText (defaultArg eventPath "")) protectedPaths allowed
                    |> Result.map (fun input ->
                        let report = Review.review input
                        let executor =
                            [ "kind", "praxis-autonomy-review"
                              "executor", (if Environment.GetEnvironmentVariable "GITHUB_ACTIONS" = "true" then "github-actions" else "local")
                              "runId", (Environment.GetEnvironmentVariable "GITHUB_RUN_ID" |> Option.ofObj |> Option.defaultValue "") ]
                        let node = LocalReview.reportNode baseSha headSha report executor
                        out |> Option.iter (fun path -> write path (AutonomyJson.pretty node))
                        report, node, baseSha, headSha))))

    let environment (journalRoot: string) (poll: TimeSpan) (log: string -> unit) =
        { GitHub = GitHubApi.port
          Journal = AutonomyStore.journal journalRoot
          Now = fun () -> DateTimeOffset.UtcNow
          Sleep = fun delay -> Threading.Thread.Sleep delay
          PollInterval = poll
          Log = log }

    let evaluate (request: MergeRequest) (log: string -> unit) =
        let env = environment (Path.Combine(Path.GetTempPath(), "praxis-autonomy-evaluate")) (TimeSpan.FromSeconds 10.) log
        MergeLifecycle.gather env request MergeLifecycle.bootstrapFailure |> Result.map (MergeLifecycle.evaluate env request)

    let merge (request: MergeRequest) (journalRoot: string) (poll: TimeSpan) (log: string -> unit) =
        MergeLifecycle.run (environment journalRoot poll log) request

    /// Dependency continuation: verified receipts, each re-confirmed on
    /// GitHub, as the planner's own observations (docs/planning.md).
    let observations (journalRoot: string) (repository: string) (out: string) : Result<int, string list> =
        let journal = AutonomyStore.journal journalRoot
        AutonomyStore.verifyChain journal
        |> Result.bind (fun _ ->
            let text (node: JsonNode) (name: string) = node[name].ToString()
            let receipts = AutonomyStore.readReceipts journal |> List.filter (fun r -> text r "repository" = repository)
            let problems =
                receipts
                |> List.choose (fun r ->
                    let number = int (text r "pullRequest")
                    let merged = text r "mergeCommitSha"
                    match GitHubApi.port.GetPull repository number with
                    | Ok pull when pull.Merged && pull.MergeCommitSha = Some merged -> None
                    | Ok _ -> Some $"#{number}: GitHub does not report the receipt's merge {merged}"
                    | Error failure -> Some $"#{number}: {failure}")
            if not problems.IsEmpty then Error problems
            else
                let items = JsonArray()
                let observation (fields: (string * JsonNode) list) = items.Add(JsonObject(fields |> List.map KeyValuePair.Create))
                for r in receipts do
                    let reference = $"""praxis.merge-receipt/1 {AutonomyJson.digest r}: {repository}#{text r "pullRequest"} merged as {text r "mergeCommitSha"}"""
                    observation [ "kind", JsonValue.Create "pull-request-merged"; "pullRequest", JsonValue.Create(int (text r "pullRequest")); "source", JsonValue.Create "github"; "reference", JsonValue.Create reference ]
                    observation [ "kind", JsonValue.Create "commit-merged"; "commit", JsonValue.Create(text r "candidateSha"); "into", JsonValue.Create(text r "targetBranch"); "source", JsonValue.Create "github"; "reference", JsonValue.Create reference ]
                    observation [ "kind", JsonValue.Create "ci-passed"; "subject", JsonValue.Create(text r "candidateSha"); "source", JsonValue.Create "ci"; "reference", JsonValue.Create reference ]
                let node = JsonObject()
                node["observations"] <- items
                write out (AutonomyJson.pretty node)
                Ok receipts.Length)

    let verifyReceipts (journalRoot: string) = AutonomyStore.verifyChain (AutonomyStore.journal journalRoot)
