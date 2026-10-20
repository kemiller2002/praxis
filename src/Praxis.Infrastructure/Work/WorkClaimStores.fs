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
/// revision directory `<store>/claims/<work-item-id>/<revision>/claim.json`
/// in the out-of-tree transient store (I8, `TransientStore`).
/// Revisions are dense (`next = expected + 1`), so "publish revision N+1"
/// must succeed for exactly one writer. The record is staged in a private
/// directory and renamed into place; rename(2) onto an existing, non-empty
/// directory fails atomically, so the rename is the compare-and-swap and a
/// writer with a stale expected revision always finds N+1 taken. Readers
/// never see a partial record. History is append-only and additive;
/// `.ros/work/queue.json` is never touched. Valid on one host's filesystem
/// only: remote agents use the Git-ref store. A claim that exists only in the
/// legacy tracked `.ros/claims` store is refused with `PRX.CLAIM.LEGACY_STORE`
/// until `praxis work claims migrate` moves it; it is never silently lost.
[<RequireQualifiedAccess>]
module FileWorkClaimStore =
    let LegacyStoreCode = "PRX.CLAIM.LEGACY_STORE"

    let directory (root: string) = TransientStore.claims root

    let itemDirectory (root: string) (id: string) = Path.Combine(directory root, id)

    let revisionDirectory (root: string) (id: string) (revision: int64) =
        Path.Combine(itemDirectory root id, $"{revision:D12}")

    let path (root: string) (id: string) (revision: int64) =
        Path.Combine(revisionDirectory root id revision, "claim.json")

    let private revisionsIn (folder: string) =
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

    let private revisions (root: string) (id: string) = revisionsIn (itemDirectory root id)

    let private legacyRevisions (root: string) (id: string) = revisionsIn (Path.Combine(TransientStore.legacyClaims root, id))

    let private legacyRefusal (ids: string list) =
        let shown = ids |> List.truncate 10 |> String.concat ", "

        Error
            $"{LegacyStoreCode}: claim(s) {shown} exist only in the legacy tracked store .ros/claims; run 'praxis work claims migrate' to move them to the out-of-tree store"

    let private read (root: string) (id: string) =
        if not (WorkItemId.isValid id) then
            Error $"'{id}' is not a work-item id"
        else
            try
                match revisions root id |> List.tryLast with
                | None when not (legacyRevisions root id).IsEmpty -> legacyRefusal [ id ]
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

    let private legacyOnly (root: string) =
        let legacy = TransientStore.legacyClaims root

        if Directory.Exists legacy then
            Directory.GetDirectories legacy
            |> Array.map Path.GetFileName
            |> Array.filter (fun id -> WorkItemId.isValid id && (revisions root id).IsEmpty && not (legacyRevisions root id).IsEmpty)
            |> Array.sort
            |> Array.toList
        else
            []

    let private list (root: string) () =
        let current () =
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

        try
            match legacyOnly root with
            | (_ :: _) as ids -> legacyRefusal ids
            | [] -> current ()
        with error ->
            Error $"cannot list claims: {error.Message}"

    /// One legacy item's migration outcome: `migrated`, `already-migrated`
    /// (the out-of-tree store already holds that revision or a later one) or
    /// `conflict` (the stores diverged; the legacy record is kept untouched).
    type Migration =
        { WorkItemId: string
          Outcome: string
          Revisions: int
          ValidationFiles: int }

    /// `praxis work claims migrate`: copies every revision of every legacy
    /// `.ros/claims/<ID>` claim (and its validation results) into the
    /// out-of-tree store, reads the latest revision back through the store,
    /// and only then removes the legacy directory (unless `keepLegacy`).
    let migrate (root: string) (keepLegacy: bool) : Result<Migration list, string> =
        let legacy = TransientStore.legacyClaims root

        let copyTree (source: string) (target: string) =
            if Directory.Exists source then
                Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                |> Array.sumBy (fun file ->
                    let destination = Path.Combine(target, Path.GetRelativePath(source, file))
                    Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore

                    if File.Exists destination then
                        0
                    else
                        File.Copy(file, destination)
                        1)
            else
                0

        let migrateOne (id: string) =
            let source = Path.Combine(legacy, id)
            let fromLegacy = legacyRevisions root id
            let latestLegacy = fromLegacy |> List.tryLast |> Option.defaultValue 0L
            let validations () = copyTree (Path.Combine(source, "validations")) (Path.Combine(TransientStore.validations root, id))

            let finish outcome copied =
                if not keepLegacy && outcome <> "conflict" then Directory.Delete(source, true)
                Ok { WorkItemId = id; Outcome = outcome; Revisions = fromLegacy.Length; ValidationFiles = copied }

            match revisions root id with
            | [] ->
                for revision in fromLegacy do
                    let target = revisionDirectory root id revision
                    Directory.CreateDirectory target |> ignore
                    File.Copy(Path.Combine(source, $"{revision:D12}", "claim.json"), Path.Combine(target, "claim.json"), true)

                match read root id with
                | Ok(Some claim) when claim.Revision = latestLegacy -> finish "migrated" (validations ())
                | Ok _ -> Error $"claim '{id}' did not read back at revision {latestLegacy} after migration; the legacy record is kept"
                | Error message -> Error $"claim '{id}' did not read back after migration ({message}); the legacy record is kept"
            | current when List.last current >= latestLegacy -> finish "already-migrated" (validations ())
            | _ -> Ok { WorkItemId = id; Outcome = "conflict"; Revisions = fromLegacy.Length; ValidationFiles = 0 }

        try
            if Directory.Exists legacy then
                Directory.GetDirectories legacy
                |> Array.map Path.GetFileName
                |> Array.filter WorkItemId.isValid
                |> Array.sort
                |> Array.toList
                |> List.fold (fun state id -> state |> Result.bind (fun done' -> migrateOne id |> Result.map (fun item -> done' @ [ item ]))) (Ok [])
            else
                Ok []
        with error ->
            Error $"cannot migrate legacy claims: {error.Message}"

    let create (root: string) : WorkClaimStore =
        { Read = read root
          CompareAndSwap = compareAndSwap root
          List = list root }
