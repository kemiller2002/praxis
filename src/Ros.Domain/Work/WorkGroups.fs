namespace Ros.Domain.Work

open System
open System.Text.RegularExpressions
open Ros.Domain.Planning
open Ros.Domain.Provenance

/// Durable execution groups (PRX-GRP-073, phase two): a declared group
/// recorded in Praxis state rather than in planner configuration. The stored
/// declaration is the planner's own `DeclaredGroup`, so `plan` reads it
/// exactly as it reads `grouping.groups`. Membership never changes a
/// member's lifecycle, evidence or attribution (PRX-GRP-002, PRX-GRP-043):
/// group decisions return a new group record and nothing else.

/// What one mutation of a group did, as recorded in its history.
[<RequireQualifiedAccess>]
type GroupOperation =
    | Created
    | MemberAdded
    | MemberRemoved

[<RequireQualifiedAccess>]
module GroupOperation =
    let all = [ GroupOperation.Created; GroupOperation.MemberAdded; GroupOperation.MemberRemoved ]

    let code operation =
        match operation with
        | GroupOperation.Created -> "created"
        | GroupOperation.MemberAdded -> "member-added"
        | GroupOperation.MemberRemoved -> "member-removed"

    let tryParse value = all |> List.tryFind (fun operation -> code operation = value)

/// One append-only provenance entry: who changed the group, when, and why.
/// Entries are never rewritten.
type GroupHistoryEntry =
    { Operation: GroupOperation
      Member: string option
      At: string
      Actor: Actor
      Reason: string option
      /// A removal that deliberately left the group empty (never implicit).
      ExplicitEmpty: bool }

/// A member's own latest durable checkpoint, referenced (never copied or
/// replaced) by a group checkpoint.
type MemberCheckpointReference =
    { WorkItemId: string
      CheckpointId: string
      Commit: string }

/// A group-level checkpoint (PRX-GRP-044): where the group stands after a
/// milestone, verified durable by the same Git rule as `work checkpoint`.
/// It claims no paths and no execution: attribution stays on members' own
/// checkpoints (PRX-GRP-043).
type GroupCheckpoint =
    { CheckpointId: string
      RecordedAt: string
      Actor: Actor
      Summary: string
      NextAction: string
      Decisions: string list
      Completed: string list
      Active: string list
      Blocked: string list
      Remaining: string list
      Abandoned: string list
      MemberCheckpoints: MemberCheckpointReference list
      Location: GitDurableLocation }

/// A declared group as Praxis state records it.
type StoredWorkGroup =
    { Declaration: DeclaredGroup
      CreatedAt: string
      CreatedBy: Actor
      History: GroupHistoryEntry list
      Checkpoints: GroupCheckpoint list }

/// Whether a work item may join a group: it must be known and not terminal.
/// The state is the recorded lifecycle state (`QueuePresentation`).
[<RequireQualifiedAccess>]
type MemberStanding =
    | Unknown
    | Open of state: string
    | Terminal of state: string

[<RequireQualifiedAccess>]
module MemberStanding =
    let private terminalStates = set [ "complete"; "abandoned" ]

    let ofState (state: string) =
        if terminalStates.Contains state then MemberStanding.Terminal state else MemberStanding.Open state

    /// Every recorded item's standing, from the backlog queue and the live
    /// context, read the way every other work view reads them.
    let lookup (queue: PlanningQueueItem list) (live: PlanningLiveItem list) : string -> MemberStanding =
        let queued = queue |> List.map (fun item -> item.Id, item.Status) |> Map.ofList
        let current = live |> List.map (fun item -> item.Id, item.State) |> Map.ofList

        fun id ->
            QueuePresentation.effectiveStatus (queued.TryFind id) (current.TryFind id)
            |> Option.map ofState
            |> Option.defaultValue MemberStanding.Unknown

    let state standing =
        match standing with
        | MemberStanding.Unknown -> None
        | MemberStanding.Open state
        | MemberStanding.Terminal state -> Some state

/// Why a group command refused. Every independently knowable problem is
/// reported together; nothing is written when any is present.
[<RequireQualifiedAccess>]
type GroupRejection =
    | InvalidGroupId of groupId: string
    | InvalidMemberId of workItemId: string
    | DuplicateGroup of groupId: string
    | UnknownGroup of groupId: string
    | NoMembers
    | RepeatedMember of workItemId: string
    | UnknownMember of workItemId: string
    | TerminalMember of workItemId: string * state: string
    | AlreadyMember of workItemId: string * groupId: string
    | NotMember of workItemId: string * groupId: string
    | RepositoryMismatch of workItemId: string * itemLocation: ExecutionLocation * groupRepository: string
    | LastMember of workItemId: string * groupId: string

[<RequireQualifiedAccess>]
module GroupRejection =
    let code rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupRejection.InvalidMemberId _ -> "invalid-member-id"
        | GroupRejection.DuplicateGroup _ -> "duplicate-group"
        | GroupRejection.UnknownGroup _ -> "unknown-group"
        | GroupRejection.NoMembers -> "no-members"
        | GroupRejection.RepeatedMember _ -> "repeated-member"
        | GroupRejection.UnknownMember _ -> "unknown-member"
        | GroupRejection.TerminalMember _ -> "terminal-member"
        | GroupRejection.AlreadyMember _ -> "already-member"
        | GroupRejection.NotMember _ -> "not-member"
        | GroupRejection.RepositoryMismatch _ -> "repository-mismatch"
        | GroupRejection.LastMember _ -> "last-member"

    let message rejection =
        match rejection with
        | GroupRejection.InvalidGroupId id -> $"'{id}' is not a group ID; use GROUP-<AREA>-<SEQUENCE> in upper case (PRX-GRP-010)"
        | GroupRejection.InvalidMemberId id -> $"'{id}' is not a valid work-item ID"
        | GroupRejection.DuplicateGroup id -> $"group {id} already exists"
        | GroupRejection.UnknownGroup id -> $"group {id} is not recorded"
        | GroupRejection.NoMembers -> "a group needs at least one member"
        | GroupRejection.RepeatedMember id -> $"{id} is named more than once"
        | GroupRejection.UnknownMember id -> $"{id} is not a recorded work item (not in the backlog queue or the live context)"
        | GroupRejection.TerminalMember(id, state) -> $"{id} is {state}; a terminal item cannot join a group"
        | GroupRejection.AlreadyMember(id, group) -> $"{id} is already a member of {group}"
        | GroupRejection.NotMember(id, group) -> $"{id} is not a member of {group}"
        | GroupRejection.RepositoryMismatch(id, item, group) ->
            $"{id} executes in {ExecutionLocation.describe item} but the group executes in {group}; declare the group cross-repository or split it (PRX-GRP-051)"
        | GroupRejection.LastMember(id, group) -> $"{id} is the last member of {group}; removing it would leave the group empty"

    /// A refusal caused by the arguments themselves (exit 2), not by state.
    let isArgumentError rejection =
        match rejection with
        | GroupRejection.InvalidGroupId _
        | GroupRejection.InvalidMemberId _
        | GroupRejection.NoMembers
        | GroupRejection.RepeatedMember _ -> true
        | _ -> false

/// Repository facts a group decision reads. Supplied by the caller so the
/// decisions stay pure.
type GroupContext =
    { Groups: StoredWorkGroup list
      Standing: string -> MemberStanding
      /// Where a work item executes, by the planner's own rule
      /// (`Grouping.executionLocation`, PRX-GRP-051).
      RepositoryOf: string -> ExecutionLocation }

type GroupCreateRequest =
    { GroupId: string
      Members: string list
      Kind: GroupKind option
      Origin: GroupOrigin
      SharedContext: string list
      ExecutionRepository: string
      CrossRepository: bool
      ArchitectureNotes: string list
      OccurredAt: string
      Actor: Actor
      Reason: string option }

/// Where a member stands inside its group (PRX-GRP-042): each member
/// completes, or not, on its own.
[<RequireQualifiedAccess>]
type MemberCategory =
    | Completed
    | Abandoned
    | Active
    | Blocked
    | Remaining
    | Unknown

[<RequireQualifiedAccess>]
module MemberCategory =
    let code category =
        match category with
        | MemberCategory.Completed -> "completed"
        | MemberCategory.Abandoned -> "abandoned"
        | MemberCategory.Active -> "active"
        | MemberCategory.Blocked -> "blocked"
        | MemberCategory.Remaining -> "remaining"
        | MemberCategory.Unknown -> "unknown"

    let ofState (state: string option) =
        match state with
        | Some "complete" -> MemberCategory.Completed
        | Some "abandoned" -> MemberCategory.Abandoned
        | Some "active" -> MemberCategory.Active
        | Some "blocked" -> MemberCategory.Blocked
        | Some _ -> MemberCategory.Remaining
        | None -> MemberCategory.Unknown

/// What is known about one member now: its recorded lifecycle state, the
/// planner's view of it when available, and the work items it still waits on.
type MemberFacts =
    { State: string option
      PlanningState: string option
      WaitsOn: string list }

[<RequireQualifiedAccess>]
module MemberFacts =
    /// Facts from the recorded state alone, without a planner analysis.
    let ofStanding (standing: string -> MemberStanding) (id: string) =
        { State = MemberStanding.state (standing id)
          PlanningState = None
          WaitsOn = [] }

type MemberProgress =
    { WorkItemId: string
      Category: MemberCategory
      State: string option
      PlanningState: string option
      /// Work items this member still waits on.
      WaitsOn: string list
      /// Open members of the same group that wait on this member.
      Gates: string list }

/// A group's partial-completion view. It never says the group succeeded:
/// `Completed` lists exactly the members that completed on their own.
type GroupProgress =
    { Members: MemberProgress list
      Completed: string list
      Abandoned: string list
      Active: string list
      Blocked: string list
      Remaining: string list
      Unknown: string list }

[<RequireQualifiedAccess>]
module GroupProgress =
    let summary (progress: GroupProgress) =
        let count (values: string list) = values.Length

        $"{count progress.Completed} of {progress.Members.Length} complete ({count progress.Active} active, {count progress.Blocked} blocked, {count progress.Remaining} remaining, {count progress.Abandoned} abandoned, {count progress.Unknown} unknown)"

/// One membership change (`work group add`, `work group remove`).
type GroupMemberRequest =
    { GroupId: string
      WorkItemId: string
      OccurredAt: string
      Actor: Actor
      Reason: string option
      /// Removal only: deliberately allow leaving the group empty.
      AllowEmpty: bool }

type GroupCheckpointRequest =
    { GroupId: string
      /// Chosen by the caller (a digest of the checkpoint), so the decision
      /// stays pure.
      CheckpointId: string
      Summary: string
      NextAction: string
      Decisions: string list
      OccurredAt: string
      Actor: Actor }

/// Why a group checkpoint was refused: the group, ownership, or durability.
[<RequireQualifiedAccess>]
type GroupCheckpointRejection =
    | Group of GroupRejection
    /// No member is active, so no grouped execution is in progress.
    | NoActiveMember of groupId: string
    /// No active member's execution belongs to the caller (member, reason).
    | NoOwnExecution of reasons: (string * string) list
    | Durability of CheckpointRejection

[<RequireQualifiedAccess>]
module GroupCheckpointRejection =
    let code rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.code group
        | GroupCheckpointRejection.NoActiveMember _ -> "no-active-member"
        | GroupCheckpointRejection.NoOwnExecution _ -> "no-own-execution"
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.code durability

    let message rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.message group
        | GroupCheckpointRejection.NoActiveMember id ->
            $"no member of {id} is active; start a member's work before recording a group checkpoint (as `work checkpoint` requires an active item)"
        | GroupCheckpointRejection.NoOwnExecution reasons ->
            let listed = reasons |> List.map (fun (id, reason) -> $"{id}: {reason}") |> String.concat "; "
            $"no active member's execution belongs to the caller ({listed}); a group checkpoint is recorded only under the caller's own execution"
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.message durability

    let isArgumentError rejection =
        match rejection with
        | GroupCheckpointRejection.Group group -> GroupRejection.isArgumentError group
        | GroupCheckpointRejection.NoActiveMember _
        | GroupCheckpointRejection.NoOwnExecution _ -> false
        | GroupCheckpointRejection.Durability durability -> CheckpointRejection.isArgumentError durability

[<RequireQualifiedAccess>]
module WorkGroups =
    let private groupIdPattern = Regex(@"^GROUP-[A-Z0-9]+(-[A-Z0-9]+)*\z", RegexOptions.CultureInvariant)

    let isValidGroupId (value: string) = groupIdPattern.IsMatch value

    let tryFind (groups: StoredWorkGroup list) (groupId: string) =
        groups |> List.tryFind (fun group -> group.Declaration.Id = groupId)

    /// Groups are kept ordered by ID so the same state always serializes to
    /// the same bytes (PRX-GRP-075).
    let upsert (groups: StoredWorkGroup list) (group: StoredWorkGroup) =
        group :: (groups |> List.filter (fun existing -> existing.Declaration.Id <> group.Declaration.Id))
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.Declaration.Id, right.Declaration.Id))

    let private repeated (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst

    /// The one join rule for every way an item enters a group: a valid ID
    /// of a known, non-terminal item that executes where the group does.
    let eligibility (context: GroupContext) (declaration: DeclaredGroup) (workItemId: string) : GroupRejection list =
        if not (WorkItemId.isValid workItemId) then
            [ GroupRejection.InvalidMemberId workItemId ]
        else
            [ match context.Standing workItemId with
              | MemberStanding.Unknown -> yield GroupRejection.UnknownMember workItemId
              | MemberStanding.Terminal state -> yield GroupRejection.TerminalMember(workItemId, state)
              | MemberStanding.Open _ -> ()
              match declaration.ExecutionRepository with
              | Some repository when not declaration.CrossRepository ->
                  match context.RepositoryOf workItemId with
                  | ExecutionLocation.Repository itemRepository when itemRepository = repository -> ()
                  | location -> yield GroupRejection.RepositoryMismatch(workItemId, location, repository)
              | _ -> () ]

    let private entry operation memberId (at: string) (actor: Actor) (reason: string option) explicitEmpty =
        { Operation = operation
          Member = memberId
          At = at
          Actor = actor
          Reason = reason
          ExplicitEmpty = explicitEmpty }

    /// `work group create`: a new declared group, or every reason it cannot be.
    let create (context: GroupContext) (request: GroupCreateRequest) : Result<StoredWorkGroup, GroupRejection list> =
        let declaration =
            { Id = request.GroupId
              Members = request.Members
              Kind = request.Kind
              Origin = request.Origin
              SharedContext = request.SharedContext
              ExecutionRepository = Some request.ExecutionRepository
              CrossRepository = request.CrossRepository
              ArchitectureNotes = request.ArchitectureNotes }

        let rejections =
            [ if not (isValidGroupId request.GroupId) then
                  yield GroupRejection.InvalidGroupId request.GroupId
              elif (tryFind context.Groups request.GroupId).IsSome then
                  yield GroupRejection.DuplicateGroup request.GroupId
              if request.Members.IsEmpty then
                  yield GroupRejection.NoMembers
              yield! repeated request.Members |> List.map GroupRejection.RepeatedMember
              yield! request.Members |> List.distinct |> List.collect (eligibility context declaration) ]

        match rejections with
        | [] ->
            Ok
                { Declaration = declaration
                  CreatedAt = request.OccurredAt
                  CreatedBy = request.Actor
                  History = [ entry GroupOperation.Created None request.OccurredAt request.Actor request.Reason false ]
                  Checkpoints = [] }
        | rejections -> Error rejections

    /// The group a membership change names, or why it cannot be found.
    let private existing (context: GroupContext) (groupId: string) : Result<StoredWorkGroup, GroupRejection list> =
        if not (isValidGroupId groupId) then Error [ GroupRejection.InvalidGroupId groupId ]
        else tryFind context.Groups groupId |> Option.map Ok |> Option.defaultValue (Error [ GroupRejection.UnknownGroup groupId ])

    /// `work group add`: one more member, joining by the same rule as at
    /// creation, recorded with who added it. The member's own record is
    /// not touched.
    let add (context: GroupContext) (request: GroupMemberRequest) : Result<StoredWorkGroup, GroupRejection list> =
        existing context request.GroupId
        |> Result.bind (fun group ->
            let declaration = group.Declaration

            let rejections =
                if declaration.Members |> List.contains request.WorkItemId then [ GroupRejection.AlreadyMember(request.WorkItemId, declaration.Id) ]
                else eligibility context declaration request.WorkItemId

            match rejections with
            | [] ->
                Ok
                    { group with
                        Declaration = { declaration with Members = declaration.Members @ [ request.WorkItemId ] }
                        History = group.History @ [ entry GroupOperation.MemberAdded (Some request.WorkItemId) request.OccurredAt request.Actor request.Reason false ] }
            | rejections -> Error rejections)

    /// `work group remove`: one member leaves. Any member may leave,
    /// whatever its state; its lifecycle, evidence and attribution are not
    /// touched. The last member leaves only with `AllowEmpty`, which the
    /// history records.
    let remove (context: GroupContext) (request: GroupMemberRequest) : Result<StoredWorkGroup, GroupRejection list> =
        existing context request.GroupId
        |> Result.bind (fun group ->
            let declaration = group.Declaration

            match declaration.Members |> List.contains request.WorkItemId, declaration.Members.Length with
            | false, _ -> Error [ GroupRejection.NotMember(request.WorkItemId, declaration.Id) ]
            | true, 1 when not request.AllowEmpty -> Error [ GroupRejection.LastMember(request.WorkItemId, declaration.Id) ]
            | true, remaining ->
                Ok
                    { group with
                        Declaration = { declaration with Members = declaration.Members |> List.filter ((<>) request.WorkItemId) }
                        History =
                            group.History
                            @ [ entry GroupOperation.MemberRemoved (Some request.WorkItemId) request.OccurredAt request.Actor request.Reason (remaining = 1) ] })

    /// The declarations the planner reads: every configured group, then every
    /// stored group whose ID the configuration does not already declare (an
    /// explicit per-invocation configuration is the narrower source).
    let declarations (configured: DeclaredGroup list) (stored: StoredWorkGroup list) : DeclaredGroup list =
        let configuredIds = configured |> List.map (fun group -> group.Id) |> Set.ofList
        configured @ (stored |> List.map (fun group -> group.Declaration) |> List.filter (fun group -> not (configuredIds.Contains group.Id)))

    /// A group may be empty only when its latest history entry deliberately
    /// removed its last member.
    let private explicitlyEmpty (group: StoredWorkGroup) =
        group.History |> List.tryLast |> Option.exists (fun last -> last.ExplicitEmpty)

    /// `validate`'s view of stored groups: (group ID or "", field, message).
    /// Terminal members are valid: members complete inside their group
    /// (PRX-GRP-042); only joining requires a non-terminal item. Repository
    /// locality is checked when an item joins, against the configuration
    /// supplied then; it is not re-derived here without that configuration.
    let findings (context: GroupContext) : (string * string * string) list =
        let groups = context.Groups

        [ for duplicate in groups |> List.map (fun group -> group.Declaration.Id) |> repeated do
              yield duplicate, "id", $"group {duplicate} is recorded more than once"
          for group in groups do
              let declaration = group.Declaration
              let id = declaration.Id

              if not (isValidGroupId id) then
                  yield id, "id", $"'{id}' is not a group ID (GROUP-<AREA>-<SEQUENCE>)"
              if declaration.Members.IsEmpty && not (explicitlyEmpty group) then
                  yield id, "members", $"group {id} has no members and no explicit empty removal records why"
              for repeatedMember in repeated declaration.Members do
                  yield id, "members", $"{repeatedMember} is a member of {id} more than once"
              for memberId in declaration.Members |> List.distinct do
                  if not (WorkItemId.isValid memberId) then
                      yield id, "members", $"'{memberId}' is not a valid work-item ID"
                  elif context.Standing memberId = MemberStanding.Unknown then
                      yield id, "members", $"member {memberId} of {id} is not a recorded work item"
              match group.History with
              | first :: _ when first.Operation = GroupOperation.Created -> ()
              | _ -> yield id, "history", $"group {id}'s history must begin with its creation"
              if group.History |> List.skip (min 1 group.History.Length) |> List.exists (fun entry -> entry.Operation = GroupOperation.Created) then
                  yield id, "history", $"group {id} records more than one creation"
              for entry in group.History do
                  if entry.Operation <> GroupOperation.Created && entry.Member.IsNone then
                      yield id, "history", $"a {GroupOperation.code entry.Operation} entry of {id} names no member" ]

    /// Re-validates every stored group checkpoint offline: it was durable
    /// when recorded (local commit equals the remote commit), its text is
    /// present, its member standings are disjoint, and every member
    /// checkpoint it references is one of that member's own recorded
    /// checkpoints. `ownerOf` maps a recorded work checkpoint's ID to the
    /// work item that recorded it.
    let checkpointFindings (ownerOf: string -> string option) (groups: StoredWorkGroup list) : (string * string * string) list =
        [ for group in groups do
              let id = group.Declaration.Id

              for duplicate in group.Checkpoints |> List.map (fun checkpoint -> checkpoint.CheckpointId) |> repeated do
                  yield id, "checkpoints", $"group checkpoint {duplicate} is recorded more than once"

              for checkpoint in group.Checkpoints do
                  let field = $"checkpoints.{checkpoint.CheckpointId}"
                  let location = checkpoint.Location

                  if location.LocalCommit <> location.RemoteCommit then
                      yield id, field, $"recorded commit {location.LocalCommit.Value} is not the remote commit {location.RemoteCommit.Value}; the checkpoint was not durable"

                  if (RequiredText.tryCreate checkpoint.Summary).IsNone then
                      yield id, field, "summary is blank"

                  if (RequiredText.tryCreate checkpoint.NextAction).IsNone then
                      yield id, field, "nextAction is blank"

                  let standings =
                      checkpoint.Completed @ checkpoint.Active @ checkpoint.Blocked @ checkpoint.Remaining @ checkpoint.Abandoned

                  for member' in standings |> repeated do
                      yield id, field, $"member {member'} is listed under more than one standing"

                  for reference in checkpoint.MemberCheckpoints do
                      if not (standings |> List.contains reference.WorkItemId) then
                          yield id, field, $"references a checkpoint of {reference.WorkItemId}, which the checkpoint does not list as a member"

                      match ownerOf reference.CheckpointId with
                      | Some owner when owner = reference.WorkItemId -> ()
                      | Some owner ->
                          yield id, field, $"references checkpoint {reference.CheckpointId} as {reference.WorkItemId}'s, but {owner} recorded it"
                      | None ->
                          yield id, field, $"references checkpoint {reference.CheckpointId} of {reference.WorkItemId}, which is not a recorded work checkpoint" ]

    /// Each member's own standing and who it gates inside the group; the
    /// same classification serves `work group show` and group checkpoints.
    let progress (group: StoredWorkGroup) (facts: string -> MemberFacts) : GroupProgress =
        let members = group.Declaration.Members
        let known = members |> List.map (fun id -> id, facts id) |> Map.ofList

        let open' id =
            match MemberCategory.ofState known[id].State with
            | MemberCategory.Completed
            | MemberCategory.Abandoned -> false
            | _ -> true

        let rows =
            members
            |> List.map (fun id ->
                let fact = known[id]

                { WorkItemId = id
                  Category = MemberCategory.ofState fact.State
                  State = fact.State
                  PlanningState = fact.PlanningState
                  WaitsOn = fact.WaitsOn
                  Gates = members |> List.filter (fun other -> other <> id && open' other && known[other].WaitsOn |> List.contains id) })

        let inCategory category =
            rows |> List.filter (fun row -> row.Category = category) |> List.map (fun row -> row.WorkItemId)

        { Members = rows
          Completed = inCategory MemberCategory.Completed
          Abandoned = inCategory MemberCategory.Abandoned
          Active = inCategory MemberCategory.Active
          Blocked = inCategory MemberCategory.Blocked
          Remaining = inCategory MemberCategory.Remaining
          Unknown = inCategory MemberCategory.Unknown }

    /// The ownership half of `work checkpoint`'s rule, over the group: at
    /// least one member is active, and at least one active member's
    /// execution resolves to the caller.
    let private ownership (group: StoredWorkGroup) (active: string list) (execution: string -> ExecutionObservation) =
        let unowned id =
            match execution id with
            | ExecutionObservation.Resolved _ -> None
            | ExecutionObservation.NoneActive -> Some(id, "no active execution belongs to the caller")
            | ExecutionObservation.Ambiguous ids -> Some(id, $"""several executions could be the caller ({String.concat ", " ids})""")
            | ExecutionObservation.Refused reason -> Some(id, reason)

        match active, active |> List.choose unowned with
        | [], _ -> [ GroupCheckpointRejection.NoActiveMember group.Declaration.Id ]
        | _, reasons when reasons.Length = active.Length -> [ GroupCheckpointRejection.NoOwnExecution reasons ]
        | _ -> []

    /// `work group checkpoint`: a durable group-level checkpoint over the
    /// members' own. `location` is the shared Git verification's verdict
    /// (`CheckpointVerification.verifyLocation`); `execution` observes the
    /// caller's execution for a member, which `work checkpoint` also
    /// requires; `memberCheckpoint` gives a member's own latest verified
    /// checkpoint. Nothing about any member is written.
    let checkpoint
        (context: GroupContext)
        (request: GroupCheckpointRequest)
        (facts: string -> MemberFacts)
        (execution: string -> ExecutionObservation)
        (memberCheckpoint: string -> MemberCheckpointReference option)
        (location: Result<GitDurableLocation, CheckpointRejection list>)
        : Result<StoredWorkGroup * GroupCheckpoint, GroupCheckpointRejection list> =
        let found = existing context request.GroupId |> Result.mapError (List.map GroupCheckpointRejection.Group)

        let owned =
            match found with
            | Ok group -> ownership group (progress group facts).Active execution
            | Error _ -> []

        let text =
            [ if (RequiredText.tryCreate request.Summary).IsNone then
                  GroupCheckpointRejection.Durability CheckpointRejection.BlankSummary
              if (RequiredText.tryCreate request.NextAction).IsNone then
                  GroupCheckpointRejection.Durability CheckpointRejection.BlankNextAction ]

        let durability =
            match location with
            | Ok _ -> []
            | Error rejections -> rejections |> List.map GroupCheckpointRejection.Durability

        match found, text @ owned @ durability, location with
        | Ok group, [], Ok git ->
            let standing = progress group facts

            let recorded =
                { CheckpointId = request.CheckpointId
                  RecordedAt = request.OccurredAt
                  Actor = request.Actor
                  Summary = request.Summary.Trim()
                  NextAction = request.NextAction.Trim()
                  Decisions = request.Decisions
                  Completed = standing.Completed
                  Active = standing.Active
                  Blocked = standing.Blocked
                  Remaining = standing.Remaining @ standing.Unknown
                  Abandoned = standing.Abandoned
                  MemberCheckpoints = group.Declaration.Members |> List.choose memberCheckpoint
                  Location = git }

            Ok({ group with Checkpoints = group.Checkpoints @ [ recorded ] }, recorded)
        | Error rejections, others, _ -> Error(rejections @ others)
        | Ok _, rejections, _ -> Error rejections
