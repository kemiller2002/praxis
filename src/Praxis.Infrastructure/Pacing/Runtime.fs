namespace Praxis.Infrastructure.Pacing

open System
open System.Globalization
open System.IO
open System.Threading
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingAdapters =
    let private mergeMissingHard
        (previous: QuotaWindow list)
        (current: QuotaWindow list)
        (now: DateTimeOffset)
        : QuotaWindow list =
        let currentKeys = current |> List.map _.Key |> Set.ofList

        let retained =
            previous
            |> List.filter (fun window ->
                window.UsedPercent > PacingPolicy.defaults.HardUsagePercent
                && window.ResetsAt > now
                && not (currentKeys.Contains window.Key))

        current @ retained

    /// Human diagnostics only (`pace.log`), never safety evidence. An
    /// unwritable log must not change a gate decision, so only filesystem
    /// failures are tolerated here. Typed pacing telemetry replaces this
    /// channel under PRX-QUAL-008 (kemiller2002/praxis#160).
    let private writeLog (directory: string) (text: string) : unit =
        try
            Directory.CreateDirectory directory |> ignore
            let timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
            let line = timestamp + " [" + string Environment.ProcessId + "] " + text + Environment.NewLine
            File.AppendAllText(Path.Combine(directory, "pace.log"), line)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

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

    let private observe
        (directory: string)
        (observedAt: DateTimeOffset)
        (provider: string)
        (model: string option)
        : ProviderSnapshot =
        let cached = PacingPersistence.loadSnapshot directory provider

        let previous =
            match cached with
            | Ok(Some snapshot) -> snapshot.Windows
            | Ok None
            | Error _ -> []

        let cacheNote =
            match cached with
            | Error message -> $"; {message}"
            | Ok _ -> ""

        let result =
            match provider with
            | "codex" ->
                let bucket =
                    match model with
                    | Some value when value.Contains("spark", StringComparison.OrdinalIgnoreCase) -> "codex_bengalfox"
                    | _ -> "codex"

                PacingProviders.queryCodex ()
                |> Result.bind (PacingNormalization.normalizeCodexResult bucket observedAt)
            | "claude" ->
                PacingProviders.queryClaude observedAt
                |> Result.bind (PacingNormalization.normalizeClaudeUsage observedAt (expectedScoped previous observedAt))
            | other -> Error $"unknown pacing provider '{other}'"

        // The snapshot is a display/merge cache; hard holds live in hold state,
        // so a cache write failure does not change safety. It is still visible.
        let persist snapshot =
            match PacingPersistence.saveSnapshot directory snapshot with
            | Ok() -> ()
            | Error message -> writeLog directory $"diagnostic: snapshot cache write failed: {message}"

            snapshot

        match result with
        | Ok reading when WindowObservation.isComplete reading.Coverage ->
            persist
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = reading.Windows
                  Coverage = reading.Coverage
                  Freshness = ObservationFreshness.Fresh }
        | Ok reading ->
            let combined = mergeMissingHard previous reading.Windows observedAt

            persist
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = combined
                  Coverage = reading.Coverage
                  Freshness =
                    ObservationFreshness.Stale(
                        $"provider quota response was incomplete: {WindowObservation.incompleteness reading.Coverage}"
                    ) }
        | Error message ->
            match cached with
            | Ok(Some snapshot) ->
                { snapshot with
                    Freshness = ObservationFreshness.Stale message }
            | Ok None
            | Error _ ->
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = []
                  Coverage = []
                  Freshness = ObservationFreshness.Unavailable(message + cacheNote) }

    let create (directory: string) : PacingRuntime =
        { Observer =
            { Observe =
                fun observedAt provider model ->
                    observe directory observedAt provider model }
          State = PacingPersistence.stateStore directory
          Override = PacingPersistence.overrideStore directory
          Clock =
            { Now = fun () -> DateTimeOffset.UtcNow
              Sleep = fun delay -> Thread.Sleep delay }
          Events =
            { Write = writeLog directory }
          Context = PacingContext.resolver
          MaxGateWait =
            fun provider ->
                if String.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase) then
                    TimeSpan.FromDays 6.0
                else
                    TimeSpan.FromDays 7.0 + TimeSpan.FromMinutes 1.0 }
