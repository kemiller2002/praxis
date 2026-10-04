namespace Praxis.Cli

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Pacing

type ProviderSnapshot =
    { Provider: string
      ObservedAt: DateTimeOffset
      Windows: QuotaWindow list
      Freshness: ObservationFreshness }

[<RequireQualifiedAccess>]
type PacingFreshnessState =
    | Fresh
    | Stale
    | Unavailable

[<RequireQualifiedAccess>]
module PacingFreshnessState =
    let code state =
        match state with
        | PacingFreshnessState.Fresh -> "fresh"
        | PacingFreshnessState.Stale -> "stale"
        | PacingFreshnessState.Unavailable -> "unavailable"

[<RequireQualifiedAccess>]
type PacingSafetyState =
    | Known
    | Indeterminate

[<RequireQualifiedAccess>]
module PacingSafetyState =
    let code state =
        match state with
        | PacingSafetyState.Known -> "known"
        | PacingSafetyState.Indeterminate -> "indeterminate"

type PacingStatusWindow =
    { Key: string
      Label: string
      UsedPercent: decimal
      DurationMinutes: float
      ResetsAt: DateTimeOffset
      PacePercent: decimal
      LeadHours: float option }

type PacingStatusHold =
    { Kind: string
      Detail: string
      ResumeAt: DateTimeOffset }

type PacingStatusView =
    { SchemaVersion: int
      Provider: string
      Model: string option
      StateDirectory: string
      ObservedAt: DateTimeOffset
      FreshnessState: PacingFreshnessState
      FreshnessReason: string option
      SafetyState: PacingSafetyState
      ProviderError: string option
      MayProceed: bool
      Override: bool
      Windows: PacingStatusWindow list
      Hold: PacingStatusHold option }

[<RequireQualifiedAccess>]
module PacingStatus =
    let private freshnessParts freshness =
        match freshness with
        | ObservationFreshness.Fresh -> PacingFreshnessState.Fresh, None
        | ObservationFreshness.Stale reason -> PacingFreshnessState.Stale, Some reason
        | ObservationFreshness.Unavailable reason -> PacingFreshnessState.Unavailable, Some reason

    let private freshnessText state reason =
        match state, reason with
        | PacingFreshnessState.Fresh, _ -> "fresh"
        | PacingFreshnessState.Stale, Some value -> $"stale: {value}"
        | PacingFreshnessState.Stale, None -> "stale"
        | PacingFreshnessState.Unavailable, Some value -> $"unavailable: {value}"
        | PacingFreshnessState.Unavailable, None -> "unavailable"

    let create
        (now: DateTimeOffset)
        (stateDirectory: string)
        (provider: string)
        (model: string option)
        (overridden: bool)
        (snapshot: ProviderSnapshot)
        (decision: PacingDecision)
        : PacingStatusView =
        let freshnessState, freshnessReason = freshnessParts snapshot.Freshness

        let safetyState =
            match snapshot.Freshness with
            | ObservationFreshness.Unavailable _ -> PacingSafetyState.Indeterminate
            | _ -> PacingSafetyState.Known

        let providerError =
            match snapshot.Freshness with
            | ObservationFreshness.Fresh -> None
            | ObservationFreshness.Stale reason
            | ObservationFreshness.Unavailable reason -> Some reason

        let windows =
            snapshot.Windows
            |> List.map (fun window ->
                { Key = window.Key
                  Label = window.Label
                  UsedPercent = window.UsedPercent
                  DurationMinutes = window.Duration.TotalMinutes
                  ResetsAt = window.ResetsAt
                  PacePercent = Pacing.idealPercent now window
                  LeadHours =
                    if Pacing.isWeekly PacingPolicy.defaults window then
                        Some((Pacing.lead now window).TotalHours)
                    else
                        None })

        let hold =
            decision.BindingReason
            |> Option.map (fun reason ->
                { Kind = PacingReasonKind.code reason.Kind
                  Detail = reason.Detail
                  ResumeAt = reason.ResumeAt })

        { SchemaVersion = 1
          Provider = provider
          Model = model
          StateDirectory = stateDirectory
          ObservedAt = snapshot.ObservedAt
          FreshnessState = freshnessState
          FreshnessReason = freshnessReason
          SafetyState = safetyState
          ProviderError = providerError
          MayProceed = decision.MayProceed
          Override = overridden
          Windows = windows
          Hold = hold }

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
        lines.Add("override: " + if status.Override then "on" else "off")

        let model = status.Model |> Option.map (fun value -> $" ({value})") |> Option.defaultValue ""
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
                            (window.ResetsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))
                | None ->
                    lines.Add(
                        sprintf
                            "%s: used %.2f%%, reset %s"
                            window.Label
                            (float window.UsedPercent)
                            (window.ResetsAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)))

        match status.Hold with
        | None -> lines.Add("gate: proceed")
        | Some reason ->
            lines.Add(
                $"gate: hold ({reason.Kind}); {reason.Detail}; recheck/resume estimate {reason.ResumeAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}")

        String.Join(Environment.NewLine, lines) + Environment.NewLine
