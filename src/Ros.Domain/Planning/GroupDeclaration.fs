namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Provenance
open Ros.Domain.Work

/// A human-declared execution group recorded in Praxis state
/// (`.ros/work/groups.json`, PRX-GRP-073 phase two). `Declaration` has
/// exactly the shape of a `grouping.groups` entry, so the planner reads a
/// stored group exactly as it reads a configured one. `DeclaredAt` and
/// `DeclaredBy` are store metadata the planner ignores.
type StoredGroup =
    { Declaration: DeclaredGroup
      DeclaredAt: string
      DeclaredBy: Actor option }

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

[<RequireQualifiedAccess>]
module GroupStore =
    [<Literal>]
    let Schema = "praxis.work-groups/1.0.0"

    let empty = { Groups = [] }

    let private sorted (groups: StoredGroup list) =
        groups |> List.sortWith (fun left right -> String.CompareOrdinal(left.Declaration.Id, right.Declaration.Id))

    let add (group: StoredGroup) (store: GroupStore) = { Groups = sorted (group :: store.Groups) }

    let tryFind (id: string) (store: GroupStore) = store.Groups |> List.tryFind (fun group -> group.Declaration.Id = id)

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
                  DeclaredBy = declaredBy }

            Ok(GroupStore.add stored store, stored)
        | found -> Error found

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
                      yield { Field = at "declaredAt"; Message = "declaredAt is required" } ])

        duplicateIds @ perGroup
