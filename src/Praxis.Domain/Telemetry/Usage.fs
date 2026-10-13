namespace Praxis.Domain.Telemetry

open System

/// Usage and cost evidence quality (PRAXIS-REMOTE-04, `PRX-REMOTE-009`):
/// the #90 vocabulary projected from what every measurement already
/// records -- its `quality` and its `source.type` -- rather than a second,
/// competing field. The projection never upgrades a value: a number an
/// agent or a runtime reported stays reported, and only Praxis's own
/// observations are "measured".
[<RequireQualifiedAccess>]
type EvidenceQuality =
    | Measured
    | ProviderReported
    | AgentReported
    | HumanReported
    | Calculated
    | Estimated
    | Unclassified

[<RequireQualifiedAccess>]
module EvidenceQuality =
    let code quality =
        match quality with
        | EvidenceQuality.Measured -> "measured"
        | EvidenceQuality.ProviderReported -> "provider-reported"
        | EvidenceQuality.AgentReported -> "agent-reported"
        | EvidenceQuality.HumanReported -> "human-reported"
        | EvidenceQuality.Calculated -> "calculated"
        | EvidenceQuality.Estimated -> "estimated"
        | EvidenceQuality.Unclassified -> "unclassified"

    /// `estimated` always wins (a guess stays a guess, whatever its source);
    /// then a calculated source or a derived value; then who reported it.
    let classify (quality: string) (sourceType: string) =
        match quality, sourceType with
        | "estimated", _ -> EvidenceQuality.Estimated
        | _, "calculated"
        | "derived", _ -> EvidenceQuality.Calculated
        | _, "agent-report" -> EvidenceQuality.AgentReported
        | _, "human-report" -> EvidenceQuality.HumanReported
        | _, ("runtime-api" | "runtime-output" | "runtime-hook" | "external-tool") -> EvidenceQuality.ProviderReported
        | _, ("ros-git" | "ros-clock" | "environment" | "host") -> EvidenceQuality.Measured
        | _ -> EvidenceQuality.Unclassified

/// One additive measurement with everything usage is aggregated by.
type UsageMeasurement =
    { ExecutionId: string
      WorkItemId: string
      Provider: string
      Model: string option
      Step: string option
      CollectedAt: string
      MetricId: string
      Value: float
      Unit: string
      Currency: string option
      Quality: EvidenceQuality
      /// The execution's ancestors, nearest first: an `execution-tree`
      /// rollup counts the measurement in each of them once.
      Lineage: string list
      /// For a monetary measurement, `CostBasis.code`: provider-reported
      /// and estimated amounts are separate totals, never summed together.
      CostBasis: string option
      /// `Some "host"` for a host-measured value (source type `host`):
      /// host compute is its own total, never added to the agent's.
      Origin: string option
      /// A lower bound rather than a final count (`dimensions.bound`).
      LowerBound: bool }

[<RequireQualifiedAccess>]
type UsageDimension =
    | WorkItem
    | Execution
    | Step
    | Provider
    | Model
    | Day
    /// An execution with every descendant: a parent rollup. Requests are
    /// attributed once (`RequestAttribution`), so the rollup never counts a
    /// child's usage twice.
    | ExecutionTree

[<RequireQualifiedAccess>]
module UsageDimension =
    let all =
        [ UsageDimension.WorkItem
          UsageDimension.Execution
          UsageDimension.ExecutionTree
          UsageDimension.Step
          UsageDimension.Provider
          UsageDimension.Model
          UsageDimension.Day ]

    let code dimension =
        match dimension with
        | UsageDimension.WorkItem -> "work-item"
        | UsageDimension.Execution -> "execution"
        | UsageDimension.Step -> "step"
        | UsageDimension.Provider -> "provider"
        | UsageDimension.Model -> "model"
        | UsageDimension.Day -> "day"
        | UsageDimension.ExecutionTree -> "execution-tree"

    let tryParse (value: string) = all |> List.tryFind (fun dimension -> code dimension = value)

type UsageGroup =
    { Key: string
      MetricId: string
      Unit: string
      Currency: string option
      CostBasis: string option
      Origin: string option
      /// Some measurement in the total is a lower bound, so the total is too.
      LowerBound: bool
      /// `None` when no execution in the group reported the metric: unknown,
      /// never zero.
      Total: float option
      Measurements: int
      /// Executions in this group that recorded the metric.
      ReportingExecutions: string list
      /// Executions in this group with no measurement of the metric: their
      /// usage is unknown, so `Total` is a lower bound, never "zero for them".
      UnavailableExecutions: string list
      Qualities: (string * int) list
      /// Why each unavailable execution has no value: (execution, capability
      /// status, reason), when its record states one.
      UnavailableReasons: (string * string * string) list }

/// A metric a report always shows, measured or not, so an unknown value is
/// printed as unknown instead of disappearing.
type RequiredUsageMetric =
    { MetricId: string
      Unit: string
      Currency: string option
      CostBasis: string option }

/// Aggregates additive usage (`PRX-REMOTE-010`) by one dimension while
/// keeping the evidence behind each total: how many measurements, of which
/// quality, and which executions reported nothing.
[<RequireQualifiedAccess>]
module Usage =
    /// The step-dimension key of execution-scoped usage: recorded before
    /// step tracking was adopted, or outside any step. Never split among steps.
    let outsideAnyStep = "(outside any step)"

    let private keysOf dimension (measurement: UsageMeasurement) =
        match dimension with
        | UsageDimension.WorkItem -> [ measurement.WorkItemId ]
        | UsageDimension.Execution -> [ measurement.ExecutionId ]
        | UsageDimension.ExecutionTree -> measurement.ExecutionId :: measurement.Lineage
        | UsageDimension.Step -> [ measurement.Step |> Option.defaultValue outsideAnyStep ]
        | UsageDimension.Provider -> [ measurement.Provider ]
        | UsageDimension.Model -> [ measurement.Model |> Option.defaultValue "unknown" ]
        | UsageDimension.Day -> [ (if measurement.CollectedAt.Length >= 10 then measurement.CollectedAt.Substring(0, 10) else "unknown") ]

    /// `executionsByKey` names, for each group key, every execution that
    /// belongs to it -- including executions with no measurements at all,
    /// which is how absent telemetry stays visible instead of vanishing.
    /// `required` metrics are reported for every key even when nothing was
    /// measured (total `None`); `reasons` maps (execution, metric) to the
    /// capability status and reason its record gives for having no value.
    let aggregateWith
        (dimension: UsageDimension)
        (executionsByKey: Map<string, string list>)
        (required: RequiredUsageMetric list)
        (reasons: Map<string * string, string * string>)
        (measurements: UsageMeasurement list)
        : UsageGroup list =
        let metrics =
            (measurements |> List.map (fun measurement -> measurement.MetricId, measurement.Unit, measurement.Currency, measurement.CostBasis, measurement.Origin))
            @ (required |> List.map (fun metric -> metric.MetricId, metric.Unit, metric.Currency, metric.CostBasis, None))
            |> List.distinct

        let keyed = measurements |> List.collect (fun measurement -> keysOf dimension measurement |> List.map (fun key -> key, measurement))

        let keys =
            (executionsByKey |> Map.toList |> List.map fst) @ (keyed |> List.map fst) |> List.distinct

        [ for key in keys do
              for metricId, unit, currency, basis, origin in metrics do
                  let group =
                      keyed
                      |> List.filter (fun (measurementKey, measurement) ->
                          measurementKey = key
                          && measurement.MetricId = metricId
                          && measurement.Unit = unit
                          && measurement.Currency = currency
                          && measurement.CostBasis = basis
                          && measurement.Origin = origin)
                      |> List.map snd

                  let reporting = group |> List.map _.ExecutionId |> List.distinct |> List.sort

                  let unavailable =
                      executionsByKey
                      |> Map.tryFind key
                      |> Option.defaultValue []
                      |> List.filter (fun execution -> not (List.contains execution reporting))
                      |> List.distinct
                      |> List.sort

                  yield
                      { Key = key
                        MetricId = metricId
                        Unit = unit
                        Currency = currency
                        CostBasis = basis
                        Origin = origin
                        LowerBound = group |> List.exists _.LowerBound
                        Total = if group.IsEmpty then None else Some(group |> List.sumBy _.Value)
                        Measurements = group.Length
                        ReportingExecutions = reporting
                        UnavailableExecutions = unavailable
                        Qualities =
                          group
                          |> List.countBy (fun measurement -> EvidenceQuality.code measurement.Quality)
                          |> List.sortBy fst
                        UnavailableReasons =
                          unavailable
                          |> List.choose (fun execution ->
                              reasons |> Map.tryFind (execution, metricId) |> Option.map (fun (status, reason) -> execution, status, reason)) } ]
        |> List.sortWith (fun left right ->
            match String.CompareOrdinal(left.Key, right.Key) with
            | 0 ->
                match String.CompareOrdinal(left.MetricId, right.MetricId) with
                | 0 -> compare (left.CostBasis, left.Origin) (right.CostBasis, right.Origin)
                | order -> order
            | order -> order)

    let aggregate (dimension: UsageDimension) (executionsByKey: Map<string, string list>) (measurements: UsageMeasurement list) : UsageGroup list =
        aggregateWith dimension executionsByKey [] Map.empty measurements
