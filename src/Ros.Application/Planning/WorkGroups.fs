namespace Ros.Application.Planning

open Ros.Domain.Planning

/// What `work group create`, `add` and `remove` read and write. The only
/// write replaces the stored groups; no member changes a work item's
/// lifecycle, queue entry, live context, evidence or telemetry (PRX-GRP-002).
type WorkGroupPort =
    { Stored: unit -> Result<StoredGroup list, string>
      /// Each known work item's effective lifecycle state.
      Lifecycle: unit -> Result<Map<string, string>, string>
      /// The repository planned, where a group without a declared execution
      /// repository executes.
      Repository: unit -> string
      /// Where each work item executes, as the planner decides it.
      Locate: unit -> Result<string -> ExecutionLocation, string>
      Write: StoredGroup list -> Result<unit, string> }

[<RequireQualifiedAccess>]
type GroupCreationOutcome =
    | Rejected of GroupCreationRejection list
    | Planned of StoredGroup
    | Recorded of StoredGroup

[<RequireQualifiedAccess>]
type MemberAdditionOutcome =
    | Rejected of MemberAdditionRejection list
    | Planned of StoredGroup
    | Recorded of StoredGroup

[<RequireQualifiedAccess>]
type MemberRemovalOutcome =
    | Rejected of MemberRemovalRejection list
    | Planned of StoredGroup
    | Recorded of StoredGroup

[<RequireQualifiedAccess>]
module WorkGroupOperations =
    /// Decides, and unless `dryRun` records, one human-declared group.
    let create (port: WorkGroupPort) (dryRun: bool) (request: GroupCreationRequest) : Result<GroupCreationOutcome, string> =
        port.Stored()
        |> Result.bind (fun stored ->
            port.Lifecycle()
            |> Result.bind (fun lifecycle ->
                let existing = stored |> List.map (fun group -> group.Declaration.Id) |> Set.ofList

                match GroupDeclaration.decide existing lifecycle request with
                | Error rejections -> Ok(GroupCreationOutcome.Rejected rejections)
                | Ok group when dryRun -> Ok(GroupCreationOutcome.Planned group)
                | Ok group -> GroupDeclaration.add stored group |> port.Write |> Result.map (fun () -> GroupCreationOutcome.Recorded group)))

    /// Decides, and unless `dryRun` records, one member joining a stored group.
    let add (port: WorkGroupPort) (dryRun: bool) (request: MemberAdditionRequest) : Result<MemberAdditionOutcome, string> =
        port.Stored()
        |> Result.bind (fun stored ->
            port.Lifecycle()
            |> Result.bind (fun lifecycle ->
                port.Locate()
                |> Result.bind (fun locate ->
                    match GroupDeclaration.decideAddition stored lifecycle locate (port.Repository()) request with
                    | Error rejections -> Ok(MemberAdditionOutcome.Rejected rejections)
                    | Ok group when dryRun -> Ok(MemberAdditionOutcome.Planned group)
                    | Ok group -> GroupDeclaration.replace stored group |> port.Write |> Result.map (fun () -> MemberAdditionOutcome.Recorded group))))

    /// Decides, and unless `dryRun` records, one member leaving a stored
    /// group. Only the stored groups are read and written.
    let remove (port: WorkGroupPort) (dryRun: bool) (request: MemberRemovalRequest) : Result<MemberRemovalOutcome, string> =
        port.Stored()
        |> Result.bind (fun stored ->
            match GroupDeclaration.decideRemoval stored request with
            | Error rejections -> Ok(MemberRemovalOutcome.Rejected rejections)
            | Ok group when dryRun -> Ok(MemberRemovalOutcome.Planned group)
            | Ok group -> GroupDeclaration.replace stored group |> port.Write |> Result.map (fun () -> MemberRemovalOutcome.Recorded group))

/// What `work group checkpoint` reads and writes. The only write appends to
/// the group checkpoints; members' own checkpoints, lifecycle, attribution
/// and telemetry are read, never written (PRX-GRP-043, PRX-GRP-044).
type GroupCheckpointPort =
    { Stored: unit -> Result<StoredGroup list, string>
      Lifecycle: unit -> Result<Map<string, string>, string>
      /// Each work item's own latest durable checkpoint.
      MemberCheckpoints: unit -> Result<Map<string, MemberCheckpointReference>, string>
      /// Whether one of each named work item's active executions is this
      /// process's own.
      Executions: string list -> Map<string, Ros.Domain.Work.ExecutionObservation>
      History: unit -> Result<GroupCheckpoint list, string>
      /// `work checkpoint`'s durability verdict on HEAD now, for a repository.
      Durability: string -> Result<Ros.Domain.Work.GitDurableLocation, Ros.Domain.Work.CheckpointRejection list>
      Write: GroupCheckpoint list -> Result<unit, string> }

[<RequireQualifiedAccess>]
type GroupCheckpointOutcome =
    | Rejected of GroupCheckpointRejection list
    | Planned of GroupCheckpoint
    | Recorded of GroupCheckpoint

[<RequireQualifiedAccess>]
module GroupCheckpointOperations =
    let private observe (port: GroupCheckpointPort) (request: GroupCheckpointRequest) =
        port.Stored()
        |> Result.bind (fun stored ->
            port.Lifecycle()
            |> Result.bind (fun lifecycle ->
                port.MemberCheckpoints()
                |> Result.bind (fun memberCheckpoints ->
                    port.History()
                    |> Result.map (fun history ->
                        let active =
                            stored
                            |> List.tryFind (fun group -> group.Declaration.Id = request.GroupId)
                            |> Option.map (fun group -> (GroupCheckpoints.progress lifecycle group.Declaration.Members).Active)
                            |> Option.defaultValue []

                        history,
                        ({ Stored = stored
                           Lifecycle = lifecycle
                           MemberCheckpoints = memberCheckpoints
                           Executions = port.Executions active
                           History = history
                           Durability = port.Durability request.Repository }
                        : GroupCheckpointObservations)))))

    /// Decides, and unless `dryRun` records, one group checkpoint.
    let checkpoint (port: GroupCheckpointPort) (dryRun: bool) (request: GroupCheckpointRequest) : Result<GroupCheckpointOutcome, string> =
        observe port request
        |> Result.bind (fun (history, observations) ->
            match GroupCheckpoints.decide observations request with
            | Error rejections -> Ok(GroupCheckpointOutcome.Rejected rejections)
            | Ok checkpoint when dryRun -> Ok(GroupCheckpointOutcome.Planned checkpoint)
            | Ok checkpoint -> history @ [ checkpoint ] |> port.Write |> Result.map (fun () -> GroupCheckpointOutcome.Recorded checkpoint))
