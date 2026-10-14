namespace Praxis.Domain.Telemetry

open System

/// The price table an estimate was computed with. An estimate without one
/// is not an estimate Praxis reports: its name, version and effective date
/// are what make a changing price auditable.
type PriceTableRef =
    { Source: string
      Version: string
      /// `None` only for a runtime's own built-in table, whose date the
      /// runtime does not state; reported as unknown, never guessed.
      EffectiveDate: string option }

/// Where a monetary amount comes from. Provider-reported and estimated cost
/// are different types and are never summed into one total.
[<RequireQualifiedAccess>]
type CostBasis =
    /// The provider or platform reported the amount.
    | ProviderReported
    /// Praxis or a runtime priced token usage with a named price table.
    | Estimated of PriceTableRef
    /// An estimate or calculation recorded without its price table
    /// (historical records): labelled, never promoted to a priced estimate.
    | Unpriced

[<RequireQualifiedAccess>]
module CostBasis =
    let code (basis: CostBasis) =
        match basis with
        | CostBasis.ProviderReported -> "provider-reported"
        | CostBasis.Estimated table ->
            let date = table.EffectiveDate |> Option.defaultValue "unknown"
            $"estimated:{table.Source}@{table.Version}/{date}"
        | CostBasis.Unpriced -> "estimated:price-table-unknown"

    /// The basis of a recorded monetary measurement: an observed amount is
    /// provider-reported; a derived or estimated amount is an estimate of
    /// the price table it names.
    let ofMeasurement (quality: string) (pricing: PriceTableRef option) : CostBasis =
        match quality, pricing with
        | "observed", _ -> CostBasis.ProviderReported
        | _, Some table -> CostBasis.Estimated table
        | _, None -> CostBasis.Unpriced

/// Per-million-token prices for one model. A missing rate is unknown, and a
/// request needing it cannot be priced.
type ModelPrices =
    { Input: decimal option
      Output: decimal option
      CacheRead: decimal option
      /// A single cache-write rate, used when the TTL split is unknown or
      /// the table does not price TTLs separately.
      CacheWrite: decimal option
      CacheWrite5m: decimal option
      CacheWrite1h: decimal option }

/// A price table the operator supplies (`telemetry usage --price-table`).
/// Praxis ships no prices: a table is the operator's named, dated evidence.
type PriceTable =
    { Reference: PriceTableRef
      Currency: string
      Models: Map<string, ModelPrices> }

[<RequireQualifiedAccess>]
module UsageCost =
    let private perMillion = 1_000_000m

    let private map2 (combine: 'a -> 'b -> 'c) (left: Result<'a, string>) (right: Result<'b, string>) : Result<'c, string> =
        left |> Result.bind (fun l -> right |> Result.map (combine l))

    let private priced (rate: decimal option) (tokens: int64 option) (bucket: string) : Result<decimal, string> =
        match tokens, rate with
        | Some 0L, _ -> Ok 0m
        | Some count, Some price -> Ok(decimal count * price / perMillion)
        | None, _ -> Error $"the request's {bucket} tokens are unknown"
        | Some _, None -> Error $"the price table has no {bucket} rate"

    /// Cache writes priced by TTL when both the request's split and the
    /// table's TTL rates are known; otherwise at the single cache-write rate,
    /// which is valid only when the table states one.
    let private cacheWriteCost (prices: ModelPrices) (values: Map<string, int64>) : Result<decimal, string> =
        let tryFind metricId = values |> Map.tryFind metricId

        match tryFind "tokens.cache_write_5m", tryFind "tokens.cache_write_1h", prices.CacheWrite5m, prices.CacheWrite1h with
        | Some short, Some long, Some _, Some _ ->
            map2 (+) (priced prices.CacheWrite5m (Some short) "5-minute cache-write") (priced prices.CacheWrite1h (Some long) "1-hour cache-write")
        | _ -> priced prices.CacheWrite (tryFind "tokens.cache_write") "cache-write"

    /// The estimated cost of one request, or why it cannot be priced. Cache
    /// read and cache write are priced as their own buckets, never as input.
    /// Reasoning is part of output for this provider family and is not
    /// priced twice.
    let estimateRequest (table: PriceTable) (request: LedgerRequest) : Result<decimal, string> =
        match request.Model |> Option.bind (fun model -> table.Models |> Map.tryFind model) with
        | None ->
            let model = request.Model |> Option.defaultValue "unknown"
            Error $"the price table has no prices for model '{model}'"
        | Some prices ->
            let tryFind metricId = request.Values |> Map.tryFind metricId

            [ priced prices.Input (tryFind "tokens.input") "input"
              priced prices.Output (tryFind "tokens.output") "output"
              priced prices.CacheRead (tryFind "tokens.cache_read") "cache-read"
              cacheWriteCost prices request.Values ]
            |> List.fold (fun total next -> map2 (+) total next) (Ok 0m)

    /// The estimate for a set of requests: a total only when every request
    /// can be priced; otherwise the first reason, never a partial sum.
    let estimate (table: PriceTable) (requests: LedgerRequest list) : Result<decimal, string> =
        requests
        |> List.fold (fun total request -> total |> Result.bind (fun sum -> estimateRequest table request |> Result.map ((+) sum))) (Ok 0m)
