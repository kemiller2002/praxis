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
type GroupHistoryOperation =
    | Created
    | MemberAdded
    | MemberRemoved

[<RequireQualifiedAccess>]
module GroupHistoryOperation =
    let code operation =
        match operation with
        | GroupHistoryOperation.Created -> "created"
        | GroupHistoryOperation.MemberAdded -> "member-added"
        | GroupHistoryOperation.MemberRemoved -> "member-removed"

    let tryParse value =
        match value with
        | "created" -> Some GroupHistoryOperation.Created
        | "member-added" -> Some GroupHistoryOperation.MemberAdded
        | "member-removed" -> Some GroupHistoryOperation.MemberRemoved
        | _ -> None

/// One append-only membership fact, with who caused it (provenance).
type GroupHistoryEntry =
    { Operation: GroupHistoryOperation
      WorkItemIds: string list
      At: string
      Actor: Actor
      Reason: string option }

/// A member's own latest durable checkpoint, referenced (never copied or
/// replaced) by a group checkpoint (PRAXIS-GROUP-05, PRX-GRP-044).
type GroupMemberCheckpoint =
    { WorkItemId: string
      CheckpointId: string option
      Commit: string option
      ExecutionId: string option }

/// A group checkpoint: the verified durable location plus the group's
/// progress and shared decisions at that moment. It claims no paths and no
/// member's work (PRX-GRP-043); members keep their own checkpoints.
type GroupCheckpoint =
    { CheckpointId: string
      RecordedAt: string
      Actor: Actor
      Summary: string
      NextAction: string
      Decisions: string list
      Repository: string
      Branch: string
      Commit: string
      Remote: string
      RemoteBranch: string
      ActiveMembers: string list
      CompletedMembers: string list
      RemainingMembers: string list
      MemberCheckpoints: GroupMemberCheckpoint list }

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
      History: GroupHistoryEntry list
      /// Append-only group checkpoints (PRAXIS-GROUP-05).
      Checkpoints: GroupCheckpoint list }

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

/// What `work group add` (and later `remove`) asks for: one work item, one
/// group, with who asked and why. Nothing here is trusted.
type GroupMembershipChange =
    { GroupId: string
      WorkItemId: string
      OccurredAt: string
      Actor: Actor
      Reason: string option }

/// What `work group checkpoint` asks for. Nothing here is trusted.
type GroupCheckpointRequest =
    { GroupId: string
      Summary: string
      NextAction: string
      Decisions: string list
      OccurredAt: string
      Actor: Actor }

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
    | AlreadyMember of groupId: string * workItemId: string
    | NotMember of groupId: string * workItemId: string
    | LastMember of groupId: string * workItemId: string
    | NotDurable of CheckpointRejection
    | NothingRemaining of groupId: string

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
        | GroupRejection.AlreadyMember _ -> "already-member"
        | GroupRejection.NotMember _ -> "not-member"
        | GroupRejection.LastMember _ -> "last-member"
        | GroupRejection.NotDurable rejection -> CheckpointRejection.code rejection
        | GroupRejection.NothingRemaining _ -> "nothing-remaining"

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
        | GroupRejection.AlreadyMember(group, id) -> $"{id} is already a member of {group}"
        | GroupRejection.NotMember(group, id) -> $"{id} is not a member of {group}"
        | GroupRejection.LastMember(group, id) -> $"{id} is the last member of {group}; a group needs at least one member"
        | GroupRejection.NotDurable rejection -> CheckpointRejection.message rejection
        | GroupRejection.NothingRemaining group -> $"every member of {group} is complete or abandoned; there is no remaining work to checkpoint"

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
        | GroupRejection.AlreadyMember _ -> "nothing to do; 'work group show' lists the members"
        | GroupRejection.NotMember _ -> "nothing to do; 'work group show' lists the members"
        | GroupRejection.LastMember _ -> "add the replacement member first; dissolving a group is not supported"
        | GroupRejection.NotDurable rejection -> CheckpointRejection.remedy rejection
        | GroupRejection.NothingRemaining _ -> "each member's own completion is already recorded; no group checkpoint is needed"

    /// Rejections the caller can fix by correcting the command line (exit 2).
    let isArgumentError rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _
        | GroupRejection.InvalidWorkItemId _
        | GroupRejection.NoMembers
        | GroupRejection.RepeatedMember _ -> true
        | GroupRejection.NotDurable rejection -> CheckpointRejection.isArgumentError rejection
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
                  Checkpoints = []
                  History =
                    [ { Operation = GroupHistoryOperation.Created
                        WorkItemIds = members
                        At = declaration.OccurredAt
                        Actor = declaration.Actor
                        Reason = declaration.Reason } ] }

            Ok({ stored with Groups = stored.Groups @ [ group ] }, group)
        | problems -> Error problems

    /// Finds the group a membership change names, and replaces it after a
    /// decision; shared by `add` and `remove`.
    let private changeGroup
        (stored: StoredGroups)
        (change: GroupMembershipChange)
        (decide: StoredGroup -> Result<StoredGroup, GroupRejection list>)
        : Result<StoredGroups * StoredGroup, GroupRejection list> =
        let argumentProblems =
            [ if not (isValidGroupId change.GroupId) then
                  GroupRejection.InvalidGroupId change.GroupId
              if not (WorkItemId.isValid change.WorkItemId) then
                  GroupRejection.InvalidWorkItemId change.WorkItemId ]

        match argumentProblems, tryFind change.GroupId stored with
        | _ :: _, _ -> Error argumentProblems
        | [], None -> Error [ GroupRejection.UnknownGroup change.GroupId ]
        | [], Some group ->
            decide group
            |> Result.map (fun changed ->
                { stored with Groups = stored.Groups |> List.map (fun candidate -> if candidate.Id = changed.Id then changed else candidate) }, changed)

    let private historyOf (operation: GroupHistoryOperation) (change: GroupMembershipChange) =
        { Operation = operation
          WorkItemIds = [ change.WorkItemId ]
          At = change.OccurredAt
          Actor = change.Actor
          Reason = change.Reason }

    /// Decides `work group add` (PRAXIS-GROUP-03): the same admission rule
    /// as `create`, plus "not already a member". Records who added it; the
    /// item itself is untouched.
    let addMember (stored: StoredGroups) (facts: Map<string, GroupMemberFacts>) (change: GroupMembershipChange) =
        changeGroup stored change (fun group ->
            match List.contains change.WorkItemId group.Members, admit group.ExecutionRepository group.CrossRepository facts change.WorkItemId with
            | true, _ -> Error [ GroupRejection.AlreadyMember(group.Id, change.WorkItemId) ]
            | false, (_ :: _ as problems) -> Error problems
            | false, [] ->
                Ok
                    { group with
                        Members = group.Members @ [ change.WorkItemId ]
                        History = group.History @ [ historyOf GroupHistoryOperation.MemberAdded change ] })

    /// Decides `work group remove` (PRAXIS-GROUP-04): refuses a non-member
    /// and the last member (analysis D8). Only the membership changes; the
    /// item's lifecycle, evidence and attribution live in its own records
    /// and are never touched. Records who removed it.
    let removeMember (stored: StoredGroups) (_: Map<string, GroupMemberFacts>) (change: GroupMembershipChange) =
        changeGroup stored change (fun group ->
            match group.Members with
            | members when not (List.contains change.WorkItemId members) -> Error [ GroupRejection.NotMember(group.Id, change.WorkItemId) ]
            | [ _ ] -> Error [ GroupRejection.LastMember(group.Id, change.WorkItemId) ]
            | members ->
                Ok
                    { group with
                        Members = members |> List.filter ((<>) change.WorkItemId)
                        History = group.History @ [ historyOf GroupHistoryOperation.MemberRemoved change ] })

    let private hex (value: string) =
        value
        |> Text.Encoding.UTF8.GetBytes
        |> Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString
        |> fun text -> text.ToLowerInvariant()

    /// The content address of a group checkpoint: every recorded field but
    /// the ID itself, so `validate` can detect an edited record.
    let checkpointId (value: GroupCheckpoint) =
        let memberRefs =
            value.MemberCheckpoints
            |> List.map (fun reference ->
                [ reference.WorkItemId
                  defaultArg reference.CheckpointId ""
                  defaultArg reference.Commit ""
                  defaultArg reference.ExecutionId "" ]
                |> String.concat "\u001f")

        [ yield value.RecordedAt
          yield ActorKind.code value.Actor.Kind
          yield value.Actor.Id
          yield value.Summary
          yield value.NextAction
          yield! value.Decisions
          yield value.Repository
          yield value.Branch
          yield value.Commit
          yield value.Remote
          yield value.RemoteBranch
          yield String.concat "," value.ActiveMembers
          yield String.concat "," value.CompletedMembers
          yield String.concat "," value.RemainingMembers
          yield! memberRefs ]
        |> String.concat "\u001e"
        |> hex
        |> fun digest -> "GCP-" + digest.Substring(0, 24)

    /// Decides `work group checkpoint` (PRAXIS-GROUP-05). `location` is the
    /// result of the same durable-checkpoint verification `work checkpoint`
    /// uses (`CheckpointVerification.durableLocation`); `view` is the group
    /// as `show` presents it (analysis D7); `memberCheckpoints` are the
    /// members' own latest verified checkpoints. The group checkpoint only
    /// references them: no member's checkpoint, state or paths change, and no
    /// member claims another's work (PRX-GRP-043).
    let checkpoint
        (stored: StoredGroups)
        (view: GroupView)
        (location: Result<GitDurableLocation, CheckpointRejection list>)
        (memberCheckpoints: Map<string, CheckpointSummary>)
        (request: GroupCheckpointRequest)
        : Result<StoredGroups * GroupCheckpoint, GroupRejection list> =
        let group = view.Group
        let complete = MemberStatus.code MemberStatus.Complete
        let remaining = view.Members |> List.filter (fun row -> row.Status <> complete) |> List.map (fun row -> row.WorkItemId)

        let rejections =
            [ match location with
              | Error problems -> yield! problems |> List.map GroupRejection.NotDurable
              | Ok _ -> ()
              if remaining.IsEmpty then
                  GroupRejection.NothingRemaining group.Id ]

        match rejections, location, tryFind request.GroupId stored with
        | _, _, None -> Error [ GroupRejection.UnknownGroup request.GroupId ]
        | [], Ok git, Some current ->
            let recorded =
                { CheckpointId = ""
                  RecordedAt = request.OccurredAt
                  Actor = request.Actor
                  Summary = request.Summary.Trim()
                  NextAction = request.NextAction.Trim()
                  Decisions = request.Decisions |> List.map (fun decision -> decision.Trim()) |> List.filter (String.IsNullOrWhiteSpace >> not)
                  Repository = git.Repository
                  Branch = git.Branch
                  Commit = git.LocalCommit.Value
                  Remote = git.Remote.Name
                  RemoteBranch = git.RemoteBranch
                  ActiveMembers =
                    view.Members
                    |> List.filter (fun row -> row.RecordedState = "active" || row.Status = MemberStatus.code MemberStatus.InProgress)
                    |> List.map (fun row -> row.WorkItemId)
                  CompletedMembers = view.Members |> List.filter (fun row -> row.RecordedState = "complete") |> List.map (fun row -> row.WorkItemId)
                  RemainingMembers = remaining
                  MemberCheckpoints =
                    current.Members
                    |> List.map (fun id ->
                        match memberCheckpoints.TryFind id with
                        | Some summary ->
                            { WorkItemId = id
                              CheckpointId = Some summary.CheckpointId
                              Commit = Some summary.Commit
                              ExecutionId = Some summary.ExecutionId }
                        | None ->
                            { WorkItemId = id
                              CheckpointId = None
                              Commit = None
                              ExecutionId = None }) }

            let identified = { recorded with CheckpointId = checkpointId recorded }
            let changed = { current with Checkpoints = current.Checkpoints @ [ identified ] }
            Ok({ stored with Groups = stored.Groups |> List.map (fun candidate -> if candidate.Id = changed.Id then changed else candidate) }, identified)
        | [], Error _, Some _ -> Error [ GroupRejection.NotDurable(CheckpointRejection.UnknownGitState "the checkpoint could not be verified") ]
        | problems, _, Some _ -> Error problems

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

    /// The members the append-only history implies, in joining order.
    let membersFromHistory (history: GroupHistoryEntry list) =
        history
        |> List.fold
            (fun members entry ->
                match entry.Operation with
                | GroupHistoryOperation.Created -> entry.WorkItemIds
                | GroupHistoryOperation.MemberAdded -> members @ (entry.WorkItemIds |> List.filter (fun id -> not (List.contains id members)))
                | GroupHistoryOperation.MemberRemoved -> members |> List.filter (fun id -> not (List.contains id entry.WorkItemIds)))
            []

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
              if membersFromHistory group.History <> group.Members then
                  { Field = field "members"; Message = "the members do not match the membership history (every membership change must be recorded with its actor)" }
              for entry in group.History do
                  if not (isTimestamp entry.At) then
                      { Field = field "history"; Message = $"'{entry.At}' is not a timestamp" }
              for recorded in group.Checkpoints do
                  let where = field $"checkpoints[{recorded.CheckpointId}]"

                  if checkpointId recorded <> recorded.CheckpointId then
                      { Field = where; Message = "the checkpoint ID does not match its content (a recorded checkpoint is never edited)" }
                  if not (Regex.IsMatch(recorded.Commit, "^[0-9a-f]{40}([0-9a-f]{24})?$")) then
                      { Field = where; Message = $"'{recorded.Commit}' is not a full commit ID" }
                  if not (isTimestamp recorded.RecordedAt) then
                      { Field = where; Message = $"'{recorded.RecordedAt}' is not a timestamp" }
                  if String.IsNullOrWhiteSpace recorded.Summary || String.IsNullOrWhiteSpace recorded.NextAction then
                      { Field = where; Message = "a group checkpoint needs a summary and a next action" } ]

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
