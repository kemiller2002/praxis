namespace Praxis.Infrastructure.Pacing

open System
open System.Globalization
open System.Text.Json
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingNormalization =
    let private labelFor (duration: TimeSpan) (scope: QuotaScope) : string =
        if Math.Abs(duration.TotalMinutes - 300.0) < 0.5 then
            "5H session"
        elif Math.Abs(duration.TotalMinutes - 10080.0) < 0.5 then
            match scope with
            | QuotaScope.Global -> "7D weekly"
            | QuotaScope.Model model -> $"7D weekly {model}"
            | QuotaScope.Surface surface -> $"7D weekly {surface}"
        else
            duration.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "m allowance"

    let normalizeCodexResult
        (bucket: string)
        (observedAt: DateTimeOffset)
        (json: string)
        : Result<QuotaWindow list * bool, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let bucketElement =
                match PacingJson.tryProperty "rateLimitsByLimitId" root with
                | Some buckets when buckets.ValueKind = JsonValueKind.Object ->
                    PacingJson.tryProperty bucket buckets
                | _ ->
                    match PacingJson.tryProperty "rateLimits" root with
                    | Some single when single.ValueKind = JsonValueKind.Object -> Some single
                    | _ -> None

            match bucketElement with
            | None -> Error $"Codex did not report quota bucket '{bucket}'"
            | Some bucketRow ->
                let mutable expected = 0

                let windows =
                    [ for slot in [ "primary"; "secondary" ] do
                          match PacingJson.tryProperty slot bucketRow with
                          | Some row when row.ValueKind = JsonValueKind.Object ->
                              expected <- expected + 1

                              match
                                  PacingJson.readNumber "usedPercent" row,
                                  PacingJson.readNumber "windowDurationMins" row,
                                  PacingJson.tryProperty "resetsAt" row |> Option.bind PacingJson.tryEpoch
                              with
                              | Some used, Some minutes, Some reset when used >= 0m && used <= 100m && minutes > 0m ->
                                  let duration = TimeSpan.FromMinutes(float minutes)
                                  let key = $"{bucket}:{int (Math.Round duration.TotalMinutes)}m"

                                  yield
                                      { Provider = "codex"
                                        Key = key
                                        Label = labelFor duration QuotaScope.Global
                                        Scope = QuotaScope.Global
                                        UsedPercent = used
                                        Duration = duration
                                        ResetsAt = reset
                                        ObservedAt = observedAt }
                              | _ -> ()
                          | _ -> () ]

                if windows.IsEmpty then
                    Error $"Codex bucket '{bucket}' contained no usable quota windows"
                else
                    Ok(windows, windows.Length = expected)
        with error ->
            Error $"Codex quota response could not be parsed: {error.Message}"

    let private scopeName (name: string) (scope: JsonElement) : string option =
        PacingJson.tryProperty name scope
        |> Option.bind (PacingJson.tryProperty "display_name")
        |> Option.bind PacingJson.tryString

    let normalizeClaudeUsage
        (observedAt: DateTimeOffset)
        (json: string)
        : Result<QuotaWindow list * bool, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let mutable windows = Map.empty<string, QuotaWindow>

            let add key scope used reset duration =
                if used >= 0m && used <= 100m && reset > DateTimeOffset.UnixEpoch then
                    windows <-
                        windows.Add(
                            key,
                            { Provider = "claude"
                              Key = key
                              Label = labelFor duration scope
                              Scope = scope
                              UsedPercent = used
                              Duration = duration
                              ResetsAt = reset
                              ObservedAt = observedAt }
                        )

            let topLevel key duration =
                match PacingJson.tryProperty key root with
                | Some row when row.ValueKind = JsonValueKind.Object ->
                    let used =
                        match PacingJson.readNumber "utilization" row with
                        | Some value -> Some value
                        | None -> PacingJson.readNumber "used_percentage" row

                    match used, PacingJson.readReset "resets_at" row with
                    | Some percent, Some reset -> add key QuotaScope.Global percent reset duration
                    | _ -> ()
                | _ -> ()

            topLevel "five_hour" (TimeSpan.FromMinutes 300.0)
            topLevel "seven_day" (TimeSpan.FromMinutes 10080.0)

            match PacingJson.tryProperty "limits" root with
            | Some limits when limits.ValueKind = JsonValueKind.Array ->
                for row in limits.EnumerateArray() do
                    let kind =
                        PacingJson.tryProperty "kind" row
                        |> Option.bind PacingJson.tryString
                        |> Option.defaultValue ""

                    let scope = PacingJson.tryProperty "scope" row
                    let model = scope |> Option.bind (scopeName "model")
                    let surface = scope |> Option.bind (scopeName "surface")
                    let hasScope = model.IsSome || surface.IsSome

                    let key, quotaScope, duration =
                        match kind, model, surface, hasScope with
                        | "session", None, None, false ->
                            Some "five_hour", QuotaScope.Global, TimeSpan.FromMinutes 300.0
                        | "weekly_all", None, None, false ->
                            Some "seven_day", QuotaScope.Global, TimeSpan.FromMinutes 10080.0
                        | weekly, Some name, _, _ when weekly.StartsWith("weekly", StringComparison.Ordinal) ->
                            Some($"seven_day:{name}"), QuotaScope.Model name, TimeSpan.FromMinutes 10080.0
                        | weekly, None, Some name, _ when weekly.StartsWith("weekly", StringComparison.Ordinal) ->
                            Some($"seven_day:surface:{name}"), QuotaScope.Surface name, TimeSpan.FromMinutes 10080.0
                        | _ ->
                            None, QuotaScope.Global, TimeSpan.Zero

                    match key, PacingJson.readNumber "percent" row, PacingJson.readReset "resets_at" row with
                    | Some windowKey, Some used, Some reset -> add windowKey quotaScope used reset duration
                    | _ -> ()
            | _ -> ()

            let result = windows |> Map.toList |> List.map snd
            let keys = result |> List.map _.Key |> Set.ofList
            let complete = keys.Contains "five_hour" && keys.Contains "seven_day"

            if result.IsEmpty then
                Error "Claude returned no usable quota windows"
            else
                Ok(result, complete)
        with error ->
            Error $"Claude quota response could not be parsed: {error.Message}"
