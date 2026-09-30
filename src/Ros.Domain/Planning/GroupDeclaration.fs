namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions

/// A human-declared execution group recorded in Praxis state by `work group
/// create` (PRX-GRP-073, phase two). The declaration is exactly the one
/// planner configuration carries under `grouping.groups`; the record adds
/// only when and by whom it was declared.
type StoredGroup =
    { Declaration: DeclaredGroup
      CreatedAt: string
      CreatedBy: string
      /// Members added after creation by `work group add`, oldest first:
      /// who added each, and when.
      Additions: MemberAddition list
      /// Members removed by `work group remove`, oldest first: who removed
      /// each, and when.
      Removals: MemberRemoval list }

/// The provenance of one member removed from a stored group.
and MemberRemoval =
    { Member: string
      RemovedAt: string
      RemovedBy: string }

/// The provenance of one member added to a stored group.
and MemberAddition =
    { Member: string
      AddedAt: string
      AddedBy: string }

/// What `work group remove` was asked to record.
type MemberRemovalRequest =
    { GroupId: string
      Member: string
      OccurredAt: string
      RemovedBy: string }

[<RequireQualifiedAccess>]
type MemberRemovalRejection =
    | UnknownGroup of id: string
    | NotMember of groupId: string * id: string
    | TooFewRemaining of groupId: string * remaining: int
    | PredatesHistory of value: string * latest: string
    | InvalidTimestamp of value: string

/// What `work group add` was asked to record.
type MemberAdditionRequest =
    { GroupId: string
      Member: string
      OccurredAt: string
      AddedBy: string }

[<RequireQualifiedAccess>]
type MemberAdditionRejection =
    | UnknownGroup of id: string
    | AlreadyMember of groupId: string * id: string
    | UnknownMember of id: string
    | TerminalMember of id: string * state: string
    | RepositoryMismatch of id: string * item: string * group: string
    | InvalidTimestamp of value: string

/// What `work group create` was asked to record.
type GroupCreationRequest =
    { Id: string
      Members: string list
      Kind: GroupKind option
      SharedContext: string list
      ExecutionRepository: string option
      CrossRepository: bool
      OccurredAt: string
      CreatedBy: string }

[<RequireQualifiedAccess>]
type GroupCreationRejection =
    | InvalidId of id: string
    | DuplicateId of id: string
    | TooFewMembers of count: int
    | DuplicateMember of id: string
    | UnknownMember of id: string
    | TerminalMember of id: string * state: string
    | InvalidTimestamp of value: string
    | BlankSharedContext
    | BlankExecutionRepository

/// A structural defect in stored groups, reported by `validate`.
type StoredGroupFinding = { GroupId: string; Field: string; Message: string }

/// Pure decisions over human-declared groups. Membership never changes a
/// member's lifecycle state (PRX-GRP-002): nothing here can express one.
[<RequireQualifiedAccess>]
module GroupDeclaration =
    /// PRX-GRP-010: `GROUP-<REPOSITORY-OR-AREA>-<SEQUENCE>`, upper-case
    /// segments, e.g. `GROUP-PRAXIS-PLANNING-001`.
    let private idPattern = Regex("^GROUP-[A-Z0-9]+(-[A-Z0-9]+)+$", RegexOptions.CultureInvariant)

    let isValidId (value: string) = idPattern.IsMatch value

    let private terminalStates = set [ "complete"; "abandoned" ]

    let isTerminal (state: string) = terminalStates.Contains state

    let private isTimestamp (value: string) = Text.tryTimestamp value |> Option.isSome

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (snd >> (<) 1) |> List.map fst |> Text.sortOrdinal

    let message rejection =
        match rejection with
        | GroupCreationRejection.InvalidId id -> $"'{id}' is not a group ID; use GROUP-<REPOSITORY-OR-AREA>-<SEQUENCE>, e.g. GROUP-PRAXIS-PLANNING-001 (PRX-GRP-010)"
        | GroupCreationRejection.DuplicateId id -> $"group {id} already exists; group IDs are stable and never reused"
        | GroupCreationRejection.TooFewMembers count -> $"a group needs at least two distinct members; {count} given"
        | GroupCreationRejection.DuplicateMember id -> $"member {id} is listed more than once"
        | GroupCreationRejection.UnknownMember id -> $"member {id} is not a known work item (backlog queue or live context)"
        | GroupCreationRejection.TerminalMember(id, state) -> $"member {id} is {state}; a terminal work item cannot join a new group"
        | GroupCreationRejection.InvalidTimestamp value -> $"--occurred-at '{value}' is not a timestamp"
        | GroupCreationRejection.BlankSharedContext -> "--shared-context must not be blank"
        | GroupCreationRejection.BlankExecutionRepository -> "--execution-repository must not be blank"

    /// Decides whether `request` may be recorded, given the IDs of groups
    /// already stored and each known work item's effective lifecycle state
    /// (`QueuePresentation.effectiveStatus`). Every rejection is reported,
    /// not only the first.
    let decide (existingIds: Set<string>) (lifecycle: Map<string, string>) (request: GroupCreationRequest) : Result<StoredGroup, GroupCreationRejection list> =
        let distinct = request.Members |> List.distinct

        let rejections =
            [ if not (isValidId request.Id) then
                  yield GroupCreationRejection.InvalidId request.Id
              elif existingIds.Contains request.Id then
                  yield GroupCreationRejection.DuplicateId request.Id
              if distinct.Length < 2 then
                  yield GroupCreationRejection.TooFewMembers distinct.Length
              yield! duplicates request.Members |> List.map GroupCreationRejection.DuplicateMember
              for id in distinct do
                  match lifecycle.TryFind id with
                  | None -> yield GroupCreationRejection.UnknownMember id
                  | Some state when isTerminal state -> yield GroupCreationRejection.TerminalMember(id, state)
                  | Some _ -> ()
              if not (isTimestamp request.OccurredAt) then
                  yield GroupCreationRejection.InvalidTimestamp request.OccurredAt
              if request.SharedContext |> List.exists String.IsNullOrWhiteSpace then
                  yield GroupCreationRejection.BlankSharedContext
              if request.ExecutionRepository |> Option.exists String.IsNullOrWhiteSpace then
                  yield GroupCreationRejection.BlankExecutionRepository ]

        match rejections with
        | [] ->
            Ok
                { Declaration =
                    { Id = request.Id
                      Members = distinct
                      Kind = request.Kind
                      Origin = GroupOrigin.HumanDeclared
                      SharedContext = request.SharedContext
                      ExecutionRepository = request.ExecutionRepository
                      CrossRepository = request.CrossRepository
                      ArchitectureNotes = [] }
                  CreatedAt = request.OccurredAt
                  CreatedBy = request.CreatedBy
                  Additions = []
                  Removals = [] }
        | _ -> Error rejections

    let additionMessage rejection =
        match rejection with
        | MemberAdditionRejection.UnknownGroup id -> $"group {id} is not a stored group; declare it with work group create (a group only in planner configuration is changed there)"
        | MemberAdditionRejection.AlreadyMember(groupId, id) -> $"{id} is already a member of {groupId}"
        | MemberAdditionRejection.UnknownMember id -> $"{id} is not a known work item (backlog queue or live context)"
        | MemberAdditionRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal work item cannot join a group"
        | MemberAdditionRejection.RepositoryMismatch(id, item, group) ->
            $"{id} executes in {item} but the group executes in {group}; only a cross-repository group may span repositories"
        | MemberAdditionRejection.InvalidTimestamp value -> $"--occurred-at '{value}' is not a timestamp"

    /// Decides whether `request.Member` may join the stored group
    /// `request.GroupId`. `lifecycle` is each known work item's effective
    /// lifecycle state; `locate` is where a work item executes; `repository`
    /// is where a group without a declared execution repository executes.
    /// A group that is not cross-repository admits only a member that
    /// provably executes in its repository, so an item inferred to execute
    /// in an unknown external repository is refused. Every rejection is
    /// reported, not only the first; the member's lifecycle is never touched.
    let decideAddition
        (stored: StoredGroup list)
        (lifecycle: Map<string, string>)
        (locate: string -> ExecutionLocation)
        (repository: string)
        (request: MemberAdditionRequest)
        : Result<StoredGroup, MemberAdditionRejection list> =
        let group = stored |> List.tryFind (fun candidate -> candidate.Declaration.Id = request.GroupId)
        let id = request.Member

        let membership =
            match group with
            | None -> [ MemberAdditionRejection.UnknownGroup request.GroupId ]
            | Some group when group.Declaration.Members |> List.contains id -> [ MemberAdditionRejection.AlreadyMember(request.GroupId, id) ]
            | Some _ -> []

        let state =
            match lifecycle.TryFind id with
            | None -> [ MemberAdditionRejection.UnknownMember id ]
            | Some state when isTerminal state -> [ MemberAdditionRejection.TerminalMember(id, state) ]
            | Some _ -> []

        let location =
            match group with
            | Some group when not group.Declaration.CrossRepository && lifecycle.ContainsKey id ->
                let groupRepository = group.Declaration.ExecutionRepository |> Option.defaultValue repository

                match locate id with
                | ExecutionLocation.Repository name when name = groupRepository -> []
                | other -> [ MemberAdditionRejection.RepositoryMismatch(id, ExecutionLocation.describe other, groupRepository) ]
            | _ -> []

        let timestamp =
            if isTimestamp request.OccurredAt then [] else [ MemberAdditionRejection.InvalidTimestamp request.OccurredAt ]

        match group, membership @ state @ location @ timestamp with
        | Some group, [] ->
            Ok
                { group with
                    Declaration = { group.Declaration with Members = group.Declaration.Members @ [ id ] }
                    Additions =
                        group.Additions
                        @ [ { Member = id
                              AddedAt = request.OccurredAt
                              AddedBy = request.AddedBy } ] }
        | _, rejections -> Error rejections

    let removalMessage rejection =
        match rejection with
        | MemberRemovalRejection.UnknownGroup id -> $"group {id} is not a stored group; declare it with work group create (a group only in planner configuration is changed there)"
        | MemberRemovalRejection.NotMember(groupId, id) -> $"{id} is not a member of {groupId}"
        | MemberRemovalRejection.TooFewRemaining(groupId, remaining) ->
            $"removing it would leave {groupId} with {remaining} member(s); a group needs at least two, so its last members cannot be removed"
        | MemberRemovalRejection.PredatesHistory(value, latest) -> $"--occurred-at '{value}' predates the group's latest recorded change ({latest})"
        | MemberRemovalRejection.InvalidTimestamp value -> $"--occurred-at '{value}' is not a timestamp"

    /// When the group was created and each membership change was recorded.
    let private history (group: StoredGroup) =
        group.CreatedAt
        :: (group.Additions |> List.map (fun addition -> addition.AddedAt))
        @ (group.Removals |> List.map (fun removal -> removal.RemovedAt))

    /// Decides whether `request.Member` may leave the stored group
    /// `request.GroupId`. Only a member can be removed, a group keeps at least
    /// two members, and a removal cannot predate the group's recorded
    /// history. The member's lifecycle state is not consulted and never
    /// touched: a completed, abandoned or no longer known member may leave.
    /// Every rejection is reported, not only the first.
    let decideRemoval (stored: StoredGroup list) (request: MemberRemovalRequest) : Result<StoredGroup, MemberRemovalRejection list> =
        let group = stored |> List.tryFind (fun candidate -> candidate.Declaration.Id = request.GroupId)
        let id = request.Member

        let membership =
            match group with
            | None -> [ MemberRemovalRejection.UnknownGroup request.GroupId ]
            | Some group when not (group.Declaration.Members |> List.contains id) -> [ MemberRemovalRejection.NotMember(request.GroupId, id) ]
            | Some group ->
                match group.Declaration.Members |> List.filter ((<>) id) |> List.distinct |> List.length with
                | remaining when remaining < 2 -> [ MemberRemovalRejection.TooFewRemaining(request.GroupId, remaining) ]
                | _ -> []

        let timestamp =
            match Text.tryTimestamp request.OccurredAt, group with
            | None, _ -> [ MemberRemovalRejection.InvalidTimestamp request.OccurredAt ]
            | Some at, Some group ->
                history group
                |> List.choose (fun value -> Text.tryTimestamp value |> Option.map (fun parsed -> parsed, value))
                |> List.sortBy fst
                |> List.tryLast
                |> Option.filter (fun (latest, _) -> at < latest)
                |> Option.map (fun (_, latest) -> MemberRemovalRejection.PredatesHistory(request.OccurredAt, latest))
                |> Option.toList
            | Some _, None -> []

        match group, membership @ timestamp with
        | Some group, [] ->
            Ok
                { group with
                    Declaration = { group.Declaration with Members = group.Declaration.Members |> List.filter ((<>) id) }
                    Removals =
                        group.Removals
                        @ [ ({ Member = id
                               RemovedAt = request.OccurredAt
                               RemovedBy = request.RemovedBy }
                            : MemberRemoval) ] }
        | _, rejections -> Error rejections

    /// Each member whose membership changed after creation, with whether
    /// its latest change added it. An unparsable time sorts first; a member
    /// added and removed at the same latest instant has no knowable latest
    /// change and is omitted.
    let private latestChanges (group: StoredGroup) =
        let at value = Text.tryTimestamp value |> Option.defaultValue DateTimeOffset.MinValue

        (group.Additions |> List.map (fun addition -> addition.Member, at addition.AddedAt, true))
        @ (group.Removals |> List.map (fun removal -> removal.Member, at removal.RemovedAt, false))
        |> List.groupBy (fun (id, _, _) -> id)
        |> List.choose (fun (id, changes) ->
            let latest = changes |> List.map (fun (_, time, _) -> time) |> List.max

            match changes |> List.filter (fun (_, time, _) -> time = latest) |> List.map (fun (_, _, added) -> added) |> List.distinct with
            | [ added ] -> Some(id, added)
            | _ -> None)

    /// `stored` with `group` in place of the stored group of the same ID.
    let replace (stored: StoredGroup list) (group: StoredGroup) =
        stored |> List.map (fun candidate -> if candidate.Declaration.Id = group.Declaration.Id then group else candidate)

    /// Stored groups in their canonical (ordinal ID) order.
    let add (stored: StoredGroup list) (group: StoredGroup) =
        stored @ [ group ] |> List.sortWith (fun left right -> Text.ordinal left.Declaration.Id right.Declaration.Id)

    /// `validate`'s structural checks. A member that has since completed or
    /// been abandoned is legitimate (partial completion, PRX-GRP-042); a
    /// member no longer known at all is not.
    let findings (stored: StoredGroup list) (known: Set<string>) : StoredGroupFinding list =
        let duplicateIds = stored |> List.map (fun group -> group.Declaration.Id) |> duplicates |> Set.ofList

        stored
        |> List.collect (fun group ->
            let declaration = group.Declaration
            let finding field text = { GroupId = declaration.Id; Field = field; Message = text }
            let distinct = declaration.Members |> List.distinct

            [ if not (isValidId declaration.Id) then
                  yield finding "id" $"'{declaration.Id}' is not a group ID (GROUP-<REPOSITORY-OR-AREA>-<SEQUENCE>)"
              if duplicateIds.Contains declaration.Id then
                  yield finding "id" $"group {declaration.Id} is stored more than once"
              if distinct.Length < 2 then
                  yield finding "members" $"group {declaration.Id} has {distinct.Length} distinct member(s); a group needs at least two"
              for id in duplicates declaration.Members do
                  yield finding "members" $"member {id} is listed more than once"
              for id in distinct do
                  if not (known.Contains id) then
                      yield finding "members" $"member {id} is not a known work item (backlog queue or live context)"
              if declaration.Origin <> GroupOrigin.HumanDeclared then
                  yield finding "origin" $"a stored group is human-declared; found {GroupOrigin.code declaration.Origin}"
              if not (isTimestamp group.CreatedAt) then
                  yield finding "createdAt" $"'{group.CreatedAt}' is not a timestamp"
              if String.IsNullOrWhiteSpace group.CreatedBy then
                  yield finding "createdBy" "the declaring actor is missing"
              for id, added in latestChanges group do
                  match added, declaration.Members |> List.contains id with
                  | true, false -> yield finding "additions" $"added member {id} is not a member"
                  | false, true -> yield finding "removals" $"removed member {id} is still a member"
                  | _ -> ()
              for addition in group.Additions do
                  if not (isTimestamp addition.AddedAt) then
                      yield finding "additions" $"'{addition.AddedAt}' (addition of {addition.Member}) is not a timestamp"
                  if String.IsNullOrWhiteSpace addition.AddedBy then
                      yield finding "additions" $"the actor who added {addition.Member} is missing"
              for removal in group.Removals do
                  if not (isTimestamp removal.RemovedAt) then
                      yield finding "removals" $"'{removal.RemovedAt}' (removal of {removal.Member}) is not a timestamp"
                  if String.IsNullOrWhiteSpace removal.RemovedBy then
                      yield finding "removals" $"the actor who removed {removal.Member} is missing" ])

    /// The declarations the planner reads: stored groups, then those of the
    /// supplied configuration. An ID declared in both is ambiguous and is
    /// refused rather than silently resolved.
    let mergeInto (configuration: PlannerConfiguration) (stored: StoredGroup list) : Result<PlannerConfiguration, string> =
        let storedIds = stored |> List.map (fun group -> group.Declaration.Id) |> Set.ofList

        match configuration.Grouping.Groups |> List.map (fun group -> group.Id) |> List.filter storedIds.Contains |> Text.distinctOrdinal with
        | [] ->
            Ok
                { configuration with
                    Grouping =
                        { configuration.Grouping with
                            Groups = (stored |> List.map (fun group -> group.Declaration)) @ configuration.Grouping.Groups } }
        | clashes ->
            let joined = String.Join(", ", clashes)
            Error $"group ID(s) {joined} are declared both in Praxis state (.ros/work/groups.json) and in planner configuration; rename one declaration"
