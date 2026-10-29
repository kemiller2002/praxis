namespace Praxis.Cli

open System
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy
open Praxis.Infrastructure.Autonomy

/// `praxis autonomy`: unattended execution under an owner-signed run
/// authorization (docs/autonomous-execution/). Parses, delegates to
/// AutonomyOperations and prints. Exit codes: 0 allowed, merged or verified;
/// 3 denied, blocked, requires the owner, must be re-verified, or failed
/// verification; 2 invalid arguments; 6 a prerequisite is unusable.
[<RequireQualifiedAccess>]
module AutonomyCommands =
    let private trust = "(--trust-store FILE | --trust-store-env NAME)"

    let usage =
        String.concat " | "
            [ "autonomy policy check --policy FILE [--json]"
              "autonomy authorize draft --policy FILE --run-id ID --owner LOGIN --key-id KEY --repository OWNER/REPO* --branch GLOB* --operation OP* --hours N [--issued-at TIMESTAMP] --out FILE"
              "autonomy authorize attach --draft FILE --signature FILE --out FILE"
              $"autonomy authorize verify --authorization FILE {trust} [--revocations DIR]* [--policy FILE] [--json]"
              "autonomy revoke --run-id ID --reason TEXT --out FILE"
              "autonomy review --base REV --head REV [--policy FILE] [--event FILE] [--allowed-path GLOB]* [--out FILE] [--json]"
              $"autonomy evaluate --repository OWNER/REPO --pr N --candidate SHA --authorization FILE {trust} [--revocations DIR]* [--json]"
              $"autonomy merge --repository OWNER/REPO --pr N --candidate SHA --authorization FILE {trust} --journal DIR [--revocations DIR]* [--poll-seconds N] [--json]"
              "autonomy receipts verify --journal DIR"
              "autonomy observations --journal DIR --repository OWNER/REPO --out FILE"
              "autonomy diagnose --repository OWNER/REPO [--branch NAME]" ]

    type private Options = { Values: Map<string, string list>; Json: bool }

    let rec private parse (options: Options) (arguments: string list) =
        match arguments with
        | [] -> Ok options
        | "--json" :: rest -> parse { options with Json = true } rest
        | name :: value :: rest when name.StartsWith "--" && not (value.StartsWith "--") ->
            let existing = options.Values |> Map.tryFind name |> Option.defaultValue []
            parse { options with Values = options.Values.Add(name, existing @ [ value ]) } rest
        | other :: _ -> Error $"unexpected argument '{other}'"

    let private many (o: Options) name = o.Values |> Map.tryFind name |> Option.defaultValue []
    let private opt (o: Options) name = match many o name with [ v ] -> Some v | _ -> None

    let private one (o: Options) name =
        match many o name with
        | [ v ] -> Ok v
        | [] -> Error $"{name} is required"
        | _ -> Error $"{name} may be given once"

    let private print (node: JsonNode) = printf "%s" (AutonomyJson.pretty node)
    let private fail code (message: string) = eprintfn "ERROR %s" message; code
    let private invalid message = fail 2 $"{message}; expected {usage}"
    let private log line = eprintfn "autonomy: %s" line

    let private trustSource (o: Options) =
        match opt o "--trust-store", opt o "--trust-store-env" with
        | Some path, None -> Ok(TrustSource.File path)
        | None, Some name -> Ok(TrustSource.Environment name)
        | _ -> Error "exactly one of --trust-store or --trust-store-env is required"

    let private authority root (o: Options) =
        one o "--authorization"
        |> Result.bind (fun path -> trustSource o |> Result.map (fun source -> AutonomyOperations.authority root path source (many o "--revocations")))

    let private request root (o: Options) =
        match one o "--repository", one o "--pr", one o "--candidate", authority root o with
        | Ok repository, Ok pr, Ok candidate, Ok current ->
            match Int32.TryParse pr with
            | true, number -> Ok { Repository = repository; PullRequest = number; Candidate = candidate; Authority = current }
            | _ -> Error "--pr must be a number"
        | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ | _, _, _, Error e -> Error e

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

    let private policyCheck (o: Options) =
        match one o "--policy" |> Result.bind AutonomyOperations.readPolicyFile with
        | Error reason -> fail 3 reason
        | Ok(policy, digest) ->
            if o.Json then
                let node = JsonObject()
                node["valid"] <- JsonValue.Create true
                node["policyVersion"] <- JsonValue.Create policy.PolicyVersion
                node["policyDigest"] <- JsonValue.Create digest
                print node
            else printfn "policy: valid (version %d)\ndigest: %s" policy.PolicyVersion digest
            0

    let private draft (o: Options) =
        let operations =
            many o "--operation"
            |> List.fold (fun acc v -> acc |> Result.bind (fun ops -> match Operation.tryParse v with Some op -> Ok(ops @ [ op ]) | None -> Error $"unknown operation '{v}'")) (Ok [])
        match one o "--policy", one o "--run-id", one o "--owner", one o "--key-id", one o "--hours", one o "--out", operations with
        | Ok policy, Ok runId, Ok owner, Ok keyId, Ok hours, Ok out, Ok ops when not (ops.IsEmpty || (many o "--repository").IsEmpty || (many o "--branch").IsEmpty) ->
            match Int32.TryParse hours with
            | false, _ -> invalid "--hours must be an integer"
            | true, hours ->
                let request =
                    { PolicyPath = policy; RunId = runId; Owner = owner; KeyId = keyId; Repositories = many o "--repository"
                      Branches = many o "--branch"; Operations = ops; Hours = hours
                      IssuedAt = opt o "--issued-at" |> Option.map DateTimeOffset.Parse; Out = out }
                match AutonomyOperations.draft request with
                | Error reason -> fail 6 reason
                | Ok payload ->
                    printfn "draft: %s\nbytes to sign: %s" out payload
                    printfn "the owner signs them with their own P-256 key: openssl dgst -sha256 -sign owner.key -out %s.sig %s" out payload
                    printfn "then: praxis autonomy authorize attach --draft %s --signature %s.sig --out AUTHORIZATION.json" out out
                    0
        | Error e, _, _, _, _, _, _ | _, Error e, _, _, _, _, _ | _, _, Error e, _, _, _, _ | _, _, _, Error e, _, _, _
        | _, _, _, _, Error e, _, _ | _, _, _, _, _, Error e, _ | _, _, _, _, _, _, Error e -> invalid e
        | _ -> invalid "at least one --repository, --branch and --operation is required"

    let private attach (o: Options) =
        match one o "--draft", one o "--signature", one o "--out" with
        | Ok draftPath, Ok signature, Ok out ->
            match AutonomyOperations.attach draftPath signature out with
            | Ok digest -> printfn "authorization: %s\ndigest: %s" out digest; 0
            | Error reason -> fail 6 reason
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> invalid e

    let private verify root (o: Options) =
        match authority root o with
        | Error e -> invalid e
        | Ok current ->
            let node, findings = AutonomyOperations.verify (current ()) (opt o "--policy")
            if o.Json then print node
            else
                printfn "authorization: %s" (if findings.IsEmpty then "valid" else "INVALID")
                findings |> List.iter (printfn "  %s")
            if findings.IsEmpty then 0 else 3

    let private review root (o: Options) =
        match one o "--base", one o "--head" with
        | Ok baseRev, Ok headRev ->
            let allowed = match many o "--allowed-path" with [] -> None | globs -> Some globs
            match AutonomyOperations.review root baseRev headRev (opt o "--policy") (opt o "--event") allowed (opt o "--out") with
            | Error reason -> fail 6 reason
            | Ok(report, node, baseSha, headSha) ->
                if o.Json then print node
                else
                    printfn "independent review of %s against %s: %s" headSha baseSha (if report.Passed then "PASS" else "FAIL")
                    report.Findings |> List.iter (fun f -> printfn "  %-8s %-28s %s %s" (ReviewSeverity.toWire f.Severity) f.Code (defaultArg f.Path "") f.Detail)
                if report.Passed then 0 else 3
        | Error e, _ | _, Error e -> invalid e

    let private evaluate root (o: Options) =
        match request root o with
        | Error e -> invalid e
        | Ok req ->
            match AutonomyOperations.evaluate req log with
            | Error failure -> fail 3 $"GitHub: %A{failure}"
            | Ok decision ->
                if o.Json then print (AutonomyJson.decisionNode decision)
                else
                    printfn "%s %s %s @ %s" (DecisionResult.toWire decision.Result) (Operation.toWire decision.Operation) decision.CandidateSha decision.TargetBranch
                    decision.Findings |> List.iter (fun f -> printfn "  %s: %s" f.Code f.Detail)
                if decision.Result = DecisionResult.Allow then 0 else 3

    let private merge root (o: Options) =
        match request root o, one o "--journal" with
        | Error e, _ | _, Error e -> invalid e
        | Ok req, Ok journal ->
            let poll = opt o "--poll-seconds" |> Option.map float |> Option.defaultValue 15.
            let outcome = AutonomyOperations.merge req journal (TimeSpan.FromSeconds poll) log
            let node = outcomeNode outcome
            if o.Json then print node else printfn "%s" (node.ToJsonString())
            match outcome with
            | LifecycleOutcome.Merged _
            | LifecycleOutcome.AlreadyMerged _ -> 0
            | _ -> 3

    let private receipts (o: Options) =
        match one o "--journal" with
        | Error e -> invalid e
        | Ok journal ->
            match AutonomyOperations.verifyReceipts journal with
            | Ok count -> printfn "receipt chain: %d receipt(s), intact" count; 0
            | Error problems -> problems |> List.iter (eprintfn "receipt chain: %s"); 3

    let private observations (o: Options) =
        match one o "--journal", one o "--repository", one o "--out" with
        | Ok journal, Ok repository, Ok out ->
            match AutonomyOperations.observations journal repository out with
            | Ok count -> printfn "observations: %d merge(s) confirmed on GitHub -> %s" count out; 0
            | Error problems -> problems |> List.iter (eprintfn "ERROR %s"); 3
        | Error e, _, _ | _, Error e, _ | _, _, Error e -> invalid e

    let private diagnose (o: Options) =
        match one o "--repository" with
        | Error e -> invalid e
        | Ok repository ->
            match GitHubApi.diagnose repository (opt o "--branch") with
            | Ok node -> print node; 0
            | Error failure -> fail 3 $"GitHub: %A{failure}"

    let run (root: string) (arguments: string list) =
        let dispatch rest command =
            match parse { Values = Map.empty; Json = false } rest with
            | Error message -> invalid message
            | Ok options -> command options

        match arguments with
        | "policy" :: "check" :: rest -> dispatch rest policyCheck
        | "authorize" :: "draft" :: rest -> dispatch rest draft
        | "authorize" :: "attach" :: rest -> dispatch rest attach
        | "authorize" :: "verify" :: rest -> dispatch rest (verify root)
        | "revoke" :: rest ->
            dispatch rest (fun o ->
                match one o "--run-id", one o "--reason", one o "--out" with
                | Ok runId, Ok reason, Ok out -> AutonomyOperations.revoke runId reason out; printfn "revocation: %s (run %s)" out runId; 0
                | Error e, _, _ | _, Error e, _ | _, _, Error e -> invalid e)
        | "review" :: rest -> dispatch rest (review root)
        | "evaluate" :: rest -> dispatch rest (evaluate root)
        | "merge" :: rest -> dispatch rest (merge root)
        | "receipts" :: "verify" :: rest -> dispatch rest receipts
        | "observations" :: rest -> dispatch rest observations
        | "diagnose" :: rest -> dispatch rest diagnose
        | _ -> invalid "unknown autonomy command"
