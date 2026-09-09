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
