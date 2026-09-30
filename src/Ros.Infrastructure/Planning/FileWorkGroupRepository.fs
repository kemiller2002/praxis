namespace Ros.Infrastructure.Planning

open Ros.Domain.Planning
open Ros.Domain.Provenance

type WorkGroupDeclarationRequest =
    { Group: DeclaredGroup
      OccurredAt: string
      Actor: Actor
      DryRun: bool }

[<RequireQualifiedAccess>]
type WorkGroupDeclarationOutcome =
    | Recorded of StoredGroupDeclaration
    | Planned of StoredGroupDeclaration
    | Rejected of StoredGroupDeclaration * GroupDeclarationRejection list

/// `work group create` and the `validate` check over stored groups. Member
/// standing is read from the backlog and live work through the planner's own
/// read-only queries; only `.ros/work/groups.json` is ever written.
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let private standingOf (root: string) : Result<string -> MemberStanding, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue -> FilePlanningRepository.readLive root |> Result.map (fun live -> GroupDeclaration.standing queue live))

    let declare (root: string) (request: WorkGroupDeclarationRequest) : Result<WorkGroupDeclarationOutcome, string> =
        let entry =
            { Group = request.Group
              DeclaredAt = request.OccurredAt
              DeclaredBy = request.Actor }

        WorkGroupStore.read root
        |> Result.bind (fun stored ->
            standingOf root
            |> Result.bind (fun standing ->
                match GroupDeclaration.decide (stored |> List.map (fun existing -> existing.Group)) standing request.Group with
                | Error rejections -> Ok(WorkGroupDeclarationOutcome.Rejected(entry, rejections))
                | Ok _ when request.DryRun -> Ok(WorkGroupDeclarationOutcome.Planned entry)
                | Ok _ -> WorkGroupStore.write root (stored @ [ entry ]) |> Result.map (fun () -> WorkGroupDeclarationOutcome.Recorded entry)))

    /// `(path, field, message)` findings for `validate`.
    let validationFindings (root: string) : (string * string * string) list =
        match WorkGroupStore.read root with
        | Error message -> [ WorkGroupStore.RelativePath, "groups", message ]
        | Ok [] -> []
        | Ok stored ->
            match standingOf root with
            | Error message -> [ WorkGroupStore.RelativePath, "groups", message ]
            | Ok standing ->
                GroupDeclaration.findings standing stored
                |> List.map (fun (field, message) -> WorkGroupStore.RelativePath, field, message)
