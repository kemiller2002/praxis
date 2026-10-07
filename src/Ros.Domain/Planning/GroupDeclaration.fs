namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Provenance
open Ros.Domain.Work

[<RequireQualifiedAccess>]
type MembershipOperation =
    | Added

[<RequireQualifiedAccess>]
module MembershipOperation =
    let code operation =
        match operation with
        | MembershipOperation.Added -> "added"

    let all = [ MembershipOperation.Added ]

    let tryParse value = all |> List.tryFind (fun operation -> code operation = value)

/// One membership change after declaration, with who made it and when
/// (provenance). It names the member only: the member's own lifecycle
/// state, evidence and attribution are never part of it (PRX-GRP-043).
type MembershipChange =
    { Member: string
      Operation: MembershipOperation
      OccurredAt: string
      Actor: Actor option }

/// A human-declared execution group recorded in Praxis state
/// (`.ros/work/groups.json`, PRX-GRP-073 phase two). `Declaration` has
/// exactly the shape of a `grouping.groups` entry, so the planner reads a
/// stored group exactly as it reads a configured one. `DeclaredAt`,
/// `DeclaredBy` and `Membership` (changes after declaration, oldest first)
/// are store metadata the planner ignores.
type StoredGroup =
    { Declaration: DeclaredGroup
      DeclaredAt: string
      DeclaredBy: Actor option
      Membership: MembershipChange list }

/// Every stored group, in ordinal ID order.
type GroupStore = { Groups: StoredGroup list }

/// What a group operation knows about a prospective member: its own
/// recorded state (live context first, else the backlog). Absent means the
/// item is unknown to this repository.
[<RequireQualifiedAccess>]
type MemberState =
    | Open of state: string
    | Terminal of state: string

[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of id: string
    | DuplicateGroupId of id: string
    | NoMembers
    | DuplicateMember of id: string
    | UnknownMember of id: string
    | TerminalMember of id: string * state: string
    | CrossRepositoryWithoutRepository
    | UnknownGroup of id: string
    | AlreadyMember of id: string * group: string
    | RepositoryMismatch of id: string * memberRepository: string * groupRepository: string

/// A stored-group problem `validate` reports.
type GroupFinding = { Field: string; Message: string }

[<RequireQualifiedAccess>]
module GroupRejection =
    let message rejection =
        match rejection with
        | GroupRejection.InvalidGroupId id -> $"'{id}' is not a valid group ID; use GROUP-<AREA>[-<PART>...]-<NNN> in upper case (PRX-GRP-010)"
        | GroupRejection.DuplicateGroupId id -> $"group {id} is already declared; group IDs are stable and never reused"
        | GroupRejection.NoMembers -> "a group needs at least one --member"
        | GroupRejection.DuplicateMember id -> $"{id} is listed more than once"
        | GroupRejection.UnknownMember id -> $"{id} is not a work item in this repository's backlog or live context"
        | GroupRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal item cannot join a new group"
        | GroupRejection.CrossRepositoryWithoutRepository -> "--cross-repository needs --execution-repository naming where the coordinated outcome is integrated (PRX-GRP-051)"
        | GroupRejection.UnknownGroup id -> $"no declared group {id}; see 'plan groups' or .ros/work/groups.json"
        | GroupRejection.AlreadyMember(id, group) -> $"{id} is already a member of {group}"
        | GroupRejection.RepositoryMismatch(id, memberRepository, groupRepository) ->
            $"{id} executes in {memberRepository} but the group executes in {groupRepository}; only a cross-repository group may mix repositories (PRX-GRP-051)"

[<RequireQualifiedAccess>]
module GroupStore =
    [<Literal>]
    let Schema = "praxis.work-groups/1.0.0"

    let empty = { Groups = [] }

    let private sorted (groups: StoredGroup list) =
        groups |> List.sortWith (fun left right -> String.CompareOrdinal(left.Declaration.Id, right.Declaration.Id))

    let add (group: StoredGroup) (store: GroupStore) = { Groups = sorted (group :: store.Groups) }

    let tryFind (id: string) (store: GroupStore) = store.Groups |> List.tryFind (fun group -> group.Declaration.Id = id)

    /// `store` with the group of the same ID replaced by `group`.
    let replace (group: StoredGroup) (store: GroupStore) =
        { Groups = store.Groups |> List.map (fun existing -> if existing.Declaration.Id = group.Declaration.Id then group else existing) }

    let declarations (store: GroupStore) = store.Groups |> List.map (fun group -> group.Declaration)

[<RequireQualifiedAccess>]
module GroupDeclaration =
    let private idPattern = Regex(@"^GROUP-[A-Z0-9]+(-[A-Z0-9]+)+$", RegexOptions.CultureInvariant)

    let isValidId (id: string) = not (isNull id) && idPattern.IsMatch id

    /// Each item's own recorded state. Live context outranks the backlog:
    /// a started item stays in the queue with its backlog status.
    let memberStates (queue: PlanningQueueItem list) (live: PlanningLiveItem list) : Map<string, MemberState> =
        let fromQueue =
            queue
            |> List.map (fun item ->
                item.Id,
                match item.Status with
                | "abandoned" -> MemberState.Terminal "abandoned"
                | status -> MemberState.Open status)

        let fromLive =
            live
            |> List.map (fun item ->
                item.Id,
                match item.State with
                | LiveWorkState.Complete -> MemberState.Terminal "complete"
                | LiveWorkState.Abandoned -> MemberState.Terminal "abandoned"
                | LiveWorkState.Ready -> MemberState.Open "ready"
                | LiveWorkState.Active -> MemberState.Open "active"
                | LiveWorkState.Blocked -> MemberState.Open "blocked")

        fromQueue @ fromLive |> Map.ofList

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    let private memberRejections (states: Map<string, MemberState>) (members: string list) =
        members
        |> List.distinct
        |> List.choose (fun memberId ->
            match states.TryFind memberId with
            | None -> Some(GroupRejection.UnknownMember memberId)
            | Some(MemberState.Terminal state) -> Some(GroupRejection.TerminalMember(memberId, state))
            | Some(MemberState.Open _) -> None)

    /// Every reason `declaration` cannot be recorded, in a fixed order; empty
    /// when it can.
    let rejections (store: GroupStore) (states: Map<string, MemberState>) (declaration: DeclaredGroup) : GroupRejection list =
        [ if not (isValidId declaration.Id) then
              yield GroupRejection.InvalidGroupId declaration.Id
          if GroupStore.tryFind declaration.Id store |> Option.isSome then
              yield GroupRejection.DuplicateGroupId declaration.Id
          if declaration.Members.IsEmpty then
              yield GroupRejection.NoMembers
          yield! duplicates declaration.Members |> List.map GroupRejection.DuplicateMember
          yield! memberRejections states declaration.Members
          if declaration.CrossRepository && declaration.ExecutionRepository.IsNone then
              yield GroupRejection.CrossRepositoryWithoutRepository ]

    /// Records `declaration` in `store`. Only the store changes: no member's
    /// lifecycle state, evidence or attribution is an input or an output.
    let create
        (store: GroupStore)
        (states: Map<string, MemberState>)
        (declaredAt: string)
        (declaredBy: Actor option)
        (declaration: DeclaredGroup)
        : Result<GroupStore * StoredGroup, GroupRejection list> =
        match rejections store states declaration with
        | [] ->
            let stored =
                { Declaration = declaration
                  DeclaredAt = declaredAt
                  DeclaredBy = declaredBy
                  Membership = [] }

            Ok(GroupStore.add stored store, stored)
        | found -> Error found

    /// Every repository a group already executes in: its declared execution
    /// repository, else wherever its current members execute.
    let private groupLocations (locate: string -> ExecutionLocation) (group: DeclaredGroup) =
        match group.ExecutionRepository with
        | Some repository -> [ ExecutionLocation.Repository repository ]
        | None -> group.Members |> List.map locate |> List.distinct

    /// PRX-GRP-051: outside a cross-repository group a member must execute
    /// where the group does. An undeclared external location is never known
    /// to match, so it is refused too.
    let private repositoryRejection (locate: string -> ExecutionLocation) (group: DeclaredGroup) (memberId: string) =
        let location = locate memberId
        let expected = groupLocations locate group

        let matches =
            match location with
            | ExecutionLocation.UnknownExternal -> expected.IsEmpty
            | ExecutionLocation.Repository _ -> expected |> List.forall ((=) location)

        if group.CrossRepository || matches then
            None
        else
            let groupRepository = expected |> List.map ExecutionLocation.describe |> String.concat " + "
            Some(GroupRejection.RepositoryMismatch(memberId, ExecutionLocation.describe location, groupRepository))

    /// Every reason `memberId` cannot join group `groupId`, in a fixed order;
    /// empty when it can. An unknown group is the only reason reported then.
    let addRejections (store: GroupStore) (states: Map<string, MemberState>) (locate: string -> ExecutionLocation) (groupId: string) (memberId: string) =
        match GroupStore.tryFind groupId store with
        | None -> [ GroupRejection.UnknownGroup groupId ]
        | Some stored ->
            let group = stored.Declaration

            [ if group.Members |> List.contains memberId then
                  yield GroupRejection.AlreadyMember(memberId, groupId)
              yield! memberRejections states [ memberId ]
              if states.ContainsKey memberId then
                  yield! repositoryRejection locate group memberId |> Option.toList ]

    /// Adds `memberId` to the end of group `groupId` and records who added it
    /// and when. Only the store changes: the member's lifecycle state,
    /// evidence and attribution are neither inputs nor outputs.
    let addMember
        (store: GroupStore)
        (states: Map<string, MemberState>)
        (locate: string -> ExecutionLocation)
        (addedAt: string)
        (addedBy: Actor option)
        (groupId: string)
        (memberId: string)
        : Result<GroupStore * StoredGroup * MembershipChange, GroupRejection list> =
        match addRejections store states locate groupId memberId, GroupStore.tryFind groupId store with
        | [], Some stored ->
            let change =
                { Member = memberId
                  Operation = MembershipOperation.Added
                  OccurredAt = addedAt
                  Actor = addedBy }

            let updated =
                { stored with
                    Declaration = { stored.Declaration with Members = stored.Declaration.Members @ [ memberId ] }
                    Membership = stored.Membership @ [ change ] }

            Ok(GroupStore.replace updated store, updated, change)
        | [], None -> Error [ GroupRejection.UnknownGroup groupId ]
        | found, _ -> Error found

    /// The planner's view: configured declarations first, then stored ones in
    /// ID order. A configured group with the same ID wins, since the
    /// configuration file is the narrower, per-invocation input.
    let mergeInto (grouping: GroupingConfiguration) (stored: DeclaredGroup list) : GroupingConfiguration =
        let configured = grouping.Groups |> List.map (fun group -> group.Id) |> Set.ofList

        { grouping with Groups = grouping.Groups @ (stored |> List.filter (fun group -> not (configured.Contains group.Id))) }

    /// What `validate` checks for stored groups. A member that became
    /// terminal after the group was declared is partial completion
    /// (PRX-GRP-042), not a finding; a member that is unknown is.
    let findings (store: GroupStore) (states: Map<string, MemberState>) : GroupFinding list =
        let duplicateIds =
            duplicates (store.Groups |> List.map (fun group -> group.Declaration.Id))
            |> List.map (fun id -> { Field = $"groups[{id}].id"; Message = $"group ID {id} is declared more than once" })

        let perGroup =
            store.Groups
            |> List.collect (fun stored ->
                let group = stored.Declaration
                let at name = $"groups[{group.Id}].{name}"

                [ if not (isValidId group.Id) then
                      yield { Field = at "id"; Message = $"'{group.Id}' is not a valid group ID" }
                  if group.Members.IsEmpty then
                      yield { Field = at "members"; Message = "a stored group has no members" }
                  for memberId in duplicates group.Members do
                      yield { Field = at "members"; Message = $"{memberId} is listed more than once" }
                  for memberId in group.Members |> List.distinct do
                      if not (states.ContainsKey memberId) then
                          yield { Field = at "members"; Message = $"{memberId} is not a work item in this repository's backlog or live context" }
                  if group.CrossRepository && group.ExecutionRepository.IsNone then
                      yield { Field = at "executionRepository"; Message = "a cross-repository group must name its execution repository" }
                  if String.IsNullOrWhiteSpace stored.DeclaredAt then
                      yield { Field = at "declaredAt"; Message = "declaredAt is required" }
                  for change in stored.Membership do
                      if String.IsNullOrWhiteSpace change.OccurredAt then
                          yield { Field = at "membership"; Message = $"the membership change for {change.Member} has no occurredAt" }
                  // Only each member's latest change describes current membership.
                  for memberId, changes in stored.Membership |> List.groupBy (fun change -> change.Member) do
                      if (List.last changes).Operation = MembershipOperation.Added && not (group.Members |> List.contains memberId) then
                          yield { Field = at "membership"; Message = $"{memberId} is recorded as added but is not a member" } ])

        duplicateIds @ perGroup
