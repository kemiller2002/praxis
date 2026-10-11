namespace Praxis.Infrastructure.Planning

open System
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
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
                        let native () = recover() |> Result.bind (fun () -> bindings.ValidateNative packet)
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
