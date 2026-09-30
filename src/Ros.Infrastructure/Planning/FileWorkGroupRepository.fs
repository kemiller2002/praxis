namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Work

/// `work group create` and `validate` over `.ros/work/groups.json`. Member
/// lifecycle is read with the planner's own read-only queue and context
/// readers and the authority `work list` uses
/// (`QueuePresentation.effectiveStatus`); it is never written.
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let lifecycle (root: string) : Result<Map<string, string>, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue ->
            FilePlanningRepository.readLive root
            |> Result.map (fun live ->
                let queued = queue |> List.map (fun item -> item.Id, item.Status) |> Map.ofList
                let current = live |> List.map (fun item -> item.Id, item.State) |> Map.ofList

                (Map.keys queued |> Seq.toList) @ (Map.keys current |> Seq.toList)
                |> List.distinct
                |> List.map (fun id ->
                    id, QueuePresentation.effectiveStatus (queued.TryFind id) (current.TryFind id) |> Option.defaultValue "")
                |> Map.ofList))

    let private writeAtomic (root: string) (groups: StoredGroup list) : Result<unit, string> =
        let file = FileWorkGroupStore.path root
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, PlanningJson.renderStoredGroups groups, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            if File.Exists temporary then File.Delete temporary
            Error $"could not write {FileWorkGroupStore.relativePath}: {error.Message}"

    let port (root: string) : WorkGroupPort =
        { Stored = fun () -> FileWorkGroupStore.read root
          Lifecycle = fun () -> lifecycle root
          Write = writeAtomic root }

    let create (root: string) (dryRun: bool) (request: GroupCreationRequest) =
        WorkGroupOperations.create (port root) dryRun request

    /// `validate` findings as (path, field, message).
    let validationFindings (root: string) : (string * string * string) list =
        match FileWorkGroupStore.read root, lifecycle root with
        | Error message, _ -> [ FileWorkGroupStore.relativePath, "groups", message ]
        | Ok [], _ -> []
        | Ok _, Error message -> [ FileWorkGroupStore.relativePath, "members", $"work item states could not be read: {message}" ]
        | Ok stored, Ok states ->
            GroupDeclaration.findings stored (states |> Map.keys |> Set.ofSeq)
            |> List.map (fun finding -> FileWorkGroupStore.relativePath, $"groups[{finding.GroupId}].{finding.Field}", finding.Message)
