namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json
open Ros.Domain.Telemetry

/// Reads the same `telemetry/metrics.json` registry production's
/// `loadMetricRegistry` reads, reduced to the fields a real telemetry
/// effect needs (`id`, `unit`, `aggregation`, `collection`) -- category and
/// description are display-only in production and are not modeled here.
/// Read-only: never writes the registry.
[<RequireQualifiedAccess>]
module FileMetricRegistryRepository =
    let read (root: string) : MetricDefinition list =
        let path = Path.Combine(root, FileWorkConfigRepository.readTelemetryMetricRegistryPath root)

        if not (File.Exists path) then
            []
        else
            use document = JsonDocument.Parse(File.ReadAllText path)

            match document.RootElement.TryGetProperty "metrics" with
            | true, metrics when metrics.ValueKind = JsonValueKind.Array ->
                metrics.EnumerateArray()
                |> Seq.choose (fun metric ->
                    match
                        metric.TryGetProperty "id",
                        metric.TryGetProperty "unit",
                        metric.TryGetProperty "aggregation",
                        metric.TryGetProperty "collection"
                    with
                    | (true, id), (true, unit), (true, aggregation), (true, collection) when
                        id.ValueKind = JsonValueKind.String
                        && unit.ValueKind = JsonValueKind.String
                        && aggregation.ValueKind = JsonValueKind.String
                        && collection.ValueKind = JsonValueKind.String
                        ->
                        Some
                            { Id = id.GetString()
                              Unit = unit.GetString()
                              Aggregation = aggregation.GetString()
                              Collection = collection.GetString() }
                    | _ -> None)
                |> Seq.toList
            | _ -> []
