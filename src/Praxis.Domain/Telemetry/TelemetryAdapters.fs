namespace Praxis.Domain.Telemetry

/// The static provider-adapter catalog `telemetry adapters` prints. The
/// first part is production's own catalog (`TELEMETRY_ADAPTERS`,
/// `tools/ros_telemetry.mjs`), verbatim and in order; `fsharpOnly` names the
/// adapters added after the F# CLI became primary (DF-ROS-2026-A030), which
/// the Node rollback path does not implement.
[<RequireQualifiedAccess>]
module TelemetryAdapters =
    let production =
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

    /// PRAXIS-PLAN-05: `anthropic-claude-session` derives session metrics
    /// from a Claude Code transcript (`SessionTranscript`).
    let fsharpOnly = [ "anthropic-claude-session" ]

    let all = production @ fsharpOnly
