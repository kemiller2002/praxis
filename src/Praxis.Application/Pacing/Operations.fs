namespace Praxis.Application.Pacing

open System
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingOperations =
    let private refreshEvery = TimeSpan.FromSeconds 60.0

    let private evaluateAt
        (runtime: PacingRuntime)
        (provider: string)
        (model: string option)
        (snapshot: ProviderSnapshot)
        (now: DateTimeOffset)
        (overridden: bool)
        : PacingDecision =
        runtime.State.WithState(fun state ->
            Pacing.evaluate
                PacingPolicy.defaults
                { Now = now
                  Provider = provider
                  Model = model
                  Windows = snapshot.Windows
                  Freshness = snapshot.Freshness
                  Existing = state
                  Override = overridden })

    let status
        (runtime: PacingRuntime)
        (stateDirectory: string)
        (provider: string)
        (model: string option)
        : PacingStatusView =
        let now = runtime.Clock.Now()
        let snapshot = runtime.Observer.Observe now provider model
        let overridden = runtime.Override.IsEnabled()
        let decision = evaluateAt runtime provider model snapshot now overridden
        PacingStatusProjection.create now stateDirectory provider model overridden snapshot decision

    let resolveModel (runtime: PacingRuntime) (provider: string) (explicitModel: string option) (payload: string) =
        runtime.Context.ResolveModel provider explicitModel payload

    let setOverride (runtime: PacingRuntime) enabled =
        runtime.Override.SetEnabled enabled

    let gate (runtime: PacingRuntime) (provider: string) (model: string option) : PacingGateOutcome =
        let started = runtime.Clock.Now()
        let maximum = runtime.MaxGateWait provider
        let mutable lastRefresh = DateTimeOffset.MinValue
        let mutable snapshot: ProviderSnapshot option = None
        let mutable lastReason: string option = None
        let mutable outcome: PacingGateOutcome option = None

        while outcome.IsNone do
            if runtime.Override.IsEnabled() then
                lastReason
                |> Option.iter (fun _ -> runtime.Events.Write "release: override enabled")

                outcome <- Some PacingGateOutcome.Proceed
            else
                let now = runtime.Clock.Now()

                if snapshot.IsNone || now - lastRefresh >= refreshEvery then
                    snapshot <- Some(runtime.Observer.Observe now provider model)
                    lastRefresh <- now

                let current = snapshot.Value
                let decision = evaluateAt runtime provider model current now false

                match decision.BindingReason with
                | None ->
                    lastReason
                    |> Option.iter (fun _ -> runtime.Events.Write "release: back within pacing rules")

                    outcome <- Some PacingGateOutcome.Proceed
                | Some reason ->
                    let identity = $"{PacingReasonKind.code reason.Kind}: {reason.Detail}"

                    if lastReason <> Some identity then
                        let resumeAt = reason.ResumeAt.ToString("O", Globalization.CultureInfo.InvariantCulture)
                        runtime.Events.Write("hold " + provider + ": " + identity + "; resume estimate " + resumeAt)
                        lastReason <- Some identity

                    if now - started >= maximum then
                        outcome <- Some(PacingGateOutcome.Denied reason)
                    else
                        let untilEstimate = reason.ResumeAt - now

                        let delay =
                            if untilEstimate <= TimeSpan.Zero then
                                TimeSpan.FromSeconds 1.0
                            else
                                min PacingPolicy.defaults.PollInterval untilEstimate

                        runtime.Clock.Sleep delay

        outcome.Value
