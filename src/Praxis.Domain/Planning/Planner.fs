namespace Praxis.Domain.Planning

open System

type StrategyPlacement =
    { Objective: string
      Section: string
      Wave: int option
      Action: RecommendedAction option
      Reasons: PlanningReason list }

type ItemExplanation =
    { WorkItem: string
      Item: ItemAnalysis
      Placements: StrategyPlacement list
      Collisions: Collision list
      Affinity: (string * string list) list
      Unlock: UnlockValue option
      OnCriticalPath: bool
      Findings: PlanningFinding list
      EstimateBasis: string list }

/// The planner's entry points. Pure functions of `PlanningInput`: identical
/// input always yields identical output (PRX-PLAN-002).
[<RequireQualifiedAccess>]
module Planner =
    let schemaVersion = "praxis.plan/1.0.0"

    let advisoryStatement =
        "Advisory shadow plan: nothing was started, completed, reprioritized or otherwise changed. " + Comparison.noUniversalOptimum

    let private finding code severity items message action confidence evidence =
        { Code = code
          Severity = severity
          WorkItems = items
          Message = message
          RecommendedAction = action
          Confidence = confidence
          Evidence = evidence }

    let private severityRank severity =
        match severity with
        | FindingSeverity.Warning -> 0
        | FindingSeverity.Advisory -> 1
        | FindingSeverity.Info -> 2

    let analyze (input: PlanningInput) : PlanningAnalysis =
        let configuration = input.Configuration
        let history = History.summarize configuration input.Executions
        let items, itemFindings = Inventory.build input history
        let collisions = Graph.collisions configuration items
        let cycles = Graph.cycles items
        let unlocks = Graph.unlocks items
        let critical = Graph.criticalPath items
        let schedulable = items |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState)
        let stale = items |> List.filter (fun item -> item.PlanningState = PlanningWorkState.StaleStateCandidate)
        let pooled = history.Distributions |> List.tryFind (fun distribution -> distribution.TaskClass = History.pooledClass)
        let unknownPairs = collisions |> List.filter (fun collision -> collision.Risk = CollisionRisk.Unknown)

        let unscoped =
            collisions
            |> List.collect (fun collision ->
                collision.Signals
                |> List.choose (function
                    | CollisionSignal.InsufficientScopeEvidence id -> Some id
                    | _ -> None))
            |> Text.distinctOrdinal

        let inferredOnly =
            items
            |> List.filter (fun item ->
                not (PlanningWorkState.isTerminal item.PlanningState)
                && not item.Dependencies.IsEmpty
                && item.Dependencies |> List.forall (fun resolved -> resolved.Dependency.Origin = DependencyOrigin.Inferred))
            |> List.map (fun item -> item.Id)

        let staleIds = stale |> List.map (fun item -> item.Id)

        let globalFindings =
            [ for cycle in cycles do
                  let chain = String.concat " -> " (cycle @ [ List.head cycle ])

                  yield
                      finding
                          FindingCode.DependencyCycle
                          FindingSeverity.Warning
                          cycle
                          $"dependency cycle {chain}: none of these items can be scheduled until the cycle is broken"
                          (Some "remove or correct one dependency in the cycle")
                          EvidenceConfidence.High
                          (cycle |> List.map (fun id -> Provenance.create EvidenceSource.InferredDependency id))
              if not history.Cost.Sufficient then
                  yield
                      finding
                          FindingCode.MonetaryOptimizationUnavailable
                          FindingSeverity.Advisory
                          []
                          history.Cost.Statement
                          (Some "record cost.execution_total (platform-reported cost with --quality observed, or token usage with pricing) for future executions")
                          EvidenceConfidence.High
                          [ Provenance.create EvidenceSource.Telemetry ".ros/telemetry/executions" ]
              match pooled with
              | Some distribution when distribution.Confidence <> EvidenceConfidence.Low && distribution.Confidence <> EvidenceConfidence.Unknown -> ()
              | Some distribution ->
                  yield
                      finding
                          FindingCode.DurationEvidenceSparse
                          FindingSeverity.Advisory
                          []
                          $"only {distribution.SampleCount} finalized executions carry duration evidence; duration estimates are {EvidenceConfidence.code distribution.Confidence} confidence"
                          (Some "finalize executions so their wall and blocked time are recorded")
                          distribution.Confidence
                          [ Provenance.create EvidenceSource.Telemetry ".ros/telemetry/executions" ]
              | None ->
                  yield
                      finding
                          FindingCode.DurationEvidenceSparse
                          FindingSeverity.Advisory
                          []
                          "no finalized execution carries duration evidence; every duration is unknown"
                          (Some "finalize executions so their wall and blocked time are recorded")
                          EvidenceConfidence.Unknown
                          []
              match history.Drift with
              | Some drift when drift.Drifted ->
                  yield finding FindingCode.HistoricalDrift FindingSeverity.Advisory [] drift.Statement (Some "weight recent executions more, or re-segment by task class") EvidenceConfidence.Medium []
              | _ -> ()
              if not unknownPairs.IsEmpty then
                  let names = String.concat ", " unscoped

                  yield
                      finding
                          FindingCode.UnknownCollision
                          FindingSeverity.Advisory
                          unscoped
                          $"{unknownPairs.Length} pair(s) of runnable items have unknown overlap and are never co-scheduled; items without scope evidence: {names}"
                          (Some "tag these items or declare their areas in the planner configuration")
                          EvidenceConfidence.High
                          []
              if not configuration.PraxisStateMergeSafe && schedulable.Length >= 2 then
                  let files = String.concat ", " CollisionSignal.praxisStateFiles

                  yield
                      finding
                          FindingCode.PraxisStateCollision
                          FindingSeverity.Advisory
                          []
                          $"every lifecycle mutation touches {files}; until PRAXIS-STATE-MERGE-01 removes that risk, every parallel pair carries elevated collision risk"
                          (Some "serialize Praxis state commits, or resolve PRAXIS-STATE-MERGE-01")
                          EvidenceConfidence.High
                          (CollisionSignal.praxisStateFiles |> List.map (Provenance.create EvidenceSource.PlannerAssumption))
              if not stale.IsEmpty then
                  let names = String.concat ", " staleIds

                  yield
                      finding
                          FindingCode.PlanningConfidenceLimited
                          FindingSeverity.Warning
                          staleIds
                          $"Planning confidence is limited because {stale.Length} work item(s) appear stale ({names}). Resolve those states before optimizing implementation order."
                          (Some "run the reconciliation wave first")
                          EvidenceConfidence.High
                          [] ]

        let findings =
            itemFindings @ globalFindings
            |> List.sortWith (fun left right ->
                compare (severityRank left.Severity) (severityRank right.Severity)
                |> fun order -> if order <> 0 then order else compare (FindingCode.all |> List.findIndex ((=) left.Code)) (FindingCode.all |> List.findIndex ((=) right.Code))
                |> fun order -> if order <> 0 then order else Text.ordinal (String.concat "," left.WorkItems) (String.concat "," right.WorkItems)
                |> fun order -> if order <> 0 then order else Text.ordinal left.Message right.Message)

        let limits =
            [ if stale.Length >= 3 then yield EvidenceConfidence.Low, $"{stale.Length} stale-state candidates"
              elif not stale.IsEmpty then yield EvidenceConfidence.Medium, $"{stale.Length} stale-state candidate(s)"
              match pooled with
              | Some distribution -> yield distribution.Confidence, $"duration evidence is {EvidenceConfidence.code distribution.Confidence} confidence ({distribution.SampleCount} samples)"
              | None -> yield EvidenceConfidence.Low, "no duration evidence"
              if not history.Cost.Sufficient then yield EvidenceConfidence.Medium, "no usable monetary cost evidence"
              if not unknownPairs.IsEmpty then yield EvidenceConfidence.Medium, $"{unknownPairs.Length} runnable pair(s) with unknown overlap"
              if not cycles.IsEmpty then yield EvidenceConfidence.Low, $"{cycles.Length} dependency cycle(s)" ]

        let confidence = limits |> List.map fst |> List.fold EvidenceConfidence.min EvidenceConfidence.High

        let statement =
            match limits |> List.filter (fun (level, _) -> EvidenceConfidence.rank level < EvidenceConfidence.rank EvidenceConfidence.High) with
            | [] -> "planning inputs are complete"
            | reasons -> "limited by: " + (reasons |> List.map snd |> String.concat "; ")

        let recommendations =
            [ if not stale.IsEmpty then
                  let names = String.concat ", " staleIds
                  yield $"Reconcile recorded state for {names}; stale state changes the runnable graph more than any estimate."
              if not history.Cost.Sufficient then
                  yield $"Collect cost.execution_total for future executions ({history.Cost.WithUsableCost} of {history.Cost.SampledExecutions} carry usable cost evidence); it enables the minimum-cost strategy."
              if not history.ContextOverhead.Sufficient then
                  yield $"Ingest session transcripts with the anthropic-claude-session adapter ({history.ContextOverhead.SampledSessions} of at least {History.minimumContextSessions} sessions measured); measured cold starts replace unknown context-reuse value and runtime-measured session time improves implementation duration estimates."
              if not unscoped.IsEmpty then
                  let names = String.concat ", " unscoped
                  yield $"Tag or declare areas for {names} so collision risk can be assessed instead of treated as unknown."
              if not inferredOnly.IsEmpty then
                  let names = String.concat ", " inferredOnly
                  yield $"Record structured dependencies (dependsOn) for {names}; theirs are inferred from free text."
              if items |> List.exists (fun item -> item.Dependencies |> List.exists (fun resolved -> resolved.Dependency.Kind = DependencyKind.Evidence && resolved.Status = DependencyStatus.Undetermined)) then
                  yield "Supply CI observations (--observations FILE) so evidence dependencies on CI can be resolved."
              if not configuration.PraxisStateMergeSafe then
                  yield "Resolve PRAXIS-STATE-MERGE-01 to remove elevated collision risk from every parallel pair." ]

        { Snapshot = Snapshot.create input items collisions
          Items = items
          Findings = findings
          Collisions = collisions
          CriticalPath = critical
          Unlocks = unlocks
          History = history
          Confidence = confidence
          ConfidenceStatement = statement
          EvidenceRecommendations = recommendations }

    let plan (analysis: PlanningAnalysis) (configuration: PlannerConfiguration) (objective: OptimizationObjective) (maxConcurrency: int option) : PlanDocument =
        let executionPlan = Scheduling.simulate analysis configuration objective maxConcurrency

        { SchemaVersion = schemaVersion
          Snapshot = analysis.Snapshot
          Plan = executionPlan
          Findings = analysis.Findings
          Statement = advisoryStatement }

    let comparedObjectives =
        [ OptimizationObjective.Baseline
          OptimizationObjective.MinimumDuration
          OptimizationObjective.Balanced
          OptimizationObjective.MinimumCost
          OptimizationObjective.MaximumSafeParallelism ]

    let compare (analysis: PlanningAnalysis) (configuration: PlannerConfiguration) (maxConcurrency: int option) : StrategyComparison =
        let plans = comparedObjectives |> List.map (fun objective -> Scheduling.simulate analysis configuration objective maxConcurrency)
        let summaries = plans |> List.map Comparison.summarize |> Comparison.frontier

        let runnable = analysis.Items |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState) |> List.length
        let limit = maxConcurrency |> Option.defaultValue 8 |> min (max 1 runnable) |> max 1

        let curve, diminishing =
            [ 1..limit ]
            |> List.map (fun concurrency -> concurrency, Scheduling.simulate analysis configuration OptimizationObjective.MinimumDuration (Some concurrency))
            |> Comparison.concurrencyCurve

        { Strategies = summaries
          Frontier = summaries |> List.filter (fun summary -> summary.OnFrontier) |> List.map (fun summary -> summary.Objective)
          ConcurrencyCurve = curve
          DiminishingReturns = diminishing
          Statement = Comparison.noUniversalOptimum }

    let private placement (plan: ExecutionPlan) (id: string) : StrategyPlacement =
        let objective = OptimizationObjective.code plan.Objective

        let inWave =
            plan.Waves
            |> List.tryPick (fun wave -> wave.Entries |> List.tryFind (fun entry -> entry.WorkItem = id) |> Option.map (fun entry -> wave, entry))

        let section (name: string) (items: ExcludedItem list) =
            items |> List.tryFind (fun item -> item.WorkItem = id) |> Option.map (fun item -> name, item.Reasons)

        match inWave with
        | Some(wave, entry) ->
            { Objective = objective
              Section = (if wave.Kind = WaveKind.Reconciliation then "reconciliation-wave" else "wave")
              Wave = Some wave.Number
              Action = Some entry.Action
              Reasons = entry.Reasons }
        | None ->
            let found =
                [ section "blocked" plan.Blocked; section "needs-triage" plan.NeedsTriage; section "stale-state" plan.StaleStateCandidates ]
                |> List.choose Operators.id
                |> List.tryHead

            match found with
            | Some(name, reasons) ->
                { Objective = objective
                  Section = name
                  Wave = None
                  Action = None
                  Reasons = reasons }
            | None ->
                let reasons =
                    match plan.Availability with
                    | StrategyAvailability.Unavailable reason -> [ PlanningReason.create ReasonCode.StrategyUnavailable $"strategy unavailable: {reason}" [] ]
                    | _ -> []

                { Objective = objective
                  Section = "not-scheduled"
                  Wave = None
                  Action = None
                  Reasons = reasons }

    /// PRX-PLAN-130: why an item is or is not scheduled, under every strategy.
    let explain (analysis: PlanningAnalysis) (configuration: PlannerConfiguration) (maxConcurrency: int option) (id: string) : Result<ItemExplanation, string> =
        match analysis.Items |> List.tryFind (fun item -> item.Id = id) with
        | None -> Error $"work item '{id}' is not in the planning inventory (backlog queue or live context)"
        | Some item ->
            let plans = comparedObjectives |> List.map (fun objective -> Scheduling.simulate analysis configuration objective maxConcurrency)
            let _, basisText = History.estimateFor analysis.History.Distributions item.TaskClass
            let lower, upper =
                match item.Basis with
                | RemainingBasis.FromScratch -> 1.0m, 1.0m
                | RemainingBasis.FinalizationOnly -> configuration.RemainingFractions.FinalizationOnly
                | RemainingBasis.VerificationRemaining -> configuration.RemainingFractions.VerificationRemaining
                | RemainingBasis.ImplementationInProgress -> configuration.RemainingFractions.ImplementationInProgress
                | RemainingBasis.UnclassifiedContinuation -> configuration.RemainingFractions.Unclassified

            let percent (value: decimal) = Math.Round(value * 100m).ToString(Globalization.CultureInfo.InvariantCulture)
            let range = $"{percent lower}%%-{percent upper}%%"
            let others = analysis.Items |> List.filter (fun other -> other.Id <> id && PlanningWorkState.isSchedulable other.PlanningState)

            Ok
                { WorkItem = id
                  Item = item
                  Placements = plans |> List.map (fun plan -> placement plan id)
                  Collisions =
                    analysis.Collisions
                    |> List.filter (fun collision -> (collision.Left = id || collision.Right = id) && collision.Risk <> CollisionRisk.Safe)
                  Affinity = others |> List.choose (fun other -> match Graph.affinity configuration item other with | [] -> None | reasons -> Some(other.Id, reasons))
                  Unlock = analysis.Unlocks |> List.tryFind (fun unlock -> unlock.WorkItem = id)
                  OnCriticalPath = analysis.CriticalPath.WorkItems |> List.contains id
                  Findings = analysis.Findings |> List.filter (fun finding -> finding.WorkItems |> List.contains id)
                  EstimateBasis =
                    [ $"task class '{item.TaskClass}' (from {item.TaskClassOrigin})"
                      $"full-effort duration from {basisText}"
                      $"remaining basis {RemainingBasis.code item.Basis}: {range} of full effort (planner assumption, reported)"
                      $"remaining-duration confidence {EvidenceConfidence.code item.RemainingDuration.Confidence}"
                      $"cost evidence {CostEvidenceKind.code item.CostEvidence}: {analysis.History.Cost.Statement}"
                      "elapsed time of an active execution is never counted as productive time" ] }
