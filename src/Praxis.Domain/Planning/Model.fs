namespace Praxis.Domain.Planning

open System
open System.Globalization
open Praxis.Domain.Work

// The typed vocabulary of the advisory planner (requirements/PLANNING-OPTIMIZATION.md).
// Everything here is data: no clock, filesystem, Git, network or provider
// access (PRX-PLAN-003). Unknown values stay explicit (PRX-PLAN-110).

[<RequireQualifiedAccess>]
type EvidenceConfidence =
    | High
    | Medium
    | Low
    | Unknown

[<RequireQualifiedAccess>]
module EvidenceConfidence =
    let code confidence =
        match confidence with
        | EvidenceConfidence.High -> "high"
        | EvidenceConfidence.Medium -> "medium"
        | EvidenceConfidence.Low -> "low"
        | EvidenceConfidence.Unknown -> "unknown"

    let tryParse value =
        match value with
        | "high" -> Some EvidenceConfidence.High
        | "medium" -> Some EvidenceConfidence.Medium
        | "low" -> Some EvidenceConfidence.Low
        | "unknown" -> Some EvidenceConfidence.Unknown
        | _ -> None

    let rank confidence =
        match confidence with
        | EvidenceConfidence.High -> 3
        | EvidenceConfidence.Medium -> 2
        | EvidenceConfidence.Low -> 1
        | EvidenceConfidence.Unknown -> 0

    let private ofRank rank =
        match rank with
        | r when r >= 3 -> EvidenceConfidence.High
        | 2 -> EvidenceConfidence.Medium
        | 1 -> EvidenceConfidence.Low
        | _ -> EvidenceConfidence.Unknown

    let min (left: EvidenceConfidence) (right: EvidenceConfidence) = ofRank (Operators.min (rank left) (rank right))

    let minimum (values: EvidenceConfidence list) =
        values |> List.fold min EvidenceConfidence.High

    /// One level less certain; `Unknown` stays `Unknown`.
    let downgrade confidence = ofRank (rank confidence - 1)

    let cap (ceiling: EvidenceConfidence) (confidence: EvidenceConfidence) = min ceiling confidence

/// A value that may be unknown at any bound. `None` is never coerced to zero.
type Estimate<'a> =
    { Lower: 'a option
      Expected: 'a option
      Upper: 'a option
      Confidence: EvidenceConfidence }

[<RequireQualifiedAccess>]
module Estimate =
    let unknown<'a> : Estimate<'a> =
        { Lower = None
          Expected = None
          Upper = None
          Confidence = EvidenceConfidence.Unknown }

    let isKnown (estimate: Estimate<'a>) = estimate.Expected.IsSome

    let private allSome (values: 'a option list) =
        if values |> List.forall Option.isSome then Some(values |> List.choose id) else None

    /// Sequential composition of durations. A lower bound of the known parts
    /// is still a valid lower bound (durations are non-negative); expected and
    /// upper become unknown as soon as any part is unknown.
    let sumDurations (estimates: Estimate<int64> list) : Estimate<int64> =
        match estimates with
        | [] ->
            { Lower = Some 0L
              Expected = Some 0L
              Upper = Some 0L
              Confidence = EvidenceConfidence.High }
        | _ ->
            let knownLowers = estimates |> List.choose (fun estimate -> estimate.Lower)

            { Lower = if knownLowers.IsEmpty then None else Some(List.sum knownLowers)
              Expected = estimates |> List.map (fun estimate -> estimate.Expected) |> allSome |> Option.map List.sum
              Upper = estimates |> List.map (fun estimate -> estimate.Upper) |> allSome |> Option.map List.sum
              Confidence = estimates |> List.map (fun estimate -> estimate.Confidence) |> EvidenceConfidence.minimum }

    /// Parallel composition: a wave lasts as long as its longest member.
    let maxDurations (estimates: Estimate<int64> list) : Estimate<int64> =
        match estimates with
        | [] -> sumDurations []
        | _ ->
            let knownLowers = estimates |> List.choose (fun estimate -> estimate.Lower)

            { Lower = if knownLowers.IsEmpty then None else Some(List.max knownLowers)
              Expected = estimates |> List.map (fun estimate -> estimate.Expected) |> allSome |> Option.map List.max
              Upper = estimates |> List.map (fun estimate -> estimate.Upper) |> allSome |> Option.map List.max
              Confidence = estimates |> List.map (fun estimate -> estimate.Confidence) |> EvidenceConfidence.minimum }

type Money = { Amount: decimal; Currency: string }

[<RequireQualifiedAccess>]
module Money =
    let sum (currency: string) (values: Money list) =
        { Amount = values |> List.sumBy (fun value -> value.Amount); Currency = currency }

/// PRX-PLAN-051: where a cost number came from.
[<RequireQualifiedAccess>]
type CostEvidenceKind =
    | Observed
    | ProviderReported
    | Calculated
    | Estimated
    | Unavailable

[<RequireQualifiedAccess>]
module CostEvidenceKind =
    let code kind =
        match kind with
        | CostEvidenceKind.Observed -> "observed"
        | CostEvidenceKind.ProviderReported -> "provider-reported"
        | CostEvidenceKind.Calculated -> "calculated"
        | CostEvidenceKind.Estimated -> "estimated"
        | CostEvidenceKind.Unavailable -> "unavailable"

    let tryParse value =
        match value with
        | "observed" -> Some CostEvidenceKind.Observed
        | "provider-reported" -> Some CostEvidenceKind.ProviderReported
        | "calculated" -> Some CostEvidenceKind.Calculated
        | "estimated" -> Some CostEvidenceKind.Estimated
        | "unavailable" -> Some CostEvidenceKind.Unavailable
        | _ -> None

/// PRX-PLAN-131: the kind of source a planning conclusion rests on.
[<RequireQualifiedAccess>]
type EvidenceSource =
    | BacklogQueue
    | LiveContext
    | Checkpoint
    | Telemetry
    | Git
    | ContinuousIntegration
    | GitHub
    | StructuredDependency
    | InferredDependency
    | ExternalObservation
    | PlannerConfiguration
    | PlannerAssumption

[<RequireQualifiedAccess>]
module EvidenceSource =
    let all =
        [ EvidenceSource.BacklogQueue
          EvidenceSource.LiveContext
          EvidenceSource.Checkpoint
          EvidenceSource.Telemetry
          EvidenceSource.Git
          EvidenceSource.ContinuousIntegration
          EvidenceSource.GitHub
          EvidenceSource.StructuredDependency
          EvidenceSource.InferredDependency
          EvidenceSource.ExternalObservation
          EvidenceSource.PlannerConfiguration
          EvidenceSource.PlannerAssumption ]

    let code source =
        match source with
        | EvidenceSource.BacklogQueue -> "backlog-queue"
        | EvidenceSource.LiveContext -> "live-context"
        | EvidenceSource.Checkpoint -> "checkpoint"
        | EvidenceSource.Telemetry -> "telemetry"
        | EvidenceSource.Git -> "git"
        | EvidenceSource.ContinuousIntegration -> "ci"
        | EvidenceSource.GitHub -> "github"
        | EvidenceSource.StructuredDependency -> "structured-dependency"
        | EvidenceSource.InferredDependency -> "inferred-dependency"
        | EvidenceSource.ExternalObservation -> "external-observation"
        | EvidenceSource.PlannerConfiguration -> "planner-configuration"
        | EvidenceSource.PlannerAssumption -> "planner-assumption"

    let tryParse value = all |> List.tryFind (fun source -> code source = value)

type Provenance =
    { Source: EvidenceSource
      Reference: string }

[<RequireQualifiedAccess>]
module Provenance =
    let create source reference = { Source = source; Reference = reference }

/// PRX-PLAN-011: the planner's own classification. It never replaces or
/// rewrites the recorded lifecycle state.
[<RequireQualifiedAccess>]
type PlanningWorkState =
    | Captured
    | Ready
    | Active
    | Blocked
    | PartiallyComplete
    | AwaitingEvidence
    | AwaitingHuman
    | StaleStateCandidate
    | Complete
    | Abandoned

[<RequireQualifiedAccess>]
module PlanningWorkState =
    let all =
        [ PlanningWorkState.Captured
          PlanningWorkState.Ready
          PlanningWorkState.Active
          PlanningWorkState.Blocked
          PlanningWorkState.PartiallyComplete
          PlanningWorkState.AwaitingEvidence
          PlanningWorkState.AwaitingHuman
          PlanningWorkState.StaleStateCandidate
          PlanningWorkState.Complete
          PlanningWorkState.Abandoned ]

    let code state =
        match state with
        | PlanningWorkState.Captured -> "captured"
        | PlanningWorkState.Ready -> "ready"
        | PlanningWorkState.Active -> "active"
        | PlanningWorkState.Blocked -> "blocked"
        | PlanningWorkState.PartiallyComplete -> "partially-complete"
        | PlanningWorkState.AwaitingEvidence -> "awaiting-evidence"
        | PlanningWorkState.AwaitingHuman -> "awaiting-human"
        | PlanningWorkState.StaleStateCandidate -> "stale-state-candidate"
        | PlanningWorkState.Complete -> "complete"
        | PlanningWorkState.Abandoned -> "abandoned"

    let tryParse value = all |> List.tryFind (fun state -> code state = value)

    let isTerminal state =
        state = PlanningWorkState.Complete || state = PlanningWorkState.Abandoned

    /// Only these may ever be placed in an execution wave.
    let isSchedulable state =
        match state with
        | PlanningWorkState.Ready
        | PlanningWorkState.Active
        | PlanningWorkState.PartiallyComplete -> true
        | _ -> false

/// PRX-PLAN-012: why captured work is not runnable yet.
[<RequireQualifiedAccess>]
type TriageNeed =
    | NeedsTriage
    | LacksAcceptanceCriteria
    | LacksDependencyInformation
    | DeferredForLater

[<RequireQualifiedAccess>]
module TriageNeed =
    let all =
        [ TriageNeed.NeedsTriage
          TriageNeed.LacksAcceptanceCriteria
          TriageNeed.LacksDependencyInformation
          TriageNeed.DeferredForLater ]

    let code need =
        match need with
        | TriageNeed.NeedsTriage -> "needs-triage"
        | TriageNeed.LacksAcceptanceCriteria -> "lacks-acceptance-criteria"
        | TriageNeed.LacksDependencyInformation -> "lacks-dependency-information"
        | TriageNeed.DeferredForLater -> "deferred-for-later"

    let tryParse value = all |> List.tryFind (fun need -> code need = value)

/// PRX-PLAN-040.
[<RequireQualifiedAccess>]
type DependencyKind =
    | Hard
    | Soft
    | External
    | Human
    | Evidence

[<RequireQualifiedAccess>]
module DependencyKind =
    let all =
        [ DependencyKind.Hard
          DependencyKind.Soft
          DependencyKind.External
          DependencyKind.Human
          DependencyKind.Evidence ]

    let code kind =
        match kind with
        | DependencyKind.Hard -> "hard"
        | DependencyKind.Soft -> "soft"
        | DependencyKind.External -> "external"
        | DependencyKind.Human -> "human"
        | DependencyKind.Evidence -> "evidence"

    let tryParse value = all |> List.tryFind (fun kind -> code kind = value)

/// PRX-PLAN-043: structured dependencies carry higher evidence quality than
/// ones derived from free text.
[<RequireQualifiedAccess>]
type DependencyOrigin =
    | Structured
    | Inferred

[<RequireQualifiedAccess>]
module DependencyOrigin =
    let code origin =
        match origin with
        | DependencyOrigin.Structured -> "structured"
        | DependencyOrigin.Inferred -> "inferred"

    let tryParse value =
        match value with
        | "structured" -> Some DependencyOrigin.Structured
        | "inferred" -> Some DependencyOrigin.Inferred
        | _ -> None

[<RequireQualifiedAccess>]
type DependencyTarget =
    | WorkItem of id: string
    | PullRequest of number: int
    | Release of tag: string
    | ContinuousIntegration of subject: string
    | HumanAction of description: string

[<RequireQualifiedAccess>]
module DependencyTarget =
    let describe target =
        match target with
        | DependencyTarget.WorkItem id -> id
        | DependencyTarget.PullRequest number -> $"PR #{number}"
        | DependencyTarget.Release tag -> $"release {tag}"
        | DependencyTarget.ContinuousIntegration subject -> $"CI for {subject}"
        | DependencyTarget.HumanAction description -> $"human action: {description}"

    let kindCode target =
        match target with
        | DependencyTarget.WorkItem _ -> "work-item"
        | DependencyTarget.PullRequest _ -> "pull-request"
        | DependencyTarget.Release _ -> "release"
        | DependencyTarget.ContinuousIntegration _ -> "ci"
        | DependencyTarget.HumanAction _ -> "human-action"

    let value target =
        match target with
        | DependencyTarget.WorkItem id -> id
        | DependencyTarget.PullRequest number -> string number
        | DependencyTarget.Release tag -> tag
        | DependencyTarget.ContinuousIntegration subject -> subject
        | DependencyTarget.HumanAction description -> description

    let tryParse (kind: string) (value: string) =
        match kind with
        | "work-item" -> Some(DependencyTarget.WorkItem value)
        | "pull-request" ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, number -> Some(DependencyTarget.PullRequest number)
            | _ -> None
        | "release" -> Some(DependencyTarget.Release value)
        | "ci" -> Some(DependencyTarget.ContinuousIntegration value)
        | "human-action" -> Some(DependencyTarget.HumanAction value)
        | _ -> None

type Dependency =
    { Target: DependencyTarget
      Kind: DependencyKind
      Origin: DependencyOrigin
      Statement: string
      Provenance: Provenance }

[<RequireQualifiedAccess>]
type DependencyStatus =
    | Satisfied
    | Unsatisfied
    | Undetermined

[<RequireQualifiedAccess>]
module DependencyStatus =
    let code status =
        match status with
        | DependencyStatus.Satisfied -> "satisfied"
        | DependencyStatus.Unsatisfied -> "unsatisfied"
        | DependencyStatus.Undetermined -> "undetermined"

    let tryParse value =
        match value with
        | "satisfied" -> Some DependencyStatus.Satisfied
        | "unsatisfied" -> Some DependencyStatus.Unsatisfied
        | "undetermined" -> Some DependencyStatus.Undetermined
        | _ -> None

type ResolvedDependency =
    { Dependency: Dependency
      Status: DependencyStatus
      Reason: string }

/// External evidence observed outside Praxis's own recorded state
/// (PRX-PLAN-021). Observations never rewrite recorded state.
[<RequireQualifiedAccess>]
type ObservationKind =
    | PullRequestMerged of number: int
    | CommitMerged of commit: string * into: string
    | ContinuousIntegrationPassed of subject: string
    | ContinuousIntegrationFailed of subject: string
    | ReleaseExists of tag: string
    /// Evidence that a grouped execution over these members strained its
    /// context (compactions, re-reads, forgotten requirements...), as
    /// indicator name and count (PRX-GRP-074).
    | ContextPressure of members: string list * indicators: (string * int) list

type Observation =
    { Kind: ObservationKind
      Provenance: Provenance }

type CheckpointSummary =
    { CheckpointId: string
      ExecutionId: string
      RecordedAt: string
      Branch: string
      Commit: string
      Summary: string
      NextAction: string
      Verified: bool }

/// A backlog queue row, as much of it as planning reads.
type PlanningQueueItem =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string option
      Status: string
      CreatedAt: string option
      DependsOn: string list }

/// A live-context work item, as much of it as planning reads.
type PlanningLiveItem =
    { Id: string
      State: LiveWorkState
      BlockReason: string option
      UpdatedAt: string option
      Checkpoint: CheckpointSummary option
      TelemetryExecutionIds: string list }

type CostObservation =
    { MetricId: string
      Amount: decimal
      Currency: string option
      Kind: CostEvidenceKind }

[<RequireQualifiedAccess>]
type ExecutionStatus =
    | Finalized
    | Active

/// Session-transcript evidence an execution carries (PRAXIS-PLAN-05): the
/// `anthropic-claude-session` adapter's metrics, each unavailable unless the
/// execution recorded it.
type SessionEvidence =
    { /// `time.active_ms`: runtime-measured active session time.
      ActiveMs: int64 option
      /// `time.first_code_change_ms`: the session's cold start.
      FirstCodeChangeMs: int64 option
      /// `context.governance_reads`.
      GovernanceReads: int option
      /// `context.repeated_file_reads`.
      RepeatedReads: int option }

[<RequireQualifiedAccess>]
module SessionEvidence =
    let none =
        { ActiveMs = None
          FirstCodeChangeMs = None
          GovernanceReads = None
          RepeatedReads = None }

/// One telemetry execution record, reduced to what planning reads.
type HistoricalExecution =
    { ExecutionId: string
      WorkItemId: string
      Status: ExecutionStatus
      StartedAt: string
      FinalizedAt: string option
      Classes: string list
      WallMs: int64 option
      BlockedMs: int64 option
      Provider: string
      Runtime: string
      Model: string option
      Costs: CostObservation list
      TokenMetrics: int
      Session: SessionEvidence }

/// PRX-PLAN-093: explicit balanced weights. They are reported with every
/// balanced plan.
type BalancedWeights =
    { Duration: decimal
      Cost: decimal
      ConflictRisk: decimal
      Uncertainty: decimal
      ContextReuse: decimal
      CompletionLikelihood: decimal }

/// The explicit, reported assumption about how much of an item's full effort
/// remains, per continuation phase, as (lower, upper) fractions.
type RemainingFractions =
    { FinalizationOnly: decimal * decimal
      VerificationRemaining: decimal * decimal
      ImplementationInProgress: decimal * decimal
      Unclassified: decimal * decimal }

type DeclaredDependency =
    { From: string
      To: string
      Kind: DependencyKind }

type DeclaredConflict =
    { Left: string
      Right: string
      Reason: string }

/// PRX-GRP-011: where a group came from.
[<RequireQualifiedAccess>]
type GroupOrigin =
    | PlannerRecommended
    | HumanDeclared
    | DependencyDerived
    | ArchitectureDeclared

[<RequireQualifiedAccess>]
module GroupOrigin =
    let all =
        [ GroupOrigin.PlannerRecommended
          GroupOrigin.HumanDeclared
          GroupOrigin.DependencyDerived
          GroupOrigin.ArchitectureDeclared ]

    let code origin =
        match origin with
        | GroupOrigin.PlannerRecommended -> "planner-recommended"
        | GroupOrigin.HumanDeclared -> "human-declared"
        | GroupOrigin.DependencyDerived -> "dependency-derived"
        | GroupOrigin.ArchitectureDeclared -> "architecture-declared"

    let tryParse value = all |> List.tryFind (fun origin -> code origin = value)

/// PRX-GRP-011: what a group's members share.
[<RequireQualifiedAccess>]
type GroupKind =
    | SharedArea
    | SharedArchitecture
    | DependencyChain
    | SharedFiles
    | SharedDataModel
    | SharedApiSurface
    | SharedMigration
    | SharedTestSurface
    | ContextAffinity
    | Custom of name: string

[<RequireQualifiedAccess>]
module GroupKind =
    let private named =
        [ GroupKind.SharedArea, "shared-area"
          GroupKind.SharedArchitecture, "shared-architecture"
          GroupKind.DependencyChain, "dependency-chain"
          GroupKind.SharedFiles, "shared-files"
          GroupKind.SharedDataModel, "shared-data-model"
          GroupKind.SharedApiSurface, "shared-api-surface"
          GroupKind.SharedMigration, "shared-migration"
          GroupKind.SharedTestSurface, "shared-test-surface"
          GroupKind.ContextAffinity, "context-affinity" ]

    let code kind =
        match kind with
        | GroupKind.Custom name -> $"custom:{name}"
        | known -> named |> List.find (fst >> (=) known) |> snd

    let tryParse (value: string) =
        match value with
        | custom when custom.StartsWith("custom:", StringComparison.Ordinal) && custom.Length > 7 -> Some(GroupKind.Custom(custom.Substring 7))
        | _ -> named |> List.tryFind (snd >> (=) value) |> Option.map fst

/// PRX-GRP-060: how much context two work items share. `Unknown` is not
/// `None`: it means the evidence to decide is missing.
[<RequireQualifiedAccess>]
type ContextAffinity =
    | Unknown
    | None
    | Low
    | Medium
    | High

[<RequireQualifiedAccess>]
module ContextAffinity =
    let all =
        [ ContextAffinity.Unknown
          ContextAffinity.None
          ContextAffinity.Low
          ContextAffinity.Medium
          ContextAffinity.High ]

    let code affinity =
        match affinity with
        | ContextAffinity.Unknown -> "unknown"
        | ContextAffinity.None -> "none"
        | ContextAffinity.Low -> "low"
        | ContextAffinity.Medium -> "medium"
        | ContextAffinity.High -> "high"

    let tryParse value = all |> List.tryFind (fun affinity -> code affinity = value)

    /// Strength for thresholds; `Unknown` never meets one.
    let rank affinity =
        match affinity with
        | ContextAffinity.Unknown
        | ContextAffinity.None -> 0
        | ContextAffinity.Low -> 1
        | ContextAffinity.Medium -> 2
        | ContextAffinity.High -> 3

/// A group a human declared in planner configuration (PRX-GRP-073). It
/// outranks every inferred grouping of the same items.
type DeclaredGroup =
    { Id: string
      Members: string list
      Kind: GroupKind option
      Origin: GroupOrigin
      SharedContext: string list
      ExecutionRepository: string option
      CrossRepository: bool
      ArchitectureNotes: string list
      /// Per-group opt-out (PRX-GRP-132): the group executes independently,
      /// for this recorded reason.
      IndependentReason: string option
      /// Per-item opt-outs (PRX-GRP-132): (member, reason). The member stays
      /// a member but executes in its own fresh context.
      IndependentMembers: (string * string) list }

/// An accepted architecture decision that materially affects several items
/// (PRX-GRP-020, PRX-GRP-074 merging).
type DeclaredArchitecture =
    { Decision: string
      Members: string list
      Statement: string }

/// Where a member repository's Praxis state can be read (PRX-GRP-103): a
/// local clone and the fetched ref to read from. Praxis never fetches,
/// writes or commits there; it reads `REF:.ros/...` with `git show`.
type RepositorySource =
    { Repository: string
      Path: string
      /// The ref to read; `None` reads the clone's `origin/HEAD`.
      Ref: string option }

/// What the planner recommends for a qualifying group (PRX-GRP-130, 138):
/// `Grouped` makes grouped execution the default; `Advisory` is the
/// rollback, a configuration change only.
[<RequireQualifiedAccess>]
type GroupedDefault =
    | Grouped
    | Advisory

/// `grouping.groupedExecution` (PRX-GRP-131, PRX-GRP-136).
type GroupedExecutionConfiguration =
    { Default: GroupedDefault
      MinimumSize: int
      MaximumSize: int
      /// Fallback when a member's `context.compactions` reaches this.
      CompactionLimit: int
      /// Fallback when a member's `context.repeated_file_reads` exceeds this.
      RepeatedReadLimit: int
      /// Fallback when a member's elapsed time exceeds this factor of its
      /// upper duration estimate.
      ElapsedFactor: decimal }

[<RequireQualifiedAccess>]
module GroupedExecutionConfiguration =
    let defaults =
        { Default = GroupedDefault.Grouped
          MinimumSize = 2
          MaximumSize = 6
          CompactionLimit = 1
          RepeatedReadLimit = 25
          ElapsedFactor = 1.5m }

/// `grouping.crossRepository` (PRX-GRP-103, PRX-GRP-109).
type CrossRepositoryConfiguration =
    { MaxObservationAgeMinutes: int
      Sources: RepositorySource list }

[<RequireQualifiedAccess>]
module CrossRepositoryConfiguration =
    let defaults = { MaxObservationAgeMinutes = 1440; Sources = [] }

/// PRX-GRP-030: size and cohesion boundaries, human declarations, and where
/// items are executed (PRX-GRP-051). Every value is reported with a group.
type GroupingConfiguration =
    { PreferredMinimumSize: int
      PreferredMaximumSize: int
      MaximumAutomaticSize: int
      MinimumAffinity: ContextAffinity
      Groups: DeclaredGroup list
      Architecture: DeclaredArchitecture list
      ExecutionRepositories: (string * string) list
      CrossRepository: CrossRepositoryConfiguration
      GroupedExecution: GroupedExecutionConfiguration }

[<RequireQualifiedAccess>]
module GroupingConfiguration =
    let defaults =
        { PreferredMinimumSize = 3
          PreferredMaximumSize = 10
          MaximumAutomaticSize = 12
          MinimumAffinity = ContextAffinity.Medium
          Groups = []
          Architecture = []
          ExecutionRepositories = []
          CrossRepository = CrossRepositoryConfiguration.defaults
          GroupedExecution = GroupedExecutionConfiguration.defaults }

type PlannerConfiguration =
    { MaxConcurrency: int
      MinimumCostSamples: int
      PraxisStateMergeSafe: bool
      GenericTags: string list
      BalancedWeights: BalancedWeights
      RemainingFractions: RemainingFractions
      Dependencies: DeclaredDependency list
      Conflicts: DeclaredConflict list
      Areas: (string * string list) list
      Grouping: GroupingConfiguration }

[<RequireQualifiedAccess>]
module PlannerConfiguration =
    let defaultWeights =
        { Duration = 0.30m
          Cost = 0.15m
          ConflictRisk = 0.15m
          Uncertainty = 0.10m
          ContextReuse = 0.10m
          CompletionLikelihood = 0.20m }

    let defaultFractions =
        { FinalizationOnly = 0.00m, 0.10m
          VerificationRemaining = 0.05m, 0.25m
          ImplementationInProgress = 0.25m, 0.75m
          Unclassified = 0.10m, 0.90m }

    let defaults =
        { MaxConcurrency = 3
          MinimumCostSamples = 5
          PraxisStateMergeSafe = false
          GenericTags = [ "follow-up"; "high"; "medium"; "low"; "code"; "testing" ]
          BalancedWeights = defaultWeights
          RemainingFractions = defaultFractions
          Dependencies = []
          Conflicts = []
          Areas = []
          Grouping = GroupingConfiguration.defaults }

/// One ended group execution, as the planner prices grouped work
/// (PRX-GRP-155): its mode, how many members it began, the executions it
/// covers, and its shared session total when recorded.
type GroupedSample =
    { GroupExecutionId: string
      Mode: string
      Members: int
      ExecutionIds: string list
      CostTotal: decimal option
      Currency: string option
      ActiveMs: int64 option }

/// Everything a plan is computed from. Identical inputs produce identical
/// plans (PRX-PLAN-002).
/// Provider capacity as planning evidence (PRX-QUAL-009). Provider-neutral:
/// the provider is an opaque name and no provider-specific rule lives in the
/// planner. `Unknown` is uncertainty, never zero capacity.
[<RequireQualifiedAccess>]
type CapacityState =
    | Available
    | Constrained of until: string option * reason: string
    | Exhausted of until: string option * reason: string
    | Unknown of reason: string

[<RequireQualifiedAccess>]
module CapacityState =
    let code state =
        match state with
        | CapacityState.Available -> "available"
        | CapacityState.Constrained _ -> "constrained"
        | CapacityState.Exhausted _ -> "exhausted"
        | CapacityState.Unknown _ -> "unknown"

    /// Constrained or exhausted: provider work is better deferred.
    let isLimited state =
        match state with
        | CapacityState.Constrained _
        | CapacityState.Exhausted _ -> true
        | CapacityState.Available
        | CapacityState.Unknown _ -> false

    let describe state =
        let until value = value |> Option.map (fun at -> $" until {at}") |> Option.defaultValue ""

        match state with
        | CapacityState.Available -> "available"
        | CapacityState.Constrained(at, reason) -> $"constrained{until at} ({reason})"
        | CapacityState.Exhausted(at, reason) -> $"exhausted{until at} ({reason})"
        | CapacityState.Unknown reason -> $"unknown ({reason})"

type ProviderCapacity =
    { Provider: string
      State: CapacityState
      Provenance: Provenance }

/// How provider capacity bears on this plan.
type CapacityAssessment =
    { Providers: ProviderCapacity list
      /// Some provider is constrained or exhausted.
      Limited: bool
      /// Runnable items that need no model provider (tag `provider-free`).
      ProviderFreeItems: string list
      /// Whether capacity changed the order the strategies use.
      AffectsOrdering: bool
      Statement: string }

type PlanningInput =
    { Repository: string
      Commit: string option
      Branch: string option
      PlannedAt: string
      PlannerVersion: string
      Queue: PlanningQueueItem list
      Live: PlanningLiveItem list
      Executions: HistoricalExecution list
      Observations: Observation list
      /// Provider capacity read through the planning port; empty when none
      /// was observed (unknown, not zero).
      Capacity: ProviderCapacity list
      Configuration: PlannerConfiguration
      /// Ended group executions (PRX-GRP-155); empty when none.
      GroupSamples: GroupedSample list }

[<RequireQualifiedAccess>]
type FindingCode =
    | StaleStateCandidate
    | StaleBlocker
    | DependencySatisfied
    | DependencyCycle
    | UnresolvedDependency
    | MonetaryOptimizationUnavailable
    | DurationEvidenceSparse
    | HistoricalDrift
    | MissingEvidence
    | UnknownCollision
    | PraxisStateCollision
    | PlanningConfidenceLimited
    | ResumableExecution
    | ProviderCapacityLimited
    | ProviderCapacityUnknown

[<RequireQualifiedAccess>]
module FindingCode =
    let all =
        [ FindingCode.StaleStateCandidate
          FindingCode.StaleBlocker
          FindingCode.DependencySatisfied
          FindingCode.DependencyCycle
          FindingCode.UnresolvedDependency
          FindingCode.MonetaryOptimizationUnavailable
          FindingCode.DurationEvidenceSparse
          FindingCode.HistoricalDrift
          FindingCode.MissingEvidence
          FindingCode.UnknownCollision
          FindingCode.PraxisStateCollision
          FindingCode.PlanningConfidenceLimited
          FindingCode.ResumableExecution
          FindingCode.ProviderCapacityLimited
          FindingCode.ProviderCapacityUnknown ]

    let code finding =
        match finding with
        | FindingCode.StaleStateCandidate -> "stale-state-candidate"
        | FindingCode.StaleBlocker -> "stale-blocker"
        | FindingCode.DependencySatisfied -> "dependency-satisfied"
        | FindingCode.DependencyCycle -> "dependency-cycle"
        | FindingCode.UnresolvedDependency -> "unresolved-dependency"
        | FindingCode.MonetaryOptimizationUnavailable -> "monetary-optimization-unavailable"
        | FindingCode.DurationEvidenceSparse -> "duration-evidence-sparse"
        | FindingCode.HistoricalDrift -> "historical-drift"
        | FindingCode.MissingEvidence -> "missing-evidence"
        | FindingCode.UnknownCollision -> "unknown-collision"
        | FindingCode.PraxisStateCollision -> "praxis-state-collision"
        | FindingCode.PlanningConfidenceLimited -> "planning-confidence-limited"
        | FindingCode.ResumableExecution -> "resumable-execution"
        | FindingCode.ProviderCapacityLimited -> "provider-capacity-limited"
        | FindingCode.ProviderCapacityUnknown -> "provider-capacity-unknown"

    let tryParse value = all |> List.tryFind (fun finding -> code finding = value)

[<RequireQualifiedAccess>]
type FindingSeverity =
    | Info
    | Advisory
    | Warning

[<RequireQualifiedAccess>]
module FindingSeverity =
    let code severity =
        match severity with
        | FindingSeverity.Info -> "info"
        | FindingSeverity.Advisory -> "advisory"
        | FindingSeverity.Warning -> "warning"

    let tryParse value =
        match value with
        | "info" -> Some FindingSeverity.Info
        | "advisory" -> Some FindingSeverity.Advisory
        | "warning" -> Some FindingSeverity.Warning
        | _ -> None

type PlanningFinding =
    { Code: FindingCode
      Severity: FindingSeverity
      WorkItems: string list
      Message: string
      RecommendedAction: string option
      Confidence: EvidenceConfidence
      Evidence: Provenance list }

/// PRX-PLAN-121: why an item appears where it does.
[<RequireQualifiedAccess>]
type ReasonCode =
    | PrerequisiteFor
    | NearlyComplete
    | CriticalPath
    | SafeParallelCandidate
    | ParallelRiskAccepted
    | ContextAffinity
    | CollisionPreventsParallelization
    | CapacityLimited
    | WaitingOnDependency
    | OldestRunnable
    | BalancedScore
    | ContinuationPreferred
    | ReconcileStaleState
    | ExternalDependency
    | HumanDependency
    | AwaitingEvidence
    | RecordedBlocker
    | NeedsTriage
    | DependencyCycle
    | InProgress
    | StrategyUnavailable
    | DependenciesSatisfied
    | ProviderFreeFirst
    | ProviderCapacityDeferred

[<RequireQualifiedAccess>]
module ReasonCode =
    let all =
        [ ReasonCode.PrerequisiteFor
          ReasonCode.NearlyComplete
          ReasonCode.CriticalPath
          ReasonCode.SafeParallelCandidate
          ReasonCode.ParallelRiskAccepted
          ReasonCode.ContextAffinity
          ReasonCode.CollisionPreventsParallelization
          ReasonCode.CapacityLimited
          ReasonCode.WaitingOnDependency
          ReasonCode.OldestRunnable
          ReasonCode.BalancedScore
          ReasonCode.ContinuationPreferred
          ReasonCode.ReconcileStaleState
          ReasonCode.ExternalDependency
          ReasonCode.HumanDependency
          ReasonCode.AwaitingEvidence
          ReasonCode.RecordedBlocker
          ReasonCode.NeedsTriage
          ReasonCode.DependencyCycle
          ReasonCode.InProgress
          ReasonCode.StrategyUnavailable
          ReasonCode.DependenciesSatisfied
          ReasonCode.ProviderFreeFirst
          ReasonCode.ProviderCapacityDeferred ]

    let code reason =
        match reason with
        | ReasonCode.PrerequisiteFor -> "prerequisite-for"
        | ReasonCode.NearlyComplete -> "nearly-complete"
        | ReasonCode.CriticalPath -> "critical-path"
        | ReasonCode.SafeParallelCandidate -> "safe-parallel-candidate"
        | ReasonCode.ParallelRiskAccepted -> "parallel-risk-accepted"
        | ReasonCode.ContextAffinity -> "context-affinity"
        | ReasonCode.CollisionPreventsParallelization -> "collision-prevents-parallelization"
        | ReasonCode.CapacityLimited -> "capacity-limited"
        | ReasonCode.WaitingOnDependency -> "waiting-on-dependency"
        | ReasonCode.OldestRunnable -> "oldest-runnable"
        | ReasonCode.BalancedScore -> "balanced-score"
        | ReasonCode.ContinuationPreferred -> "continuation-preferred"
        | ReasonCode.ReconcileStaleState -> "reconcile-stale-state"
        | ReasonCode.ExternalDependency -> "external-dependency"
        | ReasonCode.HumanDependency -> "human-dependency"
        | ReasonCode.AwaitingEvidence -> "awaiting-evidence"
        | ReasonCode.RecordedBlocker -> "recorded-blocker"
        | ReasonCode.NeedsTriage -> "needs-triage"
        | ReasonCode.DependencyCycle -> "dependency-cycle"
        | ReasonCode.InProgress -> "in-progress"
        | ReasonCode.StrategyUnavailable -> "strategy-unavailable"
        | ReasonCode.DependenciesSatisfied -> "dependencies-satisfied"
        | ReasonCode.ProviderFreeFirst -> "provider-free-first"
        | ReasonCode.ProviderCapacityDeferred -> "provider-capacity-deferred"

    let tryParse value = all |> List.tryFind (fun reason -> code reason = value)

type PlanningReason =
    { Code: ReasonCode
      Message: string
      References: string list }

[<RequireQualifiedAccess>]
module PlanningReason =
    let create code message references =
        { Code = code
          Message = message
          References = references }

/// How much of an item's effort remains, and on what basis (PRX-PLAN-030/031).
[<RequireQualifiedAccess>]
type RemainingBasis =
    | FromScratch
    | FinalizationOnly
    | VerificationRemaining
    | ImplementationInProgress
    | UnclassifiedContinuation

[<RequireQualifiedAccess>]
module RemainingBasis =
    let all =
        [ RemainingBasis.FromScratch
          RemainingBasis.FinalizationOnly
          RemainingBasis.VerificationRemaining
          RemainingBasis.ImplementationInProgress
          RemainingBasis.UnclassifiedContinuation ]

    let code basis =
        match basis with
        | RemainingBasis.FromScratch -> "from-scratch"
        | RemainingBasis.FinalizationOnly -> "finalization-only"
        | RemainingBasis.VerificationRemaining -> "verification-remaining"
        | RemainingBasis.ImplementationInProgress -> "implementation-in-progress"
        | RemainingBasis.UnclassifiedContinuation -> "unclassified-continuation"

    let tryParse value = all |> List.tryFind (fun basis -> code basis = value)

    let isContinuation basis = basis <> RemainingBasis.FromScratch

/// One work item as the planner sees it.
type ItemAnalysis =
    { Id: string
      Title: string
      LifecycleState: string
      InQueue: bool
      InContext: bool
      PlanningState: PlanningWorkState
      Tags: string list
      CreatedAt: string option
      TaskClass: string
      TaskClassOrigin: string
      Triage: TriageNeed list
      Dependencies: ResolvedDependency list
      BlockReason: string option
      Checkpoint: CheckpointSummary option
      Basis: RemainingBasis
      FullDuration: Estimate<int64>
      RemainingDuration: Estimate<int64>
      RemainingCost: Estimate<Money>
      CostEvidence: CostEvidenceKind
      Provenance: Provenance list }

[<RequireQualifiedAccess>]
type CollisionRisk =
    | Safe
    | Elevated
    | Unknown
    | Conflict

[<RequireQualifiedAccess>]
module CollisionRisk =
    let code risk =
        match risk with
        | CollisionRisk.Safe -> "safe"
        | CollisionRisk.Elevated -> "elevated"
        | CollisionRisk.Unknown -> "unknown"
        | CollisionRisk.Conflict -> "conflict"

    let tryParse value =
        match value with
        | "safe" -> Some CollisionRisk.Safe
        | "elevated" -> Some CollisionRisk.Elevated
        | "unknown" -> Some CollisionRisk.Unknown
        | "conflict" -> Some CollisionRisk.Conflict
        | _ -> None

    /// Unknown ranks above Elevated: not knowing is treated more
    /// conservatively than a known, bounded risk (PRX-PLAN-083).
    let rank risk =
        match risk with
        | CollisionRisk.Safe -> 0
        | CollisionRisk.Elevated -> 1
        | CollisionRisk.Unknown -> 2
        | CollisionRisk.Conflict -> 3

[<RequireQualifiedAccess>]
type CollisionSignal =
    | SameBranch of branch: string
    | SharedArea of area: string
    | SharedDeclaredPath of path: string
    | PraxisStateFiles
    | DeclaredConflict of reason: string
    | InsufficientScopeEvidence of workItem: string

[<RequireQualifiedAccess>]
module CollisionSignal =
    let praxisStateFiles =
        [ ".ros/context/current.json"; ".ros/work/queue.json"; ".ros/events/events.jsonl" ]

    let risk signal =
        match signal with
        | CollisionSignal.SameBranch _
        | CollisionSignal.SharedDeclaredPath _
        | CollisionSignal.DeclaredConflict _ -> CollisionRisk.Conflict
        | CollisionSignal.InsufficientScopeEvidence _ -> CollisionRisk.Unknown
        | CollisionSignal.SharedArea _
        | CollisionSignal.PraxisStateFiles -> CollisionRisk.Elevated

    let code signal =
        match signal with
        | CollisionSignal.SameBranch _ -> "same-branch"
        | CollisionSignal.SharedArea _ -> "shared-area"
        | CollisionSignal.SharedDeclaredPath _ -> "shared-declared-path"
        | CollisionSignal.PraxisStateFiles -> "praxis-state-files"
        | CollisionSignal.DeclaredConflict _ -> "declared-conflict"
        | CollisionSignal.InsufficientScopeEvidence _ -> "insufficient-scope-evidence"

    let detail signal =
        match signal with
        | CollisionSignal.SameBranch branch -> branch
        | CollisionSignal.SharedArea area -> area
        | CollisionSignal.SharedDeclaredPath path -> path
        | CollisionSignal.PraxisStateFiles -> String.Join(", ", praxisStateFiles)
        | CollisionSignal.DeclaredConflict reason -> reason
        | CollisionSignal.InsufficientScopeEvidence workItem -> workItem

    let tryParse (code: string) (detail: string) =
        match code with
        | "same-branch" -> Some(CollisionSignal.SameBranch detail)
        | "shared-area" -> Some(CollisionSignal.SharedArea detail)
        | "shared-declared-path" -> Some(CollisionSignal.SharedDeclaredPath detail)
        | "praxis-state-files" -> Some CollisionSignal.PraxisStateFiles
        | "declared-conflict" -> Some(CollisionSignal.DeclaredConflict detail)
        | "insufficient-scope-evidence" -> Some(CollisionSignal.InsufficientScopeEvidence detail)
        | _ -> None

    let describe signal =
        match signal with
        | CollisionSignal.SameBranch branch -> $"both work on branch {branch}"
        | CollisionSignal.SharedArea area -> $"both touch area '{area}'"
        | CollisionSignal.SharedDeclaredPath path -> $"both declare path {path}"
        | CollisionSignal.PraxisStateFiles -> "both mutate shared Praxis state files (PRAXIS-STATE-MERGE-01)"
        | CollisionSignal.DeclaredConflict reason -> $"declared conflict: {reason}"
        | CollisionSignal.InsufficientScopeEvidence workItem -> $"{workItem} has no scope evidence, so overlap is unknown"

type Collision =
    { Left: string
      Right: string
      Risk: CollisionRisk
      Signals: CollisionSignal list }

type DurationDistribution =
    { TaskClass: string
      SampleCount: int
      /// Samples whose productive time includes runtime-measured session
      /// time (`time.active_ms`), not only ROS execution wall time.
      SessionMeasured: int
      Lower: int64
      Median: int64
      Upper: int64
      Confidence: EvidenceConfidence }

type CostEvidenceSummary =
    { SampledExecutions: int
      WithUsableCost: int
      WithTokenUsage: int
      Currency: string option
      Sufficient: bool
      Statement: string }

/// Measured per-session context overhead (PRAXIS-PLAN-05, PRX-GRP-061):
/// what one cold start costs, from the session metrics executions recorded.
type ContextOverheadSummary =
    { SampledSessions: int
      /// Time to first code change, interquartile range; unknown unless
      /// `Sufficient`.
      ColdStart: Estimate<int64>
      MedianGovernanceReads: int option
      MedianRepeatedReads: int option
      Sufficient: bool
      Statement: string }

type HistorySegment =
    { Dimension: string
      Value: string
      SampleCount: int
      MedianMs: int64 option }

type DriftAssessment =
    { RecentSampleCount: int
      RecentMedianMs: int64
      PriorLowerMs: int64
      PriorUpperMs: int64
      Drifted: bool
      Statement: string }

type HistorySummary =
    { SampledExecutions: int
      FinalizedExecutions: int
      ActiveExecutionsExcluded: int
      DurationSamples: int
      Distributions: DurationDistribution list
      Cost: CostEvidenceSummary
      ContextOverhead: ContextOverheadSummary
      Segments: HistorySegment list
      Drift: DriftAssessment option }

type UnlockValue =
    { WorkItem: string
      DirectlyUnlocks: string list
      TransitiveDependents: string list
      Explanation: string }

type CriticalPath =
    { WorkItems: string list
      ExpectedDuration: Estimate<int64>
      Explanation: string }

type ItemDigest =
    { WorkItem: string
      LifecycleState: string
      PlanningState: PlanningWorkState
      CheckpointId: string option
      Digest: string }

/// PRX-PLAN-140: the state a plan was computed against.
type PlanSnapshot =
    { Repository: string
      Commit: string option
      Branch: string option
      PlannedAt: string
      PlannerVersion: string
      WorkStateFingerprint: string
      InputFingerprint: string
      CollisionFingerprint: string
      Items: ItemDigest list }

type PlanningAnalysis =
    { Snapshot: PlanSnapshot
      Items: ItemAnalysis list
      Findings: PlanningFinding list
      Collisions: Collision list
      CriticalPath: CriticalPath
      Unlocks: UnlockValue list
      History: HistorySummary
      Confidence: EvidenceConfidence
      ConfidenceStatement: string
      EvidenceRecommendations: string list
      Capacity: CapacityAssessment }

/// PRX-PLAN-090..094.
[<RequireQualifiedAccess>]
type OptimizationObjective =
    | Baseline
    | MinimumCost
    | MinimumDuration
    | Balanced
    | MaximumSafeParallelism
    | BudgetConstrained of budget: Money
    | DeadlineConstrained of deadlineMs: int64

[<RequireQualifiedAccess>]
module OptimizationObjective =
    let code objective =
        match objective with
        | OptimizationObjective.Baseline -> "baseline"
        | OptimizationObjective.MinimumCost -> "cost"
        | OptimizationObjective.MinimumDuration -> "speed"
        | OptimizationObjective.Balanced -> "balanced"
        | OptimizationObjective.MaximumSafeParallelism -> "max-parallel"
        | OptimizationObjective.BudgetConstrained _ -> "budget"
        | OptimizationObjective.DeadlineConstrained _ -> "deadline"

[<RequireQualifiedAccess>]
type StrategyAvailability =
    | Available
    | LowConfidence of reason: string
    | Unavailable of reason: string

[<RequireQualifiedAccess>]
module StrategyAvailability =
    let code availability =
        match availability with
        | StrategyAvailability.Available -> "available"
        | StrategyAvailability.LowConfidence _ -> "low-confidence"
        | StrategyAvailability.Unavailable _ -> "unavailable"

    let reason availability =
        match availability with
        | StrategyAvailability.Available -> None
        | StrategyAvailability.LowConfidence reason
        | StrategyAvailability.Unavailable reason -> Some reason

    let tryParse (code: string) (reason: string option) =
        match code, reason with
        | "available", _ -> Some StrategyAvailability.Available
        | "low-confidence", Some reason -> Some(StrategyAvailability.LowConfidence reason)
        | "unavailable", Some reason -> Some(StrategyAvailability.Unavailable reason)
        | _ -> None

[<RequireQualifiedAccess>]
type WaveKind =
    | Reconciliation
    | Execution

[<RequireQualifiedAccess>]
type RecommendedAction =
    | Reconcile
    | Complete
    | Continue
    | Start

[<RequireQualifiedAccess>]
module RecommendedAction =
    let code action =
        match action with
        | RecommendedAction.Reconcile -> "reconcile"
        | RecommendedAction.Complete -> "complete"
        | RecommendedAction.Continue -> "continue"
        | RecommendedAction.Start -> "start"

    let tryParse value =
        match value with
        | "reconcile" -> Some RecommendedAction.Reconcile
        | "complete" -> Some RecommendedAction.Complete
        | "continue" -> Some RecommendedAction.Continue
        | "start" -> Some RecommendedAction.Start
        | _ -> None

type WaveEntry =
    { WorkItem: string
      Action: RecommendedAction
      Remaining: Estimate<int64>
      Reasons: PlanningReason list }

type PlanWave =
    { Number: int
      Kind: WaveKind
      Entries: WaveEntry list
      ExpectedDuration: Estimate<int64>
      Explanation: PlanningReason list }

type ExcludedItem =
    { WorkItem: string
      State: PlanningWorkState
      Reasons: PlanningReason list }

/// PRX-PLAN-052: cost contributors that are known even when money is not.
type ProxyCost =
    { Executions: int
      Continuations: int
      ContextAcquisitions: int
      PeakConcurrency: int
      RiskAcceptedPairs: int }

[<RequireQualifiedAccess>]
type ConstraintVerdict =
    | Satisfiable
    | NotSatisfiable
    | CannotEvaluate

[<RequireQualifiedAccess>]
module ConstraintVerdict =
    let code verdict =
        match verdict with
        | ConstraintVerdict.Satisfiable -> "satisfiable"
        | ConstraintVerdict.NotSatisfiable -> "not-satisfiable"
        | ConstraintVerdict.CannotEvaluate -> "cannot-evaluate"

    let tryParse value =
        match value with
        | "satisfiable" -> Some ConstraintVerdict.Satisfiable
        | "not-satisfiable" -> Some ConstraintVerdict.NotSatisfiable
        | "cannot-evaluate" -> Some ConstraintVerdict.CannotEvaluate
        | _ -> None

type ConstraintAssessment =
    { Constraint: string
      Verdict: ConstraintVerdict
      Reason: string }

type ContextRecommendation =
    { WorkItems: string list
      Reason: string }

type ExecutionPlan =
    { Objective: OptimizationObjective
      Availability: StrategyAvailability
      MaxConcurrency: int option
      RiskPolicy: string
      Weights: BalancedWeights option
      Waves: PlanWave list
      Blocked: ExcludedItem list
      NeedsTriage: ExcludedItem list
      StaleStateCandidates: ExcludedItem list
      ExpectedDuration: Estimate<int64>
      ExpectedCost: Estimate<Money>
      Proxy: ProxyCost
      Constraint: ConstraintAssessment option
      ContextRecommendations: ContextRecommendation list
      Confidence: EvidenceConfidence }

module internal Text =
    let ordinal (left: string) (right: string) = String.CompareOrdinal(left, right)

    let sortOrdinal (values: string list) = values |> List.sortWith ordinal

    let distinctOrdinal (values: string list) = values |> List.distinct |> sortOrdinal

    let tryTimestamp (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, parsed -> Some parsed
        | _ -> None
