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

    let private reasonCheck (request: MemberAdditionRequest) =
        match request.Reason with
        | Some reason when String.IsNullOrWhiteSpace reason -> [ GroupAdditionRejection.BlankReason ]
        | _ -> []

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

    /// Findings for the membership ledger (`validate`): every recorded
    /// addition names a declared group that still lists the member, and
    /// carries a timestamp and an actor.
    let findings (stored: StoredGroupDeclaration list) (additions: MemberAddition list) : (string * string) list =
        let groups = stored |> List.map (fun entry -> entry.Group.Id, entry.Group) |> Map.ofList

        additions
        |> List.mapi (fun index addition ->
            let field = $"additions[{index}]"

            [ match groups.TryFind addition.GroupId with
              | None -> yield field, $"records an addition to {addition.GroupId}, which is not declared"
              | Some group when not (group.Members |> List.contains addition.WorkItem) ->
                  yield field, $"records {addition.WorkItem} added to {addition.GroupId}, but the group does not list it"
              | Some _ -> ()
              if String.IsNullOrWhiteSpace addition.AddedAt then
                  yield field, "addedAt is required"
              if String.IsNullOrWhiteSpace addition.AddedBy.Id then
                  yield field, "addedBy.id is required" ])
        |> List.concat
