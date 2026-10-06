namespace Praxis.Application.Pacing

open System
open Praxis.Domain.Pacing

/// Pure mapping from a persisted-state read to the domain's integrity input.
/// This is the boundary at which a persistence failure becomes a safety fact:
/// unreadable or newer state is indeterminate, never empty.
[<RequireQualifiedAccess>]
module PacingStateIntegrity =
    let ofRead (read: PacingStateRead) : PacingState * StateIntegrity =
        match read with
        | PacingStateRead.Absent -> PacingState.empty, StateIntegrity.Intact
        | PacingStateRead.Current(state, _) -> state, StateIntegrity.Intact
        | PacingStateRead.Migrated(state, _) -> state, StateIntegrity.Intact
        | PacingStateRead.Unreadable reason -> PacingState.empty, StateIntegrity.Indeterminate $"persisted state is unreadable: {reason}"
        | PacingStateRead.Unsupported version ->
            PacingState.empty, StateIntegrity.Indeterminate $"persisted state schema {version} is newer than this Praxis supports"

    /// Indeterminate state is preserved as evidence: the transaction never
    /// overwrites it with whatever the decision would otherwise persist.
    let transaction (now: DateTimeOffset) (model: ModelIdentity) (existing: PacingState) (integrity: StateIntegrity) (decision: PacingDecision) : PacingTransaction =
        match integrity with
        | StateIntegrity.Intact ->
            { Decision = decision
              Integrity = integrity
              Write = Some decision.State
              Model = model
              Events = PacingEvents.transitions now existing decision.State }
        | StateIntegrity.Indeterminate _ ->
            { Decision = decision
              Integrity = integrity
              Write = None
              Model = model
              Events = [] }

type private GateLoop =
    { LastRefresh: DateTimeOffset option
      Snapshot: ProviderSnapshot option
      LastReason: string option
      Emitted: PacingEvent option }

[<RequireQualifiedAccess>]
module PacingOperations =
    let private refreshEvery = TimeSpan.FromSeconds 60.0

    /// The model families a request can be recognized as: the adapter's rule
    /// table plus every family a scoped window or persisted hold names.
    let modelIdentity (snapshot: ProviderSnapshot) (existing: PacingState) (model: string option) =
        let scoped =
            (snapshot.Windows |> List.map _.Scope) @ (existing.Holds.Values |> Seq.map _.Scope |> Seq.toList)
            |> List.choose QuotaScope.family

        ModelIdentity.resolve (snapshot.Adapter.ModelFamilies @ scoped) model

    let decide
        (provider: ProviderId)
        (model: string option)
        (snapshot: ProviderSnapshot)
        (now: DateTimeOffset)
        (overridden: bool)
        (read: PacingStateRead)
        : PacingTransaction =
        let existing, integrity = PacingStateIntegrity.ofRead read
        let identity = modelIdentity snapshot existing model

        let decision =
            Pacing.evaluate
                PacingPolicy.defaults
                { Now = now
                  Provider = provider
                  Model = identity
                  Windows = snapshot.Windows
                  Freshness = snapshot.Freshness
                  Existing = existing
                  StateIntegrity = integrity
                  Override = overridden }

        PacingStateIntegrity.transaction now identity existing integrity decision

    /// Records an event unless it repeats the last recorded transition.
    let private record (runtime: PacingRuntime) (event: PacingEvent) =
        if not (PacingEvents.isDuplicate (runtime.Events.Last()) event) then
            runtime.Events.Record event

    let private evaluateAt
        (runtime: PacingRuntime)
        (provider: ProviderId)
        (model: string option)
        (snapshot: ProviderSnapshot)
        (now: DateTimeOffset)
        (overridden: bool)
        : Result<PacingTransaction, PacingStoreFault> =
        let outcome = runtime.State.Transact(decide provider model snapshot now overridden)

        match outcome with
        | Ok transaction -> transaction.Events |> List.iter (record runtime)
        | Error _ -> ()

        outcome

    let status
        (runtime: PacingRuntime)
        (stateDirectory: string)
        (provider: ProviderId)
        (model: string option)
        : Result<PacingStatusView, PacingStoreFault> =
        let now = runtime.Clock.Now()
        let snapshot = runtime.Observer.Observe now provider model
        let overridden = runtime.Override.IsEnabled()

        evaluateAt runtime provider model snapshot now overridden
        |> Result.map (fun transaction ->
            PacingStatusProjection.create
                now
                stateDirectory
                provider
                transaction.Model
                overridden
                snapshot
                transaction.Integrity
                transaction.Decision
                (runtime.Events.Last()))

    let resolveModel (runtime: PacingRuntime) (provider: ProviderId) (explicitModel: string option) (payload: string) =
        runtime.Context.ResolveModel provider explicitModel payload

    /// Turns the override on or off; a change of state is recorded as
    /// `override-enabled` / `override-disabled`.
    let setOverride (runtime: PacingRuntime) enabled =
        let before = runtime.Override.IsEnabled()

        runtime.Override.SetEnabled enabled
        |> Result.map (fun () ->
            if before <> enabled then
                let code = if enabled then PacingEventCode.OverrideEnabled else PacingEventCode.OverrideDisabled
                let detail = if enabled then "pacing override enabled; gates proceed without evaluation" else "pacing override disabled"
                runtime.Events.Record(PacingEvents.create code (runtime.Clock.Now()) None None None detail None None))

    let quarantineState (runtime: PacingRuntime) = runtime.State.Quarantine(runtime.Clock.Now())

    /// Reasons an operator must resolve; waiting cannot clear them.
    let private terminal (reason: PacingReason) =
        match reason.Kind with
        | PacingReasonKind.StateIndeterminate -> true
        | PacingReasonKind.HardLimit
        | PacingReasonKind.WeeklyLead
        | PacingReasonKind.WeeklyLeadUnverified -> false

    /// The event a gate records the first time it is held by a reason in this
    /// invocation: a hold that already existed is `hold-retained`; state that
    /// cannot be trusted is `state-fault`. New holds are recorded by the
    /// state transition itself (`hold-started` / `hard-limit`).
    let private heldEvent (now: DateTimeOffset) (provider: ProviderId) (decision: PacingDecision) (reason: PacingReason) =
        match reason.Kind with
        | PacingReasonKind.StateIndeterminate ->
            Some(PacingEvents.create PacingEventCode.StateFault now (Some provider) None (Some reason.Kind) reason.Detail None None)
        | _ ->
            decision.State.Holds.Values
            |> Seq.tryFind (fun hold -> hold.Key = reason.WindowKey && hold.Since < now)
            |> Option.map (fun hold -> PacingEvents.ofHold PacingEventCode.HoldRetained now hold reason.Detail)

    let gate (runtime: PacingRuntime) (provider: ProviderId) (model: string option) : PacingGateOutcome =
        let started = runtime.Clock.Now()
        let maximum = runtime.MaxGateWait provider

        let emit (loop: GateLoop) (event: PacingEvent) =
            if loop.Emitted |> Option.exists (fun previous -> PacingEvents.identity previous = PacingEvents.identity event) then
                loop
            else
                record runtime event
                { loop with Emitted = Some event }

        let rec step (loop: GateLoop) =
            if runtime.Override.IsEnabled() then
                PacingGateOutcome.Proceed
            else
                let now = runtime.Clock.Now()

                let refreshed =
                    match loop.Snapshot, loop.LastRefresh with
                    | Some snapshot, Some last when now - last < refreshEvery -> { loop with Snapshot = Some snapshot }
                    | _ ->
                        let snapshot = runtime.Observer.Observe now provider model
                        let next = { loop with Snapshot = Some snapshot; LastRefresh = Some now }

                        match snapshot.Freshness with
                        | ObservationFreshness.Unavailable reason ->
                            emit next (PacingEvents.create PacingEventCode.ProviderUnavailable now (Some provider) None None reason None None)
                        | ObservationFreshness.Fresh
                        | ObservationFreshness.Stale _ -> next

                match evaluateAt runtime provider model refreshed.Snapshot.Value now false with
                | Error fault ->
                    emit refreshed (PacingEvents.create PacingEventCode.StateFault now (Some provider) None None $"{PacingStoreFault.code fault}: {PacingStoreFault.message fault}" None None)
                    |> ignore

                    PacingGateOutcome.Faulted fault
                | Ok transaction ->
                    match transaction.Decision.BindingReason with
                    | None -> PacingGateOutcome.Proceed
                    | Some reason ->
                        // One held event per binding reason per invocation.
                        let reasonIdentity = PacingReasonKind.code reason.Kind + "|" + reason.WindowKey

                        let next =
                            if refreshed.LastReason = Some reasonIdentity then
                                refreshed
                            else
                                match heldEvent now provider transaction.Decision reason with
                                | Some event -> emit refreshed event
                                | None -> refreshed
                            |> fun loop -> { loop with LastReason = Some reasonIdentity }

                        if terminal reason || now - started >= maximum then
                            PacingGateOutcome.Denied reason
                        else
                            let untilEstimate = reason.ResumeAt - now

                            let delay =
                                if untilEstimate <= TimeSpan.Zero then TimeSpan.FromSeconds 1.0
                                else min PacingPolicy.defaults.PollInterval untilEstimate

                            runtime.Clock.Sleep delay
                            step next

        step { LastRefresh = None; Snapshot = None; LastReason = None; Emitted = None }
