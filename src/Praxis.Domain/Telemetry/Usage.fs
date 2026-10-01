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
        | _, ("ros-git" | "ros-clock" | "environment") -> EvidenceQuality.Measured
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
      Quality: EvidenceQuality }

[<RequireQualifiedAccess>]
type UsageDimension =
    | WorkItem
    | Execution
    | Step
    | Provider
    | Model
    | Day

[<RequireQualifiedAccess>]
module UsageDimension =
    let all = [ UsageDimension.WorkItem; UsageDimension.Execution; UsageDimension.Step; UsageDimension.Provider; UsageDimension.Model; UsageDimension.Day ]

    let code dimension =
        match dimension with
        | UsageDimension.WorkItem -> "work-item"
        | UsageDimension.Execution -> "execution"
        | UsageDimension.Step -> "step"
        | UsageDimension.Provider -> "provider"
        | UsageDimension.Model -> "model"
        | UsageDimension.Day -> "day"

    let tryParse (value: string) = all |> List.tryFind (fun dimension -> code dimension = value)

type UsageGroup =
    { Key: string
      MetricId: string
      Unit: string
      Currency: string option
      /// `None` when no execution in the group reported the metric: unknown,
      /// never zero.
      Total: float option
      Measurements: int
      /// Executions in this group that recorded the metric.
      ReportingExecutions: string list
      /// Executions in this group with no measurement of the metric: their
      /// usage is unknown, so `Total` is a lower bound, never "zero for them".
      UnavailableExecutions: string list
      Qualities: (string * int) list }

/// Aggregates additive usage (`PRX-REMOTE-010`) by one dimension while
/// keeping the evidence behind each total: how many measurements, of which
/// quality, and which executions reported nothing.
[<RequireQualifiedAccess>]
module Usage =
    /// The step-dimension key of execution-scoped usage: recorded before
    /// step tracking was adopted, or outside any step. Never split among steps.
    let outsideAnyStep = "(outside any step)"

    let private keyOf dimension (measurement: UsageMeasurement) =
        match dimension with
        | UsageDimension.WorkItem -> measurement.WorkItemId
        | UsageDimension.Execution -> measurement.ExecutionId
        | UsageDimension.Step -> measurement.Step |> Option.defaultValue outsideAnyStep
        | UsageDimension.Provider -> measurement.Provider
        | UsageDimension.Model -> measurement.Model |> Option.defaultValue "unknown"
        | UsageDimension.Day -> if measurement.CollectedAt.Length >= 10 then measurement.CollectedAt.Substring(0, 10) else "unknown"

    /// `executionsByKey` names, for each group key, every execution that
    /// belongs to it -- including executions with no measurements at all,
    /// which is how absent telemetry stays visible instead of vanishing.
    let aggregate (dimension: UsageDimension) (executionsByKey: Map<string, string list>) (measurements: UsageMeasurement list) : UsageGroup list =
        let metrics =
            measurements |> List.map (fun measurement -> measurement.MetricId, measurement.Unit, measurement.Currency) |> List.distinct

        let keys =
            (executionsByKey |> Map.toList |> List.map fst) @ (measurements |> List.map (keyOf dimension)) |> List.distinct

        [ for key in keys do
              for metricId, unit, currency in metrics do
                  let group =
                      measurements
                      |> List.filter (fun measurement ->
                          keyOf dimension measurement = key
                          && measurement.MetricId = metricId
                          && measurement.Unit = unit
                          && measurement.Currency = currency)

                  let reporting = group |> List.map _.ExecutionId |> List.distinct |> List.sort

                  yield
                      { Key = key
                        MetricId = metricId
                        Unit = unit
                        Currency = currency
                        Total = if group.IsEmpty then None else Some(group |> List.sumBy _.Value)
                        Measurements = group.Length
                        ReportingExecutions = reporting
                        UnavailableExecutions =
                          executionsByKey
                          |> Map.tryFind key
                          |> Option.defaultValue []
                          |> List.filter (fun execution -> not (List.contains execution reporting))
                          |> List.distinct
                          |> List.sort
                        Qualities =
                          group
                          |> List.countBy (fun measurement -> EvidenceQuality.code measurement.Quality)
                          |> List.sortBy fst } ]
        |> List.sortWith (fun left right ->
            match String.CompareOrdinal(left.Key, right.Key) with
            | 0 -> String.CompareOrdinal(left.MetricId, right.MetricId)
            | order -> order)
