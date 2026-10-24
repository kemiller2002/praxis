namespace Praxis.Infrastructure.Work

open System
open System.IO
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module FileLocalIntegrationJournal =
    let private entries target =
        let names = Directory.GetFileSystemEntries target |> Array.map Path.GetFileName |> Array.sort
        if names <> [| "plan.json" |] && names <> [| "applied.json"; "plan.json" |] then invalidOp "incomplete or unexpected integration journal"
        for name in names do
            if not (LocalHostJournalFiles.noLinks (Path.Combine(target, name))) then invalidOp "integration entry contains a link"
        names
    /// `reserved` names top-level directories another host record owns in the
    /// same root (construction routing records); they are never read as
    /// integration entries and must still be real, unlinked directories.
    let createReserving (reserved: Set<string>) repositoryRoot storeRoot : Result<LocalIntegrationJournalStore, string> =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot storeRoot
            let directory repo dispatch = LocalHostJournalFiles.directory root repo dispatch
            let guarded operation = try operation() with e -> Error("local integration journal refused: " + e.Message)
            let load repo dispatch = guarded (fun () ->
                let target = directory repo dispatch
                if not (Directory.Exists target) then Ok None
                else
                    let names = entries target
                    LocalHostJournalFiles.readBounded (Path.Combine(target, "plan.json")) |> LocalIntegrationJournalJson.readPlan |> Result.bind (fun plan ->
                        if plan.RepositoryIdentity <> repo || plan.DispatchId <> dispatch then Error "integration lookup identity differs"
                        elif names.Length = 1 then Ok(Some { Plan = plan; AppliedAt = None })
                        else LocalHostJournalFiles.readBounded (Path.Combine(target, "applied.json")) |> LocalIntegrationJournalJson.readApplied plan
                             |> Result.map (fun at -> Some { Plan = plan; AppliedAt = Some at })))
            let prepare plan = guarded (fun () ->
                LocalIntegrationJournal.validate { Plan = plan; AppliedAt = None } |> Result.bind (fun () ->
                    let target = directory plan.RepositoryIdentity plan.DispatchId
                    Directory.CreateDirectory target |> ignore
                    try
                        if Directory.GetFileSystemEntries(target).Length <> 0 then raise (IOException "integration already exists or is partial")
                        LocalHostJournalFiles.writeExclusive root (Path.Combine(target, "plan.json")) (LocalIntegrationJournalJson.renderPlan plan)
                        Ok LocalIntegrationPrepareOutcome.Created
                    with :? IOException ->
                        load plan.RepositoryIdentity plan.DispatchId |> Result.bind (function
                            | Some existing when existing.Plan = plan -> Ok LocalIntegrationPrepareOutcome.Existing
                            | _ -> Error "integration cannot replace its original plan")))
            let applied plan at = guarded (fun () ->
                LocalIntegrationJournal.validate { Plan = plan; AppliedAt = Some at } |> Result.bind (fun () ->
                    load plan.RepositoryIdentity plan.DispatchId |> Result.bind (function
                        | Some journal when journal.Plan = plan ->
                            if journal.AppliedAt.IsSome then Ok()
                            else
                                LocalHostJournalFiles.writeExclusive root (Path.Combine(directory plan.RepositoryIdentity plan.DispatchId, "applied.json")) (LocalIntegrationJournalJson.renderApplied plan at)
                                Ok()
                        | _ -> Error "applied observation differs from prepared integration")))
            let pending repo = guarded (fun () ->
                let paths = Directory.GetFileSystemEntries root |> Array.filter (LocalHostJournalFiles.isStaging >> not)
                if paths.Length > 256 then invalidOp "integration recovery inventory exceeds bound"
                let mutable result = []
                for path in paths do
                    if not (LocalHostJournalFiles.noLinks path) || not (Directory.Exists path) then invalidOp "unexpected integration root entry"
                    if Path.GetFileName path <> "locks" && not (reserved.Contains(Path.GetFileName path)) then
                        entries path |> ignore
                        let plan = LocalHostJournalFiles.readBounded (Path.Combine(path, "plan.json")) |> LocalIntegrationJournalJson.readPlan |> function Ok value -> value | Error error -> invalidOp error
                        if path <> directory plan.RepositoryIdentity plan.DispatchId then invalidOp "integration plan stored under another identity"
                        match load plan.RepositoryIdentity plan.DispatchId with
                        | Ok(Some journal) when plan.RepositoryIdentity = repo && journal.AppliedAt.IsNone -> result <- plan.DispatchId :: result
                        | Ok(Some _) -> ()
                        | _ -> invalidOp "integration recovery inventory is incomplete"
                Ok result)
            Ok { Pending = pending; Load = load; Prepare = prepare; RecordApplied = applied }
        with e -> Error("cannot configure integration journal: " + e.Message)

    let create repositoryRoot storeRoot = createReserving Set.empty repositoryRoot storeRoot

    /// Mechanism lock for cooperating controllers, NOT policy/OS protection.
    /// Git CAS remains mandatory against writers outside this convention.
    let acquire repositoryRoot storeRoot repositoryIdentity : Result<IDisposable, string> =
        try
            let root = LocalHostJournalFiles.rootOutside repositoryRoot storeRoot
            let locks = Path.Combine(root, "locks")
            if not (LocalHostJournalFiles.noLinks locks) then invalidOp "integration lock root contains a link"
            Directory.CreateDirectory locks |> ignore
            let target = LocalHostJournalFiles.directory locks repositoryIdentity "repository"
            Directory.CreateDirectory target |> ignore
            let file = Path.Combine(target, "integration.lock")
            if not (LocalHostJournalFiles.noLinks file) then invalidOp "integration lock contains a link"
            Ok(new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) :> IDisposable)
        with e -> Error("integration lease unavailable: " + e.Message)
