namespace Ros.Tests

open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module TelemetryIdentityTests =
    let private source (typ: string) name mechanism : CapabilitySource = { Type = typ; Name = name; Mechanism = mechanism }

    let private metric id collection : MetricDefinition =
        { Id = id
          Unit = "count"
          Aggregation = "sum"
          Collection = collection }

    let tests =
        [ { Name = "identity discovery with no override and no whitelisted environment stays fully unknown"
            Run = fun () ->
                let identity, discoverySource = Identity.discover IdentityInputs.empty
                Assert.equal "unknown" identity.Provider
                Assert.equal "unknown" identity.Runtime
                Assert.equal None identity.SessionId
                Assert.equal "explicit-or-unmapped-environment" discoverySource.Mechanism }

          { Name = "an explicit provider/runtime override always wins over a whitelisted environment variable"
            Run = fun () ->
                let identity, discoverySource =
                    Identity.discover { IdentityInputs.empty with Provider = Some "custom"; Runtime = Some "custom-runtime"; ClaudeCodeSessionId = Some "abc" }

                Assert.equal "custom" identity.Provider
                Assert.equal "custom-runtime" identity.Runtime
                // The whitelist cascade never runs once either field is explicit, so the
                // session id fallback chain (which reads CLAUDE_CODE_SESSION_ID after the
                // ROS_TELEMETRY_SESSION_ID fallback) is unaffected and still recovers it.
                Assert.equal (Some "abc") identity.SessionId
                Assert.equal "explicit-or-unmapped-environment" discoverySource.Mechanism }

          { Name = "CLAUDE_CODE_SESSION_ID alone whitelists anthropic/claude-code and supplies the session id"
            Run = fun () ->
                let identity, discoverySource = Identity.discover { IdentityInputs.empty with ClaudeCodeSessionId = Some "sess-1" }
                Assert.equal "anthropic" identity.Provider
                Assert.equal "claude-code" identity.Runtime
                Assert.equal (Some "sess-1") identity.SessionId
                Assert.equal "whitelisted-claude-environment" discoverySource.Mechanism }

          { Name = "CODEX_THREAD_ID alone (without CODEX_SESSION_ID) still whitelists openai/codex"
            Run = fun () ->
                let identity, _ = Identity.discover { IdentityInputs.empty with CodexThreadId = Some "thread-1" }
                Assert.equal "openai" identity.Provider
                Assert.equal "codex" identity.Runtime
                Assert.equal (Some "thread-1") identity.ConversationId }

          { Name = "GITHUB_ACTIONS whitelists github but keeps an already-explicit runtime rather than overwriting it"
            Run = fun () ->
                let identity, discoverySource =
                    Identity.discover { IdentityInputs.empty with Runtime = Some "custom-runner"; GitHubActions = true; GitHubRunId = Some "42" }

                Assert.equal "github" identity.Provider
                Assert.equal "custom-runner" identity.Runtime
                Assert.equal (Some "42") identity.RunId
                Assert.equal "whitelisted-github-actions-environment" discoverySource.Mechanism }

          { Name = "GITHUB_ACTIONS with no explicit runtime defaults the runtime to github-actions"
            Run = fun () ->
                let identity, _ = Identity.discover { IdentityInputs.empty with GitHubActions = true }
                Assert.equal "github" identity.Provider
                Assert.equal "github-actions" identity.Runtime }

          { Name = "the session id fallback prefers an explicit value, then ROS_TELEMETRY_SESSION_ID, then the whitelisted provider's own variable"
            Run = fun () ->
                let identity, _ =
                    Identity.discover
                        { IdentityInputs.empty with
                            SessionId = Some "explicit"
                            RosTelemetrySessionId = Some "ros-env"
                            ClaudeCodeSessionId = Some "claude-env" }

                Assert.equal (Some "explicit") identity.SessionId }

          { Name = "capability seeding marks a ros-derived metric as derived when Git is available"
            Run = fun () ->
                let capability = Capability.initial "T0" (source "e" "n" "m") "unknown" true (metric "git.baseline_dirty_files" "ros-derived")
                Assert.equal "derived" capability.Status
                Assert.equal false capability.Touched }

          { Name = "capability seeding marks a git.* ros-derived metric supported-unavailable when Git is unavailable"
            Run = fun () ->
                let capability = Capability.initial "T0" (source "e" "n" "m") "unknown" false (metric "git.baseline_dirty_files" "ros-derived")
                Assert.equal "supported-unavailable" capability.Status }

          { Name = "capability seeding marks a non-git ros-derived metric derived even when Git is unavailable"
            Run = fun () ->
                let capability = Capability.initial "T0" (source "e" "n" "m") "unknown" false (metric "documentation.files_changed" "ros-derived")
                Assert.equal "derived" capability.Status }

          { Name = "capability seeding marks a runtime-known metric supported-unavailable before any observation"
            Run = fun () ->
                let capability = Capability.initial "T0" (source "e" "n" "m") "claude-code" true (metric "tokens.input" "runtime")
                Assert.equal "supported-unavailable" capability.Status }

          { Name = "capability seeding marks an unmapped metric unknown"
            Run = fun () ->
                let capability = Capability.initial "T0" (source "e" "n" "m") "unknown" true (metric "tokens.input" "runtime")
                Assert.equal "unknown" capability.Status }

          { Name = "upserting a capability for the first time always adds lastAssessedAt/recordedAt, matching it Touched even without a status change"
            Run = fun () ->
                let previous = Capability.initial "T0" (source "e" "n" "m") "unknown" true (metric "git.baseline_dirty_files" "ros-derived")
                let merged = Capability.upsert 64 "T0" "T0" "T1" previous.Status previous.Reason previous.Source previous
                Assert.equal true merged.Touched
                Assert.equal [] merged.History
                Assert.equal 0 merged.HistoryOmitted }

          { Name = "upserting a capability with a changed reason moves the previous status into history"
            Run = fun () ->
                let previous = Capability.initial "T0" (source "e" "n" "m") "unknown" true (metric "git.baseline_dirty_files" "ros-derived")
                let merged = Capability.upsert 64 "T1" "T1" "T1" "derived" (Some "normalized measurement recorded") (source "ros-git" "git-status" "porcelain-v1") previous
                Assert.equal 1 merged.History.Length
                Assert.equal previous.Status merged.History[0].Status
                Assert.equal previous.Reason merged.History[0].Reason
                Assert.equal "T1" merged.DiscoveredAt }

          { Name = "history is capped at maxHistoryEntries, keeping the oldest entry plus the most recent tail and counting the rest as omitted"
            Run = fun () ->
                let seed = Capability.initial "T0" (source "e" "n" "m") "unknown" true (metric "git.baseline_dirty_files" "ros-derived")

                let grown =
                    [ 1..5 ]
                    |> List.fold (fun capability index -> Capability.upsert 3 $"T{index}" $"T{index}" $"T{index}" $"status-{index}" None (source "e" "n" "m") capability) seed

                Assert.equal 3 grown.History.Length
                Assert.equal "derived" grown.History[0].Status
                Assert.equal "status-3" grown.History[1].Status
                Assert.equal "status-4" grown.History[2].Status
                Assert.equal 2 grown.HistoryOmitted }

          { Name = "default classification maps every known work type, and falls back to development for an unmapped one"
            Run = fun () ->
                Assert.equal "research" (WorkClassification.defaultFor "research")
                Assert.equal "development" (WorkClassification.defaultFor "feature")
                Assert.equal "development" (WorkClassification.defaultFor "task")
                Assert.equal "maintenance" (WorkClassification.defaultFor "maintenance")
                Assert.equal "defect-bug-fix" (WorkClassification.defaultFor "bug")
                Assert.equal "infrastructure-devops" (WorkClassification.defaultFor "infrastructure")
                Assert.equal "administrative-process" (WorkClassification.defaultFor "mechanical")
                Assert.equal "development" (WorkClassification.defaultFor "unmapped-type") } ]
