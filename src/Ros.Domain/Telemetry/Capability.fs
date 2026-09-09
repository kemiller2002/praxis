namespace Ros.Domain.Telemetry

/// A metric registry entry's fields relevant to capability seeding and
/// baseline-metric normalization (`telemetry/metrics.json`'s `id`, `unit`,
/// `aggregation`, and `collection`). Category/description are display-only
/// in production and are not modeled here.
type MetricDefinition =
    { Id: string
      Unit: string
      Aggregation: string
      Collection: string }

type CapabilitySource =
    { Type: string
      Name: string
      Mechanism: string }

type CapabilityHistoryEntry =
    { Status: string
      Reason: string option
      Source: CapabilitySource
      DiscoveredAt: string
      LastAssessedAt: string
      RecordedAt: string }

type Capability =
    { MetricId: string
      Status: string
      Reason: string option
      DiscoveredAt: string
      LastAssessedAt: string
      RecordedAt: string
      Source: CapabilitySource
      History: CapabilityHistoryEntry list
      HistoryOmitted: int
      /// Production's `initialCapabilities` never sets `lastAssessedAt`,
      /// `recordedAt`, `history`, or `historyOmitted` at all -- only a later
      /// `upsertCapability` merge introduces them. `false` until a merge
      /// happens, so a JSON writer can omit those fields exactly as
      /// production's own never-merged capability objects do.
      Touched: bool }

/// Mirrors production's per-runtime `RUNTIME_CAPABILITIES` map
/// (`tools/ros_telemetry.mjs`): metric IDs a runtime family is known to be
/// able to expose, even before any observation has been ingested for a
/// given execution.
[<RequireQualifiedAccess>]
module RuntimeCapabilities =
    let private table =
        Map.ofList
            [ "codex",
              [ "tokens.input"
                "tokens.output"
                "tokens.cached_input"
                "tokens.cache_write"
                "tokens.reasoning"
                "tokens.total"
                "context.window_size" ]
              "claude-code",
              [ "tokens.input"
                "tokens.output"
                "tokens.cache_read"
                "tokens.cache_write"
                "context.window_size"
                "context.utilization"
                "cost.session_cumulative"
                "time.active_ms"
                "time.model_ms"
                "tool.calls"
                "tool.failures"
                "tool.duration_ms"
                "model.requests"
                "model.request_failures" ]
              "gemini-cli",
              [ "tokens.input"
                "tokens.output"
                "tokens.reasoning"
                "tokens.cached_input"
                "tokens.tool"
                "context.compactions"
                "tool.calls"
                "tool.failures"
                "tool.duration_ms"
                "agent.turns"
                "runtime.memory_peak_bytes"
                "runtime.cpu_utilization" ]
              "copilot", [ "tokens.input"; "tokens.output"; "tool.calls"; "tool.failures"; "tool.duration_ms"; "agent.turns" ] ]

    let known (runtime: string) =
        table |> Map.tryFind runtime |> Option.defaultValue [] |> Set.ofList

/// Mirrors production's `initialCapabilities`/`upsertCapability`
/// (`tools/ros_telemetry.mjs`): every metric in the registry starts with a
/// derived status describing what ROS or the runtime family is expected to
/// be able to report, and a later observation merges in without discarding
/// what was previously true — it is moved into a capped `history` instead.
[<RequireQualifiedAccess>]
module Capability =
    let initial (discoveredAt: string) (source: CapabilitySource) (runtime: string) (gitAvailable: bool) (metric: MetricDefinition) : Capability =
        let runtimeKnown = RuntimeCapabilities.known runtime

        let status, reason =
            if metric.Collection = "ros-derived" then
                if metric.Id.StartsWith "git." && not gitAvailable then
                    "supported-unavailable", "Git is unavailable"
                else
                    "derived", "ROS can derive this metric when its preconditions hold"
            elif runtimeKnown.Contains metric.Id then
                "supported-unavailable", "runtime family can expose this metric, but no observation has been ingested for this execution"
            else
                "unknown", "runtime capability not reported or mapped"

        { MetricId = metric.Id
          Status = status
          Reason = Some reason
          DiscoveredAt = discoveredAt
          LastAssessedAt = discoveredAt
          RecordedAt = discoveredAt
          Source = source
          History = []
          HistoryOmitted = 0
          Touched = false }

    /// Merges a newly observed status into the existing entry for the same
    /// metric. The previous status/reason/source is moved into `history`
    /// only when it actually changed, then history is capped at
    /// `maxHistoryEntries` by keeping the oldest entry plus the most recent
    /// ones (mirroring production's own first-plus-tail truncation) and
    /// counting the rest as `historyOmitted`.
    let upsert
        (maxHistoryEntries: int)
        (newDiscoveredAt: string)
        (recordedAtNow: string)
        (status: string)
        (reason: string option)
        (source: CapabilitySource)
        (previous: Capability)
        : Capability =
        let changed = (previous.Status, previous.Reason, previous.Source) <> (status, reason, source)

        let history =
            if changed then
                previous.History
                @ [ { Status = previous.Status
                      Reason = previous.Reason
                      Source = previous.Source
                      DiscoveredAt = previous.DiscoveredAt
                      LastAssessedAt = previous.LastAssessedAt
                      RecordedAt = previous.RecordedAt } ]
            else
                previous.History

        let overflow = history.Length - maxHistoryEntries

        let cappedHistory, additionalOmitted =
            if overflow > 0 then
                (List.head history :: (history |> List.skip (history.Length - (maxHistoryEntries - 1)))), overflow
            else
                history, 0

        { previous with
            Status = status
            Reason = reason
            Source = source
            DiscoveredAt = if changed then newDiscoveredAt else previous.DiscoveredAt
            LastAssessedAt = newDiscoveredAt
            RecordedAt = recordedAtNow
            History = cappedHistory
            HistoryOmitted = previous.HistoryOmitted + additionalOmitted
            Touched = true }

/// Mirrors production `defaultClassification` (`tools/ros_telemetry.mjs`):
/// the classification a new execution is stamped with when the caller does
/// not supply one explicitly.
[<RequireQualifiedAccess>]
module WorkClassification =
    let defaultFor (workType: string) =
        match workType with
        | "research" -> "research"
        | "feature" -> "development"
        | "task" -> "development"
        | "maintenance" -> "maintenance"
        | "bug" -> "defect-bug-fix"
        | "infrastructure" -> "infrastructure-devops"
        | "mechanical" -> "administrative-process"
        | _ -> "development"
