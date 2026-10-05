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

    let private observe
        (directory: string)
        (observedAt: DateTimeOffset)
        (provider: string)
        (model: string option)
        : ProviderSnapshot =
        Directory.CreateDirectory directory |> ignore

        let previous =
            PacingPersistence.loadSnapshot directory provider
            |> Option.map _.Windows
            |> Option.defaultValue []

        let result =
            match provider with
            | "codex" ->
                let bucket =
                    match model with
                    | Some value when value.Contains("spark", StringComparison.OrdinalIgnoreCase) ->
                        "codex_bengalfox"
                    | _ ->
                        "codex"

                PacingProviders.queryCodex ()
                |> Result.bind (PacingNormalization.normalizeCodexResult bucket observedAt)
            | "claude" ->
                PacingProviders.queryClaude observedAt
                |> Result.bind (PacingNormalization.normalizeClaudeUsage observedAt)
            | other ->
                Error $"unknown pacing provider '{other}'"

        match result with
        | Ok(windows, true) ->
            let snapshot =
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = windows
                  Freshness = ObservationFreshness.Fresh }

            PacingPersistence.saveSnapshot directory snapshot
            snapshot
        | Ok(windows, false) ->
            let combined = mergeMissingHard previous windows observedAt

            let snapshot =
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = combined
                  Freshness = ObservationFreshness.Stale "provider quota response was incomplete" }

            if not combined.IsEmpty then
                PacingPersistence.saveSnapshot directory snapshot

            snapshot
        | Error message ->
            match PacingPersistence.loadSnapshot directory provider with
            | Some cached ->
                { cached with
                    Freshness = ObservationFreshness.Stale message }
            | None ->
                { Provider = provider
                  ObservedAt = observedAt
                  Windows = []
                  Freshness = ObservationFreshness.Unavailable message }

    let private writeLog (directory: string) (text: string) : unit =
        try
            Directory.CreateDirectory directory |> ignore
            let timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
            let line = timestamp + " [" + string Environment.ProcessId + "] " + text + Environment.NewLine
            File.AppendAllText(Path.Combine(directory, "pace.log"), line)
        with _ ->
            ()

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
