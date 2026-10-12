namespace Praxis.Infrastructure.Planning

open System
open System.Threading
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Domain.Provenance
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Work

/// Provision these independently of worker input. Native execution/dependency
/// observations remain host obligations, separate from portable transport.
type LocalWorkerHostBindings =
    { RepositoryIdentity: string
      JournalRoot: string; SpoolRoot: string; SubmissionRoot: string
      Catalog: LocalWorkerAdapterCatalog; Platform: LocalWorkerProcessPlatform
      LoadDelegation: unit -> Result<string, string>
      ValidateNative: LocalWorkerPacket -> Result<unit, string>
      Prerequisites: unit -> Result<Map<string, string>, string>
      ControllerSessionId: unit -> Result<string, string> }

/// Host-provisioned candidate/native adapters. No worker result or repository
/// configuration can select these ports or provide a passing checklist.
type LocalWorkerAcceptanceBindings =
    { EvidenceRoot: string; IntegrationJournalRoot: string
      EvidenceAccess: LocalHostFileAccess; Validators: LocalChecklistValidators
      ObserveResult: LocalWorkerPacket -> LocalWorkerResult -> Result<LocalWorkerResultObservation, string>
      Integration: LocalSubmissionIntakePorts -> LocalIntegrationJournalStore -> LocalIntegrationPorts
      Completion: LocalIntegrationPorts -> LocalIntegratedCompletionPorts
      EventActor: Actor
      /// Quality readiness records for the exact planned completion.
      Readiness: WorkContextPlan -> Result<Map<string, JsonObject>, string>
      Conclusion: string option }

/// Concrete portable composition. Native/policy leases span admission, launch,
/// observation, spool sealing and immutable archival. No acceptance is inferred.
[<RequireQualifiedAccess>]
module FileLocalWorkerHost =
    let private withNativeLease root resource action = task {
        match RegistryLock.acquire root resource RegistryLock.defaultSettings with
        | Error error -> return Error error.Message
        | Ok lease ->
            let! result = task {
                try return! action()
                with e -> return Error("local worker host operation failed: " + e.GetType().Name) }
            match lease.Release(), result with
            | Error error, Ok _ -> return Error("local worker host lease release uncertain: " + error.Message)
            | _, value -> return value }

    let run (host: LocalEcirHostStore) (bindings: LocalWorkerHostBindings) (packet: LocalWorkerPacket) cancellation =
        let root = host.RepositoryRoot
        let now = host.Authority.UtcNow
        withNativeLease root "work-protocol" (fun () ->
            withNativeLease root "work-groups" (fun () ->
                host.WorkerScope(fun loadPolicy recover -> task {
                    let stores =
                        FileLocalDispatchJournal.create root bindings.JournalRoot |> Result.bind (fun journal ->
                            FileLocalWorkerOutputSpool.create root bindings.SpoolRoot |> Result.bind (fun spool ->
                                FileLocalWorkerSubmissions.create root bindings.SubmissionRoot
                                |> Result.map (fun submissions -> journal, spool, submissions)))
                    match stores with
                    | Error error -> return Error error
                    | Ok(journal, spool, submissions) ->
                        let native () =
                            recover()
                            |> Result.bind (fun () -> FileLocalWorkerAssignment.validate root bindings.RepositoryIdentity packet)
                            |> Result.bind (fun () -> bindings.ValidateNative packet)
                        let controller : LocalDispatchControllerPorts =
                            { Store = journal
                              Authority = LocalWorkerDelegation.authority root bindings.RepositoryIdentity loadPolicy bindings.LoadDelegation now packet
                              NativeRecovery = native; Prerequisites = bindings.Prerequisites
                              ControllerSessionId = bindings.ControllerSessionId; Now = now }
                        let worker = LocalWorkerProcess.createWithPlatform bindings.Platform
                        let dispatch : LocalJournaledDispatchPorts =
                            { Controller = controller; Worker = worker
                              SelectSpec = LocalWorkerProcessPlatform.selectCurrent bindings.Catalog }
                        let ports : LocalSpooledDispatchPorts =
                            { Dispatch = dispatch; Spool = spool
                              StreamingWorker = LocalWorkerProcess.createStreamingWithPlatform bindings.Platform }
                        let! result = LocalSpooledDispatch.run ports packet cancellation
                        return result |> Result.map (fun report ->
                            let capture : LocalSubmissionCapturePorts =
                                { Journal = journal; Submissions = submissions
                                  ControllerSessionId = bindings.ControllerSessionId; Now = now }
                            { Dispatch = report; Archive = LocalSubmissionIntake.capture capture packet report }) })))

    /// Consume an archived exit under the same ordered native/group/policy
    /// leases as dispatch. Concrete checklists replace any factory-supplied
    /// observer, and native completion repeats validation at the effect guard.
    let acceptAndComplete (host: LocalEcirHostStore) (worker: LocalWorkerHostBindings)
                          (bindings: LocalWorkerAcceptanceBindings) (packet: LocalWorkerPacket) targetRef cancellation =
        let root = host.RepositoryRoot
        let now = host.Authority.UtcNow
        withNativeLease root "work-protocol" (fun () ->
            withNativeLease root "work-groups" (fun () ->
                host.WorkerScope(fun loadPolicy recover -> task {
                    let stores =
                        [ worker.JournalRoot; worker.SubmissionRoot; bindings.IntegrationJournalRoot; bindings.EvidenceRoot ]
                        |> List.fold (fun current path -> current |> Result.bind (fun () -> bindings.EvidenceAccess.CheckDirectory path)) (Ok())
                        |> Result.bind (fun () ->
                            FileLocalDispatchJournal.create root worker.JournalRoot |> Result.bind (fun dispatch ->
                                FileLocalWorkerSubmissions.create root worker.SubmissionRoot |> Result.bind (fun submissions ->
                                    FileLocalIntegrationJournal.create root bindings.IntegrationJournalRoot
                                    |> Result.map (fun journal -> dispatch, submissions, journal))))
                    match stores with
                    | Error error -> return Error error
                    | Ok(dispatch, submissions, journal) ->
                        let policyRevision () =
                            loadPolicy() |> Result.bind (fun policy ->
                                bindings.Validators.PolicyRevision() |> Result.map (fun revision -> policy.Revision + "/" + revision))
                        let validators = { bindings.Validators with PolicyRevision = policyRevision }
                        return FileWorkerChecklist.withEvidenceSession root bindings.EvidenceRoot bindings.EvidenceAccess validators now cancellation (fun session ->
                            // An applied integration for this exact dispatch means a
                            // previous completion may have finalized member telemetry
                            // before it was interrupted; that state is resumable.
                            let resumable () =
                                match journal.Load worker.RepositoryIdentity packet.DispatchId with
                                | Ok(Some applied) when applied.AppliedAt.IsSome -> packet.Members |> List.map _.ExecutionId |> Set.ofList
                                | _ -> Set.empty
                            let native () =
                                recover() |> Result.bind (fun () -> FileLocalWorkerAssignment.validateResuming root worker.RepositoryIdentity (resumable()) packet)
                                |> Result.bind (fun () -> worker.ValidateNative packet)
                            let observe packet result =
                                bindings.ObserveResult packet result |> Result.bind (fun observation ->
                                    session.Acceptance packet result |> Result.map (fun acceptance -> { observation with Acceptance = acceptance }))
                            let intake : LocalSubmissionIntakePorts =
                                { Journal = dispatch; Submissions = submissions
                                  NativeRecovery = native
                                  ProcessRecovery = fun journal -> LocalDispatchJournal.phase journal |> Result.bind (function
                                      | LocalDispatchPhase.Exited _ -> Ok()
                                      | _ -> Error "acceptance requires an independently journaled worker root exit")
                                  Authority = LocalWorkerDelegation.authority root worker.RepositoryIdentity loadPolicy worker.LoadDelegation now packet
                                  Prerequisites = worker.Prerequisites; Observe = observe
                                  Checklists = session.Checks; Now = now }
                            let selected = bindings.Integration intake journal
                            let integration =
                                { selected with Journal = journal; Intake = intake
                                                // Real leases are held by this enclosing host scope.
                                                Acquire = fun repository ->
                                                    if repository <> worker.RepositoryIdentity then Error "integration repository differs from host"
                                                    else Ok { new IDisposable with member _.Dispose() = () } }
                            let completion = { (bindings.Completion integration) with Integration = integration }
                            LocalIntegration.run integration worker.RepositoryIdentity packet.DispatchId targetRef
                            |> Result.bind (fun _ ->
                                FileLocalIntegratedCompletion.commitLocked root completion worker.RepositoryIdentity packet.DispatchId targetRef
                                    bindings.EventActor bindings.Readiness bindings.Conclusion)) })))

    /// Dispatch and acceptance are separate scopes; acceptance always reopens
    /// current policy and native state after the worker has exited and archived.
    let runAndComplete host worker bindings packet targetRef cancellation = task {
        let! dispatched = run host worker packet cancellation
        match dispatched with
        | Error error -> return Error error
        | Ok report ->
            match report.Archive with
            | Error error -> return Error error
            | Ok _ -> return! acceptAndComplete host worker bindings packet targetRef cancellation }
