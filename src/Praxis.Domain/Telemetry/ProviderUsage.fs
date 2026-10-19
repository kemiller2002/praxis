namespace Praxis.Domain.Telemetry

open System

/// One provider-reported usage observation (PRX-REL-009, RES-REL-003): one
/// provider request (Claude Code `message.usage`) or one cumulative-counter
/// increment (Codex `token_count`). Every bucket is optional: a bucket the
/// provider did not report is unavailable, never zero. A bucket the provider
/// family does not have at all (OpenAI has no cache-write category) is named
/// in `NotApplicable`; it contributes nothing and is not "unavailable".
type UsageObservation =
    { /// Stable dedupe key: `PROVIDER:MESSAGE-ID` (else `PROVIDER:REQUEST-ID`),
      /// or `openai:SESSION:total@N` for a cumulative counter step. An
      /// observation is counted at most once per report, by this key.
      ObservationId: string
      Provider: string
      Runtime: string
      SessionId: string option
      /// Subagent (sidechain) identity, when the runtime records it.
      AgentId: string option
      Model: string option
      MessageId: string option
      RequestId: string option
      OccurredAt: DateTimeOffset option
      /// Metric ID (`tokens.input`, ...) -> provider-reported count.
      Values: Map<string, int64>
      NotApplicable: Set<string>
      /// Where the observation came from: `claude-transcript`,
      /// `codex-rollout`, `execution-ledger` or `execution-record`.
      Origin: string
      /// The execution an ingested ledger or record explicitly names.
      ExplicitExecution: string option }

/// Duplicates removed by `ProviderUsage.dedupe`.
type UsageDedupe =
    { Observations: UsageObservation list
      /// Entries beyond the first for each observation ID (streamed content
      /// blocks, a ledger and a transcript holding the same request).
      DuplicateEntries: int
      /// Observation IDs whose duplicates disagreed on usage; the entry with
      /// the largest output (the latest streaming state) is kept.
      ConflictingIds: string list }

[<RequireQualifiedAccess>]
module ProviderUsage =
    /// The normalized token buckets, in report order. Cache read and cache
    /// write are their own buckets and are never folded into input.
    let buckets = [ "tokens.input"; "tokens.output"; "tokens.cache_read"; "tokens.cache_write" ]

    /// Optional detail buckets: the TTL split partitions cache write and
    /// reasoning is a subset of output, so neither is added to a total.
    let detailBuckets = [ "tokens.cache_write_5m"; "tokens.cache_write_1h"; "tokens.reasoning" ]

    /// `Some` when every primary bucket is reported or not applicable.
    let total (observation: UsageObservation) : int64 option =
        buckets
        |> List.fold
            (fun sum bucket ->
                sum
                |> Option.bind (fun known ->
                    match observation.Values |> Map.tryFind bucket with
                    | Some value -> Some(known + value)
                    | None when observation.NotApplicable.Contains bucket -> Some known
                    | None -> None))
            (Some 0L)

    let private usageSignature (observation: UsageObservation) =
        observation.Values |> Map.toList

    /// Keeps one entry per observation ID. An explicitly attributed entry (an
    /// ingested ledger row) wins, then the largest output, then input order:
    /// deterministic for identical input.
    let dedupe (entries: UsageObservation list) : UsageDedupe =
        let groups =
            entries
            |> List.mapi (fun index entry -> index, entry)
            |> List.groupBy (fun (_, entry) -> entry.ObservationId)

        let choose (_, held: (int * UsageObservation) list) =
            held
            |> List.sortWith (fun (leftIndex, left) (rightIndex, right) ->
                let explicitRank (entry: UsageObservation) = if entry.ExplicitExecution.IsSome then 0 else 1
                let output (entry: UsageObservation) = entry.Values |> Map.tryFind "tokens.output" |> Option.defaultValue -1L

                match compare (explicitRank left) (explicitRank right) with
                | 0 ->
                    match compare (output right) (output left) with
                    | 0 ->
                        match String.CompareOrdinal(defaultArg left.ExplicitExecution "", defaultArg right.ExplicitExecution "") with
                        | 0 -> compare leftIndex rightIndex
                        | order -> order
                    | order -> order
                | order -> order)
            |> List.head
            |> snd

        let conflicting (id, held: (int * UsageObservation) list) =
            if held |> List.map (snd >> usageSignature) |> List.distinct |> List.length > 1 then Some id else None

        { Observations = groups |> List.map choose |> List.sortWith (fun a b -> String.CompareOrdinal(a.ObservationId, b.ObservationId))
          DuplicateEntries = groups |> List.sumBy (fun (_, held) -> held.Length - 1)
          ConflictingIds = groups |> List.choose conflicting |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) }

    /// Codex rollouts report a cumulative `total_token_usage` whose
    /// `input_tokens` includes `cached_input_tokens` (OpenAI semantics).
    /// Normalized: input is the uncached part, cache read is the cached part,
    /// cache write is not a category this provider has.
    type CodexCounter =
        { At: DateTimeOffset option
          Model: string option
          Input: int64 option
          CachedInput: int64 option
          Output: int64 option
          Reasoning: int64 option
          Total: int64 option }

    /// Turns a session's cumulative counter readings into increments. A
    /// repeated reading (same total) is the same observation; a decreasing
    /// counter is a reset, counted and never turned into a negative delta.
    let codexIncrements (sessionId: string) (readings: CodexCounter list) : UsageObservation list * int =
        let delta (current: int64 option) (previous: int64 option) =
            match current, previous with
            | Some now, Some before when now >= before -> Some(now - before)
            | Some now, None -> Some now
            | _ -> None

        let folder (previous: CodexCounter option, emitted: UsageObservation list, resets: int) (reading: CodexCounter) =
            match reading.Total, previous |> Option.bind _.Total with
            | None, _ -> previous, emitted, resets
            | Some now, Some before when now = before -> previous, emitted, resets
            | Some now, Some before when now < before -> Some reading, emitted, resets + 1
            | Some now, _ ->
                let prior = previous |> Option.defaultValue { reading with Input = Some 0L; CachedInput = Some 0L; Output = Some 0L; Reasoning = Some 0L }
                let input = delta reading.Input prior.Input
                let cached = delta reading.CachedInput prior.CachedInput

                let values =
                    [ "tokens.input", Option.map2 (fun all cache -> all - cache) input cached |> Option.filter (fun value -> value >= 0L)
                      "tokens.cache_read", cached
                      "tokens.output", delta reading.Output prior.Output
                      "tokens.reasoning", delta reading.Reasoning prior.Reasoning ]
                    |> List.choose (fun (metric, value) -> value |> Option.map (fun known -> metric, known))
                    |> Map.ofList

                let observation =
                    { ObservationId = $"openai:{sessionId}:total@{now}"
                      Provider = "openai"
                      Runtime = "codex"
                      SessionId = Some sessionId
                      AgentId = None
                      Model = reading.Model
                      MessageId = None
                      RequestId = None
                      OccurredAt = reading.At
                      Values = values
                      NotApplicable = set [ "tokens.cache_write" ]
                      Origin = "codex-rollout"
                      ExplicitExecution = None }

                Some reading, observation :: emitted, resets

        let _, emitted, resets = readings |> List.fold folder (None, [], 0)
        List.rev emitted, resets
