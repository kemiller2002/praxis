namespace Ros.Domain.Work

open System
open System.Text.RegularExpressions
open Ros.Domain.Planning
open Ros.Domain.Provenance

// Declared execution groups held in Praxis state (PRX-GRP-073, phase two).
// Every decision the `work group` commands make lives here, as pure
// functions over values the infrastructure has already read. A group never
// holds a member's lifecycle state, evidence, paths or attribution: those
// stay with the member (PRX-GRP-041/043).

/// What the group decisions need to know about one work item, read from the
/// backlog queue and the live context.
type GroupMemberFacts =
    { WorkItemId: string
      /// The live-context state when the item is live, else its backlog status.
      RecordedState: string
      /// `complete` or `abandoned` in either the queue or the live context.
      Terminal: bool
      /// Where the item is implemented: its planner
      /// `grouping.executionRepositories` entry, else this repository.
      ExecutionRepository: string }

[<RequireQualifiedAccess>]
type GroupHistoryOperation = | Created

[<RequireQualifiedAccess>]
module GroupHistoryOperation =
    let code operation =
        match operation with
        | GroupHistoryOperation.Created -> "created"

    let tryParse value =
        match value with
        | "created" -> Some GroupHistoryOperation.Created
        | _ -> None

/// One append-only membership fact, with who caused it (provenance).
type GroupHistoryEntry =
    { Operation: GroupHistoryOperation
      WorkItemIds: string list
      At: string
      Actor: Actor
      Reason: string option }

/// A declared group as stored in `.ros/work/groups.json`.
type StoredGroup =
    { Id: string
      Kind: GroupKind option
      Origin: GroupOrigin
      ExecutionRepository: string
      CrossRepository: bool
      SharedContext: string list
      ArchitectureNotes: string list
      /// Current members, in the order they joined.
      Members: string list
      CreatedAt: string
      CreatedBy: Actor
      History: GroupHistoryEntry list }

type StoredGroups =
    { Repository: string
      Groups: StoredGroup list }

/// What `work group create` asks for. Nothing here is trusted.
type GroupDeclaration =
    { Id: string
      Kind: GroupKind option
      ExecutionRepository: string
      CrossRepository: bool
      SharedContext: string list
      ArchitectureNotes: string list
      Members: string list
      OccurredAt: string
      Actor: Actor
      Reason: string option }

/// One member as `work group show` presents it (PRAXIS-GROUP-02): its own
/// recorded state, the planner's reading of it, and the blocked members
/// that gate it or that it gates. Nothing here is the group's state.
type GroupMemberView =
    { WorkItemId: string
      RecordedState: string
      PlanningState: string
      Status: string
      GatedBy: string list
      Gates: string list }

type GroupProgressView =
    { Total: int
      Complete: int
      InProgress: int
      Runnable: int
      Blocked: int
      NotRunnable: int
      Unknown: int
      Statement: string }

type GroupView =
    { Group: StoredGroup
      Members: GroupMemberView list
      Progress: GroupProgressView
      /// Blocked members with the members each one gates.
      Blocked: (string * string list) list }

[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of groupId: string
    | InvalidWorkItemId of workItemId: string
    | NoMembers
    | RepeatedMember of workItemId: string
    | GroupExists of groupId: string
    | UnknownWorkItem of workItemId: string
    | TerminalWorkItem of workItemId: string * state: string
    | RepositoryMismatch of workItemId: string * itemRepository: string * groupRepository: string
    | UnknownGroup of groupId: string

[<RequireQualifiedAccess>]
module GroupRejection =
    /// Stable, machine-readable causes. These codes are a public contract.
    let code rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupRejection.InvalidWorkItemId _ -> "invalid-work-item-id"
        | GroupRejection.NoMembers -> "no-members"
        | GroupRejection.RepeatedMember _ -> "repeated-member"
        | GroupRejection.GroupExists _ -> "group-exists"
        | GroupRejection.UnknownWorkItem _ -> "unknown-work-item"
        | GroupRejection.TerminalWorkItem _ -> "terminal-work-item"
        | GroupRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupRejection.UnknownGroup _ -> "unknown-group"

    let message rejection =
        match rejection with
        | GroupRejection.InvalidGroupId id -> $"'{id}' is not a group ID (expected GROUP-<AREA>-<SEQUENCE>, upper-case letters, digits and hyphens)"
        | GroupRejection.InvalidWorkItemId id -> $"'{id}' is not a valid work-item ID"
        | GroupRejection.NoMembers -> "a group needs at least one member"
        | GroupRejection.RepeatedMember id -> $"{id} is named more than once"
        | GroupRejection.GroupExists id -> $"group {id} already exists"
        | GroupRejection.UnknownWorkItem id -> $"{id} is not a work item in this repository's backlog or live context"
        | GroupRejection.TerminalWorkItem(id, state) -> $"{id} is {state}; a terminal item has nothing left to execute and cannot join a group"
        | GroupRejection.RepositoryMismatch(id, item, group) ->
            $"{id} executes in {item}, but the group executes in {group} and is not cross-repository (PRX-GRP-051)"
        | GroupRejection.UnknownGroup id -> $"no declared group {id} is stored in .ros/work/groups.json"

    let remedy rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _ -> "choose an ID such as GROUP-PRAXIS-PLANNING-001"
        | GroupRejection.InvalidWorkItemId _ -> "pass the work item's ID exactly as 'work list' shows it"
        | GroupRejection.NoMembers -> "name each member with --member ID"
        | GroupRejection.RepeatedMember _ -> "name each member once"
        | GroupRejection.GroupExists _ -> "choose another ID, or change the existing group with 'work group add' or 'work group remove'"
        | GroupRejection.UnknownWorkItem _ -> "capture the item first ('ros add'), or check the ID with 'work list'"
        | GroupRejection.TerminalWorkItem _ -> "leave the item out; its own record keeps its completion"
        | GroupRejection.RepositoryMismatch _ -> "declare the group with --cross-repository, or group the item with work in its own repository"
        | GroupRejection.UnknownGroup _ -> "check the ID in .ros/work/groups.json, or declare the group with 'work group create'"

    /// Rejections the caller can fix by correcting the command line (exit 2).
    let isArgumentError rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _
        | GroupRejection.InvalidWorkItemId _
        | GroupRejection.NoMembers
        | GroupRejection.RepeatedMember _ -> true
        | _ -> false

/// One problem with the stored groups, for `validate`.
type GroupFinding = { Field: string; Message: string }

[<RequireQualifiedAccess>]
module WorkGroups =
    let private groupIdPattern =
        Regex("^GROUP-[A-Z0-9]+(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant)

    let isValidGroupId (value: string) = groupIdPattern.IsMatch value

    let empty repository = { Repository = repository; Groups = [] }

    let tryFind (groupId: string) (stored: StoredGroups) =
        stored.Groups |> List.tryFind (fun group -> group.Id = groupId)

    let private liveCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"
        | LiveWorkState.Abandoned -> "abandoned"

    /// Assembles every known work item's facts from the backlog queue
    /// (ID, status) and the live context (ID, state). An item is executed in
    /// its `executionRepositories` entry, else in `localRepository` (the
    /// planner's own rule for explicit and derived locations; analysis D4).
    let memberFacts
        (localRepository: string)
        (executionRepositories: (string * string) list)
        (queue: (string * string) list)
        (live: (string * LiveWorkState) list)
        : Map<string, GroupMemberFacts> =
        let queued = Map.ofList queue
        let current = Map.ofList live
        let repositories = Map.ofList executionRepositories

        Set.union (Set.ofSeq queued.Keys) (Set.ofSeq current.Keys)
        |> Seq.map (fun id ->
            let status = queued.TryFind id
            let state = current.TryFind id

            let terminal =
                (status |> Option.exists (fun value -> value = "complete" || value = "abandoned"))
                || (state |> Option.exists (fun value -> value = LiveWorkState.Complete || value = LiveWorkState.Abandoned))

            id,
            { WorkItemId = id
              RecordedState = state |> Option.map liveCode |> Option.orElse status |> Option.defaultValue "unknown"
              Terminal = terminal
              ExecutionRepository = repositories.TryFind id |> Option.defaultValue localRepository })
        |> Map.ofSeq

    /// The single admission rule for a would-be member (analysis D3, D4):
    /// known, not terminal, and executing in the group's repository unless
    /// the group is cross-repository.
    let admit (groupRepository: string) (crossRepository: bool) (facts: Map<string, GroupMemberFacts>) (workItemId: string) : GroupRejection list =
        if not (WorkItemId.isValid workItemId) then
            [ GroupRejection.InvalidWorkItemId workItemId ]
        else
            match facts.TryFind workItemId with
            | None -> [ GroupRejection.UnknownWorkItem workItemId ]
            | Some item ->
                [ if item.Terminal then
                      GroupRejection.TerminalWorkItem(workItemId, item.RecordedState)
                  if not crossRepository && item.ExecutionRepository <> groupRepository then
                      GroupRejection.RepositoryMismatch(workItemId, item.ExecutionRepository, groupRepository) ]

    let private repeated (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    /// Decides `work group create`. Returns the stored groups with the new
    /// group appended, and the new group. Every independent problem is
    /// reported together; nothing about any member changes.
    let create (stored: StoredGroups) (facts: Map<string, GroupMemberFacts>) (declaration: GroupDeclaration) : Result<StoredGroups * StoredGroup, GroupRejection list> =
        let members = declaration.Members |> List.distinct

        let rejections =
            [ if not (isValidGroupId declaration.Id) then
                  GroupRejection.InvalidGroupId declaration.Id
              elif (tryFind declaration.Id stored).IsSome then
                  GroupRejection.GroupExists declaration.Id
              if declaration.Members.IsEmpty then
                  GroupRejection.NoMembers
              yield! repeated declaration.Members |> List.map GroupRejection.RepeatedMember
              yield! members |> List.collect (admit declaration.ExecutionRepository declaration.CrossRepository facts) ]

        match rejections with
        | [] ->
            let group =
                { Id = declaration.Id
                  Kind = declaration.Kind
                  Origin = GroupOrigin.HumanDeclared
                  ExecutionRepository = declaration.ExecutionRepository
                  CrossRepository = declaration.CrossRepository
                  SharedContext = declaration.SharedContext
                  ArchitectureNotes = declaration.ArchitectureNotes
                  Members = members
                  CreatedAt = declaration.OccurredAt
                  CreatedBy = declaration.Actor
                  History =
                    [ { Operation = GroupHistoryOperation.Created
                        WorkItemIds = members
                        At = declaration.OccurredAt
                        Actor = declaration.Actor
                        Reason = declaration.Reason } ] }

            Ok({ stored with Groups = stored.Groups @ [ group ] }, group)
        | problems -> Error problems

    /// The only projection of a stored group into the planner (analysis D6):
    /// the planner reads it exactly as it reads `grouping.groups`.
    let toDeclared (group: StoredGroup) : DeclaredGroup =
        { Id = group.Id
          Members = group.Members
          Kind = group.Kind
          Origin = group.Origin
          SharedContext = group.SharedContext
          ExecutionRepository = Some group.ExecutionRepository
          CrossRepository = group.CrossRepository
          ArchitectureNotes = group.ArchitectureNotes }

    /// Adds stored groups to a planner grouping configuration. A group the
    /// configuration already declares by ID is the caller's explicit
    /// per-run input and shadows the stored one (analysis R5).
    let mergeInto (stored: StoredGroup list) (grouping: GroupingConfiguration) : GroupingConfiguration =
        let declared = grouping.Groups |> List.map (fun group -> group.Id) |> Set.ofList

        { grouping with
            Groups = grouping.Groups @ (stored |> List.filter (fun group -> not (declared.Contains group.Id)) |> List.map toDeclared) }

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) |> fst

    /// Offline checks of the stored groups (analysis §10, D13). A member that
    /// became terminal after it joined is partial completion, not a finding.
    let validate (known: Set<string>) (stored: StoredGroups) : GroupFinding list =
        let duplicateIds = stored.Groups |> List.map (fun group -> group.Id) |> repeated

        [ for id in duplicateIds do
              { Field = "groups"; Message = $"group ID {id} is declared more than once" }
          for group in stored.Groups do
              let field name = $"groups[{group.Id}].{name}"

              if not (isValidGroupId group.Id) then
                  { Field = "groups"; Message = $"'{group.Id}' is not a valid group ID" }
              if group.Members.IsEmpty then
                  { Field = field "members"; Message = "a group needs at least one member" }
              for id in repeated group.Members do
                  { Field = field "members"; Message = $"{id} is listed more than once" }
              for id in group.Members |> List.distinct do
                  if not (known.Contains id) then
                      { Field = field "members"; Message = $"{id} is not a work item in the backlog or live context" }
              if not (isTimestamp group.CreatedAt) then
                  { Field = field "createdAt"; Message = $"'{group.CreatedAt}' is not a timestamp" }
              if String.IsNullOrWhiteSpace group.ExecutionRepository then
                  { Field = field "executionRepository"; Message = "the execution repository is required (PRX-GRP-051)" }
              if group.History |> List.forall (fun entry -> entry.Operation <> GroupHistoryOperation.Created) then
                  { Field = field "history"; Message = "the group has no 'created' history entry" }
              for entry in group.History do
                  if not (isTimestamp entry.At) then
                      { Field = field "history"; Message = $"'{entry.At}' is not a timestamp" } ]

    /// The read-only view of one stored group (PRAXIS-GROUP-02). Member
    /// states come from the member records and from the planner's own group
    /// for the same ID (`Grouping.recommend` over the merged configuration,
    /// analysis D7); a member the planner cannot see is reported `unknown`,
    /// never guessed. Progress is partial by design: it never implies that
    /// every member succeeded (PRX-GRP-042).
    let view (facts: Map<string, GroupMemberFacts>) (planned: WorkGroup option) (group: StoredGroup) : GroupView =
        let plannedMembers =
            planned |> Option.map (fun value -> value.Members |> List.map (fun entry -> entry.WorkItemId, entry) |> Map.ofList) |> Option.defaultValue Map.empty

        let rows =
            group.Members
            |> List.map (fun id ->
                let recorded = facts.TryFind id |> Option.map (fun item -> item.RecordedState) |> Option.defaultValue "unknown"

                match plannedMembers.TryFind id with
                | Some entry ->
                    { WorkItemId = id
                      RecordedState = recorded
                      PlanningState = PlanningWorkState.code entry.PlanningState
                      Status = MemberStatus.code entry.Status
                      GatedBy = entry.GatedBy |> List.filter (fun other -> List.contains other group.Members)
                      Gates = [] }
                | None ->
                    { WorkItemId = id
                      RecordedState = recorded
                      PlanningState = "unknown"
                      Status = "unknown"
                      GatedBy = []
                      Gates = [] })

        let gates id = rows |> List.filter (fun row -> List.contains id row.GatedBy) |> List.map (fun row -> row.WorkItemId)
        let members = rows |> List.map (fun row -> { row with Gates = gates row.WorkItemId })
        let count status = members |> List.filter (fun row -> row.Status = status) |> List.length
        let blocked = members |> List.filter (fun row -> row.Status = MemberStatus.code MemberStatus.Blocked)
        let complete = count (MemberStatus.code MemberStatus.Complete)

        let blockedText =
            match blocked with
            | [] -> ""
            | _ ->
                let names = blocked |> List.map (fun row -> row.WorkItemId) |> String.concat ", "
                $"; blocked: {names}"

        { Group = group
          Members = members
          Progress =
            { Total = members.Length
              Complete = complete
              InProgress = count (MemberStatus.code MemberStatus.InProgress)
              Runnable = count (MemberStatus.code MemberStatus.Runnable)
              Blocked = blocked.Length
              NotRunnable = count (MemberStatus.code MemberStatus.NotRunnable)
              Unknown = count "unknown"
              Statement =
                $"{complete} of {members.Length} complete{blockedText}; each member completes on its own evidence and the group never implies that every member succeeded (PRX-GRP-042)" }
          Blocked = blocked |> List.map (fun row -> row.WorkItemId, row.Gates) }
