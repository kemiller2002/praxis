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
    let transaction (integrity: StateIntegrity) (decision: PacingDecision) : PacingTransaction =
        match integrity with
        | StateIntegrity.Intact ->
            { Decision = decision
              Integrity = integrity
              Write = Some decision.State }
        | StateIntegrity.Indeterminate _ ->
            { Decision = decision
              Integrity = integrity
              Write = None }

type private GateLoop =
    { LastRefresh: DateTimeOffset option
      Snapshot: ProviderSnapshot option
      LastReason: string option }

[<RequireQualifiedAccess>]
module PacingOperations =
    let private refreshEvery = TimeSpan.FromSeconds 60.0

    let decide
        (provider: string)
        (model: string option)
        (snapshot: ProviderSnapshot)
        (now: DateTimeOffset)
        (overridden: bool)
        (read: PacingStateRead)
        : PacingTransaction =
        let existing, integrity = PacingStateIntegrity.ofRead read

        let decision =
            Pacing.evaluate
                PacingPolicy.defaults
                { Now = now
                  Provider = provider
                  Model = model
                  Windows = snapshot.Windows
                  Freshness = snapshot.Freshness
                  Existing = existing
                  StateIntegrity = integrity
                  Override = overridden }

        PacingStateIntegrity.transaction integrity decision

    let private evaluateAt
        (runtime: PacingRuntime)
        (provider: string)
        (model: string option)
        (snapshot: ProviderSnapshot)
        (now: DateTimeOffset)
        (overridden: bool)
        : Result<PacingTransaction, PacingStoreFault> =
        runtime.State.Transact(decide provider model snapshot now overridden)

    let status
        (runtime: PacingRuntime)
        (stateDirectory: string)
        (provider: string)
        (model: string option)
        : Result<PacingStatusView, PacingStoreFault> =
        let now = runtime.Clock.Now()
        let snapshot = runtime.Observer.Observe now provider model
        let overridden = runtime.Override.IsEnabled()

        evaluateAt runtime provider model snapshot now overridden
        |> Result.map (fun transaction ->
            PacingStatusProjection.create now stateDirectory provider model overridden snapshot transaction.Integrity transaction.Decision)

    let resolveModel (runtime: PacingRuntime) (provider: string) (explicitModel: string option) (payload: string) =
        runtime.Context.ResolveModel provider explicitModel payload

    let setOverride (runtime: PacingRuntime) enabled = runtime.Override.SetEnabled enabled

    let quarantineState (runtime: PacingRuntime) = runtime.State.Quarantine(runtime.Clock.Now())

    /// Reasons an operator must resolve; waiting cannot clear them.
    let private terminal (reason: PacingReason) =
        match reason.Kind with
        | PacingReasonKind.StateIndeterminate -> true
        | PacingReasonKind.HardLimit
        | PacingReasonKind.WeeklyLead
        | PacingReasonKind.WeeklyLeadUnverified -> false

    let gate (runtime: PacingRuntime) (provider: string) (model: string option) : PacingGateOutcome =
        let started = runtime.Clock.Now()
        let maximum = runtime.MaxGateWait provider

        let release (loop: GateLoop) message =
            loop.LastReason |> Option.iter (fun _ -> runtime.Events.Write message)
            PacingGateOutcome.Proceed

        let rec step (loop: GateLoop) =
            if runtime.Override.IsEnabled() then
                release loop "release: override enabled"
            else
                let now = runtime.Clock.Now()

                let refreshed =
                    match loop.Snapshot, loop.LastRefresh with
                    | Some snapshot, Some last when now - last < refreshEvery -> { loop with Snapshot = Some snapshot }
                    | _ ->
                        { loop with
                            Snapshot = Some(runtime.Observer.Observe now provider model)
                            LastRefresh = Some now }

                match evaluateAt runtime provider model refreshed.Snapshot.Value now false with
                | Error fault ->
                    runtime.Events.Write $"fault {PacingStoreFault.code fault}: {PacingStoreFault.message fault}"
                    PacingGateOutcome.Faulted fault
                | Ok transaction ->
                    match transaction.Decision.BindingReason with
                    | None -> release refreshed "release: back within pacing rules"
                    | Some reason ->
                        let identity = $"{PacingReasonKind.code reason.Kind}: {reason.Detail}"

                        if refreshed.LastReason <> Some identity then
                            let resumeAt = reason.ResumeAt.ToString("O", Globalization.CultureInfo.InvariantCulture)
                            runtime.Events.Write($"hold {provider}: {identity}; resume estimate {resumeAt}")

                        let next = { refreshed with LastReason = Some identity }

                        if terminal reason || now - started >= maximum then
                            PacingGateOutcome.Denied reason
                        else
                            let untilEstimate = reason.ResumeAt - now

                            let delay =
                                if untilEstimate <= TimeSpan.Zero then TimeSpan.FromSeconds 1.0
                                else min PacingPolicy.defaults.PollInterval untilEstimate

                            runtime.Clock.Sleep delay
                            step next

        step { LastRefresh = None; Snapshot = None; LastReason = None }
