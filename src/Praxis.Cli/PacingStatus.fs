namespace Praxis.Cli

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Pacing

[<RequireQualifiedAccess>]
module PacingStatus =
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
        root["provider"] <- JsonValue.Create(status.Provider)
        status.Model |> Option.iter (fun value -> root["model"] <- JsonValue.Create(value))
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

        status.Hold
        |> Option.iter (fun reason ->
            let hold = JsonObject()
            hold["kind"] <- JsonValue.Create(reason.Kind)
            hold["detail"] <- JsonValue.Create(reason.Detail)
            hold["resumeAt"] <- JsonValue.Create(reason.ResumeAt.ToString("O"))
            root["hold"] <- hold)

        root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let renderText (status: PacingStatusView) =
        let lines = ResizeArray<string>()
        lines.Add($"state directory: {status.StateDirectory}")
        lines.Add("override: " + (if status.Override then "on" else "off"))

        let model =
            status.Model
            |> Option.map (fun value -> $" ({value})")
            |> Option.defaultValue ""

        lines.Add($"pacing {status.Provider}{model}: {freshnessText status.FreshnessState status.FreshnessReason}")

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

        String.Join(Environment.NewLine, lines) + Environment.NewLine
