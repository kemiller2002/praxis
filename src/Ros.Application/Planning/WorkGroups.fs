namespace Ros.Application.Planning

open Ros.Domain.Planning
open Ros.Domain.Provenance

/// What `work group` commands read and write. The only write is the group
/// store: no member's queue entry, live context, events, evidence or
/// telemetry is reachable through this port (PRX-GRP-002).
type WorkGroupPort =
    { ReadStore: unit -> Result<GroupStore, string>
      Queue: unit -> Result<PlanningQueueItem list, string>
      Live: unit -> Result<PlanningLiveItem list, string>
      WriteStore: GroupStore -> Result<unit, string> }

type GroupCreateRequest =
    { Declaration: DeclaredGroup
      OccurredAt: string
      Actor: Actor option
      DryRun: bool }

[<RequireQualifiedAccess>]
type GroupCreateOutcome =
    | Rejected of GroupRejection list
    /// Valid; nothing written (`--dry-run`).
    | Planned of group: StoredGroup * members: (string * string) list
    | Recorded of group: StoredGroup * members: (string * string) list

[<RequireQualifiedAccess>]
module WorkGroupOperations =
    let private states (port: WorkGroupPort) =
        port.Queue() |> Result.bind (fun queue -> port.Live() |> Result.map (GroupDeclaration.memberStates queue))

    let private stateCode state =
        match state with
        | MemberState.Open code
        | MemberState.Terminal code -> code

    let create (port: WorkGroupPort) (request: GroupCreateRequest) : Result<GroupCreateOutcome, string> =
        port.ReadStore()
        |> Result.bind (fun store ->
            states port
            |> Result.bind (fun known ->
                match GroupDeclaration.create store known request.OccurredAt request.Actor request.Declaration with
                | Error rejections -> Ok(GroupCreateOutcome.Rejected rejections)
                | Ok(updated, stored) ->
                    let members = stored.Declaration.Members |> List.map (fun id -> id, known |> Map.find id |> stateCode)

                    if request.DryRun then
                        Ok(GroupCreateOutcome.Planned(stored, members))
                    else
                        port.WriteStore updated |> Result.map (fun () -> GroupCreateOutcome.Recorded(stored, members))))

    /// Stored-group findings for `validate`.
    let findings (port: WorkGroupPort) : Result<GroupFinding list, string> =
        port.ReadStore() |> Result.bind (fun store -> states port |> Result.map (GroupDeclaration.findings store))
