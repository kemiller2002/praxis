namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open Ros.Application.Planning
open Ros.Application.Work
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Telemetry
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// `work group checkpoint` and `validate` over
/// `.ros/work/group-checkpoints.json` (PRX-GRP-044). Durability is decided
/// by `work checkpoint`'s own verification over the same Git port and
/// continuity policy; members' checkpoints are read from their own
/// `latestCheckpoint` projections and never written.
[<RequireQualifiedAccess>]
module FileGroupCheckpointRepository =
    let relativePath = ".ros/work/group-checkpoints.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "group-checkpoints.json")

    /// An absent file means no group has been checkpointed.
    let read (root: string) : Result<GroupCheckpoint list, string> =
        let file = path root

        if File.Exists file then
            PlanningJson.parseGroupCheckpoints (File.ReadAllText file) |> Result.mapError (fun message -> $"{relativePath}: {message}")
        else
            Ok []

    let private writeAtomic (root: string) (checkpoints: GroupCheckpoint list) : Result<unit, string> =
        let file = path root
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, PlanningJson.renderGroupCheckpoints checkpoints, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            if File.Exists temporary then File.Delete temporary
            Error $"could not write {relativePath}: {error.Message}"

    /// Each work item's own latest durable checkpoint, as referenced.
    let memberCheckpoints (root: string) : Result<Map<string, MemberCheckpointReference>, string> =
        FileCheckpointRepository.readItems root
        |> Result.map (fun items ->
            items
            |> List.choose (fun item ->
                match item.LatestCheckpoint with
                | Ok(Some recorded) ->
                    Some(
                        item.WorkItemId,
                        { Member = item.WorkItemId
                          CheckpointId = recorded.CheckpointId
                          ExecutionId = recorded.Recorded.ExecutionId
                          Commit = recorded.Recorded.Commit.Value
                          RecordedAt = recorded.Recorded.RecordedAt }
                    )
                | _ -> None)
            |> Map.ofList)

    let port (root: string) (overrides: IdentityInputs) : GroupCheckpointPort =
        { Stored = fun () -> FileWorkGroupStore.read root
          Lifecycle = fun () -> FileWorkGroupRepository.lifecycle root
          MemberCheckpoints = fun () -> memberCheckpoints root
          Executions =
            fun ids -> ids |> List.map (fun id -> id, FileCheckpointRepository.resolveExecution root id overrides None) |> Map.ofList
          History = fun () -> read root
          Durability =
            fun repository ->
                CheckpointOperations.verifyLocation (ProcessGitDurability.create root) (FileCheckpointRepository.readPolicy root) repository
          Write = writeAtomic root }

    let checkpoint (root: string) (overrides: IdentityInputs) (dryRun: bool) (request: GroupCheckpointRequest) =
        GroupCheckpointOperations.checkpoint (port root overrides) dryRun request

    /// `validate` findings as (path, field, message).
    let validationFindings (root: string) : (string * string * string) list =
        match read root, FileWorkGroupStore.read root with
        | Error message, _ -> [ relativePath, "checkpoints", message ]
        | Ok [], _ -> []
        | Ok _, Error message -> [ relativePath, "groupId", $"stored groups could not be read: {message}" ]
        | Ok stored, Ok groups ->
            let owners =
                FileCheckpointRepository.readEvents root
                |> List.map (fun (_, event) -> event.EventId, event.WorkItemId)
                |> List.filter (fun (id, _) -> not (String.IsNullOrEmpty id))
                |> Map.ofList

            GroupCheckpoints.findings (groups |> List.map (fun group -> group.Declaration.Id) |> Set.ofList) owners stored
            |> List.map (fun finding -> relativePath, $"checkpoints[{finding.CheckpointId}].{finding.Field}", finding.Message)
