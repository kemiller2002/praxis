namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Infrastructure.Artifacts

[<RequireQualifiedAccess>]
type GroupCreateFailure =
    | Rejected of GroupRejection list
    | Failed of message: string

/// The only writer of `.ros/work/groups.json`. It reads the backlog and the
/// live context to decide membership and writes nothing but the group store,
/// so no member's lifecycle state can change (PRX-GRP-002).
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let private writeAtomic (file: string) (content: string) =
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, content, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            (try
                if File.Exists temporary then File.Delete temporary
             with _ ->
                 ())

            Error $"cannot write {file}: {error.Message}"

    /// Every recorded work item's membership status.
    let memberStatuses (root: string) : Result<Map<string, GroupMemberStatus>, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue -> FilePlanningRepository.readLive root |> Result.map (GroupDeclaration.memberStatuses queue))

    let private decide (root: string) (request: GroupCreateRequest) =
        FilePlanningRepository.readStoredGroups root
        |> Result.bind (fun existing -> memberStatuses root |> Result.map (fun statuses -> existing, statuses))
        |> Result.mapError GroupCreateFailure.Failed
        |> Result.bind (fun (existing, statuses) ->
            GroupDeclaration.create existing statuses request
            |> Result.mapError GroupCreateFailure.Rejected
            |> Result.map (fun created -> existing, created))

    let private persist (root: string) (existing: StoredGroup list, created: StoredGroup) =
        writeAtomic (FilePlanningRepository.groupStorePath root) (PlanningJson.renderGroupStore (existing @ [ created ]))
        |> Result.mapError GroupCreateFailure.Failed
        |> Result.map (fun () -> created)

    /// Takes a decision and, unless `dryRun`, persists it under the
    /// work-protocol lock. A dry run takes the same decision and writes nothing.
    let private apply (root: string) (dryRun: bool) (decision: unit -> Result<'a * StoredGroup, GroupCreateFailure>) (store: 'a * StoredGroup -> Result<StoredGroup, GroupCreateFailure>) =
        if dryRun then
            decision () |> Result.map snd
        else
            match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
            | Error failure -> Error(GroupCreateFailure.Failed failure.Message)
            | Ok lease ->
                try
                    decision () |> Result.bind store
                finally
                    lease.Release() |> ignore

    /// Decides and, unless `dryRun`, stores a new declaration.
    let create (root: string) (dryRun: bool) (request: GroupCreateRequest) : Result<StoredGroup, GroupCreateFailure> =
        apply root dryRun (fun () -> decide root request) (persist root)

    /// Names each item's execution repository exactly as the planner does
    /// without an explicit `--config` (PRX-GRP-051).
    let private locator (root: string) : Result<string -> string, string> =
        FilePlanningRepository.readQueue root
        |> Result.map (fun queue ->
            let repository = (FilePlanningRepository.readRepository root).Name
            let grouping = PlannerConfiguration.defaults.Grouping
            Grouping.locate grouping repository queue >> fst >> ExecutionLocation.describe)

    let private decideAdd (root: string) (request: GroupAddRequest) =
        FilePlanningRepository.readStoredGroups root
        |> Result.bind (fun existing -> memberStatuses root |> Result.map (fun statuses -> existing, statuses))
        |> Result.bind (fun (existing, statuses) -> locator root |> Result.map (fun locate -> existing, statuses, locate))
        |> Result.mapError GroupCreateFailure.Failed
        |> Result.bind (fun (existing, statuses, locate) ->
            GroupDeclaration.add existing statuses locate request
            |> Result.mapError GroupCreateFailure.Rejected
            |> Result.map (fun updated -> existing, updated))

    let private persistUpdate (root: string) (existing: StoredGroup list, updated: StoredGroup) =
        let replaced = existing |> List.map (fun stored -> if stored.Group.Id = updated.Group.Id then updated else stored)

        writeAtomic (FilePlanningRepository.groupStorePath root) (PlanningJson.renderGroupStore replaced)
        |> Result.mapError GroupCreateFailure.Failed
        |> Result.map (fun () -> updated)

    /// Decides and, unless `dryRun`, stores one added member. Only the group
    /// store is written.
    let add (root: string) (dryRun: bool) (request: GroupAddRequest) : Result<StoredGroup, GroupCreateFailure> =
        apply root dryRun (fun () -> decideAdd root request) (persistUpdate root)

    let private decideRemove (root: string) (request: GroupRemoveRequest) =
        FilePlanningRepository.readStoredGroups root
        |> Result.mapError GroupCreateFailure.Failed
        |> Result.bind (fun existing ->
            GroupDeclaration.remove existing request
            |> Result.mapError GroupCreateFailure.Rejected
            |> Result.map (fun updated -> existing, updated))

    /// Decides and, unless `dryRun`, stores one removed member. Only the
    /// group store is written: the item's queue entry, context record,
    /// evidence and attribution are never read for writing.
    let remove (root: string) (dryRun: bool) (request: GroupRemoveRequest) : Result<StoredGroup, GroupCreateFailure> =
        apply root dryRun (fun () -> decideRemove root request) (persistUpdate root)

    /// `validate` findings for the store: (path, field, message).
    let findings (root: string) : (string * string * string) list =
        let path = ".ros/work/groups.json"

        match FilePlanningRepository.readStoredGroups root, memberStatuses root with
        | Error message, _ -> [ path, "groups", message ]
        | Ok [], _ -> []
        | _, Error message -> [ path, "groups", message ]
        | Ok stored, Ok statuses ->
            GroupDeclaration.findings stored statuses
            |> List.map (fun (id, message) -> path, $"groups[{id}]", message)
