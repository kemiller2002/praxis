namespace Praxis.Infrastructure.Work

open System.Text.Json.Nodes
open Praxis.Domain.Telemetry

/// Reads additive usage measurements (the registry's `sum` metrics: tokens,
/// cost, and the like) from execution records for the `telemetry usage`
/// report (PRAXIS-REMOTE-04). Read-only. Every execution in scope is listed
/// for its group even when it recorded nothing, so absent telemetry shows
/// up as unavailable rather than disappearing into a total.
[<RequireQualifiedAccess>]
module FileTelemetryUsageRepository =
    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, result when result <> null -> Some result
            | _ -> None
        | _ -> None

    let private number (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<float>() with
            | true, result -> Some result
            | _ -> None
        | _ -> None

    let private child (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as result -> Some result
        | _ -> None

    let private items (node: JsonObject) (name: string) : JsonObject list =
        match node[name] with
        | :? JsonArray as array -> array |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
        | _ -> []

    /// The metrics a usage report always shows, measured or not, so an
    /// unknown value is printed as unknown rather than left out.
    let focusMetrics =
        [ "model.requests"
          "tokens.input"
          "tokens.output"
          "tokens.cache_read"
          "tokens.cache_write"
          "tokens.cache_write_5m"
          "tokens.cache_write_1h"
          "tokens.reasoning"
          "tool.calls"
          "model.request_failures"
          "agent.retries"
          "time.agent_wall_ms"
          "host.compile_ms"
          "host.verify_ms"
          "host.prepare_apply_ms"
          "acceptance.first_pass"
          "acceptance.repaired"
          "acceptance.repair_cycles" ]

    /// The mechanism the `anthropic-claude-session` adapter stamps on its
    /// measurements; a request ledger replaces those token measurements.
    let private transcriptMechanism = "session-transcript-jsonl"

    type RequestAttributionSummary =
        { LedgerExecutions: string list
          AttributedRequests: int
          DuplicateHoldings: int
          Conflicts: AttributionConflict list
          /// Transcript snapshots an ancestor also held: its copy of the
          /// snapshot's other metrics is excluded, the child's is counted.
          DuplicateSnapshotHoldings: int
          /// Executions whose transcript usage has no ledger (raw omitted):
          /// their totals are stored sums that cannot be checked for overlap.
          UnverifiedExecutions: string list }

    type Scope =
        { ExecutionsByKey: Map<string, string list>
          Measurements: UsageMeasurement list
          Required: RequiredUsageMetric list
          Reasons: Map<string * string, string * string>
          Attribution: RequestAttributionSummary
          Acceptance: (string * Result<AcceptanceOutcome, string>) list }

    let private executionIdOf record = text record "executionId" |> Option.defaultValue ""

    let private parentOf (record: JsonObject) =
        child record "identity" |> Option.bind (fun identity -> text identity "parentExecutionId")

    let private ledgersOf (record: JsonObject) : ExecutionLedger list =
        items record "rawTelemetry"
        |> List.choose (fun snapshot ->
            child snapshot "payload"
            |> Option.bind (fun payload ->
                child payload "requestLedger"
                |> Option.filter (fun ledger -> ledger["rows"] :? JsonArray)
                |> Option.map (fun ledger ->
                    { ExecutionId = executionIdOf record
                      SnapshotId = text snapshot "snapshotId" |> Option.defaultValue ""
                      Step = text payload "step"
                      Requests =
                        items ledger "rows"
                        |> List.choose (fun row ->
                            text row "key"
                            |> Option.map (fun key ->
                                let usage =
                                    match row["usage"] with
                                    | :? JsonArray as values ->
                                        values
                                        |> Seq.map (function
                                            | :? JsonValue as value ->
                                                match value.TryGetValue<int64>() with
                                                | true, parsed -> Some parsed
                                                | _ -> None
                                            | _ -> None)
                                        |> Seq.toList
                                    | _ -> []

                                { Key = key
                                  Model = text row "model"
                                  AgentId = text row "agentId"
                                  Values = RequestLedger.values usage })) })))

    let private hasTranscriptUsage (record: JsonObject) =
        items record "metrics"
        |> List.exists (fun metric ->
            (text metric "id" |> Option.exists (fun id -> List.contains id RequestLedger.attributedMetricIds))
            && (child metric "source" |> Option.bind (fun source -> text source "mechanism")) = Some transcriptMechanism)

    let private pricingOf (metric: JsonObject) : PriceTableRef option =
        child metric "pricing"
        |> Option.bind (fun pricing ->
            match text pricing "source", text pricing "version" with
            | Some source, Some version ->
                Some
                    { Source = source
                      Version = version
                      EffectiveDate = text pricing "effectiveAt" |> Option.orElse (text pricing "effectiveDate") }
            | _ -> None)

    /// Reads the usage scope. `priceTable`, when supplied, adds an
    /// `estimated` `cost.execution_total` per attributed cell, priced from
    /// the request ledger; it is never summed with provider-reported cost.
    let readWith (root: string) (workItemId: string option) (dimension: UsageDimension) (priceTable: PriceTable option) : Scope =
        let registry = FileMetricRegistryRepository.read root

        let additive =
            registry
            |> List.filter (fun definition -> definition.Aggregation = "sum")
            |> List.map _.Id
            |> Set.ofList

        let unitOf metricId =
            registry |> List.tryFind (fun definition -> definition.Id = metricId) |> Option.map _.Unit |> Option.defaultValue ""

        let allRecords = FileTelemetryQueryRepository.readAll root

        let records =
            allRecords
            |> List.filter (fun record -> workItemId.IsNone || text record "workItemId" = workItemId)

        let inScope = records |> List.map executionIdOf |> Set.ofList
        let recordById = records |> List.map (fun record -> executionIdOf record, record) |> Map.ofList

        let parents =
            allRecords
            |> List.choose (fun record -> parentOf record |> Option.map (fun parent -> executionIdOf record, parent))
            |> Map.ofList

        let lineage executionId = RequestAttribution.ancestors parents executionId

        // Attribution runs over every ledger in the repository, so a request
        // a child in another work item also holds is still counted once.
        let ledgers = allRecords |> List.collect ledgersOf
        let attribution = RequestAttribution.attribute parents ledgers
        let ledgerExecutions = ledgers |> List.map _.ExecutionId |> Set.ofList

        // The same rule for a transcript's other metrics (tool calls, time,
        // reads): each snapshot is one pseudo-request, owned by the deepest
        // execution that holds it.
        let snapshotOf (metric: JsonObject) = child metric "dimensions" |> Option.bind (fun node -> text node "snapshot")

        let snapshotAttribution =
            allRecords
            |> List.collect (fun record -> items record "metrics" |> List.choose snapshotOf |> List.distinct |> List.map (fun snapshot -> executionIdOf record, snapshot))
            |> List.map (fun (executionId, snapshot) ->
                { ExecutionId = executionId
                  SnapshotId = snapshot
                  Step = None
                  Requests = [ { Key = snapshot; Model = None; AgentId = None; Values = Map.empty } ] })
            |> RequestAttribution.attribute parents

        let snapshotOwner =
            snapshotAttribution.Owned |> List.map (fun (owner, row) -> row.Key, owner.ExecutionId) |> Map.ofList

        let executionKeys (record: JsonObject) =
            let identity = child record "identity"
            let executionId = executionIdOf record

            match dimension with
            | UsageDimension.WorkItem -> [ text record "workItemId" |> Option.defaultValue "" ]
            | UsageDimension.Execution -> [ executionId ]
            | UsageDimension.ExecutionTree -> executionId :: lineage executionId
            | UsageDimension.Provider -> [ identity |> Option.bind (fun node -> text node "provider") |> Option.defaultValue "unknown" ]
            | UsageDimension.Model -> [ identity |> Option.bind (fun node -> text node "model") |> Option.defaultValue "unknown" ]
            | UsageDimension.Day ->
                [ text record "startedAt" |> Option.filter (fun value -> value.Length >= 10) |> Option.map (fun value -> value.Substring(0, 10)) |> Option.defaultValue "unknown" ]
            | UsageDimension.Step ->
                let steps = FileTelemetryFinalizationRepository.stepEvents record |> Steps.project
                // An execution with an execution-scoped period belongs to the
                // outside-any-step group too, so if it reported nothing there
                // it is listed as unavailable rather than counted as zero.
                let outside =
                    if TelemetrySegmentation.derive (text record "startedAt") steps |> TelemetrySegmentation.hasExecutionScopedPeriod then
                        [ Usage.outsideAnyStep ]
                    else
                        []

                (steps |> List.map _.StepId) @ outside

        let executionsByKey =
            records
            |> List.collect (fun record -> executionKeys record |> List.map (fun key -> key, executionIdOf record))
            |> List.groupBy fst
            |> List.map (fun (key, pairs) -> key, pairs |> List.map snd |> List.distinct)
            |> Map.ofList

        let providerOf (record: JsonObject) =
            child record "identity" |> Option.bind (fun node -> text node "provider") |> Option.defaultValue "unknown"

        let stored =
            records
            |> List.collect (fun record ->
                let identity = child record "identity"
                let executionId = executionIdOf record

                items record "metrics"
                |> List.choose (fun metric ->
                    let mechanism = child metric "source" |> Option.bind (fun source -> text source "mechanism")

                    match text metric "id", number metric "value" with
                    | Some id, _ when
                        ledgerExecutions.Contains executionId
                        && List.contains id RequestLedger.attributedMetricIds
                        && mechanism = Some transcriptMechanism
                        ->
                        // Replaced by the attributed ledger cells below.
                        None
                    | Some _, _ when snapshotOf metric |> Option.exists (fun snapshot -> snapshotOwner.TryFind snapshot <> Some executionId) ->
                        // A descendant holds the same transcript and counts it.
                        None
                    | Some id, Some value when additive.Contains id ->
                        let quality = text metric "quality" |> Option.defaultValue ""
                        let currency = text metric "currency"

                        Some
                            { ExecutionId = executionId
                              WorkItemId = text record "workItemId" |> Option.defaultValue ""
                              Provider = providerOf record
                              Model = identity |> Option.bind (fun node -> text node "model")
                              Step = child metric "dimensions" |> Option.bind (fun node -> text node "step")
                              CollectedAt = text metric "collectedAt" |> Option.defaultValue ""
                              MetricId = id
                              Value = value
                              Unit = text metric "unit" |> Option.defaultValue ""
                              Currency = currency
                              Quality =
                                EvidenceQuality.classify quality (child metric "source" |> Option.bind (fun node -> text node "type") |> Option.defaultValue "")
                              Lineage = lineage executionId
                              CostBasis =
                                if unitOf id = "currency" then Some(CostBasis.ofMeasurement quality (pricingOf metric) |> CostBasis.code) else None
                              Origin =
                                match child metric "source" |> Option.bind (fun node -> text node "type") with
                                | Some "host" -> Some "host"
                                | _ -> None
                              LowerBound = (child metric "dimensions" |> Option.bind (fun node -> text node "bound")) = Some "lower" }
                    | _ -> None))

        let cells = RequestAttribution.cells attribution |> List.filter (fun cell -> inScope.Contains cell.ExecutionId)

        let cellMeasurement (cell: RequestAttribution.Cell) metricId (value: float) quality currency basis : UsageMeasurement =
            let record = recordById[cell.ExecutionId]

            { ExecutionId = cell.ExecutionId
              WorkItemId = text record "workItemId" |> Option.defaultValue ""
              Provider = providerOf record
              Model = cell.Model
              Step = cell.Step
              CollectedAt = text record "startedAt" |> Option.defaultValue ""
              MetricId = metricId
              Value = value
              Unit = unitOf metricId
              Currency = currency
              Quality = quality
              Lineage = lineage cell.ExecutionId
              CostBasis = basis
              Origin = None
              LowerBound = RequestLedger.lowerBoundMetricIds.Contains metricId || metricId = "cost.execution_total" }

        let attributed =
            cells
            |> List.collect (fun cell ->
                cellMeasurement cell "model.requests" (float cell.Requests) EvidenceQuality.ProviderReported None None
                :: (cell.Totals |> List.map (fun (metricId, total) -> cellMeasurement cell metricId (float total) EvidenceQuality.ProviderReported None None)))

        let estimates, estimateFailures =
            match priceTable with
            | None -> [], []
            | Some table ->
                let basis = Some(CostBasis.code (CostBasis.Estimated table.Reference))

                let outcomes =
                    attribution.Owned
                    |> List.filter (fun (ledger, _) -> inScope.Contains ledger.ExecutionId)
                    |> List.groupBy (fun (ledger, request) -> ledger.ExecutionId, ledger.Step, request.Model)
                    |> List.map (fun ((executionId, step, model), rows) ->
                        let cell =
                            { RequestAttribution.Cell.ExecutionId = executionId
                              RequestAttribution.Cell.Step = step
                              RequestAttribution.Cell.Model = model
                              RequestAttribution.Cell.Requests = rows.Length
                              RequestAttribution.Cell.Totals = [] }

                        cell, UsageCost.estimate table (rows |> List.map snd))

                (outcomes
                 |> List.choose (fun (cell, outcome) ->
                     match outcome with
                     | Ok amount -> Some(cellMeasurement cell "cost.execution_total" (float amount) EvidenceQuality.Estimated (Some table.Currency) basis)
                     | Error _ -> None)),
                (outcomes
                 |> List.choose (fun (cell, outcome) ->
                     match outcome with
                     | Error reason -> Some((cell.ExecutionId, "cost.execution_total"), ("estimate-unavailable", reason))
                     | Ok _ -> None))

        let measurements = stored @ attributed @ estimates

        let required =
            (focusMetrics
             |> List.map (fun metricId ->
                 { MetricId = metricId
                   Unit = unitOf metricId
                   Currency = None
                   CostBasis = None }))
            @ (if measurements |> List.exists (fun measurement -> measurement.MetricId = "cost.execution_total" && measurement.CostBasis = Some "provider-reported") then
                   []
               else
                   [ { MetricId = "cost.execution_total"
                       Unit = "currency"
                       Currency = None
                       CostBasis = Some "provider-reported" } ])

        let reasons =
            (records
             |> List.collect (fun record ->
                 items record "capabilities"
                 |> List.choose (fun capability ->
                     match text capability "metricId", text capability "status" with
                     | Some metricId, Some status when not (List.contains status [ "supported-observed"; "derived"; "estimated" ]) ->
                         Some((executionIdOf record, metricId), (status, text capability "reason" |> Option.defaultValue ""))
                     | _ -> None)))
            @ estimateFailures
            |> Map.ofList

        let acceptance =
            records
            |> List.map (fun record ->
                let executionId = executionIdOf record

                let total metricId =
                    stored
                    |> List.filter (fun measurement -> measurement.ExecutionId = executionId && measurement.MetricId = metricId)
                    |> function
                        | [] -> None
                        | values -> Some(values |> List.sumBy _.Value |> int64)

                executionId, Acceptance.outcome (total Acceptance.firstPassMetric) (total Acceptance.repairedMetric) (total Acceptance.repairCyclesMetric))
            |> List.sortBy fst

        { ExecutionsByKey = executionsByKey
          Measurements = measurements
          Required = required
          Reasons = reasons
          Attribution =
            { LedgerExecutions = ledgerExecutions |> Set.filter inScope.Contains |> Set.toList
              AttributedRequests = cells |> List.sumBy _.Requests
              DuplicateHoldings = attribution.DuplicateHoldings
              Conflicts =
                attribution.Conflicts @ snapshotAttribution.Conflicts
                |> List.filter (fun conflict -> conflict.Executions |> List.exists inScope.Contains)
              DuplicateSnapshotHoldings = snapshotAttribution.DuplicateHoldings
              UnverifiedExecutions =
                records
                |> List.filter (fun record -> hasTranscriptUsage record && not (ledgerExecutions.Contains(executionIdOf record)))
                |> List.map executionIdOf
                |> List.sort }
          Acceptance = acceptance }

    let read (root: string) (workItemId: string option) (dimension: UsageDimension) : Scope = readWith root workItemId dimension None

    /// How one execution's telemetry is segmented, and where each of its
    /// recorded measurements belongs relative to step adoption.
    type ExecutionSegmentation =
        { ExecutionId: string
          Status: string option
          StartedAt: string option
          Segmentation: TelemetrySegmentation
          Scopes: MeasurementScope list }

    let segmentationOf (record: JsonObject) : ExecutionSegmentation =
        let startedAt = text record "startedAt"
        let segmentation = FileTelemetryFinalizationRepository.stepEvents record |> Steps.project |> TelemetrySegmentation.derive startedAt

        let scopes =
            match record["metrics"] with
            | :? JsonArray as metrics ->
                metrics
                |> Seq.choose (function
                    | :? JsonObject as metric ->
                        let step = child metric "dimensions" |> Option.bind (fun node -> text node "step")
                        Some(TelemetrySegmentation.scopeOf segmentation step (text metric "collectedAt" |> Option.defaultValue ""))
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        { ExecutionId = text record "executionId" |> Option.defaultValue ""
          Status = text record "status"
          StartedAt = startedAt
          Segmentation = segmentation
          Scopes = scopes }

    /// Every execution of the work item, in start order. Read-only.
    let segmentations (root: string) (workItemId: string) : ExecutionSegmentation list =
        FileTelemetryQueryRepository.readAll root
        |> List.filter (fun record -> text record "workItemId" = Some workItemId)
        |> List.map segmentationOf
        |> List.sortBy (fun execution -> execution.StartedAt |> Option.defaultValue "", execution.ExecutionId)

    /// `validate`'s step-reference findings, as (path, field, message):
    /// a metric's `dimensions.step` must name a step its execution started.
    let stepReferenceFindings (root: string) : (string * string * string) list =
        FileTelemetryQueryRepository.readAll root
        |> List.collect (fun record ->
            let executionId = text record "executionId" |> Option.defaultValue ""
            let steps = FileTelemetryFinalizationRepository.stepEvents record |> Steps.project

            let metricSteps =
                match record["metrics"] with
                | :? JsonArray as metrics ->
                    metrics
                    |> Seq.map (function
                        | :? JsonObject as metric -> child metric "dimensions" |> Option.bind (fun node -> text node "step")
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            TelemetrySegmentation.danglingStepReferences steps metricSteps
            |> List.map (fun (index, stepId) ->
                $".ros/telemetry/executions/{executionId}.json",
                $"metrics[{index}].dimensions.step",
                $"metric is attributed to step '{stepId}', which execution '{executionId}' never started"))
