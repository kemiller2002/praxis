namespace Praxis.Cli

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Autonomy

/// `praxis autonomy`: unattended execution under an owner-signed run
/// authorization (docs/autonomous-execution/). Exit codes: 0 allowed,
/// merged or verified; 3 denied, blocked, requires the owner, must be
/// re-verified, or failed verification; 2 invalid arguments; 6 a
/// prerequisite (trust store, policy file) is unusable.
[<RequireQualifiedAccess>]
module AutonomyCommands =
    let usage =
        "autonomy policy check --policy FILE [--json] | "
        + "autonomy authorize draft --policy FILE --run-id ID --owner LOGIN --key-id KEY --repository OWNER/REPO* --branch GLOB* --operation OP* --hours N [--issued-at TIMESTAMP] --out FILE | "
        + "autonomy authorize attach --draft FILE --signature FILE --out FILE | "
        + "autonomy authorize verify --authorization FILE --trust-store FILE [--policy FILE] [--json] | "
        + "autonomy revoke --run-id ID --reason TEXT --out FILE | "
        + "autonomy review --base REV --head REV [--policy FILE] [--event FILE] [--allowed-path GLOB]* [--out FILE] [--json] | "
        + "autonomy evaluate --repository OWNER/REPO --pr N --candidate SHA --authorization FILE --trust-store FILE [--revocations DIR]* [--json] | "
        + "autonomy merge --repository OWNER/REPO --pr N --candidate SHA --authorization FILE --trust-store FILE --journal DIR [--revocations DIR]* [--poll-seconds N] [--json] | "
        + "autonomy receipts verify --journal DIR | "
        + "autonomy diagnose --repository OWNER/REPO [--branch NAME]"

    type private Options =
        { Values: Map<string, string list>
          Flags: Set<string> }

    let private flags = set [ "--json" ]

    let rec private parse (options: Options) (arguments: string list) =
        match arguments with
        | [] -> Ok options
        | flag :: rest when flags.Contains flag -> parse { options with Flags = options.Flags.Add flag } rest
        | name :: value :: rest when name.StartsWith "--" && not (value.StartsWith "--") ->
            let existing = options.Values |> Map.tryFind name |> Option.defaultValue []
            parse { options with Values = options.Values.Add(name, existing @ [ value ]) } rest
        | other :: _ -> Error $"unexpected argument '{other}'"

    let private one (options: Options) name =
        match options.Values |> Map.tryFind name with
        | Some [ value ] -> Ok value
        | Some _ -> Error $"{name} may be given once"
        | None -> Error $"{name} is required"

    let private optionalOne (options: Options) name =
        match options.Values |> Map.tryFind name with
        | Some [ value ] -> Some value
        | _ -> None

    let private many (options: Options) name = options.Values |> Map.tryFind name |> Option.defaultValue []

    let private json (options: Options) = options.Flags.Contains "--json"

    let private print (node: JsonNode) = printf "%s" (AutonomyJson.pretty node)

    let private invalid message =
        eprintfn "ERROR %s; expected %s" message usage
        2

    let private prerequisite message =
        eprintfn "ERROR %s" message
        6

    let private readPolicyFile path =
        if not (File.Exists path) then Error $"policy file {path} does not exist"
        else AutonomyJson.readPolicy (File.ReadAllBytes path)

    let private protectedRoots (root: string) = [ root; Directory.GetCurrentDirectory() ] |> List.distinct

    let private loadAuthorization (root: string) (options: Options) =
        match one options "--authorization", one options "--trust-store" with
        | Error e, _
        | _, Error e -> Error e
        | Ok authorizationPath, Ok trustPath ->
            match AutonomyStore.loadTrustStore (protectedRoots root) trustPath with
            | Error reason -> Error reason
            | Ok trust ->
                let state =
                    if not (File.Exists authorizationPath) then AuthorizationState.Missing
                    else
                        match AutonomyJson.readAuthorization (File.ReadAllText authorizationPath) with
                        | Error reason -> AuthorizationState.Invalid reason
                        | Ok authorization -> AutonomyJson.verifySignature trust authorization

                let fromDirectories, problems = AutonomyStore.readRevocations (many options "--revocations")
                problems |> List.iter (eprintfn "WARNING unreadable revocation %s")
                Ok(state, Set.union trust.RevokedRunIds fromDirectories)

    // -------------------------------------------------------------- commands

    let private policyCheck (options: Options) =
        match one options "--policy" with
        | Error e -> invalid e
        | Ok path ->
            match readPolicyFile path with
            | Error reason ->
                eprintfn "ERROR %s" reason
                3
            | Ok(policy, digest) ->
                if json options then
                    let node = JsonObject()
                    node["valid"] <- JsonValue.Create true
                    node["policyVersion"] <- JsonValue.Create policy.PolicyVersion
                    node["policyDigest"] <- JsonValue.Create digest
                    print node
                else
                    printfn "policy: valid (version %d)" policy.PolicyVersion
                    printfn "digest: %s" digest
                0

    let private parseOperations (raw: string list) =
        raw
        |> List.fold
            (fun acc value ->
                acc |> Result.bind (fun ops ->
                    match Operation.tryParse value with
                    | Some op -> Ok(ops @ [ op ])
                    | None -> Error $"unknown operation '{value}'"))
            (Ok [])

    let private draft (options: Options) =
        match one options "--policy", one options "--run-id", one options "--owner", one options "--key-id", one options "--hours", one options "--out" with
        | Ok policyPath, Ok runId, Ok owner, Ok keyId, Ok hours, Ok out ->
            match readPolicyFile policyPath, parseOperations (many options "--operation"), Int32.TryParse hours with
            | Error reason, _, _ -> prerequisite reason
            | _, Error reason, _ -> invalid reason
            | _, _, (false, _) -> invalid "--hours must be an integer"
            | Ok(policy, digest), Ok operations, (true, hours) ->
                if (many options "--repository").IsEmpty || (many options "--branch").IsEmpty || operations.IsEmpty then
                    invalid "at least one --repository, --branch and --operation is required"
                else
                    let issued =
                        optionalOne options "--issued-at"
                        |> Option.map (fun s -> DateTimeOffset.Parse(s).ToUniversalTime())
                        |> Option.defaultWith (fun () -> DateTimeOffset.UtcNow)
                    let issued = DateTimeOffset(issued.Year, issued.Month, issued.Day, issued.Hour, issued.Minute, issued.Second, TimeSpan.Zero)

                    let authorization =
                        { RunId = runId
                          Owner = owner
                          KeyId = keyId
                          IssuedAt = issued
                          ExpiresAt = issued.AddHours(float hours)
                          PolicyVersion = policy.PolicyVersion
                          PolicyDigest = digest
                          Repositories = many options "--repository"
                          TargetBranches = many options "--branch"
                          PermittedOperations = operations
                          ProtectedOperations = ProtectedClass.all
                          Signature = "" }

                    File.WriteAllText(out, AutonomyJson.pretty (AutonomyJson.authorizationNode authorization), UTF8Encoding false)
                    File.WriteAllBytes(out + ".payload", Authorization.signingPayload authorization)
                    printfn "draft: %s" out
                    printfn "bytes to sign: %s.payload" out
                    printfn "the owner signs them with their own P-256 key, for example:"
                    printfn "  openssl dgst -sha256 -sign owner.key -out %s.sig %s.payload" out out
                    printfn "then: praxis autonomy authorize attach --draft %s --signature %s.sig --out AUTHORIZATION.json" out out
                    0
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> invalid e

    let private attach (options: Options) =
        match one options "--draft", one options "--signature", one options "--out" with
        | Ok draftPath, Ok signaturePath, Ok out ->
            match AutonomyJson.readAuthorization (File.ReadAllText draftPath) with
            | Error reason -> prerequisite reason
            | Ok draft ->
                let raw = File.ReadAllBytes signaturePath
                // A text file holding base64 is accepted as well as raw bytes.
                let bytes =
                    let text = Encoding.ASCII.GetString(raw).Trim()
                    try Convert.FromBase64String text with _ -> raw

                match Praxis.Infrastructure.Planning.LocalOperator.normalizeSignature bytes with
                | Error reason -> prerequisite reason
                | Ok signature ->
                    let signed = { draft with Signature = Convert.ToBase64String signature }
                    File.WriteAllText(out, AutonomyJson.pretty (AutonomyJson.authorizationNode signed), UTF8Encoding false)
                    printfn "authorization: %s" out
                    printfn "digest: %s" (AutonomyJson.authorizationDigest signed)
                    0
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> invalid e

    let private verify (root: string) (options: Options) =
        match loadAuthorization root options with
        | Error reason -> prerequisite reason
        | Ok(state, revoked) ->
            let policyFindings =
                match optionalOne options "--policy" with
                | None -> []
                | Some path ->
                    match readPolicyFile path, state with
                    | Error reason, _ -> [ reason ]
                    | Ok(_, digest), AuthorizationState.Verified(a, _) when a.PolicyDigest <> digest ->
                        [ $"the authorization covers policy {a.PolicyDigest}, not {digest}" ]
                    | _ -> []

            let findings =
                match state with
                | AuthorizationState.Missing -> [ "no authorization file" ]
                | AuthorizationState.Invalid reason -> [ reason ]
                | AuthorizationState.Verified(a, _) ->
                    [ if revoked.Contains a.RunId then $"run {a.RunId} is revoked"
                      if DateTimeOffset.UtcNow >= a.ExpiresAt then "the authorization has expired"
                      if DateTimeOffset.UtcNow < a.IssuedAt then "the authorization is not yet valid" ]
                @ policyFindings

            let node = JsonObject()
            node["valid"] <- JsonValue.Create findings.IsEmpty
            match state with
            | AuthorizationState.Verified(a, digest) ->
                node["runId"] <- JsonValue.Create a.RunId
                node["owner"] <- JsonValue.Create a.Owner
                node["authorizationDigest"] <- JsonValue.Create digest
                node["expiresAt"] <- JsonValue.Create(Authorization.utc a.ExpiresAt)
            | _ -> ()
            let list = JsonArray()
            findings |> List.iter (fun f -> list.Add(JsonValue.Create f))
            node["findings"] <- list
            if json options then print node
            else
                printfn "authorization: %s" (if findings.IsEmpty then "valid" else "INVALID")
                findings |> List.iter (printfn "  %s")
            if findings.IsEmpty then 0 else 3

    let private revoke (options: Options) =
        match one options "--run-id", one options "--reason", one options "--out" with
        | Ok runId, Ok reason, Ok out ->
            let now = DateTimeOffset.UtcNow
            let revocation = { RunId = runId; RevokedAt = DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero); Reason = reason }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath out)) |> ignore
            File.WriteAllText(out, AutonomyJson.pretty (AutonomyJson.revocationNode revocation), UTF8Encoding false)
            printfn "revocation: %s (run %s)" out runId
            0
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> invalid e

    let private review (root: string) (options: Options) =
        match one options "--base", one options "--head" with
        | Ok baseRev, Ok headRev ->
            let declared =
                match optionalOne options "--policy" with
                | Some path when File.Exists path ->
                    match readPolicyFile path with
                    | Ok(policy, _) -> Ok policy.ProtectedPaths
                    | Error reason -> Error reason
                | Some path -> Error $"policy file {path} does not exist"
                | None -> Ok []

            match declared, LocalReview.resolve root baseRev, LocalReview.resolve root headRev with
            | Error reason, _, _ -> prerequisite reason
            | _, Error reason, _
            | _, _, Error reason -> prerequisite reason
            | Ok protectedPaths, Ok baseSha, Ok headSha ->
                let allowed = match many options "--allowed-path" with [] -> None | globs -> Some globs
                let text = LocalReview.eventText (optionalOne options "--event" |> Option.defaultValue "")

                match LocalReview.inputs root baseSha headSha text protectedPaths allowed with
                | Error reason -> prerequisite reason
                | Ok input ->
                    let report = Review.review input
                    let executor =
                        [ "kind", "praxis-autonomy-review"
                          "executor", (if Environment.GetEnvironmentVariable "GITHUB_ACTIONS" = "true" then "github-actions" else "local")
                          "runId", (Environment.GetEnvironmentVariable "GITHUB_RUN_ID" |> Option.ofObj |> Option.defaultValue "") ]
                    let node = LocalReview.reportNode baseSha headSha report executor
                    optionalOne options "--out" |> Option.iter (fun out -> File.WriteAllText(out, AutonomyJson.pretty node, UTF8Encoding false))
                    if json options then print node
                    else
                        printfn "independent review of %s against %s: %s" headSha baseSha (if report.Passed then "PASS" else "FAIL")
                        report.Findings
                        |> List.iter (fun f -> printfn "  %-8s %-28s %s %s" (ReviewSeverity.toWire f.Severity) f.Code (defaultArg f.Path "") f.Detail)
                    if report.Passed then 0 else 3
        | Error e, _
        | _, Error e -> invalid e

    let private lifecycleEnvironment (journalRoot: string) (poll: TimeSpan) =
        { GitHub = GitHubApi.port
          Journal = AutonomyStore.journal journalRoot
          Now = fun () -> DateTimeOffset.UtcNow
          Sleep = fun delay -> Threading.Thread.Sleep delay
          PollInterval = poll
          Log = fun line -> eprintfn "autonomy: %s" line }

    let private request (root: string) (options: Options) =
        match one options "--repository", one options "--pr", one options "--candidate" with
        | Ok repository, Ok pr, Ok candidate ->
            match Int32.TryParse pr with
            | false, _ -> Error(2, "--pr must be a number")
            | true, number ->
                loadAuthorization root options
                |> Result.mapError (fun reason -> 6, reason)
                |> Result.map (fun (state, revoked) ->
                    { Repository = repository
                      PullRequest = number
                      Candidate = candidate
                      Authorization = state
                      RevokedRunIds = revoked })
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error(2, e)

    let private decisionExit (decision: Decision) = if decision.Result = DecisionResult.Allow then 0 else 3

    let private printDecision (decision: Decision) =
        printfn "%s %s %s#%s -> %s @ %s" (DecisionResult.toWire decision.Result) (Operation.toWire decision.Operation)
            decision.Repository decision.CandidateSha decision.TargetBranch (Authorization.utc decision.EvaluatedAt)
        decision.Findings |> List.iter (fun f -> printfn "  %s: %s" f.Code f.Detail)

    let private evaluateCommand (root: string) (options: Options) =
        match request root options with
        | Error(code, message) -> if code = 2 then invalid message else prerequisite message
        | Ok req ->
            let env = lifecycleEnvironment (Path.GetTempPath()) (TimeSpan.FromSeconds 10.)
            match MergeLifecycle.gather env req MergeLifecycle.bootstrapFailure with
            | Error failure ->
                eprintfn "ERROR GitHub: %A" failure
                3
            | Ok context ->
                let decision = MergeLifecycle.evaluate env req context
                if json options then print (AutonomyJson.decisionNode decision) else printDecision decision
                decisionExit decision

    let private outcomeNode (outcome: LifecycleOutcome) =
        let node = JsonObject()
        node["outcome"] <- JsonValue.Create(LifecycleOutcome.code outcome)
        match outcome with
        | LifecycleOutcome.Merged(commit, digest)
        | LifecycleOutcome.AlreadyMerged(commit, digest) ->
            node["mergeCommitSha"] <- JsonValue.Create commit
            node["receiptDigest"] <- JsonValue.Create digest
        | LifecycleOutcome.Denied decision
        | LifecycleOutcome.RequiresOwner decision -> node["decision"] <- AutonomyJson.decisionNode decision
        | LifecycleOutcome.Reverify(code, detail)
        | LifecycleOutcome.Blocked(code, detail)
        | LifecycleOutcome.Failed(code, detail) ->
            node["code"] <- JsonValue.Create code
            node["detail"] <- JsonValue.Create detail
        node

    let private merge (root: string) (options: Options) =
        match request root options, one options "--journal" with
        | Error(code, message), _ -> if code = 2 then invalid message else prerequisite message
        | _, Error e -> invalid e
        | Ok req, Ok journal ->
            let poll = optionalOne options "--poll-seconds" |> Option.map float |> Option.defaultValue 15.
            let outcome = MergeLifecycle.run (lifecycleEnvironment journal (TimeSpan.FromSeconds poll)) req
            let node = outcomeNode outcome
            if json options then print node else printfn "%s" (node.ToJsonString())
            match outcome with
            | LifecycleOutcome.Merged _
            | LifecycleOutcome.AlreadyMerged _ -> 0
            | _ -> 3

    let private receiptsVerify (options: Options) =
        match one options "--journal" with
        | Error e -> invalid e
        | Ok journal ->
            match AutonomyStore.verifyChain (AutonomyStore.journal journal) with
            | Ok count ->
                printfn "receipt chain: %d receipt(s), intact" count
                0
            | Error problems ->
                problems |> List.iter (eprintfn "receipt chain: %s")
                3

    let private diagnose (options: Options) =
        match one options "--repository" with
        | Error e -> invalid e
        | Ok repository ->
            match GitHubApi.diagnose repository (optionalOne options "--branch") with
            | Ok node ->
                print node
                0
            | Error failure ->
                eprintfn "ERROR GitHub: %A" failure
                3

    let run (root: string) (arguments: string list) =
        let parsed rest = parse { Values = Map.empty; Flags = Set.empty } rest

        let dispatch rest (command: Options -> int) =
            match parsed rest with
            | Error message -> invalid message
            | Ok options -> command options

        match arguments with
        | "policy" :: "check" :: rest -> dispatch rest policyCheck
        | "authorize" :: "draft" :: rest -> dispatch rest draft
        | "authorize" :: "attach" :: rest -> dispatch rest attach
        | "authorize" :: "verify" :: rest -> dispatch rest (verify root)
        | "revoke" :: rest -> dispatch rest revoke
        | "review" :: rest -> dispatch rest (review root)
        | "evaluate" :: rest -> dispatch rest (evaluateCommand root)
        | "merge" :: rest -> dispatch rest (merge root)
        | "receipts" :: "verify" :: rest -> dispatch rest receiptsVerify
        | "diagnose" :: rest -> dispatch rest diagnose
        | _ -> invalid "unknown autonomy command"
