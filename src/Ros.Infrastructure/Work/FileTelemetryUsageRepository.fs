namespace Ros.Infrastructure.Work

open System.Text.Json.Nodes
open Ros.Domain.Telemetry

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

    type Scope =
        { ExecutionsByKey: Map<string, string list>
          Measurements: UsageMeasurement list }

    let read (root: string) (workItemId: string option) (dimension: UsageDimension) : Scope =
        let additive =
            FileMetricRegistryRepository.read root
            |> List.filter (fun definition -> definition.Aggregation = "sum")
            |> List.map _.Id
            |> Set.ofList

        let records =
            FileTelemetryQueryRepository.readAll root
            |> List.filter (fun record -> workItemId.IsNone || text record "workItemId" = workItemId)

        let executionKeys (record: JsonObject) =
            let identity = child record "identity"
            let executionId = text record "executionId" |> Option.defaultValue ""

            match dimension with
            | UsageDimension.WorkItem -> [ text record "workItemId" |> Option.defaultValue "" ]
            | UsageDimension.Execution -> [ executionId ]
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
            |> List.collect (fun record -> executionKeys record |> List.map (fun key -> key, text record "executionId" |> Option.defaultValue ""))
            |> List.groupBy fst
            |> List.map (fun (key, pairs) -> key, pairs |> List.map snd)
            |> Map.ofList

        let measurements =
            records
            |> List.collect (fun record ->
                let identity = child record "identity"

                match record["metrics"] with
                | :? JsonArray as metrics ->
                    metrics
                    |> Seq.choose (function
                        | :? JsonObject as metric ->
                            match text metric "id", number metric "value" with
                            | Some id, Some value when additive.Contains id ->
                                Some
                                    { ExecutionId = text record "executionId" |> Option.defaultValue ""
                                      WorkItemId = text record "workItemId" |> Option.defaultValue ""
                                      Provider = identity |> Option.bind (fun node -> text node "provider") |> Option.defaultValue "unknown"
                                      Model = identity |> Option.bind (fun node -> text node "model")
                                      Step = child metric "dimensions" |> Option.bind (fun node -> text node "step")
                                      CollectedAt = text metric "collectedAt" |> Option.defaultValue ""
                                      MetricId = id
                                      Value = value
                                      Unit = text metric "unit" |> Option.defaultValue ""
                                      Currency = text metric "currency"
                                      Quality =
                                        EvidenceQuality.classify
                                            (text metric "quality" |> Option.defaultValue "")
                                            (child metric "source" |> Option.bind (fun node -> text node "type") |> Option.defaultValue "") }
                            | _ -> None
                        | _ -> None)
                    |> Seq.toList
                | _ -> [])

        { ExecutionsByKey = executionsByKey
          Measurements = measurements }

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
