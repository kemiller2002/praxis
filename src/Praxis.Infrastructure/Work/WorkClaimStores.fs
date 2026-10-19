namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts

/// Deterministic in-process claim store: one immutable map behind a lock,
/// so compare-and-swap is linearizable across threads.
[<RequireQualifiedAccess>]
module InMemoryWorkClaimStore =
    let create () : WorkClaimStore =
        let gate = obj ()
        let mutable claims: Map<string, WorkClaim> = Map.empty

        { Read = fun id -> lock gate (fun () -> Ok(claims |> Map.tryFind id))
          CompareAndSwap =
            fun expected next ->
                lock gate (fun () ->
                    let current = claims |> Map.tryFind next.WorkItemId

                    if current |> Option.map _.Revision = expected then
                        claims <- claims |> Map.add next.WorkItemId next
                        Ok(ClaimWrite.Written next)
                    else
                        Ok(ClaimWrite.Conflict current))
          List = fun () -> lock gate (fun () -> Ok(claims |> Map.toList |> List.map snd)) }

/// Single-host claim store: one `praxis.work-claim/1` file per work item
/// under `.ros/claims/` (additive; `.ros/work/queue.json` is never
/// touched). A write holds the existing per-resource registry lock, checks
/// the stored revision, then publishes by atomic rename; a first write is
/// create-only, so even a lock bypass cannot overwrite an existing claim.
/// Not valid across hosts: remote agents use the Git-ref store.
[<RequireQualifiedAccess>]
module FileWorkClaimStore =
    let directory (root: string) = Path.Combine(root, ".ros", "claims")

    let path (root: string) (id: string) = Path.Combine(directory root, $"{id}.json")

    let private read (root: string) (id: string) =
        if not (WorkItemId.isValid id) then
            Error $"'{id}' is not a work-item id"
        else
            let file = path root id

            try
                if File.Exists file then
                    WorkClaimJson.parseClaim (File.ReadAllText(file, Encoding.UTF8)) |> Result.map Some
                else
                    Ok None
            with error ->
                Error $"cannot read claim '{id}': {error.Message}"

    let private publish (root: string) (expected: int64 option) (next: WorkClaim) =
        let file = path root next.WorkItemId
        let temporary = Path.Combine(directory root, $".{next.WorkItemId}.{Guid.NewGuid():N}.tmp")
        Directory.CreateDirectory(directory root) |> ignore
        File.WriteAllText(temporary, WorkClaimJson.renderClaim next, UTF8Encoding(false))

        try
            // Create-only when no record may exist; replace otherwise.
            File.Move(temporary, file, expected.IsSome)
        finally
            if File.Exists temporary then File.Delete temporary

    let create (root: string) (settings: RegistryLockSettings) : WorkClaimStore =
        let compareAndSwap (expected: int64 option) (next: WorkClaim) =
            if not (WorkItemId.isValid next.WorkItemId) then
                Error $"'{next.WorkItemId}' is not a work-item id"
            else
                match RegistryLock.acquire root $"work-claim:{next.WorkItemId}" settings with
                | Error failure -> Error $"claim lock unavailable: {failure.Message}"
                | Ok lease ->
                    let outcome =
                        match read root next.WorkItemId with
                        | Error message -> Error message
                        | Ok current when current |> Option.map _.Revision <> expected -> Ok(ClaimWrite.Conflict current)
                        | Ok _ ->
                            try
                                publish root expected next
                                Ok(ClaimWrite.Written next)
                            with
                            | :? IOException when expected.IsNone -> read root next.WorkItemId |> Result.map ClaimWrite.Conflict
                            | error -> Error $"cannot write claim '{next.WorkItemId}': {error.Message}"

                    match lease.Release(), outcome with
                    | Ok(), _ -> outcome
                    | Error _, Ok written -> Ok written
                    | Error failure, Error message -> Error $"{message}; {failure.Message}"

        let list () =
            try
                if Directory.Exists(directory root) then
                    Directory.GetFiles(directory root, "*.json")
                    |> Array.sort
                    |> Array.toList
                    |> List.map (fun file -> read root (Path.GetFileNameWithoutExtension file))
                    |> List.fold
                        (fun state item ->
                            match state, item with
                            | Ok claims, Ok(Some claim) -> Ok(claims @ [ claim ])
                            | Ok claims, Ok None -> Ok claims
                            | Error message, _
                            | _, Error message -> Error message)
                        (Ok [])
                else
                    Ok []
            with error ->
                Error $"cannot list claims: {error.Message}"

        { Read = read root
          CompareAndSwap = compareAndSwap
          List = list }
