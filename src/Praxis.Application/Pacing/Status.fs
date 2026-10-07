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
        (provider: ProviderId)
        (model: ModelIdentity)
        (overridden: bool)
        (snapshot: ProviderSnapshot)
        (integrity: StateIntegrity)
        (decision: PacingDecision)
        (lastEvent: PacingEvent option)
        : PacingStatusView =
        let freshnessState, freshnessReason = freshnessParts snapshot.Freshness

        let safetyState =
            match snapshot.Freshness, integrity with
            | ObservationFreshness.Fresh, StateIntegrity.Intact -> PacingSafetyState.Known
            | _ -> PacingSafetyState.Indeterminate

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

        let coverage =
            snapshot.Coverage
            |> List.map (fun observation ->
                { Key = observation.Key
                  Status = WindowObservation.statusCode observation
                  Reason = WindowObservation.reason observation })

        { SchemaVersion = 1
          Provider = provider
          Model = model
          Adapter = snapshot.Adapter
          StateDirectory = stateDirectory
          ObservedAt = snapshot.ObservedAt
          FreshnessState = freshnessState
          FreshnessReason = freshnessReason
          SafetyState = safetyState
          ProviderError = providerError
          MayProceed = decision.MayProceed
          Override = overridden
          Windows = windows
          Coverage = coverage
          StateIntegrity = integrity
          Hold = hold
          LastEvent = lastEvent }
