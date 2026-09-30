namespace Ros.Domain.Planning

open System

/// One member of a declared group as it stands now: its own recorded
/// lifecycle state and the planner's own classification, side by side. A
/// group view never merges or rewrites either (PRX-GRP-002, PRX-GRP-041).
type GroupMemberView =
    { WorkItem: string
      Title: string option
      /// `None`: Praxis does not track the item (neither backlog nor live work).
      RecordedState: string option
      /// `None`: the planner has no analysis for the item.
      PlanningState: PlanningWorkState option
      BlockReason: string option
      /// Open hard work-item prerequisites, members or not.
      WaitsOn: string list }

/// Partial-completion progress (PRX-GRP-042). Abandoned members are counted
/// apart from completed ones, so progress never implies every member
/// succeeded.
type DeclaredGroupProgress =
    { Total: int
      Complete: int
      Abandoned: int
      Open: int
      Blocked: int
      Unknown: int }

/// A blocked member and the pending work it holds back, split into fellow
/// members and items outside the group.
type BlockedMember =
    { WorkItem: string
      PlanningState: PlanningWorkState option
      Reasons: string list
      GatesMembers: string list
      GatesOthers: string list }

type GroupView =
    { Declaration: StoredGroupDeclaration
      Members: GroupMemberView list
      Progress: DeclaredGroupProgress
      Blocked: BlockedMember list }

/// `work group show`: a read-only projection of one stored declaration over
/// the planner's analysis (PRX-GRP-073 phase two). Pure: it reads recorded
/// state and planning state and returns a value; it changes nothing.
[<RequireQualifiedAccess>]
module GroupView =
    let private blockedPlanningStates =
        set [ PlanningWorkState.Blocked; PlanningWorkState.AwaitingHuman; PlanningWorkState.AwaitingEvidence ]

    let private recordedState (standing: MemberStanding) =
        match standing with
        | MemberStanding.Unknown -> None
        | MemberStanding.Open state
        | MemberStanding.Terminal state -> Some state

    let isBlocked (view: GroupMemberView) =
        view.RecordedState = Some "blocked"
        || view.PlanningState |> Option.exists blockedPlanningStates.Contains

    let private memberView (standingOf: string -> MemberStanding) (items: Map<string, ItemAnalysis>) (id: string) : GroupMemberView =
        let item = items.TryFind id

        { WorkItem = id
          Title = item |> Option.map (fun found -> found.Title) |> Option.filter (String.IsNullOrWhiteSpace >> not)
          RecordedState = recordedState (standingOf id)
          PlanningState = item |> Option.map (fun found -> found.PlanningState)
          BlockReason = item |> Option.bind (fun found -> found.BlockReason)
          WaitsOn = item |> Option.map Graph.openHardPrerequisites |> Option.defaultValue [] }

    let private progress (members: GroupMemberView list) : DeclaredGroupProgress =
        let count predicate = members |> List.filter predicate |> List.length
        let recorded state (view: GroupMemberView) = view.RecordedState = Some state

        { Total = members.Length
          Complete = count (recorded "complete")
          Abandoned = count (recorded "abandoned")
          Open = count (fun view -> view.RecordedState |> Option.exists (fun state -> state <> "complete" && state <> "abandoned"))
          Blocked = count isBlocked
          Unknown = count (fun view -> view.RecordedState.IsNone) }

    let private blockedMember (memberIds: Set<string>) (unlocks: Map<string, UnlockValue>) (view: GroupMemberView) : BlockedMember =
        let dependents =
            unlocks.TryFind view.WorkItem |> Option.map (fun unlock -> unlock.TransitiveDependents) |> Option.defaultValue []

        let reasons =
            [ yield! view.BlockReason |> Option.toList
              yield! view.WaitsOn |> List.map (fun prerequisite -> $"waits on {prerequisite}") ]

        { WorkItem = view.WorkItem
          PlanningState = view.PlanningState
          Reasons = reasons
          GatesMembers = dependents |> List.filter memberIds.Contains
          GatesOthers = dependents |> List.filter (memberIds.Contains >> not) }

    /// Members keep their declared order; nothing about a member is changed.
    let project (standingOf: string -> MemberStanding) (analysis: PlanningAnalysis) (entry: StoredGroupDeclaration) : GroupView =
        let items = analysis.Items |> List.map (fun item -> item.Id, item) |> Map.ofList
        let unlocks = analysis.Unlocks |> List.map (fun unlock -> unlock.WorkItem, unlock) |> Map.ofList
        let members = entry.Group.Members |> List.map (memberView standingOf items)
        let memberIds = entry.Group.Members |> Set.ofList

        { Declaration = entry
          Members = members
          Progress = progress members
          Blocked = members |> List.filter isBlocked |> List.map (blockedMember memberIds unlocks) }

    /// The stored declaration with this ID, if any.
    let find (stored: StoredGroupDeclaration list) (id: string) =
        stored |> List.tryFind (fun entry -> entry.Group.Id = id)

    let progressStatement (progress: DeclaredGroupProgress) =
        let unknown = if progress.Unknown > 0 then $", {progress.Unknown} not tracked" else ""

        $"{progress.Complete} of {progress.Total} complete, {progress.Abandoned} abandoned, {progress.Open} open ({progress.Blocked} blocked){unknown}; each member completes on its own"
