namespace Praxis.Application.Pacing

open System
open System.Globalization
open System.Text.RegularExpressions
open Praxis.Domain.Pacing

/// Typed pacing telemetry (PRX-QUAL-008): stable event codes, transition
/// derivation from persisted state, redaction and deduplication. Pure.
[<RequireQualifiedAccess>]
module PacingEvents =
    [<Literal>]
    let Schema = "praxis.pacing-event/1"

    let code eventCode =
        match eventCode with
        | PacingEventCode.HoldStarted -> "hold-started"
        | PacingEventCode.HoldRetained -> "hold-retained"
        | PacingEventCode.HoldReleased -> "hold-released"
        | PacingEventCode.HardLimit -> "hard-limit"
        | PacingEventCode.ProviderUnavailable -> "provider-unavailable"
        | PacingEventCode.OverrideEnabled -> "override-enabled"
        | PacingEventCode.OverrideDisabled -> "override-disabled"
        | PacingEventCode.StateFault -> "state-fault"

    let allCodes =
        [ PacingEventCode.HoldStarted
          PacingEventCode.HoldRetained
          PacingEventCode.HoldReleased
          PacingEventCode.HardLimit
          PacingEventCode.ProviderUnavailable
          PacingEventCode.OverrideEnabled
          PacingEventCode.OverrideDisabled
          PacingEventCode.StateFault ]

    let tryParseCode (value: string) = allCodes |> List.tryFind (fun candidate -> code candidate = value)

    let reasonKinds =
        [ PacingReasonKind.HardLimit
          PacingReasonKind.WeeklyLead
          PacingReasonKind.WeeklyLeadUnverified
          PacingReasonKind.StateIndeterminate ]

    let tryParseReason (value: string) = reasonKinds |> List.tryFind (fun kind -> PacingReasonKind.code kind = value)

    let private maximumDetail = 240

    let private secrets =
        [ Regex(@"(?i)bearer\s+\S+", RegexOptions.CultureInvariant), "bearer [redacted]"
          Regex(@"(?i)(access_?token|refresh_?token|api_?key|authorization|password|secret)(""?\s*[:=]\s*""?)[^\s"",}]+", RegexOptions.CultureInvariant), "$1$2[redacted]"
          Regex(@"[A-Za-z0-9_\-\.=+]{32,}", RegexOptions.CultureInvariant), "[redacted]" ]

    /// Events carry diagnostics, never credentials or provider payloads: token
    /// shapes are removed, raw JSON objects are elided and the text is bounded.
    let redact (text: string) =
        let withoutSecrets = secrets |> List.fold (fun (current: string) (pattern, replacement) -> pattern.Replace(current, replacement)) text
        let withoutPayloads = Regex.Replace(withoutSecrets, @"\{.*\}", "{...}", RegexOptions.Singleline)
        let single = withoutPayloads.Replace('\n', ' ').Replace('\r', ' ').Trim()
        if single.Length <= maximumDetail then single else single.Substring(0, maximumDetail) + "..."

    let create eventCode (occurredAt: DateTimeOffset) provider windowKey reason (detail: string) resetsAt observedAt =
        { Code = eventCode
          OccurredAt = occurredAt
          Provider = provider
          WindowKey = windowKey
          Reason = reason
          Detail = redact detail
          ResetsAt = resetsAt
          ObservedAt = observedAt }

    /// Two events describe the same transition when code, provider, window,
    /// reason and reset identity agree (time and wording do not matter).
    let identity (event: PacingEvent) = event.Code, event.Provider, event.WindowKey, event.Reason, event.ResetsAt

    /// Repeated polling must not repeat a transition already recorded.
    let isDuplicate (last: PacingEvent option) (next: PacingEvent) =
        last |> Option.exists (fun previous -> identity previous = identity next)

    let private holdReset (hold: PacingHold) =
        match hold.Basis with
        | HoldBasis.HardLimit(_, resetsAt) -> Some resetsAt
        | HoldBasis.WeeklyLead resetsAt -> resetsAt

    let private holdReason (hold: PacingHold) =
        match hold.Basis with
        | HoldBasis.HardLimit _ -> PacingReasonKind.HardLimit
        | HoldBasis.WeeklyLead _ -> PacingReasonKind.WeeklyLead

    let private observedOf (hold: PacingHold) =
        match hold.Evidence with
        | HoldEvidence.Observed(observedAt, _) -> Some observedAt
        | HoldEvidence.Unrecorded -> None

    let private percent (value: decimal) = value.ToString("0.##", CultureInfo.InvariantCulture)

    let private describe (hold: PacingHold) =
        match hold.Basis with
        | HoldBasis.HardLimit(used, _) -> $"{hold.Key} at {percent used}%% used"
        | HoldBasis.WeeklyLead _ -> $"{hold.Key} weekly pacing latch"

    let ofHold eventCode (now: DateTimeOffset) (hold: PacingHold) (detail: string) =
        create eventCode now (Some hold.Provider) (Some hold.Key) (Some(holdReason hold)) detail (holdReset hold) (observedOf hold)

    /// Hold transitions between two persisted states: a hold that appears is
    /// `hard-limit` (exhausted window) or `hold-started` (weekly latch); a hold
    /// that disappears is `hold-released`. A refreshed hold is not a transition.
    let transitions (now: DateTimeOffset) (before: PacingState) (after: PacingState) : PacingEvent list =
        let started =
            after.Holds
            |> Map.toList
            |> List.filter (fun (id, _) -> not (before.Holds.ContainsKey id))
            |> List.map (fun (_, hold) ->
                match hold.Basis with
                | HoldBasis.HardLimit _ -> ofHold PacingEventCode.HardLimit now hold $"hard limit reached: {describe hold}"
                | HoldBasis.WeeklyLead _ -> ofHold PacingEventCode.HoldStarted now hold $"hold started: {describe hold}")

        let released =
            before.Holds
            |> Map.toList
            |> List.filter (fun (id, _) -> not (after.Holds.ContainsKey id))
            |> List.map (fun (_, hold) -> ofHold PacingEventCode.HoldReleased now hold $"hold released: {describe hold}")

        started @ released

    /// The human-readable `pace.log` rendering of one event.
    let render (event: PacingEvent) =
        let provider = event.Provider |> Option.map ProviderId.code |> Option.defaultValue "-"
        let window = event.WindowKey |> Option.defaultValue "-"

        let reset =
            event.ResetsAt
            |> Option.map (fun value -> " resets " + value.ToString("O", CultureInfo.InvariantCulture))
            |> Option.defaultValue ""

        $"{code event.Code} {provider} {window}: {event.Detail}{reset}"
