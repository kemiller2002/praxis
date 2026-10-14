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
            | QuotaScope.Model family -> $"7D weekly {ModelFamily.value family}"
            | QuotaScope.Surface surface -> $"7D weekly {surface}"
        else
            duration.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "m allowance"

    /// What the adapter made of one reported window.
    [<RequireQualifiedAccess>]
    type private Outcome =
        | Accepted of QuotaWindow
        | Rejected of WindowStatus

    type private Candidate = { Key: string; Outcome: Outcome }

    /// Per-window coverage: every expected key is Observed, Invalid, Stale,
    /// Unsupported or Missing. Unexpected but valid windows are Observed too;
    /// unexpected rejected ones are reported, never dropped.
    let private coverage (expected: string list) (candidates: Candidate list) : QuotaReading =
        let windows =
            candidates
            |> List.choose (fun candidate ->
                match candidate.Outcome with
                | Outcome.Accepted window -> Some window
                | Outcome.Rejected _ -> None)
            |> List.fold (fun acc (window: QuotaWindow) -> acc |> Map.add window.Key window) Map.empty

        let rejected =
            candidates
            |> List.choose (fun candidate ->
                match candidate.Outcome with
                | Outcome.Rejected status when not (windows.ContainsKey candidate.Key) -> Some(candidate.Key, status)
                | _ -> None)
            |> List.fold (fun acc (key, status) -> if Map.containsKey key acc then acc else Map.add key status acc) Map.empty

        let statusOf key =
            if windows.ContainsKey key then WindowStatus.Observed
            else rejected |> Map.tryFind key |> Option.defaultValue WindowStatus.Missing

        let keys =
            (expected @ (windows |> Map.toList |> List.map fst) @ (rejected |> Map.toList |> List.map fst))
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

    let private iso (value: DateTimeOffset) = value.ToString("O", CultureInfo.InvariantCulture)

    /// A provider that reports a window whose reset already passed is
    /// describing the previous quota window: stale, not evidence about now.
    let private staleAcrossReset (reset: DateTimeOffset) (observedAt: DateTimeOffset) =
        WindowStatus.Stale $"provider reported a window that reset at {iso reset}, before this reading at {iso observedAt}"

    /// Codex reports a primary (session) and secondary (weekly) window per
    /// quota bucket; both are always expected. A response carrying only the
    /// primary window is incomplete, never fresh. Other window-shaped members
    /// of the bucket are unsupported (response drift), not ignored.
    let normalizeCodexResult (bucket: QuotaBucket) (observedAt: DateTimeOffset) (json: string) : Result<QuotaReading, string> =
        let bucketName = QuotaBucket.value bucket

        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let bucketElement =
                match PacingJson.tryProperty "rateLimitsByLimitId" root with
                | Some buckets when buckets.ValueKind = JsonValueKind.Object -> PacingJson.tryProperty bucketName buckets
                | _ ->
                    match PacingJson.tryProperty "rateLimits" root with
                    | Some single when single.ValueKind = JsonValueKind.Object -> Some single
                    | _ -> None

            match bucketElement with
            | None -> Error $"Codex did not report quota bucket '{bucketName}'"
            | Some bucketRow ->
                let slotKey slot = $"{bucketName}:{slot}"

                let slotCandidate slot (row: JsonElement) =
                    if row.ValueKind <> JsonValueKind.Object then
                        { Key = slotKey slot; Outcome = Outcome.Rejected(WindowStatus.Invalid "slot is not an object") }
                    else
                        match
                            PacingJson.readNumber "usedPercent" row,
                            PacingJson.readNumber "windowDurationMins" row,
                            PacingJson.tryProperty "resetsAt" row |> Option.bind PacingJson.tryEpoch
                        with
                        | Some used, Some minutes, Some reset when used >= 0m && used <= 100m && minutes > 0m ->
                            if reset <= observedAt then
                                { Key = slotKey slot; Outcome = Outcome.Rejected(staleAcrossReset reset observedAt) }
                            else
                                let duration = TimeSpan.FromMinutes(float minutes)
                                let key = $"{bucketName}:{int (Math.Round duration.TotalMinutes)}m"
                                { Key = slotKey slot; Outcome = Outcome.Accepted(window ProviderId.Codex key QuotaScope.Global used duration reset observedAt) }
                        | _ ->
                            { Key = slotKey slot
                              Outcome = Outcome.Rejected(WindowStatus.Invalid "usedPercent, windowDurationMins or resetsAt is missing or out of range") }

                let supported =
                    PacingAdapterRules.codexSlots
                    |> List.choose (fun slot ->
                        PacingJson.tryProperty slot bucketRow
                        |> Option.filter (fun row -> row.ValueKind <> JsonValueKind.Null)
                        |> Option.map (slotCandidate slot))

                let unsupported =
                    bucketRow.EnumerateObject()
                    |> Seq.filter (fun property ->
                        not (List.contains property.Name PacingAdapterRules.codexSlots)
                        && property.Value.ValueKind = JsonValueKind.Object
                        && (PacingJson.tryProperty "usedPercent" property.Value).IsSome)
                    |> Seq.map (fun property ->
                        { Key = slotKey property.Name
                          Outcome = Outcome.Rejected(WindowStatus.Unsupported $"window slot '{property.Name}' is not supported by {PacingAdapterRules.CodexRulesVersion}") })
                    |> Seq.toList

                // Coverage is tracked per slot; the window key is duration-based.
                let candidates = supported @ unsupported

                let slotCoverage =
                    (PacingAdapterRules.codexSlots |> List.map slotKey) @ (unsupported |> List.map _.Key)
                    |> List.map (fun key ->
                        match candidates |> List.tryFind (fun candidate -> candidate.Key = key) with
                        | Some { Outcome = Outcome.Accepted _ } -> { Key = key; Status = WindowStatus.Observed }
                        | Some { Outcome = Outcome.Rejected status } -> { Key = key; Status = status }
                        | None -> { Key = key; Status = WindowStatus.Missing })

                let windows =
                    candidates
                    |> List.choose (fun candidate ->
                        match candidate.Outcome with
                        | Outcome.Accepted window -> Some window
                        | Outcome.Rejected _ -> None)

                if windows.IsEmpty then
                    Error $"Codex bucket '{bucketName}' contained no usable quota windows"
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
    /// Window kinds the rules do not map are unsupported, never dropped.
    let normalizeClaudeUsage
        (observedAt: DateTimeOffset)
        (expectedScoped: string list)
        (json: string)
        : Result<QuotaReading, string> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let candidate key scope used reset duration =
                match used, reset with
                | Some percent, Some at when percent >= 0m && percent <= 100m && at > DateTimeOffset.UnixEpoch ->
                    if at <= observedAt then
                        { Key = key; Outcome = Outcome.Rejected(staleAcrossReset at observedAt) }
                    else
                        { Key = key; Outcome = Outcome.Accepted(window ProviderId.Claude key scope percent duration at observedAt) }
                | _ -> { Key = key; Outcome = Outcome.Rejected(WindowStatus.Invalid "utilization or resets_at is missing or out of range") }

            let usedOf (row: JsonElement) =
                PacingJson.readNumber "utilization" row
                |> Option.orElse (PacingJson.readNumber "used_percentage" row)

            let known = PacingAdapterRules.claudeTopLevel |> List.map (fun (key, _, _, _) -> key)

            let topLevel =
                PacingAdapterRules.claudeTopLevel
                |> List.choose (fun (key, scope, duration, _) ->
                    match PacingJson.tryProperty key root with
                    | Some row when row.ValueKind = JsonValueKind.Object ->
                        Some(candidate key scope (usedOf row) (PacingJson.readReset "resets_at" row) duration)
                    | Some row when row.ValueKind <> JsonValueKind.Null ->
                        Some { Key = key; Outcome = Outcome.Rejected(WindowStatus.Invalid "window is not an object") }
                    | _ -> None)

            let unknownTopLevel =
                if root.ValueKind <> JsonValueKind.Object then
                    []
                else
                    root.EnumerateObject()
                    |> Seq.filter (fun property ->
                        not (List.contains property.Name known)
                        && property.Value.ValueKind = JsonValueKind.Object
                        && (usedOf property.Value).IsSome)
                    |> Seq.map (fun property ->
                        { Key = property.Name
                          Outcome = Outcome.Rejected(WindowStatus.Unsupported $"usage window '{property.Name}' is not supported by {PacingAdapterRules.ClaudeRulesVersion}") })
                    |> Seq.toList

            let session = TimeSpan.FromMinutes 300.0
            let week = TimeSpan.FromMinutes 10080.0

            let limitCandidate (row: JsonElement) =
                let kind = PacingJson.tryProperty "kind" row |> Option.bind PacingJson.tryString |> Option.defaultValue ""
                let scope = PacingJson.tryProperty "scope" row
                let model = scope |> Option.bind (scopeName "model")
                let surface = scope |> Option.bind (scopeName "surface")
                let used = PacingJson.readNumber "percent" row
                let reset = PacingJson.readReset "resets_at" row

                match PacingAdapterRules.claudeLimit kind model surface with
                | PacingAdapterRules.ClaudeLimit.Session -> candidate "five_hour" QuotaScope.Global used reset session
                | PacingAdapterRules.ClaudeLimit.WeeklyAll -> candidate "seven_day" QuotaScope.Global used reset week
                | PacingAdapterRules.ClaudeLimit.WeeklyModel name ->
                    let key = $"seven_day:{name}"

                    match PacingAdapterRules.claudeFamily name with
                    | Some family -> candidate key (QuotaScope.Model family) used reset week
                    | None ->
                        { Key = key
                          Outcome = Outcome.Rejected(WindowStatus.Unsupported $"model scope '{name}' does not map to one model family under {PacingAdapterRules.ClaudeRulesVersion}") }
                | PacingAdapterRules.ClaudeLimit.WeeklySurface name -> candidate $"seven_day:surface:{name}" (QuotaScope.Surface name) used reset week
                | PacingAdapterRules.ClaudeLimit.Unsupported reason -> { Key = $"limit:{kind}"; Outcome = Outcome.Rejected(WindowStatus.Unsupported reason) }

            let limits =
                match PacingJson.tryProperty "limits" root with
                | Some rows when rows.ValueKind = JsonValueKind.Array -> rows.EnumerateArray() |> Seq.map limitCandidate |> Seq.toList
                | _ -> []

            let candidates = topLevel @ unknownTopLevel @ limits
            let reading = coverage (PacingAdapterRules.claudeExpected @ expectedScoped) candidates

            if reading.Windows.IsEmpty then
                Error "Claude returned no usable quota windows"
            else
                Ok reading
        with :? JsonException as error ->
            Error $"Claude quota response could not be parsed: {error.Message}"
