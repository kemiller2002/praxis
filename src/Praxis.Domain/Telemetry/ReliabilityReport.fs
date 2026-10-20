namespace Praxis.Domain.Telemetry

open System

/// One Praxis work event (shared interface I1) reduced to the fields the
/// reliability report reads. Unknown event types are ignored.
type ReliabilityEvent =
    { EventId: string
      Type: string
      WorkItem: string
      OccurredAt: DateTimeOffset option
      /// Position in the log: the tiebreak for equal timestamps.
      Order: int
      Execution: string option
      ActorId: string option
      Claimant: string option
      Reason: string option
      Codes: string list
      RequirementIds: string list
      TelemetryExecutions: string list
      Commit: string option }

/// One execution record reduced to the facts the report reads.
type ReliabilityExecution =
    { ExecutionId: string
      WorkItemId: string option
      StartedAt: DateTimeOffset option
      FinalizedAt: DateTimeOffset option
      Provider: string option
      SessionId: string option
      AgentId: string option
      /// `provider.agentInstanceId` (I7): the agent instance that ran it.
      AgentInstanceId: string option
      WallMs: int64 option
      ActiveMs: int64 option
      BlockedMs: int64 option
      AgentRetries: int64 option
      /// `cost.execution_total` with quality `observed`: provider/platform
      /// reported ("billed"), never mixed with an estimate.
      BilledCost: (decimal * string) option
      /// Rows of an ingested request ledger: explicitly attributed.
      Ledger: UsageObservation list
      /// `tokens.*` metrics recorded without a ledger (one observation).
      RecordUsage: UsageObservation option }

/// A transcript file the report read, identified by content digest only.
type TranscriptSource =
    { Kind: string
      Digest: string
      SessionId: string option
      AgentId: string option
      UsageEntries: int
      MalformedLines: int
      /// Usage entries without message or request ID: never counted,
      /// because they cannot be deduplicated.
      UnidentifiedUsageEntries: int
      /// Sub-agent usage echoed in a parent transcript (`progress` lines):
      /// read, then deduplicated with the sub-agent's own copy.
      EchoedUsageEntries: int
      CounterResets: int }

type ReliabilityInput =
    { Events: ReliabilityEvent list
      EventLines: int
      MalformedEventLines: int
      Executions: ReliabilityExecution list
      Transcripts: UsageObservation list
      Sources: TranscriptSource list
      /// Legacy or `praxis.price-table/1`, normalized to dated entries.
      PriceTable: PriceSchedule option
      /// `--agent-binding` (I7): enables per-agent attribution.
      AgentBinding: AgentBinding option
      /// Attribute session transcript usage to executions by session ID and
      /// execution time window (`--attribution explicit-only` disables it).
      SessionWindow: bool
      WorkItemFilter: string list }

/// A provider-neutral JSON-like tree; the CLI writes it canonically.
[<RequireQualifiedAccess>]
type ReportNode =
    | Text of string
    | Integer of int64
    | Number of decimal
    | Flag of bool
    | Missing
    | List of ReportNode list
    | Object of (string * ReportNode) list

/// PRX-REL-009 / RES-REL-003: deterministic cost per independently verified
/// requirement. Every metric carries its availability and denominator;
/// unavailable is never zero and a zero denominator yields no ratio.
[<RequireQualifiedAccess>]
module ReliabilityReport =
    let schema = "praxis.telemetry-reliability-report"
    let schemaVersion = 1L

    let private stamp (at: DateTimeOffset) =
        at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    let private ordinal (values: string list) = values |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    let private texts values = values |> ordinal |> List.map ReportNode.Text |> ReportNode.List
    let private optText value = value |> Option.map ReportNode.Text |> Option.defaultValue ReportNode.Missing
    let private countNode (value: int) = ReportNode.Integer(int64 value)

    type Availability =
        | Available
        | Partial
        | Unavailable

    let private availabilityCode =
        function
        | Available -> "available"
        | Partial -> "partial"
        | Unavailable -> "unavailable"

    let private metric (availability: Availability) (value: ReportNode) (unit: string) (numerator: ReportNode) (denominator: ReportNode) (definition: string) (reason: string option) =
        ReportNode.Object
            [ "availability", ReportNode.Text(availabilityCode availability)
              "value", (if availability = Unavailable then ReportNode.Missing else value)
              "unit", ReportNode.Text unit
              "numerator", numerator
              "denominator", denominator
              "definition", ReportNode.Text definition
              "reason", optText reason ]

    /// A count read directly from the event log: available whenever the log
    /// was read, zero included.
    let private count (value: int) (unit: string) (definition: string) =
        metric Available (countNode value) unit ReportNode.Missing ReportNode.Missing definition None

    let private ratio (numerator: int) (denominator: int) (definition: string) (emptyReason: string) =
        if denominator = 0 then
            metric Unavailable ReportNode.Missing "ratio" (countNode numerator) (countNode denominator) definition (Some emptyReason)
        else
            metric Available (ReportNode.Number(decimal numerator / decimal denominator)) "ratio" (countNode numerator) (countNode denominator) definition None

    // ---- allocation ------------------------------------------------------

    /// A share of one observation assigned to one target. Integer buckets are
    /// split evenly; the remainder goes one token each to the ordinally first
    /// targets, so shares always sum exactly to the observation.
    type Piece =
        { Observation: UsageObservation
          Execution: string
          Method: string
          WorkItem: string option
          Requirement: string option
          Values: Map<string, int64> }

    let splitEven (targets: string list) (values: Map<string, int64>) : (string * Map<string, int64>) list =
        let ordered = ordinal targets
        let n = int64 ordered.Length

        ordered
        |> List.mapi (fun index target ->
            target,
            values
            |> Map.map (fun _ value ->
                let share = value / n
                if int64 index < value % n then share + 1L else share))

    let private split (assign: Piece -> string -> Piece) (targets: string list) (piece: Piece) =
        splitEven targets piece.Values |> List.map (fun (target, values) -> assign { piece with Values = values } target)

    type private Unattributed =
        { Observation: UsageObservation
          Reason: string }

    let private inWindow (at: DateTimeOffset) (execution: ReliabilityExecution) =
        match execution.StartedAt with
        | Some started -> started <= at && (execution.FinalizedAt |> Option.forall (fun ended -> at <= ended))
        | None -> false

    /// Session-window attribution: executions of the observation's session
    /// (and provider) whose window contains it; an agent-ID match narrows the
    /// candidates when one exists.
    let private candidates (executions: ReliabilityExecution list) (observation: UsageObservation) : Result<string list, string> =
        match observation.SessionId, observation.OccurredAt with
        | None, _ -> Error "no-session-id"
        | _, None -> Error "no-timestamp"
        | Some session, Some at ->
            let sessionExecutions =
                executions
                |> List.filter (fun execution ->
                    execution.SessionId = Some session
                    && execution.Provider |> Option.forall (fun provider -> provider = observation.Provider))

            if sessionExecutions.IsEmpty then
                Error "no-execution-for-session"
            else
                match sessionExecutions |> List.filter (inWindow at) with
                | [] -> Error "outside-execution-windows"
                | windowed ->
                    let byAgent =
                        match observation.AgentId with
                        | Some agent -> windowed |> List.filter (fun execution -> execution.AgentId = Some agent)
                        | None -> []

                    Ok((if byAgent.IsEmpty then windowed else byAgent) |> List.map _.ExecutionId)

    // ---- totals over pieces ------------------------------------------------

    /// The TTL split partitions cache write: where cache write is not
    /// applicable, neither is its split.
    let private notApplicable (bucket: string) (observation: UsageObservation) =
        observation.NotApplicable.Contains bucket
        || ((bucket = "tokens.cache_write_5m" || bucket = "tokens.cache_write_1h") && observation.NotApplicable.Contains "tokens.cache_write")

    let private bucketValue (bucket: string) (piece: Piece) =
        match piece.Values |> Map.tryFind bucket with
        | Some value -> Some value
        | None when notApplicable bucket piece.Observation -> Some 0L
        | None -> None

    let private pieceTotal (piece: Piece) =
        ProviderUsage.buckets |> List.fold (fun sum bucket -> Option.map2 (+) sum (bucketValue bucket piece)) (Some 0L)

    /// Priced with the entry effective at the observation's timestamp
    /// (F5); a timestamp outside every entry's range is unpriced, never
    /// priced at another date's rates.
    let private pieceCost (table: PriceSchedule) (piece: Piece) : Result<decimal * PriceEntry, string> =
        let values =
            piece.Observation.NotApplicable |> Seq.fold (fun (map: Map<string, int64>) bucket -> if map.ContainsKey bucket then map else map.Add(bucket, 0L)) piece.Values

        PriceSchedule.estimate table piece.Observation.Provider piece.Observation.Model piece.Observation.OccurredAt piece.Observation.ObservationId values

    type private Sum =
        { Value: int64
          Observations: int
          Reporting: int }

    let private sumOf (select: Piece -> int64 option) (pieces: Piece list) =
        let byObservation = pieces |> List.groupBy (fun piece -> piece.Observation.ObservationId)
        let reporting = byObservation |> List.filter (fun (_, held) -> held |> List.forall (select >> Option.isSome)) |> List.length

        { Value = pieces |> List.sumBy (select >> Option.defaultValue 0L)
          Observations = byObservation.Length
          Reporting = reporting }

    /// Claude Code writes a transcript line per content block when the block
    /// starts, with the usage known then, and does not rewrite it when the
    /// message finishes: `output_tokens` there is a partial count. Verified
    /// on REL-001 run-01: the last message of each of 12 sub-agents read
    /// 3-24 output tokens in its transcript against 120-761 in the usage the
    /// orchestrator received; input and cache counts matched exactly.
    let streamedOutputReason =
        "Claude Code transcripts record output_tokens before a message finishes streaming and never rewrite it; output (and any total or cost including it) is a lower bound"

    let private hasStreamedOutput (pieces: Piece list) =
        pieces
        |> List.exists (fun piece ->
            piece.Observation.Runtime = "claude-code"
            && (piece.Observation.Origin = "claude-transcript" || piece.Observation.Origin = "execution-ledger")
            && piece.Values.ContainsKey "tokens.output")

    /// Downgrades an available value to a labelled lower bound.
    let private lowerBound (bounded: bool) (availability: Availability, reason: string option) =
        match bounded, availability with
        | true, Available -> Partial, Some streamedOutputReason
        | true, Partial -> Partial, reason |> Option.map (fun known -> $"{known}; {streamedOutputReason}")
        | _ -> availability, reason

    /// Available only when every in-scope execution has usage and every
    /// observation reported the bucket; a partial value is a lower bound.
    let private tokenMetricBounded (bounded: bool) (scope: int) (covered: int) (definition: string) (sum: Sum) =
        let availability, reason =
            lowerBound bounded (
                if sum.Observations = 0 then Unavailable, Some "no provider usage observation is attributable"
                elif sum.Reporting = 0 then Unavailable, Some $"none of {sum.Observations} observations reported this bucket"
                elif sum.Reporting < sum.Observations then Partial, Some $"reported on {sum.Reporting} of {sum.Observations} observations; value is a lower bound"
                elif covered < scope then Partial, Some $"usage observed for {covered} of {scope} executions; value is a lower bound"
                else Available, None
            )

        metric availability (ReportNode.Integer sum.Value) "tokens" (countNode sum.Reporting) (countNode sum.Observations) definition reason

    let private tokenMetric scope covered definition sum = tokenMetricBounded false scope covered definition sum

    type private CostSum =
        { Amount: decimal
          Priced: int
          Observed: int
          Reasons: string list
          /// Price entries used (`PriceSchedule.entryKey`), ordinal.
          Applied: string list }

    /// Priced per allocated share (linear, so shares sum to the whole); an
    /// observation counts as priced only when every one of its shares is.
    let private estimatedCost (table: PriceSchedule option) (pieces: Piece list) =
        match table with
        | None -> None
        | Some priced ->
            let results = pieces |> List.map (fun piece -> piece.Observation.ObservationId, pieceCost priced piece)
            let byObservation = results |> List.groupBy fst

            Some
                { Amount = results |> List.sumBy (function _, Ok(value, _) -> value | _, Error _ -> 0m)
                  Priced = byObservation |> List.filter (fun (_, held) -> held |> List.forall (snd >> Result.isOk)) |> List.length
                  Observed = byObservation.Length
                  Reasons = results |> List.choose (function _, Error reason -> Some reason | _, Ok _ -> None) |> ordinal
                  Applied = results |> List.choose (function _, Ok(_, entry) -> Some(PriceSchedule.entryKey entry) | _ -> None) |> ordinal }

    let rec private costMetric (table: PriceSchedule option) (scope: int) (covered: int) (cost: CostSum option) = costMetricBounded false table scope covered cost

    and private costMetricBounded (bounded: bool) (table: PriceSchedule option) (scope: int) (covered: int) (cost: CostSum option) =
        let definition = "sum of usage priced with the supplied, versioned price table (estimated, never billed)"
        let currency = table |> Option.map _.Currency |> Option.defaultValue "unknown"

        match cost with
        | None -> metric Unavailable ReportNode.Missing currency ReportNode.Missing ReportNode.Missing definition (Some "no price table supplied (--price-table); Praxis ships no prices")
        | Some sum when sum.Observed = 0 -> metric Unavailable ReportNode.Missing currency (countNode 0) (countNode 0) definition (Some "no provider usage observation is attributable")
        | Some sum when sum.Priced = 0 -> metric Unavailable ReportNode.Missing currency (countNode 0) (countNode sum.Observed) definition (Some(String.concat "; " sum.Reasons))
        | Some sum ->
            let availability, reason =
                lowerBound bounded (
                    if sum.Priced < sum.Observed then Partial, Some("lower bound; unpriced: " + String.concat "; " sum.Reasons)
                    elif covered < scope then Partial, Some $"usage observed for {covered} of {scope} executions; value is a lower bound"
                    else Available, None
                )

            metric availability (ReportNode.Number sum.Amount) currency (countNode sum.Priced) (countNode sum.Observed) definition reason

    let private perRequirement (verified: int) (unit: string) (definition: string) (source: ReportNode) =
        match source with
        | ReportNode.Object fields ->
            let field name = fields |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue ReportNode.Missing
            let availability = field "availability"

            let value =
                match field "value" with
                | ReportNode.Integer value -> Some(decimal value)
                | ReportNode.Number value -> Some value
                | _ -> None

            match availability, value with
            | _, _ when verified = 0 ->
                metric Unavailable ReportNode.Missing unit (field "value") (countNode 0) definition (Some "zero independently verified requirements: the ratio is undefined, not zero or infinite")
            | ReportNode.Text "unavailable", _
            | _, None -> metric Unavailable ReportNode.Missing unit ReportNode.Missing (countNode verified) definition (field "reason" |> function ReportNode.Text reason -> Some $"numerator unavailable: {reason}" | _ -> Some "numerator unavailable")
            | ReportNode.Text "partial", Some known -> metric Partial (ReportNode.Number(known / decimal verified)) unit (field "value") (countNode verified) definition (Some "numerator is a lower bound")
            | _, Some known -> metric Available (ReportNode.Number(known / decimal verified)) unit (field "value") (countNode verified) definition None
        | _ -> metric Unavailable ReportNode.Missing unit ReportNode.Missing (countNode verified) definition (Some "numerator unavailable")

    // ---- events --------------------------------------------------------------

    let private isAttempt (event: ReliabilityEvent) =
        event.Type = "work.completion.verified" || event.Type = "work.completion.rejected"

    let private chronological (events: ReliabilityEvent list) =
        events |> List.sortBy (fun event -> event.OccurredAt |> Option.defaultValue DateTimeOffset.MaxValue, event.Order)

    let private isStale (event: ReliabilityEvent) =
        // A write from an owner whose lease expired is stale too (PRX-REL-005).
        (event.Type = "work.claim.rejected"
         && event.Reason |> Option.exists (fun reason -> reason.StartsWith("stale-", StringComparison.Ordinal) || reason = "expired"))
        || (event.Type = "work.completion.rejected"
            && event.Codes |> List.exists (fun code -> code.Contains("STALE", StringComparison.OrdinalIgnoreCase) || code = "PRX.CLAIM.EXPIRED"))

    let allocationRules =
        [ "R1 observation identity: each provider usage observation has an ID (provider message ID, else request ID; Codex: session + cumulative total) and is counted at most once; duplicates (streamed content blocks, a ledger and a transcript holding the same request) are excluded and counted."
          "R2 explicit first: an observation in an ingested execution ledger belongs to that execution. Recorded tokens.* metrics without a ledger form one execution-record observation, superseded (and listed) when transcript observations exist for the same session."
          "R3 session window: otherwise an observation belongs to the executions of its session and provider whose [startedAt, finalizedAt] contains its timestamp, narrowed by agent ID when one matches; several candidates split it evenly. No candidate leaves it unattributed: reported, never counted as attributable."
          "R4 execution to work items: an execution named by events of several work items is split evenly among them; otherwise it belongs to its record's work item."
          "R5 work item to requirements: a work item's share is split evenly among the requirement IDs of its work.completion.verified events; a work item without verified requirements keeps its share at work-item level (still in the numerator)."
          "R6 even split of integer tokens: quotient to every target, remainder one token each to the ordinally first targets, so shares sum exactly; estimated cost is priced per share (linear), so allocated costs sum to the total."
          "R7 numerator: total attributable usage/cost of every in-scope execution, including rejected and unverified work; denominator: distinct requirement IDs in work.completion.verified events. Zero verified requirements leaves every per-requirement ratio unavailable."
          "R8 cost basis: estimated (supplied price table) and billed (observed cost.execution_total) are separate totals and never summed; a missing price makes the cost unavailable or a labelled lower bound, never zero." ]

    /// Token kinds in report order (I7): the four primary buckets, then the
    /// cache-write TTL split, available only when the provider reports
    /// `cache_creation.ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens`.
    let tokenKinds =
        [ "tokens.input", "input"
          "tokens.output", "output"
          "tokens.cache_read", "cacheRead"
          "tokens.cache_write", "cacheWrite"
          "tokens.cache_write_5m", "cacheWrite5m"
          "tokens.cache_write_1h", "cacheWrite1h" ]

    let build (input: ReliabilityInput) : ReportNode =
        let filter = set input.WorkItemFilter

        let events =
            input.Events
            |> List.distinctBy _.EventId
            |> List.filter (fun event -> filter.IsEmpty || filter.Contains event.WorkItem)

        let duplicateEvents = input.Events.Length - (input.Events |> List.distinctBy _.EventId |> List.length)

        let linkedWorkItems =
            events
            |> List.collect (fun event -> (event.Execution |> Option.toList) @ event.TelemetryExecutions |> List.map (fun execution -> execution, event.WorkItem))
            |> List.groupBy fst
            |> List.map (fun (execution, links) -> execution, links |> List.map snd |> ordinal)
            |> Map.ofList

        let executions =
            input.Executions
            |> List.filter (fun execution ->
                filter.IsEmpty
                || execution.WorkItemId |> Option.exists filter.Contains
                || linkedWorkItems.ContainsKey execution.ExecutionId)
            |> List.sortWith (fun a b -> String.CompareOrdinal(a.ExecutionId, b.ExecutionId))

        let scopeIds = executions |> List.map _.ExecutionId |> set

        // R1 + R2: one deduplicated observation universe.
        let transcriptSessions = input.Transcripts |> List.choose _.SessionId |> set

        let recordObservations, superseded =
            executions
            |> List.filter (fun execution -> execution.Ledger.IsEmpty)
            |> List.choose (fun execution -> execution.RecordUsage |> Option.map (fun usage -> execution, usage))
            |> List.partition (fun (execution, _) -> execution.SessionId |> Option.forall (transcriptSessions.Contains >> not))

        let deduped =
            ProviderUsage.dedupe ((executions |> List.collect _.Ledger) @ (recordObservations |> List.map snd) @ input.Transcripts)

        // R2 + R3: observation -> executions.
        let attributed, unattributed =
            deduped.Observations
            |> List.fold
                (fun (pieces: Piece list, missing: Unattributed list) observation ->
                    let piece execution method =
                        { Observation = observation; Execution = execution; Method = method; WorkItem = None; Requirement = None; Values = observation.Values }

                    match observation.ExplicitExecution with
                    | Some execution when scopeIds.Contains execution ->
                        piece execution (if observation.Origin = "execution-record" then "execution-record" else "explicit") :: pieces, missing
                    | Some _ -> pieces, missing
                    | None when not input.SessionWindow -> pieces, { Observation = observation; Reason = "session-window-attribution-disabled" } :: missing
                    | None ->
                        match candidates executions observation with
                        | Error reason -> pieces, { Observation = observation; Reason = reason } :: missing
                        | Ok [ single ] -> piece single "session-window" :: pieces, missing
                        | Ok several ->
                            let shared = piece "" "session-window-shared"
                            (split (fun part target -> { part with Execution = target }) several shared |> List.rev) @ pieces, missing)
                ([], [])

        let attributed = List.rev attributed
        let unattributed = List.rev unattributed

        // R4 + R5: executions -> work items -> requirements.
        let verifiedEvents = events |> List.filter (fun event -> event.Type = "work.completion.verified")

        let requirementsOf =
            verifiedEvents |> List.groupBy _.WorkItem |> List.map (fun (item, held) -> item, held |> List.collect _.RequirementIds |> ordinal) |> Map.ofList

        let executionById = executions |> List.map (fun execution -> execution.ExecutionId, execution) |> Map.ofList

        let workItemsOf executionId =
            match linkedWorkItems |> Map.tryFind executionId with
            | Some items when not items.IsEmpty -> items
            | _ -> executionById |> Map.tryFind executionId |> Option.bind _.WorkItemId |> Option.toList

        let allocated =
            attributed
            |> List.collect (fun piece ->
                match workItemsOf piece.Execution with
                | [] -> [ piece ]
                | items -> split (fun part item -> { part with WorkItem = Some item }) items piece)
            |> List.collect (fun piece ->
                match piece.WorkItem |> Option.bind requirementsOf.TryFind with
                | Some requirements when not requirements.IsEmpty -> split (fun part requirement -> { part with Requirement = Some requirement }) requirements piece
                | _ -> [ piece ])

        let covered = attributed |> List.map _.Execution |> List.distinct |> List.length
        let scope = executions.Length

        let tokenMetrics (pieces: Piece list) (scope: int) (covered: int) =
            [ for bucket, name in tokenKinds ->
                  name, tokenMetricBounded (bucket = "tokens.output" && hasStreamedOutput pieces) scope covered $"sum of provider-reported {bucket} (observations reporting / observations)" (sumOf (bucketValue bucket) pieces) ]
            @ [ "total", tokenMetricBounded (hasStreamedOutput pieces) scope covered "input + output + cache read + cache write; an observation counts only when all four are reported or not applicable" (sumOf pieceTotal pieces) ]

        let tokens = tokenMetrics allocated scope covered
        let estimated = costMetricBounded (hasStreamedOutput allocated) input.PriceTable scope covered (estimatedCost input.PriceTable allocated)

        let billed =
            let known = executions |> List.choose (fun execution -> execution.BilledCost |> Option.map (fun cost -> execution.ExecutionId, cost))
            let currencies = known |> List.map (snd >> snd) |> List.distinct
            let definition = "sum of observed cost.execution_total (provider/platform reported; billed basis)"

            match known, currencies with
            | [], _ -> metric Unavailable ReportNode.Missing "unknown" (countNode 0) (countNode scope) definition (Some "no execution carries a provider-reported cost")
            | _, [ currency ] ->
                let availability, reason = if known.Length < scope then Partial, Some "lower bound: some executions carry no provider-reported cost" else Available, None
                metric availability (ReportNode.Number(known |> List.sumBy (snd >> fst))) currency (countNode known.Length) (countNode scope) definition reason
            | _, several -> metric Unavailable ReportNode.Missing "mixed" (countNode known.Length) (countNode scope) definition (Some $"""mixed currencies: {String.concat ", " (ordinal several)}""")

        let verifiedRequirements = verifiedEvents |> List.collect _.RequirementIds |> ordinal
        let verified = verifiedRequirements.Length

        let attemptsByItem =
            events |> List.filter isAttempt |> chronological |> List.groupBy _.WorkItem |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        let attempted = attemptsByItem.Length
        let firstPass = attemptsByItem |> List.filter (fun (_, attempts) -> (List.head attempts).Type = "work.completion.verified") |> List.length
        let reworked = attemptsByItem |> List.filter (fun (_, attempts) -> attempts.Length > 1) |> List.length
        let extraAttempts = attemptsByItem |> List.sumBy (fun (_, attempts) -> attempts.Length - 1)
        let ofType kind = events |> List.filter (fun event -> event.Type = kind)
        let claimRejected = ofType "work.claim.rejected"
        let claimAttempts = (ofType "work.claim.acquired").Length + claimRejected.Length
        let completionRejected = (ofType "work.completion.rejected").Length

        let reacquisitions =
            ofType "work.claim.acquired" |> List.countBy _.WorkItem |> List.sumBy (fun (_, n) -> n - 1)

        let executionSum (select: ReliabilityExecution -> int64 option) (unit: string) (definition: string) =
            let known = executions |> List.choose select

            let availability, reason =
                if known.IsEmpty then Unavailable, Some "no in-scope execution reports this metric"
                elif known.Length < scope then Partial, Some $"reported by {known.Length} of {scope} executions; value is a lower bound"
                else Available, None

            metric availability (ReportNode.Integer(List.sum known)) unit (countNode known.Length) (countNode scope) definition reason

        let elapsed =
            let verifiedItems = verifiedEvents |> List.map _.WorkItem |> ordinal
            let definition = "per verified work item: first work.claim.acquired (else first event) to first work.completion.verified, summed"

            let spans =
                verifiedItems
                |> List.map (fun item ->
                    let itemEvents = events |> List.filter (fun event -> event.WorkItem = item) |> chronological
                    let start = itemEvents |> List.tryFind (fun event -> event.Type = "work.claim.acquired") |> Option.orElse (List.tryHead itemEvents) |> Option.bind _.OccurredAt
                    let finish = itemEvents |> List.tryFind (fun event -> event.Type = "work.completion.verified") |> Option.bind _.OccurredAt
                    Option.map2 (fun (s: DateTimeOffset) f -> int64 (f - s).TotalMilliseconds) start finish)

            let known = spans |> List.choose id

            if verifiedItems.IsEmpty then metric Unavailable ReportNode.Missing "milliseconds" (countNode 0) (countNode 0) definition (Some "no verified work item")
            elif known.Length < verifiedItems.Length then metric Partial (ReportNode.Integer(List.sum known)) "milliseconds" (countNode known.Length) (countNode verifiedItems.Length) definition (Some "some verified work items lack timestamps")
            else metric Available (ReportNode.Integer(List.sum known)) "milliseconds" (countNode known.Length) (countNode verifiedItems.Length) definition None

        let tokensTotal = tokens |> List.find (fst >> (=) "total") |> snd

        let pieceSummary (pieces: Piece list) =
            let cost = estimatedCost input.PriceTable pieces

            [ "tokensTotal", ReportNode.Integer(pieces |> List.sumBy (pieceTotal >> Option.defaultValue 0L))
              "tokensTotalComplete", ReportNode.Flag(not pieces.IsEmpty && pieces |> List.forall (pieceTotal >> Option.isSome))
              "estimatedCost", cost |> Option.map (fun sum -> ReportNode.Number sum.Amount) |> Option.defaultValue ReportNode.Missing
              "estimatedCostComplete", ReportNode.Flag(cost |> Option.exists (fun sum -> sum.Observed > 0 && sum.Priced = sum.Observed)) ]

        let workItems =
            (events |> List.map _.WorkItem) @ (allocated |> List.choose _.WorkItem)
            |> ordinal
            |> List.map (fun item ->
                let attempts = attemptsByItem |> List.tryFind (fst >> (=) item) |> Option.map snd |> Option.defaultValue []

                ReportNode.Object(
                    [ "id", ReportNode.Text item
                      "completionAttempts", countNode attempts.Length
                      "firstPass", (match attempts with [] -> ReportNode.Missing | first :: _ -> ReportNode.Flag(first.Type = "work.completion.verified"))
                      "verifiedRequirementIds", texts (requirementsOf |> Map.tryFind item |> Option.defaultValue [])
                      "executions", texts (allocated |> List.filter (fun piece -> piece.WorkItem = Some item) |> List.map _.Execution) ]
                    @ pieceSummary (allocated |> List.filter (fun piece -> piece.WorkItem = Some item))
                ))

        let requirements =
            verifiedRequirements
            |> List.map (fun requirement ->
                let pieces = allocated |> List.filter (fun piece -> piece.Requirement = Some requirement)
                ReportNode.Object([ "id", ReportNode.Text requirement; "workItems", texts (pieces |> List.choose _.WorkItem) ] @ pieceSummary pieces))

        let unattributedPieces =
            unattributed |> List.map (fun missing -> { Observation = missing.Observation; Execution = ""; Method = ""; WorkItem = None; Requirement = None; Values = missing.Observation.Values })

        let methodCounts =
            attributed |> List.distinctBy (fun piece -> piece.Observation.ObservationId) |> List.countBy _.Method |> List.map (fun (method, n) -> method, countNode n) |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        // I7 (PRAXIS-REL-010): per-agent attribution over the same
        // deduplicated universe; each observation goes whole to one agent,
        // one orchestrator, or the unattributed set (never split).
        let agentAttribution =
            match input.AgentBinding with
            | None -> ReportNode.Missing
            | Some binding ->
                let executionAgents (observation: UsageObservation) =
                    let agentsOf ids = ids |> List.map (fun id -> executionById |> Map.tryFind id |> Option.bind _.AgentInstanceId)

                    match observation.ExplicitExecution with
                    | Some id when executionById.ContainsKey id -> Some(agentsOf [ id ])
                    | Some _ -> None
                    | None ->
                        match candidates executions observation with
                        | Ok ids -> Some(agentsOf ids)
                        | Error _ -> None

                // `Execution` holds the target label: `agent:ID`,
                // `orchestrator:SESSION` or empty for unattributed.
                let resolved =
                    deduped.Observations
                    |> List.map (fun observation ->
                        let piece label method = { Observation = observation; Execution = label; Method = method; WorkItem = None; Requirement = None; Values = observation.Values }

                        match AgentAttribution.resolve binding executionAgents observation with
                        | AgentResolution.Attributed(AgentTarget.Agent agent, method) -> Choice1Of3(agent, piece agent method)
                        | AgentResolution.Attributed(AgentTarget.Orchestrator session, method) -> Choice2Of3(session, piece session method)
                        | AgentResolution.Unattributed reason -> Choice3Of3(piece "" reason))

                let agentPieces = resolved |> List.choose (function Choice1Of3 value -> Some value | _ -> None)
                let orchestratorPieces = resolved |> List.choose (function Choice2Of3 value -> Some value | _ -> None)
                let unattributedPieces = resolved |> List.choose (function Choice3Of3 piece -> Some piece | _ -> None)
                let currency = input.PriceTable |> Option.map _.Currency |> Option.defaultValue "unknown"

                let kindMetric (bucket: string) (pieces: Piece list) =
                    let sum = sumOf (bucketValue bucket) pieces
                    let definition = $"sum of provider-reported {bucket} over deduplicated observations (observations reporting / observations)"

                    let ttl = bucket = "tokens.cache_write_5m" || bucket = "tokens.cache_write_1h"

                    let availability, reason =
                        lowerBound (bucket = "tokens.output" && hasStreamedOutput pieces) (
                            if sum.Observations = 0 then Unavailable, Some "no observation is attributed here"
                            elif sum.Reporting = 0 && ttl then Unavailable, Some "the provider did not report the cache-write TTL split (cache_creation.ephemeral_5m/1h_input_tokens)"
                            elif sum.Reporting = 0 then Unavailable, Some $"none of {sum.Observations} observations reported this bucket"
                            elif sum.Reporting < sum.Observations then Partial, Some $"reported on {sum.Reporting} of {sum.Observations} observations; value is a lower bound"
                            else Available, None
                        )

                    metric availability (ReportNode.Integer sum.Value) "tokens" (countNode sum.Reporting) (countNode sum.Observations) definition reason

                let kinds (pieces: Piece list) =
                    ReportNode.Object(
                        [ for bucket, name in tokenKinds -> name, kindMetric bucket pieces ]
                        @ [ "total", tokenMetricBounded (hasStreamedOutput pieces) 0 0 "input + output + cache read + cache write; an observation counts only when all four are reported or not applicable" (sumOf pieceTotal pieces) ]
                    )

                let cost (pieces: Piece list) = costMetricBounded (hasStreamedOutput pieces) input.PriceTable 0 0 (estimatedCost input.PriceTable pieces)

                let methods (pieces: Piece list) =
                    pieces |> List.countBy _.Method |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b)) |> List.map (fun (method, n) -> method, countNode n) |> ReportNode.Object

                // Compact: plain values, null when unavailable, and one flag
                // per summary saying whether every token kind is complete.
                let summary (identity: (string * ReportNode) list) (pieces: Piece list) =
                    let known bucket =
                        let sum = sumOf (bucketValue bucket) pieces
                        if sum.Observations > 0 && sum.Reporting = sum.Observations then ReportNode.Integer sum.Value else ReportNode.Missing

                    let total = sumOf pieceTotal pieces
                    let priced = estimatedCost input.PriceTable pieces

                    ReportNode.Object(
                        identity
                        @ [ "observations", countNode (pieces |> List.distinctBy (fun piece -> piece.Observation.ObservationId) |> List.length) ]
                        @ [ for bucket, name in tokenKinds -> name, known bucket ]
                        @ [ "total", (if total.Observations > 0 && total.Reporting = total.Observations then ReportNode.Integer total.Value else ReportNode.Missing)
                            "estimatedCost",
                            (match priced with
                             | Some sum when sum.Observed > 0 && sum.Priced = sum.Observed -> ReportNode.Number sum.Amount
                             | _ -> ReportNode.Missing)
                            "currency", ReportNode.Text currency
                            // Values kept, but named: these kinds are lower bounds.
                            "lowerBounds", texts (if hasStreamedOutput pieces then [ "output"; "total"; "estimatedCost" ] else []) ]
                    )

                let agentIds = (binding.Bindings |> List.map _.AgentId) @ (agentPieces |> List.map fst) |> ordinal

                let agentEntries =
                    agentIds
                    |> List.map (fun agent ->
                        let pieces = agentPieces |> List.filter (fst >> (=) agent) |> List.map snd
                        let entries = binding.Bindings |> List.filter (fun entry -> entry.AgentId = agent)

                        agent,
                        pieces,
                        [ "agentId", ReportNode.Text agent
                          "sessionIds", texts (entries |> List.choose _.SessionId)
                          "scopes", texts (entries |> List.choose _.Scope)
                          "workItems", texts (AgentAttribution.workItems binding agent) ])

                let orchestrators =
                    orchestratorPieces |> List.map fst |> ordinal |> List.map (fun session -> session, orchestratorPieces |> List.filter (fst >> (=) session) |> List.map snd)

                // A6: agent -> bound work items -> verified requirements.
                let requirementPieces =
                    agentPieces
                    |> List.collect (fun (agent, piece) ->
                        match AgentAttribution.workItems binding agent with
                        | [] -> [ piece ]
                        | items -> split (fun part item -> { part with WorkItem = Some item }) items piece)
                    |> List.collect (fun piece ->
                        match piece.WorkItem |> Option.bind requirementsOf.TryFind with
                        | Some requirements when not requirements.IsEmpty -> split (fun part requirement -> { part with Requirement = Some requirement }) requirements piece
                        | _ -> [ piece ])

                let requirementEntries =
                    verifiedRequirements
                    |> List.map (fun requirement ->
                        let pieces = requirementPieces |> List.filter (fun piece -> piece.Requirement = Some requirement)
                        requirement, pieces, [ "requirementId", ReportNode.Text requirement; "workItems", texts (pieces |> List.choose _.WorkItem); "agents", texts (pieces |> List.map _.Execution) ])

                let everything = deduped.Observations |> List.map (fun observation -> { Observation = observation; Execution = ""; Method = ""; WorkItem = None; Requirement = None; Values = observation.Values })
                let raw (bucket: string) (pieces: Piece list) = pieces |> List.sumBy (fun piece -> piece.Values |> Map.tryFind bucket |> Option.defaultValue 0L)
                let parts = (agentPieces |> List.map snd) @ (orchestratorPieces |> List.map snd) @ unattributedPieces

                let conservation =
                    let rows = [ for bucket, name in tokenKinds -> name, raw bucket everything, raw bucket parts ]

                    ReportNode.Object
                        [ "definition", ReportNode.Text "per token kind: agents + orchestrators + unattributed equals the global deduplicated sum (each observation counted once, whole)"
                          "observations", ReportNode.Object [ "global", countNode everything.Length; "attributedParts", countNode parts.Length ]
                          "global", ReportNode.Object(rows |> List.map (fun (name, global', _) -> name, ReportNode.Integer global'))
                          "parts", ReportNode.Object(rows |> List.map (fun (name, _, part) -> name, ReportNode.Integer part))
                          "balanced", ReportNode.Flag(everything.Length = parts.Length && rows |> List.forall (fun (_, global', part) -> global' = part)) ]

                ReportNode.Object
                    [ "bindingSchema", ReportNode.Text AgentAttribution.schema
                      "bindingSha256", optText binding.Digest
                      "bindings", countNode binding.Bindings.Length
                      "observations", countNode deduped.Observations.Length
                      "duplicateEntriesExcluded", countNode deduped.DuplicateEntries
                      "crossFileDuplicatesExcluded", countNode deduped.CrossFileDuplicates
                      "tokens", kinds everything
                      "estimatedCost", cost everything
                      "agents",
                      agentEntries
                      |> List.map (fun (_, pieces, identity) -> ReportNode.Object(identity @ [ "methods", methods pieces; "tokens", kinds pieces; "estimatedCost", cost pieces ]))
                      |> ReportNode.List
                      "orchestrators",
                      orchestrators
                      |> List.map (fun (session, pieces) -> ReportNode.Object [ "sessionId", ReportNode.Text session; "tokens", kinds pieces; "estimatedCost", cost pieces ])
                      |> ReportNode.List
                      "unattributed",
                      ReportNode.Object
                          [ "observations", countNode unattributedPieces.Length
                            "byReason", methods unattributedPieces
                            "agentIds", texts (unattributedPieces |> List.choose _.Observation.AgentId)
                            "tokens", kinds unattributedPieces ]
                      "requirements",
                      requirementEntries
                      |> List.map (fun (_, pieces, identity) -> ReportNode.Object(identity @ [ "tokens", kinds pieces; "estimatedCost", cost pieces ]))
                      |> ReportNode.List
                      "summaries",
                      ReportNode.Object
                          [ "perAgent", agentEntries |> List.map (fun (agent, pieces, _) -> summary [ "agentId", ReportNode.Text agent ] pieces) |> ReportNode.List
                            "perOrchestrator", orchestrators |> List.map (fun (session, pieces) -> summary [ "sessionId", ReportNode.Text session ] pieces) |> ReportNode.List
                            "perVerifiedRequirement", requirementEntries |> List.map (fun (requirement, pieces, _) -> summary [ "requirementId", ReportNode.Text requirement ] pieces) |> ReportNode.List
                            "unattributed", summary [] unattributedPieces ]
                      "conservation", conservation
                      "rules", texts AgentAttribution.rules ]

        ReportNode.Object
            [ "schema", ReportNode.Text schema
              "schemaVersion", ReportNode.Integer schemaVersion
              "inputs",
              ReportNode.Object
                  [ "events", ReportNode.Object [ "lines", countNode input.EventLines; "malformedLines", countNode input.MalformedEventLines; "duplicateEventIds", countNode duplicateEvents; "considered", countNode events.Length ]
                    "executionsInScope", countNode scope
                    "workItemFilter", texts input.WorkItemFilter
                    "sessionWindowAttribution", ReportNode.Flag input.SessionWindow
                    "priceTable",
                    (match input.PriceTable with
                     | None -> ReportNode.Missing
                     | Some table ->
                         ReportNode.Object
                             [ "format", ReportNode.Text table.Format
                               "source", ReportNode.Text table.Reference.Source
                               "version", ReportNode.Text table.Reference.Version
                               "effectiveDate", optText table.Reference.EffectiveDate
                               "currency", ReportNode.Text table.Currency
                               "entries",
                               table.Entries
                               |> List.sortWith (fun a b -> String.CompareOrdinal(PriceSchedule.entryKey a, PriceSchedule.entryKey b))
                               |> List.map (fun entry ->
                                   ReportNode.Object
                                       [ "key", ReportNode.Text(PriceSchedule.entryKey entry)
                                         "provider", optText entry.Provider
                                         "model", ReportNode.Text entry.Model
                                         "effectiveFrom", ReportNode.Text(stamp entry.EffectiveFrom)
                                         "effectiveTo", optText (entry.EffectiveTo |> Option.map stamp)
                                         "sourceUrl", optText entry.SourceUrl
                                         "retrievedAt", optText entry.RetrievedAt ])
                               |> ReportNode.List ])
                    "transcripts",
                    input.Sources
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a.Digest, b.Digest))
                    |> List.map (fun source ->
                        ReportNode.Object
                            [ "kind", ReportNode.Text source.Kind
                              "sha256", ReportNode.Text source.Digest
                              "sessionId", optText source.SessionId
                              "agentId", optText source.AgentId
                              "usageEntries", countNode source.UsageEntries
                              "malformedLines", countNode source.MalformedLines
                              "unidentifiedUsageEntries", countNode source.UnidentifiedUsageEntries
                              "echoedUsageEntries", countNode source.EchoedUsageEntries
                              "counterResets", countNode source.CounterResets ])
                    |> ReportNode.List ]
              "usage",
              ReportNode.Object
                  [ "observations", countNode deduped.Observations.Length
                    "duplicateEntriesExcluded", countNode deduped.DuplicateEntries
                    "crossFileDuplicatesExcluded", countNode deduped.CrossFileDuplicates
                    "conflictingObservationIds", texts deduped.ConflictingIds
                    "attributionMethods", ReportNode.Object methodCounts
                    "supersededRecordObservations", texts (superseded |> List.map (fun (execution, _) -> execution.ExecutionId))
                    "coverage",
                    (if scope = 0 then
                         metric Unavailable ReportNode.Missing "ratio" (countNode covered) (countNode scope) "in-scope executions with attributable usage / in-scope executions" (Some "no in-scope execution")
                     else
                         metric (if covered = scope then Available else Partial) (ReportNode.Number(decimal covered / decimal scope)) "ratio" (countNode covered) (countNode scope) "in-scope executions with attributable usage / in-scope executions" None)
                    "tokens", ReportNode.Object tokens
                    "cost", ReportNode.Object [ "estimated", estimated; "billed", billed ]
                    "unattributed",
                    ReportNode.Object
                        [ "observations", countNode unattributed.Length
                          "byReason", ReportNode.Object(unattributed |> List.countBy _.Reason |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b)) |> List.map (fun (reason, n) -> reason, countNode n))
                          "tokens", ReportNode.Object(tokenMetrics unattributedPieces unattributed.Length unattributed.Length) ] ]
              "verification",
              ReportNode.Object
                  [ "verifiedRequirements", count verified "requirements" "distinct requirement IDs in work.completion.verified events"
                    "verifiedRequirementIds", texts verifiedRequirements
                    "verifiedWorkItems", count (verifiedEvents |> List.map _.WorkItem |> ordinal).Length "work items" "distinct work items with a work.completion.verified event"
                    "firstPassVerificationRate", ratio firstPass attempted "work items whose first completion attempt was verified / work items with at least one completion attempt" "no work item has a completion attempt"
                    "reworkFrequency", ratio reworked attempted "work items with more than one completion attempt / work items with at least one completion attempt" "no work item has a completion attempt"
                    "completionRejectionFrequency", ratio completionRejected (completionRejected + verifiedEvents.Length) "work.completion.rejected / (rejected + verified) completion attempts" "no completion attempt"
                    "duplicateOwnershipAttempts", metric Available (countNode (claimRejected |> List.filter (fun event -> event.Reason = Some "held") |> List.length)) "attempts" (countNode (claimRejected |> List.filter (fun event -> event.Reason = Some "held") |> List.length)) (countNode claimAttempts) "work.claim.rejected with reason held (denominator: claim attempts = acquired + rejected)" None
                    "staleWriteRejections", metric Available (countNode (events |> List.filter isStale |> List.length)) "rejections" (countNode (events |> List.filter isStale |> List.length)) (countNode (claimAttempts + completionRejected + verifiedEvents.Length)) "work.claim.rejected reason stale-*|expired plus work.completion.rejected with a *STALE* or PRX.CLAIM.EXPIRED code (denominator: claim and completion attempts)" None
                    "retries",
                    ReportNode.Object
                        [ "completionReattempts", count extraAttempts "attempts" "completion attempts beyond each work item's first"
                          "completionReplays", count (ofType "work.completion.replayed").Length "replays" "work.completion.replayed events (idempotent retries)"
                          "claimReacquisitions", count reacquisitions "claims" "work.claim.acquired beyond each work item's first"
                          "agentRetries", executionSum _.AgentRetries "retries" "sum of agent.retries over in-scope executions" ] ]
              "perVerifiedRequirement",
              ReportNode.Object
                  [ "estimatedCost", perRequirement verified (input.PriceTable |> Option.map _.Currency |> Option.defaultValue "unknown") "total attributable estimated cost / verified requirements" estimated
                    "billedCost", perRequirement verified "currency" "total billed (provider-reported) cost / verified requirements" billed
                    "tokensTotal", perRequirement verified "tokens" "total attributable tokens / verified requirements" tokensTotal ]
              "time",
              ReportNode.Object
                  [ "elapsedMs", elapsed
                    "activeMs", executionSum _.ActiveMs "milliseconds" "sum of time.active_ms over in-scope executions"
                    "blockedMs", executionSum _.BlockedMs "milliseconds" "sum of time.blocked_ms over in-scope executions"
                    "wallMs", executionSum _.WallMs "milliseconds" "sum of time.wall_ms over in-scope executions (overlapping executions add)" ]
              "attributedCommits", texts (verifiedEvents |> List.choose _.Commit)
              "agents", texts ((events |> List.choose _.Claimant) @ (events |> List.choose _.ActorId))
              "executions", texts (executions |> List.map _.ExecutionId)
              "workItems", ReportNode.List workItems
              "requirements", ReportNode.List requirements
              "allocationRules", texts allocationRules
              "agentAttribution", agentAttribution ]

    /// Canonical JSON: object keys sorted ordinally, arrays in the order the
    /// report built them (already sorted), two-space indentation, `\n` line
    /// ends, invariant culture, numbers rounded half-to-even to six decimal
    /// places and always printed with six. Identical input, identical bytes.
    let toJson (node: ReportNode) : string =
        let builder = Text.StringBuilder()
        let quote (value: string) = Text.Json.JsonSerializer.Serialize value

        let rec write (indent: int) (node: ReportNode) =
            let pad depth = String(' ', depth * 2)

            match node with
            | ReportNode.Text value -> builder.Append(quote value) |> ignore
            | ReportNode.Integer value -> builder.Append(value.ToString(Globalization.CultureInfo.InvariantCulture)) |> ignore
            | ReportNode.Number value ->
                builder.Append(Math.Round(value, 6, MidpointRounding.ToEven).ToString("0.000000", Globalization.CultureInfo.InvariantCulture)) |> ignore
            | ReportNode.Flag value -> builder.Append(if value then "true" else "false") |> ignore
            | ReportNode.Missing -> builder.Append "null" |> ignore
            | ReportNode.List [] -> builder.Append "[]" |> ignore
            | ReportNode.Object [] -> builder.Append "{}" |> ignore
            | ReportNode.List items ->
                builder.Append "[\n" |> ignore

                items
                |> List.iteri (fun index item ->
                    builder.Append(pad (indent + 1)) |> ignore
                    write (indent + 1) item
                    builder.Append(if index < items.Length - 1 then ",\n" else "\n") |> ignore)

                builder.Append(pad indent).Append "]" |> ignore
            | ReportNode.Object fields ->
                let sorted = fields |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                builder.Append "{\n" |> ignore

                sorted
                |> List.iteri (fun index (key, value) ->
                    builder.Append(pad (indent + 1)).Append(quote key).Append ": " |> ignore
                    write (indent + 1) value
                    builder.Append(if index < sorted.Length - 1 then ",\n" else "\n") |> ignore)

                builder.Append(pad indent).Append "}" |> ignore

        write 0 node
        builder.Append('\n').ToString()
