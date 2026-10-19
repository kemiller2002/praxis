namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open Praxis.Application.Work
open Praxis.Domain.Work

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

/// Single-host claim store, lock-free: each durable write is a new
/// revision directory `.ros/claims/<work-item-id>/<revision>/claim.json`.
/// Revisions are dense (`next = expected + 1`), so "publish revision N+1"
/// must succeed for exactly one writer. The record is staged in a private
/// directory and renamed into place; rename(2) onto an existing, non-empty
/// directory fails atomically, so the rename is the compare-and-swap and a
/// writer with a stale expected revision always finds N+1 taken. Readers
/// never see a partial record. History is append-only and additive;
/// `.ros/work/queue.json` is never touched. Valid on one host's filesystem
/// only: remote agents use the Git-ref store.
[<RequireQualifiedAccess>]
module FileWorkClaimStore =
    let directory (root: string) = Path.Combine(root, ".ros", "claims")

    let itemDirectory (root: string) (id: string) = Path.Combine(directory root, id)

    let revisionDirectory (root: string) (id: string) (revision: int64) =
        Path.Combine(itemDirectory root id, $"{revision:D12}")

    let path (root: string) (id: string) (revision: int64) =
        Path.Combine(revisionDirectory root id revision, "claim.json")

    let private revisions (root: string) (id: string) =
        let folder = itemDirectory root id

        if Directory.Exists folder then
            Directory.GetDirectories folder
            |> Array.choose (fun entry ->
                match Int64.TryParse(Path.GetFileName entry) with
                | true, revision -> Some revision
                | _ -> None)
            |> Array.sort
            |> Array.toList
        else
            []

    let private read (root: string) (id: string) =
        if not (WorkItemId.isValid id) then
            Error $"'{id}' is not a work-item id"
        else
            try
                match revisions root id |> List.tryLast with
                | None -> Ok None
                | Some revision ->
                    WorkClaimJson.parseClaim (File.ReadAllText(path root id revision, Encoding.UTF8))
                    |> Result.bind (fun claim ->
                        if claim.Revision = revision && claim.WorkItemId = id then Ok(Some claim)
                        else Error $"claim file {revision} for '{id}' does not match its record")
            with error ->
                Error $"cannot read claim '{id}': {error.Message}"

    let private compareAndSwap (root: string) (expected: int64 option) (next: WorkClaim) =
        let id = next.WorkItemId

        if not (WorkItemId.isValid id) then
            Error $"'{id}' is not a work-item id"
        elif next.Revision <> (expected |> Option.defaultValue 0L) + 1L then
            Error $"claim revision {next.Revision} does not follow expected revision {expected}"
        else
            let folder = itemDirectory root id
            let stage = Path.Combine(folder, $".stage-{Guid.NewGuid():N}")
            let target = revisionDirectory root id next.Revision

            try
                try
                    Directory.CreateDirectory stage |> ignore
                    File.WriteAllText(Path.Combine(stage, "claim.json"), WorkClaimJson.renderClaim next, UTF8Encoding(false))

                    match read root id with
                    | Error message -> Error message
                    | Ok current when current |> Option.map _.Revision <> expected -> Ok(ClaimWrite.Conflict current)
                    | Ok _ ->
                        try
                            Directory.Move(stage, target)
                            Ok(ClaimWrite.Written next)
                        with :? IOException when Directory.Exists target ->
                            read root id |> Result.map ClaimWrite.Conflict
                with error ->
                    Error $"cannot write claim '{id}': {error.Message}"
            finally
                try
                    if Directory.Exists stage then Directory.Delete(stage, true)
                with _ ->
                    ()

    let private list (root: string) () =
        try
            if Directory.Exists(directory root) then
                Directory.GetDirectories(directory root)
                |> Array.map Path.GetFileName
                |> Array.filter WorkItemId.isValid
                |> Array.sort
                |> Array.toList
                |> List.fold
                    (fun state id ->
                        state
                        |> Result.bind (fun claims -> read root id |> Result.map (fun claim -> claims @ Option.toList claim)))
                    (Ok [])
            else
                Ok []
        with error ->
            Error $"cannot list claims: {error.Message}"

    let create (root: string) : WorkClaimStore =
        { Read = read root
          CompareAndSwap = compareAndSwap root
          List = list root }
