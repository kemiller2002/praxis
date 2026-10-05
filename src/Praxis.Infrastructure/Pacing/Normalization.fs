namespace Praxis.Infrastructure.Pacing

open System
open System.Globalization
open System.Text.Json
open Praxis.Application.Pacing
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

    type private Candidate = { Key: string; Window: Result<QuotaWindow, string> }

    /// Per-window coverage: every expected key is Observed, Invalid (present
    /// but malformed) or Missing. Unexpected but valid windows are Observed too.
    let private coverage (expected: string list) (candidates: Candidate list) : QuotaReading =
        let windows =
            candidates
            |> List.choose (fun candidate -> candidate.Window |> Result.toOption)
            |> List.fold (fun acc window -> acc |> Map.add window.Key window) Map.empty

        let invalid =
            candidates
            |> List.choose (fun candidate ->
                match candidate.Window with
                | Error reason when not (windows.ContainsKey candidate.Key) -> Some(candidate.Key, reason)
                | _ -> None)
            |> Map.ofList

        let statusOf key =
            if windows.ContainsKey key then WindowStatus.Observed
            else
                match invalid |> Map.tryFind key with
                | Some reason -> WindowStatus.Invalid reason
                | None -> WindowStatus.Missing

        let keys =
            (expected @ (windows |> Map.toList |> List.map fst) @ (invalid |> Map.toList |> List.map fst))
            |> List.distinct

        { Windows = windows |> Map.toList |> List.map snd
          Coverage = keys |> List.map (fun key -> { Key = key; Status = statusOf key }) }

    let private window provider key scope used duration reset observedAt =
        { Provider = provider
          Key = key
          Label = labelFor duration scope
          Scope = scope
          UsedPercent = used
          Duration = duration
          ResetsAt = reset
          ObservedAt = observedAt }

    /// Codex reports a primary (session) and secondary (weekly) window per
    /// quota bucket; both are always expected. A response carrying only the
    /// primary window is incomplete, never fresh.
    let normalizeCodexResult (bucket: string) (observedAt: DateTimeOffset) (json: string) : Result<QuotaReading, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let bucketElement =
                match PacingJson.tryProperty "rateLimitsByLimitId" root with
                | Some buckets when buckets.ValueKind = JsonValueKind.Object -> PacingJson.tryProperty bucket buckets
                | _ ->
                    match PacingJson.tryProperty "rateLimits" root with
                    | Some single when single.ValueKind = JsonValueKind.Object -> Some single
                    | _ -> None

            match bucketElement with
            | None -> Error $"Codex did not report quota bucket '{bucket}'"
            | Some bucketRow ->
                let slotKey slot = $"{bucket}:{slot}"

                let candidates =
                    [ "primary"; "secondary" ]
                    |> List.choose (fun slot ->
                        match PacingJson.tryProperty slot bucketRow with
                        | Some row when row.ValueKind = JsonValueKind.Object ->
                            match
                                PacingJson.readNumber "usedPercent" row,
                                PacingJson.readNumber "windowDurationMins" row,
                                PacingJson.tryProperty "resetsAt" row |> Option.bind PacingJson.tryEpoch
                            with
                            | Some used, Some minutes, Some reset when used >= 0m && used <= 100m && minutes > 0m ->
                                let duration = TimeSpan.FromMinutes(float minutes)
                                let key = $"{bucket}:{int (Math.Round duration.TotalMinutes)}m"
                                Some { Key = slotKey slot; Window = Ok(window "codex" key QuotaScope.Global used duration reset observedAt) }
                            | _ -> Some { Key = slotKey slot; Window = Error "usedPercent, windowDurationMins or resetsAt is missing or out of range" }
                        | Some _ -> Some { Key = slotKey slot; Window = Error "slot is not an object" }
                        | None -> None)

                // Coverage is tracked per slot; the window key is duration-based.
                let slotCoverage =
                    [ "primary"; "secondary" ]
                    |> List.map (fun slot ->
                        let key = slotKey slot

                        match candidates |> List.tryFind (fun candidate -> candidate.Key = key) with
                        | Some { Window = Ok _ } -> { Key = key; Status = WindowStatus.Observed }
                        | Some { Window = Error reason } -> { Key = key; Status = WindowStatus.Invalid reason }
                        | None -> { Key = key; Status = WindowStatus.Missing })

                let windows = candidates |> List.choose (fun candidate -> candidate.Window |> Result.toOption)

                if windows.IsEmpty then
                    Error $"Codex bucket '{bucket}' contained no usable quota windows"
                else
                    Ok { Windows = windows; Coverage = slotCoverage }
        with :? JsonException as error ->
            Error $"Codex quota response could not be parsed: {error.Message}"

    let private scopeName (name: string) (scope: JsonElement) : string option =
        PacingJson.tryProperty name scope
        |> Option.bind (PacingJson.tryProperty "display_name")
        |> Option.bind PacingJson.tryString


    /// Claude always reports the global session and weekly windows. Scoped
    /// weekly windows (`expectedScoped`, e.g. keys present in the previous
    /// reading whose window has not reset) are expected too: a scoped window
    /// that disappears makes the reading incomplete rather than silently fresh.
    let normalizeClaudeUsage
        (observedAt: DateTimeOffset)
        (expectedScoped: string list)
        (json: string)
        : Result<QuotaReading, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            let session = TimeSpan.FromMinutes 300.0
            let week = TimeSpan.FromMinutes 10080.0

            let candidate key scope used reset duration =
                match used, reset with
                | Some percent, Some at when percent >= 0m && percent <= 100m && at > DateTimeOffset.UnixEpoch ->
                    { Key = key; Window = Ok(window "claude" key scope percent duration at observedAt) }
                | _ -> { Key = key; Window = Error "utilization or resets_at is missing or out of range" }

            let topLevel key duration =
                match PacingJson.tryProperty key root with
                | Some row when row.ValueKind = JsonValueKind.Object ->
                    let used =
                        PacingJson.readNumber "utilization" row
                        |> Option.orElse (PacingJson.readNumber "used_percentage" row)

                    Some(candidate key QuotaScope.Global used (PacingJson.readReset "resets_at" row) duration)
                | Some row when row.ValueKind <> JsonValueKind.Null ->
                    Some { Key = key; Window = Error "window is not an object" }
                | _ -> None

            let limitCandidate (row: JsonElement) =
                let kind = PacingJson.tryProperty "kind" row |> Option.bind PacingJson.tryString |> Option.defaultValue ""
                let scope = PacingJson.tryProperty "scope" row
                let model = scope |> Option.bind (scopeName "model")
                let surface = scope |> Option.bind (scopeName "surface")
                let weekly = kind.StartsWith("weekly", StringComparison.Ordinal)

                let mapped =
                    match kind, model, surface with
                    | "session", None, None -> Some("five_hour", QuotaScope.Global, session)
                    | "weekly_all", None, None -> Some("seven_day", QuotaScope.Global, week)
                    | _, Some name, _ when weekly -> Some($"seven_day:{name}", QuotaScope.Model name, week)
                    | _, None, Some name when weekly -> Some($"seven_day:surface:{name}", QuotaScope.Surface name, week)
                    | _ -> None

                mapped
                |> Option.map (fun (key, quotaScope, duration) ->
                    candidate key quotaScope (PacingJson.readNumber "percent" row) (PacingJson.readReset "resets_at" row) duration)

            let limits =
                match PacingJson.tryProperty "limits" root with
                | Some rows when rows.ValueKind = JsonValueKind.Array -> rows.EnumerateArray() |> Seq.choose limitCandidate |> Seq.toList
                | _ -> []

            let candidates = ([ topLevel "five_hour" session; topLevel "seven_day" week ] |> List.choose id) @ limits
            let reading = coverage ([ "five_hour"; "seven_day" ] @ expectedScoped) candidates

            if reading.Windows.IsEmpty then
                Error "Claude returned no usable quota windows"
            else
                Ok reading
        with :? JsonException as error ->
            Error $"Claude quota response could not be parsed: {error.Message}"
