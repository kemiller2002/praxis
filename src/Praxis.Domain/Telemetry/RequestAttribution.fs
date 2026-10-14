namespace Praxis.Domain.Telemetry

open System

/// One provider request as an execution's request ledger records it: its
/// provider message ID and the usage buckets the provider reported. A bucket
/// absent from `Values` is unknown, never zero.
type LedgerRequest =
    { Key: string
      Model: string option
      AgentId: string option
      Values: Map<string, int64> }

/// One request ledger (one transcript snapshot) held by one execution.
type ExecutionLedger =
    { ExecutionId: string
      SnapshotId: string
      Step: string option
      Requests: LedgerRequest list }

/// A request two executions both hold, where neither is the other's
/// ancestor: a genuine double ingest, counted once and reported.
type AttributionConflict =
    { Key: string
      Executions: string list
      CountedIn: string }

/// Every ledger request assigned to exactly one owner.
type Attribution =
    { /// The rows each counted exactly once, with the ledger that owns them.
      Owned: (ExecutionLedger * LedgerRequest) list
      /// Holdings beyond each request's first: the double count avoided.
      DuplicateHoldings: int
      Conflicts: AttributionConflict list }

/// The content-free request ledger the `anthropic-claude-session` adapter
/// keeps in its raw snapshot: one row per deduplicated provider request.
[<RequireQualifiedAccess>]
module RequestLedger =
    /// The order of a row's `usage` array, and the metric each column is.
    let columns =
        [ "input"; "output"; "cacheRead"; "cacheWrite"; "cacheWrite5m"; "cacheWrite1h"; "reasoning" ]

    let metricIds =
        [ "tokens.input"
          "tokens.output"
          "tokens.cache_read"
          "tokens.cache_write"
          "tokens.cache_write_5m"
          "tokens.cache_write_1h"
          "tokens.reasoning" ]

    /// Metrics a ledger replaces in usage reports: its token buckets and the
    /// request count.
    let attributedMetricIds = "model.requests" :: metricIds

    /// Buckets a transcript records at streaming start: lower bounds on the
    /// provider's final count, never presented as final.
    let lowerBoundMetricIds = set [ "tokens.output"; "tokens.reasoning" ]

    /// A row's values from its `usage` array; `None` columns stay absent.
    let values (usage: int64 option list) : Map<string, int64> =
        List.zip metricIds (usage @ List.replicate (max 0 (metricIds.Length - usage.Length)) None |> List.truncate metricIds.Length)
        |> List.choose (fun (metricId, value) -> value |> Option.map (fun known -> metricId, known))
        |> Map.ofList

/// Attributes provider requests across a lineage of executions so that
/// every request is counted exactly once (CONSTRUCT-010 Phase 6):
///
/// - a request held by several ledgers of one execution counts once;
/// - a request a parent and its descendant both hold belongs to the
///   descendant: a child's usage is the child's, and a parent rollup that
///   adds its descendants still counts the request once;
/// - a request held by unrelated executions (a double ingest) counts once,
///   in the deepest, then lexically first, execution, and is reported.
[<RequireQualifiedAccess>]
module RequestAttribution =
    /// Ancestors of an execution, nearest first. Cycle-safe: a repeated
    /// execution ends the walk.
    let ancestors (parents: Map<string, string>) (executionId: string) : string list =
        let rec walk (seen: Set<string>) current acc =
            match parents |> Map.tryFind current with
            | Some parent when not (seen.Contains parent) -> walk (seen.Add parent) parent (parent :: acc)
            | _ -> List.rev acc

        walk (Set.singleton executionId) executionId []

    let private isAncestorOf parents (ancestor: string) (execution: string) =
        ancestors parents execution |> List.contains ancestor

    let attribute (parents: Map<string, string>) (ledgers: ExecutionLedger list) : Attribution =
        let holdings =
            ledgers
            |> List.collect (fun ledger -> ledger.Requests |> List.map (fun request -> request.Key, (ledger, request)))
            |> List.groupBy fst
            |> List.map (fun (key, held) -> key, held |> List.map snd)

        let depth execution = (ancestors parents execution).Length

        let decide (key: string, held: (ExecutionLedger * LedgerRequest) list) =
            let executions = held |> List.map (fun (ledger, _) -> ledger.ExecutionId) |> List.distinct

            let owner =
                executions
                |> List.sortWith (fun left right ->
                    match compare (depth right) (depth left) with
                    | 0 -> String.CompareOrdinal(left, right)
                    | order -> order)
                |> List.head

            let related =
                executions |> List.forall (fun execution -> execution = owner || isAncestorOf parents execution owner)

            let kept = held |> List.find (fun (ledger, _) -> ledger.ExecutionId = owner)

            let conflict =
                if related then None
                else Some { Key = key; Executions = executions |> List.sort; CountedIn = owner }

            kept, held.Length - 1, conflict

        let decided = holdings |> List.map decide

        { Owned = decided |> List.map (fun (kept, _, _) -> kept)
          DuplicateHoldings = decided |> List.sumBy (fun (_, duplicates, _) -> duplicates)
          Conflicts = decided |> List.choose (fun (_, _, conflict) -> conflict) }

    /// One attributed cell: the requests one execution owns for one step and
    /// model, and for each ledger metric its total when every request in
    /// the cell reported it (else absent: unknown, never a low total).
    type Cell =
        { ExecutionId: string
          Step: string option
          Model: string option
          Requests: int
          Totals: (string * int64) list }

    let cells (attribution: Attribution) : Cell list =
        attribution.Owned
        |> List.groupBy (fun (ledger, request) -> ledger.ExecutionId, ledger.Step, request.Model)
        |> List.map (fun ((executionId, step, model), rows) ->
            let requests = rows |> List.map snd

            let totals =
                RequestLedger.metricIds
                |> List.choose (fun metricId ->
                    let known = requests |> List.choose (fun request -> request.Values |> Map.tryFind metricId)
                    if known.Length = requests.Length then Some(metricId, List.sum known) else None)

            { ExecutionId = executionId
              Step = step
              Model = model
              Requests = requests.Length
              Totals = totals })
        |> List.sortBy (fun cell -> cell.ExecutionId, cell.Step, cell.Model)
