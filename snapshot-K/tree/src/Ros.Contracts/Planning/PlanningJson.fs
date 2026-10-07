namespace Ros.Contracts.Planning

open System
open System.Globalization
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Planning

/// The versioned, machine-readable planner contract (PRX-PLAN-180/181).
/// Every document carries `schema` and `kind`; field order is fixed, so an
/// identical plan renders to identical bytes (PRX-PLAN-002).
[<RequireQualifiedAccess>]
module PlanningJson =
    let schema = Planner.schemaVersion

    // ---- rendering helpers ----------------------------------------------------

    let private options =
        JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let render (node: JsonNode) = node.ToJsonString(options) + "\n"

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj
    let private integer (value: int) : JsonNode = JsonValue.Create value
    let private long (value: int64) : JsonNode = JsonValue.Create value
    let private optionalLong (value: int64 option) : JsonNode = value |> Option.map long |> Option.toObj
    let private number (value: decimal) : JsonNode = JsonValue.Create value
    let private optionalNumber (value: decimal option) : JsonNode = value |> Option.map number |> Option.toObj
    let private boolean (value: bool) : JsonNode = JsonValue.Create value

    let private array (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter (fun value -> result.Add value)
        result

    let private texts (values: string list) = values |> List.map text |> array

    let private record (fields: (string * JsonNode) list) : JsonNode =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    let private minutes (ms: int64) =
        let total = ms / 60_000L
        if total < 60L then $"{total} min" else $"{total / 60L} h {total % 60L:D2} min"

    /// PRX-PLAN-061: a range in words, never false precision.
    let describeDuration (estimate: Estimate<int64>) =
        match estimate.Lower, estimate.Expected, estimate.Upper with
        | Some lower, _, Some upper when lower = upper && lower = 0L -> "under 1 min"
        | Some lower, _, Some upper when lower = upper -> $"approximately {minutes lower}"
        | Some 0L, _, Some upper -> $"up to approximately {minutes upper}"
        | Some lower, _, Some upper -> $"approximately {minutes lower} to {minutes upper}"
        | Some lower, None, None -> $"at least {minutes lower}; upper bound unknown"
        | _ -> "unknown"

    let private duration (estimate: Estimate<int64>) =
        record
            [ "lowerMs", optionalLong estimate.Lower
              "expectedMs", optionalLong estimate.Expected
              "upperMs", optionalLong estimate.Upper
              "confidence", text (EvidenceConfidence.code estimate.Confidence)
              "display", text (describeDuration estimate) ]

    let private money (value: Money) = record [ "amount", number value.Amount; "currency", text value.Currency ]

    let private moneyEstimate (estimate: Estimate<Money>) =
        record
            [ "lower", estimate.Lower |> Option.map money |> Option.toObj
              "expected", estimate.Expected |> Option.map money |> Option.toObj
              "upper", estimate.Upper |> Option.map money |> Option.toObj
              "confidence", text (EvidenceConfidence.code estimate.Confidence) ]

    let private provenance (value: Provenance) =
        record [ "source", text (EvidenceSource.code value.Source); "reference", text value.Reference ]

    let private reason (value: PlanningReason) =
        record [ "code", text (ReasonCode.code value.Code); "message", text value.Message; "references", texts value.References ]

    let private finding (value: PlanningFinding) =
        record
            [ "code", text (FindingCode.code value.Code)
              "severity", text (FindingSeverity.code value.Severity)
              "workItems", texts value.WorkItems
              "message", text value.Message
              "recommendedAction", optionalText value.RecommendedAction
              "confidence", text (EvidenceConfidence.code value.Confidence)
              "evidence", value.Evidence |> List.map provenance |> array ]

    let private itemDigest (value: ItemDigest) =
        record
            [ "workItem", text value.WorkItem
              "lifecycleState", text value.LifecycleState
              "planningState", text (PlanningWorkState.code value.PlanningState)
              "checkpointId", optionalText value.CheckpointId
              "digest", text value.Digest ]

    let private snapshot (value: PlanSnapshot) =
        record
            [ "repository", text value.Repository
              "commit", optionalText value.Commit
              "branch", optionalText value.Branch
              "plannedAt", text value.PlannedAt
              "plannerVersion", text value.PlannerVersion
              "workStateFingerprint", text value.WorkStateFingerprint
              "inputFingerprint", text value.InputFingerprint
              "collisionFingerprint", text value.CollisionFingerprint
              "items", value.Items |> List.map itemDigest |> array ]

    let private checkpoint (value: CheckpointSummary) =
        record
            [ "id", text value.CheckpointId
              "executionId", text value.ExecutionId
              "recordedAt", text value.RecordedAt
              "branch", text value.Branch
              "commit", text value.Commit
              "summary", text value.Summary
              "nextAction", text value.NextAction
              "verified", boolean value.Verified ]

    let private dependency (value: ResolvedDependency) =
        record
            [ "target", text (DependencyTarget.value value.Dependency.Target)
              "targetKind", text (DependencyTarget.kindCode value.Dependency.Target)
              "kind", text (DependencyKind.code value.Dependency.Kind)
              "origin", text (DependencyOrigin.code value.Dependency.Origin)
              "status", text (DependencyStatus.code value.Status)
              "reason", text value.Reason
              "statement", text value.Dependency.Statement
              "provenance", provenance value.Dependency.Provenance ]

    let private item (value: ItemAnalysis) =
        record
            [ "id", text value.Id
              "title", text value.Title
              "recordedState", text value.LifecycleState
              "planningState", text (PlanningWorkState.code value.PlanningState)
              "inQueue", boolean value.InQueue
              "inContext", boolean value.InContext
              "tags", texts value.Tags
              "createdAt", optionalText value.CreatedAt
              "taskClass", text value.TaskClass
              "taskClassOrigin", text value.TaskClassOrigin
              "triage", value.Triage |> List.map TriageNeed.code |> texts
              "blockReason", optionalText value.BlockReason
              "dependencies", value.Dependencies |> List.map dependency |> array
              "checkpoint", value.Checkpoint |> Option.map checkpoint |> Option.toObj
              "remainingBasis", text (RemainingBasis.code value.Basis)
              "fullDuration", duration value.FullDuration
              "remainingDuration", duration value.RemainingDuration
              "remainingCost", moneyEstimate value.RemainingCost
              "costEvidence", text (CostEvidenceKind.code value.CostEvidence)
              "provenance", value.Provenance |> List.map provenance |> array ]

    let private collision (value: Collision) =
        record
            [ "left", text value.Left
              "right", text value.Right
              "risk", text (CollisionRisk.code value.Risk)
              "signals",
              value.Signals
              |> List.map (fun signal ->
                  record
                      [ "code", text (CollisionSignal.code signal)
                        "detail", text (CollisionSignal.detail signal)
                        "risk", text (CollisionRisk.code (CollisionSignal.risk signal)) ])
              |> array ]

    let private distribution (value: DurationDistribution) =
        record
            [ "taskClass", text value.TaskClass
              "samples", integer value.SampleCount
              "lowerMs", long value.Lower
              "medianMs", long value.Median
              "upperMs", long value.Upper
              "confidence", text (EvidenceConfidence.code value.Confidence) ]

    let private costSummary (value: CostEvidenceSummary) =
        record
            [ "sampledExecutions", integer value.SampledExecutions
              "withUsableCost", integer value.WithUsableCost
              "withTokenUsage", integer value.WithTokenUsage
              "currency", optionalText value.Currency
              "sufficient", boolean value.Sufficient
              "statement", text value.Statement ]

    let private history (value: HistorySummary) =
        record
            [ "sampledExecutions", integer value.SampledExecutions
              "finalizedExecutions", integer value.FinalizedExecutions
              "activeExecutionsExcluded", integer value.ActiveExecutionsExcluded
              "durationSamples", integer value.DurationSamples
              "durations", value.Distributions |> List.map distribution |> array
              "cost", costSummary value.Cost
              "segments",
              value.Segments
              |> List.map (fun segment ->
                  record
                      [ "dimension", text segment.Dimension
                        "value", text segment.Value
                        "samples", integer segment.SampleCount
                        "medianMs", optionalLong segment.MedianMs ])
              |> array
              "drift",
              value.Drift
              |> Option.map (fun drift ->
                  record
                      [ "recentSamples", integer drift.RecentSampleCount
                        "recentMedianMs", long drift.RecentMedianMs
                        "priorLowerMs", long drift.PriorLowerMs
                        "priorUpperMs", long drift.PriorUpperMs
                        "drifted", boolean drift.Drifted
                        "statement", text drift.Statement ])
              |> Option.toObj ]

    let private unlock (value: UnlockValue) =
        record
            [ "workItem", text value.WorkItem
              "directlyUnlocks", texts value.DirectlyUnlocks
              "transitiveDependents", texts value.TransitiveDependents
              "explanation", text value.Explanation ]

    let private criticalPath (value: CriticalPath) =
        record [ "workItems", texts value.WorkItems; "expectedDuration", duration value.ExpectedDuration; "explanation", text value.Explanation ]

    let private stateCounts (items: ItemAnalysis list) =
        PlanningWorkState.all
        |> List.map (fun state -> PlanningWorkState.code state, integer (items |> List.filter (fun item -> item.PlanningState = state) |> List.length))
        |> record

    let analysis (value: PlanningAnalysis) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "analysis"
              "snapshot", snapshot value.Snapshot
              "statement", text Planner.advisoryStatement
              "confidence", text (EvidenceConfidence.code value.Confidence)
              "confidenceStatement", text value.ConfidenceStatement
              "planningStates", stateCounts value.Items
              "items", value.Items |> List.filter (fun entry -> not (PlanningWorkState.isTerminal entry.PlanningState)) |> List.map item |> array
              "terminalItems", value.Items |> List.filter (fun entry -> PlanningWorkState.isTerminal entry.PlanningState) |> List.length |> integer
              "findings", value.Findings |> List.map finding |> array
              "criticalPath", criticalPath value.CriticalPath
              "unlocks", value.Unlocks |> List.map unlock |> array
              "collisions", value.Collisions |> List.map collision |> array
              "history", history value.History
              "evidenceRecommendations", texts value.EvidenceRecommendations ]

    // ---- plans ------------------------------------------------------------------

    let private objective (value: OptimizationObjective) =
        match value with
        | OptimizationObjective.BudgetConstrained budget -> record [ "code", text (OptimizationObjective.code value); "budget", money budget ]
        | OptimizationObjective.DeadlineConstrained deadline -> record [ "code", text (OptimizationObjective.code value); "deadlineMs", long deadline ]
        | _ -> record [ "code", text (OptimizationObjective.code value) ]

    let private weights (value: BalancedWeights) =
        record
            [ "duration", number value.Duration
              "cost", number value.Cost
              "conflictRisk", number value.ConflictRisk
              "uncertainty", number value.Uncertainty
              "contextReuse", number value.ContextReuse
              "completionLikelihood", number value.CompletionLikelihood ]

    let private excluded (value: ExcludedItem) =
        record
            [ "workItem", text value.WorkItem
              "planningState", text (PlanningWorkState.code value.State)
              "reasons", value.Reasons |> List.map reason |> array ]

    let private wave (value: PlanWave) =
        record
            [ "number", integer value.Number
              "kind", text (match value.Kind with WaveKind.Reconciliation -> "reconciliation" | WaveKind.Execution -> "execution")
              "expectedDuration", duration value.ExpectedDuration
              "explanation", value.Explanation |> List.map reason |> array
              "entries",
              value.Entries
              |> List.map (fun entry ->
                  record
                      [ "workItem", text entry.WorkItem
                        "action", text (RecommendedAction.code entry.Action)
                        "remaining", duration entry.Remaining
                        "reasons", entry.Reasons |> List.map reason |> array ])
              |> array ]

    let executionPlan (value: ExecutionPlan) : JsonNode =
        record
            [ "objective", objective value.Objective
              "availability", text (StrategyAvailability.code value.Availability)
              "availabilityReason", optionalText (StrategyAvailability.reason value.Availability)
              "maxConcurrency", value.MaxConcurrency |> Option.map integer |> Option.toObj
              "riskPolicy", text value.RiskPolicy
              "weights", value.Weights |> Option.map weights |> Option.toObj
              "confidence", text (EvidenceConfidence.code value.Confidence)
              "expectedDuration", duration value.ExpectedDuration
              "expectedCost", moneyEstimate value.ExpectedCost
              "proxyCost",
              record
                  [ "executions", integer value.Proxy.Executions
                    "continuations", integer value.Proxy.Continuations
                    "contextAcquisitions", integer value.Proxy.ContextAcquisitions
                    "peakConcurrency", integer value.Proxy.PeakConcurrency
                    "riskAcceptedPairs", integer value.Proxy.RiskAcceptedPairs ]
              "constraint",
              value.Constraint
              |> Option.map (fun assessment ->
                  record
                      [ "constraint", text assessment.Constraint
                        "verdict", text (ConstraintVerdict.code assessment.Verdict)
                        "reason", text assessment.Reason ])
              |> Option.toObj
              "waves", value.Waves |> List.map wave |> array
              "blocked", value.Blocked |> List.map excluded |> array
              "needsTriage", value.NeedsTriage |> List.map excluded |> array
              "staleStateCandidates", value.StaleStateCandidates |> List.map excluded |> array
              "contextRecommendations",
              value.ContextRecommendations |> List.map (fun recommendation -> record [ "workItems", texts recommendation.WorkItems; "reason", text recommendation.Reason ]) |> array ]

    /// The logical plan: everything except when it was computed. Identical
    /// planning inputs render this to identical bytes.
    let logicalPlan (value: PlanDocument) : JsonNode =
        record
            [ "schema", text value.SchemaVersion
              "kind", text "plan"
              "workStateFingerprint", text value.Snapshot.WorkStateFingerprint
              "inputFingerprint", text value.Snapshot.InputFingerprint
              "plan", executionPlan value.Plan
              "findings", value.Findings |> List.map finding |> array ]

    let plan (value: PlanDocument) : JsonNode =
        record
            [ "schema", text value.SchemaVersion
              "kind", text "plan"
              "snapshot", snapshot value.Snapshot
              "statement", text value.Statement
              "plan", executionPlan value.Plan
              "findings", value.Findings |> List.map finding |> array ]

    let comparison (snapshotValue: PlanSnapshot) (value: StrategyComparison) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "comparison"
              "snapshot", snapshot snapshotValue
              "statement", text value.Statement
              "frontier", texts value.Frontier
              "strategies",
              value.Strategies
              |> List.map (fun summary ->
                  record
                      [ "objective", text summary.Objective
                        "availability", text (StrategyAvailability.code summary.Availability)
                        "availabilityReason", optionalText (StrategyAvailability.reason summary.Availability)
                        "expectedDuration", duration summary.ExpectedDuration
                        "expectedCost", moneyEstimate summary.ExpectedCost
                        "waves", integer summary.WaveCount
                        "peakConcurrency", integer summary.PeakConcurrency
                        "riskAcceptedPairs", integer summary.RiskAcceptedPairs
                        "executions", integer summary.Executions
                        "contextAcquisitions", integer summary.ContextAcquisitions
                        "confidence", text (EvidenceConfidence.code summary.Confidence)
                        "onFrontier", boolean summary.OnFrontier ])
              |> array
              "concurrencyCurve",
              value.ConcurrencyCurve
              |> List.map (fun point ->
                  record
                      [ "maxConcurrency", integer point.MaxConcurrency
                        "expectedDuration", duration point.ExpectedDuration
                        "peakConcurrency", integer point.PeakConcurrency
                        "riskAcceptedPairs", integer point.RiskAcceptedPairs
                        "marginalSavingMs", optionalLong point.MarginalSavingMs
                        "diminishingReturns", boolean point.DiminishingReturns ])
              |> array
              "diminishingReturns", optionalText value.DiminishingReturns ]

    let explanation (snapshotValue: PlanSnapshot) (value: ItemExplanation) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "explanation"
              "snapshot", snapshot snapshotValue
              "workItem", text value.WorkItem
              "item", item value.Item
              "placements",
              value.Placements
              |> List.map (fun placement ->
                  record
                      [ "objective", text placement.Objective
                        "section", text placement.Section
                        "wave", placement.Wave |> Option.map integer |> Option.toObj
                        "action", placement.Action |> Option.map (RecommendedAction.code >> text) |> Option.toObj
                        "reasons", placement.Reasons |> List.map reason |> array ])
              |> array
              "onCriticalPath", boolean value.OnCriticalPath
              "unlock", value.Unlock |> Option.map unlock |> Option.toObj
              "collisions", value.Collisions |> List.map collision |> array
              "affinity", value.Affinity |> List.map (fun (other, reasons) -> record [ "workItem", text other; "reasons", texts reasons ]) |> array
              "estimateBasis", texts value.EstimateBasis
              "findings", value.Findings |> List.map finding |> array ]

    let replay (value: ReplayReport) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "replay"
              "statement", text value.Statement
              "executions", integer value.Executions
              "predicted", integer value.Predicted
              "unpredictable", integer value.Unpredictable
              "withinRange", integer value.WithinRange
              "coverage", optionalNumber value.Coverage
              "medianAbsoluteErrorMs", optionalLong value.MedianAbsoluteErrorMs
              "medianRelativeError", optionalNumber value.MedianRelativeError
              "byClass",
              value.ByClass
              |> List.map (fun accuracy ->
                  record
                      [ "taskClass", text accuracy.TaskClass
                        "predictions", integer accuracy.Predictions
                        "withinRange", integer accuracy.WithinRange
                        "medianAbsoluteErrorMs", optionalLong accuracy.MedianAbsoluteErrorMs
                        "medianRelativeError", optionalNumber accuracy.MedianRelativeError ])
              |> array
              "observedOverlap",
              record
                  [ "peakConcurrency", integer value.Overlap.PeakConcurrency
                    "overlappingMs", long value.Overlap.OverlappingMs
                    "calendarSpanMs", long value.Overlap.CalendarSpanMs
                    "modeledBaselineConcurrency", integer 1 ]
              "ordering",
              record
                  [ "comparablePairs", integer value.Ordering.ComparablePairs
                    "concordantPairs", integer value.Ordering.ConcordantPairs
                    "agreement", optionalNumber value.Ordering.Agreement
                    "statement", text value.Ordering.Statement ]
              "cost", costSummary value.Cost
              "predictions",
              value.Predictions
              |> List.map (fun prediction ->
                  record
                      [ "executionId", text prediction.ExecutionId
                        "workItem", text prediction.WorkItemId
                        "taskClass", text prediction.TaskClass
                        "trainingSamples", integer prediction.TrainingSamples
                        "predicted", duration prediction.Predicted
                        "actualMs", long prediction.ActualMs
                        "withinRange", prediction.WithinRange |> Option.map boolean |> Option.toObj
                        "absoluteErrorMs", optionalLong prediction.AbsoluteErrorMs
                        "relativeError", optionalNumber prediction.RelativeError ])
              |> array ]

    let review (current: PlanSnapshot) (value: PlanReview) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "freshness"
              "snapshot", snapshot current
              "stale", boolean value.Freshness.Stale
              "changes",
              value.Freshness.Changes
              |> List.map (fun change -> record [ "kind", text (PlanChangeKind.code change.Kind); "workItem", optionalText change.WorkItem; "message", text change.Message ])
              |> array
              "statement", text value.Statement
              "outcomes",
              value.Outcomes
              |> List.map (fun outcome ->
                  record
                      [ "workItem", text outcome.WorkItem
                        "recommendedWave", integer outcome.RecommendedWave
                        "recommendedAction", text (RecommendedAction.code outcome.RecommendedAction)
                        "lifecycleThen", text outcome.LifecycleThen
                        "lifecycleNow", text outcome.LifecycleNow
                        "outcome", text outcome.Outcome ])
              |> array ]

    // ---- parsing (round trip of the plan document) ------------------------------

    exception private Malformed of string

    let private fail message = raise (Malformed message)

    let private field (node: JsonObject) (name: string) : JsonNode = node[name]

    let private asObject (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as value -> value
        | _ -> fail $"{name} must be an object"

    let private obj (node: JsonObject) name = field node name |> asObject name

    let private optionalObj (node: JsonObject) name =
        match field node name with
        | null -> None
        | value -> Some(asObject name value)

    let private readText (node: JsonObject) name =
        match field node name with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
        | _ -> fail $"{name} must be a string"

    let private readOptionalText (node: JsonObject) name =
        match field node name with
        | null -> None
        | _ -> Some(readText node name)

    let private readNumber<'a> (node: JsonObject) (name: string) : 'a option =
        match field node name with
        | null -> None
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> Some(value.GetValue<'a>())
        | _ -> fail $"{name} must be a number"

    let private readRequired<'a> (node: JsonObject) (name: string) : 'a =
        readNumber<'a> node name |> Option.defaultWith (fun () -> fail $"{name} is required")

    let private readBool (node: JsonObject) name =
        match field node name with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.False -> false
        | _ -> fail $"{name} must be a boolean"

    let private items (node: JsonObject) (name: string) : JsonNode list =
        match field node name with
        | :? JsonArray as values -> values |> Seq.toList
        | _ -> fail $"{name} must be an array"

    let private objects (node: JsonObject) (name: string) = items node name |> List.map (asObject name)

    let private readTexts (node: JsonObject) (name: string) =
        items node name
        |> List.map (function
            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
            | _ -> fail $"{name} must contain strings")

    let private parsed (what: string) (parse: string -> 'a option) (value: string) =
        parse value |> Option.defaultWith (fun () -> fail $"unknown {what} '{value}'")

    let private readConfidence node name = readText node name |> parsed "confidence" EvidenceConfidence.tryParse

    let private readDuration (node: JsonObject) : Estimate<int64> =
        { Lower = readNumber<int64> node "lowerMs"
          Expected = readNumber<int64> node "expectedMs"
          Upper = readNumber<int64> node "upperMs"
          Confidence = readConfidence node "confidence" }

    let private readMoney (node: JsonObject) : Money =
        { Amount = readRequired<decimal> node "amount"; Currency = readText node "currency" }

    let private readMoneyEstimate (node: JsonObject) : Estimate<Money> =
        { Lower = optionalObj node "lower" |> Option.map readMoney
          Expected = optionalObj node "expected" |> Option.map readMoney
          Upper = optionalObj node "upper" |> Option.map readMoney
          Confidence = readConfidence node "confidence" }

    let private readProvenance (node: JsonObject) : Provenance =
        { Source = readText node "source" |> parsed "evidence source" EvidenceSource.tryParse
          Reference = readText node "reference" }

    let private readReason (node: JsonObject) : PlanningReason =
        { Code = readText node "code" |> parsed "reason code" ReasonCode.tryParse
          Message = readText node "message"
          References = readTexts node "references" }

    let private readFinding (node: JsonObject) : PlanningFinding =
        { Code = readText node "code" |> parsed "finding code" FindingCode.tryParse
          Severity = readText node "severity" |> parsed "severity" FindingSeverity.tryParse
          WorkItems = readTexts node "workItems"
          Message = readText node "message"
          RecommendedAction = readOptionalText node "recommendedAction"
          Confidence = readConfidence node "confidence"
          Evidence = objects node "evidence" |> List.map readProvenance }

    let private readSnapshot (node: JsonObject) : PlanSnapshot =
        { Repository = readText node "repository"
          Commit = readOptionalText node "commit"
          Branch = readOptionalText node "branch"
          PlannedAt = readText node "plannedAt"
          PlannerVersion = readText node "plannerVersion"
          WorkStateFingerprint = readText node "workStateFingerprint"
          InputFingerprint = readText node "inputFingerprint"
          CollisionFingerprint = readText node "collisionFingerprint"
          Items =
            objects node "items"
            |> List.map (fun entry ->
                { WorkItem = readText entry "workItem"
                  LifecycleState = readText entry "lifecycleState"
                  PlanningState = readText entry "planningState" |> parsed "planning state" PlanningWorkState.tryParse
                  CheckpointId = readOptionalText entry "checkpointId"
                  Digest = readText entry "digest" }) }

    let private readObjective (node: JsonObject) =
        match readText node "code" with
        | "baseline" -> OptimizationObjective.Baseline
        | "cost" -> OptimizationObjective.MinimumCost
        | "speed" -> OptimizationObjective.MinimumDuration
        | "balanced" -> OptimizationObjective.Balanced
        | "max-parallel" -> OptimizationObjective.MaximumSafeParallelism
        | "budget" -> OptimizationObjective.BudgetConstrained(obj node "budget" |> readMoney)
        | "deadline" -> OptimizationObjective.DeadlineConstrained(readRequired<int64> node "deadlineMs")
        | other -> fail $"unknown objective '{other}'"

    let private readExcluded (node: JsonObject) : ExcludedItem =
        { WorkItem = readText node "workItem"
          State = readText node "planningState" |> parsed "planning state" PlanningWorkState.tryParse
          Reasons = objects node "reasons" |> List.map readReason }

    let private readWave (node: JsonObject) : PlanWave =
        { Number = readRequired<int> node "number"
          Kind =
            match readText node "kind" with
            | "reconciliation" -> WaveKind.Reconciliation
            | "execution" -> WaveKind.Execution
            | other -> fail $"unknown wave kind '{other}'"
          ExpectedDuration = obj node "expectedDuration" |> readDuration
          Explanation = objects node "explanation" |> List.map readReason
          Entries =
            objects node "entries"
            |> List.map (fun entry ->
                { WorkItem = readText entry "workItem"
                  Action = readText entry "action" |> parsed "action" RecommendedAction.tryParse
                  Remaining = obj entry "remaining" |> readDuration
                  Reasons = objects entry "reasons" |> List.map readReason }) }

    let private readPlan (node: JsonObject) : ExecutionPlan =
        let proxy = obj node "proxyCost"

        { Objective = obj node "objective" |> readObjective
          Availability =
            StrategyAvailability.tryParse (readText node "availability") (readOptionalText node "availabilityReason")
            |> Option.defaultWith (fun () -> fail "invalid availability")
          MaxConcurrency = readNumber<int> node "maxConcurrency"
          RiskPolicy = readText node "riskPolicy"
          Weights =
            optionalObj node "weights"
            |> Option.map (fun weights ->
                { Duration = readRequired<decimal> weights "duration"
                  Cost = readRequired<decimal> weights "cost"
                  ConflictRisk = readRequired<decimal> weights "conflictRisk"
                  Uncertainty = readRequired<decimal> weights "uncertainty"
                  ContextReuse = readRequired<decimal> weights "contextReuse"
                  CompletionLikelihood = readRequired<decimal> weights "completionLikelihood" })
          Confidence = readConfidence node "confidence"
          ExpectedDuration = obj node "expectedDuration" |> readDuration
          ExpectedCost = obj node "expectedCost" |> readMoneyEstimate
          Proxy =
            { Executions = readRequired<int> proxy "executions"
              Continuations = readRequired<int> proxy "continuations"
              ContextAcquisitions = readRequired<int> proxy "contextAcquisitions"
              PeakConcurrency = readRequired<int> proxy "peakConcurrency"
              RiskAcceptedPairs = readRequired<int> proxy "riskAcceptedPairs" }
          Constraint =
            optionalObj node "constraint"
            |> Option.map (fun assessment ->
                { Constraint = readText assessment "constraint"
                  Verdict = readText assessment "verdict" |> parsed "verdict" ConstraintVerdict.tryParse
                  Reason = readText assessment "reason" })
          Waves = objects node "waves" |> List.map readWave
          Blocked = objects node "blocked" |> List.map readExcluded
          NeedsTriage = objects node "needsTriage" |> List.map readExcluded
          StaleStateCandidates = objects node "staleStateCandidates" |> List.map readExcluded
          ContextRecommendations =
            objects node "contextRecommendations"
            |> List.map (fun recommendation -> { WorkItems = readTexts recommendation "workItems"; Reason = readText recommendation "reason" }) }

    /// Reads a plan document written by `plan`; refuses another schema.
    let parsePlan (json: string) : Result<PlanDocument, string> =
        try
            let root = JsonNode.Parse json |> asObject "document"

            match readText root "schema", readText root "kind" with
            | version, _ when version <> schema -> Error $"unsupported plan schema '{version}' (expected '{schema}')"
            | _, kind when kind <> "plan" -> Error $"expected a plan document, found '{kind}'"
            | version, _ ->
                Ok
                    { SchemaVersion = version
                      Snapshot = obj root "snapshot" |> readSnapshot
                      Statement = readText root "statement"
                      Plan = obj root "plan" |> readPlan
                      Findings = objects root "findings" |> List.map readFinding }
        with
        | Malformed message -> Error $"malformed plan document: {message}"
        | :? JsonException as error -> Error $"malformed plan document: {error.Message}"

    // ---- planner inputs supplied by the caller -----------------------------------

    let private readPair (node: JsonObject) (name: string) (fallback: decimal * decimal) =
        match field node name with
        | null -> fallback
        | :? JsonArray as values when values.Count = 2 ->
            let at (index: int) =
                match values[index] with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> value.GetValue<decimal>()
                | _ -> fail $"{name} must be [lower, upper]"

            at 0, at 1
        | _ -> fail $"{name} must be [lower, upper]"

    /// The optional planner configuration file. Every field is optional and
    /// defaults to `PlannerConfiguration.defaults`.
    /// One `grouping.groups` entry. `.ros/work/groups.json` stores the same
    /// shape, so a stored declaration is read exactly as a configured one.
    let private readDeclaredGroup (group: JsonObject) : DeclaredGroup =
        let optionalTexts name = if isNull (field group name) then [] else readTexts group name

        { Id = readText group "id"
          Members = readTexts group "members"
          Kind = readOptionalText group "kind" |> Option.map (parsed "group kind" GroupKind.tryParse)
          Origin = readOptionalText group "origin" |> Option.map (parsed "group origin" GroupOrigin.tryParse) |> Option.defaultValue GroupOrigin.HumanDeclared
          SharedContext = optionalTexts "sharedContext"
          ExecutionRepository = readOptionalText group "executionRepository"
          CrossRepository = if isNull (field group "crossRepository") then false else readBool group "crossRepository"
          ArchitectureNotes = optionalTexts "architectureNotes" }

    let parseConfiguration (json: string) : Result<PlannerConfiguration, string> =
        try
            let root = JsonNode.Parse json |> asObject "configuration"
            let defaults = PlannerConfiguration.defaults
            let orDefault (read: unit -> 'a option) (fallback: 'a) = read () |> Option.defaultValue fallback

            let weights =
                match optionalObj root "balancedWeights" with
                | None -> defaults.BalancedWeights
                | Some node ->
                    let weight name fallback = readNumber<decimal> node name |> Option.defaultValue fallback

                    { Duration = weight "duration" defaults.BalancedWeights.Duration
                      Cost = weight "cost" defaults.BalancedWeights.Cost
                      ConflictRisk = weight "conflictRisk" defaults.BalancedWeights.ConflictRisk
                      Uncertainty = weight "uncertainty" defaults.BalancedWeights.Uncertainty
                      ContextReuse = weight "contextReuse" defaults.BalancedWeights.ContextReuse
                      CompletionLikelihood = weight "completionLikelihood" defaults.BalancedWeights.CompletionLikelihood }

            let fractions =
                match optionalObj root "remainingFractions" with
                | None -> defaults.RemainingFractions
                | Some node ->
                    { FinalizationOnly = readPair node "finalizationOnly" defaults.RemainingFractions.FinalizationOnly
                      VerificationRemaining = readPair node "verificationRemaining" defaults.RemainingFractions.VerificationRemaining
                      ImplementationInProgress = readPair node "implementationInProgress" defaults.RemainingFractions.ImplementationInProgress
                      Unclassified = readPair node "unclassified" defaults.RemainingFractions.Unclassified }

            let listOf name read = if isNull (field root name) then [] else objects root name |> List.map read

            let grouping =
                let fallback = GroupingConfiguration.defaults

                match optionalObj root "grouping" with
                | None -> fallback
                | Some node ->
                    let preferredMinimum, preferredMaximum =
                        match field node "preferredSize" with
                        | null -> fallback.PreferredMinimumSize, fallback.PreferredMaximumSize
                        | _ ->
                            let low, high = readPair node "preferredSize" (decimal fallback.PreferredMinimumSize, decimal fallback.PreferredMaximumSize)
                            int low, int high

                    let optionalList name read = if isNull (field node name) then [] else objects node name |> List.map read

                    { PreferredMinimumSize = preferredMinimum
                      PreferredMaximumSize = preferredMaximum
                      MaximumAutomaticSize = readNumber<int> node "maximumAutomaticSize" |> Option.defaultValue fallback.MaximumAutomaticSize
                      MinimumAffinity =
                        readOptionalText node "minimumAffinity"
                        |> Option.map (parsed "affinity" ContextAffinity.tryParse)
                        |> Option.defaultValue fallback.MinimumAffinity
                      Groups = optionalList "groups" readDeclaredGroup
                      Architecture =
                        optionalList "architecture" (fun decision ->
                            { Decision = readText decision "decision"
                              Members = readTexts decision "members"
                              Statement = readOptionalText decision "statement" |> Option.defaultValue "" })
                      ExecutionRepositories =
                        match optionalObj node "executionRepositories" with
                        | None -> []
                        | Some repositories ->
                            repositories
                            |> Seq.map (fun property -> property.Key, readText repositories property.Key)
                            |> Seq.toList
                            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right)) }

            Ok
                { MaxConcurrency = orDefault (fun () -> readNumber<int> root "maxConcurrency") defaults.MaxConcurrency
                  MinimumCostSamples = orDefault (fun () -> readNumber<int> root "minimumCostSamples") defaults.MinimumCostSamples
                  PraxisStateMergeSafe = if isNull (field root "praxisStateMergeSafe") then defaults.PraxisStateMergeSafe else readBool root "praxisStateMergeSafe"
                  GenericTags = if isNull (field root "genericTags") then defaults.GenericTags else readTexts root "genericTags"
                  BalancedWeights = weights
                  RemainingFractions = fractions
                  Dependencies =
                    listOf "dependencies" (fun node ->
                        { From = readText node "from"
                          To = readText node "to"
                          Kind = readOptionalText node "kind" |> Option.defaultValue "hard" |> parsed "dependency kind" DependencyKind.tryParse })
                  Conflicts = listOf "conflicts" (fun node -> { Left = readText node "left"; Right = readText node "right"; Reason = readText node "reason" })
                  Areas =
                    match optionalObj root "areas" with
                    | None -> []
                    | Some areas ->
                        areas
                        |> Seq.map (fun property -> property.Key, readTexts areas property.Key)
                        |> Seq.toList
                        |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))
                  Grouping = grouping }
        with
        | Malformed message -> Error $"malformed planner configuration: {message}"
        | :? JsonException as error -> Error $"malformed planner configuration: {error.Message}"

    /// External evidence supplied by a caller that can see CI or GitHub
    /// (an agent or a workflow). The planner itself never contacts either.
    let parseObservations (origin: string) (json: string) : Result<Observation list, string> =
        try
            let root = JsonNode.Parse json |> asObject "observations"

            objects root "observations"
            |> List.mapi (fun index node ->
                let source =
                    match readOptionalText node "source" with
                    | Some "github" -> EvidenceSource.GitHub
                    | Some "ci" -> EvidenceSource.ContinuousIntegration
                    | Some "git" -> EvidenceSource.Git
                    | Some "telemetry" -> EvidenceSource.Telemetry
                    | _ -> EvidenceSource.ExternalObservation

                let reference = readOptionalText node "reference" |> Option.defaultValue $"{origin}#observations[{index}]"

                let kind =
                    match readText node "kind" with
                    | "pull-request-merged" -> ObservationKind.PullRequestMerged(readRequired<int> node "pullRequest")
                    | "commit-merged" -> ObservationKind.CommitMerged(readText node "commit", readOptionalText node "into" |> Option.defaultValue "default branch")
                    | "ci-passed" -> ObservationKind.ContinuousIntegrationPassed(readText node "subject")
                    | "ci-failed" -> ObservationKind.ContinuousIntegrationFailed(readText node "subject")
                    | "release-exists" -> ObservationKind.ReleaseExists(readText node "tag")
                    | "context-pressure" ->
                        let indicators =
                            obj node "indicators"
                            |> Seq.map (fun property ->
                                match property.Value with
                                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> property.Key, value.GetValue<int>()
                                | _ -> fail "indicators must be counts")
                            |> Seq.toList
                            |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))

                        ObservationKind.ContextPressure(readTexts node "members", indicators)
                    | other -> fail $"unknown observation kind '{other}'"

                { Kind = kind; Provenance = Provenance.create source reference })
            |> Ok
        with
        | Malformed message -> Error $"malformed observations: {message}"
        | :? JsonException as error -> Error $"malformed observations: {error.Message}"

    // ---- work groups (requirements/PLANNING-WORK-GROUPS.md) ----------------------

    let private signal (value: AffinitySignal) =
        record
            [ "code", text (AffinitySignal.code value)
              "detail", text (AffinitySignal.detail value)
              "basis", text (SignalBasis.code (AffinitySignal.basis value))
              "level", text (ContextAffinity.code (AffinitySignal.level value))
              "confidence", text (EvidenceConfidence.code (AffinitySignal.confidence value))
              "description", text (AffinitySignal.describe value) ]

    let private pairAffinity (value: PairAffinity) =
        record
            [ "left", text value.Left
              "right", text value.Right
              "affinity", text (ContextAffinity.code value.Level)
              "confidence", text (EvidenceConfidence.code value.Confidence)
              "signals", value.Signals |> List.map signal |> array
              "statement", text value.Statement ]

    let private groupNote (value: GroupNote) =
        record
            [ "code", text (GroupNoteCode.code value.Code)
              "severity", text (FindingSeverity.code value.Severity)
              "message", text value.Message ]

    let private workGroup (value: WorkGroup) =
        record
            [ "id", text (WorkGroupId.value value.Id)
              "kind", text (GroupKind.code value.Kind)
              "origin", text (GroupOrigin.code value.Origin)
              "area", text value.Area
              "executionRepository", text value.ExecutionRepository
              "crossRepository", boolean value.CrossRepository
              "affinity", text (ContextAffinity.code value.Affinity)
              "confidence", text (EvidenceConfidence.code value.Confidence)
              "members",
              value.Members
              |> List.map (fun entry ->
                  record
                      [ "workItem", text entry.WorkItemId
                        "reason", text entry.Reason
                        "confidence", text (EvidenceConfidence.code entry.Confidence)
                        "recordedState", text entry.LifecycleState
                        "planningState", text (PlanningWorkState.code entry.PlanningState)
                        "status", text (MemberStatus.code entry.Status)
                        "gatedBy", texts entry.GatedBy ])
              |> array
              "cohesion",
              value.Cohesion
              |> List.map (fun line ->
                  record
                      [ "signal", signal line.Signal
                        "members", texts line.Members
                        "covered", integer line.Covered
                        "total", integer line.Total
                        "statement", text line.Statement ])
              |> array
              "sharedContext", texts value.SharedContext
              "requiredSequence", texts value.RequiredSequence
              "collisionRisk", text (CollisionRisk.code value.Collision)
              "collisions", value.CollisionPairs |> List.map collision |> array
              "parallelSafe", boolean value.ParallelSafe
              "recommendedExecution", text (GroupExecution.code value.Execution)
              "executionReasons", texts value.ExecutionReasons
              "contextCost",
              record
                  [ "independentAcquisitions", integer value.ContextCost.IndependentAcquisitions
                    "groupedAcquisitions", integer value.ContextCost.GroupedAcquisitions
                    "coldStart", duration value.ContextCost.ColdStart
                    "sharedContext", duration value.ContextCost.SharedContext
                    "memberIncremental", duration value.ContextCost.MemberIncremental
                    "estimatedReuse", text "unknown"
                    "statement", text value.ContextCost.Statement ]
              "progress",
              record
                  [ "total", integer value.Progress.Total
                    "complete", integer value.Progress.Complete
                    "inProgress", integer value.Progress.InProgress
                    "runnable", integer value.Progress.Runnable
                    "blocked", integer value.Progress.Blocked
                    "notRunnable", integer value.Progress.NotRunnable
                    "statement", text value.Progress.Statement ]
              "architectureNotes", texts value.ArchitectureNotes
              "notes", value.Notes |> List.map groupNote |> array ]

    let private endpoint (value: GroupEndpoint) =
        record [ "kind", text (GroupEndpoint.kindCode value); "value", text (GroupEndpoint.value value) ]

    let private groupingBody (value: GroupingReport) : (string * JsonNode) list =
        [ "statement", text value.Statement
          "settings",
          record
              [ "preferredSize", array [ integer value.Settings.PreferredMinimumSize; integer value.Settings.PreferredMaximumSize ]
                "maximumAutomaticSize", integer value.Settings.MaximumAutomaticSize
                "minimumAffinity", text (ContextAffinity.code value.Settings.MinimumAffinity)
                "riskPolicy", text value.Settings.RiskPolicy ]
          "groups", value.Groups |> List.map workGroup |> array
          "ungrouped",
          value.Ungrouped
          |> List.map (fun entry ->
              record
                  [ "workItem", text entry.WorkItem
                    "planningState", text (PlanningWorkState.code entry.PlanningState)
                    "affinity", text (ContextAffinity.code entry.Affinity)
                    "reason", text entry.Reason ])
          |> array
          "dependencies",
          value.Dependencies
          |> List.map (fun edge -> record [ "from", endpoint edge.From; "to", endpoint edge.To; "gating", boolean edge.Gating; "via", texts edge.Via ])
          |> array
          "cycles", value.Cycles |> List.map texts |> array
          "relations",
          value.Relations
          |> List.map (fun relation ->
              record
                  [ "left", text relation.Left
                    "right", text relation.Right
                    "collisionRisk", text (CollisionRisk.code relation.Collision)
                    "dependent", boolean relation.Dependent
                    "mayRunConcurrently", boolean relation.MayRunConcurrently
                    "reason", text relation.Reason ])
          |> array
          "affinities", value.Affinities |> List.map pairAffinity |> array
          "unknownEvidence", texts value.UnknownEvidence ]

    /// PRX-GRP-070: the `groups` document.
    let groups (snapshotValue: PlanSnapshot) (value: GroupingReport) : JsonNode =
        record ([ "schema", text schema; "kind", text "groups"; "snapshot", snapshot snapshotValue ] @ groupingBody value)

    /// The groups document without its timestamped snapshot, for determinism checks.
    let logicalGroups (snapshotValue: PlanSnapshot) (value: GroupingReport) : JsonNode =
        record
            ([ "schema", text schema
               "kind", text "groups"
               "workStateFingerprint", text snapshotValue.WorkStateFingerprint
               "inputFingerprint", text snapshotValue.InputFingerprint ]
             @ groupingBody value)

    /// PRX-GRP-071: the `group-explanation` document.
    let groupExplanation (snapshotValue: PlanSnapshot) (value: GroupExplanation) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "group-explanation"
              "snapshot", snapshot snapshotValue
              "group", workGroup value.Group
              "whyTogether", texts value.WhyTogether
              "excluded",
              value.Excluded
              |> List.map (fun entry -> record [ "workItem", text entry.WorkItem; "affinity", text (ContextAffinity.code entry.Affinity); "reason", text entry.Reason ])
              |> array
              "evidence", value.Evidence |> List.map (fun (basis, statement) -> record [ "basis", text (SignalBasis.code basis); "statement", text statement ]) |> array
              "inferred", texts value.Inferred
              "unknown", texts value.Unknown
              "sharedArchitecture", texts value.SharedArchitecture
              "dependencyOrder", texts value.DependencyOrder
              "collisionRisk", texts value.CollisionRisk
              "executionRationale", texts value.ExecutionRationale
              "wouldChange", texts value.WouldChange ]

    let private groupSchedule (value: GroupSchedule) =
        record
            [ "maxConcurrency", integer value.MaxConcurrency
              "riskPolicy", text value.RiskPolicy
              "expectedDuration", duration value.ExpectedDuration
              "contextAcquisitions", integer value.ContextAcquisitions
              "independentContextAcquisitions", integer value.IndependentContextAcquisitions
              "waves",
              value.Waves
              |> List.map (fun wave ->
                  record
                      [ "number", integer wave.Number
                        "expectedDuration", duration wave.ExpectedDuration
                        "units",
                        wave.Units
                        |> List.map (fun entry ->
                            record
                                [ "unit", text entry.Unit
                                  "isGroup", boolean entry.IsGroup
                                  "members", texts entry.Members
                                  "remaining", duration entry.Remaining
                                  "reasons", texts entry.Reasons ])
                        |> array ])
              |> array
              "notScheduled", value.NotScheduled |> List.map (fun (unit, why) -> record [ "unit", text unit; "reason", text why ]) |> array
              "statement", text value.Statement ]

    /// PRX-GRP-072: `simulate --groups`.
    let groupSimulation (snapshotValue: PlanSnapshot) (value: GroupSchedule) : JsonNode =
        record [ "schema", text schema; "kind", text "group-plan"; "snapshot", snapshot snapshotValue; "statement", text Grouping.advisoryStatement; "plan", groupSchedule value ]

    let private arm (value: ArmEstimate) =
        record
            [ "executions", integer value.Executions
              "contextAcquisitions", integer value.ContextAcquisitions
              "expectedDuration", duration value.ExpectedDuration
              "peakConcurrency", integer value.PeakConcurrency
              "designOwners", integer value.DesignOwners ]

    /// PRX-GRP-072: `compare --groups`.
    let groupComparison (snapshotValue: PlanSnapshot) (value: GroupComparison) : JsonNode =
        record
            [ "schema", text schema
              "kind", text "group-comparison"
              "snapshot", snapshot snapshotValue
              "tradeoffs",
              value.Tradeoffs
              |> List.map (fun tradeoff ->
                  record
                      [ "group", text tradeoff.Group
                        "members", texts tradeoff.Members
                        "notYetRunnable", texts tradeoff.NotYetRunnable
                        "independent", arm tradeoff.Independent
                        "grouped", arm tradeoff.Grouped
                        "contextSaving", text tradeoff.ContextSaving
                        "architectureConsideration", text tradeoff.ArchitectureConsideration
                        "contextPressureRisk", text tradeoff.ContextPressureRisk ])
              |> array
              "portfolio", groupSchedule value.Portfolio
              "itemPlan", record [ "strategy", text "speed"; "expectedDuration", duration value.ItemPlanDuration; "contextAcquisitions", integer value.ItemPlanContextAcquisitions ]
              "statement", text value.Statement ]

    // ---- parsing the groups document (round trip, PRX-GRP-090 case 16) ------------

    let private readSignal (node: JsonObject) : AffinitySignal =
        let basis = readText node "basis" |> parsed "signal basis" SignalBasis.tryParse
        let code = readText node "code"
        AffinitySignal.tryParse code (readText node "detail") basis |> Option.defaultWith (fun () -> fail $"unknown affinity signal '{code}'")

    let private readAffinity node name = readText node name |> parsed "affinity" ContextAffinity.tryParse

    let private readCollision (node: JsonObject) : Collision =
        { Left = readText node "left"
          Right = readText node "right"
          Risk = readText node "risk" |> parsed "collision risk" CollisionRisk.tryParse
          Signals =
            objects node "signals"
            |> List.map (fun entry ->
                let code = readText entry "code"
                CollisionSignal.tryParse code (readText entry "detail") |> Option.defaultWith (fun () -> fail $"unknown collision signal '{code}'")) }

    let private readEndpoint (node: JsonObject) =
        let kind = readText node "kind"
        GroupEndpoint.tryParse kind (readText node "value") |> Option.defaultWith (fun () -> fail $"unknown endpoint kind '{kind}'")

    let private readWorkGroup (node: JsonObject) : WorkGroup =
        let cost = obj node "contextCost"
        let progress = obj node "progress"

        { Id = WorkGroupId(readText node "id")
          Kind = readText node "kind" |> parsed "group kind" GroupKind.tryParse
          Origin = readText node "origin" |> parsed "group origin" GroupOrigin.tryParse
          Area = readText node "area"
          ExecutionRepository = readText node "executionRepository"
          CrossRepository = readBool node "crossRepository"
          Affinity = readAffinity node "affinity"
          Confidence = readConfidence node "confidence"
          Members =
            objects node "members"
            |> List.map (fun entry ->
                { WorkItemId = readText entry "workItem"
                  Reason = readText entry "reason"
                  Confidence = readConfidence entry "confidence"
                  LifecycleState = readText entry "recordedState"
                  PlanningState = readText entry "planningState" |> parsed "planning state" PlanningWorkState.tryParse
                  Status = readText entry "status" |> parsed "member status" MemberStatus.tryParse
                  GatedBy = readTexts entry "gatedBy" })
          Cohesion =
            objects node "cohesion"
            |> List.map (fun line ->
                { Signal = obj line "signal" |> readSignal
                  Members = readTexts line "members"
                  Covered = readRequired<int> line "covered"
                  Total = readRequired<int> line "total"
                  Statement = readText line "statement" })
          SharedContext = readTexts node "sharedContext"
          RequiredSequence = readTexts node "requiredSequence"
          Collision = readText node "collisionRisk" |> parsed "collision risk" CollisionRisk.tryParse
          CollisionPairs = objects node "collisions" |> List.map readCollision
          ParallelSafe = readBool node "parallelSafe"
          Execution = readText node "recommendedExecution" |> parsed "group execution" GroupExecution.tryParse
          ExecutionReasons = readTexts node "executionReasons"
          ContextCost =
            { IndependentAcquisitions = readRequired<int> cost "independentAcquisitions"
              GroupedAcquisitions = readRequired<int> cost "groupedAcquisitions"
              ColdStart = obj cost "coldStart" |> readDuration
              SharedContext = obj cost "sharedContext" |> readDuration
              MemberIncremental = obj cost "memberIncremental" |> readDuration
              Statement = readText cost "statement" }
          Progress =
            { Total = readRequired<int> progress "total"
              Complete = readRequired<int> progress "complete"
              InProgress = readRequired<int> progress "inProgress"
              Runnable = readRequired<int> progress "runnable"
              Blocked = readRequired<int> progress "blocked"
              NotRunnable = readRequired<int> progress "notRunnable"
              Statement = readText progress "statement" }
          ArchitectureNotes = readTexts node "architectureNotes"
          Notes =
            objects node "notes"
            |> List.map (fun entry ->
                { Code = readText entry "code" |> parsed "group note" GroupNoteCode.tryParse
                  Severity = readText entry "severity" |> parsed "severity" FindingSeverity.tryParse
                  Message = readText entry "message" }) }

    /// Reads a document written by `groups`; refuses another schema or kind.
    let parseGroups (json: string) : Result<PlanSnapshot * GroupingReport, string> =
        try
            let root = JsonNode.Parse json |> asObject "document"

            match readText root "schema", readText root "kind" with
            | version, _ when version <> schema -> Error $"unsupported plan schema '{version}' (expected '{schema}')"
            | _, kind when kind <> "groups" -> Error $"expected a groups document, found '{kind}'"
            | _ ->
                let settings = obj root "settings"

                let minimum, maximum =
                    match field settings "preferredSize" with
                    | :? JsonArray as values when values.Count = 2 -> values[0].GetValue<int>(), values[1].GetValue<int>()
                    | _ -> fail "preferredSize must be [minimum, maximum]"

                Ok(
                    obj root "snapshot" |> readSnapshot,
                    { Statement = readText root "statement"
                      Settings =
                        { PreferredMinimumSize = minimum
                          PreferredMaximumSize = maximum
                          MaximumAutomaticSize = readRequired<int> settings "maximumAutomaticSize"
                          MinimumAffinity = readAffinity settings "minimumAffinity"
                          RiskPolicy = readText settings "riskPolicy" }
                      Groups = objects root "groups" |> List.map readWorkGroup
                      Ungrouped =
                        objects root "ungrouped"
                        |> List.map (fun entry ->
                            ({ WorkItem = readText entry "workItem"
                               PlanningState = readText entry "planningState" |> parsed "planning state" PlanningWorkState.tryParse
                               Affinity = readAffinity entry "affinity"
                               Reason = readText entry "reason" }
                            : UngroupedItem))
                      Dependencies =
                        objects root "dependencies"
                        |> List.map (fun edge ->
                            { From = obj edge "from" |> readEndpoint
                              To = obj edge "to" |> readEndpoint
                              Gating = readBool edge "gating"
                              Via = readTexts edge "via" })
                      Cycles =
                        items root "cycles"
                        |> List.map (function
                            | :? JsonArray as cycle -> cycle |> Seq.map (fun value -> value.GetValue<string>()) |> Seq.toList
                            | _ -> fail "cycles must contain arrays")
                      Relations =
                        objects root "relations"
                        |> List.map (fun relation ->
                            ({ Left = readText relation "left"
                               Right = readText relation "right"
                               Collision = readText relation "collisionRisk" |> parsed "collision risk" CollisionRisk.tryParse
                               Dependent = readBool relation "dependent"
                               MayRunConcurrently = readBool relation "mayRunConcurrently"
                               Reason = readText relation "reason" }
                            : GroupRelation))
                      Affinities =
                        objects root "affinities"
                        |> List.map (fun pair ->
                            ({ Left = readText pair "left"
                               Right = readText pair "right"
                               Level = readAffinity pair "affinity"
                               Confidence = readConfidence pair "confidence"
                               Signals = objects pair "signals" |> List.map readSignal
                               Statement = readText pair "statement" }
                            : PairAffinity))
                      UnknownEvidence = readTexts root "unknownEvidence" }
                )
        with
        | Malformed message -> Error $"malformed groups document: {message}"
        | :? JsonException as error -> Error $"malformed groups document: {error.Message}"
        | :? InvalidOperationException as error -> Error $"malformed groups document: {error.Message}"

    // ---- stored group declarations (.ros/work/groups.json, PRX-GRP-073) -------

    let groupStoreSchemaVersion = "1.0.0"

    let private memberCheckpoint (entry: MemberCheckpoint) : JsonNode =
        match entry.Reference with
        | MemberCheckpointReference.Latest(checkpointId, commit, recordedAt) ->
            record
                [ "workItem", text entry.WorkItem
                  "status", text "recorded"
                  "checkpointId", text checkpointId
                  "commit", text commit
                  "recordedAt", text recordedAt ]
        | MemberCheckpointReference.NoneRecorded -> record [ "workItem", text entry.WorkItem; "status", text "none" ]
        | MemberCheckpointReference.Unreadable problems ->
            record [ "workItem", text entry.WorkItem; "status", text "unreadable"; "problems", texts problems ]

    /// One group checkpoint (PRX-GRP-044). It names members' own checkpoints
    /// and carries no paths: it claims no member's changes (PRX-GRP-043).
    let groupCheckpoint (value: GroupCheckpoint) : JsonNode =
        record
            [ "checkpointId", text value.CheckpointId
              "recordedAt", text value.RecordedAt
              "recordedBy", text value.RecordedBy
              "summary", text value.Summary
              "nextAction", text value.NextAction
              "sharedDecisions", texts value.SharedDecisions
              "members",
              record
                  [ "active", texts value.Members.Active
                    "completed", texts value.Members.Completed
                    "remaining", texts value.Members.Remaining
                    "abandoned", texts value.Members.Abandoned
                    "unknown", texts value.Members.Unknown ]
              "location",
              record
                  [ "repository", text value.Location.Repository
                    "branch", text value.Location.Branch
                    "commit", text value.Location.Commit
                    "remote", text value.Location.Remote
                    "remoteUrl", optionalText value.Location.RemoteUrl
                    "remoteBranch", text value.Location.RemoteBranch ]
              "memberCheckpoints", value.MemberCheckpoints |> List.map memberCheckpoint |> array ]

    /// One declaration, with the keys `grouping.groups` reads, plus who
    /// declared it and when, who added each later member, and who removed any.
    let storedGroup (value: StoredGroup) : JsonNode =
        let group = value.Group

        record
            [ "id", text group.Id
              "members", texts group.Members
              "kind", group.Kind |> Option.map GroupKind.code |> optionalText
              "origin", text (GroupOrigin.code group.Origin)
              "sharedContext", texts group.SharedContext
              "executionRepository", optionalText group.ExecutionRepository
              "crossRepository", boolean group.CrossRepository
              "architectureNotes", texts group.ArchitectureNotes
              "declaredAt", text value.DeclaredAt
              "declaredBy", text value.DeclaredBy
              "additions",
              value.Additions
              |> List.map (fun addition ->
                  record
                      [ "workItem", text addition.WorkItem
                        "addedAt", text addition.AddedAt
                        "addedBy", text addition.AddedBy ])
              |> array
              "removals",
              value.Removals
              |> List.map (fun removal ->
                  record
                      [ "workItem", text removal.WorkItem
                        "removedAt", text removal.RemovedAt
                        "removedBy", text removal.RemovedBy
                        "reason", optionalText removal.Reason ])
              |> array
              "checkpoints", value.Checkpoints |> List.map groupCheckpoint |> array ]

    /// `work group show`: one declaration with its members' own states and
    /// partial-completion progress. Unavailable values are null, never zero.
    let groupView (value: GroupView) : JsonNode =
        record
            [ "group", storedGroup value.Declaration
              "executionRepository",
              record
                  [ "name", optionalText value.ExecutionRepository
                    "basis", text (RepositoryBasis.code value.RepositoryBasis) ]
              "members",
              value.Members
              |> List.map (fun entry ->
                  record
                      [ "workItem", text entry.WorkItemId
                        "recordedState", optionalText entry.RecordedState
                        "planningState", entry.PlanningState |> Option.map PlanningWorkState.code |> optionalText
                        "status", entry.Status |> Option.map MemberStatus.code |> optionalText
                        "gatedBy", texts entry.GatedBy
                        "gates", texts entry.Gates ])
              |> array
              "progress",
              record
                  [ "total", integer value.Progress.Total
                    "complete", integer value.Progress.Complete
                    "inProgress", integer value.Progress.InProgress
                    "runnable", integer value.Progress.Runnable
                    "blocked", integer value.Progress.Blocked
                    "notRunnable", integer value.Progress.NotRunnable
                    "unknown", integer value.Progress.Unknown
                    "statement", text value.Progress.Statement ]
              "blocked",
              value.Blocked
              |> List.map (fun entry -> record [ "workItem", text entry.WorkItemId; "gates", texts entry.Gates ])
              |> array
              "architectureNotes", texts value.Declaration.Group.ArchitectureNotes
              "plannerNotes", value.PlannerNotes |> List.map groupNote |> array
              "unavailable", texts value.Unavailable ]

    /// The whole store, ordered by group ID so it renders deterministically.
    let renderGroupStore (groups: StoredGroup list) : string =
        record
            [ "schemaVersion", text groupStoreSchemaVersion
              "groups",
              groups
              |> List.sortWith (fun left right -> String.CompareOrdinal(left.Group.Id, right.Group.Id))
              |> List.map storedGroup
              |> array ]
        |> render

    let private readMemberCheckpoint (node: JsonObject) : MemberCheckpoint =
        { WorkItem = readText node "workItem"
          Reference =
            match readText node "status" with
            | "recorded" ->
                MemberCheckpointReference.Latest(readText node "checkpointId", readText node "commit", readText node "recordedAt")
            | "none" -> MemberCheckpointReference.NoneRecorded
            | "unreadable" -> MemberCheckpointReference.Unreadable(readTexts node "problems")
            | other -> fail $"unknown member checkpoint status '{other}'" }

    let private readGroupCheckpoint (node: JsonObject) : GroupCheckpoint =
        let members = field node "members" |> asObject "members"
        let location = field node "location" |> asObject "location"

        { CheckpointId = readText node "checkpointId"
          RecordedAt = readText node "recordedAt"
          RecordedBy = readText node "recordedBy"
          Summary = readText node "summary"
          NextAction = readText node "nextAction"
          SharedDecisions = readTexts node "sharedDecisions"
          Members =
            { Active = readTexts members "active"
              Completed = readTexts members "completed"
              Remaining = readTexts members "remaining"
              Abandoned = readTexts members "abandoned"
              Unknown = readTexts members "unknown" }
          Location =
            { Repository = readText location "repository"
              Branch = readText location "branch"
              Commit = readText location "commit"
              Remote = readText location "remote"
              RemoteUrl = readOptionalText location "remoteUrl"
              RemoteBranch = readText location "remoteBranch" }
          MemberCheckpoints = objects node "memberCheckpoints" |> List.map readMemberCheckpoint }

    let parseGroupStore (json: string) : Result<StoredGroup list, string> =
        try
            let root = JsonNode.Parse json |> asObject "group store"

            match readText root "schemaVersion" with
            | version when version <> groupStoreSchemaVersion -> fail $"unsupported schemaVersion '{version}'"
            | _ ->
                objects root "groups"
                |> List.map (fun node ->
                    { Group = readDeclaredGroup node
                      DeclaredAt = readText node "declaredAt"
                      DeclaredBy = readText node "declaredBy"
                      // Absent in declarations written before `work group add`.
                      Additions =
                        match field node "additions" with
                        | null -> []
                        | _ ->
                            objects node "additions"
                            |> List.map (fun addition ->
                                { WorkItem = readText addition "workItem"
                                  AddedAt = readText addition "addedAt"
                                  AddedBy = readText addition "addedBy" })
                      // Absent in declarations written before `work group remove`.
                      Removals =
                        match field node "removals" with
                        | null -> []
                        | _ ->
                            objects node "removals"
                            |> List.map (fun removal ->
                                { WorkItem = readText removal "workItem"
                                  RemovedAt = readText removal "removedAt"
                                  RemovedBy = readText removal "removedBy"
                                  Reason = readOptionalText removal "reason" })
                      // Absent in declarations written before `work group checkpoint`.
                      Checkpoints =
                        match field node "checkpoints" with
                        | null -> []
                        | _ -> objects node "checkpoints" |> List.map readGroupCheckpoint })
                |> Ok
        with
        | Malformed message -> Error $"malformed group store: {message}"
        | :? JsonException as error -> Error $"malformed group store: {error.Message}"
