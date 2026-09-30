namespace Ros.Domain.Planning

open System
open Ros.Domain.Work

/// A member's own latest durable checkpoint, as a group checkpoint refers to
/// it. It is a reference only: the member's checkpoint history stays the
/// member's, and the group checkpoint never copies, replaces or re-records
/// it (PRX-GRP-044).
type MemberCheckpointReference =
    { Member: string
      CheckpointId: string
      ExecutionId: string
      Commit: string
      RecordedAt: string }

/// Members as their own lifecycle states placed them when the group was
/// checkpointed. Abandoned members are kept apart from completed ones, so a
/// group checkpoint never implies every member succeeded (PRX-GRP-042).
type GroupCheckpointMembers =
    { Active: string list
      Completed: string list
      Abandoned: string list
      Remaining: string list }

/// The checkpointing executor's own active execution of one active member.
type GroupMemberExecution = { Member: string; ExecutionId: string }

/// A durable group-level checkpoint (PRX-GRP-044): where the grouped
/// execution stands, verified exactly as `work checkpoint` verifies a work
/// item's. It records no changed paths and claims no commit for any member:
/// what each member changed stays attributed by that member's own
/// checkpoints (PRX-GRP-043, PRAXIS-CONT-12).
type GroupCheckpoint =
    { Id: string
      GroupId: string
      Summary: string
      NextAction: string
      /// Architectural decisions shared by the members, available to later
      /// member executions.
      SharedDecisions: string list
      Progress: GroupCheckpointMembers
      /// Each member's own latest checkpoint, referenced, in member order.
      MemberCheckpoints: MemberCheckpointReference list
      /// Members with no checkpoint of their own yet.
      UncheckpointedMembers: string list
      /// The executor's own executions of active members.
      Executions: GroupMemberExecution list
      Location: GitDurableLocation
      RecordedAt: string
      RecordedBy: string }

/// What `work group checkpoint` was asked to record.
type GroupCheckpointRequest =
    { GroupId: string
      Repository: string
      Summary: string
      NextAction: string
      SharedDecisions: string list
      OccurredAt: string
      RecordedBy: string }

/// Everything the decision needs, observed before it is made.
type GroupCheckpointObservations =
    { Stored: StoredGroup list
      /// Each known work item's effective lifecycle state.
      Lifecycle: Map<string, string>
      /// Each work item's own latest durable checkpoint.
      MemberCheckpoints: Map<string, MemberCheckpointReference>
      /// For each active member, whether one of its active executions is
      /// this executor's own.
      Executions: Map<string, ExecutionObservation>
      /// Group checkpoints already recorded, oldest first.
      History: GroupCheckpoint list
      /// `work checkpoint`'s durability verdict on HEAD now.
      Durability: Result<GitDurableLocation, CheckpointRejection list> }

[<RequireQualifiedAccess>]
type GroupCheckpointRejection =
    | BlankSummary
    | BlankNextAction
    | BlankSharedDecision
    | InvalidTimestamp of value: string
    | UnknownGroup of id: string
    | NoActiveMember of groupId: string
    | NoOwnExecution of groupId: string * reasons: string list
    | PredatesHistory of value: string * latest: string
    | NotDurable of CheckpointRejection

/// A structural defect in stored group checkpoints, reported by `validate`.
type GroupCheckpointFinding = { CheckpointId: string; Field: string; Message: string }

/// Pure decisions over group checkpoints. Nothing here can express a change
/// to a member's lifecycle, checkpoint history or attribution.
[<RequireQualifiedAccess>]
module GroupCheckpoints =
    /// Group checkpoint IDs are `<GROUP-ID>/GCP-<sequence>`, sequential per
    /// group from 1.
    let checkpointId (groupId: string) (sequence: int) = $"{groupId}/GCP-%03d{sequence}"

    let private isTimestamp (value: string) = Text.tryTimestamp value |> Option.isSome

    let code rejection =
        match rejection with
        | GroupCheckpointRejection.BlankSummary -> "blank-summary"
        | GroupCheckpointRejection.BlankNextAction -> "blank-next-action"
        | GroupCheckpointRejection.BlankSharedDecision -> "blank-shared-decision"
        | GroupCheckpointRejection.InvalidTimestamp _ -> "invalid-timestamp"
        | GroupCheckpointRejection.UnknownGroup _ -> "unknown-group"
        | GroupCheckpointRejection.NoActiveMember _ -> "no-active-member"
        | GroupCheckpointRejection.NoOwnExecution _ -> "missing-execution"
        | GroupCheckpointRejection.PredatesHistory _ -> "predates-history"
        | GroupCheckpointRejection.NotDurable rejection -> CheckpointRejection.code rejection

    let message rejection =
        match rejection with
        | GroupCheckpointRejection.BlankSummary -> "a group checkpoint requires a non-blank --summary of the completed milestone"
        | GroupCheckpointRejection.BlankNextAction -> "a group checkpoint requires a non-blank --next-action"
        | GroupCheckpointRejection.BlankSharedDecision -> "--decision must not be blank"
        | GroupCheckpointRejection.InvalidTimestamp value -> $"--occurred-at '{value}' is not a timestamp"
        | GroupCheckpointRejection.UnknownGroup id ->
            $"group {id} is not a stored group; declare it with work group create (a group only in planner configuration cannot be checkpointed)"
        | GroupCheckpointRejection.NoActiveMember groupId -> $"no member of {groupId} is active; only a group whose execution is under way can be checkpointed"
        | GroupCheckpointRejection.NoOwnExecution(groupId, reasons) ->
            let detail = if reasons.IsEmpty then "" else " (" + String.Join("; ", reasons) + ")"
            $"no active execution of an active member of {groupId} belongs to this process's identity{detail}"
        | GroupCheckpointRejection.PredatesHistory(value, latest) -> $"--occurred-at '{value}' predates the group's latest checkpoint ({latest})"
        | GroupCheckpointRejection.NotDurable rejection -> CheckpointRejection.message rejection

    /// A safe remedy. None discards work or rewrites history.
    let remedy rejection =
        match rejection with
        | GroupCheckpointRejection.BlankSummary -> "Pass --summary \"what this milestone completed\"."
        | GroupCheckpointRejection.BlankNextAction -> "Pass --next-action \"the group's next intended step\"."
        | GroupCheckpointRejection.BlankSharedDecision -> "Give each --decision a non-blank text, or omit it."
        | GroupCheckpointRejection.InvalidTimestamp _ -> "Pass the real current time, e.g. `date -u +%Y-%m-%dT%H:%M:%S.000Z`."
        | GroupCheckpointRejection.UnknownGroup _ -> "Check the ID with './ros work group show'; declare the group with 'work group create'."
        | GroupCheckpointRejection.NoActiveMember _ -> "Start a member with 'work start', then checkpoint the group."
        | GroupCheckpointRejection.NoOwnExecution _ ->
            "Begin or continue an active member under your own identity ('work start' or 'work continue'); never use another executor's execution."
        | GroupCheckpointRejection.PredatesHistory _ -> "Pass the real current time."
        | GroupCheckpointRejection.NotDurable rejection -> CheckpointRejection.remedy rejection

    /// Validation and argument problems are the caller's to fix (exit 2).
    let isArgumentError rejection =
        match rejection with
        | GroupCheckpointRejection.BlankSummary
        | GroupCheckpointRejection.BlankNextAction
        | GroupCheckpointRejection.BlankSharedDecision
        | GroupCheckpointRejection.InvalidTimestamp _ -> true
        | _ -> false

    /// Members partitioned by their own effective lifecycle state, in
    /// member order. A member no longer known is remaining, not completed.
    let progress (lifecycle: Map<string, string>) (members: string list) : GroupCheckpointMembers =
        let having state = members |> List.filter (fun id -> lifecycle.TryFind id = Some state)
        let settled = set [ "active"; "complete"; "abandoned" ]

        { Active = having "active"
          Completed = having "complete"
          Abandoned = having "abandoned"
          Remaining = members |> List.filter (fun id -> lifecycle.TryFind id |> Option.forall (settled.Contains >> not)) }

    let private latest (history: GroupCheckpoint list) =
        history
        |> List.choose (fun checkpoint -> Text.tryTimestamp checkpoint.RecordedAt |> Option.map (fun at -> at, checkpoint.RecordedAt))
        |> List.sortBy fst
        |> List.tryLast

    let private textRejections (request: GroupCheckpointRequest) =
        [ if String.IsNullOrWhiteSpace request.Summary then
              GroupCheckpointRejection.BlankSummary
          if String.IsNullOrWhiteSpace request.NextAction then
              GroupCheckpointRejection.BlankNextAction
          if request.SharedDecisions |> List.exists String.IsNullOrWhiteSpace then
              GroupCheckpointRejection.BlankSharedDecision ]

    let private timeRejections (request: GroupCheckpointRequest) (history: GroupCheckpoint list) =
        match Text.tryTimestamp request.OccurredAt with
        | None -> [ GroupCheckpointRejection.InvalidTimestamp request.OccurredAt ]
        | Some at ->
            latest history
            |> Option.filter (fun (previous, _) -> at < previous)
            |> Option.map (fun (_, value) -> GroupCheckpointRejection.PredatesHistory(request.OccurredAt, value))
            |> Option.toList

    let private ownExecutions (observations: GroupCheckpointObservations) (active: string list) =
        active
        |> List.map (fun id -> id, observations.Executions.TryFind id |> Option.defaultValue ExecutionObservation.NoneActive)

    let private executionReason (id: string, observation: ExecutionObservation) =
        match observation with
        | ExecutionObservation.Resolved _ -> None
        | ExecutionObservation.NoneActive -> Some $"{id}: none is yours"
        | ExecutionObservation.Ambiguous ids -> Some $"""{id}: several could be yours ({String.Join(", ", ids)})"""
        | ExecutionObservation.Refused reason -> Some $"{id}: {reason}"

    /// Decides whether the group may be checkpointed now. Every problem that
    /// can be known independently is reported. The group must be stored,
    /// have an active member, and this executor must hold an active execution
    /// of one; HEAD must pass `work checkpoint`'s own durability
    /// verification. Member checkpoints are referenced, never recorded.
    let decide (observations: GroupCheckpointObservations) (request: GroupCheckpointRequest) : Result<GroupCheckpoint, GroupCheckpointRejection list> =
        let group = observations.Stored |> List.tryFind (fun candidate -> candidate.Declaration.Id = request.GroupId)
        let history = observations.History |> List.filter (fun checkpoint -> checkpoint.GroupId = request.GroupId)
        let members = group |> Option.map (fun group -> group.Declaration.Members) |> Option.defaultValue []
        let progress = progress observations.Lifecycle members
        let executions = ownExecutions observations progress.Active

        let resolved =
            executions
            |> List.choose (fun (id, observation) ->
                match observation with
                | ExecutionObservation.Resolved(executionId, _) -> Some { Member = id; ExecutionId = executionId }
                | _ -> None)

        let membership =
            match group with
            | None -> [ GroupCheckpointRejection.UnknownGroup request.GroupId ]
            | Some _ when progress.Active.IsEmpty -> [ GroupCheckpointRejection.NoActiveMember request.GroupId ]
            | Some _ when resolved.IsEmpty -> [ GroupCheckpointRejection.NoOwnExecution(request.GroupId, executions |> List.choose executionReason) ]
            | Some _ -> []

        let durability =
            match observations.Durability with
            | Ok _ -> []
            | Error rejections -> rejections |> List.map GroupCheckpointRejection.NotDurable

        let rejections = textRejections request @ timeRejections request history @ membership @ durability

        match rejections, observations.Durability with
        | [], Ok location ->
            Ok
                { Id = checkpointId request.GroupId (history.Length + 1)
                  GroupId = request.GroupId
                  Summary = request.Summary.Trim()
                  NextAction = request.NextAction.Trim()
                  SharedDecisions = request.SharedDecisions |> List.map (fun decision -> decision.Trim())
                  Progress = progress
                  MemberCheckpoints = members |> List.choose observations.MemberCheckpoints.TryFind
                  UncheckpointedMembers = members |> List.filter (observations.MemberCheckpoints.ContainsKey >> not)
                  Executions = resolved
                  Location = location
                  RecordedAt = request.OccurredAt
                  RecordedBy = request.RecordedBy }
        | _ -> Error rejections

    /// `validate`'s structural checks. `groups` are the stored group IDs;
    /// `memberCheckpoints` maps each recorded work-item checkpoint ID to the
    /// work item it belongs to. A reference to a checkpoint that is not the
    /// named member's own would let one member claim another's work
    /// (PRX-GRP-043), so it is a finding.
    let findings (groups: Set<string>) (memberCheckpoints: Map<string, string>) (stored: GroupCheckpoint list) : GroupCheckpointFinding list =
        let duplicateIds =
            stored |> List.countBy (fun checkpoint -> checkpoint.Id) |> List.filter (snd >> (<) 1) |> List.map fst |> Set.ofList

        let sequences =
            stored
            |> List.groupBy (fun checkpoint -> checkpoint.GroupId)
            |> List.collect (fun (groupId, checkpoints) -> checkpoints |> List.mapi (fun index checkpoint -> checkpoint.Id, checkpointId groupId (index + 1)))
            |> Map.ofList

        stored
        |> List.collect (fun checkpoint ->
            let finding field text = { CheckpointId = checkpoint.Id; Field = field; Message = text }
            let location = checkpoint.Location

            [ if not (groups.Contains checkpoint.GroupId) then
                  yield finding "groupId" $"group {checkpoint.GroupId} is not a stored group"
              if duplicateIds.Contains checkpoint.Id then
                  yield finding "id" $"group checkpoint {checkpoint.Id} is stored more than once"
              elif sequences.TryFind checkpoint.Id <> Some checkpoint.Id then
                  yield finding "id" $"'{checkpoint.Id}' is not the next checkpoint of {checkpoint.GroupId} in sequence"
              if String.IsNullOrWhiteSpace checkpoint.Summary then
                  yield finding "summary" "the summary is blank"
              if String.IsNullOrWhiteSpace checkpoint.NextAction then
                  yield finding "nextAction" "the next action is blank"
              if not (isTimestamp checkpoint.RecordedAt) then
                  yield finding "recordedAt" $"'{checkpoint.RecordedAt}' is not a timestamp"
              if String.IsNullOrWhiteSpace checkpoint.RecordedBy then
                  yield finding "recordedBy" "the checkpointing actor is missing"
              if location.LocalCommit <> location.RemoteCommit then
                  yield finding "location" "the local and remote commits differ; a group checkpoint is only recorded when they are equal"
              if checkpoint.Executions.IsEmpty then
                  yield finding "executions" "no execution of an active member is recorded"
              for execution in checkpoint.Executions do
                  if not (List.contains execution.Member checkpoint.Progress.Active) then
                      yield finding "executions" $"execution {execution.ExecutionId} names {execution.Member}, which was not an active member"
              for reference in checkpoint.MemberCheckpoints do
                  match memberCheckpoints.TryFind reference.CheckpointId with
                  | None -> yield finding "memberCheckpoints" $"{reference.Member}'s checkpoint {reference.CheckpointId} is not a recorded checkpoint"
                  | Some owner when owner <> reference.Member ->
                      yield finding "memberCheckpoints" $"checkpoint {reference.CheckpointId} belongs to {owner}, not {reference.Member}; a member never claims another's checkpoint"
                  | Some _ -> () ])
