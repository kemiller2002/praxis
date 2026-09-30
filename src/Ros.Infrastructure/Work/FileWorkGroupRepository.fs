namespace Ros.Infrastructure.Work

open System.IO
open Ros.Contracts.Work
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts

/// Declared execution groups in `.ros/work/groups.json` (analysis D1). The
/// only writer of that file; it writes nothing else. A missing file means
/// "no groups".
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let relativePath = ".ros/work/groups.json"

    let private path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    let exists (root: string) = File.Exists(path root)

    let read (root: string) : Result<StoredGroups, string> =
        let file = path root

        if not (File.Exists file) then
            Ok(WorkGroups.empty (FileWorkConfigRepository.readRepositoryId root))
        else
            try
                WorkGroupJson.parse (File.ReadAllText file)
            with error ->
                Error $"cannot read {relativePath}: {error.Message}"

    let private write (root: string) (stored: StoredGroups) : Result<unit, string> =
        RegistryTransaction.writeAtomic (path root) (WorkGroupJson.render stored)
        |> Result.mapError (fun failure -> $"state persistence failed: {failure.Message}")

    /// Reads, decides and (unless `dryRun`) writes, under the `work-protocol`
    /// lock so a membership decision cannot race a lifecycle transition. The
    /// decision is a pure function of the stored groups; a rejection writes
    /// nothing. The outer error is a persistence failure; the inner result
    /// is the decision.
    let mutate
        (root: string)
        (dryRun: bool)
        (decide: StoredGroups -> Result<StoredGroups * 'value, 'rejection>)
        : Result<Result<'value, 'rejection>, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                read root
                |> Result.bind (fun stored ->
                    match decide stored with
                    | Error rejection -> Ok(Error rejection)
                    | Ok(_, value) when dryRun -> Ok(Ok value)
                    | Ok(next, value) -> write root next |> Result.map (fun () -> Ok value))

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    /// The planner's view of stored groups: none when the file is absent.
    let readForPlanning (root: string) : Result<StoredGroup list, string> =
        read root |> Result.map (fun stored -> stored.Groups)
