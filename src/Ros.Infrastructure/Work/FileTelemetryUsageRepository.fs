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
                FileTelemetryFinalizationRepository.stepEvents record |> Steps.project |> List.map _.StepId

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
