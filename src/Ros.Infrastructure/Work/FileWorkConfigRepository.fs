namespace Ros.Infrastructure.Work

open System.IO
open System.Text.Json
open Ros.Domain.Work

/// Reads the same `ros.json` `workProtocol.*` fields production's
/// `workConfig` reads, with the same defaults when the file or fields are
/// absent. Read-only: never writes `ros.json`.
[<RequireQualifiedAccess>]
module FileWorkConfigRepository =
    let private stringArray (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun entry -> if entry.ValueKind = JsonValueKind.String then Some(entry.GetString()) else None)
            |> Seq.toList
            |> Some
        | _ -> None

    let private readWorkProtocol (root: string) : JsonElement option =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "workProtocol" with
                | true, value when value.ValueKind = JsonValueKind.Object -> Some(value.Clone())
                | _ -> None
            with _ ->
                None

    /// Reads `<root>/ros.json`'s `workProtocol.meaningfulPaths`/
    /// `.ignoredPaths`, falling back to `PathFilterConfig.defaultConfig` for
    /// a missing file, missing `workProtocol` object, or missing/malformed
    /// field, field by field (matching production's `?? default` per field
    /// rather than an all-or-nothing fallback).
    let readPathFilterConfig (root: string) : PathFilterConfig =
        match readWorkProtocol root with
        | None -> PathFilterConfig.defaultConfig
        | Some element ->
            { MeaningfulPatterns = stringArray element "meaningfulPaths" |> Option.defaultValue PathFilterConfig.defaultConfig.MeaningfulPatterns
              IgnoredPatterns = stringArray element "ignoredPaths" |> Option.defaultValue PathFilterConfig.defaultConfig.IgnoredPatterns }

    /// Reads `workProtocol.enforceAttribution`, defaulting to `false` for a
    /// missing file, missing `workProtocol` object, or a value that is not
    /// the JSON literal `true` — matching production's
    /// `config.workProtocol?.enforceAttribution === true` exactly (any other
    /// value, including the string `"true"`, is not enforcement).
    let readEnforceAttribution (root: string) : bool =
        match readWorkProtocol root with
        | None -> false
        | Some element ->
            match element.TryGetProperty "enforceAttribution" with
            | true, value when value.ValueKind = JsonValueKind.True -> true
            | _ -> false

    let private readTelemetry (root: string) : JsonElement option =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            None
        else
            try
                use document = JsonDocument.Parse(File.ReadAllText path)

                match document.RootElement.TryGetProperty "telemetry" with
                | true, value when value.ValueKind = JsonValueKind.Object -> Some(value.Clone())
                | _ -> None
            with _ ->
                None

    /// Mirrors production `telemetryConfig().enabled`
    /// (`tools/ros_telemetry.mjs`): telemetry is enabled unless the JSON
    /// literal `false` says otherwise -- any other value, including absence
    /// of the field or file, leaves it enabled.
    let readTelemetryEnabled (root: string) : bool =
        match readTelemetry root with
        | None -> true
        | Some element ->
            match element.TryGetProperty "enabled" with
            | true, value when value.ValueKind = JsonValueKind.False -> false
            | _ -> true

    /// Mirrors production `telemetryConfig().executionRoot`, relative to
    /// `root`, defaulting to `.ros/telemetry/executions`.
    let readTelemetryExecutionRoot (root: string) : string =
        match readTelemetry root with
        | None -> ".ros/telemetry/executions"
        | Some element ->
            match element.TryGetProperty "executionRoot" with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> ".ros/telemetry/executions"

    /// Mirrors production `telemetryConfig().metricRegistry`, relative to
    /// `root`, defaulting to `telemetry/metrics.json`.
    let readTelemetryMetricRegistryPath (root: string) : string =
        match readTelemetry root with
        | None -> "telemetry/metrics.json"
        | Some element ->
            match element.TryGetProperty "metricRegistry" with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> "telemetry/metrics.json"

    /// Mirrors production `telemetryConfig().ignoredPaths`: the same
    /// `workProtocol.ignoredPaths` configuration key `readPathFilterConfig`
    /// reads, but with telemetry's own distinct default list (it additionally
    /// ignores `registries/**`, unlike the work-protocol default).
    let readTelemetryIgnoredPaths (root: string) : string list =
        match readWorkProtocol root with
        | Some element ->
            stringArray element "ignoredPaths"
            |> Option.defaultValue
                [ ".git/**"; ".ros/context/**"; ".ros/events/**"; ".ros/work/**"; ".ros/telemetry/**"; ".ros/locks/**"; "registries/**" ]
        | None -> [ ".git/**"; ".ros/context/**"; ".ros/events/**"; ".ros/work/**"; ".ros/telemetry/**"; ".ros/locks/**"; "registries/**" ]

    /// Mirrors production `telemetryConfig().maxCapabilityHistoryEntries`,
    /// defaulting to 64.
    let readTelemetryMaxCapabilityHistoryEntries (root: string) : int =
        match readTelemetry root with
        | None -> 64
        | Some element ->
            match element.TryGetProperty "maxCapabilityHistoryEntries" with
            | true, value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, parsed -> parsed
                | _ -> 64
            | _ -> 64

    /// Mirrors production `workConfig().protocolVersion`:
    /// `config.workProtocol?.version ?? "1.0.0"`.
    let readProtocolVersion (root: string) : string =
        match readWorkProtocol root with
        | None -> "1.0.0"
        | Some element ->
            match element.TryGetProperty "version" with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> "1.0.0"

    /// Mirrors production `workConfig`'s `repository: config.repository?.id
    /// ?? config.name ?? path.basename(root)`: the identity a freshly
    /// synthesized `queue.json` is stamped with when none exists yet.
    let readRepositoryId (root: string) : string =
        let path = Path.Combine(root, "ros.json")

        let fromConfig =
            if not (File.Exists path) then
                None
            else
                try
                    use document = JsonDocument.Parse(File.ReadAllText path)
                    let rootElement = document.RootElement

                    let repositoryId =
                        match rootElement.TryGetProperty "repository" with
                        | true, value when value.ValueKind = JsonValueKind.Object ->
                            match value.TryGetProperty "id" with
                            | true, id when id.ValueKind = JsonValueKind.String -> Some(id.GetString())
                            | _ -> None
                        | _ -> None

                    match repositoryId with
                    | Some _ -> repositoryId
                    | None ->
                        match rootElement.TryGetProperty "name" with
                        | true, name when name.ValueKind = JsonValueKind.String -> Some(name.GetString())
                        | _ -> None
                with _ ->
                    None

        fromConfig |> Option.defaultValue (Path.GetFileName(root.TrimEnd('/', '\\')))
