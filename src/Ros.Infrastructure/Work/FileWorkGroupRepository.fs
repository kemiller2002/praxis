namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text
open Ros.Contracts.Work
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts

/// Stored work groups at `.ros/work/groups.json`. Group commands write this
/// file and nothing else: membership never touches the backlog queue, the
/// live context or the event log (PRX-GRP-002).
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let relativePath = ".ros/work/groups.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    /// The raw store for validation; `None` when no group was ever recorded.
    let parse (root: string) : Result<WorkGroupJson.StoreRead, string> option =
        let file = path root

        if File.Exists file then Some(WorkGroupJson.parseStore (File.ReadAllText(file, Encoding.UTF8)))
        else None

    /// Every stored group; no file means none.
    let read (root: string) : Result<StoredWorkGroup list, string> =
        let file = path root

        if not (File.Exists file) then
            Ok []
        else
            try
                WorkGroupJson.readStore (File.ReadAllText(file, Encoding.UTF8)) |> Result.mapError (fun message -> $"{relativePath}: {message}")
            with error ->
                Error $"cannot read {relativePath}: {error.Message}"

    let private writeAtomic (file: string) (content: string) : Result<unit, string> =
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, content, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            try
                if File.Exists temporary then File.Delete temporary
            with _ ->
                ()

            Error $"cannot write {relativePath}: {error.Message}"

    let write (root: string) (groups: StoredWorkGroup list) : Result<unit, string> =
        writeAtomic (path root) (WorkGroupJson.renderStore groups)

    /// Reads, decides and (unless `dryRun`) writes under the `work-groups`
    /// lock, so concurrent group commands never lose each other's changes.
    /// `decide` returns the new store and a command-specific outcome.
    let transact (root: string) (dryRun: bool) (decide: StoredWorkGroup list -> Result<StoredWorkGroup list * 'outcome, 'rejection>) : Result<Result<'outcome, 'rejection>, string> =
        match RegistryLock.acquire root "work-groups" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                read root
                |> Result.bind (fun groups ->
                    match decide groups with
                    | Error rejection -> Ok(Error rejection)
                    | Ok(_, outcome) when dryRun -> Ok(Ok outcome)
                    | Ok(updated, outcome) -> write root updated |> Result.map (fun () -> Ok outcome))

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value
