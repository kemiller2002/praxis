namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts

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

    /// The whole store; no file means no groups and no references.
    let readStore (root: string) : Result<GroupStore, string> =
        let file = path root

        if not (File.Exists file) then
            Ok { Groups = []; References = [] }
        else
            try
                WorkGroupJson.readGroupStore (File.ReadAllText(file, Encoding.UTF8)) |> Result.mapError (fun message -> $"{relativePath}: {message}")
            with error ->
                Error $"cannot read {relativePath}: {error.Message}"

    /// Every stored group; no file means none.
    let read (root: string) : Result<StoredWorkGroup list, string> = readStore root |> Result.map (fun store -> store.Groups)

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

    let writeStore (root: string) (store: GroupStore) : Result<unit, string> =
        writeAtomic (path root) (WorkGroupJson.renderGroupStore store)

    /// Reads, decides and (unless `dryRun`, or the store is unchanged)
    /// writes under the `work-groups` lock, so concurrent group commands
    /// never lose each other's changes. `decide` returns the new store and
    /// a command-specific outcome.
    let private transactStoreLocked (root: string) (dryRun: bool) (decide: GroupStore -> Result<GroupStore * 'outcome, 'rejection>) : Result<Result<'outcome, 'rejection>, string> =
        match RegistryLock.acquire root "work-groups" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                readStore root
                |> Result.bind (fun store ->
                    match decide store with
                    | Error rejection -> Ok(Error rejection)
                    | Ok(_, outcome) when dryRun -> Ok(Ok outcome)
                    | Ok(updated, outcome) when updated = store -> Ok(Ok outcome)
                    | Ok(updated, outcome) -> writeStore root updated |> Result.map (fun () -> Ok outcome))

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    /// Shared lock order with member transitions: work-protocol, then
    /// work-groups. A member cannot start while ECIR membership is changing.
    let transactStore (root: string) (dryRun: bool) (decide: GroupStore -> Result<GroupStore * 'outcome, 'rejection>) : Result<Result<'outcome, 'rejection>, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try transactStoreLocked root dryRun decide
                with _ ->
                    lease.Release() |> ignore
                    reraise()
            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    /// `transactStore` over the groups alone; references are kept as read.
    let transact (root: string) (dryRun: bool) (decide: StoredWorkGroup list -> Result<StoredWorkGroup list * 'outcome, 'rejection>) : Result<Result<'outcome, 'rejection>, string> =
        transactStore root dryRun (fun store -> decide store.Groups |> Result.map (fun (groups, outcome) -> { store with Groups = groups }, outcome))
