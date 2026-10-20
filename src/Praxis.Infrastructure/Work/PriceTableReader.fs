namespace Praxis.Infrastructure.Work

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry

/// Reads an operator-supplied price table for `telemetry usage
/// --price-table FILE`. Praxis ships no prices; the table must name its
/// source, version and effective date so every estimate it produces is
/// auditable. Rates are per million tokens.
///
/// ```json
/// { "source": "provider-list-prices", "version": "2026-10", "effectiveDate": "2026-10-01",
///   "currency": "USD", "unit": "per-million-tokens",
///   "models": { "MODEL": { "input": 0, "output": 0, "cacheRead": 0,
///                          "cacheWrite": 0, "cacheWrite5m": 0, "cacheWrite1h": 0 } } }
/// ```
///
/// `praxis.price-table/1` (PRAXIS-REL-013, F5) adds dated, per-provider
/// entries; `telemetry reliability-report` selects each observation's entry
/// by its timestamp within `[effectiveFrom, effectiveTo)`:
///
/// ```json
/// { "schema": "praxis.price-table/1", "tableVersion": "2026-10-20",
///   "entries": [ { "provider": "anthropic", "model": "MODEL",
///                  "effectiveFrom": "2026-10-01", "effectiveTo": null,
///                  "currency": "USD", "unit": "per-million-tokens",
///                  "input": 0, "output": 0, "cacheRead": 0,
///                  "cacheWrite5m": 0, "cacheWrite1h": 0,
///                  "source": { "url": "https://…", "retrievedAt": "2026-10-20" } } ] }
/// ```
///
/// Both formats stay readable by the reliability report (`parseSchedule`).
[<RequireQualifiedAccess>]
module PriceTableReader =
    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String ->
            Some(value.GetValue<string>()) |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None

    let private rate (node: JsonObject) (name: string) : Result<decimal option, string> =
        match node[name] with
        | null -> Ok None
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<decimal>() with
            | true, parsed when parsed >= 0m -> Ok(Some parsed)
            | _ -> Error $"rate '{name}' must be a non-negative number"
        | _ -> Error $"rate '{name}' must be a number"

    let private prices (model: string) (node: JsonNode) : Result<string * ModelPrices, string> =
        match node with
        | :? JsonObject as rates ->
            let field name = rate rates name |> Result.mapError (fun message -> $"model '{model}': {message}")

            field "input"
            |> Result.bind (fun input ->
                field "output"
                |> Result.bind (fun output ->
                    field "cacheRead"
                    |> Result.bind (fun cacheRead ->
                        field "cacheWrite"
                        |> Result.bind (fun cacheWrite ->
                            field "cacheWrite5m"
                            |> Result.bind (fun cacheWrite5m ->
                                field "cacheWrite1h"
                                |> Result.map (fun cacheWrite1h ->
                                    model,
                                    { Input = input
                                      Output = output
                                      CacheRead = cacheRead
                                      CacheWrite = cacheWrite
                                      CacheWrite5m = cacheWrite5m
                                      CacheWrite1h = cacheWrite1h }))))))
        | _ -> Error $"model '{model}' must map to an object of rates"

    let private isVersioned (table: JsonObject) =
        text table "schema" = Some PriceSchedule.VersionedFormat || not (isNull table["entries"]) || not (isNull table["tableVersion"])

    let private instant (label: string) (raw: string) : Result<DateTimeOffset, string> =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
        | true, parsed -> Ok parsed
        | _ -> Error $"{label} '{raw}' is not a date"

    let parse (json: string) : Result<PriceTable, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as table when isVersioned table ->
                Error "this is a praxis.price-table/1 table: it selects prices by observation time, and telemetry usage requests carry no timestamp; use it with telemetry reliability-report"
            | :? JsonObject as table ->
                let required name =
                    text table name |> Option.map Ok |> Option.defaultValue (Error $"price table requires '{name}'")

                let unitOk =
                    match text table "unit" with
                    | None
                    | Some "per-million-tokens" -> Ok()
                    | Some other -> Error $"price table unit '{other}' is not supported; use 'per-million-tokens'"

                let effectiveDate =
                    required "effectiveDate"
                    |> Result.bind (fun date ->
                        match DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                        | true, _ -> Ok date
                        | _ -> Error $"price table effectiveDate '{date}' is not a date")

                let models =
                    match table["models"] with
                    | :? JsonObject as models when models.Count > 0 ->
                        models
                        |> Seq.toList
                        |> List.fold
                            (fun acc entry -> acc |> Result.bind (fun found -> prices entry.Key entry.Value |> Result.map (fun item -> item :: found)))
                            (Ok [])
                        |> Result.map Map.ofList
                    | _ -> Error "price table requires a non-empty 'models' object"

                unitOk
                |> Result.bind (fun () -> required "source")
                |> Result.bind (fun source ->
                    required "version"
                    |> Result.bind (fun version ->
                        effectiveDate
                        |> Result.bind (fun date ->
                            required "currency"
                            |> Result.bind (fun currency ->
                                models
                                |> Result.map (fun models ->
                                    { Reference =
                                        { Source = source
                                          Version = version
                                          EffectiveDate = Some date }
                                      Currency = currency
                                      Models = models })))))
            | _ -> Error "price table must be a JSON object"
        with :? JsonException as error ->
            Error $"price table is not valid JSON: {error.Message}"

    let private entry (index: int) (node: JsonNode) : Result<PriceEntry, string> =
        match node with
        | :? JsonObject as item ->
            let label = $"entry {index}"
            let required name = text item name |> Option.map Ok |> Option.defaultValue (Error $"{label} requires '{name}'")

            let unitOk =
                match text item "unit" with
                | Some "per-million-tokens" -> Ok()
                | Some other -> Error $"{label}: unit '{other}' is not supported; use 'per-million-tokens'"
                | None -> Error $"{label} requires 'unit' (per-million-tokens)"

            let source = match item["source"] with :? JsonObject as value -> Some value | _ -> None

            let effectiveTo =
                match item["effectiveTo"] with
                | null -> Ok None
                | _ ->
                    match text item "effectiveTo" with
                    | Some raw -> instant $"{label} effectiveTo" raw |> Result.map Some
                    | None -> Error $"{label}: effectiveTo must be a date or null"

            unitOk
            |> Result.bind (fun () -> required "provider")
            |> Result.bind (fun provider ->
                required "model"
                |> Result.bind (fun model ->
                    required "effectiveFrom"
                    |> Result.bind (instant $"{label} effectiveFrom")
                    |> Result.bind (fun effectiveFrom ->
                        effectiveTo
                        |> Result.bind (fun effectiveTo ->
                            required "currency"
                            |> Result.bind (fun currency ->
                                match source |> Option.bind (fun value -> text value "url"), source |> Option.bind (fun value -> text value "retrievedAt") with
                                | Some url, Some retrievedAt ->
                                    prices model (item :> JsonNode)
                                    |> Result.mapError (fun message -> $"{label}: {message}")
                                    |> Result.map (fun (_, rates) ->
                                        { Provider = Some provider
                                          Model = model
                                          EffectiveFrom = effectiveFrom
                                          EffectiveTo = effectiveTo
                                          Currency = currency
                                          Prices = rates
                                          SourceUrl = Some url
                                          RetrievedAt = Some retrievedAt })
                                | _ -> Error $"{label} requires 'source' with 'url' and 'retrievedAt'")))))
        | _ -> Error $"entry {index} must be an object"

    /// Parses either format into dated entries for the reliability report.
    /// A legacy table prices its models from its `effectiveDate` onward; a
    /// `praxis.price-table/1` table must be unambiguous (no overlapping
    /// ranges for one provider and model) and use one currency.
    let parseSchedule (json: string) : Result<PriceSchedule, string> =
        try
            match JsonNode.Parse json with
            | :? JsonObject as table when isVersioned table ->
                match text table "schema" with
                | Some other when other <> PriceSchedule.VersionedFormat -> Error $"price table schema '{other}' is not supported; expected {PriceSchedule.VersionedFormat}"
                | _ ->
                    match text table "tableVersion", table["entries"] with
                    | None, _ -> Error "price table requires 'tableVersion'"
                    | Some version, (:? JsonArray as entries) when entries.Count > 0 ->
                        entries
                        |> Seq.mapi entry
                        |> Seq.toList
                        |> List.fold (fun acc next -> acc |> Result.bind (fun found -> next |> Result.map (fun item -> item :: found))) (Ok [])
                        |> Result.map List.rev
                        |> Result.bind (fun parsed ->
                            let schedule =
                                { Format = PriceSchedule.VersionedFormat
                                  Reference =
                                    { Source = text table "source" |> Option.defaultValue PriceSchedule.VersionedFormat
                                      Version = version
                                      EffectiveDate = None }
                                  Currency = parsed |> List.map _.Currency |> List.distinct |> String.concat ","
                                  Entries = parsed }

                            match PriceSchedule.problems schedule with
                            | [] -> Ok schedule
                            | problems -> Error(String.concat "; " problems))
                    | Some _, _ -> Error "price table requires a non-empty 'entries' array"
            | _ ->
                parse json
                |> Result.bind (fun legacy ->
                    let date = legacy.Reference.EffectiveDate |> Option.defaultValue ""
                    instant "price table effectiveDate" date |> Result.map (fun from -> PriceSchedule.ofLegacy from legacy))
        with :? JsonException as error ->
            Error $"price table is not valid JSON: {error.Message}"

    /// Reads and parses a price table file relative to the repository root.
    let read (root: string) (path: string) : Result<PriceTable, string> =
        try
            parse (IO.File.ReadAllText(IO.Path.Combine(root, path)))
        with
        | :? IO.IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message
