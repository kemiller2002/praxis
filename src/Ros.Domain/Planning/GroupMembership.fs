namespace Ros.Domain.Planning

open System
open Ros.Domain.Provenance
open Ros.Domain.Work

/// A durable record that one work item joined a declared group after the
/// group was declared: who added it, when and why (PRX-GRP-073 phase two).
/// Membership changes nothing about the item itself (PRX-GRP-002).
type MemberAddition =
    { GroupId: string
      WorkItem: string
      AddedAt: string
      AddedBy: Actor
      Reason: string option }

/// A durable record that one member left a declared group: who removed it,
/// when and why. Removal changes nothing about the item itself: its
/// lifecycle state, evidence and attribution stay as recorded (PRX-GRP-002).
type MemberRemoval =
    { GroupId: string
      WorkItem: string
      RemovedAt: string
      RemovedBy: Actor
      Reason: string option }

/// One entry of the append-only membership ledger, in the order recorded.
[<RequireQualifiedAccess>]
type MembershipChange =
    | Added of MemberAddition
    | Removed of MemberRemoval

[<RequireQualifiedAccess>]
module MembershipChange =
    let groupId change =
        match change with
        | MembershipChange.Added addition -> addition.GroupId
        | MembershipChange.Removed removal -> removal.GroupId

    let workItem change =
        match change with
        | MembershipChange.Added addition -> addition.WorkItem
        | MembershipChange.Removed removal -> removal.WorkItem

[<RequireQualifiedAccess>]
type GroupAdditionRejection =
    | UndeclaredGroup of string
    | InvalidMemberId of string
    | UnknownMember of string
    | TerminalMember of workItemId: string * state: string
    | AlreadyMember of workItemId: string * groupId: string
    | RepositoryMismatch of workItemId: string * itemRepository: string * groupRepository: string
    | BlankReason

[<RequireQualifiedAccess>]
module GroupAdditionRejection =
    let code rejection =
        match rejection with
        | GroupAdditionRejection.UndeclaredGroup _ -> "undeclared-group"
        | GroupAdditionRejection.InvalidMemberId _ -> "invalid-member-id"
        | GroupAdditionRejection.UnknownMember _ -> "unknown-member"
        | GroupAdditionRejection.TerminalMember _ -> "terminal-member"
        | GroupAdditionRejection.AlreadyMember _ -> "already-member"
        | GroupAdditionRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupAdditionRejection.BlankReason -> "blank-reason"

    let message rejection =
        match rejection with
        | GroupAdditionRejection.UndeclaredGroup id -> $"group {id} is not declared (see 'work group create')"
        | GroupAdditionRejection.InvalidMemberId id -> $"'{id}' is not a valid work-item ID"
        | GroupAdditionRejection.UnknownMember id -> $"{id} is not a work item this repository tracks (neither in the backlog nor in live work)"
        | GroupAdditionRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal work item cannot join a group"
        | GroupAdditionRejection.AlreadyMember(id, group) -> $"{id} is already a member of {group}"
        | GroupAdditionRejection.RepositoryMismatch(id, item, group) ->
            $"{id} executes in {item} but the group executes in {group}; declare the group --cross-repository or use a group local to {item} (PRX-GRP-051)"
        | GroupAdditionRejection.BlankReason -> "--reason must not be blank"

[<RequireQualifiedAccess>]
type GroupRemovalRejection =
    | UndeclaredGroup of string
    | NotMember of workItemId: string * groupId: string
    | LastMember of workItemId: string * groupId: string
    | BlankReason

[<RequireQualifiedAccess>]
module GroupRemovalRejection =
    let code rejection =
        match rejection with
        | GroupRemovalRejection.UndeclaredGroup _ -> "undeclared-group"
        | GroupRemovalRejection.NotMember _ -> "not-member"
        | GroupRemovalRejection.LastMember _ -> "last-member"
        | GroupRemovalRejection.BlankReason -> "blank-reason"

    let message rejection =
        match rejection with
        | GroupRemovalRejection.UndeclaredGroup id -> $"group {id} is not declared (see 'work group create')"
        | GroupRemovalRejection.NotMember(id, group) -> $"{id} is not a member of {group}"
        | GroupRemovalRejection.LastMember(id, group) ->
            $"{id} is the last member of {group}; a group needs at least one member, so add another member first (group IDs are never reused)"
        | GroupRemovalRejection.BlankReason -> "--reason must not be blank"

/// The request `work group remove` decides: remove one member from one
/// declared group.
type MemberRemovalRequest =
    { GroupId: string
      WorkItem: string
      Reason: string option }

/// The request `work group add` decides: add one item to one declared group.
type MemberAdditionRequest =
    { GroupId: string
      WorkItem: string
      Reason: string option }

/// What the decision needs to know about the repository; every function is
/// a read, never a write.
type MemberAdditionContext =
    { Stored: StoredGroupDeclaration list
      Standing: string -> MemberStanding
      /// Where an item executes (`Grouping.executionLocation`).
      Location: string -> ExecutionLocation
      /// This checkout's repository, the execution repository of a group
      /// that declares none.
      Repository: string }

[<RequireQualifiedAccess>]
module GroupMembership =
    /// The repository a group executes in.
    let groupRepository (repository: string) (group: DeclaredGroup) =
        group.ExecutionRepository |> Option.defaultValue repository

    let private repositoryCheck (context: MemberAdditionContext) (group: DeclaredGroup) (workItem: string) =
        let expected = groupRepository context.Repository group

        match group.CrossRepository, context.Location workItem with
        | true, _ -> []
        | false, ExecutionLocation.Repository name when name = expected -> []
        | false, location -> [ GroupAdditionRejection.RepositoryMismatch(workItem, ExecutionLocation.describe location, expected) ]

    let private itemChecks (context: MemberAdditionContext) (group: DeclaredGroup) (workItem: string) =
        if not (WorkItemId.isValid workItem) then
            [ GroupAdditionRejection.InvalidMemberId workItem ]
        elif group.Members |> List.contains workItem then
            [ GroupAdditionRejection.AlreadyMember(workItem, group.Id) ]
        else
            match context.Standing workItem with
            | MemberStanding.Unknown -> [ GroupAdditionRejection.UnknownMember workItem ]
            | MemberStanding.Terminal state -> [ GroupAdditionRejection.TerminalMember(workItem, state) ]
            | MemberStanding.Open _ -> repositoryCheck context group workItem

    let private isBlank (reason: string option) =
        reason |> Option.exists String.IsNullOrWhiteSpace

    let private reasonCheck (request: MemberAdditionRequest) =
        if isBlank request.Reason then [ GroupAdditionRejection.BlankReason ] else []

    /// The stored declaration with one member appended, in declared order.
    let withMember (workItem: string) (entry: StoredGroupDeclaration) : StoredGroupDeclaration =
        { entry with Group = { entry.Group with Members = entry.Group.Members @ [ workItem ] } }

    /// Decides an addition. Every rejection is reported at once; on success
    /// the updated declaration is returned and nothing else changes: the
    /// member keeps its own lifecycle state (PRX-GRP-002).
    let decide
        (context: MemberAdditionContext)
        (request: MemberAdditionRequest)
        : Result<StoredGroupDeclaration, GroupAdditionRejection list> =
        match context.Stored |> List.tryFind (fun entry -> entry.Group.Id = request.GroupId) with
        | None -> Error(GroupAdditionRejection.UndeclaredGroup request.GroupId :: reasonCheck request)
        | Some entry ->
            match itemChecks context entry.Group request.WorkItem @ reasonCheck request with
            | [] -> Ok(withMember request.WorkItem entry)
            | rejections -> Error rejections

    /// The store with one declaration replaced in place, keeping order.
    let replace (updated: StoredGroupDeclaration) (stored: StoredGroupDeclaration list) =
        stored |> List.map (fun entry -> if entry.Group.Id = updated.Group.Id then updated else entry)

    /// The stored declaration without one member, keeping the others' order.
    let withoutMember (workItem: string) (entry: StoredGroupDeclaration) : StoredGroupDeclaration =
        { entry with Group = { entry.Group with Members = entry.Group.Members |> List.filter ((<>) workItem) } }

    /// Decides a removal. Every rejection is reported at once; on success the
    /// updated declaration is returned. Removing the last member is refused:
    /// a declared group always has at least one member. The member's own
    /// state is never consulted or changed, so a member that completed or
    /// was abandoned may leave as well (PRX-GRP-002, PRX-GRP-042).
    let decideRemoval
        (stored: StoredGroupDeclaration list)
        (request: MemberRemovalRequest)
        : Result<StoredGroupDeclaration, GroupRemovalRejection list> =
        let reason = if isBlank request.Reason then [ GroupRemovalRejection.BlankReason ] else []

        let membership (group: DeclaredGroup) =
            match group.Members with
            | members when not (members |> List.contains request.WorkItem) -> [ GroupRemovalRejection.NotMember(request.WorkItem, group.Id) ]
            | [ _ ] -> [ GroupRemovalRejection.LastMember(request.WorkItem, group.Id) ]
            | _ -> []

        match stored |> List.tryFind (fun entry -> entry.Group.Id = request.GroupId) with
        | None -> Error(GroupRemovalRejection.UndeclaredGroup request.GroupId :: reason)
        | Some entry ->
            match membership entry.Group @ reason with
            | [] -> Ok(withoutMember request.WorkItem entry)
            | rejections -> Error rejections

    let private changeFindings (index: int) (change: MembershipChange) =
        let field = $"changes[{index}]"

        match change with
        | MembershipChange.Added addition ->
            [ if String.IsNullOrWhiteSpace addition.AddedAt then
                  yield field, "addedAt is required"
              if String.IsNullOrWhiteSpace addition.AddedBy.Id then
                  yield field, "addedBy.id is required" ]
        | MembershipChange.Removed removal ->
            [ if String.IsNullOrWhiteSpace removal.RemovedAt then
                  yield field, "removedAt is required"
              if String.IsNullOrWhiteSpace removal.RemovedBy.Id then
                  yield field, "removedBy.id is required" ]

    /// Findings for the membership ledger (`validate`). Every change carries
    /// a timestamp and an actor and names a declared group; the latest
    /// change for each group and item must agree with the group: a member
    /// last added is still listed, a member last removed is not.
    let findings (stored: StoredGroupDeclaration list) (changes: MembershipChange list) : (string * string) list =
        let groups = stored |> List.map (fun entry -> entry.Group.Id, entry.Group) |> Map.ofList
        let indexed = changes |> List.indexed

        let latest =
            indexed
            |> List.filter (fun (_, change) -> groups.ContainsKey(MembershipChange.groupId change))
            |> List.groupBy (fun (_, change) -> MembershipChange.groupId change, MembershipChange.workItem change)
            |> List.map (snd >> List.last)
            |> List.sortBy fst

        let undeclared =
            indexed
            |> List.filter (fun (_, change) -> not (groups.ContainsKey(MembershipChange.groupId change)))
            |> List.map (fun (index, change) ->
                index, ($"changes[{index}]", $"records a change to {MembershipChange.groupId change}, which is not declared"))

        let disagreements =
            latest
            |> List.choose (fun (index, change) ->
                let listed = groups[MembershipChange.groupId change].Members |> List.contains (MembershipChange.workItem change)

                match change, listed with
                | MembershipChange.Added addition, false ->
                    Some(index, ($"changes[{index}]", $"records {addition.WorkItem} added to {addition.GroupId}, but the group does not list it"))
                | MembershipChange.Removed removal, true ->
                    Some(index, ($"changes[{index}]", $"records {removal.WorkItem} removed from {removal.GroupId}, but the group still lists it"))
                | _ -> None)

        let shape = indexed |> List.collect (fun (index, change) -> changeFindings index change |> List.map (fun finding -> index, finding))

        undeclared @ disagreements @ shape |> List.sortBy fst |> List.map snd
