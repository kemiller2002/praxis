namespace Praxis.Cli

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Work

/// `telemetry usage [WORKITEM] --by DIMENSION [--price-table FILE] [--format
/// json|text]` (PRAXIS-REMOTE-04, CONSTRUCT-010 Phase 6): additive usage and
/// cost grouped by one dimension, with the evidence quality of every total
/// and the executions that reported nothing, so a total is never mistaken
/// for "zero for them". Provider requests are attributed once across parent
/// and child executions; provider-reported and estimated cost, and host and
/// agent measurements, are separate totals. Read-only.
[<RequireQualifiedAccess>]
module TelemetryUsageCommands =
    let private optionValue (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name then Some value else None)

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj

    let private record (fields: (string * JsonNode) list) : JsonNode =
        fields |> List.fold (fun (target: JsonObject) (key, value) -> target[key] <- value; target) (JsonObject()) :> JsonNode

    let private array (values: JsonNode list) : JsonNode =
        values |> List.fold (fun (target: JsonArray) value -> target.Add value; target) (JsonArray()) :> JsonNode

    let private strings (values: string list) = values |> List.map text |> array

    let private group (group: UsageGroup) : JsonNode =
        record
            [ "key", text group.Key
              "metric", text group.MetricId
              "unit", text group.Unit
              "currency", optionalText group.Currency
              "costBasis", optionalText group.CostBasis
              "origin", text (group.Origin |> Option.defaultValue "agent")
              "total", group.Total |> Option.map (fun total -> JsonValue.Create total :> JsonNode) |> Option.toObj
              "status", text (if group.Total.IsSome then "known" else "unknown")
              "bound", text (if group.LowerBound then "lower" else "exact")
              "complete", JsonValue.Create(group.Total.IsSome && group.UnavailableExecutions.IsEmpty)
              "measurements", JsonValue.Create group.Measurements
              "reportingExecutions", strings group.ReportingExecutions
              "unavailableExecutions", strings group.UnavailableExecutions
              "unavailableReasons",
              group.UnavailableReasons
              |> List.map (fun (execution, status, reason) -> record [ "execution", text execution; "status", text status; "reason", text reason ])
              |> array
              "evidenceQuality", record (group.Qualities |> List.map (fun (quality, count) -> quality, JsonValue.Create count :> JsonNode)) ]

    let private attribution (summary: FileTelemetryUsageRepository.RequestAttributionSummary) : JsonNode =
        record
            [ "ledgerExecutions", strings summary.LedgerExecutions
              "attributedRequests", JsonValue.Create summary.AttributedRequests
              "duplicateHoldingsExcluded", JsonValue.Create summary.DuplicateHoldings
              "duplicateSnapshotHoldingsExcluded", JsonValue.Create summary.DuplicateSnapshotHoldings
              "unverifiedExecutions", strings summary.UnverifiedExecutions
              "conflicts",
              summary.Conflicts
              |> List.map (fun conflict ->
                  record [ "requestKey", text conflict.Key; "executions", strings conflict.Executions; "countedIn", text conflict.CountedIn ])
              |> array ]

    let private acceptance (execution: string, outcome: Result<AcceptanceOutcome, string>) : JsonNode =
        match outcome with
        | Ok value ->
            let cycles =
                match value with
                | AcceptanceOutcome.Repaired(Some cycles)
                | AcceptanceOutcome.NotAccepted(Some cycles) -> JsonValue.Create cycles :> JsonNode
                | AcceptanceOutcome.FirstPass -> JsonValue.Create 0L :> JsonNode
                | _ -> null

            record [ "execution", text execution; "outcome", text (Acceptance.code value); "repairCycles", cycles ]
        | Error finding -> record [ "execution", text execution; "outcome", text "contradictory"; "finding", text finding ]

    let private priceTableNode (table: PriceTable option) : JsonNode =
        table
        |> Option.map (fun priced ->
            record
                [ "source", text priced.Reference.Source
                  "version", text priced.Reference.Version
                  "effectiveAt", optionalText priced.Reference.EffectiveDate
                  "currency", text priced.Currency ])
        |> Option.toObj

    /// The machine-readable report (`praxis.telemetry-usage`, version 2).
    let report (workItemId: string option) (dimension: UsageDimension) (table: PriceTable option) (scope: FileTelemetryUsageRepository.Scope) (groups: UsageGroup list) : JsonNode =
        record
            [ "schema", text "praxis.telemetry-usage"
              "schemaVersion", JsonValue.Create 2
              "workItemId", optionalText workItemId
              "by", text (UsageDimension.code dimension)
              "priceTable", priceTableNode table
              "groups", groups |> List.map group |> array
              "requestAttribution", attribution scope.Attribution
              "acceptance", scope.Acceptance |> List.map acceptance |> array ]

    let private number (value: float) = value.ToString("0.######", CultureInfo.InvariantCulture)

    /// One line per group; an unknown total reads `unknown`, a partial one
    /// is marked as a lower bound.
    let textTable (groups: UsageGroup list) : string list =
        let row key metric basis total = String.Format("{0,-40} {1,-36} {2,-36} {3}", key, metric, basis, total)

        row "KEY" "METRIC" "BASIS" "TOTAL"
        :: (groups
            |> List.map (fun group ->
                let basis =
                    match [ group.CostBasis; group.Origin ] |> List.choose id with
                    | [] -> "-"
                    | parts -> String.concat "," parts

                let total =
                    match group.Total with
                    | None -> "unknown"
                    | Some value when group.UnavailableExecutions.IsEmpty && not group.LowerBound -> number value
                    | Some value -> number value + " (lower bound)"

                row group.Key group.MetricId basis total))

    let run (root: string) (arguments: string list) =
        let workItemId = arguments |> List.tryHead |> Option.filter (fun value -> not (value.StartsWith("--", StringComparison.Ordinal)))

        let priceTable =
            optionValue "--price-table" arguments |> Option.map (PriceTableReader.read root >> Result.map Some) |> Option.defaultValue (Ok None)

        let format = optionValue "--format" arguments |> Option.defaultValue "json"

        match optionValue "--by" arguments |> Option.defaultValue "execution" |> UsageDimension.tryParse, priceTable with
        | None, _ ->
            eprintfn "ERROR --by must be one of: %s" (UsageDimension.all |> List.map UsageDimension.code |> String.concat ", ")
            2
        | _, Error message ->
            eprintfn "ERROR --price-table: %s" message
            2
        | Some _, Ok _ when format <> "json" && format <> "text" ->
            eprintfn "ERROR --format must be json or text"
            2
        | Some dimension, Ok table ->
            let scope = FileTelemetryUsageRepository.readWith root workItemId dimension table
            let groups = Usage.aggregateWith dimension scope.ExecutionsByKey scope.Required scope.Reasons scope.Measurements

            if format = "json" then
                printfn "%s" ((report workItemId dimension table scope groups).ToJsonString(JsonSerializerOptions(WriteIndented = true)))
            else
                textTable groups |> List.iter (printfn "%s")

            0
