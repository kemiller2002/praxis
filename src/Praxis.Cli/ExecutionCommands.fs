namespace Praxis.Cli

open System
open System.Text.Json.Nodes
open Praxis.Application.Execution
open Praxis.Contracts.Execution
open Praxis.Domain.Execution
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Execution

/// `praxis execution ...` -- the Praxis runtime of Ordo's execution contract.
/// This adapter parses arguments, calls `ExecutionService` and renders; every
/// rule, including the legal-action gate on every mutation, lives below it.
[<RequireQualifiedAccess>]
module ExecutionCommands =
    let usage =
        "execution start --work-item ID --role ROLE [--baseline REV] [--worktree] [--worktree-root DIR] [--scope SCOPE]* [--allow SCOPE=GLOB]* [--evaluator KIND=PATH]* [--evaluator-command CMD] [--human-only TRANSITION]* [--parent EXE-ID] [--containment-evidence FILE] [--launch] [--json] [IDENTITY] | execution show|actions|boundary|containment EXE-ID [--json] | execution list [--work-item ID] [--json] | execution step declare EXE-ID --step ID --expect-command CMD|--expect-artifact PATH|--expect-json JSON [--sequence N] [--depends-on ID]* [--retry-safe BASIS] | execution step run EXE-ID --step ID [--command CMD] | execution step start|observe EXE-ID --step ID [--observed-json JSON] | execution step reconcile EXE-ID --step ID --finding occurred|did-not-occur|unknown --detail TEXT | execution evaluate EXE-ID [--command CMD] [--evidence PATH]* | execution launch EXE-ID [--dry-run] | execution expand-scope EXE-ID --scope SCOPE [--allow SCOPE=GLOB]* --justification TEXT | execution resolve-effect EXE-ID --resource PATH --resolution reverted|expanded|transferred --detail TEXT | execution transition EXE-ID --action block|resume|complete|abandon [--reason TEXT] | execution rebind EXE-ID --reason TEXT | execution cleanup EXE-ID"

    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private hasFlag name (arguments: string list) = List.contains name arguments
    let private print (node: JsonNode) = printfn "%s" (node.ToJsonString ExecutionJson.options)

    let private exitWith (result: Result<int, ExecutionFailure>) =
        match result with
        | Ok code -> code
        | Error(ExecutionFailure.Invalid message) ->
            eprintfn "ERROR %s" message
            2
        | Error(ExecutionFailure.Refused message) ->
            eprintfn "REFUSED %s" message
            3

    let executionActor (actor: Actor) : ExecutionActor =
        { Id = actor.Id
          Kind = ActorKind.code actor.Kind
          Provider = actor.Provider |> Option.filter ((<>) Actor.UnknownValue)
          Model = actor.Model |> Option.filter ((<>) Actor.UnknownValue)
          Runtime = actor.Runtime |> Option.filter ((<>) Actor.UnknownValue) }

    let private parseJson (raw: string) =
        let text = if IO.File.Exists raw then IO.File.ReadAllText raw else raw

        try
            match JsonNode.Parse text with
            | null -> Error "empty JSON"
            | n -> Ok n
        with :? Text.Json.JsonException as ex ->
            Error ex.Message

    let private printLaunch (launcher: Launcher, environment: (string * string) list, fact: ObservedFact option) =
        printfn "launcher: %s (%s)" launcher.Id launcher.Command
        environment |> List.iter (fun (k, v) -> printfn "  %s=%s" k v)

        match fact with
        | Some(ObservedFact.CommandExited(_, code)) ->
            printfn "launcher exited %d" code
            if code = 0 then 0 else 3
        | Some _ ->
            printfn "launcher outcome unknown"
            3
        | None -> 0

    let private start ports (requester: Requester) actorKind (arguments: string list) =
        match optionValue "--work-item" arguments, optionValue "--role" arguments |> Option.bind ExecutionRole.tryParse with
        | None, _ -> Error(ExecutionFailure.Invalid "--work-item ID is required")
        | _, None -> Error(ExecutionFailure.Invalid "--role specification|implementation|verification|review|integration|administration is required")
        | Some workItem, Some role ->
            ExecutionService.start
                ports
                requester
                { WorkItem = workItem
                  Role = role
                  Baseline = optionValue "--baseline" arguments
                  Worktree = hasFlag "--worktree" arguments
                  WorktreeRoot = optionValue "--worktree-root" arguments
                  Scopes = optionValues "--scope" arguments
                  Allow = optionValues "--allow" arguments
                  Evaluators = optionValues "--evaluator" arguments
                  EvaluatorCommand = optionValue "--evaluator-command" arguments
                  HumanOnly = optionValues "--human-only" arguments
                  Parent = optionValue "--parent" arguments
                  ContainmentEvidence = optionValue "--containment-evidence" arguments }
            |> Result.bind (fun envelope ->
                if hasFlag "--json" arguments then
                    print (ExecutionJson.envelope envelope)
                else
                    printfn "work item:   %s" envelope.WorkItem
                    printfn "execution:   %s" envelope.ExecutionId
                    printfn "baseline:    %s" envelope.BaselineRevision
                    envelope.Workspace |> Option.bind _.Branch |> Option.iter (printfn "branch:      %s")
                    envelope.Workspace |> Option.iter (fun w -> printfn "workspace:   %s%s" w.Id (w.Path |> Option.map (fun p -> $" ({p})") |> Option.defaultValue ""))
                    printfn "actor:       %s (%s)" envelope.Actor.Id envelope.Actor.Kind
                    printfn "role:        %s" (ExecutionRole.toWire role)
                    printfn "containment: %s%s" (Containment.toWire envelope.Containment) (if Containment.isSecuritySandbox envelope.Containment then "" else " (not a security sandbox)")
                    envelope.Evaluator |> Option.iter (fun e -> printfn "evaluator:   %s" e.Fingerprint)

                // The start is accepted first; only then is the role's
                // launcher run (PRX-UI-025).
                if hasFlag "--launch" arguments then
                    ExecutionService.launch ports requester actorKind envelope.ExecutionId false |> Result.map printLaunch
                else
                    Ok 0)

    let private render (s: ExecutionSnapshot) (arguments: string list) =
        if hasFlag "--json" arguments then
            print (ExecutionJson.snapshot s)
        else
            printfn "%s  %s  %s  %s" s.Envelope.ExecutionId (ExecutionRole.toWire s.Envelope.Authority.Role) (ExecutionState.toWire s.Envelope.State) s.Envelope.WorkItem
            s.Observation.Steps |> List.iter (fun v -> printfn "  step %-20s %A (attempts %d)" v.StepId v.Status v.Attempts)
            s.Observation.Effects |> List.iter (fun e -> printfn "  scope effect: %s (%A)" e.Resource e.Classification)
            s.Observation.Divergence |> List.iter (printfn "  divergence: %s")

        0

    let private renderActions (s: ExecutionSnapshot) (arguments: string list) =
        if hasFlag "--json" arguments then
            print (ExecutionJson.legalActions s.LegalActions)
        else
            for a in s.LegalActions do
                let target = a.Target |> Option.map (fun t -> " " + t) |> Option.defaultValue ""
                let why = if a.Available then "" else "  -- " + String.concat "; " a.Reasons
                let human = if a.ActorRequirement = "human-required" then " (human required)" else ""
                printfn "%s %s%s%s%s" (if a.Available then "[legal]  " else "[blocked]") a.Transition target human why

        0

    let private renderBoundary (s: ExecutionSnapshot) (arguments: string list) =
        if hasFlag "--json" arguments then
            print (JsonArray(s.Observation.Effects |> List.map ExecutionJson.scopeEffect |> List.toArray))
        elif not (ExecutionEnvelope.declaresBoundary s.Envelope) then
            printfn "this execution declared no mutation boundary; nothing to compare"
        elif s.Observation.Effects.IsEmpty then
            printfn "every observed mutation is within the declared boundary"
        else
            s.Observation.Effects |> List.iter (fun e -> printfn "%s  %A" e.Resource e.Classification)

        if s.Observation.Effects.IsEmpty then 0 else 3

    let private renderContainment (s: ExecutionSnapshot) (arguments: string list) =
        if hasFlag "--json" arguments then
            print (ExecutionJson.containmentProfile s.Envelope.ContainmentProfile)
        else
            printfn "containment: %s (source: %s)" (Containment.toWire s.Envelope.Containment) (s.Envelope.ContainmentProfile.Source |> Option.defaultValue "no host report")

            for r in s.Envelope.ContainmentProfile.Restrictions do
                printfn "  %-12s %-12s %s" r.Dimension (RestrictionStatus.toWire r.Status) (r.Mechanism |> Option.defaultValue "")

        0

    let private step ports requester (s: ExecutionSnapshot) verb (rest: string list) =
        match optionValue "--step" rest with
        | None -> Error(ExecutionFailure.Invalid "--step ID is required")
        | Some stepId ->
            match verb with
            | "declare" ->
                let expected =
                    match optionValue "--expect-command" rest, optionValue "--expect-artifact" rest, optionValue "--expect-json" rest with
                    | Some c, _, _ -> Ok(ExpectedReceipt.CommandSucceeded c)
                    | _, Some a, _ -> Ok(ExpectedReceipt.ArtifactExists a)
                    | _, _, Some j -> parseJson j |> Result.bind ExecutionJson.readExpected
                    | _ -> Error "declare needs --expect-command, --expect-artifact or --expect-json"

                let sequence = optionValue "--sequence" rest |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None)

                expected
                |> Result.mapError ExecutionFailure.Invalid
                |> Result.bind (fun e -> ExecutionService.declareStep ports requester s stepId sequence (optionValue "--name" rest |> Option.defaultValue stepId) (optionValues "--depends-on" rest) e (optionValue "--retry-safe" rest))
                |> Result.map (fun () -> 0)
            | "start" -> ExecutionService.startStep ports requester s stepId |> Result.map (fun () -> 0)
            | "run" ->
                ExecutionService.runStep ports requester s stepId (optionValue "--command" rest)
                |> Result.map (fun outcome ->
                    printfn "step %s: %s" stepId (ReceiptOutcome.toWire outcome)
                    if outcome = ReceiptOutcome.Match then 0 else 3)
            | "observe" ->
                match optionValue "--observed-json" rest |> Option.map parseJson with
                | None -> Error(ExecutionFailure.Invalid "observe needs --observed-json JSON|FILE")
                | Some(Error e) -> Error(ExecutionFailure.Invalid e)
                | Some(Ok node) ->
                    ExecutionJson.readObserved node
                    |> Result.mapError ExecutionFailure.Refused
                    |> Result.bind (ExecutionService.observeStep ports requester s stepId)
                    |> Result.map (fun () -> 0)
            | "reconcile" ->
                let detail = optionValue "--detail" rest |> Option.defaultValue ""

                match optionValue "--finding" rest with
                | _ when String.IsNullOrWhiteSpace detail -> Error(ExecutionFailure.Invalid "--detail must say what reconciliation observed")
                | Some "occurred" -> ExecutionService.reconcileStep ports requester s stepId (Reconciliation.Occurred detail) |> Result.map (fun () -> 0)
                | Some "did-not-occur" -> ExecutionService.reconcileStep ports requester s stepId (Reconciliation.DidNotOccur detail) |> Result.map (fun () -> 0)
                | Some "unknown" -> ExecutionService.reconcileStep ports requester s stepId (Reconciliation.StillUnknown detail) |> Result.map (fun () -> 0)
                | _ -> Error(ExecutionFailure.Invalid "--finding occurred|did-not-occur|unknown is required")
            | other -> Error(ExecutionFailure.Invalid $"unknown step command '{other}'")

    let private withExecution ports actorKind (arguments: string list) (f: ExecutionSnapshot -> string list -> Result<int, ExecutionFailure>) =
        match arguments with
        | id :: rest when not (id.StartsWith "--") -> ExecutionService.snapshot ports actorKind id |> Result.bind (fun s -> f s rest)
        | _ -> Error(ExecutionFailure.Invalid "an execution id is required")

    let run (root: string) (actor: Actor) (arguments: string list) =
        let ports = FileExecutionPorts.create root
        let requester = { Actor = executionActor actor }
        let kind = ActorKind.code actor.Kind
        let on = withExecution ports kind

        match arguments with
        | "start" :: rest -> start ports requester kind rest |> exitWith
        | "show" :: rest -> on rest (fun s _ -> Ok(render s rest)) |> exitWith
        | "actions" :: rest -> on rest (fun s _ -> Ok(renderActions s rest)) |> exitWith
        | "boundary" :: rest -> on rest (fun s _ -> Ok(renderBoundary s rest)) |> exitWith
        | "containment" :: rest -> on rest (fun s _ -> Ok(renderContainment s rest)) |> exitWith
        | "list" :: rest ->
            let envelopes = ExecutionService.list ports (optionValue "--work-item" rest)

            if hasFlag "--json" rest then
                print (JsonArray(envelopes |> List.map ExecutionJson.envelope |> List.toArray))
            else
                envelopes
                |> List.iter (fun e -> printfn "%s  %-14s %-10s %s" e.ExecutionId (ExecutionRole.toWire e.Authority.Role) (ExecutionState.toWire e.State) e.WorkItem)

            0
        | "step" :: verb :: rest -> on rest (fun s more -> step ports requester s verb more) |> exitWith
        | "evaluate" :: rest ->
            on rest (fun s more ->
                ExecutionService.evaluate ports requester s (optionValue "--command" more) (optionValues "--evidence" more)
                |> Result.map (fun record ->
                    printfn "evaluation: %s" (EvaluationOutcome.toWire record.Outcome)
                    match record.Outcome with
                    | EvaluationOutcome.Passed _ -> 0
                    | _ -> 3))
            |> exitWith
        | "launch" :: rest -> on rest (fun s more -> ExecutionService.launch ports requester kind s.Envelope.ExecutionId (hasFlag "--dry-run" more) |> Result.map printLaunch) |> exitWith
        | "expand-scope" :: rest ->
            on rest (fun s more ->
                ExecutionService.expandScope ports requester s (optionValues "--scope" more) (optionValues "--allow" more) (optionValue "--justification" more |> Option.defaultValue "")
                |> Result.map (fun expansion ->
                    printfn "scope expanded (%s)" expansion
                    0))
            |> exitWith
        | "resolve-effect" :: rest ->
            on rest (fun s more ->
                match optionValue "--resource" more, optionValue "--resolution" more, optionValue "--detail" more with
                | Some resource, Some resolution, Some detail when List.contains resolution [ "reverted"; "expanded"; "transferred" ] ->
                    ExecutionService.resolveEffect ports requester s resource resolution detail |> Result.map (fun () -> 0)
                | _ -> Error(ExecutionFailure.Invalid "--resource PATH --resolution reverted|expanded|transferred --detail TEXT are required"))
            |> exitWith
        | "transition" :: rest ->
            on rest (fun s more ->
                ExecutionService.transition ports requester s (optionValue "--action" more |> Option.defaultValue "") (optionValue "--reason" more |> Option.defaultValue "")
                |> Result.map (fun state ->
                    printfn "%s -> %s" s.Envelope.ExecutionId (ExecutionState.toWire state)
                    0))
            |> exitWith
        | "rebind" :: rest ->
            on rest (fun s more ->
                ExecutionService.rebind ports requester s (optionValue "--reason" more |> Option.defaultValue "")
                |> Result.map (fun divergence ->
                    divergence |> List.iter (printfn "rebound despite: %s")
                    0))
            |> exitWith
        | "cleanup" :: rest ->
            on rest (fun s _ ->
                ExecutionService.cleanup ports requester s
                |> Result.map (fun () ->
                    printfn "workspace removed; execution %s and its branch are preserved" s.Envelope.ExecutionId
                    0))
            |> exitWith
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
