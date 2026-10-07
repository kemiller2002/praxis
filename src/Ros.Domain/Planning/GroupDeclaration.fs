namespace Ros.Domain.Planning

open System
open System.Text.RegularExpressions
open Ros.Domain.Work

// Phase two of requirements/PLANNING-WORK-GROUPS.md (PRX-GRP-073): a human
// declares, durably in Praxis state, that work items belong together. The
// declaration is data only. It never changes a member's lifecycle state
// (PRX-GRP-002) and never collapses members into one item (PRX-GRP-003).
// Everything here is pure: no clock, filesystem or Git.

/// Who added one member to an existing declaration, and when
/// (`work group add`). Members named at `create` are covered by the
/// declaration's own `DeclaredBy`.
type MemberAddition =
    { WorkItem: string
      AddedAt: string
      AddedBy: string }

/// Who removed one member from a declaration, when, and why
/// (`work group remove`). The member's earlier addition stays recorded.
type MemberRemoval =
    { WorkItem: string
      RemovedAt: string
      RemovedBy: string
      Reason: string option }

/// What a group checkpoint saw of one member's own checkpoint history: a
/// reference to its latest durable checkpoint, never a copy or replacement.
[<RequireQualifiedAccess>]
type MemberCheckpointReference =
    | Latest of checkpointId: string * commit: string * recordedAt: string
    | NoneRecorded
    | Unreadable of problems: string list

type MemberCheckpoint =
    { WorkItem: string
      Reference: MemberCheckpointReference }

/// Where each member stood when a group checkpoint was recorded. Abandoned
/// and unknown members are kept apart from completed ones, so a group
/// checkpoint never implies that every member succeeded (PRX-GRP-042).
type GroupMemberProgress =
    { Active: string list
      Completed: string list
      Remaining: string list
      Abandoned: string list
      Unknown: string list }

/// The durable Git location a group checkpoint verified, as observed.
type GroupCheckpointLocation =
    { Repository: string
      Branch: string
      Commit: string
      Remote: string
      RemoteUrl: string option
      RemoteBranch: string }

/// A group-level checkpoint (PRX-GRP-044): a verified durable location plus
/// the group's progress, shared decisions and next action. It references
/// members' own checkpoints and claims no paths for any member, so member
/// attribution stays with each member's own checkpoints (PRX-GRP-043).
type GroupCheckpoint =
    { CheckpointId: string
      RecordedAt: string
      RecordedBy: string
      Summary: string
      NextAction: string
      SharedDecisions: string list
      Members: GroupMemberProgress
      Location: GroupCheckpointLocation
      MemberCheckpoints: MemberCheckpoint list }

/// A declaration as stored in `.ros/work/groups.json`: the same
/// `DeclaredGroup` the planner reads from `grouping.groups`, plus who
/// declared it and when, who added each later member, who removed any, and
/// its group checkpoints (oldest first; never rewritten).
type StoredGroup =
    { Group: DeclaredGroup
      DeclaredAt: string
      DeclaredBy: string
      Additions: MemberAddition list
      Removals: MemberRemoval list
      Checkpoints: GroupCheckpoint list }

/// What a work item's recorded lifecycle says about group membership.
[<RequireQualifiedAccess>]
type GroupMemberStatus =
    | Open of state: string
    | Terminal of state: string

type GroupCreateRequest =
    { Id: string
      Members: string list
      Kind: string option
      ExecutionRepository: string option
      CrossRepository: bool
      SharedContext: string list
      ArchitectureNotes: string list
      DeclaredAt: string
      DeclaredBy: string }

type GroupAddRequest =
    { GroupId: string
      WorkItem: string
      AddedAt: string
      AddedBy: string }

type GroupRemoveRequest =
    { GroupId: string
      WorkItem: string
      RemovedAt: string
      RemovedBy: string
      Reason: string option }

type GroupCheckpointRequest =
    { GroupId: string
      Summary: string
      NextAction: string
      SharedDecisions: string list
      RecordedAt: string
      RecordedBy: string }

[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of id: string
    | DuplicateGroupId of id: string
    | TooFewMembers of count: int
    | DuplicateMember of workItem: string
    | UnknownMember of workItem: string
    | TerminalMember of workItem: string * state: string
    | UnknownKind of kind: string
    | EmptyValue of field: string
    | UnknownGroup of id: string
    | AlreadyMember of workItem: string * group: string
    | RepositoryMismatch of workItem: string * itemRepository: string * groupRepositories: string list
    | NotMember of workItem: string * group: string
    | LastMembers of workItem: string * group: string * remaining: int
    | NoActiveMember of group: string
    | BlankSharedDecision
    /// The same durable-checkpoint verification `work checkpoint` applies.
    | NotDurable of CheckpointRejection

[<RequireQualifiedAccess>]
module GroupRejection =
    let code rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupRejection.DuplicateGroupId _ -> "duplicate-group-id"
        | GroupRejection.TooFewMembers _ -> "too-few-members"
        | GroupRejection.DuplicateMember _ -> "duplicate-member"
        | GroupRejection.UnknownMember _ -> "unknown-member"
        | GroupRejection.TerminalMember _ -> "terminal-member"
        | GroupRejection.UnknownKind _ -> "unknown-kind"
        | GroupRejection.EmptyValue _ -> "empty-value"
        | GroupRejection.UnknownGroup _ -> "unknown-group"
        | GroupRejection.AlreadyMember _ -> "already-member"
        | GroupRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupRejection.NotMember _ -> "not-member"
        | GroupRejection.LastMembers _ -> "last-members"
        | GroupRejection.NoActiveMember _ -> "no-active-member"
        | GroupRejection.BlankSharedDecision -> "blank-shared-decision"
        | GroupRejection.NotDurable rejection -> CheckpointRejection.code rejection

    let message rejection =
        match rejection with
        | GroupRejection.InvalidGroupId id -> $"group ID '{id}' must look like GROUP-<AREA>-<SEQUENCE> (upper-case letters, digits and hyphens; PRX-GRP-010)"
        | GroupRejection.DuplicateGroupId id -> $"group {id} is already declared"
        | GroupRejection.TooFewMembers count -> $"a group needs at least two distinct members; {count} given"
        | GroupRejection.DuplicateMember id -> $"{id} is named more than once"
        | GroupRejection.UnknownMember id -> $"{id} is not a recorded work item (backlog or live context)"
        | GroupRejection.TerminalMember(id, state) -> $"{id} is {state}; terminal work cannot join a group"
        | GroupRejection.UnknownKind kind -> $"unknown group kind '{kind}'"
        | GroupRejection.EmptyValue field -> $"{field} cannot be empty"
        | GroupRejection.UnknownGroup id -> $"group {id} is not declared (see .ros/work/groups.json or 'work group create')"
        | GroupRejection.AlreadyMember(id, group) -> $"{id} is already a member of {group}"
        | GroupRejection.RepositoryMismatch(id, repository, repositories) ->
            $"""{id} executes in {repository} but the group executes in {String.concat " + " repositories}; only a cross-repository group may span execution repositories (PRX-GRP-051)"""
        | GroupRejection.NotMember(id, group) -> $"{id} is not a member of {group}"
        | GroupRejection.LastMembers(id, group, remaining) ->
            $"removing {id} would leave {group} with {remaining} member(s); a group needs at least two, so its last members cannot be removed"
        | GroupRejection.NoActiveMember group ->
            $"{group} has no active member; a group checkpoint records grouped execution in progress, so start a member first"
        | GroupRejection.BlankSharedDecision -> "--shared-decision cannot be empty"
        | GroupRejection.NotDurable rejection -> $"{CheckpointRejection.message rejection} ({CheckpointRejection.remedy rejection})"

[<RequireQualifiedAccess>]
module GroupDeclaration =
    let private idPattern = Regex(@"^GROUP-[A-Z0-9]+(?:-[A-Z0-9]+)+$", RegexOptions.CultureInvariant)

    let isValidId (id: string) = idPattern.IsMatch id

    let private liveCode state =
        match state with
        | LiveWorkState.Ready -> "ready"
        | LiveWorkState.Active -> "active"
        | LiveWorkState.Blocked -> "blocked"
        | LiveWorkState.Complete -> "complete"
        | LiveWorkState.Abandoned -> "abandoned"

    let private liveStatus state =
        match state with
        | LiveWorkState.Complete
        | LiveWorkState.Abandoned -> GroupMemberStatus.Terminal(liveCode state)
        | open' -> GroupMemberStatus.Open(liveCode open')

    let private queueStatus (status: string) =
        match status with
        | "abandoned" -> GroupMemberStatus.Terminal status
        | other -> GroupMemberStatus.Open other

    /// Every recorded work item and its membership status. A live-context
    /// record outranks the backlog entry it was promoted from.
    let memberStatuses (queue: PlanningQueueItem list) (live: PlanningLiveItem list) : Map<string, GroupMemberStatus> =
        let fromQueue = queue |> List.map (fun item -> item.Id, queueStatus item.Status)
        let fromLive = live |> List.map (fun item -> item.Id, liveStatus item.State)
        fromQueue @ fromLive |> Map.ofList

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (snd >> (<) 1) |> List.map fst

    /// Problems with a group's shape, independent of when it is checked.
    let private shapeRejections (group: DeclaredGroup) (statuses: Map<string, GroupMemberStatus>) =
        let distinct = group.Members |> List.distinct

        [ if not (isValidId group.Id) then yield GroupRejection.InvalidGroupId group.Id
          if distinct.Length < 2 then yield GroupRejection.TooFewMembers distinct.Length
          yield! duplicates group.Members |> List.map GroupRejection.DuplicateMember
          yield! distinct |> List.filter (statuses.ContainsKey >> not) |> List.map GroupRejection.UnknownMember
          if group.ExecutionRepository |> Option.exists String.IsNullOrWhiteSpace then
              yield GroupRejection.EmptyValue "execution repository"
          if group.SharedContext |> List.exists String.IsNullOrWhiteSpace then
              yield GroupRejection.EmptyValue "shared context"
          if group.ArchitectureNotes |> List.exists String.IsNullOrWhiteSpace then
              yield GroupRejection.EmptyValue "architecture note" ]

    /// Decides a `work group create`. Every reason is reported at once; on
    /// success the declaration is returned for storage and nothing else
    /// changes: no member's lifecycle state is read for writing.
    let create (existing: StoredGroup list) (statuses: Map<string, GroupMemberStatus>) (request: GroupCreateRequest) : Result<StoredGroup, GroupRejection list> =
        let kind =
            request.Kind
            |> Option.map (fun value -> GroupKind.tryParse value |> Option.map Ok |> Option.defaultValue (Error(GroupRejection.UnknownKind value)))

        let group =
            { Id = request.Id
              Members = request.Members
              Kind = kind |> Option.bind (function Ok value -> Some value | Error _ -> None)
              Origin = GroupOrigin.HumanDeclared
              SharedContext = request.SharedContext
              ExecutionRepository = request.ExecutionRepository
              CrossRepository = request.CrossRepository
              ArchitectureNotes = request.ArchitectureNotes }

        let terminal =
            group.Members
            |> List.distinct
            |> List.choose (fun id ->
                match statuses.TryFind id with
                | Some(GroupMemberStatus.Terminal state) -> Some(GroupRejection.TerminalMember(id, state))
                | _ -> None)

        let rejections =
            [ if existing |> List.exists (fun stored -> stored.Group.Id = request.Id) then
                  yield GroupRejection.DuplicateGroupId request.Id
              yield! shapeRejections group statuses
              yield! terminal
              match kind with
              | Some(Error rejection) -> yield rejection
              | _ -> () ]

        if rejections.IsEmpty then
            Ok
                { Group = group
                  DeclaredAt = request.DeclaredAt
                  DeclaredBy = request.DeclaredBy
                  Additions = []
                  Removals = []
                  Checkpoints = [] }
        else
            Error rejections

    /// The execution repositories a group runs in: the declared one, or else
    /// those of its current members.
    let private groupRepositories (locate: string -> string) (group: DeclaredGroup) =
        match group.ExecutionRepository with
        | Some declared -> [ declared ]
        | None -> group.Members |> List.map locate |> List.distinct

    /// Decides a `work group add`: one open, recorded work item joins a
    /// declared group. `locate` names an item's execution repository
    /// (PRX-GRP-051). Every reason is reported at once; on success the
    /// updated declaration records who added the member and when. No
    /// member's lifecycle state is changed (PRX-GRP-002).
    let add (existing: StoredGroup list) (statuses: Map<string, GroupMemberStatus>) (locate: string -> string) (request: GroupAddRequest) : Result<StoredGroup, GroupRejection list> =
        match existing |> List.tryFind (fun stored -> stored.Group.Id = request.GroupId) with
        | None -> Error [ GroupRejection.UnknownGroup request.GroupId ]
        | Some stored ->
            let group = stored.Group
            let item = request.WorkItem

            let mismatch () =
                let repository = locate item

                match groupRepositories locate group with
                | repositories when group.CrossRepository || repositories |> List.forall ((=) repository) -> []
                | repositories -> [ GroupRejection.RepositoryMismatch(item, repository, repositories) ]

            let rejections =
                [ if List.contains item group.Members then
                      yield GroupRejection.AlreadyMember(item, group.Id)
                  match statuses.TryFind item with
                  | None -> yield GroupRejection.UnknownMember item
                  | Some(GroupMemberStatus.Terminal state) -> yield GroupRejection.TerminalMember(item, state)
                  | Some(GroupMemberStatus.Open _) -> yield! mismatch () ]

            if rejections.IsEmpty then
                Ok
                    { stored with
                        Group = { group with Members = group.Members @ [ item ] }
                        Additions =
                            stored.Additions
                            @ [ { WorkItem = item
                                  AddedAt = request.AddedAt
                                  AddedBy = request.AddedBy } ] }
            else
                Error rejections

    /// Decides a `work group remove`: one current member leaves a declared
    /// group. A group keeps at least two members (as `create` and `validate`
    /// require), so removing one of its last two is refused rather than
    /// leaving a degenerate group. Only the declaration changes: the item's
    /// lifecycle state, evidence and attribution are not read for writing
    /// (PRX-GRP-002), and its earlier addition stays recorded.
    let remove (existing: StoredGroup list) (request: GroupRemoveRequest) : Result<StoredGroup, GroupRejection list> =
        match existing |> List.tryFind (fun stored -> stored.Group.Id = request.GroupId) with
        | None -> Error [ GroupRejection.UnknownGroup request.GroupId ]
        | Some stored ->
            let group = stored.Group
            let item = request.WorkItem
            let remaining = group.Members |> List.filter ((<>) item)

            let rejections =
                [ if not (List.contains item group.Members) then
                      yield GroupRejection.NotMember(item, group.Id)
                  elif (List.distinct remaining).Length < 2 then
                      yield GroupRejection.LastMembers(item, group.Id, (List.distinct remaining).Length) ]

            if rejections.IsEmpty then
                Ok
                    { stored with
                        Group = { group with Members = remaining }
                        Removals =
                            stored.Removals
                            @ [ { WorkItem = item
                                  RemovedAt = request.RemovedAt
                                  RemovedBy = request.RemovedBy
                                  Reason = request.Reason } ] }
            else
                Error rejections

    /// Where each member stands now, from its recorded lifecycle.
    let progress (statuses: Map<string, GroupMemberStatus>) (members: string list) : GroupMemberProgress =
        let where predicate =
            members |> List.distinct |> List.filter (fun id -> statuses |> Map.tryFind id |> predicate)

        { Active = where (fun status -> status = Some(GroupMemberStatus.Open "active"))
          Completed = where (fun status -> status = Some(GroupMemberStatus.Terminal "complete"))
          Remaining =
            where (fun status ->
                match status with
                | Some(GroupMemberStatus.Open state) -> state <> "active"
                | _ -> false)
          Abandoned =
            where (fun status ->
                match status with
                | Some(GroupMemberStatus.Terminal state) -> state <> "complete"
                | _ -> false)
          Unknown = where Option.isNone }

    let private location (git: GitDurableLocation) : GroupCheckpointLocation =
        { Repository = git.Repository
          Branch = git.Branch
          Commit = git.RemoteCommit.Value
          Remote = git.Remote.Name
          RemoteUrl = git.Remote.Url
          RemoteBranch = git.RemoteBranch }

    /// Decides a `work group checkpoint` (PRX-GRP-044). `durable` is the
    /// outcome of the same verification `work checkpoint` applies (local HEAD
    /// on a branch, equal to its upstream as read from the remote, and no
    /// meaningful uncommitted change). The checkpoint references each
    /// member's own latest checkpoint and claims no paths: members keep their
    /// own checkpoint history and attribution (PRX-GRP-043). Every problem
    /// that can be known independently is reported together.
    let checkpoint
        (existing: StoredGroup list)
        (statuses: Map<string, GroupMemberStatus>)
        (references: string -> MemberCheckpointReference)
        (durable: Result<GitDurableLocation, CheckpointRejection list>)
        (request: GroupCheckpointRequest)
        : Result<StoredGroup, GroupRejection list> =
        match existing |> List.tryFind (fun stored -> stored.Group.Id = request.GroupId) with
        | None -> Error [ GroupRejection.UnknownGroup request.GroupId ]
        | Some stored ->
            let members = progress statuses stored.Group.Members

            let rejections =
                [ if String.IsNullOrWhiteSpace request.Summary then
                      yield GroupRejection.NotDurable CheckpointRejection.BlankSummary
                  if String.IsNullOrWhiteSpace request.NextAction then
                      yield GroupRejection.NotDurable CheckpointRejection.BlankNextAction
                  if request.SharedDecisions |> List.exists String.IsNullOrWhiteSpace then
                      yield GroupRejection.BlankSharedDecision
                  if members.Active.IsEmpty then
                      yield GroupRejection.NoActiveMember stored.Group.Id
                  match durable with
                  | Error failures -> yield! failures |> List.map GroupRejection.NotDurable
                  | Ok _ -> () ]
                |> List.distinct

            match rejections, durable with
            | [], Ok git ->
                Ok
                    { stored with
                        Checkpoints =
                            stored.Checkpoints
                            @ [ { CheckpointId = $"{stored.Group.Id}-checkpoint-{stored.Checkpoints.Length + 1}"
                                  RecordedAt = request.RecordedAt
                                  RecordedBy = request.RecordedBy
                                  Summary = request.Summary.Trim()
                                  NextAction = request.NextAction.Trim()
                                  SharedDecisions = request.SharedDecisions |> List.map _.Trim()
                                  Members = members
                                  Location = location git
                                  MemberCheckpoints =
                                    stored.Group.Members
                                    |> List.distinct
                                    |> List.map (fun item -> { WorkItem = item; Reference = references item }) } ] }
            | _ -> Error rejections

    /// `validate` over the stored declarations. A member that became
    /// terminal after the group was declared is legitimate partial
    /// completion (PRX-GRP-042), so only shape, duplicate IDs and unknown
    /// members are findings. Returns (group ID, message) pairs.
    let findings (stored: StoredGroup list) (statuses: Map<string, GroupMemberStatus>) : (string * string) list =
        let duplicateIds =
            stored
            |> List.map (fun entry -> entry.Group.Id)
            |> duplicates
            |> List.map (fun id -> id, GroupRejection.message (GroupRejection.DuplicateGroupId id))

        let shapes =
            stored
            |> List.collect (fun entry ->
                shapeRejections entry.Group statuses |> List.map (fun rejection -> entry.Group.Id, GroupRejection.message rejection))

        duplicateIds @ shapes

    /// The planner reads stored declarations exactly as it reads
    /// `grouping.groups`. A group already declared under the same ID in an
    /// explicit `--config` file keeps that definition.
    let mergeInto (stored: StoredGroup list) (configuration: PlannerConfiguration) : PlannerConfiguration =
        let configured = configuration.Grouping.Groups |> List.map (fun group -> group.Id) |> Set.ofList

        let added =
            stored
            |> List.map (fun entry -> entry.Group)
            |> List.filter (fun group -> not (configured.Contains group.Id))

        { configuration with
            Grouping =
                { configuration.Grouping with
                    Groups = configuration.Grouping.Groups @ added } }
