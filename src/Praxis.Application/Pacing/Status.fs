namespace Praxis.Application.Pacing

open System
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingStatusProjection =
    let private freshnessParts freshness =
        match freshness with
        | ObservationFreshness.Fresh -> PacingFreshnessState.Fresh, None
        | ObservationFreshness.Stale reason -> PacingFreshnessState.Stale, Some reason
        | ObservationFreshness.Unavailable reason -> PacingFreshnessState.Unavailable, Some reason

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
            | ObservationFreshness.Fresh -> PacingSafetyState.Known
            | ObservationFreshness.Stale _
            | ObservationFreshness.Unavailable _ -> PacingSafetyState.Indeterminate

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
