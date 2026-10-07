namespace Ros.Application.Planning

open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work

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

type GroupAddRequest =
    { GroupId: string
      Member: string
      OccurredAt: string
      Actor: Actor option
      DryRun: bool }

/// The member's own recorded state and where it executes, as checked.
type AddedMember =
    { Change: MembershipChange
      State: string
      ExecutionRepository: string }

[<RequireQualifiedAccess>]
type GroupAddOutcome =
    | Rejected of GroupRejection list
    /// Valid; nothing written (`--dry-run`).
    | Planned of group: StoredGroup * added: AddedMember
    | Recorded of group: StoredGroup * added: AddedMember

type GroupRemoveRequest =
    { GroupId: string
      Member: string
      OccurredAt: string
      Actor: Actor option
      DryRun: bool }

/// The removal as recorded and the item's own recorded state, which the
/// removal leaves as it was (`unknown` when the item is in neither the
/// backlog nor the live context).
type RemovedMember =
    { Change: MembershipChange
      State: string }

[<RequireQualifiedAccess>]
type GroupRemoveOutcome =
    | Rejected of GroupRejection list
    /// Valid; nothing written (`--dry-run`).
    | Planned of group: StoredGroup * removed: RemovedMember
    | Recorded of group: StoredGroup * removed: RemovedMember

type GroupCheckpointRequest =
    { GroupId: string
      /// The repository identity (`ros.json` `repository.id`).
      Repository: string
      Summary: string
      NextAction: string
      Decisions: string list
      OccurredAt: string
      Actor: Actor option
      DryRun: bool }

[<RequireQualifiedAccess>]
type GroupCheckpointOutcome =
    | Rejected of GroupRejection list
    /// `work checkpoint`'s own durability verification refused it.
    | NotDurable of CheckpointRejection list
    /// Verified; nothing written (`--dry-run`).
    | Planned of group: StoredGroup * checkpoint: GroupCheckpoint
    | Recorded of group: StoredGroup * checkpoint: GroupCheckpoint

[<RequireQualifiedAccess>]
type GroupShowOutcome =
    | NotFound of id: string
    | Shown of view: GroupView * snapshot: PlanSnapshot

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

    /// `work group add`: adds one member to a stored group and records who
    /// added it. Member states come from the same backlog and live context
    /// the planner reads; execution repositories from the planner's own
    /// rule (`Grouping.executionLocation`) over its configuration. The only
    /// write is the group store.
    let add (port: WorkGroupPort) (planning: PlanningReadPort) (plannedAt: string) (plannerVersion: string) (request: GroupAddRequest) : Result<GroupAddOutcome, string> =
        port.ReadStore()
        |> Result.bind (fun store ->
            PlanningOperations.gather planning plannedAt plannerVersion
            |> Result.bind (fun input ->
                let known = GroupDeclaration.memberStates input.Queue input.Live
                let locate = Grouping.executionLocation input >> fst

                match GroupDeclaration.addMember store known locate request.OccurredAt request.Actor request.GroupId request.Member with
                | Error rejections -> Ok(GroupAddOutcome.Rejected rejections)
                | Ok(updated, stored, change) ->
                    let added: AddedMember =
                        { Change = change
                          State = known |> Map.find change.Member |> stateCode
                          ExecutionRepository = locate change.Member |> ExecutionLocation.describe }

                    if request.DryRun then
                        Ok(GroupAddOutcome.Planned(stored, added))
                    else
                        port.WriteStore updated |> Result.map (fun () -> GroupAddOutcome.Recorded(stored, added))))

    /// `work group remove`: removes one member from a stored group and records
    /// who removed it. No repository check applies, so the planner is not
    /// consulted; member states are read only to report the item's own state.
    /// The only write is the group store.
    let remove (port: WorkGroupPort) (request: GroupRemoveRequest) : Result<GroupRemoveOutcome, string> =
        port.ReadStore()
        |> Result.bind (fun store ->
            states port
            |> Result.bind (fun known ->
                match GroupDeclaration.removeMember store request.OccurredAt request.Actor request.GroupId request.Member with
                | Error rejections -> Ok(GroupRemoveOutcome.Rejected rejections)
                | Ok(updated, stored, change) ->
                    let removed: RemovedMember =
                        { Change = change
                          State = known |> Map.tryFind change.Member |> Option.map stateCode |> Option.defaultValue "unknown" }

                    if request.DryRun then
                        Ok(GroupRemoveOutcome.Planned(stored, removed))
                    else
                        port.WriteStore updated |> Result.map (fun () -> GroupRemoveOutcome.Recorded(stored, removed))))

    /// Each item's own latest verified checkpoint, as a reference.
    let private reference (checkpoint: CheckpointSummary) : MemberCheckpointReference =
        { CheckpointId = checkpoint.CheckpointId
          ExecutionId = checkpoint.ExecutionId
          Commit = checkpoint.Commit
          RecordedAt = checkpoint.RecordedAt }

    let private memberCheckpoints (live: PlanningLiveItem list) =
        live
        |> List.choose (fun item ->
            item.Checkpoint
            |> Option.filter (fun checkpoint -> checkpoint.Verified)
            |> Option.map (fun checkpoint -> item.Id, reference checkpoint))
        |> Map.ofList

    /// `work group checkpoint` (PRX-GRP-044): verifies durability with
    /// `verify` (`work checkpoint`'s own rules: `CheckpointOperations
    /// .verifyDurableLocation`), then appends a group checkpoint that
    /// references each member's own latest checkpoint. An unknown group is
    /// refused before Git is consulted. The only write is the group store.
    let checkpoint
        (port: WorkGroupPort)
        (verify: CheckpointCandidate -> Result<GitDurableLocation, CheckpointRejection list>)
        (request: GroupCheckpointRequest)
        : Result<GroupCheckpointOutcome, string> =
        port.ReadStore()
        |> Result.bind (fun store ->
            match GroupStore.tryFind request.GroupId store with
            | None -> Ok(GroupCheckpointOutcome.Rejected [ GroupRejection.UnknownGroup request.GroupId ])
            | Some _ ->
                let candidate: CheckpointCandidate =
                    { WorkItemId = request.GroupId
                      Repository = request.Repository
                      Summary = request.Summary
                      NextAction = request.NextAction
                      StepId = None
                      OccurredAt = request.OccurredAt }

                match verify candidate with
                | Error rejections -> Ok(GroupCheckpointOutcome.NotDurable rejections)
                | Ok location ->
                    port.Queue()
                    |> Result.bind (fun queue ->
                        port.Live()
                        |> Result.bind (fun live ->
                            let known = GroupDeclaration.memberStates queue live

                            match
                                GroupDeclaration.recordCheckpoint
                                    store
                                    known
                                    (memberCheckpoints live)
                                    request.OccurredAt
                                    request.Actor
                                    request.Summary
                                    request.NextAction
                                    request.Decisions
                                    location
                                    request.GroupId
                            with
                            | Error rejections -> Ok(GroupCheckpointOutcome.Rejected rejections)
                            | Ok(updated, stored, recorded) ->
                                if request.DryRun then
                                    Ok(GroupCheckpointOutcome.Planned(stored, recorded))
                                else
                                    port.WriteStore updated |> Result.map (fun () -> GroupCheckpointOutcome.Recorded(stored, recorded)))))

    /// `work group show`: the stored group, each member's own recorded state
    /// and the planner's view of the same group. Reads only: neither port's
    /// write is called (the planning port has none).
    let show (port: WorkGroupPort) (planning: PlanningReadPort) (plannedAt: string) (plannerVersion: string) (id: string) : Result<GroupShowOutcome, string> =
        port.ReadStore()
        |> Result.bind (fun store ->
            match GroupStore.tryFind id store with
            | None -> Ok(GroupShowOutcome.NotFound id)
            | Some _ ->
                PlanningOperations.analyze planning plannedAt plannerVersion
                |> Result.map (fun (input, analysis) ->
                    let states = GroupDeclaration.memberStates input.Queue input.Live
                    let report = Grouping.recommend input analysis

                    match GroupView.tryShow store states report id with
                    | Some view -> GroupShowOutcome.Shown(view, analysis.Snapshot)
                    | None -> GroupShowOutcome.NotFound id))

    /// Stored-group findings for `validate`.
    let findings (port: WorkGroupPort) : Result<GroupFinding list, string> =
        port.ReadStore() |> Result.bind (fun store -> states port |> Result.map (GroupDeclaration.findings store))
