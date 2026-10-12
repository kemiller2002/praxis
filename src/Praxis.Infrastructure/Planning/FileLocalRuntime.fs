namespace Praxis.Infrastructure.Planning

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Execution
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

/// Native completion policy bound by the entry point: the same quality and
/// durable-checkpoint gates the standalone `work complete` command uses.
type LocalRuntimeNativePolicy =
    { QualityGate: string -> WorkEvidence list -> Result<unit, string>
      Readiness: WorkContextPlan -> Result<Map<string, JsonObject>, string>
      CheckpointGuard: string list -> Result<unit, string> }

type LocalRunRequest = { Packet: LocalWorkerPacket; DelegationPath: string; TargetRef: string }

[<RequireQualifiedAccess>]
type LocalRunStage = Dispatched | Resumed

type LocalRunReport =
    { DispatchId: string; Stage: LocalRunStage; TargetRef: string; TargetCommit: string
      CompletedMembers: string list; WorkerWorktree: string option }

/// Production composition of the portable host, concrete validators, Git
/// staging and the native completion effect. Authority is the signed host
/// policy plus a signed delegation of the exact packet; the host
/// configuration only selects mechanisms.
[<RequireQualifiedAccess>]
module FileLocalRuntime =
    let private framed (values: string list) =
        let buffer = StringBuilder()
        for value in values do buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('|') |> ignore
        "sha256:" + (Encoding.UTF8.GetBytes(buffer.ToString()) |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let private fileDigest (path: string) =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    let private label (value: string) =
        let safe = value |> String.filter (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')
        let trimmed = if safe.Length > 40 then safe.Substring(0, 40) else safe
        if trimmed = "" then "dispatch" else trimmed

    let hostStore (configuration: LocalHostConfiguration) =
        FileLocalEcirAuthority.createWithFileAccess
            { RepositoryRoot = configuration.RepositoryRoot; HostRoot = configuration.PolicyRoot; LockTimeout = configuration.LockTimeout }
            (fun () -> DateTimeOffset.UtcNow) configuration.FileAccess

    // ------------------------------------------------------------ validators

    let private expand (template: LocalCommandTemplate) (tokens: (string * string) list) workingDirectory : LocalWorkerProcessSpec =
        let replace (value: string) = tokens |> List.fold (fun (text: string) (token, path) -> text.Replace(token, path)) value
        { Executable = template.Executable; ExecutableDigest = template.ExecutableDigest
          Arguments = template.Arguments |> List.map replace; WorkingDirectory = workingDirectory
          Environment = template.Environment |> Map.map (fun _ value -> replace value); Input = replace template.Input
          TimeoutMilliseconds = template.TimeoutMilliseconds; MaxOutputBytes = template.MaxOutputBytes }

    let validatorRequest (configuration: LocalHostConfiguration) (template: LocalValidatorTemplate) candidate checkout trx : LocalValidatorRequest =
        { ValidatorId = template.Id; CandidateCommit = candidate
          GitExecutable = configuration.Git.Executable; GitExecutableDigest = configuration.Git.ExecutableDigest
          Dependencies = template.Command.Dependencies
          Process = expand template.Command [ "{candidate}", checkout; "{trx}", trx ] checkout
          TrxPath = (if template.Tests then Some trx else None); EvidenceRoot = configuration.EvidenceRoot }

    /// The signed pin a coordinator places in an acceptance obligation. It is
    /// independent of where the host later provisions checkouts and TRX files.
    let validatorPin (configuration: LocalHostConfiguration) validatorId =
        match configuration.AcceptanceValidators |> Map.tryFind validatorId with
        | None -> Error("host configuration has no acceptance validator " + validatorId)
        | Some template ->
            let request =
                validatorRequest configuration template (String('0', 40))
                    (Path.Combine(configuration.CheckoutRoot, "praxis-pin-candidate"))
                    (Path.Combine(configuration.EvidenceRoot, "praxis-pin.trx"))
            FileLocalValidator.inputDigest request

    /// Checkouts provisioned for this run, removed when it ends. One checkout
    /// and TRX path per (candidate, validator), stable across observations in
    /// the session as the checklist adapter requires; a retry gets new ones.
    type private CheckoutRegistry =
        { Requests: ConcurrentDictionary<string * string, LocalValidatorRequest>
          Paths: ConcurrentBag<string> }

    let private checklistValidators (configuration: LocalHostConfiguration) (registry: CheckoutRegistry) cancellation : LocalChecklistValidators =
        let template (row: WorkerChecklistRequirement) =
            match row.Kind, row.Validator with
            | WorkerCheckKind.Acceptance, Some pinned ->
                configuration.AcceptanceValidators |> Map.tryFind pinned.ValidatorId
                |> Option.map Ok |> Option.defaultValue (Error("acceptance validator is not configured: " + pinned.ValidatorId))
            | WorkerCheckKind.Acceptance, None -> Error "acceptance row has no signed validator"
            | kind, _ ->
                configuration.SystemValidators |> Map.tryFind kind
                |> Option.map Ok |> Option.defaultValue (Error "system validator is not configured")
        { PolicyRevision = fun () ->
            LocalHostConfiguration.unchanged configuration
            |> Result.map (fun () -> configuration.ValidatorPolicyRevision + "@" + configuration.Digest)
          Select = fun _ row candidate ->
            template row |> Result.bind (fun selected ->
                let key = candidate, selected.Id
                match registry.Requests.TryGetValue key with
                | true, request -> Ok request
                | _ ->
                    FileLocalGitCandidates.provisionCheckout configuration.Git configuration.RepositoryRoot configuration.CheckoutRoot
                        ("validator-" + label selected.Id) candidate cancellation
                    |> Result.map (fun checkout ->
                        registry.Paths.Add checkout
                        let trx = Path.Combine(configuration.EvidenceRoot, "trx-" + Guid.NewGuid().ToString("N") + ".trx")
                        registry.Requests.GetOrAdd(key, validatorRequest configuration selected candidate checkout trx))) }

    // --------------------------------------------------------------- workers

    let private catalog (configuration: LocalHostConfiguration) cancellation (worktrees: ConcurrentBag<string>) =
        configuration.Workers
        |> List.map (fun worker ->
            { Id = worker.AdapterId; WorkerId = worker.WorkerId
              OperatingSystems = worker.OperatingSystems; Roles = worker.Roles
              Prepare = fun admitted ->
                  let packet = LocalAgentHandoff.checkedPacket admitted
                  // The worker's tool closure is pinned like a validator's.
                  let changed = worker.Command.Dependencies |> Map.exists (fun path expected ->
                      not (Path.IsPathFullyQualified path && File.Exists path && LocalHostJournalFiles.noLinks path) || fileDigest path <> expected)
                  if changed then Error "worker tool closure pin changed"
                  else
                      FileLocalGitCandidates.provisionCheckout configuration.Git configuration.RepositoryRoot configuration.CheckoutRoot
                          ("worker-" + label packet.DispatchId) packet.SourceCommit cancellation
                      |> Result.map (fun worktree ->
                          worktrees.Add worktree
                          { expand worker.Command [ "{worktree}", worktree ] worktree with
                              Input = LocalAgentHandoffJson.renderPacket packet
                              TimeoutMilliseconds = min worker.Command.TimeoutMilliseconds (packet.TimeoutSeconds * 1000)
                              MaxOutputBytes = min worker.Command.MaxOutputBytes packet.MaxOutputBytes }) })
        |> LocalWorkerAdapters.catalog

    // ---------------------------------------------------------------- native

    let private readContext root =
        let path = Path.Combine(root, ".ros", "context", "current.json")
        if File.Exists path then WorkContextPlanContract.parseJson (File.ReadAllText path)
        else Error "native work context is unavailable"

    /// Evidence identity of a completed prerequisite, recomputed from native
    /// state at every boundary. Coordinators place this digest in packets.
    let prerequisiteDigest (item: LiveWorkItem) =
        framed ([ "praxis.local-prerequisite/1"; item.Id; Option.defaultValue "" item.CompletedAt ]
                @ (item.Evidence |> List.map (fun evidence -> evidence.Type + "=" + evidence.Path) |> List.sort))

    let observePrerequisites root () =
        readContext root |> Result.map (fun context ->
            context.WorkItems
            |> List.filter (fun item -> item.SemanticState = LiveWorkState.Complete)
            |> List.map (fun item -> item.Id, prerequisiteDigest item) |> Map.ofList)

    let private observeNative root (actor: Actor) () =
        readContext root |> Result.map (fun context ->
            let required, byType = FileWorkConfigRepository.readCompletionEvidence root
            let request : WorkContextPlanRequest =
                { Context = context; Action = WorkAction.Complete; WorkItemIds = []; NewItemType = "task"
                  TargetLocalState = "complete"; BlockReason = None
                  DefaultRequiredEvidence = required; RequiredEvidenceByType = byType; ProvidedEvidence = []
                  Repository = FileWorkConfigRepository.readRepositoryId root
                  ProtocolVersion = FileWorkConfigRepository.readProtocolVersion root
                  Actor = actor.Id; OccurredAt = ""; MeaningfulChangedPaths = []; ObservedGitPaths = []
                  TelemetryEnabled = FileWorkConfigRepository.readTelemetryEnabled root }
            let active =
                Praxis.Infrastructure.Provenance.FileProvenanceRepository.readExecutions root
                |> List.filter (fun view -> view.Status = "active")
                |> List.groupBy _.WorkItemId
                |> List.map (fun (id, views) -> id, views |> List.map _.ExecutionId |> List.sort)
                |> Map.ofList
            { Request = request; ActiveExecutions = active })

    let private readDelegation (path: string) () =
        try
            if not (Path.IsPathFullyQualified path && LocalHostJournalFiles.noLinks path) then Error "delegation path must be absolute and unlinked"
            else
                let info = FileInfo path
                if info.Length > 8192L then Error "delegation exceeds 8192 bytes"
                else Ok(File.ReadAllText path)
        with error -> Error("delegation unavailable: " + error.Message)

    // ----------------------------------------------------------- composition

    let private bindings (configuration: LocalHostConfiguration) (policy: LocalRuntimeNativePolicy) (actor: Actor)
                         (request: LocalRunRequest) (registry: CheckoutRegistry) worktrees sessionId cancellation =
        let root, git = configuration.RepositoryRoot, configuration.Git
        catalog configuration cancellation worktrees |> Result.map (fun workers ->
            let worker : LocalWorkerHostBindings =
                { RepositoryIdentity = configuration.RepositoryIdentity
                  JournalRoot = configuration.JournalRoot; SpoolRoot = configuration.SpoolRoot; SubmissionRoot = configuration.SubmissionRoot
                  Catalog = workers; Platform = LocalWorkerProcessPlatform.dotNet
                  LoadDelegation = readDelegation request.DelegationPath
                  ValidateNative = fun _ -> LocalHostConfiguration.unchanged configuration
                  Prerequisites = observePrerequisites root
                  ControllerSessionId = fun () -> Ok sessionId }
            let integration (intake: LocalSubmissionIntakePorts) (journal: LocalIntegrationJournalStore) : LocalIntegrationPorts =
                { Journal = journal; Intake = intake
                  Acquire = fun _ -> Error "the host supplies the integration lease"
                  ReadTarget = fun target -> FileLocalGitCandidates.readTarget git root target cancellation
                  Stage = fun snapshot target before -> FileLocalGitCandidates.stage git root configuration.Committer snapshot target before cancellation
                  ValidateCandidate = fun snapshot plan -> FileLocalGitCandidates.validateCandidate git root snapshot plan cancellation
                  CompareAndSwap = fun plan -> FileLocalGitCandidates.compareAndSwap git root plan cancellation }
            let completion (ports: LocalIntegrationPorts) : LocalIntegratedCompletionPorts =
                { Integration = ports
                  ObserveNative = observeNative root actor
                  CandidateEvidence = fun snapshot plan -> FileLocalGitCandidates.candidateEvidence git root configuration.CompletionEvidence snapshot plan cancellation
                  Evidence = FileLocalGitCandidates.evidenceRepository git root request.TargetRef cancellation
                  QualityGate = policy.QualityGate
                  CheckpointGuard = policy.CheckpointGuard }
            let acceptance : LocalWorkerAcceptanceBindings =
                { EvidenceRoot = configuration.EvidenceRoot; IntegrationJournalRoot = configuration.IntegrationRoot
                  EvidenceAccess = configuration.FileAccess
                  Validators = checklistValidators configuration registry cancellation
                  ObserveResult = fun packet result -> FileLocalGitCandidates.observeResult git root packet result cancellation
                  Integration = integration; Completion = completion
                  EventActor = actor; Readiness = policy.Readiness; Conclusion = None }
            worker, acceptance)

    /// Mirror completed members into their execution envelopes under the
    /// lifecycle lease, as the standalone completion command does.
    let private mirrorCompleted root (actor: Actor) (members: string list) =
        ExecutionLifecycleLease.within root (fun () ->
            let ports = FileExecutionPorts.createLocked root
            let requester = { Actor = { Id = actor.Id; Kind = ActorKind.code actor.Kind
                                        Provider = actor.Provider |> Option.filter ((<>) Actor.UnknownValue)
                                        Model = actor.Model |> Option.filter ((<>) Actor.UnknownValue)
                                        Runtime = actor.Runtime |> Option.filter ((<>) Actor.UnknownValue) } }
            for id in members do ExecutionBinding.mirror ports requester id "work.complete" ExecutionState.Completed None |> ignore)

    let private dispatchPhase (configuration: LocalHostConfiguration) dispatchId =
        FileLocalDispatchJournal.create configuration.RepositoryRoot configuration.JournalRoot
        |> Result.map (fun journal ->
            match journal.Load configuration.RepositoryIdentity dispatchId with
            | Ok saved -> LocalDispatchJournal.phase saved |> Result.toOption
            | Error _ -> None)

    /// Dispatch a signed packet to its worker and, after the worker exits and
    /// its output is archived, accept, integrate and complete it. An exited,
    /// archived dispatch resumes at acceptance; every other existing journal
    /// state is refused for operator reconciliation rather than relaunched.
    let run (configuration: LocalHostConfiguration) (policy: LocalRuntimeNativePolicy) (actor: Actor) (request: LocalRunRequest) (cancellation: CancellationToken) =
        let registry = { Requests = ConcurrentDictionary(); Paths = ConcurrentBag() }
        let worktrees = ConcurrentBag<string>()
        let sessionId = "praxis-local-" + Guid.NewGuid().ToString("N")
        let packet = request.Packet
        let cleanup () =
            for path in registry.Paths do FileLocalGitCandidates.removeCheckout configuration.Git configuration.RepositoryRoot path cancellation |> ignore
        try
            if packet.RepositoryIdentity <> configuration.RepositoryIdentity then Error "packet repository differs from the host configuration"
            elif not (LocalIntegrationJournal.isTargetRef request.TargetRef) then Error "invalid integration target ref"
            else
                let host = hostStore configuration
                readContext configuration.RepositoryRoot
                |> Result.bind (fun context ->
                    WorkerChecklistCompletionPolicy.requireDelegated configuration.RepositoryRoot context.WorkItems (packet.Members |> List.map _.WorkItemId))
                |> Result.bind (fun () -> bindings configuration policy actor request registry worktrees sessionId cancellation)
                |> Result.bind (fun (worker, acceptance) ->
                    dispatchPhase configuration packet.DispatchId |> Result.bind (fun phase ->
                        let outcome, stage =
                            match phase with
                            | None -> FileLocalWorkerHost.runAndComplete host worker acceptance packet request.TargetRef cancellation, LocalRunStage.Dispatched
                            | Some(LocalDispatchPhase.Exited _) -> FileLocalWorkerHost.acceptAndComplete host worker acceptance packet request.TargetRef cancellation, LocalRunStage.Resumed
                            | Some other ->
                                System.Threading.Tasks.Task.FromResult(Error(sprintf "dispatch %s is %A; operator reconciliation is required before any relaunch" packet.DispatchId other)),
                                LocalRunStage.Resumed
                        outcome.GetAwaiter().GetResult() |> Result.bind (fun (_: Text.Json.Nodes.JsonArray * string list) ->
                            let members = packet.Members |> List.map _.WorkItemId
                            mirrorCompleted configuration.RepositoryRoot actor members
                            |> Result.mapError (fun error -> "native completion succeeded but execution envelopes were not mirrored: " + error)
                            |> Result.bind (fun () ->
                                FileLocalGitCandidates.readTarget configuration.Git configuration.RepositoryRoot request.TargetRef cancellation
                                |> Result.map (fun target ->
                                    { DispatchId = packet.DispatchId; Stage = stage; TargetRef = request.TargetRef; TargetCommit = target
                                      CompletedMembers = members; WorkerWorktree = worktrees |> Seq.tryHead })))))
        finally cleanup ()
