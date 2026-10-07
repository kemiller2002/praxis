namespace Ros.Cli

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Execution
open Ros.Domain.Execution
open Ros.Domain.Provenance
open Ros.Infrastructure.Execution

/// `praxis execution ...` — the Praxis runtime of Ordo's execution contract:
/// first-class envelopes, optional worktree-per-execution, the durable step
/// ledger, mutation-boundary observation, legal scope expansion, evaluator
/// identity at verdict time, and the one legal-action computation every
/// presentation consumes (`execution actions --json`). Every mutation here
/// is checked against `LegalActions.compute` first; nothing else decides
/// legality.
[<RequireQualifiedAccess>]
module ExecutionCommands =
    let usage =
        "execution start --work-item ID --role ROLE [--baseline REV] [--worktree] [--worktree-root DIR] [--scope SCOPE]* [--allow SCOPE=GLOB]* [--evaluator KIND=PATH]* [--human-only TRANSITION]* [--parent EXE-ID] [--json] [IDENTITY] | execution show|actions|boundary EXE-ID [--json] | execution list [--work-item ID] [--json] | execution step declare EXE-ID --step ID --expect-command CMD|--expect-artifact PATH|--expect-json JSON [--sequence N] [--depends-on ID]* [--retry-safe BASIS] | execution step run EXE-ID --step ID [--command CMD] | execution step start|observe EXE-ID --step ID [--observed-json JSON] | execution step reconcile EXE-ID --step ID --finding occurred|did-not-occur|unknown --detail TEXT | execution evaluate EXE-ID --command CMD | execution expand-scope EXE-ID --scope SCOPE [--allow SCOPE=GLOB]* --justification TEXT | execution resolve-effect EXE-ID --resource PATH --resolution reverted|expanded|transferred --detail TEXT | execution transition EXE-ID --action block|resume|complete|abandon [--reason TEXT] | execution cleanup EXE-ID"

    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValues (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private hasFlag name (arguments: string list) = List.contains name arguments

    let private fail (message: string) =
        eprintfn "ERROR %s" message
        2

    let private refuse (message: string) =
        eprintfn "REFUSED %s" message
        3

    let private print (node: JsonNode) = printfn "%s" (node.ToJsonString ExecutionJson.options)

    let private executionActor (actor: Actor) : ExecutionActor =
        { Id = actor.Id
          Kind = ActorKind.code actor.Kind
          Provider = actor.Provider |> Option.filter ((<>) Actor.UnknownValue)
          Model = actor.Model |> Option.filter ((<>) Actor.UnknownValue)
          Runtime = actor.Runtime |> Option.filter ((<>) Actor.UnknownValue) }

    /// A globally unambiguous work identity: `<owner/repo>:<WORK-ID>` when the
    /// repository identity is known.
    let qualifyWorkItem (root: string) (raw: string) =
        if raw.Contains ':' || raw.Contains '#' then
            raw
        else
            match GitWorkspace.remoteUrl root |> Option.bind Ros.Domain.Installation.Target.repositoryFromRemote with
            | Some repo -> repo + ":" + raw
            | None -> raw

    let private localWorkId (workItem: string) =
        let local = match workItem.LastIndexOfAny [| ':'; '#' |] with -1 -> workItem | i -> workItem.Substring(i + 1)
        local |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.' then c else '-')

    let private evaluatorFrom (root: string) (specs: string list) =
        specs
        |> List.map (fun spec ->
            match spec.IndexOf '=' with
            | -1 -> Error $"--evaluator expects KIND=PATH, got '{spec}'"
            | i ->
                let kind, path = spec.Substring(0, i), spec.Substring(i + 1)
                GitWorkspace.fileDigest (Path.Combine(root, path)) |> Result.map (fun digest -> { Kind = kind; Reference = path.Replace('\\', '/'); Digest = digest }))
        |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
        |> Result.map List.rev

    let private boundaryFrom (arguments: string list) =
        let scopes = optionValues "--scope" arguments

        let projections =
            optionValues "--allow" arguments
            |> List.choose (fun spec ->
                match spec.LastIndexOf '=' with
                | -1 -> None
                | i -> Some { Scope = spec.Substring(0, i); Patterns = [ spec.Substring(i + 1) ] })
            |> List.groupBy _.Scope
            |> List.map (fun (scope, ps) -> { Scope = scope; Patterns = ps |> List.collect _.Patterns })

        match scopes |> List.tryFind (MutationBoundary.isScope >> not) with
        | Some bad -> Error $"'{bad}' is not a semantic scope (feature:|cluster:|authority:|capability:<id>)"
        | None -> Ok { Scopes = scopes; Projections = projections; EvaluatorReferences = [] }

    let private workspaceDirectory (root: string) (envelope: ExecutionEnvelope) =
        match envelope.Workspace |> Option.bind _.Path with
        | Some relative -> Path.GetFullPath(Path.Combine(root, relative))
        | None -> root

    let private readVerification root id =
        ExecutionStore.readRecords root id "verification"
        |> List.tryLast
        |> Option.bind (fun node ->
            match ExecutionStore.recordField "outcome" node, ExecutionStore.recordField "evaluator" node, ExecutionStore.recordField "current" node with
            | Some "passed", Some fp, _ -> Some(EvaluationOutcome.Passed fp)
            | Some "failed", Some fp, _ -> Some(EvaluationOutcome.Failed(fp, ExecutionStore.recordField "reason" node |> Option.defaultValue ""))
            | Some "evaluator-changed", Some fp, Some current -> Some(EvaluationOutcome.EvaluatorChanged(fp, current, []))
            | Some "evaluator-unavailable", _, _ -> Some(EvaluationOutcome.EvaluatorUnavailable(ExecutionStore.recordField "reason" node |> Option.defaultValue ""))
            | _ -> None)

    /// Unresolved out-of-boundary mutations, observed from Git.
    let private scopeEffects root (envelope: ExecutionEnvelope) =
        match GitWorkspace.changedPaths (workspaceDirectory root envelope) envelope.BaselineRevision with
        | Error _ -> []
        | Ok paths ->
            let resolved = ExecutionStore.resolvedResources root envelope.ExecutionId

            paths
            |> List.filter (fun p -> not (p.StartsWith(".ros/", StringComparison.Ordinal)) && not (resolved.Contains p))
            |> List.map (fun p -> p, None)
            |> MutationBoundary.scopeEffects envelope.Boundary

    type private Snapshot =
        { Envelope: ExecutionEnvelope
          Entries: StepEntry list
          Steps: StepView list
          Effects: ScopeEffect list
          Verification: EvaluationOutcome option
          Uncommitted: bool }

    let private load root id =
        ExecutionStore.loadEnvelope root id
        |> Result.bind (fun envelope ->
            ExecutionStore.readEntries root id
            |> Result.map (fun entries ->
                { Envelope = envelope
                  Entries = entries
                  Steps = StepLedger.reconstruct entries
                  Effects = scopeEffects root envelope
                  Verification = readVerification root id
                  Uncommitted = GitWorkspace.hasUncommittedChanges (workspaceDirectory root envelope) }))

    let private legal (snapshot: Snapshot) (actor: Actor) =
        LegalActions.compute snapshot.Envelope snapshot.Steps snapshot.Effects snapshot.Verification snapshot.Uncommitted (ActorKind.code actor.Kind)

    /// The single legality gate for every mutating command.
    let private requireLegal snapshot actor transition (target: string option) (run: unit -> int) =
        match legal snapshot actor |> List.tryFind (fun a -> a.Transition = transition && (target.IsNone || a.Target = target)) with
        | Some a when a.Available -> run ()
        | Some a -> refuse (transition + " is not legal now: " + String.concat "; " a.Reasons)
        | None -> refuse $"{transition} is not a legal action for this execution"

    let private snapshotNode (s: Snapshot) (actor: Actor) : JsonNode =
        let node = ExecutionJson.envelope s.Envelope
        let steps = JsonArray()

        s.Steps
        |> List.iter (fun v ->
            let step = JsonObject()
            step["stepId"] <- JsonValue.Create v.StepId
            step["sequence"] <- JsonValue.Create v.Sequence
            step["name"] <- JsonValue.Create v.Name
            step["attempts"] <- JsonValue.Create v.Attempts

            step["status"] <-
                JsonValue.Create(
                    match v.Status with
                    | StepStatus.NotStarted -> "not-started"
                    | StepStatus.Satisfied -> "match"
                    | StepStatus.Mismatched -> "mismatch"
                    | StepStatus.EffectUnknown -> "indeterminate"
                    | StepStatus.ReconciledNotOccurred -> "reconciled-not-occurred"
                    | StepStatus.ReconciledOccurred -> "reconciled-occurred"
                )

            step["expected"] <- ExecutionJson.expected v.Expected
            steps.Add step)

        node["steps"] <- steps
        let effects = JsonArray()
        s.Effects |> List.iter (ExecutionJson.scopeEffect >> effects.Add)
        node["scopeEffects"] <- effects
        node["verification"] <- (s.Verification |> Option.map (EvaluationOutcome.toWire >> JsonValue.Create >> fun v -> v :> JsonNode) |> Option.toObj)
        node["legalActions"] <- ExecutionJson.legalActions (legal s actor)
        node

    let private start (root: string) (actor: Actor) (arguments: string list) =
        let now = DateTimeOffset.UtcNow

        match optionValue "--work-item" arguments, optionValue "--role" arguments |> Option.bind ExecutionRole.tryParse with
        | None, _ -> fail "--work-item ID is required"
        | _, None -> fail "--role specification|implementation|verification|review|integration|administration is required"
        | Some rawWork, Some role ->
            let workItem = qualifyWorkItem root rawWork

            let baseline =
                optionValue "--baseline" arguments
                |> Option.map (GitWorkspace.resolve root)
                |> Option.defaultWith (fun () -> GitWorkspace.head root)

            let evaluator =
                match optionValues "--evaluator" arguments with
                | [] -> Ok None
                | specs -> evaluatorFrom root specs |> Result.bind EvaluatorIdentity.create |> Result.map Some

            match baseline, evaluator, boundaryFrom arguments with
            | Error e, _, _ -> fail $"cannot resolve the baseline revision: {e}"
            | _, Error e, _
            | _, _, Error e -> fail e
            | Ok baselineSha, Ok ev, Ok boundary ->
                let id = ExecutionStore.newId now

                match ExecutionEnvelope.create id workItem (executionActor actor) (RoleAuthority.defaultFor role) baselineSha boundary ev now with
                | Error e -> refuse e
                | Ok envelope ->
                    let bound =
                        if hasFlag "--worktree" arguments then
                            let rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath root))
                            let parent = optionValue "--worktree-root" arguments |> Option.defaultValue (Path.Combine(root, "..", rootName + ".worktrees"))
                            let path = Path.GetFullPath(Path.Combine(parent, id))
                            let branch = $"praxis/{localWorkId workItem}/{id}"

                            GitWorkspace.createWorktree root branch path baselineSha
                            |> Result.map (fun _ ->
                                { envelope with
                                    Workspace =
                                        Some
                                            { Id = "worktree:" + id
                                              Branch = Some branch
                                              Path = Some(GitWorkspace.relativeTo root path)
                                              Mechanism = "git-worktree" }
                                    Containment = Containment.SemanticOnly "git-worktree" })
                        else
                            Ok
                                { envelope with
                                    Workspace = Some { Id = "checkout:" + id; Branch = None; Path = None; Mechanism = "working-directory" }
                                    Containment = Containment.SemanticOnly "working-directory" }

                    match bound with
                    | Error e -> fail $"could not create the execution worktree: {e}"
                    | Ok envelope ->
                        let envelope =
                            { envelope with
                                HumanOnlyTransitions = optionValues "--human-only" arguments
                                Parent = optionValue "--parent" arguments }

                        ExecutionStore.saveEnvelope root envelope
                        ExecutionStore.appendRecord root id "transition" [ "transition", Some "execution.start"; "state", Some "active"; "actor", Some actor.Id ] now

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
                            printfn "containment: %s (not a security sandbox)" (Containment.toWire envelope.Containment)
                            envelope.Evaluator |> Option.iter (fun e -> printfn "evaluator:   %s" e.Fingerprint)

                        0

    let private withSnapshot root id (f: Snapshot -> int) =
        match load root id with
        | Error e -> fail e
        | Ok s -> f s

    let private show root actor (arguments: string list) =
        match arguments |> List.filter (fun a -> not (a.StartsWith "--")) with
        | id :: _ ->
            withSnapshot root id (fun s ->
                if hasFlag "--json" arguments then
                    print (snapshotNode s actor)
                else
                    printfn "%s  %s  %s  %s" s.Envelope.ExecutionId (ExecutionRole.toWire s.Envelope.Authority.Role) (ExecutionState.toWire s.Envelope.State) s.Envelope.WorkItem

                    s.Steps
                    |> List.iter (fun v -> printfn "  step %-20s %A (attempts %d)" v.StepId v.Status v.Attempts)

                    s.Effects |> List.iter (fun e -> printfn "  scope effect: %s (%A)" e.Resource e.Classification)

                0)
        | [] -> fail "an execution id is required"

    let private actions root actor (arguments: string list) =
        match arguments |> List.filter (fun a -> not (a.StartsWith "--")) with
        | id :: _ ->
            withSnapshot root id (fun s ->
                let computed = legal s actor

                if hasFlag "--json" arguments then
                    print (ExecutionJson.legalActions computed)
                else
                    computed
                    |> List.iter (fun a ->
                        let target = a.Target |> Option.map (fun t -> " " + t) |> Option.defaultValue ""
                        let why = if a.Available then "" else "  -- " + String.concat "; " a.Reasons
                        printfn "%s %s%s%s" (if a.Available then "[legal]  " else "[blocked]") a.Transition target why)

                0)
        | [] -> fail "an execution id is required"

    let private list root (arguments: string list) =
        let filter = optionValue "--work-item" arguments |> Option.map (qualifyWorkItem root)

        let envelopes =
            ExecutionStore.list root
            |> List.choose (fun id -> ExecutionStore.loadEnvelope root id |> Result.toOption)
            |> List.filter (fun e -> filter |> Option.forall (fun w -> e.WorkItem = w))

        if hasFlag "--json" arguments then
            let a = JsonArray()
            envelopes |> List.iter (ExecutionJson.envelope >> a.Add)
            print a
        else
            envelopes
            |> List.iter (fun e -> printfn "%s  %-14s %-10s %s" e.ExecutionId (ExecutionRole.toWire e.Authority.Role) (ExecutionState.toWire e.State) e.WorkItem)

        0

    let private boundary root (arguments: string list) =
        match arguments |> List.filter (fun a -> not (a.StartsWith "--")) with
        | id :: _ ->
            withSnapshot root id (fun s ->
                if hasFlag "--json" arguments then
                    let a = JsonArray()
                    s.Effects |> List.iter (ExecutionJson.scopeEffect >> a.Add)
                    print a
                elif s.Effects.IsEmpty then
                    printfn "every observed mutation is within the declared boundary"
                else
                    s.Effects |> List.iter (fun e -> printfn "%s  %A" e.Resource e.Classification)

                if s.Effects.IsEmpty then 0 else 3)
        | [] -> fail "an execution id is required"

    let private parseJson (raw: string) =
        let text = if File.Exists raw then File.ReadAllText raw else raw

        try
            match JsonNode.Parse text with
            | null -> Error "empty JSON"
            | n -> Ok n
        with ex ->
            Error ex.Message

    let private runShell (cwd: string) (command: string) =
        let info = ProcessStartInfo("/bin/sh", [ "-c"; command ])
        info.WorkingDirectory <- cwd
        info.UseShellExecute <- false

        try
            use p = Process.Start info
            p.WaitForExit()
            ObservedFact.CommandExited(command, p.ExitCode)
        with ex ->
            ObservedFact.CommandOutcomeUnknown(command, ex.Message)

    let private step root actor (arguments: string list) =
        let now = DateTimeOffset.UtcNow

        match arguments with
        | verb :: id :: rest ->
            withSnapshot root id (fun s ->
                match optionValue "--step" rest with
                | None -> fail "--step ID is required"
                | Some stepId ->
                    let append entry =
                        ExecutionStore.appendEntry root id entry
                        0

                    match verb with
                    | "declare" ->
                        let expected =
                            match optionValue "--expect-command" rest, optionValue "--expect-artifact" rest, optionValue "--expect-json" rest with
                            | Some c, _, _ -> Ok(ExpectedReceipt.CommandSucceeded c)
                            | _, Some a, _ -> Ok(ExpectedReceipt.ArtifactExists a)
                            | _, _, Some j -> parseJson j |> Result.bind ExecutionJson.readExpected
                            | _ -> Error "declare needs --expect-command, --expect-artifact or --expect-json"

                        let sequence = optionValue "--sequence" rest |> Option.bind (fun v -> match Int32.TryParse v with | true, n -> Some n | _ -> None) |> Option.defaultValue (s.Steps.Length + 1)

                        match expected |> Result.bind (fun e -> StepLedger.declare s.Entries stepId sequence (optionValue "--name" rest |> Option.defaultValue stepId) (optionValues "--depends-on" rest) e (optionValue "--retry-safe" rest) now) with
                        | Error e -> fail e
                        | Ok entry -> append entry
                    | "start" ->
                        let legalStart =
                            legal s actor
                            |> List.tryFind (fun a -> (a.Transition = "step.start" || a.Transition = "step.retry") && a.Target = Some stepId)

                        match legalStart, StepLedger.start s.Entries stepId now with
                        | _, Error e -> refuse e
                        | Some a, Ok _ when not a.Available -> refuse (String.concat "; " a.Reasons)
                        | None, Ok _ -> refuse $"step {stepId} is not legal to start now"
                        | Some _, Ok entry -> append entry
                    | "run" ->
                        let transition =
                            legal s actor
                            |> List.tryFind (fun a -> (a.Transition = "step.start" || a.Transition = "step.retry") && a.Target = Some stepId && a.Available)

                        match transition, StepLedger.start s.Entries stepId now with
                        | None, Error e -> refuse e
                        | None, Ok _ -> refuse $"step {stepId} is not legal to start now"
                        | Some _, Error e -> refuse e
                        | Some _, Ok started ->
                            let view = s.Steps |> List.find (fun v -> v.StepId = stepId)

                            let command =
                                optionValue "--command" rest
                                |> Option.orElse (match view.Expected with ExpectedReceipt.CommandSucceeded c -> Some c | _ -> None)

                            match command with
                            | None -> fail "run needs --command, or a step whose expected receipt is command-succeeded"
                            | Some command ->
                                ExecutionStore.appendEntry root id started
                                let fact = runShell (workspaceDirectory root s.Envelope) command
                                let observed = { Source = ObservationSource.Host "praxis"; Facts = [ fact ]; Narrative = None }
                                let entries = s.Entries @ [ started ]

                                match StepLedger.observe entries stepId observed DateTimeOffset.UtcNow with
                                | Error e -> fail e
                                | Ok (StepEntry.Observed(_, _, _, result, _) as entry) ->
                                    ExecutionStore.appendEntry root id entry
                                    printfn "step %s: %s" stepId (ReceiptOutcome.toWire result.Outcome)
                                    if result.Outcome = ReceiptOutcome.Match then 0 else 3
                                | Ok entry -> append entry
                    | "observe" ->
                        match optionValue "--observed-json" rest |> Option.map parseJson with
                        | None -> fail "observe needs --observed-json JSON|FILE"
                        | Some(Error e) -> fail e
                        | Some(Ok node) ->
                            match ExecutionJson.readObserved node |> Result.bind (fun o -> StepLedger.observe s.Entries stepId o now) with
                            | Error e -> refuse e
                            | Ok entry -> append entry
                    | "reconcile" ->
                        let detail = optionValue "--detail" rest |> Option.defaultValue ""

                        let finding =
                            match optionValue "--finding" rest with
                            | Some "occurred" -> Ok(Reconciliation.Occurred detail)
                            | Some "did-not-occur" -> Ok(Reconciliation.DidNotOccur detail)
                            | Some "unknown" -> Ok(Reconciliation.StillUnknown detail)
                            | _ -> Error "--finding occurred|did-not-occur|unknown is required"

                        match finding with
                        | Error e -> fail e
                        | Ok _ when String.IsNullOrWhiteSpace detail -> fail "--detail must say what reconciliation observed"
                        | Ok f ->
                            requireLegal s actor "step.reconcile" (Some stepId) (fun () ->
                                match StepLedger.reconcile s.Entries stepId f now with
                                | Error e -> refuse e
                                | Ok entry -> append entry)
                    | other -> fail $"unknown step command '{other}'")
        | _ -> fail "usage: execution step declare|start|run|observe|reconcile EXE-ID --step ID ..."

    let private evaluate root (arguments: string list) =
        match arguments with
        | id :: rest ->
            withSnapshot root id (fun s ->
                match s.Envelope.Evaluator, optionValue "--command" rest with
                | None, _ -> refuse "this execution declares no evaluator"
                | _, None -> fail "--command is required"
                | Some baseline, Some command ->
                    let cwd = workspaceDirectory root s.Envelope

                    let current =
                        baseline.Inputs
                        |> List.map (fun i -> GitWorkspace.fileDigest (Path.Combine(cwd, i.Reference)) |> Result.map (fun d -> { i with Digest = d }))
                        |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
                        |> Result.bind (List.rev >> EvaluatorIdentity.create)

                    let fact = runShell cwd command

                    let passed, reason =
                        match fact with
                        | ObservedFact.CommandExited(_, 0) -> true, ""
                        | ObservedFact.CommandExited(_, code) -> false, $"exit code {code}"
                        | _ -> false, "command outcome unknown"

                    let outcome =
                        match fact with
                        | ObservedFact.CommandOutcomeUnknown(_, why) -> EvaluationOutcome.EvaluatorUnavailable why
                        | _ -> EvaluationOutcome.judge baseline current passed reason

                    let currentFp = current |> Result.map _.Fingerprint |> Result.toOption

                    ExecutionStore.appendRecord
                        root
                        id
                        "verification"
                        [ "outcome", Some(EvaluationOutcome.toWire outcome)
                          "evaluator", Some baseline.Fingerprint
                          "current", currentFp
                          "command", Some command
                          "reason", (match outcome with | EvaluationOutcome.Failed(_, r) | EvaluationOutcome.EvaluatorUnavailable r -> Some r | EvaluationOutcome.EvaluatorChanged(_, _, changed) -> Some("changed: " + String.concat ", " changed) | _ -> None) ]
                        DateTimeOffset.UtcNow

                    printfn "evaluation: %s" (EvaluationOutcome.toWire outcome)

                    match outcome with
                    | EvaluationOutcome.Passed _ -> 0
                    | _ -> 3)
        | [] -> fail "an execution id is required"

    let private expandScope root actor (arguments: string list) =
        match arguments with
        | id :: rest ->
            withSnapshot root id (fun s ->
                requireLegal s actor "scope.expand" None (fun () ->
                    let expansion =
                        { ExpansionId = $"EXP-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}"
                          Scopes = optionValues "--scope" rest
                          Projections =
                            optionValues "--allow" rest
                            |> List.choose (fun spec -> match spec.LastIndexOf '=' with -1 -> None | i -> Some { Scope = spec.Substring(0, i); Patterns = [ spec.Substring(i + 1) ] })
                          Justification = optionValue "--justification" rest |> Option.defaultValue ""
                          AuthorizedBy = actor.Id }

                    match MutationBoundary.expand expansion s.Envelope.Boundary with
                    | Error e -> refuse e
                    | Ok widened ->
                        ExecutionStore.saveEnvelope root { s.Envelope with Boundary = widened }

                        ExecutionStore.appendRecord
                            root
                            id
                            "scope-expanded"
                            [ "expansion", Some expansion.ExpansionId
                              "scopes", Some(String.concat "," expansion.Scopes)
                              "justification", Some expansion.Justification
                              "authorizedBy", Some actor.Id ]
                            DateTimeOffset.UtcNow

                        printfn "scope expanded (%s)" expansion.ExpansionId
                        0))
        | [] -> fail "an execution id is required"

    let private resolveEffect root actor (arguments: string list) =
        match arguments with
        | id :: rest ->
            withSnapshot root id (fun s ->
                match optionValue "--resource" rest, optionValue "--resolution" rest, optionValue "--detail" rest with
                | Some resource, Some resolution, Some detail when List.contains resolution [ "reverted"; "expanded"; "transferred" ] ->
                    requireLegal s actor "scope.resolve" (Some resource) (fun () ->
                        // A revert must actually be observed: the resource
                        // no longer differs from the baseline.
                        let stillChanged = s.Effects |> List.exists (fun e -> e.Resource = resource)

                        if resolution = "reverted" && stillChanged && (GitWorkspace.changedPaths (workspaceDirectory root s.Envelope) s.Envelope.BaselineRevision |> Result.toOption |> Option.exists (List.contains resource)) then
                            refuse $"{resource} still differs from the baseline; a revert must be observable"
                        else
                            ExecutionStore.appendRecord root id "scope-resolved" [ "resource", Some resource; "resolution", Some resolution; "detail", Some detail; "actor", Some actor.Id ] DateTimeOffset.UtcNow
                            0)
                | _ -> fail "--resource PATH --resolution reverted|expanded|transferred --detail TEXT are required")
        | [] -> fail "an execution id is required"

    let private transition root actor (arguments: string list) =
        match arguments with
        | id :: rest ->
            withSnapshot root id (fun s ->
                let reason = optionValue "--reason" rest |> Option.defaultValue ""
                let now = DateTimeOffset.UtcNow

                let apply name (state: ExecutionState) (extra: ExecutionEnvelope -> ExecutionEnvelope) =
                    requireLegal s actor name None (fun () ->
                        let updated = extra { s.Envelope with State = state }
                        ExecutionStore.saveEnvelope root updated
                        ExecutionStore.appendRecord root id "transition" [ "transition", Some name; "state", Some(ExecutionState.toWire state); "reason", Some reason; "actor", Some actor.Id; "actorKind", Some(ActorKind.code actor.Kind) ] now
                        printfn "%s -> %s" id (ExecutionState.toWire state)
                        0)

                match optionValue "--action" rest with
                | Some "block" -> apply "execution.block" (ExecutionState.Blocked reason) (fun e -> e)
                | Some "resume" -> apply "execution.resume" ExecutionState.Active (fun e -> e)
                | Some "abandon" -> apply "execution.abandon" (ExecutionState.Abandoned reason) (fun e -> e)
                | Some "complete" ->
                    let candidate = GitWorkspace.head (workspaceDirectory root s.Envelope) |> Result.toOption
                    apply "execution.complete" ExecutionState.Completed (fun e -> { e with CandidateRevision = candidate })
                | _ -> fail "--action block|resume|complete|abandon is required")
        | [] -> fail "an execution id is required"

    let private cleanup root actor (arguments: string list) =
        match arguments with
        | id :: _ ->
            withSnapshot root id (fun s ->
                requireLegal s actor "workspace.cleanup" None (fun () ->
                    match s.Envelope.Workspace |> Option.bind _.Path with
                    | None -> 0
                    | Some relative ->
                        match GitWorkspace.removeWorktree root (Path.GetFullPath(Path.Combine(root, relative))) with
                        | Error e -> fail $"could not remove the worktree: {e}"
                        | Ok _ ->
                            // Identity is preserved; only the physical workspace goes.
                            ExecutionStore.saveEnvelope root { s.Envelope with Workspace = s.Envelope.Workspace |> Option.map (fun w -> { w with Path = None }) }
                            ExecutionStore.appendRecord root id "workspace-cleaned" [ "workspace", s.Envelope.Workspace |> Option.map _.Id; "actor", Some actor.Id ] DateTimeOffset.UtcNow
                            printfn "workspace removed; execution %s and its branch are preserved" id
                            0))
        | [] -> fail "an execution id is required"

    let run (root: string) (actor: Actor) (arguments: string list) =
        match arguments with
        | "start" :: rest -> start root actor rest
        | "show" :: rest -> show root actor rest
        | "actions" :: rest -> actions root actor rest
        | "list" :: rest -> list root rest
        | "boundary" :: rest -> boundary root rest
        | "step" :: rest -> step root actor rest
        | "evaluate" :: rest -> evaluate root rest
        | "expand-scope" :: rest -> expandScope root actor rest
        | "resolve-effect" :: rest -> resolveEffect root actor rest
        | "transition" :: rest -> transition root actor rest
        | "cleanup" :: rest -> cleanup root actor rest
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
