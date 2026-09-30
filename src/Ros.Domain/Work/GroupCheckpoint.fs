namespace Ros.Domain.Work

open Ros.Domain.Git

/// Where a member of a group stands when the group checkpoint is recorded
/// (PRX-GRP-044). Standing is read from the member's own lifecycle state and
/// never implies success: abandoned work is reported as abandoned, not as
/// completed (PRX-GRP-042).
[<RequireQualifiedAccess>]
type GroupMemberStanding =
    | Active
    | Completed
    | Remaining
    | Abandoned

[<RequireQualifiedAccess>]
module GroupMemberStanding =
    let code standing =
        match standing with
        | GroupMemberStanding.Active -> "active"
        | GroupMemberStanding.Completed -> "completed"
        | GroupMemberStanding.Remaining -> "remaining"
        | GroupMemberStanding.Abandoned -> "abandoned"

    let tryParse value =
        [ GroupMemberStanding.Active
          GroupMemberStanding.Completed
          GroupMemberStanding.Remaining
          GroupMemberStanding.Abandoned ]
        |> List.tryFind (fun standing -> code standing = value)

    /// A member not yet in repository context (only in the backlog), ready
    /// or blocked is remaining work.
    let ofState (state: LiveWorkState option) =
        match state with
        | Some LiveWorkState.Active -> GroupMemberStanding.Active
        | Some LiveWorkState.Complete -> GroupMemberStanding.Completed
        | Some LiveWorkState.Abandoned -> GroupMemberStanding.Abandoned
        | Some LiveWorkState.Ready
        | Some LiveWorkState.Blocked
        | None -> GroupMemberStanding.Remaining

/// Where the group's membership came from: a durable declaration
/// (`work group create`, `.ros/work/groups.json`) or planner configuration
/// (`grouping.groups`), read exactly as the planner reads them (PRX-GRP-073).
/// The membership is recorded with the checkpoint, so it survives a later
/// `work group add` or `remove`.
[<RequireQualifiedAccess>]
type GroupDeclarationSource =
    | StoredDeclaration of path: string
    | PlannerConfiguration of path: string

[<RequireQualifiedAccess>]
module GroupDeclarationSource =
    let code source =
        match source with
        | GroupDeclarationSource.StoredDeclaration _ -> "stored-declaration"
        | GroupDeclarationSource.PlannerConfiguration _ -> "planner-configuration"

    let path source =
        match source with
        | GroupDeclarationSource.StoredDeclaration path
        | GroupDeclarationSource.PlannerConfiguration path -> path

/// A reference to a member's own latest checkpoint: its identity and commit,
/// never a copy of it. The member's checkpoint history is untouched.
type MemberCheckpointReference =
    { CheckpointId: string
      ExecutionId: string
      Commit: CommitId
      RecordedAt: string }

[<RequireQualifiedAccess>]
module MemberCheckpointReference =
    let ofRecorded (recorded: RecordedCheckpoint) =
        { CheckpointId = recorded.CheckpointId
          ExecutionId = recorded.Recorded.ExecutionId
          Commit = recorded.Recorded.Commit
          RecordedAt = recorded.Recorded.RecordedAt }

/// What was observed about one requested member before any decision.
type GroupMemberObservation =
    { WorkItemId: string
      /// In repository context or in the backlog.
      Known: bool
      /// The live lifecycle state, when the member is in repository context.
      State: LiveWorkState option
      LatestCheckpoint: RecordedCheckpoint option
      /// The caller's own execution of the member; observed only for active
      /// members.
      Execution: ExecutionObservation }

/// One member as the group checkpoint records it.
type GroupMember =
    { WorkItemId: string
      Standing: GroupMemberStanding
      State: LiveWorkState option
      /// The caller's own execution of this member, when it has one.
      ExecutionId: string option
      Checkpoint: MemberCheckpointReference option }

/// What the caller asks to record. Nothing here is trusted.
type GroupCheckpointCandidate =
    { GroupId: string
      Repository: string
      Members: string list
      Declaration: GroupDeclarationSource
      Decisions: string list
      Summary: string
      NextAction: string
      OccurredAt: string }

/// A durable group checkpoint (PRX-GRP-044): the historical fact that, at
/// `RecordedAt`, the group's branch and commit were remotely recoverable, with
/// each member's standing and a reference to its own latest checkpoint. It
/// claims no paths: every change stays attributed through the members' own
/// checkpoints, so no member claims another's changes (PRX-GRP-043).
type GroupCheckpoint =
    private
        { groupId: string
          declaration: GroupDeclarationSource
          members: GroupMember list
          decisions: string list
          summary: RequiredText
          nextAction: RequiredText
          recordedAt: string
          location: GitDurableLocation
          verification: CheckpointVerification }

    member this.GroupId = this.groupId
    member this.Declaration = this.declaration
    member this.Members = this.members
    member this.Decisions = this.decisions
    member this.Summary = this.summary.Value
    member this.NextAction = this.nextAction.Value
    member this.RecordedAt = this.recordedAt
    member this.Location = this.location
    member this.Verification = this.verification
    member this.Commit = this.location.LocalCommit

    /// The executions this checkpoint was recorded under: the caller's own.
    member this.ExecutionIds = this.members |> List.choose _.ExecutionId

    member this.WithStanding standing =
        this.members |> List.filter (fun memberItem -> memberItem.Standing = standing) |> List.map _.WorkItemId

[<RequireQualifiedAccess>]
type GroupCheckpointRejection =
    | InvalidGroupId of groupId: string
    | GroupNotDeclared of reason: string
    | NoMembers
    | DuplicateMember of workItemId: string
    | UnknownMember of workItemId: string
    | BlankDecision
    | NoActiveMember of groupId: string
    | NoOwnExecution of reasons: (string * string) list
    /// The shared durability and text rules of `work checkpoint`.
    | Checkpoint of CheckpointRejection

[<RequireQualifiedAccess>]
module GroupCheckpointRejection =
    let code rejection =
        match rejection with
        | GroupCheckpointRejection.InvalidGroupId _ -> "invalid-group-id"
        | GroupCheckpointRejection.GroupNotDeclared _ -> "group-not-declared"
        | GroupCheckpointRejection.NoMembers -> "no-members"
        | GroupCheckpointRejection.DuplicateMember _ -> "duplicate-member"
        | GroupCheckpointRejection.UnknownMember _ -> "unknown-member"
        | GroupCheckpointRejection.BlankDecision -> "blank-decision"
        | GroupCheckpointRejection.NoActiveMember _ -> "no-active-member"
        | GroupCheckpointRejection.NoOwnExecution _ -> "missing-execution"
        | GroupCheckpointRejection.Checkpoint inner -> CheckpointRejection.code inner

    let message rejection =
        match rejection with
        | GroupCheckpointRejection.InvalidGroupId id -> $"'{id}' is not a valid group ID"
        | GroupCheckpointRejection.GroupNotDeclared reason -> reason
        | GroupCheckpointRejection.NoMembers -> "a group checkpoint needs at least one member"
        | GroupCheckpointRejection.DuplicateMember id -> $"'{id}' is named more than once"
        | GroupCheckpointRejection.UnknownMember id -> $"member '{id}' is neither in repository context nor in the backlog"
        | GroupCheckpointRejection.BlankDecision -> "a shared decision must not be blank"
        | GroupCheckpointRejection.NoActiveMember id -> $"no member of '{id}' is active; a group checkpoint records a grouped execution in progress"
        | GroupCheckpointRejection.NoOwnExecution reasons ->
            let detail = reasons |> List.map (fun (id, reason) -> $"{id}: {reason}") |> String.concat "; "
            $"no active member has an execution that belongs to this process's identity ({detail})"
        | GroupCheckpointRejection.Checkpoint inner -> CheckpointRejection.message inner

    let remedy rejection =
        match rejection with
        | GroupCheckpointRejection.InvalidGroupId _ -> "Use the declared group ID (letters, digits, '.', '_' or '-')."
        | GroupCheckpointRejection.GroupNotDeclared _ ->
            "Declare the group first ('work group create'), or pass --config FILE naming planner configuration that declares it under grouping.groups."
        | GroupCheckpointRejection.NoMembers -> "Add the members to the group ('work group add'), then checkpoint it."
        | GroupCheckpointRejection.DuplicateMember _ -> "Name each member once."
        | GroupCheckpointRejection.UnknownMember _ -> "Check the ID with './ros work show ID'; capture new work with './ros add'."
        | GroupCheckpointRejection.BlankDecision -> "Pass --decision \"the shared architectural decision\", or omit it."
        | GroupCheckpointRejection.NoActiveMember _ -> "Start the member you are working on ('work start'), then checkpoint the group."
        | GroupCheckpointRejection.NoOwnExecution _ ->
            "Begin or continue a member under your own identity ('work start' or 'work continue'); never use another executor's execution."
        | GroupCheckpointRejection.Checkpoint inner -> CheckpointRejection.remedy inner

    let isArgumentError rejection =
        match rejection with
        | GroupCheckpointRejection.InvalidGroupId _
        | GroupCheckpointRejection.GroupNotDeclared _
        | GroupCheckpointRejection.NoMembers
        | GroupCheckpointRejection.DuplicateMember _
        | GroupCheckpointRejection.BlankDecision -> true
        | GroupCheckpointRejection.Checkpoint inner -> CheckpointRejection.isArgumentError inner
        | _ -> false

/// Something true of an accepted group checkpoint that the executor should
/// act on, but that never refuses it.
[<RequireQualifiedAccess>]
type GroupCheckpointWarning =
    /// An active member has no checkpoint of its own at the group commit.
    /// The group checkpoint does not stand in for it.
    | MemberCheckpointBehind of workItemId: string * latest: MemberCheckpointReference option

[<RequireQualifiedAccess>]
module GroupCheckpointWarning =
    let code warning =
        match warning with
        | GroupCheckpointWarning.MemberCheckpointBehind _ -> "member-checkpoint-behind"

    let workItemId warning =
        match warning with
        | GroupCheckpointWarning.MemberCheckpointBehind(id, _) -> id

    let message warning =
        match warning with
        | GroupCheckpointWarning.MemberCheckpointBehind(id, None) ->
            $"active member '{id}' has no checkpoint of its own; the group checkpoint does not replace it (work checkpoint --id {id})"
        | GroupCheckpointWarning.MemberCheckpointBehind(id, Some latest) ->
            $"active member '{id}''s own latest checkpoint is at {CommitId.short latest.Commit}, not the group commit; the group checkpoint does not replace it (work checkpoint --id {id})"

[<RequireQualifiedAccess>]
module GroupCheckpoint =
    let private groupIdPattern =
        System.Text.RegularExpressions.Regex("^[A-Za-z0-9][A-Za-z0-9._-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)

    let isValidGroupId (value: string) = not (isNull value) && groupIdPattern.IsMatch value

    let private textRejections (candidate: GroupCheckpointCandidate) =
        [ if (RequiredText.tryCreate candidate.Summary).IsNone then
              GroupCheckpointRejection.Checkpoint CheckpointRejection.BlankSummary
          if (RequiredText.tryCreate candidate.NextAction).IsNone then
              GroupCheckpointRejection.Checkpoint CheckpointRejection.BlankNextAction
          if candidate.Decisions |> List.exists (RequiredText.tryCreate >> Option.isNone) then
              GroupCheckpointRejection.BlankDecision ]

    let private membershipRejections (candidate: GroupCheckpointCandidate) (observed: GroupMemberObservation list) =
        let duplicates =
            candidate.Members
            |> List.countBy id
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fst >> GroupCheckpointRejection.DuplicateMember)

        let unknown =
            observed
            |> List.filter (fun observation -> not observation.Known)
            |> List.map (fun observation -> GroupCheckpointRejection.UnknownMember observation.WorkItemId)

        [ if not (isValidGroupId candidate.GroupId) then
              GroupCheckpointRejection.InvalidGroupId candidate.GroupId
          if candidate.Members.IsEmpty then
              GroupCheckpointRejection.NoMembers
          yield! duplicates
          yield! unknown ]

    let private resolvedExecution (observation: GroupMemberObservation) =
        match observation.State, observation.Execution with
        | Some LiveWorkState.Active, ExecutionObservation.Resolved(executionId, _) -> Some executionId
        | _ -> None

    let private executionReason (observation: GroupMemberObservation) =
        match observation.Execution with
        | ExecutionObservation.Resolved _ -> None
        | ExecutionObservation.NoneActive -> Some(observation.WorkItemId, "no active execution belongs to this process")
        | ExecutionObservation.Ambiguous ids -> Some(observation.WorkItemId, $"""several executions could be this process ({String.concat ", " ids})""")
        | ExecutionObservation.Refused reason -> Some(observation.WorkItemId, reason)

    /// A grouped execution is in progress only when a member is active, and
    /// the checkpoint is recorded only under the caller's own executions.
    let private executionRejections (candidate: GroupCheckpointCandidate) (observed: GroupMemberObservation list) =
        let active = observed |> List.filter (fun observation -> observation.State = Some LiveWorkState.Active)

        match active, active |> List.choose resolvedExecution with
        | [], _ when observed |> List.forall _.Known -> [ GroupCheckpointRejection.NoActiveMember candidate.GroupId ]
        | [], _ -> []
        | _, [] -> [ GroupCheckpointRejection.NoOwnExecution(active |> List.choose executionReason) ]
        | _ -> []

    let private memberOf (observation: GroupMemberObservation) =
        { WorkItemId = observation.WorkItemId
          Standing = GroupMemberStanding.ofState observation.State
          State = observation.State
          ExecutionId = resolvedExecution observation
          Checkpoint = observation.LatestCheckpoint |> Option.map MemberCheckpointReference.ofRecorded }

    /// Decides whether a group checkpoint is durable, with the same
    /// durability rules as a work item's own checkpoint. `observed` holds one
    /// observation per requested member, in the requested order. Every
    /// independent problem is reported; nothing unknown is accepted.
    let verify
        (candidate: GroupCheckpointCandidate)
        (observed: GroupMemberObservation list)
        (durability: CheckpointObservations)
        : Result<GroupCheckpoint, GroupCheckpointRejection list> =
        let location = CheckpointVerification.durableLocation candidate.Repository durability

        let rejections =
            textRejections candidate
            @ membershipRejections candidate observed
            @ executionRejections candidate observed
            @ (match location with
               | Error rejections -> rejections |> List.map GroupCheckpointRejection.Checkpoint
               | Ok _ -> [])
            |> List.distinct

        match rejections, location with
        | [], Ok git ->
            Ok
                { groupId = candidate.GroupId
                  declaration = candidate.Declaration
                  members = observed |> List.map memberOf
                  decisions = candidate.Decisions |> List.choose RequiredText.tryCreate |> List.map RequiredText.value
                  summary = (RequiredText.tryCreate candidate.Summary).Value
                  nextAction = (RequiredText.tryCreate candidate.NextAction).Value
                  recordedAt = candidate.OccurredAt
                  location = git
                  verification =
                    { Status = VerificationStatus.Verified
                      Mechanism = DurabilityMechanism.GitRemoteObservation } }
        | [], Error _ -> Error [ GroupCheckpointRejection.Checkpoint(CheckpointRejection.UnknownGitState "the group checkpoint could not be verified") ]
        | rejections, _ -> Error rejections

    /// Active members whose own checkpoint does not cover the group commit.
    let warnings (checkpoint: GroupCheckpoint) =
        checkpoint.Members
        |> List.filter (fun memberItem -> memberItem.Standing = GroupMemberStanding.Active)
        |> List.choose (fun memberItem ->
            match memberItem.Checkpoint with
            | Some reference when reference.Commit = checkpoint.Commit -> None
            | latest -> Some(GroupCheckpointWarning.MemberCheckpointBehind(memberItem.WorkItemId, latest)))

/// The fields of a stored group checkpoint, before they are re-checked.
type StoredGroupMember =
    { WorkItemId: string
      Standing: string
      State: string option
      ExecutionId: string option
      CheckpointId: string option
      CheckpointExecutionId: string option
      CheckpointCommit: string option
      CheckpointRecordedAt: string option }

type StoredGroupCheckpoint =
    { GroupId: string
      DeclarationSource: string
      DeclarationPath: string option
      Members: StoredGroupMember list
      Decisions: string list
      Summary: string
      NextAction: string
      RecordedAt: string
      Repository: string
      Branch: string
      Commit: string
      RemoteName: string
      RemoteUrl: string option
      RemoteBranch: string
      RemoteCommit: string
      VerificationStatus: string
      Mechanism: string
      Paths: string list }

[<RequireQualifiedAccess>]
module StoredGroupCheckpoint =
    let private stateOf value =
        match value with
        | "ready" -> Some LiveWorkState.Ready
        | "active" -> Some LiveWorkState.Active
        | "blocked" -> Some LiveWorkState.Blocked
        | "complete" -> Some LiveWorkState.Complete
        | "abandoned" -> Some LiveWorkState.Abandoned
        | _ -> None

    let private memberProblems (stored: StoredGroupMember) =
        let checkpointFields =
            [ stored.CheckpointId; stored.CheckpointExecutionId; stored.CheckpointCommit; stored.CheckpointRecordedAt ]

        [ if not (WorkItemId.isValid stored.WorkItemId) then
              $"member '{stored.WorkItemId}' is not a valid work-item ID"
          match GroupMemberStanding.tryParse stored.Standing with
          | None -> $"member '{stored.WorkItemId}' standing '{stored.Standing}' is not active, completed, remaining or abandoned"
          | Some standing when GroupMemberStanding.ofState (stored.State |> Option.bind stateOf) <> standing ->
              $"member '{stored.WorkItemId}' standing '{stored.Standing}' contradicts its recorded state"
          | Some _ -> ()
          if stored.State |> Option.exists (stateOf >> Option.isNone) then
              $"member '{stored.WorkItemId}' state '{stored.State.Value}' is not a work state"
          if stored.ExecutionId |> Option.exists (fun id -> not (id.StartsWith "EXE-")) then
              $"member '{stored.WorkItemId}' execution '{stored.ExecutionId.Value}' is not an execution ID"
          if not (checkpointFields |> List.forall Option.isSome || checkpointFields |> List.forall Option.isNone) then
              $"member '{stored.WorkItemId}' checkpoint reference is incomplete"
          if stored.CheckpointCommit |> Option.exists (CommitId.tryParse >> Option.isNone) then
              $"member '{stored.WorkItemId}' checkpoint commit '{stored.CheckpointCommit.Value}' is not a full commit ID" ]

    /// Re-checks every invariant an accepted group checkpoint satisfies.
    let problems (stored: StoredGroupCheckpoint) =
        let commit = CommitId.tryParse stored.Commit
        let remoteCommit = CommitId.tryParse stored.RemoteCommit

        [ if not (GroupCheckpoint.isValidGroupId stored.GroupId) then
              $"group '{stored.GroupId}' is not a valid group ID"
          if stored.DeclarationSource <> "stored-declaration" && stored.DeclarationSource <> "planner-configuration" then
              $"declaration source '{stored.DeclarationSource}' is not supported"
          if stored.Members.IsEmpty then "members is empty"
          if (RequiredText.tryCreate stored.Summary).IsNone then "summary is blank"
          if (RequiredText.tryCreate stored.NextAction).IsNone then "nextAction is blank"
          if stored.Decisions |> List.exists (RequiredText.tryCreate >> Option.isNone) then "a decision is blank"
          if System.String.IsNullOrWhiteSpace stored.Branch then "branch is blank"
          if System.String.IsNullOrWhiteSpace stored.RemoteName then "remote is blank"
          if System.String.IsNullOrWhiteSpace stored.RemoteBranch then "remoteBranch is blank"
          if commit.IsNone then $"commit '{stored.Commit}' is not a full commit ID"
          if remoteCommit.IsNone then $"remoteCommit '{stored.RemoteCommit}' is not a full commit ID"
          if commit.IsSome && remoteCommit.IsSome && commit <> remoteCommit then
              "commit and remoteCommit differ; a group checkpoint is accepted only when they are equal"
          if stored.VerificationStatus <> "verified" then
              $"verification status '{stored.VerificationStatus}' is not 'verified'"
          if (DurabilityMechanism.tryParse stored.Mechanism).IsNone then
              $"verification mechanism '{stored.Mechanism}' is not supported"
          if not stored.Paths.IsEmpty then
              "a group checkpoint claims no paths; members' own checkpoints attribute every change (PRX-GRP-043)"
          if stored.Members |> List.forall (fun memberItem -> memberItem.ExecutionId.IsNone) then
              "no member names the execution the group checkpoint was recorded under"
          yield! stored.Members |> List.collect memberProblems ]
