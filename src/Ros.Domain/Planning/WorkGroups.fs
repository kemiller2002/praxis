namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Provenance
open Ros.Domain.Work

// Declared execution groups (requirements/PLANNING-WORK-GROUPS.md phase two,
// PRX-GRP-073). A stored group is a durable human declaration that several
// work items belong together. It is Praxis state of its own: recording,
// changing or checkpointing a group never changes a member's lifecycle
// state, evidence, attribution, telemetry or checkpoints (PRX-GRP-002, 041,
// 043). The planner reads a stored group exactly as it reads a
// `grouping.groups` declaration (`WorkGroups.declared`). Every function here
// is pure; the file and Git effects live in Infrastructure and the CLI.

/// A work item's own recorded state: its live state when it has a live
/// record, otherwise its backlog status. Never inferred.
[<RequireQualifiedAccess>]
type RecordedWorkState =
    | Live of LiveWorkState
    | Backlog of status: string

[<RequireQualifiedAccess>]
module RecordedWorkState =
    let code state =
        match state with
        | RecordedWorkState.Live LiveWorkState.Ready -> "ready"
        | RecordedWorkState.Live LiveWorkState.Active -> "active"
        | RecordedWorkState.Live LiveWorkState.Blocked -> "blocked"
        | RecordedWorkState.Live LiveWorkState.Complete -> "complete"
        | RecordedWorkState.Live LiveWorkState.Abandoned -> "abandoned"
        | RecordedWorkState.Backlog status -> status

    let isTerminal state = code state = "complete" || code state = "abandoned"

/// One work item as group decisions see it.
type CatalogItem =
    { Id: string
      State: RecordedWorkState
      /// Where the item is implemented (PRX-GRP-051): an explicit
      /// declaration, otherwise this repository.
      ExecutionRepository: string
      /// The item's own latest durable checkpoint, when it has one.
      LatestCheckpointId: string option }

/// Every work item this repository tracks, and the repository itself.
type WorkCatalog =
    { Repository: string
      Items: Map<string, CatalogItem> }

/// One member of a stored group and who added it (provenance).
type GroupMembership =
    { WorkItemId: string
      ExecutionRepository: string
      AddedAt: string
      AddedBy: Actor
      Reason: string option }

[<RequireQualifiedAccess>]
type GroupChange =
    | Created
    | MemberAdded of workItemId: string
    | MemberRemoved of workItemId: string
    | Checkpointed of checkpointId: string

[<RequireQualifiedAccess>]
module GroupChange =
    let code change =
        match change with
        | GroupChange.Created -> "created"
        | GroupChange.MemberAdded _ -> "member-added"
        | GroupChange.MemberRemoved _ -> "member-removed"
        | GroupChange.Checkpointed _ -> "checkpointed"

    let subject change =
        match change with
        | GroupChange.Created -> None
        | GroupChange.MemberAdded id
        | GroupChange.MemberRemoved id
        | GroupChange.Checkpointed id -> Some id

    let tryParse (code: string) (subject: string option) =
        match code, subject with
        | "created", None -> Some GroupChange.Created
        | "member-added", Some id -> Some(GroupChange.MemberAdded id)
        | "member-removed", Some id -> Some(GroupChange.MemberRemoved id)
        | "checkpointed", Some id -> Some(GroupChange.Checkpointed id)
        | _ -> None

/// An append-only record of who changed the group, when and why.
type GroupHistoryEntry =
    { Change: GroupChange
      OccurredAt: string
      Actor: Actor
      Reason: string option }

/// A member's own latest checkpoint as the group checkpoint saw it. It is a
/// reference: the member's checkpoint history is never replaced.
type MemberCheckpointReference =
    { WorkItemId: string
      CheckpointId: string option }

/// PRX-GRP-044: a group-level recovery point over its members' own
/// checkpoints, verified exactly like `work checkpoint`. It carries no paths:
/// no member is attributed another member's changes (PRX-GRP-043).
type GroupCheckpoint =
    { Id: string
      RecordedAt: string
      Actor: Actor
      Location: GitDurableLocation
      Summary: string
      NextAction: string
      SharedDecisions: string list
      Active: string list
      Completed: string list
      Remaining: string list
      MemberCheckpoints: MemberCheckpointReference list }

/// A durable, human-declared group (PRX-GRP-010, 011, 051, 073).
type StoredWorkGroup =
    { Id: string
      Kind: GroupKind option
      Origin: GroupOrigin
      ExecutionRepository: string
      CrossRepository: bool
      SharedContext: string list
      ArchitectureNotes: string list
      CreatedAt: string
      CreatedBy: Actor
      Members: GroupMembership list
      History: GroupHistoryEntry list
      Checkpoints: GroupCheckpoint list }

/// Why a group operation was refused. Nothing is recorded on a refusal.
[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of groupId: string
    | DuplicateGroup of groupId: string
    | GroupNotFound of groupId: string
    | NoMembers
    | InvalidMemberId of workItemId: string
    | UnknownMember of workItemId: string
    | TerminalMember of workItemId: string * state: string
    | RepeatedMember of workItemId: string
    | AlreadyMember of groupId: string * workItemId: string
    | NotMember of groupId: string * workItemId: string
    | RepositoryMismatch of workItemId: string * itemRepository: string * groupRepository: string
    | LastMember of groupId: string * workItemId: string
    | BlankText of field: string
    | Checkpoint of CheckpointRejection

[<RequireQualifiedAccess>]
module GroupRejection =
    let code rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupRejection.DuplicateGroup _ -> "duplicate-group"
        | GroupRejection.GroupNotFound _ -> "group-not-found"
        | GroupRejection.NoMembers -> "no-members"
        | GroupRejection.InvalidMemberId _ -> "invalid-member-id"
        | GroupRejection.UnknownMember _ -> "unknown-member"
        | GroupRejection.TerminalMember _ -> "terminal-member"
        | GroupRejection.RepeatedMember _ -> "repeated-member"
        | GroupRejection.AlreadyMember _ -> "already-member"
        | GroupRejection.NotMember _ -> "not-member"
        | GroupRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupRejection.LastMember _ -> "last-member"
        | GroupRejection.BlankText _ -> "blank-text"
        | GroupRejection.Checkpoint inner -> CheckpointRejection.code inner

    let message rejection =
        match rejection with
        | GroupRejection.InvalidGroupId id -> $"'{id}' is not a valid group ID; use GROUP-<AREA>-<SEQUENCE>, for example GROUP-PRAXIS-PLANNING-001"
        | GroupRejection.DuplicateGroup id -> $"a group '{id}' is already declared"
        | GroupRejection.GroupNotFound id -> $"no group '{id}' is declared"
        | GroupRejection.NoMembers -> "a group needs at least one member; pass --member ID"
        | GroupRejection.InvalidMemberId id -> $"'{id}' is not a valid work-item ID"
        | GroupRejection.UnknownMember id -> $"work item '{id}' is not tracked by this repository (neither in the backlog nor in work context)"
        | GroupRejection.TerminalMember(id, state) -> $"work item '{id}' is {state}; a terminal item cannot join a group"
        | GroupRejection.RepeatedMember id -> $"work item '{id}' is named more than once"
        | GroupRejection.AlreadyMember(group, id) -> $"work item '{id}' is already a member of {group}"
        | GroupRejection.NotMember(group, id) -> $"work item '{id}' is not a member of {group}"
        | GroupRejection.RepositoryMismatch(id, item, group) ->
            $"work item '{id}' executes in {item} but the group executes in {group}; declare the group --cross-repository or keep groups repository-local (PRX-GRP-051)"
        | GroupRejection.LastMember(group, id) -> $"'{id}' is the last member of {group}; pass --allow-empty to leave the group declared with no members"
        | GroupRejection.BlankText field -> $"{field} must not be blank"
        | GroupRejection.Checkpoint inner -> CheckpointRejection.message inner

    let isArgumentError rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _
        | GroupRejection.NoMembers
        | GroupRejection.InvalidMemberId _
        | GroupRejection.RepeatedMember _
        | GroupRejection.BlankText _ -> true
        | GroupRejection.Checkpoint inner -> CheckpointRejection.isArgumentError inner
        | _ -> false

/// What `work group create` asks for.
type GroupDeclarationRequest =
    { Id: string
      Members: string list
      Kind: GroupKind option
      Origin: GroupOrigin
      ExecutionRepository: string option
      CrossRepository: bool
      SharedContext: string list
      ArchitectureNotes: string list
      OccurredAt: string
      Actor: Actor
      Reason: string option }

/// A structural problem in stored groups, reported by `validate`.
type GroupFinding =
    { GroupId: string
      Field: string
      Message: string }

[<RequireQualifiedAccess>]
module WorkGroups =
    let private idPattern = Regex(@"^GROUP-[A-Z0-9]+(?:-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)

    /// PRX-GRP-010: `GROUP-<repository-or-area>-<sequence>`.
    let isValidId (id: string) = not (isNull id) && idPattern.IsMatch id

    let memberIds (group: StoredWorkGroup) = group.Members |> List.map (fun entry -> entry.WorkItemId)

    let tryFind (groups: StoredWorkGroup list) (id: string) = groups |> List.tryFind (fun group -> group.Id = id)

    let private repeated (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    /// Whether one item may join a group executing in `groupRepository`:
    /// tracked, not terminal, and repository-local unless cross-repository.
    let admit (catalog: WorkCatalog) (groupRepository: string) (crossRepository: bool) (workItemId: string) : Result<CatalogItem, GroupRejection> =
        if not (WorkItemId.isValid workItemId) then
            Error(GroupRejection.InvalidMemberId workItemId)
        else
            match catalog.Items.TryFind workItemId with
            | None -> Error(GroupRejection.UnknownMember workItemId)
            | Some item when RecordedWorkState.isTerminal item.State -> Error(GroupRejection.TerminalMember(workItemId, RecordedWorkState.code item.State))
            | Some item when not crossRepository && item.ExecutionRepository <> groupRepository ->
                Error(GroupRejection.RepositoryMismatch(workItemId, item.ExecutionRepository, groupRepository))
            | Some item -> Ok item

    let private errors (results: Result<'a, GroupRejection> list) =
        results |> List.choose (function Error rejection -> Some rejection | Ok _ -> None)

    let private membership (request: GroupDeclarationRequest) (item: CatalogItem) =
        { WorkItemId = item.Id
          ExecutionRepository = item.ExecutionRepository
          AddedAt = request.OccurredAt
          AddedBy = request.Actor
          Reason = request.Reason }

    /// `work group create`: a new declared group, or every reason it cannot
    /// be recorded. `declaredIds` are the IDs already in use (stored groups
    /// and any planner-configured `grouping.groups`). Members' own records
    /// are never part of the result, so creation cannot change them.
    let create (catalog: WorkCatalog) (declaredIds: string list) (request: GroupDeclarationRequest) : Result<StoredWorkGroup, GroupRejection list> =
        let repository = request.ExecutionRepository |> Option.defaultValue catalog.Repository
        let admitted = request.Members |> List.distinct |> List.map (admit catalog repository request.CrossRepository)

        let rejections =
            [ if not (isValidId request.Id) then GroupRejection.InvalidGroupId request.Id
              elif List.contains request.Id declaredIds then GroupRejection.DuplicateGroup request.Id
              if request.Members.IsEmpty then GroupRejection.NoMembers
              yield! repeated request.Members |> List.map GroupRejection.RepeatedMember
              if String.IsNullOrWhiteSpace repository then GroupRejection.BlankText "--execution-repository"
              yield! errors admitted ]

        match rejections with
        | [] ->
            let members = admitted |> List.choose (function Ok item -> Some(membership request item) | Error _ -> None)

            Ok
                { Id = request.Id
                  Kind = request.Kind
                  Origin = request.Origin
                  ExecutionRepository = repository
                  CrossRepository = request.CrossRepository
                  SharedContext = request.SharedContext
                  ArchitectureNotes = request.ArchitectureNotes
                  CreatedAt = request.OccurredAt
                  CreatedBy = request.Actor
                  Members = members
                  History =
                    [ { Change = GroupChange.Created
                        OccurredAt = request.OccurredAt
                        Actor = request.Actor
                        Reason = request.Reason } ]
                  Checkpoints = [] }
        | rejections -> Error rejections

    /// The planner's view of a stored group: exactly a `grouping.groups`
    /// declaration (PRX-GRP-073).
    let declared (group: StoredWorkGroup) : DeclaredGroup =
        { Id = group.Id
          Members = memberIds group
          Kind = group.Kind
          Origin = group.Origin
          SharedContext = group.SharedContext
          ExecutionRepository = Some group.ExecutionRepository
          CrossRepository = group.CrossRepository
          ArchitectureNotes = group.ArchitectureNotes }

    /// Adds stored groups to a planner configuration. A configured
    /// declaration with the same ID wins (it is the file the caller named),
    /// an empty group declares nothing, and a member recorded as executing
    /// elsewhere keeps that location unless the configuration names one.
    let declare (localRepository: string) (stored: StoredWorkGroup list) (configuration: PlannerConfiguration) : PlannerConfiguration =
        let grouping = configuration.Grouping
        let configured = grouping.Groups |> List.map (fun group -> group.Id) |> Set.ofList

        let added =
            stored
            |> List.filter (fun group -> not (configured.Contains group.Id) && not group.Members.IsEmpty)
            |> List.sortWith (fun left right -> String.CompareOrdinal(left.Id, right.Id))

        let located = grouping.ExecutionRepositories |> List.map fst |> Set.ofList

        let repositories =
            added
            |> List.collect (fun group -> group.Members)
            |> List.filter (fun entry -> entry.ExecutionRepository <> localRepository && not (located.Contains entry.WorkItemId))
            |> List.map (fun entry -> entry.WorkItemId, entry.ExecutionRepository)
            |> List.distinctBy fst

        { configuration with
            Grouping =
                { grouping with
                    Groups = grouping.Groups @ (added |> List.map declared)
                    ExecutionRepositories =
                        grouping.ExecutionRepositories @ repositories
                        |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right)) } }

    /// Structural checks over every stored group (`validate`). Terminal
    /// members are expected (partial completion, PRX-GRP-042); unknown ones
    /// are not.
    let validate (catalog: WorkCatalog) (groups: StoredWorkGroup list) : GroupFinding list =
        let finding (group: StoredWorkGroup) field message =
            { GroupId = group.Id
              Field = field
              Message = message }

        let duplicates =
            repeated (groups |> List.map (fun group -> group.Id))
            |> List.map (fun id -> { GroupId = id; Field = "id"; Message = $"group '{id}' is declared more than once" })

        let perGroup (group: StoredWorkGroup) =
            let ids = memberIds group

            [ if not (isValidId group.Id) then
                  finding group "id" $"'{group.Id}' is not a valid group ID (GROUP-<AREA>-<SEQUENCE>)"
              if String.IsNullOrWhiteSpace group.ExecutionRepository then
                  finding group "executionRepository" "executionRepository is blank"
              yield! repeated ids |> List.map (fun id -> finding group "members" $"member '{id}' is listed more than once")
              yield!
                  ids
                  |> List.distinct
                  |> List.filter (catalog.Items.ContainsKey >> not)
                  |> List.map (fun id -> finding group "members" $"member '{id}' is not a work item this repository tracks")
              if not group.CrossRepository then
                  yield!
                      group.Members
                      |> List.filter (fun entry -> entry.ExecutionRepository <> group.ExecutionRepository)
                      |> List.map (fun entry ->
                          finding group "members" $"member '{entry.WorkItemId}' executes in {entry.ExecutionRepository} but the group is repository-local to {group.ExecutionRepository}")
              match group.History with
              | { Change = GroupChange.Created } :: _ -> ()
              | _ -> finding group "history" "history must begin with the group's creation"
              yield!
                  group.Checkpoints
                  |> List.collect (fun checkpoint ->
                      [ if String.IsNullOrWhiteSpace checkpoint.Summary then
                            finding group "checkpoints" $"checkpoint {checkpoint.Id} has a blank summary"
                        if String.IsNullOrWhiteSpace checkpoint.NextAction then
                            finding group "checkpoints" $"checkpoint {checkpoint.Id} has a blank next action"
                        if checkpoint.Location.LocalCommit <> checkpoint.Location.RemoteCommit then
                            finding group "checkpoints" $"checkpoint {checkpoint.Id} commit and remote commit differ; a group checkpoint is accepted only when they are equal" ]) ]

        duplicates @ (groups |> List.collect perGroup)

/// One member as `work group show` reports it: its own recorded state, the
/// planner's view of it, and the work it holds up when blocked.
type GroupMemberView =
    { Membership: GroupMembership
      /// `None` when the item is no longer tracked (`validate` reports it).
      RecordedState: RecordedWorkState option
      /// `None` when the planner could not be consulted.
      PlanningState: PlanningWorkState option
      BlockReason: string option
      LatestCheckpointId: string option
      /// Open items that wait directly on this member (PRX-PLAN-046).
      Gates: string list }

/// PRX-GRP-042: partial completion, counted per member, never rounded up.
type StoredGroupProgress =
    { Members: int
      Complete: int
      Abandoned: int
      Active: int
      Blocked: int
      Remaining: int
      Unknown: int }

type GroupView =
    { Group: StoredWorkGroup
      Members: GroupMemberView list
      Progress: StoredGroupProgress
      PlanningAvailable: bool
      LatestCheckpoint: GroupCheckpoint option }

[<RequireQualifiedAccess>]
module GroupView =
    let private isBlocked (view: GroupMemberView) =
        view.RecordedState = Some(RecordedWorkState.Live LiveWorkState.Blocked)
        || view.PlanningState = Some PlanningWorkState.Blocked

    let blocked (view: GroupView) = view.Members |> List.filter isBlocked

    let private progress (members: GroupMemberView list) =
        let count predicate = members |> List.filter predicate |> List.length
        let recorded code (view: GroupMemberView) = view.RecordedState |> Option.map RecordedWorkState.code = Some code

        let complete = count (recorded "complete")
        let abandoned = count (recorded "abandoned")

        { Members = members.Length
          Complete = complete
          Abandoned = abandoned
          Active = count (fun view -> recorded "active" view && not (isBlocked view))
          Blocked = count isBlocked
          Remaining = members.Length - complete - abandoned
          Unknown = count (fun view -> view.RecordedState.IsNone) }

    /// The read model of one stored group. `analysis` is the planner's view
    /// of the repository when it could be computed.
    let build (catalog: WorkCatalog) (analysis: PlanningAnalysis option) (group: StoredWorkGroup) : GroupView =
        let items = analysis |> Option.map (fun value -> value.Items |> List.map (fun item -> item.Id, item) |> Map.ofList) |> Option.defaultValue Map.empty
        let unlocks = analysis |> Option.map (fun value -> value.Unlocks |> List.map (fun unlock -> unlock.WorkItem, unlock.DirectlyUnlocks) |> Map.ofList) |> Option.defaultValue Map.empty

        let members =
            group.Members
            |> List.map (fun entry ->
                let known = catalog.Items.TryFind entry.WorkItemId
                let planned = items.TryFind entry.WorkItemId

                { Membership = entry
                  RecordedState = known |> Option.map (fun item -> item.State)
                  PlanningState = planned |> Option.map (fun item -> item.PlanningState)
                  BlockReason = planned |> Option.bind (fun item -> item.BlockReason)
                  LatestCheckpointId = known |> Option.bind (fun item -> item.LatestCheckpointId)
                  Gates = unlocks.TryFind entry.WorkItemId |> Option.defaultValue [] })

        { Group = group
          Members = members
          Progress = progress members
          PlanningAvailable = analysis.IsSome
          LatestCheckpoint = group.Checkpoints |> List.tryLast }

    /// "2 of 5 complete, ...": never implies every member succeeded.
    let describeProgress (progress: StoredGroupProgress) =
        let parts =
            [ $"{progress.Complete} of {progress.Members} complete"
              if progress.Abandoned > 0 then $"{progress.Abandoned} abandoned"
              if progress.Active > 0 then $"{progress.Active} active"
              if progress.Blocked > 0 then $"{progress.Blocked} blocked"
              $"{progress.Remaining} remaining"
              if progress.Unknown > 0 then $"{progress.Unknown} no longer tracked" ]

        String.concat ", " parts
