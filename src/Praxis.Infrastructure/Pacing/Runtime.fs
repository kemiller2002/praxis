namespace Praxis.Infrastructure.Pacing

open System
open System.Threading
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingAdapters =
    /// Exhausted windows the previous reading saw that the current response
    /// omitted. They are carried so the hard limit stays visible, and each is
    /// reported `Stale` in coverage: carried evidence is never `Observed`.
    let carryMissingHard
        (previous: QuotaWindow list)
        (current: QuotaReading)
        (now: DateTimeOffset)
        : QuotaReading =
        let currentKeys = current.Windows |> List.map _.Key |> Set.ofList

        let carried =
            previous
            |> List.filter (fun window ->
                window.UsedPercent > PacingPolicy.defaults.HardUsagePercent
                && window.ResetsAt > now
                && not (currentKeys.Contains window.Key))

        let observedAt (window: QuotaWindow) = window.ObservedAt.ToString("O", Globalization.CultureInfo.InvariantCulture)

        { Windows = current.Windows @ carried
          Coverage =
            current.Coverage
            @ (carried
               |> List.map (fun window ->
                   { Key = window.Key
                     Status = WindowStatus.Stale $"carried from the reading at {observedAt window}; absent from this response" })) }

    /// Scoped windows the previous reading saw whose window has not reset:
    /// their disappearance is missing evidence, not proof of capacity.
    let private expectedScoped (previous: QuotaWindow list) (now: DateTimeOffset) =
        previous
        |> List.filter (fun window ->
            window.ResetsAt > now
            && match window.Scope with
               | QuotaScope.Global -> false
               | QuotaScope.Model _
               | QuotaScope.Surface _ -> true)
        |> List.map _.Key

    /// Production provider queries: Codex app-server and the Claude usage API.
    let queryProvider (provider: ProviderId) (adapter: PacingAdapterInfo) (expectedScoped: string list) (observedAt: DateTimeOffset) : Result<QuotaReading, string> =
        match provider with
        | ProviderId.Codex ->
            let bucket = adapter.Bucket |> Option.defaultValue PacingAdapterRules.codexDefaultBucket

            PacingProviders.queryCodex ()
            |> Result.bind (PacingNormalization.normalizeCodexResult bucket observedAt)
        | ProviderId.Claude ->
            PacingProviders.queryClaude observedAt
            |> Result.bind (PacingNormalization.normalizeClaudeUsage observedAt expectedScoped)

    /// One observation: query the provider through `query`, judge per-window
    /// completeness, and fall back to the cache only as stale evidence.
    let observeWith
        (query: ProviderId -> PacingAdapterInfo -> string list -> DateTimeOffset -> Result<QuotaReading, string>)
        (directory: string)
        (observedAt: DateTimeOffset)
        (provider: ProviderId)
        (model: string option)
        : ProviderSnapshot =
        let adapter = PacingAdapterRules.info provider model
        let cached = PacingPersistence.loadSnapshot directory provider adapter

        let previous =
            match cached with
            | Ok(Some snapshot) -> snapshot.Windows
            | Ok None
            | Error _ -> []

        let cacheNote =
            match cached with
            | Error message -> $"; {message}"
            | Ok _ -> ""

        let result = query provider adapter (expectedScoped previous observedAt) observedAt

        // The snapshot is a display/merge cache; hard holds live in hold state,
        // so a cache write failure does not change safety. It is still visible.
        let persist snapshot =
            match PacingPersistence.saveSnapshot directory snapshot with
            | Ok() -> ()
            | Error message -> PacingEventLog.diagnostic directory observedAt $"{ProviderId.code provider} snapshot cache write failed: {message}"

            snapshot

        match result with
        | Ok reading when WindowObservation.isComplete reading.Coverage ->
            persist
                { Provider = provider
                  Adapter = adapter
                  ObservedAt = observedAt
                  Windows = reading.Windows
                  Coverage = reading.Coverage
                  Freshness = ObservationFreshness.Fresh }
        | Ok reading ->
            let combined = carryMissingHard previous reading observedAt

            persist
                { Provider = provider
                  Adapter = adapter
                  ObservedAt = observedAt
                  Windows = combined.Windows
                  Coverage = combined.Coverage
                  Freshness =
                    ObservationFreshness.Stale(
                        $"provider quota response was incomplete: {WindowObservation.incompleteness reading.Coverage}"
                    ) }
        | Error message ->
            match cached with
            | Ok(Some snapshot) ->
                { snapshot with
                    Coverage =
                        snapshot.Windows
                        |> List.map (fun window ->
                            { Key = window.Key
                              Status = WindowStatus.Stale "cached reading; the provider could not be queried" })
                    Freshness = ObservationFreshness.Stale message }
            | Ok None
            | Error _ ->
                { Provider = provider
                  Adapter = adapter
                  ObservedAt = observedAt
                  Windows = []
                  Coverage = []
                  Freshness = ObservationFreshness.Unavailable(message + cacheNote) }

    let create (directory: string) : PacingRuntime =
        { Observer =
            { Observe =
                fun observedAt provider model ->
                    observeWith queryProvider directory observedAt provider model }
          State = PacingPersistence.stateStore directory
          Override = PacingPersistence.overrideStore directory
          Clock =
            { Now = fun () -> DateTimeOffset.UtcNow
              Sleep = fun delay -> Thread.Sleep delay }
          Events = PacingEventLog.sink directory
          Context = PacingContext.resolver
          MaxGateWait =
            fun provider ->
                match provider with
                | ProviderId.Claude -> TimeSpan.FromDays 6.0
                | ProviderId.Codex -> TimeSpan.FromDays 7.0 + TimeSpan.FromMinutes 1.0 }
