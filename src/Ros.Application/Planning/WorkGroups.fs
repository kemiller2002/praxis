namespace Ros.Application.Planning

open Ros.Domain.Planning

/// What `work group create` reads and writes. The only write replaces the
/// stored groups; no member changes a work item's lifecycle, queue entry,
/// live context, evidence or telemetry (PRX-GRP-002).
type WorkGroupPort =
    { Stored: unit -> Result<StoredGroup list, string>
      /// Each known work item's effective lifecycle state.
      Lifecycle: unit -> Result<Map<string, string>, string>
      Write: StoredGroup list -> Result<unit, string> }

[<RequireQualifiedAccess>]
type GroupCreationOutcome =
    | Rejected of GroupCreationRejection list
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
