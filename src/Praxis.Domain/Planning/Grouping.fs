namespace Praxis.Domain.Planning

open System
open System.Text.RegularExpressions

// Evidence-based work groups (requirements/PLANNING-WORK-GROUPS.md). A group
// is advisory: computing one never changes a member's lifecycle state,
// attribution or identity (PRX-GRP-002, 041, 043). Everything here is a pure,
// deterministic function of the planning input (PRX-GRP-075).

type WorkGroupId = WorkGroupId of string

[<RequireQualifiedAccess>]
module WorkGroupId =
    let value (WorkGroupId id) = id

/// PRX-GRP-021: how a grouping signal is known.
[<RequireQualifiedAccess>]
type SignalBasis =
    | Explicit
    | Derived
    | Inferred

[<RequireQualifiedAccess>]
module SignalBasis =
    let all = [ SignalBasis.Explicit; SignalBasis.Derived; SignalBasis.Inferred ]

    let code basis =
        match basis with
        | SignalBasis.Explicit -> "explicit"
        | SignalBasis.Derived -> "derived"
        | SignalBasis.Inferred -> "inferred"

    let tryParse value = all |> List.tryFind (fun basis -> code basis = value)

/// PRX-GRP-020/022: one reason two work items share context. Listed in the
/// evidence priority of PRX-GRP-022.
[<RequireQualifiedAccess>]
type AffinitySignal =
    | DeclaredGroup of groupId: string
    | ArchitectureDecision of decision: string
    | SharedArea of area: string
    | HardDependency of dependent: string * prerequisite: string * origin: DependencyOrigin
    | SharedRequirement of requirement: string
    | SharedDeclaredPath of path: string
    | SameBranch of branch: string
    | RequirementFamily of family: string
    | SimilarTitle of words: string list

[<RequireQualifiedAccess>]
module AffinitySignal =
    let priority signal =
        match signal with
        | AffinitySignal.DeclaredGroup _ -> 1
        | AffinitySignal.ArchitectureDecision _ -> 1
        | AffinitySignal.SharedArea _ -> 2
        | AffinitySignal.HardDependency _ -> 3
        | AffinitySignal.SharedRequirement _ -> 4
        | AffinitySignal.SharedDeclaredPath _ -> 5
        | AffinitySignal.SameBranch _ -> 5
        | AffinitySignal.RequirementFamily _ -> 7
        | AffinitySignal.SimilarTitle _ -> 7

    let code signal =
        match signal with
        | AffinitySignal.DeclaredGroup _ -> "declared-group"
        | AffinitySignal.ArchitectureDecision _ -> "architecture-decision"
        | AffinitySignal.SharedArea _ -> "shared-area"
        | AffinitySignal.HardDependency _ -> "hard-dependency"
        | AffinitySignal.SharedRequirement _ -> "shared-requirement"
        | AffinitySignal.SharedDeclaredPath _ -> "shared-declared-path"
        | AffinitySignal.SameBranch _ -> "same-branch"
        | AffinitySignal.RequirementFamily _ -> "requirement-family"
        | AffinitySignal.SimilarTitle _ -> "similar-title"

    let detail signal =
        match signal with
        | AffinitySignal.DeclaredGroup id -> id
        | AffinitySignal.ArchitectureDecision decision -> decision
        | AffinitySignal.SharedArea area -> area
        | AffinitySignal.HardDependency(dependent, prerequisite, _) -> $"{dependent}>{prerequisite}"
        | AffinitySignal.SharedRequirement requirement -> requirement
        | AffinitySignal.SharedDeclaredPath path -> path
        | AffinitySignal.SameBranch branch -> branch
        | AffinitySignal.RequirementFamily family -> family
        | AffinitySignal.SimilarTitle words -> String.concat "," words

    let basis signal =
        match signal with
        | AffinitySignal.DeclaredGroup _
        | AffinitySignal.ArchitectureDecision _
        | AffinitySignal.SharedArea _
        | AffinitySignal.SharedDeclaredPath _ -> SignalBasis.Explicit
        | AffinitySignal.HardDependency(_, _, DependencyOrigin.Structured) -> SignalBasis.Explicit
        | AffinitySignal.HardDependency(_, _, DependencyOrigin.Inferred) -> SignalBasis.Inferred
        | AffinitySignal.SharedRequirement _
        | AffinitySignal.SameBranch _ -> SignalBasis.Derived
        | AffinitySignal.RequirementFamily _
        | AffinitySignal.SimilarTitle _ -> SignalBasis.Inferred

    /// PRX-GRP-060: the affinity one signal can establish on its own. Title
    /// similarity and ID families never exceed Low (PRX-GRP-022).
    let level signal =
        match signal with
        | AffinitySignal.DeclaredGroup _
        | AffinitySignal.ArchitectureDecision _
        | AffinitySignal.SharedDeclaredPath _
        | AffinitySignal.SameBranch _ -> ContextAffinity.High
        | AffinitySignal.SharedArea _
        | AffinitySignal.HardDependency _
        | AffinitySignal.SharedRequirement _ -> ContextAffinity.Medium
        | AffinitySignal.RequirementFamily _
        | AffinitySignal.SimilarTitle _ -> ContextAffinity.Low

    /// How sure the planner is that the signal is real (PRX-GRP-021).
    let confidence signal =
        match basis signal with
        | SignalBasis.Explicit -> EvidenceConfidence.High
        | SignalBasis.Derived -> EvidenceConfidence.Medium
        | SignalBasis.Inferred -> EvidenceConfidence.Low

    let describe signal =
        match signal with
        | AffinitySignal.DeclaredGroup id -> $"declared together in {id}"
        | AffinitySignal.ArchitectureDecision decision -> $"governed by architecture decision {decision}"
        | AffinitySignal.SharedArea area -> $"same area '{area}'"
        | AffinitySignal.HardDependency(dependent, prerequisite, origin) -> $"{dependent} depends on {prerequisite} ({DependencyOrigin.code origin})"
        | AffinitySignal.SharedRequirement requirement -> $"both cite requirement {requirement}"
        | AffinitySignal.SharedDeclaredPath path -> $"both declare path {path}"
        | AffinitySignal.SameBranch branch -> $"both checkpointed on branch {branch}"
        | AffinitySignal.RequirementFamily family -> $"same ID family {family}"
        | AffinitySignal.SimilarTitle words -> $"""similar titles ({String.concat ", " words})"""

    let tryParse (code: string) (detail: string) (basis: SignalBasis) =
        match code with
        | "declared-group" -> Some(AffinitySignal.DeclaredGroup detail)
        | "architecture-decision" -> Some(AffinitySignal.ArchitectureDecision detail)
        | "shared-area" -> Some(AffinitySignal.SharedArea detail)
        | "hard-dependency" ->
            match detail.Split('>') with
            | [| dependent; prerequisite |] ->
                let origin = if basis = SignalBasis.Inferred then DependencyOrigin.Inferred else DependencyOrigin.Structured
                Some(AffinitySignal.HardDependency(dependent, prerequisite, origin))
            | _ -> None
        | "shared-requirement" -> Some(AffinitySignal.SharedRequirement detail)
        | "shared-declared-path" -> Some(AffinitySignal.SharedDeclaredPath detail)
        | "same-branch" -> Some(AffinitySignal.SameBranch detail)
        | "requirement-family" -> Some(AffinitySignal.RequirementFamily detail)
        | "similar-title" -> Some(AffinitySignal.SimilarTitle(detail.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList))
        | _ -> None

/// PRX-GRP-060: the context two items share, and why.
type PairAffinity =
    { Left: string
      Right: string
      Level: ContextAffinity
      Signals: AffinitySignal list
      Confidence: EvidenceConfidence
      Statement: string }

/// PRX-GRP-051: where an item's implementation happens.
[<RequireQualifiedAccess>]
type ExecutionLocation =
    | Repository of name: string
    | UnknownExternal

[<RequireQualifiedAccess>]
module ExecutionLocation =
    let describe location =
        match location with
        | ExecutionLocation.Repository name -> name
        | ExecutionLocation.UnknownExternal -> "unknown external repository"

[<RequireQualifiedAccess>]
type MemberStatus =
    | Complete
    | Runnable
    | InProgress
    | Blocked
    | NotRunnable

[<RequireQualifiedAccess>]
module MemberStatus =
    let all =
        [ MemberStatus.Complete
          MemberStatus.Runnable
          MemberStatus.InProgress
          MemberStatus.Blocked
          MemberStatus.NotRunnable ]

    let code status =
        match status with
        | MemberStatus.Complete -> "complete"
        | MemberStatus.Runnable -> "runnable"
        | MemberStatus.InProgress -> "in-progress"
        | MemberStatus.Blocked -> "blocked"
        | MemberStatus.NotRunnable -> "not-runnable"

    let tryParse value = all |> List.tryFind (fun status -> code status = value)

    let ofState state =
        match state with
        | PlanningWorkState.Complete
        | PlanningWorkState.Abandoned -> MemberStatus.Complete
        | PlanningWorkState.Ready -> MemberStatus.Runnable
        | PlanningWorkState.Active
        | PlanningWorkState.PartiallyComplete -> MemberStatus.InProgress
        | PlanningWorkState.Blocked
        | PlanningWorkState.AwaitingEvidence
        | PlanningWorkState.AwaitingHuman -> MemberStatus.Blocked
        | PlanningWorkState.Captured
        | PlanningWorkState.StaleStateCandidate -> MemberStatus.NotRunnable

/// PRX-GRP-011/041/042: a member keeps its own identity and state; the
/// group only records why it belongs. There is deliberately no path,
/// evidence or attribution field here (PRX-GRP-043).
type GroupMember =
    { WorkItemId: string
      Reason: string
      Confidence: EvidenceConfidence
      LifecycleState: string
      PlanningState: PlanningWorkState
      Status: MemberStatus
      GatedBy: string list }

/// PRX-GRP-031: "k/n members <signal>".
type CohesionLine =
    { Signal: AffinitySignal
      Members: string list
      Covered: int
      Total: int
      Statement: string }

[<RequireQualifiedAccess>]
type GroupNoteCode =
    | Oversized
    | AbovePreferredSize
    | BelowPreferredSize
    | MixedRepositories
    | CrossRepository
    | UnknownMember
    | ExecutesElsewhere
    | DeclaredOverlap
    | SplitForSize
    | SplitForContextPressure
    | ContextPressureObserved
    | MergedByArchitecture
    | InternalCycle
    | PartialCompletion

[<RequireQualifiedAccess>]
module GroupNoteCode =
    let all =
        [ GroupNoteCode.Oversized
          GroupNoteCode.AbovePreferredSize
          GroupNoteCode.BelowPreferredSize
          GroupNoteCode.MixedRepositories
          GroupNoteCode.CrossRepository
          GroupNoteCode.UnknownMember
          GroupNoteCode.ExecutesElsewhere
          GroupNoteCode.DeclaredOverlap
          GroupNoteCode.SplitForSize
          GroupNoteCode.SplitForContextPressure
          GroupNoteCode.ContextPressureObserved
          GroupNoteCode.MergedByArchitecture
          GroupNoteCode.InternalCycle
          GroupNoteCode.PartialCompletion ]

    let code note =
        match note with
        | GroupNoteCode.Oversized -> "oversized"
        | GroupNoteCode.AbovePreferredSize -> "above-preferred-size"
        | GroupNoteCode.BelowPreferredSize -> "below-preferred-size"
        | GroupNoteCode.MixedRepositories -> "mixed-repositories"
        | GroupNoteCode.CrossRepository -> "cross-repository"
        | GroupNoteCode.UnknownMember -> "unknown-member"
        | GroupNoteCode.ExecutesElsewhere -> "executes-elsewhere"
        | GroupNoteCode.DeclaredOverlap -> "declared-overlap"
        | GroupNoteCode.SplitForSize -> "split-for-size"
        | GroupNoteCode.SplitForContextPressure -> "split-for-context-pressure"
        | GroupNoteCode.ContextPressureObserved -> "context-pressure-observed"
        | GroupNoteCode.MergedByArchitecture -> "merged-by-architecture"
        | GroupNoteCode.InternalCycle -> "internal-cycle"
        | GroupNoteCode.PartialCompletion -> "partial-completion"

    let tryParse value = all |> List.tryFind (fun note -> code note = value)

type GroupNote =
    { Code: GroupNoteCode
      Severity: FindingSeverity
      Message: string }

/// PRX-GRP-063: one reasoning owner by default; never one agent per item.
[<RequireQualifiedAccess>]
type GroupExecution =
    | OneSequentialAgent
    | OneOwnerIndependentSubtasks
    | SplitByRepository

[<RequireQualifiedAccess>]
module GroupExecution =
    let all =
        [ GroupExecution.OneSequentialAgent
          GroupExecution.OneOwnerIndependentSubtasks
          GroupExecution.SplitByRepository ]

    let code execution =
        match execution with
        | GroupExecution.OneSequentialAgent -> "one-sequential-agent"
        | GroupExecution.OneOwnerIndependentSubtasks -> "one-owner-independent-subtasks"
        | GroupExecution.SplitByRepository -> "split-by-repository"

    let tryParse value = all |> List.tryFind (fun execution -> code execution = value)

/// PRX-GRP-017/061: context setup cost. Counts are known; durations and
/// tokens stay unknown until measured.
type ContextCost =
    { IndependentAcquisitions: int
      GroupedAcquisitions: int
      ColdStart: Estimate<int64>
      SharedContext: Estimate<int64>
      MemberIncremental: Estimate<int64>
      Statement: string }

type GroupProgress =
    { Total: int
      Complete: int
      InProgress: int
      Runnable: int
      Blocked: int
      NotRunnable: int
      Statement: string }

type WorkGroup =
    { Id: WorkGroupId
      Kind: GroupKind
      Origin: GroupOrigin
      Area: string
      Members: GroupMember list
      SharedContext: string list
      RequiredSequence: string list
      ExecutionRepository: string
      CrossRepository: bool
      Affinity: ContextAffinity
      Confidence: EvidenceConfidence
      Cohesion: CohesionLine list
      Collision: CollisionRisk
      CollisionPairs: Collision list
      ParallelSafe: bool
      Execution: GroupExecution
      ExecutionReasons: string list
      ContextCost: ContextCost
      Progress: GroupProgress
      ArchitectureNotes: string list
      Notes: GroupNote list }

[<RequireQualifiedAccess>]
type GroupEndpoint =
    | Group of id: string
    | Item of id: string
    | External of target: DependencyTarget

[<RequireQualifiedAccess>]
module GroupEndpoint =
    let describe endpoint =
        match endpoint with
        | GroupEndpoint.Group id -> id
        | GroupEndpoint.Item id -> id
        | GroupEndpoint.External target -> DependencyTarget.describe target

    let kindCode endpoint =
        match endpoint with
        | GroupEndpoint.Group _ -> "group"
        | GroupEndpoint.Item _ -> "work-item"
        | GroupEndpoint.External target -> DependencyTarget.kindCode target

    let value endpoint =
        match endpoint with
        | GroupEndpoint.Group id
        | GroupEndpoint.Item id -> id
        | GroupEndpoint.External target -> DependencyTarget.value target

    let tryParse (kind: string) (value: string) =
        match kind with
        | "group" -> Some(GroupEndpoint.Group value)
        | "work-item" -> Some(GroupEndpoint.Item value)
        | other -> DependencyTarget.tryParse other value |> Option.map GroupEndpoint.External

    let sortKey endpoint = $"{kindCode endpoint}:{value endpoint}"

/// PRX-GRP-050: a dependency between groups, items and external
/// prerequisites, derived from (never hiding) member dependencies.
type GroupDependency =
    { From: GroupEndpoint
      To: GroupEndpoint
      Via: string list
      Gating: bool }

/// PRX-GRP-021/063: may two groups run at the same time?
type GroupRelation =
    { Left: string
      Right: string
      Collision: CollisionRisk
      Dependent: bool
      MayRunConcurrently: bool
      Reason: string }

type UngroupedItem =
    { WorkItem: string
      PlanningState: PlanningWorkState
      Affinity: ContextAffinity
      Reason: string }

type GroupingSettings =
    { PreferredMinimumSize: int
      PreferredMaximumSize: int
      MaximumAutomaticSize: int
      MinimumAffinity: ContextAffinity
      RiskPolicy: string }

type GroupingReport =
    { Groups: WorkGroup list
      Ungrouped: UngroupedItem list
      Dependencies: GroupDependency list
      Cycles: string list list
      Relations: GroupRelation list
      Affinities: PairAffinity list
      Settings: GroupingSettings
      UnknownEvidence: string list
      Statement: string }

type GroupExclusion =
    { WorkItem: string
      Affinity: ContextAffinity
      Reason: string }

/// PRX-GRP-071: every question `explain-group` must answer.
type GroupExplanation =
    { Group: WorkGroup
      WhyTogether: string list
      Excluded: GroupExclusion list
      Evidence: (SignalBasis * string) list
      Inferred: string list
      Unknown: string list
      SharedArchitecture: string list
      DependencyOrder: string list
      CollisionRisk: string list
      ExecutionRationale: string list
      WouldChange: string list }

/// A unit of the group-level schedule: a group or an ungrouped item.
type ScheduledUnit =
    { Unit: string
      IsGroup: bool
      Members: string list
      Remaining: Estimate<int64>
      Reasons: string list }

type GroupWave =
    { Number: int
      Units: ScheduledUnit list
      ExpectedDuration: Estimate<int64> }

/// PRX-GRP-072: portfolio -> groups -> items.
type GroupSchedule =
    { MaxConcurrency: int
      RiskPolicy: string
      Waves: GroupWave list
      NotScheduled: (string * string) list
      ExpectedDuration: Estimate<int64>
      ContextAcquisitions: int
      IndependentContextAcquisitions: int
      Statement: string }

type ArmEstimate =
    { Executions: int
      ContextAcquisitions: int
      ExpectedDuration: Estimate<int64>
      PeakConcurrency: int
      DesignOwners: int }

type GroupTradeoff =
    { Group: string
      Members: string list
      NotYetRunnable: string list
      Independent: ArmEstimate
      Grouped: ArmEstimate
      ContextSaving: string
      ArchitectureConsideration: string
      ContextPressureRisk: string }

type GroupComparison =
    { Tradeoffs: GroupTradeoff list
      Portfolio: GroupSchedule
      ItemPlanDuration: Estimate<int64>
      ItemPlanContextAcquisitions: int
      Statement: string }

[<RequireQualifiedAccess>]
module Grouping =
    let advisoryStatement =
        "Advisory grouping: a group is a recommendation to reason about these items together. It changes no work item's state, attribution, evidence or identity, and nothing was started."

    let unmeasuredContext =
        "context reuse is unmeasured: too few finalized executions record session metrics (repeated and governance reads, time to first code change; the anthropic-claude-session adapter), so savings in tokens or time are unknown (PRX-GRP-061)"

    /// The measured context overhead when history carries enough session
    /// metrics (PRAXIS-PLAN-05), otherwise the unmeasured statement.
    let contextNote (history: HistorySummary) =
        if history.ContextOverhead.Sufficient then history.ContextOverhead.Statement else unmeasuredContext

    /// What avoiding `avoided` cold starts is worth, from measured history.
    let coldStartSaving (history: HistorySummary) (avoided: int) =
        let minutes (value: int64) = Math.Round(decimal value / 60_000m, 1).ToString(Globalization.CultureInfo.InvariantCulture)

        match history.ContextOverhead.ColdStart with
        | { Lower = Some lower; Expected = Some median; Upper = Some upper } when history.ContextOverhead.Sufficient ->
            $"{avoided} cold start(s) avoided by construction; at the measured cold start (median {minutes median} min, interquartile {minutes lower}-{minutes upper} min, {history.ContextOverhead.SampledSessions} sessions) that is about {minutes (int64 avoided * median)} min of time to first code change"
        | _ -> $"{avoided} cold start(s) avoided by construction; their token and time value is unknown (unmeasured)"

    let riskPolicy = Scheduling.acceptElevated

    // ---- per-item evidence ------------------------------------------------------

    let private requirementPattern =
        Regex(@"\b(?:(?:PRX|RQ|REQ)-[A-Z0-9]+(?:-[A-Z0-9]+)*-\d+|DF-[A-Z]+-\d{4}-[A-Z]\d+)\b", RegexOptions.CultureInvariant)

    let private externalPattern =
        Regex(@"\bexternal repository\b", RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

    let private wordPattern = Regex(@"[A-Za-z][A-Za-z0-9]{3,}", RegexOptions.CultureInvariant)

    let private stopWords =
        set [ "with"; "from"; "into"; "that"; "this"; "when"; "then"; "than"; "their"; "them"; "they"; "have"; "does"; "make"; "only"; "each"; "every"; "work"; "item"; "items"; "praxis"; "phase"; "increment"; "real"; "effect"; "slice"; "should"; "must" ]

    type private Evidence =
        { Item: ItemAnalysis
          Areas: string list
          Paths: string list
          Requirements: string list
          TitleWords: Set<string>
          Declared: string list
          Decisions: string list
          Location: ExecutionLocation
          LocationBasis: SignalBasis }

    let private areaTags (configuration: PlannerConfiguration) (item: ItemAnalysis) =
        let generic = configuration.GenericTags |> List.map (fun tag -> tag.ToLowerInvariant()) |> Set.ofList
        item.Tags |> List.map (fun tag -> tag.ToLowerInvariant()) |> List.filter (generic.Contains >> not) |> Text.distinctOrdinal

    /// PRX-GRP-051: where an item's implementation happens. Declared
    /// `grouping.executionRepositories` wins; a description naming an
    /// external repository is an inference; otherwise it is this repository.
    let executionLocation
        (grouping: GroupingConfiguration)
        (repository: string)
        (queue: PlanningQueueItem list)
        (id: string)
        : ExecutionLocation * SignalBasis =
        let description =
            queue
            |> List.tryFind (fun entry -> entry.Id = id)
            |> Option.bind (fun entry -> entry.Description)
            |> Option.defaultValue ""

        match grouping.ExecutionRepositories |> List.tryFind (fun (item, _) -> item = id) with
        | Some(_, declared) -> ExecutionLocation.Repository declared, SignalBasis.Explicit
        | None when externalPattern.IsMatch description -> ExecutionLocation.UnknownExternal, SignalBasis.Inferred
        | None -> ExecutionLocation.Repository repository, SignalBasis.Derived

    let private evidenceFor (input: PlanningInput) (item: ItemAnalysis) : Evidence =
        let configuration = input.Configuration
        let grouping = configuration.Grouping
        let queued = input.Queue |> List.tryFind (fun entry -> entry.Id = item.Id)
        let description = queued |> Option.bind (fun entry -> entry.Description) |> Option.defaultValue ""
        let location, basis = executionLocation grouping input.Repository input.Queue item.Id

        { Item = item
          Areas = areaTags configuration item
          Paths = configuration.Areas |> List.tryFind (fun (id, _) -> id = item.Id) |> Option.map snd |> Option.defaultValue []
          Requirements = requirementPattern.Matches(description) |> Seq.map (fun found -> found.Value) |> Seq.toList |> Text.distinctOrdinal
          TitleWords =
            wordPattern.Matches(item.Title)
            |> Seq.map (fun found -> found.Value.ToLowerInvariant())
            |> Seq.filter (stopWords.Contains >> not)
            |> Set.ofSeq
          Declared = grouping.Groups |> List.filter (fun group -> group.Members |> List.contains item.Id) |> List.map (fun group -> group.Id)
          Decisions = grouping.Architecture |> List.filter (fun decision -> decision.Members |> List.contains item.Id) |> List.map (fun decision -> decision.Decision)
          Location = location
          LocationBasis = basis }

    /// Enough structural evidence to say two items share *nothing*.
    let private hasScope (evidence: Evidence) =
        not evidence.Areas.IsEmpty
        || not evidence.Paths.IsEmpty
        || not evidence.Requirements.IsEmpty
        || not evidence.Declared.IsEmpty
        || not evidence.Decisions.IsEmpty
        || evidence.Item.Checkpoint.IsSome
        || evidence.Item.Dependencies |> List.exists (fun resolved -> match resolved.Dependency.Target with DependencyTarget.WorkItem _ -> true | _ -> false)

    let private overlaps (left: string) (right: string) =
        let normalize (value: string) = value.Replace('\\', '/').TrimEnd('/')
        let left, right = normalize left, normalize right
        left = right || left.StartsWith(right + "/", StringComparison.Ordinal) || right.StartsWith(left + "/", StringComparison.Ordinal)

    let private shared (left: string list) (right: string list) =
        Set.intersect (Set.ofList left) (Set.ofList right) |> Set.toList |> Text.sortOrdinal

    let private dependencySignals (dependent: ItemAnalysis) (prerequisite: string) =
        dependent.Dependencies
        |> List.choose (fun resolved ->
            match resolved.Dependency.Target, resolved.Dependency.Kind with
            | DependencyTarget.WorkItem target, DependencyKind.Hard when target = prerequisite ->
                Some(AffinitySignal.HardDependency(dependent.Id, prerequisite, resolved.Dependency.Origin))
            | _ -> None)
        |> List.distinct
        |> List.truncate 1

    let private signalsBetween (left: Evidence) (right: Evidence) : AffinitySignal list =
        [ yield! shared left.Declared right.Declared |> List.map AffinitySignal.DeclaredGroup
          yield! shared left.Decisions right.Decisions |> List.map AffinitySignal.ArchitectureDecision
          yield! shared left.Areas right.Areas |> List.map AffinitySignal.SharedArea
          yield! dependencySignals left.Item right.Item.Id
          yield! dependencySignals right.Item left.Item.Id
          yield! shared left.Requirements right.Requirements |> List.map AffinitySignal.SharedRequirement
          yield!
              left.Paths
              |> List.collect (fun path -> right.Paths |> List.filter (overlaps path) |> List.map (fun _ -> path))
              |> Text.distinctOrdinal
              |> List.map AffinitySignal.SharedDeclaredPath
          match left.Item.Checkpoint, right.Item.Checkpoint with
          | Some a, Some b when a.Branch = b.Branch -> yield AffinitySignal.SameBranch a.Branch
          | _ -> ()
          match Graph.family left.Item.Id, Graph.family right.Item.Id with
          | Some a, Some b when a = b -> yield AffinitySignal.RequirementFamily a
          | _ -> ()
          let words = Set.intersect left.TitleWords right.TitleWords |> Set.toList |> Text.sortOrdinal
          if words.Length >= 2 then yield AffinitySignal.SimilarTitle words ]

    let private levelOf (signals: AffinitySignal list) (scoped: bool) =
        match signals with
        | [] -> if scoped then ContextAffinity.None else ContextAffinity.Unknown
        | _ ->
            let strongest = signals |> List.map AffinitySignal.level |> List.maxBy ContextAffinity.rank
            // Two independent structural Medium signals corroborate each other.
            let corroborated =
                signals
                |> List.filter (fun signal -> AffinitySignal.level signal = ContextAffinity.Medium && AffinitySignal.basis signal <> SignalBasis.Inferred)
                |> List.length
                >= 2

            if strongest = ContextAffinity.Medium && corroborated then ContextAffinity.High else strongest

    let private affinityBetween (left: Evidence) (right: Evidence) : PairAffinity =
        let left, right = if Text.ordinal left.Item.Id right.Item.Id <= 0 then left, right else right, left
        let signals = signalsBetween left right |> List.sortBy AffinitySignal.priority
        let scoped = hasScope left && hasScope right
        let level = levelOf signals scoped

        let confidence =
            signals
            |> List.filter (fun signal -> AffinitySignal.level signal = level || level = ContextAffinity.High)
            |> List.map AffinitySignal.confidence
            |> function
                | [] -> EvidenceConfidence.Unknown
                | values -> values |> List.maxBy EvidenceConfidence.rank

        let statement =
            match level, signals with
            | ContextAffinity.Unknown, _ ->
                let missing = [ left; right ] |> List.filter (hasScope >> not) |> List.map (fun evidence -> evidence.Item.Id) |> String.concat ", "
                $"unknown: {missing} carries no scope evidence (area tag, declared path, requirement, dependency, checkpoint or declaration)"
            | ContextAffinity.None, _ -> "none: both carry scope evidence and share none of it"
            | _, _ -> ContextAffinity.code level + ": " + (signals |> List.map AffinitySignal.describe |> String.concat "; ")

        ({ Left = left.Item.Id
           Right = right.Item.Id
           Level = level
           Signals = signals
           Confidence = confidence
           Statement = statement }
        : PairAffinity)

    // ---- small graph helpers ----------------------------------------------------

    let private key (left: string) (right: string) = if Text.ordinal left right <= 0 then left, right else right, left

    /// Mutually reachable node sets (size > 1, or a self loop) in ordinal
    /// order. Quadratic reachability, adequate for backlog-sized graphs.
    let stronglyConnected (nodes: string list) (successors: string -> string list) : string list list =
        let rec reach (seen: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> seen
            | next :: rest ->
                let fresh = successors next |> List.filter (seen.Contains >> not)
                reach (fresh |> List.fold (fun set value -> Set.add value set) seen) (rest @ fresh)

        let reachable = nodes |> List.map (fun node -> node, reach Set.empty [ node ]) |> Map.ofList

        nodes
        |> List.choose (fun node ->
            let mutual = nodes |> List.filter (fun other -> reachable[node].Contains other && reachable[other].Contains node)
            let selfLoop = successors node |> List.contains node

            match mutual with
            | [ single ] when single = node && not selfLoop -> None
            | [] when selfLoop -> Some [ node ]
            | [] -> None
            | members -> Some(Text.sortOrdinal members))
        |> List.distinct
        |> List.sortWith (fun left right -> Text.ordinal (List.head left) (List.head right))

    /// Topological order of `members` by hard work-item dependencies among
    /// them; ordinal tie-breaks (PRX-GRP-090 case 4). Members in a cycle are
    /// appended in ordinal order and reported separately.
    let private sequence (byId: Map<string, ItemAnalysis>) (members: string list) =
        let inGroup = Set.ofList members

        let prerequisites (id: string) =
            byId.TryFind id
            |> Option.map (fun item ->
                item.Dependencies
                |> List.choose (fun resolved ->
                    match resolved.Dependency.Target, resolved.Dependency.Kind with
                    | DependencyTarget.WorkItem target, DependencyKind.Hard when inGroup.Contains target && target <> id -> Some target
                    | _ -> None))
            |> Option.defaultValue []
            |> Text.distinctOrdinal

        let rec order (placed: string list) (remaining: string list) =
            let ready = remaining |> List.filter (fun id -> prerequisites id |> List.forall (fun prerequisite -> placed |> List.contains prerequisite))

            match ready with
            | [] -> placed, remaining
            | next :: _ -> order (placed @ [ next ]) (remaining |> List.filter ((<>) next))

        let placed, cyclic = order [] (Text.sortOrdinal members)
        placed @ cyclic, stronglyConnected (Text.sortOrdinal members) prerequisites

    // ---- clustering -------------------------------------------------------------

    type private Candidate =
        { Members: string list
          Origin: GroupOrigin
          Declared: DeclaredGroup option
          Notes: GroupNote list
          SplitOf: string option }

    let private note code severity message =
        { Code = code
          Severity = severity
          Message = message }

    let private qualifies (minimum: ContextAffinity) (pair: PairAffinity) =
        ContextAffinity.rank pair.Level >= ContextAffinity.rank minimum

    /// PRX-GRP-031: seed with the best-connected item, then admit the
    /// candidate most connected to the group, provided it qualifies with at
    /// least two thirds of the members (so both members of a pair). A single
    /// link never chains two areas together, and no item is admitted just to
    /// fill capacity.
    let private cluster (minimum: ContextAffinity) (pairOf: string -> string -> PairAffinity) (pool: string list) : string list list =
        let degree (id: string) (among: string list) =
            among |> List.filter (fun other -> other <> id && qualifies minimum (pairOf id other)) |> List.length

        // Level first, then the evidence priority of PRX-GRP-022 (an area
        // declaration outranks a dependency, which outranks text).
        let score (pair: PairAffinity) =
            let best = pair.Signals |> List.map AffinitySignal.priority |> function | [] -> 10 | values -> List.min values
            ContextAffinity.rank pair.Level * 10 + (10 - best)

        let strength (id: string) (among: string list) =
            among |> List.filter ((<>) id) |> List.sumBy (fun other -> score (pairOf id other))

        let rec grow (group: string list) (available: string list) =
            let needed = (2 * group.Length + 2) / 3

            let admissible =
                available
                |> List.map (fun id -> id, degree id group, strength id group)
                |> List.filter (fun (_, links, _) -> links >= max 1 needed)
                |> List.sortWith (fun (leftId, leftLinks, leftStrength) (rightId, rightLinks, rightStrength) ->
                    match compare rightLinks leftLinks with
                    | 0 ->
                        match compare rightStrength leftStrength with
                        | 0 -> Text.ordinal leftId rightId
                        | order -> order
                    | order -> order)

            match admissible with
            | [] -> group
            | (next, _, _) :: _ -> grow (group @ [ next ]) (available |> List.filter ((<>) next))

        let rec build (remaining: string list) (groups: string list list) =
            let seeds =
                remaining
                |> List.map (fun id -> id, degree id remaining, strength id remaining)
                |> List.filter (fun (_, links, _) -> links > 0)
                |> List.sortWith (fun (leftId, left, leftStrength) (rightId, right, rightStrength) ->
                    match compare right left with
                    | 0 -> match compare rightStrength leftStrength with | 0 -> Text.ordinal leftId rightId | order -> order
                    | order -> order)

            match seeds with
            | [] -> List.rev groups
            | (seed, _, _) :: _ ->
                let group = grow [ seed ] (remaining |> List.filter ((<>) seed))
                let rest = remaining |> List.filter (fun id -> not (List.contains id group))
                if group.Length < 2 then build rest groups else build rest (Text.sortOrdinal group :: groups)

        build (Text.sortOrdinal pool) []

    let private chunk (limit: int) (members: string list) =
        let parts = int (Math.Ceiling(float members.Length / float limit))
        let size = int (Math.Ceiling(float members.Length / float parts))
        members |> List.chunkBySize size

    let private pressureLimits (observations: Observation list) =
        observations
        |> List.choose (fun observation ->
            match observation.Kind with
            | ObservationKind.ContextPressure(members, indicators) when indicators |> List.sumBy snd > 0 ->
                Some(members, indicators, observation.Provenance.Reference)
            | _ -> None)

    let private describeIndicators (indicators: (string * int) list) =
        indicators |> List.filter (fun (_, count) -> count > 0) |> List.map (fun (name, count) -> $"{name} {count}") |> String.concat ", "

    /// PRX-GRP-074: a cohesive candidate is split, in dependency order, when it
    /// exceeds the automatic maximum or an observed context-pressure limit.
    let private splitCandidate
        (grouping: GroupingConfiguration)
        (pressure: (string list * (string * int) list * string) list)
        (ordered: string list)
        : Candidate list =
        let applicable = pressure |> List.filter (fun (members, _, _) -> members |> List.exists (fun id -> List.contains id ordered))

        let pressureLimit =
            applicable
            |> List.map (fun (members, indicators, reference) -> max 2 (members.Length - 1), (members, indicators, reference))
            |> List.sortBy fst
            |> List.tryHead

        let label = String.concat ", " ordered

        match pressureLimit with
        | Some(limit, (members, indicators, reference)) when ordered.Length > limit ->
            let parts = chunk limit ordered

            parts
            |> List.map (fun part ->
                { Members = part
                  Origin = GroupOrigin.PlannerRecommended
                  Declared = None
                  SplitOf = Some label
                  Notes =
                    [ note
                          GroupNoteCode.SplitForContextPressure
                          FindingSeverity.Warning
                          $"""split from a cohesive candidate of {ordered.Length} ({label}) into {parts.Length} parts of at most {limit}: a grouped execution of {members.Length} related members showed context pressure ({describeIndicators indicators}; {reference})""" ] })
        | _ when ordered.Length > grouping.MaximumAutomaticSize ->
            let parts = chunk grouping.MaximumAutomaticSize ordered

            parts
            |> List.map (fun part ->
                { Members = part
                  Origin = GroupOrigin.PlannerRecommended
                  Declared = None
                  SplitOf = Some label
                  Notes =
                    [ note
                          GroupNoteCode.SplitForSize
                          FindingSeverity.Advisory
                          $"split from a cohesive candidate of {ordered.Length} ({label}) into {parts.Length} parts in dependency order: the automatic maximum is {grouping.MaximumAutomaticSize}, beyond which context and verification cost grow faster than reuse" ] })
        | _ ->
            [ { Members = ordered
                Origin = GroupOrigin.PlannerRecommended
                Declared = None
                SplitOf = None
                Notes =
                  applicable
                  |> List.map (fun (members, indicators, reference) ->
                      note
                          GroupNoteCode.ContextPressureObserved
                          FindingSeverity.Advisory
                          $"context pressure was observed for a grouped execution of {members.Length} related members ({describeIndicators indicators}; {reference}); this group is within that limit") } ]

    // ---- building groups ----------------------------------------------------------

    let private sanitize (value: string) =
        let upper = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9]+", "-").Trim('-')
        if upper.Length = 0 then "GENERAL" else upper

    let private cohesionLines (pairs: PairAffinity list) (members: string list) =
        pairs
        |> List.collect (fun pair -> pair.Signals |> List.map (fun signal -> signal, [ pair.Left; pair.Right ]))
        |> List.groupBy (fun (signal, _) -> AffinitySignal.code signal, AffinitySignal.detail signal)
        |> List.map (fun (_, entries) ->
            let signal = entries |> List.head |> fst
            let covered = entries |> List.collect snd |> Text.distinctOrdinal |> List.filter (fun id -> List.contains id members)

            ({ Signal = signal
               Members = covered
               Covered = covered.Length
               Total = members.Length
               Statement = $"{covered.Length}/{members.Length} {AffinitySignal.describe signal} [{SignalBasis.code (AffinitySignal.basis signal)}]" }
            : CohesionLine))
        |> List.sortWith (fun left right ->
            match compare right.Covered left.Covered with
            | 0 ->
                match compare (AffinitySignal.priority left.Signal) (AffinitySignal.priority right.Signal) with
                | 0 -> Text.ordinal left.Statement right.Statement
                | order -> order
            | order -> order)

    let private kindOf (signal: AffinitySignal option) =
        match signal with
        | Some(AffinitySignal.ArchitectureDecision _) -> GroupKind.SharedArchitecture
        | Some(AffinitySignal.SharedArea _) -> GroupKind.SharedArea
        | Some(AffinitySignal.HardDependency _) -> GroupKind.DependencyChain
        | Some(AffinitySignal.SharedDeclaredPath _)
        | Some(AffinitySignal.SameBranch _) -> GroupKind.SharedFiles
        | Some(AffinitySignal.DeclaredGroup _) -> GroupKind.Custom "declared"
        | _ -> GroupKind.ContextAffinity

    let private areaOf (signal: AffinitySignal option) =
        match signal with
        | Some(AffinitySignal.SharedArea area) -> area
        | Some(AffinitySignal.SharedDeclaredPath path) -> path.Replace('\\', '/').TrimEnd('/').Split('/') |> Array.last
        | Some(AffinitySignal.ArchitectureDecision decision) -> decision
        | Some(AffinitySignal.SharedRequirement requirement) -> Regex.Replace(requirement, @"-\d+$", "")
        | Some(AffinitySignal.HardDependency(_, prerequisite, _)) -> Graph.family prerequisite |> Option.defaultValue "chain"
        | Some(AffinitySignal.SameBranch branch) -> branch.Split('/') |> Array.last
        | Some(AffinitySignal.RequirementFamily family) -> family
        | _ -> "general"

    let private gatedBy (byId: Map<string, ItemAnalysis>) (members: string list) (id: string) =
        let blocked =
            members
            |> List.filter (fun other ->
                byId.TryFind other |> Option.exists (fun item -> MemberStatus.ofState item.PlanningState = MemberStatus.Blocked))
            |> Set.ofList

        let prerequisites (current: string) =
            byId.TryFind current
            |> Option.map (fun item ->
                item.Dependencies
                |> List.choose (fun resolved ->
                    match resolved.Dependency.Target, resolved.Dependency.Kind, resolved.Status with
                    | DependencyTarget.WorkItem target, DependencyKind.Hard, status when status <> DependencyStatus.Satisfied && List.contains target members -> Some target
                    | _ -> None))
            |> Option.defaultValue []

        let rec walk (seen: Set<string>) (frontier: string list) =
            match frontier with
            | [] -> seen
            | next :: rest ->
                let fresh = prerequisites next |> List.filter (seen.Contains >> not)
                walk (fresh |> List.fold (fun set value -> Set.add value set) seen) (rest @ fresh)

        walk Set.empty [ id ] |> Set.remove id |> Set.intersect blocked |> Set.toList |> Text.sortOrdinal

    let private progress (members: GroupMember list) =
        let count status = members |> List.filter (fun entry -> entry.Status = status) |> List.length
        let complete = count MemberStatus.Complete
        let blocked = members |> List.filter (fun entry -> entry.Status = MemberStatus.Blocked) |> List.map (fun entry -> entry.WorkItemId)
        let blockedText = if blocked.IsEmpty then "" else $"""; blocked: {String.concat ", " blocked}"""

        { Total = members.Length
          Complete = complete
          InProgress = count MemberStatus.InProgress
          Runnable = count MemberStatus.Runnable
          Blocked = blocked.Length
          NotRunnable = count MemberStatus.NotRunnable
          Statement =
            $"{complete} of {members.Length} complete{blockedText}; each member completes on its own evidence and the group never implies that every member succeeded (PRX-GRP-042)" }

    type private Context =
        { Input: PlanningInput
          Analysis: PlanningAnalysis
          ById: Map<string, ItemAnalysis>
          Evidence: Map<string, Evidence>
          PairOf: string -> string -> PairAffinity }

    let private buildGroup (context: Context) (candidate: Candidate) (id: string) (area: string) : WorkGroup =
        let configuration = context.Input.Configuration
        let members = candidate.Members
        let items = members |> List.choose context.ById.TryFind
        let pairs = members |> List.collect (fun left -> members |> List.filter (fun right -> Text.ordinal left right < 0) |> List.map (context.PairOf left))
        let cohesion = cohesionLines pairs members
        let ordered, cycles = sequence context.ById members

        let locations =
            members
            |> List.choose context.Evidence.TryFind
            |> List.map (fun evidence -> evidence.Location)
            |> List.distinct

        let crossRepository = candidate.Declared |> Option.exists (fun declared -> declared.CrossRepository)

        let repository =
            match candidate.Declared |> Option.bind (fun declared -> declared.ExecutionRepository), locations with
            | Some declared, _ -> declared
            | None, [ single ] -> ExecutionLocation.describe single
            | None, many -> many |> List.map ExecutionLocation.describe |> Text.distinctOrdinal |> String.concat " + "

        let open' = items |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState))

        let collisions =
            open'
            |> List.collect (fun left ->
                open'
                |> List.filter (fun right -> Text.ordinal left.Id right.Id < 0)
                |> List.map (Graph.collision configuration left))

        let worst =
            collisions
            |> List.map (fun collision -> collision.Risk)
            |> List.fold (fun worst risk -> if CollisionRisk.rank risk > CollisionRisk.rank worst then risk else worst) CollisionRisk.Safe

        let parallelSafe = collisions |> List.forall (fun collision -> collision.Risk = CollisionRisk.Safe)
        let internalDependencies = pairs |> List.exists (fun pair -> pair.Signals |> List.exists (function AffinitySignal.HardDependency _ -> true | _ -> false))
        let mixed = locations.Length > 1 && not crossRepository

        let untriaged = items |> List.filter (fun item -> MemberStatus.ofState item.PlanningState = MemberStatus.NotRunnable) |> List.map (fun item -> item.Id)

        let triage =
            match untriaged with
            | [] -> []
            | ids -> [ $"""triage {String.concat ", " ids} first: a planning group may hold captured work, but only triaged members can execute""" ]

        let execution, reasons =
            if mixed then
                GroupExecution.SplitByRepository,
                [ $"members execute in different repositories ({repository}) and the group is not declared cross-repository; split it into repository-local groups (PRX-GRP-051)" ]
            elif not parallelSafe || internalDependencies then
                GroupExecution.OneSequentialAgent,
                [ yield "one reasoning owner by default (PRX-GRP-063); high affinity usually means shared files, so one warmed agent keeps one design"
                  if internalDependencies then yield "members depend on each other, so their order is fixed"
                  if not parallelSafe then
                      yield $"intra-group collision risk is {CollisionRisk.code worst}: parallel subtasks are not proven independent" ]
            else
                GroupExecution.OneOwnerIndependentSubtasks,
                [ "one reasoning owner (PRX-GRP-063) who designs once; every member pair is collision-safe, so subtasks may run in parallel under that owner"
                  "never one agent per member: affinity is a reason to share reasoning, not to split it" ]

        let members' =
            members
            |> List.map (fun memberId ->
                let item = context.ById.TryFind memberId

                let strongest =
                    members
                    |> List.filter ((<>) memberId)
                    |> List.map (context.PairOf memberId)
                    |> List.sortWith (fun left right ->
                        match compare (ContextAffinity.rank right.Level) (ContextAffinity.rank left.Level) with
                        | 0 -> Text.ordinal left.Statement right.Statement
                        | order -> order)
                    |> List.tryHead

                let reason =
                    match candidate.Declared, strongest with
                    | Some declared, _ -> $"declared a member of {declared.Id} ({GroupOrigin.code declared.Origin})"
                    | None, Some pair ->
                        let other = if pair.Left = memberId then pair.Right else pair.Left
                        $"with {other}: {pair.Statement}"
                    | None, None -> "no pairwise evidence"

                let confidence =
                    match candidate.Declared, strongest with
                    | Some _, _ -> EvidenceConfidence.High
                    | None, Some pair -> pair.Confidence
                    | None, None -> EvidenceConfidence.Unknown

                let state = item |> Option.map (fun entry -> entry.PlanningState) |> Option.defaultValue PlanningWorkState.Captured

                { WorkItemId = memberId
                  Reason = reason
                  Confidence = confidence
                  LifecycleState = item |> Option.map (fun entry -> entry.LifecycleState) |> Option.defaultValue "unknown"
                  PlanningState = state
                  Status = MemberStatus.ofState state
                  GatedBy = gatedBy context.ById members memberId })

        let levels = pairs |> List.map (fun pair -> pair.Level)

        let affinity =
            match candidate.Declared with
            | Some _ -> ContextAffinity.High
            | None when levels.IsEmpty -> ContextAffinity.Unknown
            | None ->
                // The level most member pairs reach: a weakest-link view would
                // make every chain look weak, the strongest would hide gaps.
                let sorted = levels |> List.sortByDescending ContextAffinity.rank
                sorted[(sorted.Length - 1) / 2]

        let confidence =
            match candidate.Declared with
            | Some _ -> EvidenceConfidence.High
            | None -> members' |> List.map (fun entry -> entry.Confidence) |> EvidenceConfidence.minimum

        let dominant = cohesion |> List.tryHead |> Option.map (fun line -> line.Signal)

        let sharedContext =
            match candidate.Declared with
            | Some declared when not declared.SharedContext.IsEmpty -> declared.SharedContext
            | _ ->
                cohesion
                |> List.filter (fun line -> line.Covered * 2 >= members.Length && AffinitySignal.basis line.Signal <> SignalBasis.Inferred)
                |> List.map (fun line -> AffinitySignal.describe line.Signal)

        let merged =
            if candidate.Declared.IsSome then
                []
            else
                // PRX-GRP-074 merge: which members would be disconnected
                // without the architecture decision that joins them?
                let decisions = cohesion |> List.choose (fun line -> match line.Signal with AffinitySignal.ArchitectureDecision decision -> Some decision | _ -> None)

                if decisions.IsEmpty then
                    []
                else
                    let linked (left: string) (right: string) =
                        let pair = context.PairOf left right
                        let rest = pair.Signals |> List.filter (function AffinitySignal.ArchitectureDecision _ -> false | _ -> true)
                        ContextAffinity.rank (levelOf rest true) >= ContextAffinity.rank configuration.Grouping.MinimumAffinity

                    let components =
                        members
                        |> List.fold
                            (fun (parts: string list list) id ->
                                let joined, apart = parts |> List.partition (List.exists (linked id))
                                (id :: List.concat joined) :: apart)
                            []

                    if components.Length > 1 then
                        let parts = components |> List.map (Text.sortOrdinal >> String.concat ", ") |> Text.sortOrdinal |> List.map (fun part -> $"[{part}]") |> String.concat " + "
                        let names = String.concat ", " decisions
                        [ note GroupNoteCode.MergedByArchitecture FindingSeverity.Info $"merged {parts}: without {names} these parts share no qualifying evidence; the common decision materially affects all of them" ]
                    else
                        []

        let declaredNotes =
            match candidate.Declared with
            | None -> []
            | Some declared ->
                // `owner/repo:ID` names another repository's member
                // (PRX-GRP-100): never scheduled here, only pointed to.
                let elsewhere (memberId: string) =
                    match memberId.LastIndexOf ':' with
                    | index when index > 0 && memberId.Substring(0, index).Contains '/' -> Some(memberId.Substring(0, index))
                    | _ -> None

                [ for missing in declared.Members |> List.filter (context.ById.ContainsKey >> not) do
                      match elsewhere missing with
                      | Some other ->
                          yield
                              note
                                  GroupNoteCode.ExecutesElsewhere
                                  FindingSeverity.Info
                                  $"{missing} executes in {other}: begin, checkpoint and complete it there with that repository's own Praxis; it is never scheduled into this checkout (PRX-GRP-108)"
                      | None -> yield note GroupNoteCode.UnknownMember FindingSeverity.Warning $"declared member {missing} is not in the planning inventory"
                  if crossRepository then
                      yield note GroupNoteCode.CrossRepository FindingSeverity.Info $"declared cross-repository ({repository}): each repository still gets its own branch, commits, validation, evidence and pull request (PRX-GRP-052)"
                  let pressured =
                      pressureLimits context.Input.Observations
                      |> List.filter (fun (observed, _, _) -> observed |> List.exists (fun id -> List.contains id members) && members.Length > max 2 (observed.Length - 1))

                  yield!
                      pressured
                      |> List.map (fun (observed, indicators, reference) ->
                          note
                              GroupNoteCode.ContextPressureObserved
                              FindingSeverity.Warning
                              $"context pressure was observed for a grouped execution of {observed.Length} of these items ({describeIndicators indicators}; {reference}); consider splitting this declared group into parts of at most {max 2 (observed.Length - 1)}") ]

        let size = members.Length
        let grouping = configuration.Grouping

        let sizeNotes =
            [ if size > grouping.MaximumAutomaticSize then
                  yield
                      note
                          GroupNoteCode.Oversized
                          FindingSeverity.Warning
                          $"{size} members exceeds the recommended maximum of {grouping.MaximumAutomaticSize}: one execution must hold every member's requirements and acceptance criteria, so expect context compaction, re-reading and a long verification pass; consider splitting by dependency chain or sub-area"
              elif size > grouping.PreferredMaximumSize then
                  yield note GroupNoteCode.AbovePreferredSize FindingSeverity.Advisory $"{size} members is above the preferred {grouping.PreferredMinimumSize}..{grouping.PreferredMaximumSize}; watch for context pressure"
              elif size < grouping.PreferredMinimumSize then
                  yield note GroupNoteCode.BelowPreferredSize FindingSeverity.Info $"{size} members is below the preferred {grouping.PreferredMinimumSize}..{grouping.PreferredMaximumSize}; the group is kept because its members are cohesive, not to reach a size" ]

        let otherNotes =
            [ if mixed then
                  yield note GroupNoteCode.MixedRepositories FindingSeverity.Warning $"members execute in different repositories ({repository}); split into repository-local groups or declare the group cross-repository"
              yield!
                  cycles
                  |> List.map (fun cycle ->
                      let names = String.concat ", " cycle
                      note GroupNoteCode.InternalCycle FindingSeverity.Warning $"members {names} depend on each other in a cycle; their order cannot be derived")
              let done' = members' |> List.filter (fun entry -> entry.Status = MemberStatus.Complete)

              if not done'.IsEmpty && done'.Length < members'.Length then
                  let names = done' |> List.map (fun entry -> entry.WorkItemId) |> String.concat ", "
                  yield note GroupNoteCode.PartialCompletion FindingSeverity.Info $"partially complete: {names} complete; the remaining members stay independently completable" ]

        let remaining = members' |> List.filter (fun entry -> entry.Status <> MemberStatus.Complete) |> List.length

        { Id = WorkGroupId id
          Kind =
            match candidate.Declared with
            | Some declared -> declared.Kind |> Option.defaultValue (kindOf dominant)
            | None -> kindOf dominant
          Origin =
            match candidate.Declared, dominant with
            | Some declared, _ -> declared.Origin
            | None, Some(AffinitySignal.HardDependency _) -> GroupOrigin.DependencyDerived
            | None, Some(AffinitySignal.ArchitectureDecision _) -> GroupOrigin.ArchitectureDeclared
            | None, _ -> candidate.Origin
          Area = area
          Members = members'
          SharedContext = sharedContext
          RequiredSequence = ordered
          ExecutionRepository = repository
          CrossRepository = crossRepository
          Affinity = affinity
          Confidence = confidence
          Cohesion = cohesion
          Collision = worst
          CollisionPairs = collisions |> List.filter (fun collision -> collision.Risk <> CollisionRisk.Safe)
          ParallelSafe = parallelSafe
          Execution = execution
          ExecutionReasons = reasons @ triage
          ContextCost =
            { IndependentAcquisitions = remaining
              GroupedAcquisitions = (if remaining = 0 then 0 else 1)
              ColdStart = if remaining = 0 then Estimate.unknown else context.Analysis.History.ContextOverhead.ColdStart
              SharedContext = Estimate.unknown
              MemberIncremental = Estimate.unknown
              Statement =
                $"grouped: coldStart + sharedContext + sum(memberIncremental) = 1 context acquisition; independent: {remaining} x (coldStart + itemCost) = {remaining} acquisitions; {contextNote context.Analysis.History}" }
          Progress = progress members'
          ArchitectureNotes = candidate.Declared |> Option.map (fun declared -> declared.ArchitectureNotes) |> Option.defaultValue []
          Notes = declaredNotes @ candidate.Notes @ merged @ sizeNotes @ otherNotes }

    // ---- group relations and dependencies --------------------------------------

    let private dependencies (context: Context) (groups: WorkGroup list) =
        let groupOf =
            groups
            |> List.collect (fun group -> group.Members |> List.map (fun entry -> entry.WorkItemId, WorkGroupId.value group.Id))
            |> List.groupBy fst
            |> List.map (fun (id, owners) -> id, owners |> List.map snd |> Text.sortOrdinal |> List.head)
            |> Map.ofList

        let endpoint (id: string) =
            match groupOf.TryFind id with
            | Some group -> GroupEndpoint.Group group
            | None -> GroupEndpoint.Item id

        context.Analysis.Items
        |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState))
        |> List.collect (fun item ->
            item.Dependencies
            |> List.map (fun resolved ->
                let target =
                    match resolved.Dependency.Target with
                    | DependencyTarget.WorkItem id -> endpoint id
                    | other -> GroupEndpoint.External other

                endpoint item.Id, target, $"{item.Id} -> {DependencyTarget.describe resolved.Dependency.Target} ({DependencyKind.code resolved.Dependency.Kind}, {DependencyOrigin.code resolved.Dependency.Origin}, {DependencyStatus.code resolved.Status})", resolved))
        |> List.filter (fun (source, target, _, _) -> source <> target)
        |> List.filter (fun (source, target, _, _) -> match source, target with | GroupEndpoint.Group _, _ | _, GroupEndpoint.Group _ -> true | _ -> false)
        |> List.groupBy (fun (source, target, _, _) -> GroupEndpoint.sortKey source, GroupEndpoint.sortKey target)
        |> List.map (fun (_, entries) ->
            let source, target, _, _ = List.head entries

            { From = source
              To = target
              Via = entries |> List.map (fun (_, _, via, _) -> via) |> Text.distinctOrdinal
              Gating = entries |> List.exists (fun (_, _, _, resolved) -> resolved.Status <> DependencyStatus.Satisfied && resolved.Dependency.Kind <> DependencyKind.Soft) })
        |> List.sortWith (fun left right ->
            match Text.ordinal (GroupEndpoint.sortKey left.From) (GroupEndpoint.sortKey right.From) with
            | 0 -> Text.ordinal (GroupEndpoint.sortKey left.To) (GroupEndpoint.sortKey right.To)
            | order -> order)

    let private endpointCycles (edges: GroupDependency list) =
        let gating = edges |> List.filter (fun edge -> edge.Gating)
        let nodes = gating |> List.collect (fun edge -> [ GroupEndpoint.sortKey edge.From; GroupEndpoint.sortKey edge.To ]) |> Text.distinctOrdinal
        let successors (node: string) = gating |> List.filter (fun edge -> GroupEndpoint.sortKey edge.From = node) |> List.map (fun edge -> GroupEndpoint.sortKey edge.To)
        let name (node: string) = node.Substring(node.IndexOf(':') + 1)
        stronglyConnected nodes successors |> List.map (List.map name)

    let private relation (configuration: PlannerConfiguration) (context: Context) (edges: GroupDependency list) (left: WorkGroup) (right: WorkGroup) =
        let leftId, rightId = WorkGroupId.value left.Id, WorkGroupId.value right.Id
        let open' (group: WorkGroup) = group.Members |> List.filter (fun entry -> entry.Status <> MemberStatus.Complete) |> List.choose (fun entry -> context.ById.TryFind entry.WorkItemId)

        let collisions =
            open' left
            |> List.collect (fun a -> open' right |> List.filter (fun b -> a.Id <> b.Id) |> List.map (Graph.collision configuration a))

        let worst =
            collisions
            |> List.map (fun collision -> collision.Risk)
            |> List.fold (fun worst risk -> if CollisionRisk.rank risk > CollisionRisk.rank worst then risk else worst) CollisionRisk.Safe

        let dependent =
            edges
            |> List.exists (fun edge ->
                edge.Gating
                && ((edge.From = GroupEndpoint.Group leftId && edge.To = GroupEndpoint.Group rightId)
                    || (edge.From = GroupEndpoint.Group rightId && edge.To = GroupEndpoint.Group leftId)))

        let admitted = collisions |> List.forall riskPolicy.Admits
        let concurrent = not dependent && admitted

        let reason =
            if dependent then $"{leftId} and {rightId} are ordered by a member dependency"
            elif not admitted then
                let signals = collisions |> List.filter (riskPolicy.Admits >> not) |> List.collect (fun collision -> collision.Signals) |> List.map CollisionSignal.describe |> List.distinct |> String.concat "; "
                $"cross-group collision risk {CollisionRisk.code worst} ({signals}) is not admitted by the {riskPolicy.Name} policy"
            else $"no dependency and cross-group collision risk {CollisionRisk.code worst} is admitted by the {riskPolicy.Name} policy; they may run concurrently"

        ({ Left = leftId
           Right = rightId
           Collision = worst
           Dependent = dependent
           MayRunConcurrently = concurrent
           Reason = reason }
        : GroupRelation)

    // ---- the report ---------------------------------------------------------------

    let private exclusionReason (context: Context) (item: ItemAnalysis) =
        match item.PlanningState with
        | PlanningWorkState.Complete -> Some "complete: nothing remains to execute (PRX-GRP-090 case 7)"
        | PlanningWorkState.Abandoned -> Some "abandoned"
        | PlanningWorkState.StaleStateCandidate -> Some "stale-state candidate: reconcile its recorded state before grouping it"
        | _ ->
            match context.Evidence.TryFind item.Id |> Option.map (fun evidence -> evidence.Location) with
            | Some ExecutionLocation.UnknownExternal ->
                Some "executes in an external repository that is not declared (grouping.executionRepositories); it is never grouped into this checkout (PRX-GRP-051)"
            | _ -> None

    let private build (input: PlanningInput) (analysis: PlanningAnalysis) : GroupingReport =
        let configuration = input.Configuration
        let grouping = configuration.Grouping
        let byId = analysis.Items |> List.map (fun item -> item.Id, item) |> Map.ofList
        let evidence = analysis.Items |> List.map (fun item -> item.Id, evidenceFor input item) |> Map.ofList

        let pairs =
            analysis.Items
            |> List.collect (fun left ->
                analysis.Items
                |> List.filter (fun right -> Text.ordinal left.Id right.Id < 0)
                |> List.map (fun right -> affinityBetween evidence[left.Id] evidence[right.Id]))

        let pairMap = pairs |> List.map (fun pair -> (pair.Left, pair.Right), pair) |> Map.ofList

        let pairOf (left: string) (right: string) =
            match pairMap.TryFind(key left right) with
            | Some pair -> pair
            | None ->
                ({ Left = fst (key left right)
                   Right = snd (key left right)
                   Level = ContextAffinity.Unknown
                   Signals = []
                   Confidence = EvidenceConfidence.Unknown
                   Statement = "unknown: not in the planning inventory" }
                : PairAffinity)

        let context =
            { Input = input
              Analysis = analysis
              ById = byId
              Evidence = evidence
              PairOf = pairOf }

        // Explicit declarations outrank every inferred grouping (PRX-GRP-090 case 3).
        let declaredMembers = grouping.Groups |> List.collect (fun group -> group.Members) |> Set.ofList

        let declaredCandidates =
            grouping.Groups
            |> List.map (fun declared ->
                let overlapping =
                    grouping.Groups
                    |> List.filter (fun other -> other.Id <> declared.Id && other.Members |> List.exists (fun id -> List.contains id declared.Members))
                    |> List.map (fun other -> other.Id)

                { Members = declared.Members |> List.filter byId.ContainsKey |> Text.distinctOrdinal
                  Origin = declared.Origin
                  Declared = Some declared
                  SplitOf = None
                  Notes =
                    overlapping
                    |> List.map (fun other -> note GroupNoteCode.DeclaredOverlap FindingSeverity.Warning $"shares members with declared group {other}; each member still has one lifecycle") })

        let excluded = analysis.Items |> List.choose (fun item -> exclusionReason context item |> Option.map (fun reason -> item.Id, reason)) |> Map.ofList

        let pool =
            analysis.Items
            |> List.filter (fun item -> not (excluded.ContainsKey item.Id) && not (declaredMembers.Contains item.Id))
            |> List.map (fun item -> item.Id)

        // Repository-local clustering: never mixes execution repositories.
        let byRepository =
            pool
            |> List.groupBy (fun id -> ExecutionLocation.describe evidence[id].Location)
            |> List.sortWith (fun (left, _) (right, _) -> Text.ordinal left right)

        let pressure = pressureLimits input.Observations

        let automatic =
            byRepository
            |> List.collect (fun (_, ids) -> cluster grouping.MinimumAffinity pairOf ids)
            |> List.collect (fun members -> splitCandidate grouping pressure (fst (sequence byId members)))

        let repositoryName (candidate: Candidate) =
            match candidate.Declared |> Option.bind (fun declared -> declared.ExecutionRepository) with
            | Some repository -> repository
            | None ->
                candidate.Members
                |> List.choose evidence.TryFind
                |> List.map (fun entry -> ExecutionLocation.describe entry.Location)
                |> Text.distinctOrdinal
                |> function
                    | [ single ] -> single
                    | _ -> "echelon"

        let labelled =
            automatic
            |> List.map (fun candidate ->
                let members = candidate.Members
                let pairs' = members |> List.collect (fun left -> members |> List.filter (fun right -> Text.ordinal left right < 0) |> List.map (pairOf left))
                let area = cohesionLines pairs' members |> List.tryHead |> Option.map (fun line -> line.Signal) |> areaOf
                let prefix = $"GROUP-{sanitize (repositoryName candidate)}-{sanitize area}"
                prefix, area, candidate)
            |> List.sortWith (fun (leftPrefix, _, left) (rightPrefix, _, right) ->
                match Text.ordinal leftPrefix rightPrefix with
                | 0 -> Text.ordinal (List.head left.Members) (List.head right.Members)
                | order -> order)
            |> List.fold
                (fun (numbered: (string * string * Candidate) list, counts: Map<string, int>) (prefix, area, candidate) ->
                    let next = (counts.TryFind prefix |> Option.defaultValue 0) + 1
                    numbered @ [ $"{prefix}-{next:D3}", area, candidate ], counts.Add(prefix, next))
                ([], Map.empty)
            |> fst

        let groups =
            (declaredCandidates |> List.map (fun candidate -> candidate.Declared.Value.Id, "declared", candidate)) @ labelled
            |> List.map (fun (id, area, candidate) ->
                let area = match candidate.Declared with | Some declared -> declared.SharedContext |> List.tryHead |> Option.defaultValue area | None -> area
                buildGroup context candidate id area)

        let grouped = groups |> List.collect (fun group -> group.Members |> List.map (fun entry -> entry.WorkItemId)) |> Set.ofList

        let ungrouped =
            analysis.Items
            |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState) && not (grouped.Contains item.Id))
            |> List.map (fun item ->
                let others = analysis.Items |> List.filter (fun other -> other.Id <> item.Id && not (PlanningWorkState.isTerminal other.PlanningState))
                let best = others |> List.map (fun other -> pairOf item.Id other.Id) |> List.sortByDescending (fun pair -> ContextAffinity.rank pair.Level) |> List.tryHead

                let level =
                    match best with
                    | Some pair when ContextAffinity.rank pair.Level > 0 -> pair.Level
                    | _ when not (hasScope evidence[item.Id]) -> ContextAffinity.Unknown
                    | Some pair -> pair.Level
                    | None -> ContextAffinity.Unknown

                let reason =
                    match excluded.TryFind item.Id, best with
                    | Some reason, _ -> reason
                    | None, _ when level = ContextAffinity.Unknown ->
                        "affinity unknown: the item carries no scope evidence (area tag, declared path, requirement, dependency or checkpoint), so the planner cannot say what it shares (PRX-GRP-090 case 11)"
                    | None, Some pair when ContextAffinity.rank pair.Level >= ContextAffinity.rank grouping.MinimumAffinity ->
                        let other = if pair.Left = item.Id then pair.Right else pair.Left

                        match excluded.TryFind other with
                        | Some why -> $"its related item {other} ({pair.Statement}) is not groupable: {why}"
                        | None -> $"related to {other} ({pair.Statement}) but not cohesive with any formed group: grouping needs {ContextAffinity.code grouping.MinimumAffinity} affinity with at least two thirds of a group's members"
                    | None, Some pair when ContextAffinity.rank pair.Level > 0 ->
                        $"strongest affinity is {pair.Statement}; below the {ContextAffinity.code grouping.MinimumAffinity} cohesion threshold (title similarity and ID families alone never group, PRX-GRP-022)"
                    | None, _ -> "no shared context with any other open item"

                ({ WorkItem = item.Id
                   PlanningState = item.PlanningState
                   Affinity = level
                   Reason = reason }
                : UngroupedItem))

        let edges = dependencies context groups

        let relations =
            groups
            |> List.collect (fun left ->
                groups
                |> List.filter (fun right -> Text.ordinal (WorkGroupId.value left.Id) (WorkGroupId.value right.Id) < 0)
                |> List.map (relation configuration context edges left))

        let unknowns =
            [ if not analysis.History.ContextOverhead.Sufficient then unmeasuredContext
              "historical co-change is not observable: telemetry does not yet link executions to the files they changed per work item"
              if configuration.Areas.IsEmpty then "no declared paths (planner configuration 'areas'): file and module overlap is unknown, only tags are compared"
              if grouping.Groups.IsEmpty then "no human-declared groups"
              if not analysis.History.Cost.Sufficient then "monetary cost is unknown" ]

        { Groups = groups
          Ungrouped = ungrouped
          Dependencies = edges
          Cycles = endpointCycles edges
          Relations = relations
          Affinities =
            pairs
            |> List.filter (fun pair ->
                let isOpen (id: string) = byId.TryFind id |> Option.exists (fun item -> not (PlanningWorkState.isTerminal item.PlanningState))
                ContextAffinity.rank pair.Level > 0 && isOpen pair.Left && isOpen pair.Right)
          Settings =
            { PreferredMinimumSize = grouping.PreferredMinimumSize
              PreferredMaximumSize = grouping.PreferredMaximumSize
              MaximumAutomaticSize = grouping.MaximumAutomaticSize
              MinimumAffinity = grouping.MinimumAffinity
              RiskPolicy = riskPolicy.Name }
          UnknownEvidence = unknowns
          Statement = advisoryStatement }

    /// PRX-GRP-070: the read-only group recommendations for a planning input.
    let recommend (input: PlanningInput) (analysis: PlanningAnalysis) : GroupingReport = build input analysis

    /// The context affinity between two inventory items (PRX-GRP-060).
    let affinity (input: PlanningInput) (analysis: PlanningAnalysis) (left: string) (right: string) : PairAffinity option =
        let find id = analysis.Items |> List.tryFind (fun item -> item.Id = id)

        match find left, find right with
        | Some a, Some b -> Some(affinityBetween (evidenceFor input a) (evidenceFor input b))
        | _ -> None

    // ---- explanation --------------------------------------------------------------

    /// PRX-GRP-071.
    let explain (input: PlanningInput) (analysis: PlanningAnalysis) (report: GroupingReport) (id: string) : Result<GroupExplanation, string> =
        match report.Groups |> List.tryFind (fun group -> WorkGroupId.value group.Id = id) with
        | None ->
            let known = report.Groups |> List.map (fun group -> WorkGroupId.value group.Id) |> String.concat ", "
            Error $"group '{id}' is not among the current recommendations ({known})"
        | Some group ->
            let members = group.Members |> List.map (fun entry -> entry.WorkItemId)
            let pairOf left right = affinity input analysis left right
            let groupOf (item: string) = report.Groups |> List.tryFind (fun other -> other.Members |> List.exists (fun entry -> entry.WorkItemId = item)) |> Option.map (fun other -> WorkGroupId.value other.Id)

            let excluded =
                analysis.Items
                |> List.filter (fun item -> not (List.contains item.Id members))
                |> List.choose (fun item ->
                    let links = members |> List.choose (pairOf item.Id) |> List.filter (fun pair -> ContextAffinity.rank pair.Level > 0)

                    match links with
                    | [] -> None
                    | _ ->
                        let best = links |> List.maxBy (fun pair -> ContextAffinity.rank pair.Level)
                        let qualifying = links |> List.filter (qualifies report.Settings.MinimumAffinity) |> List.length

                        let reason =
                            match groupOf item.Id, report.Ungrouped |> List.tryFind (fun entry -> entry.WorkItem = item.Id) with
                            | _ when PlanningWorkState.isTerminal item.PlanningState -> $"{PlanningWorkState.code item.PlanningState}: nothing remains to execute"
                            | Some other, _ -> $"placed in {other}, where it is more cohesive ({qualifying} of {members.Length} members of this group reach {ContextAffinity.code report.Settings.MinimumAffinity})"
                            | None, Some ungrouped -> ungrouped.Reason
                            | None, None -> "not eligible"

                        Some
                            { WorkItem = item.Id
                              Affinity = best.Level
                              Reason = $"{reason}; strongest link: {best.Statement}" })
                |> List.sortWith (fun left right ->
                    match compare (ContextAffinity.rank right.Affinity) (ContextAffinity.rank left.Affinity) with
                    | 0 -> Text.ordinal left.WorkItem right.WorkItem
                    | order -> order)

            let signals = group.Cohesion |> List.map (fun line -> AffinitySignal.basis line.Signal, line.Statement)

            let dependencyOrder =
                [ yield $"""required sequence: {String.concat " -> " group.RequiredSequence}"""
                  yield!
                      group.Members
                      |> List.filter (fun entry -> not entry.GatedBy.IsEmpty)
                      |> List.map (fun entry -> $"""{entry.WorkItemId} waits on blocked member(s) {String.concat ", " entry.GatedBy}""")
                  yield!
                      report.Dependencies
                      |> List.filter (fun edge -> edge.From = GroupEndpoint.Group id || edge.To = GroupEndpoint.Group id)
                      |> List.map (fun edge ->
                          let gating = if edge.Gating then "gating" else "satisfied/soft"
                          $"""{GroupEndpoint.describe edge.From} -> {GroupEndpoint.describe edge.To} ({gating}; via {String.concat "; " edge.Via})""") ]

            let collisionLines =
                [ yield $"intra-group collision risk: {CollisionRisk.code group.Collision}; parallel-safe: {group.ParallelSafe}"
                  yield!
                      group.CollisionPairs
                      |> List.map (fun collision ->
                          let signals = collision.Signals |> List.map CollisionSignal.describe |> String.concat "; "
                          $"{collision.Left} x {collision.Right}: {CollisionRisk.code collision.Risk} ({signals})")
                  yield!
                      report.Relations
                      |> List.filter (fun relation -> relation.Left = id || relation.Right = id)
                      |> List.map (fun relation -> relation.Reason) ]

            let wouldChange =
                [ "a human declaration (grouping.groups) naming these or other items would replace this recommendation"
                  "declared paths (planner configuration 'areas') would turn tag-level evidence into file-level evidence and change both affinity and collision risk"
                  "context-pressure evidence from a grouped execution of these members would split the group"
                  $"a member losing its shared evidence (tag, requirement, dependency) would drop it below the {ContextAffinity.code report.Settings.MinimumAffinity} cohesion threshold"
                  if group.ParallelSafe then "any collision signal between members would make execution strictly sequential"
                  else "proving member boundaries independent (collision-safe) would allow parallel subtasks under one owner"
                  "measured context reuse (repeated reads, time to first productive change) would replace 'unknown' savings with evidence" ]

            Ok
                { Group = group
                  WhyTogether = group.Cohesion |> List.map (fun line -> line.Statement)
                  Excluded = excluded
                  Evidence = signals
                  Inferred =
                    group.Cohesion
                    |> List.filter (fun line -> AffinitySignal.basis line.Signal = SignalBasis.Inferred)
                    |> List.map (fun line -> $"{line.Statement} (confidence {EvidenceConfidence.code (AffinitySignal.confidence line.Signal)})")
                  Unknown =
                    [ yield group.ContextCost.Statement
                      yield!
                          group.Members
                          |> List.filter (fun entry -> entry.Confidence = EvidenceConfidence.Unknown)
                          |> List.map (fun entry -> $"{entry.WorkItemId}: membership confidence unknown")
                      yield! report.UnknownEvidence |> List.filter ((<>) unmeasuredContext) ]
                  SharedArchitecture =
                    match group.SharedContext @ group.ArchitectureNotes with
                    | [] -> [ "no shared architecture is recorded; the group-level reasoning pass must establish it before mutation (PRX-GRP-040)" ]
                    | shared -> shared
                  DependencyOrder = dependencyOrder
                  CollisionRisk = collisionLines
                  ExecutionRationale = [ $"recommended execution: {GroupExecution.code group.Execution}" ] @ group.ExecutionReasons
                  WouldChange = wouldChange }

    // ---- group-level scheduling and comparison ------------------------------------

    type private ScheduleUnit =
        { Name: string
          IsGroup: bool
          Items: ItemAnalysis list }

    /// Schedulable items that still wait, directly or transitively, on a
    /// prerequisite that cannot run (blocked, captured, stale or cyclic).
    let private gated (analysis: PlanningAnalysis) : Map<string, string> =
        let byId = analysis.Items |> List.map (fun item -> item.Id, item) |> Map.ofList
        let cyclic = Graph.cycles analysis.Items |> List.concat |> Set.ofList

        let stuck =
            analysis.Items
            |> List.filter (fun item -> not (PlanningWorkState.isTerminal item.PlanningState) && not (PlanningWorkState.isSchedulable item.PlanningState))
            |> List.map (fun item -> item.Id, $"{item.Id} is {PlanningWorkState.code item.PlanningState}")
            |> Map.ofList

        let initial =
            cyclic |> Set.toList |> List.fold (fun (found: Map<string, string>) id -> found.Add(id, "member of a dependency cycle")) Map.empty

        let rec fixpoint (found: Map<string, string>) =
            let next =
                analysis.Items
                |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState && not (found.ContainsKey item.Id))
                |> List.choose (fun item ->
                    Graph.openHardPrerequisites item
                    |> List.filter byId.ContainsKey
                    |> List.tryPick (fun prerequisite ->
                        match stuck.TryFind prerequisite, found.TryFind prerequisite with
                        | Some why, _ -> Some(item.Id, $"waits on {why}")
                        | None, Some _ -> Some(item.Id, $"waits on {prerequisite}, which cannot run yet")
                        | None, None -> None))

            if next.IsEmpty then found else fixpoint (next |> List.fold (fun (map: Map<string, string>) (id, why) -> map.Add(id, why)) found)

        fixpoint initial

    let private unitsOf (analysis: PlanningAnalysis) (report: GroupingReport) =
        let waiting = gated analysis

        let schedulable (id: string) =
            analysis.Items |> List.tryFind (fun item -> item.Id = id && PlanningWorkState.isSchedulable item.PlanningState && not (waiting.ContainsKey item.Id))

        let groupUnits =
            report.Groups
            |> List.filter (fun group -> group.Execution <> GroupExecution.SplitByRepository)
            |> List.map (fun group ->
                { Name = WorkGroupId.value group.Id
                  IsGroup = true
                  Items = group.RequiredSequence |> List.choose schedulable })
            |> List.filter (fun unit -> not unit.Items.IsEmpty)

        let taken = groupUnits |> List.collect (fun unit -> unit.Items |> List.map (fun item -> item.Id)) |> Set.ofList

        let singles =
            analysis.Items
            |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState && not (taken.Contains item.Id) && not (waiting.ContainsKey item.Id))
            |> List.map (fun item -> { Name = item.Id; IsGroup = false; Items = [ item ] })

        groupUnits @ singles, waiting

    /// PRX-GRP-072 (`simulate --groups`): waves of groups and ungrouped items.
    /// A group runs as one sequential owner; groups run concurrently only when
    /// no dependency orders them and every cross pair is admitted by the
    /// policy.
    let schedule (configuration: PlannerConfiguration) (analysis: PlanningAnalysis) (report: GroupingReport) (maxConcurrency: int option) : GroupSchedule =
        let limit = maxConcurrency |> Option.defaultValue configuration.MaxConcurrency |> max 1
        let units, waiting = unitsOf analysis report
        let owner = units |> List.collect (fun unit -> unit.Items |> List.map (fun item -> item.Id, unit.Name)) |> Map.ofList
        let critical = Set.ofList analysis.CriticalPath.WorkItems

        let prerequisites (unit: ScheduleUnit) =
            unit.Items
            |> List.collect Graph.openHardPrerequisites
            |> List.choose owner.TryFind
            |> List.filter ((<>) unit.Name)
            |> Text.distinctOrdinal

        let admitted (left: ScheduleUnit) (right: ScheduleUnit) =
            left.Items
            |> List.forall (fun a -> right.Items |> List.forall (fun b -> riskPolicy.Admits(Graph.collision configuration a b)))

        let order =
            units
            |> List.sortWith (fun left right ->
                let onPath (unit: ScheduleUnit) = if unit.Items |> List.exists (fun item -> critical.Contains item.Id) then 0 else 1
                match compare (onPath left) (onPath right) with
                | 0 -> Text.ordinal left.Name right.Name
                | order -> order)

        let rec waves (finished: Set<string>) (remaining: ScheduleUnit list) (built: GroupWave list) =
            let available = remaining |> List.filter (fun unit -> prerequisites unit |> List.forall finished.Contains)

            let chosen =
                available
                |> List.fold
                    (fun (wave: ScheduleUnit list) unit ->
                        if wave.Length < limit && wave |> List.forall (admitted unit) then wave @ [ unit ] else wave)
                    []

            match chosen with
            | [] -> List.rev built, remaining
            | _ ->
                let entries =
                    chosen
                    |> List.map (fun unit ->
                        let estimate = unit.Items |> List.map (fun item -> item.RemainingDuration) |> Estimate.sumDurations

                        ({ Unit = unit.Name
                           IsGroup = unit.IsGroup
                           Members = unit.Items |> List.map (fun item -> item.Id)
                           Remaining = estimate
                           Reasons =
                             [ if unit.IsGroup then yield $"one owner executes {unit.Items.Length} member(s) in required sequence after one group-level reasoning pass"
                               else yield "ungrouped item: its own execution"
                               if unit.Items |> List.exists (fun item -> critical.Contains item.Id) then yield "contains critical-path work"
                               if chosen.Length > 1 then yield $"runs alongside {chosen.Length - 1} other unit(s): no ordering dependency and every cross pair admitted by {riskPolicy.Name}" ] }
                        : ScheduledUnit))

                let wave =
                    { Number = built.Length + 1
                      Units = entries
                      ExpectedDuration = entries |> List.map (fun entry -> entry.Remaining) |> Estimate.maxDurations }

                let done' = chosen |> List.fold (fun set unit -> Set.add unit.Name set) finished
                waves done' (remaining |> List.filter (fun unit -> not (List.contains unit chosen))) (wave :: built)

        let built, stuck = waves Set.empty order []

        { MaxConcurrency = limit
          RiskPolicy = riskPolicy.Name
          Waves = built
          NotScheduled =
            (stuck |> List.map (fun unit -> unit.Name, "waits on a unit that could not be scheduled"))
            @ (waiting |> Map.toList |> List.map (fun (id, why) -> id, $"not scheduled: {why}"))
          ExpectedDuration = built |> List.map (fun wave -> wave.ExpectedDuration) |> Estimate.sumDurations
          ContextAcquisitions = built |> List.sumBy (fun wave -> wave.Units.Length)
          IndependentContextAcquisitions = built |> List.sumBy (fun wave -> wave.Units |> List.sumBy (fun unit -> unit.Members.Length))
          Statement =
            $"portfolio -> groups -> items: each group is one reasoning owner; units co-run only under the {riskPolicy.Name} policy. Durations are the members' item estimates summed; the context reuse that grouping might add is not subtracted from them ({contextNote analysis.History})." }

    /// PRX-GRP-072 (`compare --groups`): grouped versus independent execution
    /// of each group, and the group schedule versus the item-level speed plan.
    let compare (configuration: PlannerConfiguration) (analysis: PlanningAnalysis) (report: GroupingReport) (itemPlan: ExecutionPlan) (maxConcurrency: int option) : GroupComparison =
        let limit = maxConcurrency |> Option.defaultValue configuration.MaxConcurrency |> max 1

        let tradeoffs =
            report.Groups
            |> List.map (fun group ->
                // Hypothetical by design: every open member counts, and the
                // ones that cannot run yet are named rather than dropped.
                let items =
                    group.RequiredSequence
                    |> List.choose (fun id -> analysis.Items |> List.tryFind (fun item -> item.Id = id && not (PlanningWorkState.isTerminal item.PlanningState)))

                let notYetRunnable = items |> List.filter (fun item -> not (PlanningWorkState.isSchedulable item.PlanningState)) |> List.map (fun item -> item.Id)

                let durations = items |> List.map (fun item -> item.RemainingDuration)
                let pairs = items |> List.collect (fun a -> items |> List.filter (fun b -> Text.ordinal a.Id b.Id < 0) |> List.map (Graph.collision configuration a))
                let independentParallel = not items.IsEmpty && pairs |> List.forall riskPolicy.Admits && not (group.Cohesion |> List.exists (fun line -> match line.Signal with AffinitySignal.HardDependency _ -> true | _ -> false))
                let peak = if independentParallel then min limit (max 1 items.Length) else min 1 items.Length

                let independentDuration =
                    if independentParallel then
                        durations |> List.chunkBySize (max 1 limit) |> List.map Estimate.maxDurations |> Estimate.sumDurations
                    else
                        Estimate.sumDurations durations

                let shared =
                    match group.SharedContext with
                    | [] -> "no recorded shared context"
                    | values -> String.concat "; " values

                { Group = WorkGroupId.value group.Id
                  Members = items |> List.map (fun item -> item.Id)
                  NotYetRunnable = notYetRunnable
                  Independent =
                    { Executions = items.Length
                      ContextAcquisitions = items.Length
                      ExpectedDuration = independentDuration
                      PeakConcurrency = peak
                      DesignOwners = items.Length }
                  Grouped =
                    { Executions = (if items.IsEmpty then 0 else 1)
                      ContextAcquisitions = (if items.IsEmpty then 0 else 1)
                      ExpectedDuration = Estimate.sumDurations durations
                      PeakConcurrency = min 1 items.Length
                      DesignOwners = min 1 items.Length }
                  ContextSaving = coldStartSaving analysis.History (max 0 (items.Length - 1))
                  ArchitectureConsideration =
                    $"independent execution gives {items.Length} separate design owner(s) over shared context ({shared}); grouped execution gives one, which must still keep per-item attribution"
                  ContextPressureRisk =
                    if items.Length > report.Settings.PreferredMaximumSize then $"{items.Length} members is above the preferred maximum; context pressure is likely"
                    elif group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.ContextPressureObserved || entry.Code = GroupNoteCode.SplitForContextPressure) then "context pressure has been observed for these members"
                    else "no context-pressure evidence yet" })

        let portfolio = schedule configuration analysis report maxConcurrency

        { Tradeoffs = tradeoffs
          Portfolio = portfolio
          ItemPlanDuration = itemPlan.ExpectedDuration
          ItemPlanContextAcquisitions = itemPlan.Proxy.ContextAcquisitions
          Statement =
            "Grouping is compared, not assumed: it replaces cold starts with one shared context and serializes members under one owner. Whether that saves tokens or time, or improves architecture, is what the grouping experiment measures (PRX-GRP-080); no strategy is universally better." }
