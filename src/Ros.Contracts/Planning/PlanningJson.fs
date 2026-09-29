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
                        |> List.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right)) }
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
                    | _ -> EvidenceSource.ExternalObservation

                let reference = readOptionalText node "reference" |> Option.defaultValue $"{origin}#observations[{index}]"

                let kind =
                    match readText node "kind" with
                    | "pull-request-merged" -> ObservationKind.PullRequestMerged(readRequired<int> node "pullRequest")
                    | "commit-merged" -> ObservationKind.CommitMerged(readText node "commit", readOptionalText node "into" |> Option.defaultValue "default branch")
                    | "ci-passed" -> ObservationKind.ContinuousIntegrationPassed(readText node "subject")
                    | "ci-failed" -> ObservationKind.ContinuousIntegrationFailed(readText node "subject")
                    | "release-exists" -> ObservationKind.ReleaseExists(readText node "tag")
                    | other -> fail $"unknown observation kind '{other}'"

                { Kind = kind; Provenance = Provenance.create source reference })
            |> Ok
        with
        | Malformed message -> Error $"malformed observations: {message}"
        | :? JsonException as error -> Error $"malformed observations: {error.Message}"
