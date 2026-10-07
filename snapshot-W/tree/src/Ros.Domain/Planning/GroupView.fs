namespace Ros.Domain.Planning

open System

/// One declared member as `work group show` presents it (PRX-GRP-073 phase
/// two). `Recorded` is the item's own recorded state; `Planning` is the
/// planner's derived view of it. The group owns neither (PRX-GRP-041).
type GroupMemberView =
    { WorkItemId: string
      /// None: the item is unknown to this repository's backlog and context.
      Recorded: MemberState option
      /// None: the item is not in the planning inventory.
      Planning: GroupMember option
      /// Members whose progress waits on this one (inverse of `GatedBy`).
      Gates: string list }

/// Partial-completion progress over every declared member (PRX-GRP-042).
type GroupViewProgress =
    { Total: int
      Complete: int
      Abandoned: int
      InProgress: int
      Runnable: int
      Blocked: int
      NotRunnable: int
      Unknown: int
      Statement: string }

/// A blocked member and the members it gates.
type BlockedMember = { WorkItemId: string; Gates: string list }

/// The read-only view of one stored group.
type GroupView =
    { Group: StoredGroup
      Members: GroupMemberView list
      Progress: GroupViewProgress
      Blocked: BlockedMember list
      /// Where the planner resolves the group to execute; None when the
      /// planner did not report the group.
      PlannerExecutionRepository: string option
      Notes: GroupNote list
      Statement: string }

[<RequireQualifiedAccess>]
module GroupView =
    let statement =
        "Read-only view: membership is advisory; every member keeps its own lifecycle, evidence and attribution, and the group never implies that every member succeeded (PRX-GRP-041/042/043)."

    /// The member's progress category: the planner's status, with abandoned
    /// kept apart from complete and an item outside the inventory unknown.
    let statusCode (entry: GroupMemberView) =
        match entry.Planning with
        | None -> "unknown"
        | Some planned when planned.PlanningState = PlanningWorkState.Abandoned -> "abandoned"
        | Some planned -> MemberStatus.code planned.Status

    let private gates (planned: GroupMember list) (id: string) =
        planned |> List.filter (fun other -> List.contains id other.GatedBy) |> List.map (fun other -> other.WorkItemId)

    let private progress (members: GroupMemberView list) =
        let codes = members |> List.map statusCode
        let count code = codes |> List.filter ((=) code) |> List.length
        let named code = members |> List.filter (statusCode >> (=) code) |> List.map (fun entry -> entry.WorkItemId)

        let clause label ids =
            match ids with
            | [] -> []
            | ids -> [ $"""{label}: {String.concat ", " ids}""" ]

        let details = clause "blocked" (named "blocked") @ clause "abandoned" (named "abandoned") @ clause "unknown" (named "unknown")

        { Total = members.Length
          Complete = count "complete"
          Abandoned = count "abandoned"
          InProgress = count "in-progress"
          Runnable = count "runnable"
          Blocked = count "blocked"
          NotRunnable = count "not-runnable"
          Unknown = count "unknown"
          Statement =
            String.concat "; " ([ $"""{count "complete"} of {members.Length} complete""" ] @ details)
            + "; each member completes on its own evidence (PRX-GRP-042)" }

    /// Builds the view of `stored` from each item's own recorded state and
    /// the planner's report of the same group, if it has one.
    let build (stored: StoredGroup) (states: Map<string, MemberState>) (planned: WorkGroup option) : GroupView =
        let plannedMembers = planned |> Option.map (fun group -> group.Members) |> Option.defaultValue []

        let members =
            stored.Declaration.Members
            |> List.map (fun id ->
                { WorkItemId = id
                  Recorded = states.TryFind id
                  Planning = plannedMembers |> List.tryFind (fun entry -> entry.WorkItemId = id)
                  Gates = gates plannedMembers id })

        { Group = stored
          Members = members
          Progress = progress members
          Blocked =
            members
            |> List.filter (statusCode >> (=) "blocked")
            |> List.map (fun entry -> { WorkItemId = entry.WorkItemId; Gates = entry.Gates })
          PlannerExecutionRepository = planned |> Option.map (fun group -> group.ExecutionRepository)
          Notes = planned |> Option.map (fun group -> group.Notes) |> Option.defaultValue []
          Statement = statement }

    /// The view of group `id`, or None when no group with that ID is stored.
    let tryShow (store: GroupStore) (states: Map<string, MemberState>) (report: GroupingReport) (id: string) : GroupView option =
        GroupStore.tryFind id store
        |> Option.map (fun stored -> build stored states (report.Groups |> List.tryFind (fun group -> WorkGroupId.value group.Id = id)))
