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

type WorkGroupAdditionRequest =
    { Addition: MemberAdditionRequest
      OccurredAt: string
      Actor: Actor
      /// Planner configuration supplying `grouping.executionRepositories`.
      ConfigurationFile: string option
      DryRun: bool }

/// Each case carries the attempted addition; `Added` and `Planned` also the
/// updated declaration, `Rejected` the group's unchanged members.
[<RequireQualifiedAccess>]
type WorkGroupAdditionOutcome =
    | Added of MemberAddition * StoredGroupDeclaration
    | Planned of MemberAddition * StoredGroupDeclaration
    | Rejected of MemberAddition * string list * GroupAdditionRejection list

/// `work group create`, `work group add` and the `validate` check over stored groups. Member
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

    let private additionContext (root: string) (configurationFile: string option) (stored: StoredGroupDeclaration list) =
        let repository = FilePlanningRepository.create root None configurationFile

        repository.Queue()
        |> Result.bind (fun queue ->
            repository.Live()
            |> Result.bind (fun live ->
                repository.Configuration()
                |> Result.map (fun configuration ->
                    let name = repository.Repository().Name

                    { Stored = stored
                      Standing = GroupDeclaration.standing queue live
                      Location = Grouping.executionLocation configuration.Grouping name queue >> fst
                      Repository = name })))

    /// Writes the ledger, then the group; if the group cannot be written the
    /// ledger is put back, so neither file claims an addition the other lacks.
    let private record root stored ledger (updated: StoredGroupDeclaration) (addition: MemberAddition) =
        let previous = GroupMembershipStore.snapshot root

        GroupMembershipStore.write root (ledger @ [ addition ])
        |> Result.bind (fun () ->
            match WorkGroupStore.write root (GroupMembership.replace updated stored) with
            | Ok() -> Ok()
            | Error message ->
                match GroupMembershipStore.restore root previous with
                | Ok() -> Error message
                | Error restoreMessage -> Error $"{message}; additionally {restoreMessage}")

    /// `work group add`: adds one item to a declared group and records who
    /// added it. Writes only `.ros/work/groups.json` and
    /// `.ros/work/group-membership.json`; the member's lifecycle is untouched.
    let add (root: string) (request: WorkGroupAdditionRequest) : Result<WorkGroupAdditionOutcome, string> =
        let addition: MemberAddition =
            { GroupId = request.Addition.GroupId
              WorkItem = request.Addition.WorkItem
              AddedAt = request.OccurredAt
              AddedBy = request.Actor
              Reason = request.Addition.Reason }

        WorkGroupStore.read root
        |> Result.bind (fun stored ->
            GroupMembershipStore.read root
            |> Result.bind (fun ledger ->
                additionContext root request.ConfigurationFile stored
                |> Result.bind (fun context ->
                    match GroupMembership.decide context request.Addition with
                    | Error rejections ->
                        let members =
                            stored
                            |> List.tryFind (fun entry -> entry.Group.Id = request.Addition.GroupId)
                            |> Option.map (fun entry -> entry.Group.Members)
                            |> Option.defaultValue []

                        Ok(WorkGroupAdditionOutcome.Rejected(addition, members, rejections))
                    | Ok updated when request.DryRun -> Ok(WorkGroupAdditionOutcome.Planned(addition, updated))
                    | Ok updated -> record root stored ledger updated addition |> Result.map (fun () -> WorkGroupAdditionOutcome.Added(addition, updated)))))

    /// `(path, field, message)` findings for `validate`.
    let validationFindings (root: string) : (string * string * string) list =
        let groupFindings =
            match WorkGroupStore.read root with
            | Error message -> [ WorkGroupStore.RelativePath, "groups", message ]
            | Ok [] -> []
            | Ok stored ->
                match standingOf root with
                | Error message -> [ WorkGroupStore.RelativePath, "groups", message ]
                | Ok standing ->
                    GroupDeclaration.findings standing stored
                    |> List.map (fun (field, message) -> WorkGroupStore.RelativePath, field, message)

        let membershipFindings =
            match GroupMembershipStore.read root, WorkGroupStore.read root with
            | Error message, _ -> [ GroupMembershipStore.RelativePath, "changes", message ]
            | Ok [], _ -> []
            | Ok _, Error _ -> []
            | Ok ledger, Ok stored ->
                GroupMembership.findings stored ledger
                |> List.map (fun (field, message) -> GroupMembershipStore.RelativePath, field, message)

        groupFindings @ membershipFindings
