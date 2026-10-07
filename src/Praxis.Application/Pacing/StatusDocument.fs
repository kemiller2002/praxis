namespace Praxis.Application.Pacing

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Pacing

/// The two projections of the typed status model (`pacing status --json`
/// and text), and the hook contract the gate answers agent runtimes with.
/// Both are pure renderings of typed values; the CLI only writes them.
[<RequireQualifiedAccess>]
module PacingStatusDocument =
    let private freshnessText state reason =
        match state, reason with
        | PacingFreshnessState.Fresh, _ -> "fresh"
        | PacingFreshnessState.Stale, Some value -> $"stale: {value}"
        | PacingFreshnessState.Stale, None -> "stale"
        | PacingFreshnessState.Unavailable, Some value -> $"unavailable: {value}"
        | PacingFreshnessState.Unavailable, None -> "unavailable"

    let renderJson (status: PacingStatusView) =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create(status.SchemaVersion)
        root["provider"] <- JsonValue.Create(ProviderId.code status.Provider)
        ModelIdentity.model status.Model |> Option.iter (fun value -> root["model"] <- JsonValue.Create(value))
        root["modelIdentity"] <- JsonValue.Create(ModelIdentity.code status.Model)
        ModelIdentity.family status.Model |> Option.iter (fun family -> root["modelFamily"] <- JsonValue.Create(ModelFamily.value family))

        let adapter = JsonObject()
        adapter["id"] <- JsonValue.Create(status.Adapter.AdapterId)
        adapter["rulesVersion"] <- JsonValue.Create(status.Adapter.RulesVersion)
        let capabilities = JsonArray()
        status.Adapter.Capabilities |> List.iter (fun value -> capabilities.Add(JsonValue.Create value))
        adapter["capabilities"] <- capabilities
        let families = JsonArray()
        status.Adapter.ModelFamilies |> List.iter (fun value -> families.Add(JsonValue.Create(ModelFamily.value value)))
        adapter["modelFamilies"] <- families
        status.Adapter.Bucket |> Option.iter (fun bucket -> adapter["quotaBucket"] <- JsonValue.Create(QuotaBucket.value bucket))
        root["adapter"] <- adapter
        root["stateDirectory"] <- JsonValue.Create(status.StateDirectory)
        root["observedAt"] <- JsonValue.Create(status.ObservedAt.ToString("O"))
        root["freshness"] <- JsonValue.Create(freshnessText status.FreshnessState status.FreshnessReason)
        root["freshnessState"] <- JsonValue.Create(PacingFreshnessState.code status.FreshnessState)
        status.FreshnessReason |> Option.iter (fun value -> root["freshnessReason"] <- JsonValue.Create(value))
        root["safetyState"] <- JsonValue.Create(PacingSafetyState.code status.SafetyState)
        status.ProviderError |> Option.iter (fun value -> root["providerError"] <- JsonValue.Create(value))
        root["mayProceed"] <- JsonValue.Create(status.MayProceed)
        root["override"] <- JsonValue.Create(status.Override)

        let windows = JsonArray()

        for window in status.Windows do
            let row = JsonObject()
            row["key"] <- JsonValue.Create(window.Key)
            row["label"] <- JsonValue.Create(window.Label)
            row["usedPercent"] <- JsonValue.Create(window.UsedPercent)
            row["durationMinutes"] <- JsonValue.Create(window.DurationMinutes)
            row["resetsAt"] <- JsonValue.Create(window.ResetsAt.ToString("O"))
            row["pacePercent"] <- JsonValue.Create(window.PacePercent)
            window.LeadHours |> Option.iter (fun value -> row["leadHours"] <- JsonValue.Create(value))
            windows.Add row

        root["windows"] <- windows

        let coverage = JsonArray()

        for observation in status.Coverage do
            let row = JsonObject()
            row["key"] <- JsonValue.Create(observation.Key)
            row["status"] <- JsonValue.Create(observation.Status)
            observation.Reason |> Option.iter (fun value -> row["reason"] <- JsonValue.Create(value))
            coverage.Add row

        root["coverage"] <- coverage

        match status.StateIntegrity with
        | StateIntegrity.Intact -> root["stateIntegrity"] <- JsonValue.Create("intact")
        | StateIntegrity.Indeterminate reason ->
            root["stateIntegrity"] <- JsonValue.Create("indeterminate")
            root["stateIntegrityReason"] <- JsonValue.Create(reason)

        status.Hold
        |> Option.iter (fun reason ->
            let hold = JsonObject()
            hold["kind"] <- JsonValue.Create(reason.Kind)
            hold["detail"] <- JsonValue.Create(reason.Detail)
            hold["resumeAt"] <- JsonValue.Create(reason.ResumeAt.ToString("O"))
            root["hold"] <- hold)

        status.LastEvent
        |> Option.iter (fun event ->
            let node = JsonObject()
            node["code"] <- JsonValue.Create(PacingEvents.code event.Code)
            node["occurredAt"] <- JsonValue.Create(event.OccurredAt.ToString("O"))
            event.WindowKey |> Option.iter (fun key -> node["window"] <- JsonValue.Create(key))
            node["detail"] <- JsonValue.Create(event.Detail)
            root["lastEvent"] <- node)

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let renderText (status: PacingStatusView) =
        let lines = ResizeArray<string>()
        lines.Add($"state directory: {status.StateDirectory}")
        lines.Add("override: " + (if status.Override then "on" else "off"))

        let model =
            ModelIdentity.model status.Model
            |> Option.map (fun value -> $" ({value})")
            |> Option.defaultValue ""

        lines.Add($"pacing {ProviderId.code status.Provider}{model}: {freshnessText status.FreshnessState status.FreshnessReason}")

        let bucket =
            status.Adapter.Bucket
            |> Option.map (fun value -> $", bucket {QuotaBucket.value value}")
            |> Option.defaultValue ""

        let family =
            ModelIdentity.family status.Model
            |> Option.map (fun value -> $", model family {ModelFamily.value value}")
            |> Option.defaultValue $", model {ModelIdentity.code status.Model}"

        lines.Add($"adapter: {status.Adapter.AdapterId} {status.Adapter.RulesVersion}{bucket}{family}")

        match status.StateIntegrity with
        | StateIntegrity.Intact -> ()
        | StateIntegrity.Indeterminate reason -> lines.Add($"state: INDETERMINATE ({reason})")

        status.Coverage
        |> List.filter (fun observation -> observation.Status <> "observed")
        |> List.iter (fun observation ->
            let reason = observation.Reason |> Option.map (fun value -> $" ({value})") |> Option.defaultValue ""
            lines.Add($"coverage: {observation.Key} {observation.Status}{reason}"))

        if status.Windows.IsEmpty then
            lines.Add("quota: no usable windows")
        else
            for window in status.Windows do
                match window.LeadHours with
                | Some lead ->
                    lines.Add(
                        sprintf
                            "%s: used %.2f%%, pace %.2f%%, lead %+.1fh, reset %s"
                            window.Label
                            (float window.UsedPercent)
                            (float window.PacePercent)
                            lead
                            (window.ResetsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                    )
                | None ->
                    lines.Add(
                        sprintf
                            "%s: used %.2f%%, reset %s"
                            window.Label
                            (float window.UsedPercent)
                            (window.ResetsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                    )

        match status.Hold with
        | None -> lines.Add("gate: proceed")
        | Some reason ->
            let resume = reason.ResumeAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            lines.Add($"gate: hold ({reason.Kind}); {reason.Detail}; recheck/resume estimate {resume}")

        status.LastEvent
        |> Option.iter (fun event ->
            let at = event.OccurredAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            lines.Add($"last event: {PacingEvents.render event} at {at}"))

        String.Join(Environment.NewLine, lines) + Environment.NewLine

[<RequireQualifiedAccess>]
module PacingHookContract =
    /// The documented hook denial shape for a provider and hook event:
    /// Claude Code and Codex `PreToolUse` use `hookSpecificOutput`
    /// (`permissionDecision: deny`); other Codex events stop with
    /// `continue: false`; other Claude events block with `decision: block`.
    let denyOutput (provider: ProviderId) (event: string) (detail: string) : string =
        let reason =
            $"Praxis usage pacing hold remains active: {detail}. Retry after quota refresh/reset."

        let root = JsonObject()

        if event = "PreToolUse" then
            let output = JsonObject()
            output["hookEventName"] <- JsonValue.Create(event)
            output["permissionDecision"] <- JsonValue.Create("deny")
            output["permissionDecisionReason"] <- JsonValue.Create(reason)
            root["hookSpecificOutput"] <- output
        else
            match provider with
            | ProviderId.Codex ->
                root["continue"] <- JsonValue.Create(false)
                root["stopReason"] <- JsonValue.Create(reason)
            | ProviderId.Claude ->
                root["decision"] <- JsonValue.Create("block")
                root["reason"] <- JsonValue.Create(reason)

        root.ToJsonString()

