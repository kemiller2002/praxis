namespace Ros.Domain.Planning

open System
open System.Globalization

/// A plan together with the state it was computed against (PRX-PLAN-140).
type PlanDocument =
    { SchemaVersion: string
      Snapshot: PlanSnapshot
      Plan: ExecutionPlan
      Findings: PlanningFinding list
      Statement: string }

/// Deterministic wave scheduling for every strategy (PRX-PLAN-090..094,
/// 120..124). Scheduling only recommends: it never starts, changes or
/// reorders recorded work (PRX-PLAN-001).
[<RequireQualifiedAccess>]
module Scheduling =
    type RiskPolicy =
        { Name: string
          Description: string
          Admits: Collision -> bool }

    let private isStateOnly (collision: Collision) =
        not collision.Signals.IsEmpty && collision.Signals |> List.forall ((=) CollisionSignal.PraxisStateFiles)

    let serial =
        { Name = "serial"
          Description = "one item at a time; no parallel collision risk is taken"
          Admits = fun _ -> false }

    let praxisStateOnly =
        { Name = "praxis-state-only"
          Description = "co-schedules a pair only when it is safe or its sole collision signal is the shared Praxis state files; never unknown-overlap, same-area or conflicting pairs"
          Admits = fun collision -> collision.Risk = CollisionRisk.Safe || (collision.Risk = CollisionRisk.Elevated && isStateOnly collision) }

    let acceptElevated =
        { Name = "accept-elevated"
          Description = "co-schedules safe and elevated-risk pairs and reports each accepted risk; never unknown-overlap or conflicting pairs"
          Admits = fun collision -> collision.Risk = CollisionRisk.Safe || collision.Risk = CollisionRisk.Elevated }

    type private Context =
        { Analysis: PlanningAnalysis
          Configuration: PlannerConfiguration
          ById: Map<string, ItemAnalysis>
          Collisions: Map<string * string, Collision>
          Unlocks: Map<string, UnlockValue>
          Critical: Set<string> }

    let private context (analysis: PlanningAnalysis) (configuration: PlannerConfiguration) =
        { Analysis = analysis
          Configuration = configuration
          ById = analysis.Items |> List.map (fun item -> item.Id, item) |> Map.ofList
          Collisions = analysis.Collisions |> List.map (fun collision -> (collision.Left, collision.Right), collision) |> Map.ofList
          Unlocks = analysis.Unlocks |> List.map (fun unlock -> unlock.WorkItem, unlock) |> Map.ofList
          Critical = Set.ofList analysis.CriticalPath.WorkItems }

    let private collisionOf (context: Context) (left: string) (right: string) =
        let key = if Text.ordinal left right <= 0 then left, right else right, left

        context.Collisions.TryFind key
        |> Option.defaultValue
            { Left = fst key
              Right = snd key
              Risk = CollisionRisk.Unknown
              Signals = [ CollisionSignal.InsufficientScopeEvidence(fst key) ] }

    let private signalsText (collision: Collision) =
        collision.Signals |> List.map CollisionSignal.describe |> String.concat "; "

    let private invariant (value: decimal) = value.ToString("0.00", CultureInfo.InvariantCulture)

    let private minutes (value: int64 option) =
        value |> Option.map (fun ms -> $"{ms / 60_000L} min") |> Option.defaultValue "unknown"

    // ---- ordering ------------------------------------------------------------

    let private fifoKey (item: ItemAnalysis) =
        item.CreatedAt |> Option.bind Text.tryTimestamp |> Option.defaultValue DateTimeOffset.MaxValue, item.Id

    let private compareFifo (left: ItemAnalysis) (right: ItemAnalysis) =
        let leftTime, leftId = fifoKey left
        let rightTime, rightId = fifoKey right

        match compare leftTime rightTime with
        | 0 -> Text.ordinal leftId rightId
        | order -> order

    let private thenBy (next: int) (order: int) = if order <> 0 then order else next

    let private transitiveCount (context: Context) (id: string) =
        context.Unlocks.TryFind id |> Option.map (fun unlock -> unlock.TransitiveDependents.Length) |> Option.defaultValue 0

    let private compareSpeed (context: Context) (left: ItemAnalysis) (right: ItemAnalysis) =
        let critical (item: ItemAnalysis) = if context.Critical.Contains item.Id then 0 else 1
        let continuation (item: ItemAnalysis) = if RemainingBasis.isContinuation item.Basis then 0 else 1
        let expected (item: ItemAnalysis) = item.RemainingDuration.Expected |> Option.defaultValue Int64.MaxValue

        compare (critical left) (critical right)
        |> thenBy (compare (transitiveCount context right.Id) (transitiveCount context left.Id))
        |> thenBy (compare (continuation left) (continuation right))
        |> thenBy (compare (expected left) (expected right))
        |> thenBy (compareFifo left right)

    let private compareCost (left: ItemAnalysis) (right: ItemAnalysis) =
        let cost (item: ItemAnalysis) = item.RemainingCost.Expected |> Option.map (fun money -> money.Amount) |> Option.defaultValue Decimal.MaxValue
        compare (cost left) (cost right) |> thenBy (compareFifo left right)

    let private uncertainty confidence =
        match confidence with
        | EvidenceConfidence.High -> 0.00m
        | EvidenceConfidence.Medium -> 0.33m
        | EvidenceConfidence.Low -> 0.67m
        | EvidenceConfidence.Unknown -> 1.00m

    let private completionLikelihood (fractions: RemainingFractions) basis =
        let low, high =
            match basis with
            | RemainingBasis.FromScratch -> 1.0m, 1.0m
            | RemainingBasis.FinalizationOnly -> fractions.FinalizationOnly
            | RemainingBasis.VerificationRemaining -> fractions.VerificationRemaining
            | RemainingBasis.ImplementationInProgress -> fractions.ImplementationInProgress
            | RemainingBasis.UnclassifiedContinuation -> fractions.Unclassified

        1.0m - (low + high) / 2.0m

    /// PRX-PLAN-093: an explicit, component-by-component score (lower first).
    let private balancedScores (context: Context) (candidates: ItemAnalysis list) : Map<string, decimal * string> =
        let weights = context.Configuration.BalancedWeights
        let known = candidates |> List.choose (fun item -> item.RemainingDuration.Expected)
        let longest = if known.IsEmpty then 1L else max 1L (List.max known)
        let costAvailable = context.Analysis.History.Cost.Sufficient
        let costs = candidates |> List.choose (fun item -> item.RemainingCost.Expected |> Option.map (fun money -> money.Amount))
        let dearest = if costs.IsEmpty then 1.0m else max 0.0001m (List.max costs)
        let others = max 1 (candidates.Length - 1)

        candidates
        |> List.map (fun item ->
            let duration =
                item.RemainingDuration.Expected |> Option.map (fun ms -> decimal ms / decimal longest) |> Option.defaultValue 1.0m

            let cost =
                if costAvailable then item.RemainingCost.Expected |> Option.map (fun money -> money.Amount / dearest) |> Option.defaultValue 1.0m
                else 0.0m

            let risk =
                candidates
                |> List.filter (fun other -> other.Id <> item.Id && not (praxisStateOnly.Admits(collisionOf context item.Id other.Id)))
                |> List.length
                |> fun count -> decimal count / decimal others

            let unsure = uncertainty item.RemainingDuration.Confidence

            let reuse =
                if candidates |> List.exists (fun other -> other.Id <> item.Id && not (Graph.affinity context.Configuration item other).IsEmpty) then 1.0m
                else 0.0m

            let likely = completionLikelihood context.Configuration.RemainingFractions item.Basis

            let score =
                weights.Duration * duration
                + (if costAvailable then weights.Cost * cost else 0.0m)
                + weights.ConflictRisk * risk
                + weights.Uncertainty * unsure
                - weights.ContextReuse * reuse
                - weights.CompletionLikelihood * likely
                |> fun value -> Math.Round(value, 4)

            let costPart =
                if costAvailable then $"cost {invariant weights.Cost}x{invariant cost}"
                else $"cost weight {invariant weights.Cost} not applied (cost unknown)"

            let message =
                $"balanced score {invariant score} (lower runs first) = duration {invariant weights.Duration}x{invariant duration} + {costPart} + conflict risk {invariant weights.ConflictRisk}x{invariant risk} + uncertainty {invariant weights.Uncertainty}x{invariant unsure} - context reuse {invariant weights.ContextReuse}x{invariant reuse} - completion likelihood {invariant weights.CompletionLikelihood}x{invariant likely}"

            item.Id, (score, message))
        |> Map.ofList

    // ---- partitioning ---------------------------------------------------------

    let private gatingDependencies (item: ItemAnalysis) =
        item.Dependencies
        |> List.filter (fun resolved ->
            resolved.Status <> DependencyStatus.Satisfied
            && resolved.Dependency.Provenance.Source <> EvidenceSource.Checkpoint
            && (resolved.Dependency.Kind = DependencyKind.External
                || resolved.Dependency.Kind = DependencyKind.Human
                || resolved.Dependency.Kind = DependencyKind.Evidence))

    let private dependencyReason (resolved: ResolvedDependency) =
        let code =
            match resolved.Dependency.Kind with
            | DependencyKind.Human -> ReasonCode.HumanDependency
            | DependencyKind.Evidence -> ReasonCode.AwaitingEvidence
            | _ -> ReasonCode.ExternalDependency

        PlanningReason.create
            code
            $"waiting on {DependencyTarget.describe resolved.Dependency.Target} ({DependencyOrigin.code resolved.Dependency.Origin}, {resolved.Reason})"
            [ resolved.Dependency.Provenance.Reference ]

    let private findingsFor (context: Context) (id: string) (codes: FindingCode list) =
        context.Analysis.Findings |> List.filter (fun finding -> List.contains finding.Code codes && List.tryHead finding.WorkItems = Some id)

    let private excluded (item: ItemAnalysis) reasons =
        { WorkItem = item.Id
          State = item.PlanningState
          Reasons = reasons }

    let private triageSection (context: Context) =
        context.Analysis.Items
        |> List.filter (fun item -> item.PlanningState = PlanningWorkState.Captured)
        |> List.map (fun item ->
            let needs = item.Triage |> List.map TriageNeed.code |> String.concat ", "
            excluded item [ PlanningReason.create ReasonCode.NeedsTriage $"captured work is not runnable until triaged ({needs})" [] ])

    let private staleSection (context: Context) =
        context.Analysis.Items
        |> List.filter (fun item -> item.PlanningState = PlanningWorkState.StaleStateCandidate)
        |> List.map (fun item ->
            findingsFor context item.Id [ FindingCode.StaleStateCandidate; FindingCode.StaleBlocker ]
            |> List.map (fun finding ->
                let action = finding.RecommendedAction |> Option.map (fun action -> $" Recommended: {action}.") |> Option.defaultValue ""
                PlanningReason.create ReasonCode.ReconcileStaleState (finding.Message + action) (finding.Evidence |> List.map (fun evidence -> evidence.Reference)))
            |> excluded item)

    let private recordedBlockedSection (context: Context) =
        context.Analysis.Items
        |> List.filter (fun item ->
            item.PlanningState = PlanningWorkState.Blocked
            || item.PlanningState = PlanningWorkState.AwaitingHuman
            || item.PlanningState = PlanningWorkState.AwaitingEvidence)
        |> List.map (fun item ->
            let recorded = item.BlockReason |> Option.defaultValue (item.LifecycleState + " (no reason recorded)")

            [ yield
                  PlanningReason.create
                      ReasonCode.RecordedBlocker
                      $"recorded blocker: {recorded}"
                      [ $".ros/context/current.json#{item.Id}.blockReason" ]
              yield! item.Dependencies |> List.filter (fun resolved -> resolved.Status <> DependencyStatus.Satisfied && resolved.Dependency.Kind <> DependencyKind.Hard) |> List.map dependencyReason ]
            |> excluded item)

    // ---- waves -----------------------------------------------------------------

    type private Strategy =
        { Objective: OptimizationObjective
          Cap: int
          Policy: RiskPolicy
          Order: ItemAnalysis list -> ItemAnalysis list
          OrderingReason: ItemAnalysis -> PlanningReason option }

    type private Built =
        { Waves: (int * ItemAnalysis list) list
          Leftover: ItemAnalysis list
          Deferrals: Map<string, PlanningReason list> }

    let private defer (id: string) (reason: PlanningReason) (deferrals: Map<string, PlanningReason list>) =
        let existing = deferrals.TryFind id |> Option.defaultValue [] |> List.filter (fun current -> current.Code <> reason.Code)
        deferrals.Add(id, existing @ [ reason ])

    let rec private buildWaves (context: Context) (strategy: Strategy) (number: int) (remaining: ItemAnalysis list) (finished: Set<string>) (built: Built) : Built =
        let available =
            remaining |> List.filter (fun item -> Graph.openHardPrerequisites item |> List.forall finished.Contains)

        match available with
        | [] -> { built with Leftover = remaining }
        | _ ->
            let chosen, deferrals =
                strategy.Order available
                |> List.fold
                    (fun (chosen: ItemAnalysis list, deferrals) candidate ->
                        if chosen.Length >= strategy.Cap then
                            chosen,
                            defer
                                candidate.Id
                                (PlanningReason.create ReasonCode.CapacityLimited $"deferred from wave {number}: maximum concurrency {strategy.Cap} reached" [])
                                deferrals
                        else
                            match chosen |> List.tryFind (fun member' -> not (strategy.Policy.Admits(collisionOf context candidate.Id member'.Id))) with
                            | Some member' ->
                                let collision = collisionOf context candidate.Id member'.Id

                                chosen,
                                defer
                                    candidate.Id
                                    (PlanningReason.create
                                        ReasonCode.CollisionPreventsParallelization
                                        $"not run alongside {member'.Id} in wave {number}: {CollisionRisk.code collision.Risk} collision risk ({signalsText collision}) is outside policy '{strategy.Policy.Name}'"
                                        [ member'.Id ])
                                    deferrals
                            | None -> chosen @ [ candidate ], deferrals)
                    ([], built.Deferrals)

            let chosenIds = chosen |> List.map (fun item -> item.Id) |> Set.ofList

            buildWaves
                context
                strategy
                (number + 1)
                (remaining |> List.filter (fun item -> not (chosenIds.Contains item.Id)))
                (Set.union finished chosenIds)
                { built with
                    Waves = built.Waves @ [ number, chosen ]
                    Deferrals = deferrals }

    let private actionFor (item: ItemAnalysis) =
        match item.Basis with
        | RemainingBasis.FinalizationOnly -> RecommendedAction.Complete
        | basis when RemainingBasis.isContinuation basis -> RecommendedAction.Continue
        | _ when item.PlanningState = PlanningWorkState.Active -> RecommendedAction.Continue
        | _ -> RecommendedAction.Start

    let private entryReasons (context: Context) (strategy: Strategy) (placement: Map<string, int>) (members: ItemAnalysis list) (deferrals: Map<string, PlanningReason list>) (item: ItemAnalysis) =
        let wave = placement[item.Id]

        [ yield! strategy.OrderingReason item |> Option.toList
          match context.Unlocks.TryFind item.Id with
          | Some unlock when not unlock.TransitiveDependents.IsEmpty ->
              yield PlanningReason.create ReasonCode.PrerequisiteFor $"prerequisite for {unlock.TransitiveDependents.Length} item(s): {unlock.Explanation}" unlock.TransitiveDependents
          | _ -> ()
          if context.Critical.Contains item.Id then
              yield PlanningReason.create ReasonCode.CriticalPath $"on the critical path: {context.Analysis.CriticalPath.Explanation}" context.Analysis.CriticalPath.WorkItems
          match item.Basis, item.Checkpoint with
          | (RemainingBasis.FinalizationOnly | RemainingBasis.VerificationRemaining), Some checkpoint ->
              yield
                  PlanningReason.create
                      ReasonCode.NearlyComplete
                      $"verified checkpoint says little remains ({RemainingBasis.code item.Basis}): next action \"{checkpoint.NextAction}\"; remaining {minutes item.RemainingDuration.Expected} vs {minutes item.FullDuration.Expected} from scratch"
                      [ checkpoint.CheckpointId ]
          | basis, Some checkpoint when RemainingBasis.isContinuation basis ->
              yield
                  PlanningReason.create
                      ReasonCode.ContinuationPreferred
                      $"continue from verified checkpoint {checkpoint.CheckpointId} on {checkpoint.Branch} rather than restart"
                      [ checkpoint.CheckpointId ]
          | _ when item.PlanningState = PlanningWorkState.Active ->
              yield PlanningReason.create ReasonCode.InProgress "already active; continuing it avoids a restart (no checkpoint, so remaining effort is uncertain)" []
          | _ -> ()
          let satisfied =
              item.Dependencies
              |> List.filter (fun resolved -> resolved.Status = DependencyStatus.Satisfied && resolved.Dependency.Kind = DependencyKind.Hard)

          if not satisfied.IsEmpty then
              let names = satisfied |> List.map (fun resolved -> resolved.Reason) |> String.concat "; "
              yield PlanningReason.create ReasonCode.DependenciesSatisfied $"dependencies no longer block it: {names}" (satisfied |> List.map (fun resolved -> DependencyTarget.value resolved.Dependency.Target))
          for prerequisite in Graph.openHardPrerequisites item do
              match placement.TryFind prerequisite with
              | Some earlier -> yield PlanningReason.create ReasonCode.WaitingOnDependency $"runs after its prerequisite {prerequisite} (wave {earlier})" [ prerequisite ]
              | None -> ()
          for other in members |> List.filter (fun other -> other.Id <> item.Id) do
              let collision = collisionOf context item.Id other.Id

              if collision.Risk = CollisionRisk.Safe then
                  yield PlanningReason.create ReasonCode.SafeParallelCandidate $"runs in parallel with {other.Id}: no collision signal" [ other.Id ]
              else
                  yield
                      PlanningReason.create
                          ReasonCode.ParallelRiskAccepted
                          $"runs in parallel with {other.Id} accepting {CollisionRisk.code collision.Risk} risk under policy '{strategy.Policy.Name}': {signalsText collision}"
                          [ other.Id ]
          let related =
              placement
              |> Map.toList
              |> List.filter (fun (other, otherWave) -> other <> item.Id && otherWave <= wave)
              |> List.map fst
              |> Text.sortOrdinal
              |> List.choose (fun other ->
                  match Graph.affinity context.Configuration item context.ById[other] with
                  | [] -> None
                  | reasons -> Some(other, reasons))

          if not related.IsEmpty then
              let text = related |> List.map (fun (other, reasons) -> other + " (" + String.concat ", " reasons + ")") |> String.concat "; "
              yield PlanningReason.create ReasonCode.ContextAffinity $"shares context with {text}" (related |> List.map fst)
          yield! deferrals.TryFind item.Id |> Option.defaultValue [] ]

    let private waveExplanation (policy: RiskPolicy) (members: ItemAnalysis list) =
        match members with
        | [ single ] -> [ PlanningReason.create ReasonCode.OldestRunnable $"{single.Id} runs alone in this wave" [ single.Id ] ]
        | _ ->
            [ PlanningReason.create
                  ReasonCode.SafeParallelCandidate
                  $"{members.Length} items run in parallel under policy '{policy.Name}': {policy.Description}"
                  (members |> List.map (fun item -> item.Id)) ]

    let private sumMoney (estimates: Estimate<Money> list) (currency: string option) : Estimate<Money> =
        match currency, estimates with
        | None, _ -> Estimate.unknown
        | Some currency, _ ->
            let all (pick: Estimate<Money> -> Money option) =
                let values = estimates |> List.map pick
                if values |> List.forall Option.isSome then Some(values |> List.choose id |> Money.sum currency) else None

            let lowers = estimates |> List.choose (fun estimate -> estimate.Lower)

            { Lower = if lowers.IsEmpty then None else Some(Money.sum currency lowers)
              Expected = all (fun estimate -> estimate.Expected)
              Upper = all (fun estimate -> estimate.Upper)
              Confidence = estimates |> List.map (fun estimate -> estimate.Confidence) |> EvidenceConfidence.minimum }

    let private affinityGroups (context: Context) (ordered: ItemAnalysis list) : ContextRecommendation list =
        let related (left: ItemAnalysis) (right: ItemAnalysis) = not (Graph.affinity context.Configuration left right).IsEmpty

        let rec grow (group: ItemAnalysis list) (pool: ItemAnalysis list) =
            match pool |> List.filter (fun candidate -> group |> List.exists (related candidate)) with
            | [] -> group, pool
            | joined ->
                let joinedIds = joined |> List.map (fun item -> item.Id) |> Set.ofList
                grow (group @ joined) (pool |> List.filter (fun item -> not (joinedIds.Contains item.Id)))

        let rec collect (pool: ItemAnalysis list) (groups: ItemAnalysis list list) =
            match pool with
            | [] -> List.rev groups
            | first :: rest ->
                let group, remaining = grow [ first ] rest
                collect remaining (group :: groups)

        let position = ordered |> List.mapi (fun index item -> item.Id, index) |> Map.ofList

        collect ordered []
        |> List.filter (fun group -> group.Length >= 2)
        |> List.map (fun group ->
            let group = group |> List.sortBy (fun item -> position[item.Id])

            let reasons =
                group
                |> List.pairwise
                |> List.collect (fun (left, right) -> Graph.affinity context.Configuration left right)
                |> List.append (group |> List.collect (fun left -> group |> List.filter (fun right -> right.Id <> left.Id) |> List.collect (Graph.affinity context.Configuration left)))
                |> Text.distinctOrdinal

            let chain = group |> List.map (fun item -> item.Id) |> String.concat " -> "
            let because = String.concat ", " reasons

            { WorkItems = group |> List.map (fun item -> item.Id)
              Reason = $"consider one executor for {chain}, in this order, to reuse context ({because}); each keeps its own work-item identity" })

    let private strategyFor (context: Context) (objective: OptimizationObjective) (maxConcurrency: int option) (candidates: ItemAnalysis list) =
        let cap = maxConcurrency |> Option.defaultValue context.Configuration.MaxConcurrency |> max 1
        let scores = balancedScores context candidates

        let fifoReason (item: ItemAnalysis) =
            let created = item.CreatedAt |> Option.defaultValue "unknown"
            Some(PlanningReason.create ReasonCode.OldestRunnable $"baseline order: oldest runnable first (created {created})" [])

        let speedReason (item: ItemAnalysis) =
            Some(
                PlanningReason.create
                    ReasonCode.CriticalPath
                    $"speed order: critical path first, then most dependents ({transitiveCount context item.Id}), continuations, shorter remaining work ({minutes item.RemainingDuration.Expected})"
                    []
            )

        let byScore (items: ItemAnalysis list) =
            items
            |> List.sortWith (fun left right -> compare (fst scores[left.Id]) (fst scores[right.Id]) |> thenBy (compareFifo left right))

        let balancedReason (item: ItemAnalysis) =
            Some(PlanningReason.create ReasonCode.BalancedScore (snd scores[item.Id]) [])

        let costReason (item: ItemAnalysis) =
            let expected = item.RemainingCost.Expected |> Option.map (fun money -> $"{money.Amount} {money.Currency}") |> Option.defaultValue "unknown"
            Some(PlanningReason.create ReasonCode.OldestRunnable $"cost order: cheapest expected remaining cost first ({expected}), one at a time" [])

        match objective with
        | OptimizationObjective.Baseline ->
            { Objective = objective; Cap = 1; Policy = serial; Order = List.sortWith compareFifo; OrderingReason = fifoReason }
        | OptimizationObjective.MinimumCost
        | OptimizationObjective.BudgetConstrained _ ->
            { Objective = objective; Cap = 1; Policy = serial; Order = List.sortWith compareCost; OrderingReason = costReason }
        | OptimizationObjective.MinimumDuration
        | OptimizationObjective.DeadlineConstrained _ ->
            { Objective = objective; Cap = cap; Policy = acceptElevated; Order = List.sortWith (compareSpeed context); OrderingReason = speedReason }
        | OptimizationObjective.Balanced ->
            { Objective = objective; Cap = cap; Policy = praxisStateOnly; Order = byScore; OrderingReason = balancedReason }
        | OptimizationObjective.MaximumSafeParallelism ->
            { Objective = objective
              Cap = maxConcurrency |> Option.defaultValue (max 1 candidates.Length) |> max 1
              Policy = acceptElevated
              Order = List.sortWith (compareSpeed context)
              OrderingReason = speedReason }

    let private assessConstraint (objective: OptimizationObjective) (duration: Estimate<int64>) (cost: Estimate<Money>) (costStatement: string) =
        match objective with
        | OptimizationObjective.DeadlineConstrained deadline ->
            let label = $"deadline {deadline / 60_000L} min"

            Some(
                match duration.Upper, duration.Lower with
                | Some upper, _ when upper <= deadline ->
                    { Constraint = label; Verdict = ConstraintVerdict.Satisfiable; Reason = $"the plan's upper duration bound ({upper / 60_000L} min) is within the deadline" }
                | _, Some lower when lower > deadline ->
                    { Constraint = label; Verdict = ConstraintVerdict.NotSatisfiable; Reason = $"even the plan's lower duration bound ({lower / 60_000L} min) exceeds the deadline" }
                | _ ->
                    { Constraint = label
                      Verdict = ConstraintVerdict.CannotEvaluate
                      Reason = "duration evidence is missing or its range straddles the deadline; the planner will not claim it can be met" }
            )
        | OptimizationObjective.BudgetConstrained budget ->
            let label = $"budget {budget.Amount} {budget.Currency}"

            Some(
                match cost.Upper, cost.Lower with
                | Some upper, _ when upper.Currency = budget.Currency && upper.Amount <= budget.Amount ->
                    { Constraint = label; Verdict = ConstraintVerdict.Satisfiable; Reason = $"the plan's upper cost bound ({upper.Amount} {upper.Currency}) is within budget" }
                | _, Some lower when lower.Currency = budget.Currency && lower.Amount > budget.Amount ->
                    { Constraint = label; Verdict = ConstraintVerdict.NotSatisfiable; Reason = $"even the plan's lower cost bound ({lower.Amount} {lower.Currency}) exceeds the budget" }
                | _ ->
                    { Constraint = label
                      Verdict = ConstraintVerdict.CannotEvaluate
                      Reason = $"the budget cannot be evaluated: {costStatement}" }
            )
        | _ -> None

    /// One strategy's plan over an analysis.
    let simulate (analysis: PlanningAnalysis) (configuration: PlannerConfiguration) (objective: OptimizationObjective) (maxConcurrency: int option) : ExecutionPlan =
        let context = context analysis configuration
        let cycleMembers = Graph.cycles analysis.Items
        let inCycle = cycleMembers |> List.concat |> Set.ofList

        let schedulable = analysis.Items |> List.filter (fun item -> PlanningWorkState.isSchedulable item.PlanningState)
        let gated = schedulable |> List.filter (fun item -> not (gatingDependencies item).IsEmpty)
        let cyclic = schedulable |> List.filter (fun item -> inCycle.Contains item.Id && (gatingDependencies item).IsEmpty)

        let candidates =
            schedulable |> List.filter (fun item -> (gatingDependencies item).IsEmpty && not (inCycle.Contains item.Id))

        let strategy = strategyFor context objective maxConcurrency candidates
        let costRequired = match objective with | OptimizationObjective.MinimumCost | OptimizationObjective.BudgetConstrained _ -> true | _ -> false

        let availability =
            if costRequired && not analysis.History.Cost.Sufficient then
                StrategyAvailability.Unavailable analysis.History.Cost.Statement
            elif objective <> OptimizationObjective.Baseline && not candidates.IsEmpty && candidates |> List.forall (fun item -> not (Estimate.isKnown item.RemainingDuration)) then
                StrategyAvailability.LowConfidence "no remaining-duration estimate is available for any runnable item"
            else
                StrategyAvailability.Available

        let built =
            match availability with
            | StrategyAvailability.Unavailable _ -> { Waves = []; Leftover = []; Deferrals = Map.empty }
            | _ -> buildWaves context strategy 1 candidates Set.empty { Waves = []; Leftover = []; Deferrals = Map.empty }

        let placement =
            built.Waves |> List.collect (fun (number, members) -> members |> List.map (fun item -> item.Id, number)) |> Map.ofList

        let executionWaves =
            built.Waves
            |> List.map (fun (number, members) ->
                let entries =
                    members
                    |> List.map (fun item ->
                        { WorkItem = item.Id
                          Action = actionFor item
                          Remaining = item.RemainingDuration
                          Reasons = entryReasons context strategy placement members built.Deferrals item })

                { Number = number
                  Kind = WaveKind.Execution
                  Entries = entries
                  ExpectedDuration = entries |> List.map (fun entry -> entry.Remaining) |> Estimate.maxDurations
                  Explanation = waveExplanation strategy.Policy members })

        let stale = staleSection context

        let reconciliation =
            match stale with
            | [] -> []
            | _ ->
                let entries =
                    stale
                    |> List.map (fun entry ->
                        let item = context.ById[entry.WorkItem]

                        { WorkItem = entry.WorkItem
                          Action = RecommendedAction.Reconcile
                          Remaining = Inventory.remaining configuration.RemainingFractions RemainingBasis.FinalizationOnly item.FullDuration
                          Reasons = entry.Reasons })

                [ { Number = 0
                    Kind = WaveKind.Reconciliation
                    Entries = entries
                    ExpectedDuration = entries |> List.map (fun entry -> entry.Remaining) |> Estimate.sumDurations
                    Explanation =
                      [ PlanningReason.create
                            ReasonCode.ReconcileStaleState
                            $"{entries.Length} item(s) appear stale; reconcile recorded state before optimizing implementation order (PRX-PLAN-022). Reconciliation mutates shared Praxis state, so it is serial."
                            (entries |> List.map (fun entry -> entry.WorkItem)) ] } ]

        // An unavailable strategy produces no plan at all rather than a partial one.
        let waves =
            match availability with
            | StrategyAvailability.Unavailable _ -> []
            | _ -> reconciliation @ executionWaves

        let leftoverBlocked =
            built.Leftover
            |> List.map (fun item ->
                Graph.openHardPrerequisites item
                |> List.filter (fun prerequisite -> not (placement.ContainsKey prerequisite))
                |> List.map (fun prerequisite ->
                    let state =
                        context.ById.TryFind prerequisite
                        |> Option.map (fun other -> PlanningWorkState.code other.PlanningState)
                        |> Option.defaultValue "unknown"

                    let note = if state = PlanningWorkState.code PlanningWorkState.StaleStateCandidate then " pending reconciliation" else ""
                    PlanningReason.create ReasonCode.WaitingOnDependency $"hard dependency on {prerequisite}, which is {state} and cannot be scheduled{note}" [ prerequisite ])
                |> excluded item)

        let blocked =
            recordedBlockedSection context
            @ (gated |> List.map (fun item -> gatingDependencies item |> List.map dependencyReason |> excluded item))
            @ (cyclic
               |> List.map (fun item ->
                   let cycle = cycleMembers |> List.find (List.contains item.Id)
                   let chain = String.concat " -> " (cycle @ [ List.head cycle ])
                   excluded item [ PlanningReason.create ReasonCode.DependencyCycle $"part of dependency cycle {chain}; no order is chosen arbitrarily" cycle ]))
            @ leftoverBlocked
            |> List.sortWith (fun left right -> Text.ordinal left.WorkItem right.WorkItem)

        let entries = waves |> List.collect (fun wave -> wave.Entries)
        let expectedDuration =
            match availability with
            | StrategyAvailability.Unavailable _ -> Estimate.unknown
            | _ -> waves |> List.map (fun wave -> wave.ExpectedDuration) |> Estimate.sumDurations

        let expectedCost =
            if analysis.History.Cost.Sufficient then
                entries |> List.map (fun entry -> context.ById[entry.WorkItem].RemainingCost) |> fun costs -> sumMoney costs analysis.History.Cost.Currency
            else
                Estimate.unknown

        let ordered = executionWaves |> List.collect (fun wave -> wave.Entries |> List.map (fun entry -> context.ById[entry.WorkItem]))

        let contextAcquisitions =
            ordered
            |> List.filter (fun item ->
                ordered
                |> List.exists (fun earlier -> placement[earlier.Id] < placement[item.Id] && not (Graph.affinity configuration item earlier).IsEmpty)
                |> not)
            |> List.length

        let riskAccepted =
            executionWaves
            |> List.sumBy (fun wave ->
                let ids = wave.Entries |> List.map (fun entry -> entry.WorkItem)
                ids |> List.mapi (fun index left -> ids |> List.skip (index + 1) |> List.filter (fun right -> (collisionOf context left right).Risk <> CollisionRisk.Safe) |> List.length) |> List.sum)

        let confidence =
            match availability with
            | StrategyAvailability.Unavailable _ -> EvidenceConfidence.Unknown
            | _ -> EvidenceConfidence.min analysis.Confidence expectedDuration.Confidence

        { Objective = objective
          Availability = availability
          MaxConcurrency = Some strategy.Cap
          RiskPolicy = $"{strategy.Policy.Name}: {strategy.Policy.Description}"
          Weights = if objective = OptimizationObjective.Balanced then Some configuration.BalancedWeights else None
          Waves = waves
          Blocked = blocked
          NeedsTriage = triageSection context
          StaleStateCandidates = stale
          ExpectedDuration = expectedDuration
          ExpectedCost = expectedCost
          Proxy =
            { Executions = entries.Length
              Continuations = entries |> List.filter (fun entry -> entry.Action = RecommendedAction.Continue || entry.Action = RecommendedAction.Complete) |> List.length
              ContextAcquisitions = contextAcquisitions
              PeakConcurrency = executionWaves |> List.map (fun wave -> wave.Entries.Length) |> List.fold max 0
              RiskAcceptedPairs = riskAccepted }
          Constraint = assessConstraint objective expectedDuration expectedCost analysis.History.Cost.Statement
          ContextRecommendations = affinityGroups context ordered
          Confidence = confidence }
