namespace Ros.Domain.Telemetry

/// The static provider-adapter catalog production's own `telemetry adapters`
/// prints verbatim (`TELEMETRY_ADAPTERS`, `tools/ros_telemetry.mjs`). It is a
/// hardcoded list, not a computed decision -- ingestion itself (mapping each
/// adapter's payload shape into the normalized schema) remains out of scope
/// for this slice.
[<RequireQualifiedAccess>]
module TelemetryAdapters =
    let all =
        [ "generic"
          "openai-codex"
          "anthropic-claude-statusline"
          "anthropic-claude-hook"
          "anthropic-claude-otel"
          "google-gemini-hook"
          "google-gemini-otel"
          "github-copilot-hook"
          "github-copilot-otel"
          "otel-json" ]
