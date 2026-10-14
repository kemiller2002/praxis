namespace Praxis.Domain.Telemetry

/// PRX-GRP-150, PRX-GRP-151: the provider-neutral context-overhead metric
/// IDs, and every telemetry adapter's declared capability for each. An
/// adapter that cannot observe a metric declares it `Unsupported`; a metric
/// it could observe but did not see in an input is `supported-unavailable`.
/// Neither is ever recorded as zero.
[<RequireQualifiedAccess>]
type ContextMetricSupport =
    /// The adapter emits the metric when its input carries it.
    | Observes
    /// The adapter cannot observe the metric.
    | Unsupported

[<RequireQualifiedAccess>]
module ContextMetrics =
    let firstProductiveChange = "time.first_productive_change_ms"
    let peakTokens = "context.peak_tokens"
    let windowTokens = "context.window_tokens"
    let repeatedDistinct = "context.repeated_file_reads_distinct"

    let ids =
        [ "context.repeated_file_reads"
          repeatedDistinct
          "context.governance_reads"
          "context.compactions"
          peakTokens
          windowTokens
          "time.first_code_change_ms"
          firstProductiveChange
          "tokens.cache_read"
          "tokens.cache_write"
          "model.requests"
          "tool.file_reads"
          "tool.searches" ]

    let code support =
        match support with
        | ContextMetricSupport.Observes -> "observes"
        | ContextMetricSupport.Unsupported -> "unsupported"

    let private otelObserved =
        set [ "tokens.cache_read"; "tokens.cache_write"; "model.requests"; "context.compactions"; "tool.file_reads"; "tool.searches" ]

    /// The declaration, for every adapter in `TelemetryAdapters.all` and every
    /// ID in `ids` (a catalog entry with no declaration is a defect, tested).
    let declaration (adapter: string) (metricId: string) : ContextMetricSupport =
        let observes condition = if condition then ContextMetricSupport.Observes else ContextMetricSupport.Unsupported

        match adapter with
        | "anthropic-claude-session" -> observes (metricId <> windowTokens)
        // The generic adapter records whatever registry metric its input names.
        | "generic" -> ContextMetricSupport.Observes
        | "anthropic-claude-otel"
        | "google-gemini-otel"
        | "github-copilot-otel"
        | "otel-json" -> observes (otelObserved.Contains metricId)
        | "anthropic-claude-hook"
        | "google-gemini-hook"
        | "github-copilot-hook" -> observes (metricId = "context.compactions")
        | _ -> ContextMetricSupport.Unsupported
