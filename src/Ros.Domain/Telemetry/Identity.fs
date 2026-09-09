namespace Ros.Domain.Telemetry

/// Every environment input production's `discoverIdentity`
/// (`tools/ros_telemetry.mjs`) reads, plus the explicit per-field overrides
/// a caller (a CLI flag, or a future producer command) may supply. Kept as
/// one explicit record rather than a live environment read so discovery
/// stays a pure decision; an Infrastructure port supplies the real values.
type IdentityInputs =
    { Provider: string option
      Model: string option
      ModelVersion: string option
      Runtime: string option
      RuntimeVersion: string option
      SessionId: string option
      ConversationId: string option
      RunId: string option
      AgentId: string option
      SubagentId: string option
      ParentExecutionId: string option
      RosTelemetryProvider: string option
      RosTelemetryRuntime: string option
      RosTelemetryModel: string option
      RosTelemetryModelVersion: string option
      RosTelemetryRuntimeVersion: string option
      RosTelemetrySessionId: string option
      RosTelemetryConversationId: string option
      RosTelemetryRunId: string option
      RosActor: string option
      CodexSessionId: string option
      CodexThreadId: string option
      ClaudeCodeSessionId: string option
      GeminiSessionId: string option
      CopilotSessionId: string option
      GitHubActions: bool
      GitHubRunId: string option
      OllamaHost: string option }

[<RequireQualifiedAccess>]
module IdentityInputs =
    /// No explicit override and no whitelisted environment variable set:
    /// every field defaults exactly as production's own `options = {}` /
    /// unset `process.env` entries would.
    let empty =
        { Provider = None
          Model = None
          ModelVersion = None
          Runtime = None
          RuntimeVersion = None
          SessionId = None
          ConversationId = None
          RunId = None
          AgentId = None
          SubagentId = None
          ParentExecutionId = None
          RosTelemetryProvider = None
          RosTelemetryRuntime = None
          RosTelemetryModel = None
          RosTelemetryModelVersion = None
          RosTelemetryRuntimeVersion = None
          RosTelemetrySessionId = None
          RosTelemetryConversationId = None
          RosTelemetryRunId = None
          RosActor = None
          CodexSessionId = None
          CodexThreadId = None
          ClaudeCodeSessionId = None
          GeminiSessionId = None
          CopilotSessionId = None
          GitHubActions = false
          GitHubRunId = None
          OllamaHost = None }

type IdentitySource =
    { Type: string
      Name: string
      Mechanism: string }

type Identity =
    { Provider: string
      Model: string option
      ModelVersion: string option
      Runtime: string
      RuntimeVersion: string option
      SessionId: string option
      ConversationId: string option
      RunId: string option
      AgentId: string option
      SubagentId: string option
      ParentExecutionId: string option }

/// Ports production's `discoverIdentity` (`tools/ros_telemetry.mjs`) exactly:
/// an explicit override always wins; otherwise a fixed cascade of whitelisted
/// environment variables assigns `provider`/`runtime` together, and several
/// independent fields (`sessionId`, `conversationId`, `runId`) each fall back
/// through their own ordered list of environment variables.
[<RequireQualifiedAccess>]
module Identity =
    let private orElse (options: string option) (fallback: string option) = options |> Option.orElse fallback

    let discover (inputs: IdentityInputs) : Identity * IdentitySource =
        let explicitProvider = orElse inputs.Provider inputs.RosTelemetryProvider
        let explicitRuntime = orElse inputs.Runtime inputs.RosTelemetryRuntime
        let unknownBoth = explicitProvider.IsNone && explicitRuntime.IsNone

        let provider, runtime, mechanism =
            if unknownBoth && (inputs.CodexSessionId.IsSome || inputs.CodexThreadId.IsSome) then
                "openai", "codex", "whitelisted-codex-environment"
            elif unknownBoth && inputs.ClaudeCodeSessionId.IsSome then
                "anthropic", "claude-code", "whitelisted-claude-environment"
            elif unknownBoth && inputs.GeminiSessionId.IsSome then
                "google", "gemini-cli", "whitelisted-gemini-environment"
            elif unknownBoth && inputs.CopilotSessionId.IsSome then
                "github", "copilot", "whitelisted-copilot-environment"
            elif explicitProvider.IsNone && inputs.GitHubActions then
                "github", (explicitRuntime |> Option.defaultValue "github-actions"), "whitelisted-github-actions-environment"
            elif explicitProvider.IsNone && inputs.OllamaHost.IsSome then
                "local", (explicitRuntime |> Option.defaultValue "ollama"), "whitelisted-local-runtime-environment"
            else
                explicitProvider |> Option.defaultValue "unknown", explicitRuntime |> Option.defaultValue "unknown", "explicit-or-unmapped-environment"

        let identity =
            { Provider = provider
              Model = orElse inputs.Model inputs.RosTelemetryModel
              ModelVersion = orElse inputs.ModelVersion inputs.RosTelemetryModelVersion
              Runtime = runtime
              RuntimeVersion = orElse inputs.RuntimeVersion inputs.RosTelemetryRuntimeVersion
              SessionId =
                inputs.SessionId
                |> Option.orElse inputs.RosTelemetrySessionId
                |> Option.orElse inputs.CodexSessionId
                |> Option.orElse inputs.ClaudeCodeSessionId
                |> Option.orElse inputs.GeminiSessionId
                |> Option.orElse inputs.CopilotSessionId
              ConversationId = inputs.ConversationId |> Option.orElse inputs.RosTelemetryConversationId |> Option.orElse inputs.CodexThreadId
              RunId = inputs.RunId |> Option.orElse inputs.RosTelemetryRunId |> Option.orElse inputs.GitHubRunId
              AgentId = orElse inputs.AgentId inputs.RosActor
              SubagentId = inputs.SubagentId
              ParentExecutionId = inputs.ParentExecutionId }

        identity, { Type = "environment"; Name = "runtime-identity"; Mechanism = mechanism }
