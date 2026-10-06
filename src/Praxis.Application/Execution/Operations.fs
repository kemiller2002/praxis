namespace Praxis.Application.Execution

open System
open Praxis.Domain.Execution

/// Durable execution state under `.ros/executions/<id>/`.
type ExecutionStorePort =
    { Load: string -> Result<ExecutionEnvelope, string>
      Save: ExecutionEnvelope -> unit
      Exists: string -> bool
      ReadEntries: string -> Result<StepEntry list, string>
      AppendEntry: string -> StepEntry -> EntryAttribution -> unit
      AppendRecord: string -> string -> (string * string option) list -> unit
      AppendVerification: string -> VerificationRecord -> unit
      ReadVerification: string -> VerificationRecord option
      Attributions: string -> (string * EntryAttribution) list
      ResolvedResources: string -> Set<string>
      List: unit -> string list
      NewId: unit -> string
      EnvelopePath: string -> string }

/// Git observation of a workspace directory. Every function takes the
/// directory it observes.
type WorkspacePort =
    { Root: string
      Head: string -> Result<string, string>
      Branch: string -> string option
      Resolve: string -> string -> Result<string, string>
      ChangedPaths: string -> string -> Result<string list, string>
      Uncommitted: string -> bool
      IsAncestor: string -> string -> string -> bool option
      DirectoryExists: string -> bool
      Commits: string -> string -> string -> Result<string list, string>
      Digest: string -> Result<string, string>
      Repository: unit -> string option
      CreateWorktree: string -> string -> string -> Result<unit, string>
      RemoveWorktree: string -> Result<unit, string>
      /// Resolve a stored (relative) workspace path to a directory.
      Absolute: string -> string
      /// Store a directory relative to the root (never as an absolute path).
      Relative: string -> string }

/// The host: clock, process execution, policy and host-reported facts.
type ExecutionHostPort =
    { Now: unit -> DateTimeOffset
      /// Run a shell command in a directory with extra environment.
      Run: string -> string -> (string * string) list -> ObservedFact
      Policy: unit -> Result<ExecutionPolicy, string>
      /// Host containment evidence: an explicit file, else the host's
      /// `PRAXIS_CONTAINMENT_EVIDENCE`; `Ok None` when nothing is reported.
      ContainmentEvidence: string option -> Result<ContainmentProfile option, string>
      /// The remote executor running this process, if any.
      RemoteExecutor: unit -> string option }

type ExecutionPorts =
    { Store: ExecutionStorePort
      Workspace: WorkspacePort
      Host: ExecutionHostPort }

/// Why an execution use case did not run. `Invalid` is an argument or
/// lookup problem (exit 2); `Refused` is a governance refusal (exit 3).
[<RequireQualifiedAccess>]
type ExecutionFailure =
    | Invalid of string
    | Refused of string

/// Who asks: the actor's identity, carried onto every record it causes.
type Requester = { Actor: ExecutionActor }

type StartRequest =
    { WorkItem: string
      Role: ExecutionRole
      Baseline: string option
      Worktree: bool
      WorktreeRoot: string option
      Scopes: string list
      Allow: string list
      Evaluators: string list
      EvaluatorCommand: string option
      HumanOnly: string list
      Parent: string option
      ContainmentEvidence: string option }

/// Every `praxis execution` use case. Each mutation is gated by the legal
/// action it performs (`LegalActions.evaluate`); nothing else decides
/// legality.
[<RequireQualifiedAccess>]
module ExecutionService =
    let private refuse message = Error(ExecutionFailure.Refused message)
    let private invalid message = Error(ExecutionFailure.Invalid message)

    let private traverse f xs =
        List.foldBack (fun x acc -> Result.bind (fun ys -> f x |> Result.map (fun y -> y :: ys)) acc) xs (Ok [])

    /// A globally unambiguous work identity: `<owner/repo>:<WORK-ID>` when
    /// the repository identity is known.
    let qualifyWorkItem (ports: ExecutionPorts) (raw: string) =
        if raw.Contains ':' || raw.Contains '#' then
            raw
        else
            match ports.Workspace.Repository() with
            | Some repo -> repo + ":" + raw
            | None -> raw

    let localWorkId (workItem: string) =
        match workItem.LastIndexOfAny [| ':'; '#' |] with
        | -1 -> workItem
        | i -> workItem.Substring(i + 1)

    let workspaceDirectory (ports: ExecutionPorts) (envelope: ExecutionEnvelope) =
        match envelope.Workspace |> Option.bind _.Path with
        | Some relative -> ports.Workspace.Absolute relative
        | None -> ports.Workspace.Root

    let private observeWorkspace (ports: ExecutionPorts) (envelope: ExecutionEnvelope) : WorkspaceObservation =
        let dir = workspaceDirectory ports envelope
        let w = ports.Workspace

        if not (w.DirectoryExists dir) then
            { Present = false; Branch = None; Head = None; BaselineIsAncestor = None; CandidateIsAncestor = None }
        else
            let head = w.Head dir |> Result.toOption

            { Present = true
              Branch = w.Branch dir
              Head = head
              BaselineIsAncestor = head |> Option.bind (w.IsAncestor dir envelope.BaselineRevision)
              CandidateIsAncestor = match envelope.CandidateRevision, head with | Some c, Some h -> w.IsAncestor dir c h | _ -> None }

    /// Unresolved out-of-boundary mutations, observed from Git; none for an
    /// execution that declared no boundary.
    let private scopeEffects (ports: ExecutionPorts) (envelope: ExecutionEnvelope) =
        if not (ExecutionEnvelope.declaresBoundary envelope) then
            []
        else
            match ports.Workspace.ChangedPaths (workspaceDirectory ports envelope) envelope.BaselineRevision with
            | Error _ -> []
            | Ok paths ->
                let resolved = ports.Store.ResolvedResources envelope.ExecutionId

                paths
                |> List.filter (fun p -> not (p.StartsWith(".ros/", StringComparison.Ordinal)) && not (resolved.Contains p))
                |> List.map (fun p -> p, None)
                |> MutationBoundary.scopeEffects envelope.Boundary

    let private policy (ports: ExecutionPorts) =
        ports.Host.Policy() |> Result.mapError (fun e -> ExecutionFailure.Invalid("ros.json execution policy is invalid: " + e))

    /// Observe everything the legal-action computation needs.
    let observe (ports: ExecutionPorts) (envelope: ExecutionEnvelope) (entries: StepEntry list) (policy: ExecutionPolicy) : ExecutionObservation =
        let dir = workspaceDirectory ports envelope
        let workspace = observeWorkspace ports envelope
        let terminal = ExecutionState.isTerminal envelope.State
        let role = envelope.Authority.Role

        { Steps = StepLedger.reconstruct entries
          Effects = scopeEffects ports envelope
          Verification = ports.Store.ReadVerification envelope.ExecutionId
          Uncommitted = workspace.Present && ports.Workspace.Uncommitted dir
          Head = workspace.Head
          Divergence = if terminal then [] else WorkspaceBinding.divergence envelope workspace
          Launcher = ExecutionPolicy.launcherFor role policy
          ContainmentShortfall = ContainmentProfile.shortfall (ExecutionPolicy.requiredRestrictions role policy) envelope.ContainmentProfile }

    let snapshot (ports: ExecutionPorts) (actorKind: string) (id: string) : Result<ExecutionSnapshot, ExecutionFailure> =
        match ports.Store.Load id, ports.Store.ReadEntries id with
        | Error e, _
        | _, Error e -> invalid e
        | Ok envelope, Ok entries ->
            policy ports
            |> Result.map (fun p ->
                let observed = observe ports envelope entries p

                { Envelope = envelope
                  Entries = entries
                  Observation = observed
                  Attributions = ports.Store.Attributions id
                  LegalActions = LegalActions.evaluate envelope observed actorKind })

    /// The single legality gate for every mutating use case.
    let requireLegal (s: ExecutionSnapshot) transition (target: string option) (run: unit -> Result<'a, ExecutionFailure>) =
        match s.LegalActions |> List.tryFind (fun a -> a.Transition = transition && (target.IsNone || a.Target = target)) with
        | Some a when a.Available -> run ()
        | Some a -> refuse (transition + " is not legal now: " + String.concat "; " a.Reasons)
        | None -> refuse $"{transition} is not a legal action for this execution"

    let private attribution (ports: ExecutionPorts) (s: ExecutionSnapshot) (requester: Requester) : EntryAttribution =
        { ActorId = requester.Actor.Id
          ActorKind = requester.Actor.Kind
          Role = s.Envelope.Authority.Role
          Revision = ports.Workspace.Head(workspaceDirectory ports s.Envelope) |> Result.toOption
          Evaluator = s.Envelope.Evaluator |> Option.map _.Fingerprint }

    let private transitionRecord (ports: ExecutionPorts) id name (state: ExecutionState) reason (requester: Requester) extra =
        ports.Store.AppendRecord
            id
            "transition"
            ([ "transition", Some name
               "state", Some(ExecutionState.toWire state)
               "reason", reason
               "actor", Some requester.Actor.Id
               "actorKind", Some requester.Actor.Kind ]
             @ extra)

    // ---------------------------------------------------------------- start

    let private evaluatorFrom (ports: ExecutionPorts) (specs: string list) =
        specs
        |> traverse (fun spec ->
            match spec.IndexOf '=' with
            | -1 -> Error $"--evaluator expects KIND=PATH, got '{spec}'"
            | i ->
                let kind, path = spec.Substring(0, i), spec.Substring(i + 1)

                ports.Workspace.Digest(ports.Workspace.Absolute path)
                |> Result.map (fun digest -> { Kind = kind; Reference = path.Replace('\\', '/'); Digest = digest }))

    let private projections (allow: string list) =
        allow
        |> List.choose (fun spec ->
            match spec.LastIndexOf '=' with
            | -1 -> None
            | i -> Some { Scope = spec.Substring(0, i); Patterns = [ spec.Substring(i + 1) ] })
        |> List.groupBy _.Scope
        |> List.map (fun (scope, ps) -> { Scope = scope; Patterns = ps |> List.collect _.Patterns })

    /// The containment profile for a new execution: what the host reported,
    /// else all unknown.
    let private hostProfile (ports: ExecutionPorts) (explicitEvidence: string option) =
        ports.Host.ContainmentEvidence explicitEvidence
        |> Result.map (Option.defaultValue ContainmentProfile.unknown)
        |> Result.mapError (fun e -> ExecutionFailure.Invalid("containment evidence: " + e))

    let private withProfile (profile: ContainmentProfile) (envelope: ExecutionEnvelope) =
        { envelope with
            ContainmentProfile = profile
            Containment = ContainmentProfile.containment envelope.Containment profile }

    let start (ports: ExecutionPorts) (requester: Requester) (request: StartRequest) : Result<ExecutionEnvelope, ExecutionFailure> =
        let now = ports.Host.Now()
        let root = ports.Workspace.Root
        let workItem = qualifyWorkItem ports request.WorkItem

        let baseline =
            request.Baseline
            |> Option.map (ports.Workspace.Resolve root)
            |> Option.defaultWith (fun () -> ports.Workspace.Head root)
            |> Result.mapError (fun e -> ExecutionFailure.Invalid $"cannot resolve the baseline revision: {e}")

        let evaluator =
            match request.Evaluators with
            | [] -> Ok None
            | specs -> evaluatorFrom ports specs |> Result.bind EvaluatorIdentity.create |> Result.map Some |> Result.mapError ExecutionFailure.Invalid

        let boundary =
            match request.Scopes |> List.tryFind (MutationBoundary.isScope >> not) with
            | Some bad -> invalid $"'{bad}' is not a semantic scope (feature:|cluster:|authority:|capability:<id>)"
            | None -> Ok { Scopes = request.Scopes; Projections = projections request.Allow; EvaluatorReferences = [] }

        let command =
            match request.EvaluatorCommand, request.Evaluators with
            | Some _, [] -> invalid "--evaluator-command needs a declared evaluator closure (--evaluator KIND=PATH)"
            | c, _ -> Ok c

        match baseline, evaluator, boundary, command, policy ports, hostProfile ports request.ContainmentEvidence with
        | Error e, _, _, _, _, _
        | _, Error e, _, _, _, _
        | _, _, Error e, _, _, _
        | _, _, _, Error e, _, _
        | _, _, _, _, Error e, _
        | _, _, _, _, _, Error e -> Error e
        | Ok baselineSha, Ok ev, Ok bound, Ok evaluatorCommand, Ok policy, Ok profile ->
            let id = ports.Store.NewId()

            match ExecutionEnvelope.create id workItem requester.Actor (RoleAuthority.defaultFor request.Role) baselineSha bound ev now with
            | Error e -> refuse e
            | Ok envelope ->
                let worktree = request.Worktree || ExecutionPolicy.requiresWorktree request.Role policy

                let workspace =
                    if worktree then
                        let parent = request.WorktreeRoot |> Option.defaultValue (IO.Path.Combine(root, "..", IO.Path.GetFileName(IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath root)) + ".worktrees"))
                        let path = IO.Path.GetFullPath(IO.Path.Combine(parent, id))
                        let branch = $"praxis/{localWorkId workItem |> String.map (fun c -> if Char.IsAsciiLetterOrDigit c || c = '-' || c = '_' || c = '.' then c else '-')}/{id}"

                        ports.Workspace.CreateWorktree branch path baselineSha
                        |> Result.map (fun () ->
                            { Id = "worktree:" + id; Branch = Some branch; Path = Some(ports.Workspace.Relative path); Mechanism = "git-worktree" },
                            Containment.SemanticOnly "git-worktree")
                        |> Result.mapError (fun e -> ExecutionFailure.Invalid $"could not create the execution worktree: {e}")
                    else
                        Ok({ Id = "checkout:" + id; Branch = ports.Workspace.Branch root; Path = None; Mechanism = "working-directory" }, Containment.SemanticOnly "working-directory")

                workspace
                |> Result.map (fun (w, containment) ->
                    let envelope =
                        { envelope with
                            Workspace = Some w
                            Containment = containment
                            HumanOnlyTransitions = request.HumanOnly
                            Parent = request.Parent
                            EvaluatorCommand = evaluatorCommand }
                        |> withProfile profile

                    ports.Store.Save envelope
                    transitionRecord ports id "execution.start" ExecutionState.Active None requester [ "worktreePolicy", (if worktree && not request.Worktree then Some "required" else None) ]
                    envelope)

    // ------------------------------------------------------------- reading

    let list (ports: ExecutionPorts) (workItem: string option) =
        let filter = workItem |> Option.map (qualifyWorkItem ports)

        ports.Store.List()
        |> List.choose (ports.Store.Load >> Result.toOption)
        |> List.filter (fun e -> filter |> Option.forall (fun w -> e.WorkItem = w || localWorkId e.WorkItem = localWorkId w && not (w.Contains ':')))

    // --------------------------------------------------------------- steps

    let declareStep (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) step sequence name deps expected retrySafe =
        let sequence = sequence |> Option.defaultValue (s.Observation.Steps.Length + 1)

        requireLegal s "step.declare" None (fun () ->
            StepLedger.declare s.Entries step sequence name deps expected retrySafe (ports.Host.Now())
            |> Result.mapError ExecutionFailure.Invalid
            |> Result.map (fun entry -> ports.Store.AppendEntry s.Envelope.ExecutionId entry (attribution ports s requester)))

    let private startable (s: ExecutionSnapshot) step =
        s.LegalActions
        |> List.tryFind (fun a -> (a.Transition = "step.start" || a.Transition = "step.retry") && a.Target = Some step)

    let startStep (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) step =
        match startable s step, StepLedger.start s.Entries step (ports.Host.Now()) with
        | _, Error e -> refuse e
        | Some a, Ok _ when not a.Available -> refuse (String.concat "; " a.Reasons)
        | None, Ok _ -> refuse $"step {step} is not legal to start now"
        | Some _, Ok entry -> Ok(ports.Store.AppendEntry s.Envelope.ExecutionId entry (attribution ports s requester))

    /// Start a step, run its command on the host, and record the
    /// host-observed receipt.
    let runStep (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) step (command: string option) =
        match startable s step |> Option.filter _.Available, StepLedger.start s.Entries step (ports.Host.Now()) with
        | _, Error e -> refuse e
        | None, Ok _ -> refuse $"step {step} is not legal to start now"
        | Some _, Ok started ->
            let view = s.Observation.Steps |> List.find (fun v -> v.StepId = step)
            let command = command |> Option.orElse (match view.Expected with ExpectedReceipt.CommandSucceeded c -> Some c | _ -> None)

            match command with
            | None -> invalid "run needs --command, or a step whose expected receipt is command-succeeded"
            | Some command ->
                let by = attribution ports s requester
                ports.Store.AppendEntry s.Envelope.ExecutionId started by
                let fact = ports.Host.Run (workspaceDirectory ports s.Envelope) command []
                let observed = { Source = ObservationSource.Host "praxis"; Facts = [ fact ]; Narrative = None }

                match StepLedger.observe (s.Entries @ [ started ]) step observed (ports.Host.Now()) with
                | Error e -> invalid e
                | Ok(StepEntry.Observed(_, _, _, result, _) as entry) ->
                    ports.Store.AppendEntry s.Envelope.ExecutionId entry by
                    Ok result.Outcome
                | Ok entry ->
                    ports.Store.AppendEntry s.Envelope.ExecutionId entry by
                    Ok ReceiptOutcome.Indeterminate

    let observeStep (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) step (observed: ObservedReceipt) =
        requireLegal s "step.observe" (Some step) (fun () ->
            StepLedger.observe s.Entries step observed (ports.Host.Now())
            |> Result.mapError ExecutionFailure.Refused
            |> Result.map (fun entry -> ports.Store.AppendEntry s.Envelope.ExecutionId entry (attribution ports s requester)))

    let reconcileStep (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) step finding =
        requireLegal s "step.reconcile" (Some step) (fun () ->
            StepLedger.reconcile s.Entries step finding (ports.Host.Now())
            |> Result.mapError ExecutionFailure.Refused
            |> Result.map (fun entry -> ports.Store.AppendEntry s.Envelope.ExecutionId entry (attribution ports s requester)))

    // ----------------------------------------------------------- evaluation

    /// Run the declared evaluator command, and only it (PRX-VER-001), and
    /// record the verification with its candidate, exit code, actor and
    /// evidence (PRX-VER-002).
    let evaluate (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) (command: string option) (evidence: string list) =
        requireLegal s "execution.evaluate" None (fun () ->
            match s.Envelope.Evaluator, s.Envelope.EvaluatorCommand with
            | Some baseline, Some declared ->
                match command with
                | Some other when other <> declared -> refuse $"this execution may run only its declared evaluator command '{declared}', not '{other}'"
                | _ ->
                    let cwd = workspaceDirectory ports s.Envelope

                    let current =
                        baseline.Inputs
                        |> traverse (fun i -> ports.Workspace.Digest(IO.Path.Combine(cwd, i.Reference)) |> Result.map (fun d -> { i with Digest = d }))
                        |> Result.bind EvaluatorIdentity.create

                    let candidate = ports.Workspace.Head cwd |> Result.toOption
                    let fact = ports.Host.Run cwd declared []

                    let exitCode, passed, reason =
                        match fact with
                        | ObservedFact.CommandExited(_, 0) -> Some 0, true, ""
                        | ObservedFact.CommandExited(_, code) -> Some code, false, $"exit code {code}"
                        | _ -> None, false, "command outcome unknown"

                    let outcome =
                        match fact with
                        | ObservedFact.CommandOutcomeUnknown(_, why) -> EvaluationOutcome.EvaluatorUnavailable why
                        | _ -> EvaluationOutcome.judge baseline current passed reason

                    let record =
                        { Outcome = outcome
                          Command = declared
                          Candidate = candidate
                          ExitCode = exitCode
                          ActorId = requester.Actor.Id
                          ActorKind = requester.Actor.Kind
                          Evidence = evidence
                          At = ports.Host.Now() }

                    ports.Store.AppendVerification s.Envelope.ExecutionId record
                    Ok record
            | _ -> refuse "this execution declares no evaluator command")

    // -------------------------------------------------------------- scope

    let expandScope (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) scopes allow justification =
        requireLegal s "scope.expand" None (fun () ->
            let now = ports.Host.Now()

            let expansion =
                { ExpansionId = $"EXP-{now:yyyyMMddHHmmssfff}"
                  Scopes = scopes
                  Projections = allow |> List.choose (fun (spec: string) -> match spec.LastIndexOf '=' with -1 -> None | i -> Some { Scope = spec.Substring(0, i); Patterns = [ spec.Substring(i + 1) ] })
                  Justification = justification
                  AuthorizedBy = requester.Actor.Id }

            match MutationBoundary.expand expansion s.Envelope.Boundary with
            | Error e -> refuse e
            | Ok widened ->
                ports.Store.Save { s.Envelope with Boundary = widened }

                ports.Store.AppendRecord
                    s.Envelope.ExecutionId
                    "scope-expanded"
                    [ "expansion", Some expansion.ExpansionId
                      "scopes", Some(String.concat "," expansion.Scopes)
                      "justification", Some expansion.Justification
                      "authorizedBy", Some requester.Actor.Id ]

                Ok expansion.ExpansionId)

    let resolveEffect (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) resource resolution detail =
        requireLegal s "scope.resolve" (Some resource) (fun () ->
            let still =
                ports.Workspace.ChangedPaths (workspaceDirectory ports s.Envelope) s.Envelope.BaselineRevision
                |> Result.toOption
                |> Option.exists (List.contains resource)

            // A revert must actually be observed: the resource no longer
            // differs from the baseline.
            if resolution = "reverted" && still then
                refuse $"{resource} still differs from the baseline; a revert must be observable"
            else
                Ok(ports.Store.AppendRecord s.Envelope.ExecutionId "scope-resolved" [ "resource", Some resource; "resolution", Some resolution; "detail", Some detail; "actor", Some requester.Actor.Id ]))

    // ---------------------------------------------------------- transitions

    /// The commits between baseline and candidate: the candidate lineage
    /// this execution recorded (PRX-EXEC-053), not an attribution claim.
    let private recordCandidate (ports: ExecutionPorts) (envelope: ExecutionEnvelope) (candidate: string option) =
        match candidate with
        | Some c when c <> envelope.BaselineRevision ->
            match ports.Workspace.Commits (workspaceDirectory ports envelope) envelope.BaselineRevision c with
            | Ok commits -> [ "candidate", Some c; "commits", Some(String.concat "," commits) ]
            | Error why -> [ "candidate", Some c; "commits", None; "commitsUnavailable", Some why ]
        | Some c -> [ "candidate", Some c; "commits", Some "" ]
        | None -> []

    let transition (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) (action: string) (reason: string) =
        let apply name (state: ExecutionState) (extra: ExecutionEnvelope -> ExecutionEnvelope * (string * string option) list) =
            requireLegal s name None (fun () ->
                let updated, fields = extra { s.Envelope with State = state }
                ports.Store.Save updated
                transitionRecord ports s.Envelope.ExecutionId name state (Some reason) requester fields
                Ok state)

        match action with
        | "block" -> apply "execution.block" (ExecutionState.Blocked reason) (fun e -> e, [])
        | "resume" -> apply "execution.resume" ExecutionState.Active (fun e -> e, [])
        | "abandon" -> apply "execution.abandon" (ExecutionState.Abandoned reason) (fun e -> e, [])
        | "complete" ->
            let candidate = ports.Workspace.Head(workspaceDirectory ports s.Envelope) |> Result.toOption
            apply "execution.complete" ExecutionState.Completed (fun e -> { e with CandidateRevision = candidate }, recordCandidate ports s.Envelope candidate)
        | _ -> invalid "--action block|resume|complete|abandon is required"

    /// An explicit, recorded rebind to the workspace as it is now
    /// (PRX-EXEC-055): never silent, always with a reason.
    let rebind (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) (reason: string) =
        if String.IsNullOrWhiteSpace reason then
            invalid "a rebind needs a reason"
        else
            requireLegal s "execution.rebind" None (fun () ->
                let observed = observeWorkspace ports s.Envelope
                ports.Store.Save(WorkspaceBinding.rebind s.Envelope observed)

                ports.Store.AppendRecord
                    s.Envelope.ExecutionId
                    "workspace-rebound"
                    [ "divergence", Some(String.concat "; " s.Observation.Divergence)
                      "branch", observed.Branch
                      "head", observed.Head
                      "reason", Some reason
                      "actor", Some requester.Actor.Id ]

                Ok s.Observation.Divergence)

    let cleanup (ports: ExecutionPorts) (requester: Requester) (s: ExecutionSnapshot) =
        requireLegal s "workspace.cleanup" None (fun () ->
            match s.Envelope.Workspace |> Option.bind _.Path with
            | None -> Ok()
            | Some relative ->
                match ports.Workspace.RemoveWorktree(ports.Workspace.Absolute relative) with
                | Error e -> invalid $"could not remove the worktree: {e}"
                | Ok() ->
                    // Identity is preserved; only the physical workspace goes.
                    ports.Store.Save { s.Envelope with Workspace = s.Envelope.Workspace |> Option.map (fun w -> { w with Path = None }) }
                    Ok(ports.Store.AppendRecord s.Envelope.ExecutionId "workspace-cleaned" [ "workspace", s.Envelope.Workspace |> Option.map _.Id; "actor", Some requester.Actor.Id ]))

    // --------------------------------------------------------------- launch

    /// The environment a launched worker receives: its execution identity
    /// and effective authority in machine-readable form (PRX-EXEC-035).
    let launchEnvironment (ports: ExecutionPorts) (envelope: ExecutionEnvelope) =
        [ "PRAXIS_EXECUTION_ID", envelope.ExecutionId
          "PRAXIS_EXECUTION_ROLE", ExecutionRole.toWire envelope.Authority.Role
          "PRAXIS_EXECUTION_ENVELOPE", ports.Store.EnvelopePath envelope.ExecutionId
          "PRAXIS_WORK_ITEM", envelope.WorkItem
          "PRAXIS_EXECUTION_CAPABILITIES", RoleAuthority.effective envelope.Authority |> Set.toList |> List.map Capability.toWire |> String.concat ","
          "PRAXIS_EXECUTION_PROHIBITIONS", envelope.Authority.Prohibits |> Set.toList |> List.map Capability.toWire |> String.concat "," ]

    /// Launch the repository-configured worker for the envelope's role
    /// (PRX-EXEC-005/040) after the legal transition is accepted. The
    /// launcher's host evidence, if configured, is recorded first, and the
    /// role's required restrictions must be shown enforced.
    let launch (ports: ExecutionPorts) (requester: Requester) (actorKind: string) (id: string) (dryRun: bool) =
        snapshot ports actorKind id
        |> Result.bind (fun s ->
            policy ports
            |> Result.bind (fun p ->
                // The launcher's own host evidence describes the host it runs
                // under; it is judged before required restrictions.
                let launcherEvidence =
                    match s.Observation.Launcher |> Option.bind _.ContainmentEvidence with
                    | None -> Ok None
                    | Some path ->
                        ports.Host.ContainmentEvidence(Some(ports.Workspace.Absolute path))
                        |> Result.mapError (fun e -> ExecutionFailure.Invalid $"launcher containment evidence: {e}")

                launcherEvidence
                |> Result.bind (fun reported ->
                    let envelope = reported |> Option.map (fun profile -> withProfile profile s.Envelope) |> Option.defaultValue s.Envelope

                    let observed =
                        { s.Observation with
                            ContainmentShortfall = ContainmentProfile.shortfall (ExecutionPolicy.requiredRestrictions envelope.Authority.Role p) envelope.ContainmentProfile }

                    let judged =
                        { s with
                            Envelope = envelope
                            Observation = observed
                            LegalActions = LegalActions.evaluate envelope observed actorKind }

                    requireLegal judged "execution.launch" None (fun () ->
                        match observed.Launcher with
                        | None -> refuse "no launcher is configured for this role"
                        | Some launcher ->
                            let environment = launchEnvironment ports envelope

                            if dryRun then
                                Ok(launcher, environment, None)
                            else
                                reported
                                |> Option.iter (fun profile ->
                                    ports.Store.Save envelope
                                    ports.Store.AppendRecord id "containment-observed" [ "source", profile.Source; "enforced", Some(String.concat "," (ContainmentProfile.enforced profile)); "launcher", Some launcher.Id ])

                                ports.Store.AppendRecord id "launch-started" [ "launcher", Some launcher.Id; "command", Some launcher.Command; "actor", Some requester.Actor.Id; "actorKind", Some requester.Actor.Kind ]
                                let fact = ports.Host.Run (workspaceDirectory ports envelope) launcher.Command environment

                                let exitCode, unknown =
                                    match fact with
                                    | ObservedFact.CommandExited(_, code) -> Some code, None
                                    | ObservedFact.CommandOutcomeUnknown(_, why) -> None, Some why
                                    | _ -> None, Some "not observed"

                                ports.Store.AppendRecord id "launch-finished" [ "launcher", Some launcher.Id; "exitCode", exitCode |> Option.map string; "unknown", unknown ]
                                Ok(launcher, environment, Some fact)))))

/// What a work transition binds (PRX-EXEC-030).
type WorkBinding =
    { WorkItem: string
      /// The telemetry execution the transition created or continued, when
      /// telemetry is enabled; the envelope shares its ID.
      ExecutionId: string option
      Origin: ExecutionOrigin
      Role: ExecutionRole
      Parent: string option
      /// A fallback envelope's recorded base commit and branch; otherwise
      /// the current checkout is observed.
      Baseline: string option
      Branch: string option }

/// Binds work transitions to durable execution envelopes and mirrors their
/// outcome (PRX-EXEC-014/026/030/053/055). A work-bound envelope binds the
/// current checkout and never creates a branch or worktree; it declares no
/// mutation boundary, so no scope effect is computed for it.
[<RequireQualifiedAccess>]
module ExecutionBinding =
    let private isWorkBound (e: ExecutionEnvelope) = e.Origin <> ExecutionOrigin.Explicit

    /// The envelopes recorded for a work item, oldest first.
    let forWorkItem (ports: ExecutionPorts) (workItem: string) =
        ExecutionService.list ports (Some workItem) |> List.sortBy _.StartedAt

    let private open' (ports: ExecutionPorts) workItem =
        forWorkItem ports workItem |> List.filter (fun e -> isWorkBound e && not (ExecutionState.isTerminal e.State))

    /// Validate what a bind will need before the work transition changes
    /// anything: the repository policy and any host containment evidence.
    let preflight (ports: ExecutionPorts) : Result<unit, ExecutionFailure> =
        match ports.Host.Policy(), ports.Host.ContainmentEvidence None with
        | Error e, _ -> Error(ExecutionFailure.Invalid("ros.json execution policy is invalid: " + e))
        | _, Error e -> Error(ExecutionFailure.Invalid("containment evidence: " + e))
        | Ok _, Ok _ -> Ok()

    /// Persist an envelope for a begun (or continued) work execution.
    /// `Ok None` when that execution already has one.
    let bind (ports: ExecutionPorts) (requester: Requester) (binding: WorkBinding) : Result<ExecutionEnvelope option, ExecutionFailure> =
        let id = binding.ExecutionId |> Option.defaultWith ports.Store.NewId
        let root = ports.Workspace.Root

        if ports.Store.Exists id then
            Ok None
        else
            let baseline =
                match binding.Baseline with
                | Some b -> Ok b
                | None -> ports.Workspace.Head root |> Result.mapError (fun e -> ExecutionFailure.Invalid $"no baseline commit to bind ({e})")

            match baseline, ports.Host.ContainmentEvidence None with
            | Error e, _ -> Error e
            | _, Error e -> Error(ExecutionFailure.Invalid("containment evidence: " + e))
            | Ok sha, Ok reported ->
                let now = ports.Host.Now()

                ExecutionEnvelope.create id (ExecutionService.qualifyWorkItem ports binding.WorkItem) requester.Actor (RoleAuthority.defaultFor binding.Role) sha MutationBoundary.empty None now
                |> Result.mapError ExecutionFailure.Refused
                |> Result.map (fun created ->
                    let profile = reported |> Option.defaultValue ContainmentProfile.unknown
                    let semantic = Containment.SemanticOnly "working-directory"

                    let envelope =
                        { created with
                            Origin = binding.Origin
                            Parent = binding.Parent
                            Workspace =
                              Some
                                  { Id = "checkout:" + id
                                    Branch = binding.Branch |> Option.orElse (ports.Workspace.Branch root)
                                    Path = None
                                    Mechanism = "working-directory" }
                            ContainmentProfile = profile
                            Containment = ContainmentProfile.containment semantic profile }

                    ports.Store.Save envelope
                    let origin, reference = ExecutionOrigin.toWire binding.Origin

                    ports.Store.AppendRecord
                        id
                        "transition"
                        [ "transition", Some "execution.start"
                          "state", Some "active"
                          "origin", Some origin
                          "originReference", reference
                          "actor", Some requester.Actor.Id
                          "actorKind", Some requester.Actor.Kind ]

                    Some envelope)

    /// Mirror an accepted work transition onto the item's open work-bound
    /// envelopes. Completion records the candidate and its commit lineage.
    let mirror (ports: ExecutionPorts) (requester: Requester) (workItem: string) (transition: string) (state: ExecutionState) (reason: string option) =
        open' ports workItem
        |> List.filter (fun e -> e.State <> state)
        |> List.map (fun e ->
            let dir = ExecutionService.workspaceDirectory ports e

            let candidate, lineage =
                match state with
                | ExecutionState.Completed ->
                    let head = ports.Workspace.Head dir |> Result.toOption

                    let commits =
                        match head with
                        | Some h when h <> e.BaselineRevision ->
                            match ports.Workspace.Commits dir e.BaselineRevision h with
                            | Ok cs -> [ "commits", Some(String.concat "," cs) ]
                            | Error why -> [ "commitsUnavailable", Some why ]
                        | Some _ -> [ "commits", Some "" ]
                        | None -> []

                    head, [ "candidate", head ] @ commits
                | _ -> e.CandidateRevision, []

            ports.Store.Save { e with State = state; CandidateRevision = candidate }

            ports.Store.AppendRecord
                e.ExecutionId
                "transition"
                ([ "transition", Some transition
                   "state", Some(ExecutionState.toWire state)
                   "reason", reason
                   "actor", Some requester.Actor.Id
                   "actorKind", Some requester.Actor.Kind ]
                 @ lineage)

            e.ExecutionId)

    /// Before `work resume`: every open work-bound envelope must still be
    /// bound to its workspace (PRX-EXEC-014/055). A divergence refuses the
    /// resume unless a rebind reason is given, which is recorded.
    let preflightResume (ports: ExecutionPorts) (requester: Requester) (workItem: string) (rebindReason: string option) =
        let diverged =
            open' ports workItem
            |> List.choose (fun e ->
                match ExecutionService.snapshot ports requester.Actor.Kind e.ExecutionId with
                | Ok s when not s.Observation.Divergence.IsEmpty -> Some s
                | _ -> None)

        match diverged, rebindReason with
        | [], _ -> Ok []
        | found, Some reason when not (String.IsNullOrWhiteSpace reason) ->
            found
            |> List.fold (fun acc s -> acc |> Result.bind (fun ids -> ExecutionService.rebind ports requester s reason |> Result.map (fun _ -> ids @ [ s.Envelope.ExecutionId ]))) (Ok [])
        | found, _ ->
            Error(
                ExecutionFailure.Refused(
                    found
                    |> List.map (fun s -> $"execution {s.Envelope.ExecutionId} has diverged from its workspace: " + String.concat "; " s.Observation.Divergence)
                    |> String.concat "; "
                    |> fun text -> text + " (resume with --rebind-reason TEXT to rebind it explicitly)"
                )
            )

    /// Before `work complete`: recorded receipts and verification outrank
    /// any claim (PRX-EXEC-026). Every open execution of the item, explicit
    /// or work-bound, must have no mismatched or unknown receipt, unresolved
    /// scope effect, or failed or stale verification, and a human-only
    /// completion needs a human.
    let preflightComplete (ports: ExecutionPorts) (requester: Requester) (workItem: string) =
        let blockers =
            forWorkItem ports workItem
            |> List.filter (fun e -> not (ExecutionState.isTerminal e.State))
            |> List.choose (fun e ->
                match ExecutionService.snapshot ports requester.Actor.Kind e.ExecutionId with
                | Error _ -> None
                | Ok s ->
                    let human =
                        if List.contains "execution.complete" e.HumanOnlyTransitions && requester.Actor.Kind <> "human" then
                            [ "completion requires a human actor" ]
                        else
                            []

                    match LegalActions.receiptBlockers s.Envelope s.Observation @ human with
                    | [] -> None
                    | reasons -> Some $"""execution {e.ExecutionId}: {String.concat "; " reasons}""")

        match blockers with
        | [] -> Ok()
        | found -> Error(ExecutionFailure.Refused("completion is not supported by recorded receipts: " + String.concat " | " found))

    /// The execution a fallback envelope describes: its own execution ID, or
    /// one derived from the transaction so a replay binds the same envelope.
    let fallbackExecutionId (input: Praxis.Domain.Work.EnvelopeReconciliationInput) =
        match input.Execution with
        | Some execution -> execution.ExecutionId
        | None ->
            let at =
                input.Requests |> List.tryPick _.OccurredAt
                |> Option.orElse (input.Timeline |> List.tryHead |> Option.map _.Timestamp)
                |> Option.defaultValue DateTimeOffset.UnixEpoch

            let digest = Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes input.TransactionId)
            let stamp = at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", Globalization.CultureInfo.InvariantCulture)
            $"EXE-{stamp}-{Convert.ToHexString(digest).Substring(0, 8).ToLowerInvariant()}"

    /// Apply a reconciled runtime-free envelope's work requests to its
    /// execution envelope, in order (PRX-EXEC-030 for fallback executions).
    let fallback (ports: ExecutionPorts) (input: Praxis.Domain.Work.EnvelopeReconciliationInput) =
        let requester =
            { Actor =
                { Id = input.Agent.ActorId
                  Kind = input.Agent.ActorKind
                  Provider = input.Agent.Provider |> Option.filter ((<>) "unknown")
                  Model = input.Agent.Model |> Option.filter ((<>) "unknown")
                  Runtime = input.Agent.Runtime |> Option.filter ((<>) "unknown") } }

        let binding =
            { WorkItem = input.WorkItem
              ExecutionId = Some(fallbackExecutionId input)
              Origin = ExecutionOrigin.Fallback input.TransactionId
              Role = ExecutionRole.Implementation
              Parent = None
              Baseline = Some input.BaseCommit
              Branch = Some input.Branch }

        input.Requests
        |> List.fold
            (fun acc request ->
                acc
                |> Result.bind (fun () ->
                    match request.RequestType with
                    | "work.start"
                    | "work.begin" -> bind ports requester binding |> Result.map ignore
                    | "work.block" -> Ok(mirror ports requester input.WorkItem "work.block" (ExecutionState.Blocked(request.Reason |> Option.defaultValue "")) request.Reason |> ignore)
                    | "work.resume" -> Ok(mirror ports requester input.WorkItem "work.resume" ExecutionState.Active None |> ignore)
                    | "work.complete" -> Ok(mirror ports requester input.WorkItem "work.complete" ExecutionState.Completed None |> ignore)
                    | _ -> Ok()))
            (Ok())
