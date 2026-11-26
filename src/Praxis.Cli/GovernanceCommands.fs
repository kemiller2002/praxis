namespace Praxis.Cli

open System
open Praxis.Domain.Governance
open Praxis.Infrastructure.Governance

/// `praxis governance ...` and `praxis merge authorize` (WI-0116,
/// docs/governance/). Parses, delegates and prints. Exit codes: 0 satisfied,
/// 3 refused / drifted / findings / not ready, 2 invalid arguments, 1 failure.
[<RequireQualifiedAccess>]
module GovernanceCommands =
    let usage =
        String.concat " | "
            [ "governance inspect (--repository OWNER/NAME | --snapshot FILE) [--base REF] [--branch NAME] [--audit] [--save-snapshot FILE] [--out FILE] [--require LEVEL] [--json]"
              "governance drift --baseline REPORT (--repository OWNER/NAME | --snapshot FILE) [--base REF] [--json]"
              "governance audit [--repository OWNER/NAME] [--branch REF] [--limit N] [--since SHA] [--previous-head SHA] [--decisions DIR] [--json]"
              "governance authority [--base REF] [--json]"
              "governance bootstrap --repository OWNER/NAME --approved-by LOGIN --validation NAME=COMMAND... [--branch NAME] [--agent ID]* [--base REF]"
              "governance validations --policy FILE"
              "governance observe --candidate SHA --validation NAME --policy FILE"
              "governance attest --repository OWNER/NAME --run-id ID --pull-request N --candidate SHA --policy FILE --out DIR"
              "merge authorize --repository OWNER/NAME --candidate SHA [--base REF] [--pull-request N] [--work-item ID]* [--receipts DIR]* [--jwks FILE] [--governance-report FILE] [--live] [--evidence-out FILE] [--json]" ]

    let private value = ClaimArguments.optionValue
    let private values = ClaimArguments.optionValues
    let private required = ClaimArguments.required
    let private invalid = ClaimArguments.invalid usage
    let private json arguments = List.contains "--json" arguments
    let private baseRef arguments = value "--base" arguments |> Option.defaultValue "origin/main"

    let private fail (message: string) =
        eprintfn "ERROR %s" message
        1

    let private emit arguments (node: Text.Json.Nodes.JsonNode) (text: string) code =
        if json arguments then printfn "%s" (GovernanceJson.indented node) else printfn "%s" text
        code

    let private snapshotFor root arguments =
        match value "--snapshot" arguments, value "--repository" arguments with
        | Some file, _ -> GovernanceFiles.readJson file |> Result.map (fun raw -> raw, false)
        | None, Some repository -> Ok(GovernanceOperations.snapshot root repository (baseRef arguments) (value "--branch" arguments), true)
        | None, None -> Error "--repository or --snapshot is required"

    let private demonstratedFor root arguments live =
        match live && List.contains "--audit" arguments, value "--repository" arguments with
        | true, Some repository ->
            GovernanceOperations.audit
                { Root = root; Repository = Some repository; BranchRef = baseRef arguments; Limit = 20; Since = None; PreviousHead = None; DecisionDirectory = None; GateCheck = "praxis-merge-gate" }
            |> Result.map IntegrationAudit.demonstrated
            |> Result.defaultWith (fun e -> false, [ $"the integration audit failed: {e}" ])
        | _ -> false, [ "operational demonstration is not assessed (add --audit, or run governance audit)" ]

    let private inspect root arguments =
        match snapshotFor root arguments with
        | Error message -> invalid message
        | Ok(raw, live) ->
            let saved = value "--save-snapshot" arguments |> Option.map (fun path -> GovernanceFiles.writeText path (GovernanceJson.indented raw))

            match saved, GovernanceOperations.inspect root (baseRef arguments) raw (demonstratedFor root arguments live) with
            | Some(Error e), _
            | _, Error e -> fail e
            | _, Ok report ->
                let node = GovernanceJson.report report
                let written = value "--out" arguments |> Option.map (fun path -> GovernanceFiles.writeText path (GovernanceJson.indented node))

                let below =
                    match value "--require" arguments with
                    | None -> false
                    | Some level ->
                        let index l = ReadinessLevel.ordered |> List.findIndex ((=) l)
                        match ReadinessLevel.ordered |> List.tryFind (fun l -> ReadinessLevel.toWire l = level) with
                        | Some wanted -> index report.Level < index wanted
                        | None -> true

                match written with
                | Some(Error e) -> fail e
                | _ -> emit arguments node (GovernanceText.report report) (if below then 3 else 0)

    let private drift root arguments =
        match required "--baseline" arguments, snapshotFor root arguments with
        | Error m, _
        | _, Error m -> invalid m
        | Ok baseline, Ok(raw, _) ->
            match GovernanceFiles.readJson baseline |> Result.bind (fun n -> GovernanceJson.parseReport (n.ToJsonString())), GovernanceOperations.inspect root (baseRef arguments) raw (false, []) with
            | Error e, _
            | _, Error e -> fail e
            | Ok before, Ok now ->
                let findings = PlatformGovernance.drift before now
                let node = Text.Json.Nodes.JsonObject()
                node["schema"] <- Text.Json.Nodes.JsonValue.Create "praxis.governance-drift/1"
                node["drifted"] <- Text.Json.Nodes.JsonValue.Create(not findings.IsEmpty)
                node["findings"] <- Text.Json.Nodes.JsonArray(findings |> List.map (fun f -> GovernanceJson.finding f :> Text.Json.Nodes.JsonNode) |> List.toArray)
                let text = if findings.IsEmpty then "governance drift: none" else findings |> List.map (fun f -> $"DRIFT {f.Detail}") |> String.concat "\n"
                emit arguments node text (if findings.IsEmpty then 0 else 3)

    let private audit root arguments =
        match ClaimArguments.number "--limit" Int32.TryParse arguments with
        | Error m -> invalid m
        | Ok limit ->
            let request: AuditRequest =
                { Root = root
                  Repository = value "--repository" arguments
                  BranchRef = value "--branch" arguments |> Option.defaultValue "origin/main"
                  Limit = limit |> Option.defaultValue 50
                  Since = value "--since" arguments
                  PreviousHead = value "--previous-head" arguments
                  DecisionDirectory = value "--decisions" arguments
                  GateCheck = "praxis-merge-gate" }

            match GovernanceOperations.audit request with
            | Error e -> fail e
            | Ok report ->
                let errors = report.Findings |> List.exists (fun f -> f.Severity = "error")
                emit arguments (GovernanceJson.audit report) (GovernanceText.audit report) (if errors then 3 else 0)

    let private authority root arguments =
        match GovernanceOperations.authorityState root (baseRef arguments) with
        | Error e -> fail e
        | Ok state ->
            let d = AuthorityDiagnostics.diagnose state
            emit arguments (GovernanceJson.diagnosis d) (GovernanceText.diagnosis d) (if d.Ready then 0 else 3)

    let private bootstrap root arguments =
        let validations = values "--validation" arguments |> ClaimArguments.collect GovernanceFiles.validationArgument

        match required "--repository" arguments, required "--approved-by" arguments, validations with
        | Error m, _, _
        | _, Error m, _
        | _, _, Error m -> invalid m
        | Ok repository, Ok approvedBy, Ok validations ->
            let request: BootstrapRequest =
                { Root = root
                  Repository = repository
                  Branch = value "--branch" arguments |> Option.defaultValue "main"
                  ApprovedBy = approvedBy
                  AgentIdentities = values "--agent" arguments
                  Validations = validations
                  BaseRef = baseRef arguments
                  Now = GovernanceFiles.now () }

            match GovernanceBootstrap.bootstrap request with
            | Error e ->
                eprintfn "REFUSED %s" e
                3
            | Ok written ->
                written |> List.iter (printfn "wrote %s")
                printfn "next: commit these files on a branch and open a pull request; the owner reviews and merges it, then imports %s as the default-branch ruleset." GovernanceBootstrap.RulesetPath
                0

    let private validations arguments =
        match required "--policy" arguments with
        | Error m -> invalid m
        | Ok policy ->
            match GovernanceBootstrap.validations policy with
            | Ok text -> ClaimArguments.print (text, 0)
            | Error e -> fail e

    let private observe root arguments =
        match required "--candidate" arguments, required "--validation" arguments, required "--policy" arguments with
        | Error m, _, _
        | _, Error m, _
        | _, _, Error m -> invalid m
        | Ok candidate, Ok validation, Ok policy ->
            match GovernanceBootstrap.observe { Root = root; Candidate = candidate; Validation = validation; PolicyFile = policy } with
            | Error e -> fail e
            | Ok(passed, output) ->
                printfn "%s" output
                printfn "validation %s: %s" validation (if passed then "passed" else "NOT PASSED")
                if passed then 0 else 3

    let private attest root arguments =
        let fields = [ "--repository"; "--run-id"; "--candidate"; "--policy"; "--out" ] |> List.map (fun name -> required name arguments)

        match fields |> List.tryPick (function Error m -> Some m | Ok _ -> None), ClaimArguments.number "--pull-request" Int32.TryParse arguments with
        | Some m, _
        | None, Error m -> invalid m
        | None, Ok None -> invalid "--pull-request is required"
        | None, Ok(Some pr) ->
            let get name = required name arguments |> Result.defaultValue ""

            match GovernanceBootstrap.attest { Root = root; Repository = get "--repository"; RunId = get "--run-id"; PullRequest = pr; Candidate = get "--candidate"; PolicyFile = get "--policy"; OutDirectory = get "--out" } with
            | Error e -> fail e
            | Ok files ->
                files |> List.iter (printfn "attested %s")
                0

    let authorize root arguments =
        match required "--repository" arguments, required "--candidate" arguments, ClaimArguments.number "--pull-request" Int32.TryParse arguments with
        | Error m, _, _
        | _, Error m, _
        | _, _, Error m -> invalid m
        | Ok repository, Ok candidate, Ok pr ->
            let request: AuthorizeRequest =
                { Root = root
                  Repository = repository
                  BaseRef = baseRef arguments
                  Candidate = candidate
                  PullRequest = pr
                  WorkItems = values "--work-item" arguments
                  ReceiptDirectories = values "--receipts" arguments
                  JwksFile = value "--jwks" arguments
                  GovernanceReport = value "--governance-report" arguments
                  Live = List.contains "--live" arguments }

            match GovernanceOperations.authorize request with
            | Error e -> fail e
            | Ok decision ->
                let node = GovernanceJson.decision decision
                let code = if MergeOutcome.permitsMerge decision.Outcome then 0 else 3

                match value "--evidence-out" arguments |> Option.map (fun path -> GovernanceFiles.writeText path (GovernanceJson.indented node)) with
                | Some(Error e) -> fail e
                | _ -> emit arguments node (GovernanceText.decision decision) code

    let run root (arguments: string list) =
        match arguments with
        | "inspect" :: rest -> inspect root rest
        | "drift" :: rest -> drift root rest
        | "audit" :: rest -> audit root rest
        | "authority" :: rest -> authority root rest
        | "bootstrap" :: rest -> bootstrap root rest
        | "validations" :: rest -> validations rest
        | "observe" :: rest -> observe root rest
        | "attest" :: rest -> attest root rest
        | _ -> invalid "unknown governance command"
