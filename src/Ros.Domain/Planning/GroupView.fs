namespace Ros.Domain.Planning

open System

// `work group show` (PRAXIS-GROUP-02; phase two of PRX-GRP-073): a read-only
// view of one stored declaration. It joins the declaration, each member's own
// recorded lifecycle state and the planner's advisory view of the same group.
// Members are never collapsed into the group (PRX-GRP-003): every member
// reports its own states, and progress is partial by construction
// (PRX-GRP-042). Everything here is pure: no clock, filesystem or Git.

/// Where the shown execution repository comes from.
[<RequireQualifiedAccess>]
type RepositoryBasis =
    | Declared
    | Derived
    | Unknown

[<RequireQualifiedAccess>]
module RepositoryBasis =
    let code basis =
        match basis with
        | RepositoryBasis.Declared -> "declared"
        | RepositoryBasis.Derived -> "derived"
        | RepositoryBasis.Unknown -> "unknown"

/// One member as it stands now. `None` means the value is unavailable (the
/// item is not recorded, or the planner could not run), never a default.
type GroupViewMember =
    { WorkItemId: string
      RecordedState: string option
      PlanningState: PlanningWorkState option
      Status: MemberStatus option
      /// Blocked members this one waits on, directly or transitively.
      GatedBy: string list
      /// Members that wait on this one while it is blocked.
      Gates: string list }

type BlockedMember =
    { WorkItemId: string
      Gates: string list }

type GroupViewProgress =
    { Total: int
      Complete: int
      InProgress: int
      Runnable: int
      Blocked: int
      NotRunnable: int
      Unknown: int
      Statement: string }

type GroupView =
    { Declaration: StoredGroup
      ExecutionRepository: string option
      RepositoryBasis: RepositoryBasis
      Members: GroupViewMember list
      Progress: GroupViewProgress
      Blocked: BlockedMember list
      PlannerNotes: GroupNote list
      Unavailable: string list }

[<RequireQualifiedAccess>]
module GroupView =
    /// The stored declaration with this ID; `None` when it is not declared.
    let tryFind (stored: StoredGroup list) (id: string) : StoredGroup option =
        stored |> List.tryFind (fun entry -> entry.Group.Id = id)

    let private recordedState (status: GroupMemberStatus) =
        match status with
        | GroupMemberStatus.Open state
        | GroupMemberStatus.Terminal state -> state

    let private progress (members: GroupViewMember list) : GroupViewProgress =
        let count status = members |> List.filter (fun entry -> entry.Status = Some status) |> List.length
        let complete = count MemberStatus.Complete
        let total = members.Length
        let unknown = members |> List.filter (fun entry -> entry.Status.IsNone) |> List.length

        let blocked =
            members
            |> List.filter (fun entry -> entry.Status = Some MemberStatus.Blocked)
            |> List.map (fun entry -> entry.WorkItemId)

        let blockedText = if blocked.IsEmpty then "" else $"""; blocked: {String.concat ", " blocked}"""
        let unknownText = if unknown = 0 then "" else $"; {unknown} with unknown planning state"

        { Total = total
          Complete = complete
          InProgress = count MemberStatus.InProgress
          Runnable = count MemberStatus.Runnable
          Blocked = blocked.Length
          NotRunnable = count MemberStatus.NotRunnable
          Unknown = unknown
          Statement =
            $"{complete} of {total} complete{blockedText}{unknownText}; each member completes on its own evidence and the group never implies that every member succeeded (PRX-GRP-042)" }

    /// Builds the view. `planned` is the planner's group with the same ID,
    /// or `Error reason` when the planner could not produce one; the view
    /// then reports planning state as unavailable rather than guessing.
    let build (stored: StoredGroup) (statuses: Map<string, GroupMemberStatus>) (planned: Result<WorkGroup, string>) : GroupView =
        let declared = stored.Group
        let ids = declared.Members |> List.distinct
        let plannedGroup = planned |> Result.toOption

        let plannedMember (id: string) =
            plannedGroup |> Option.bind (fun group -> group.Members |> List.tryFind (fun entry -> entry.WorkItemId = id))

        let gatedBy (id: string) =
            plannedMember id |> Option.map (fun entry -> entry.GatedBy |> List.filter (fun other -> List.contains other ids)) |> Option.defaultValue []

        let gates (id: string) = ids |> List.filter (fun other -> other <> id && List.contains id (gatedBy other))

        let members =
            ids
            |> List.map (fun id ->
                let entry = plannedMember id

                ({ WorkItemId = id
                   RecordedState = statuses.TryFind id |> Option.map recordedState
                   PlanningState = entry |> Option.map (fun value -> value.PlanningState)
                   Status = entry |> Option.map (fun value -> value.Status)
                   GatedBy = gatedBy id
                   Gates = gates id }
                : GroupViewMember))

        let blocked =
            members
            |> List.filter (fun entry -> entry.Status = Some MemberStatus.Blocked)
            |> List.map (fun entry -> ({ WorkItemId = entry.WorkItemId; Gates = entry.Gates }: BlockedMember))

        let repository, basis =
            match declared.ExecutionRepository, plannedGroup with
            | Some name, _ -> Some name, RepositoryBasis.Declared
            | None, Some group -> Some group.ExecutionRepository, RepositoryBasis.Derived
            | None, None -> None, RepositoryBasis.Unknown

        let unavailable =
            [ match planned with
              | Error reason -> yield $"planning state is unavailable: {reason}"
              | Ok _ -> ()
              yield!
                  members
                  |> List.filter (fun entry -> entry.RecordedState.IsNone)
                  |> List.map (fun entry -> $"{entry.WorkItemId} is not a recorded work item (backlog or live context); its states are unknown") ]

        { Declaration = stored
          ExecutionRepository = repository
          RepositoryBasis = basis
          Members = members
          Progress = progress members
          Blocked = blocked
          PlannerNotes = plannedGroup |> Option.map (fun group -> group.Notes) |> Option.defaultValue []
          Unavailable = unavailable }

    /// The planner's group with this ID from a grouping report.
    let planned (report: GroupingReport) (id: string) : Result<WorkGroup, string> =
        report.Groups
        |> List.tryFind (fun group -> WorkGroupId.value group.Id = id)
        |> Option.map Ok
        |> Option.defaultValue (Error $"the planner did not report group {id}")
