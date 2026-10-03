namespace Praxis.Domain.Pacing

open System

[<RequireQualifiedAccess>]
type QuotaScope =
    | Global
    | Model of name: string
    | Surface of name: string

[<RequireQualifiedAccess>]
module QuotaScope =
    let appliesTo (model: string option) scope =
        match scope, model with
        | QuotaScope.Global, _
        | QuotaScope.Surface _, _ -> true
        | QuotaScope.Model _, None -> true
        | QuotaScope.Model expected, Some actual ->
            actual.Contains(expected, StringComparison.OrdinalIgnoreCase)

type QuotaWindow =
    { Provider: string
      Key: string
      Label: string
      Scope: QuotaScope
      UsedPercent: decimal
      Duration: TimeSpan
      ResetsAt: DateTimeOffset
      ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type ObservationFreshness =
    | Fresh
    | Stale of reason: string
    | Unavailable of reason: string

type PacingPolicy =
    { HardUsagePercent: decimal
      WeeklyWindow: TimeSpan
      TriggerLead: TimeSpan
      ResumeLead: TimeSpan
      FreshFor: TimeSpan
      PollInterval: TimeSpan }

[<RequireQualifiedAccess>]
module PacingPolicy =
    let defaults =
        { HardUsagePercent = 98m
          WeeklyWindow = TimeSpan.FromDays 7.0
          TriggerLead = TimeSpan.FromHours 8.0
          ResumeLead = TimeSpan.FromHours 4.0
          FreshFor = TimeSpan.FromSeconds 90.0
          PollInterval = TimeSpan.FromSeconds 5.0 }

type PacingHold =
    { Key: string
      Provider: string
      Scope: QuotaScope
      Since: DateTimeOffset }

type PacingState = { Holds: Map<string, PacingHold> }

[<RequireQualifiedAccess>]
module PacingState =
    let empty = { Holds = Map.empty }

[<RequireQualifiedAccess>]
type PacingReasonKind =
    | HardLimit
    | WeeklyLead
    | WeeklyLeadUnverified

[<RequireQualifiedAccess>]
module PacingReasonKind =
    let code reason =
        match reason with
        | PacingReasonKind.HardLimit -> "hard-limit"
        | PacingReasonKind.WeeklyLead -> "weekly-lead"
        | PacingReasonKind.WeeklyLeadUnverified -> "weekly-lead-unverified"

type PacingReason =
    { WindowKey: string
      Kind: PacingReasonKind
      Detail: string
      ResumeAt: DateTimeOffset }

type PacingRequest =
    { Now: DateTimeOffset
      Provider: string
      Model: string option
      Windows: QuotaWindow list
      Freshness: ObservationFreshness
      Existing: PacingState
      Override: bool }

type PacingDecision =
    { MayProceed: bool
      Reasons: PacingReason list
      BindingReason: PacingReason option
      State: PacingState
      LeadByWindow: Map<string, TimeSpan> }

[<RequireQualifiedAccess>]
module Pacing =
    let private almostEqual left right = Math.Abs(left - right) < 0.5

    let isWeekly policy (window: QuotaWindow) =
        almostEqual window.Duration.TotalMinutes policy.WeeklyWindow.TotalMinutes

    let isLive now (window: QuotaWindow) =
        window.Duration > TimeSpan.Zero
        && window.ResetsAt > now
        && window.UsedPercent >= 0m
        && window.UsedPercent <= 100m

    let idealPercent now (window: QuotaWindow) =
        let remaining = window.ResetsAt - now
        let elapsedMinutes = window.Duration.TotalMinutes - remaining.TotalMinutes
        let percent = 100.0 * elapsedMinutes / window.Duration.TotalMinutes
        decimal (Math.Clamp(percent, 0.0, 100.0))

    let lead now (window: QuotaWindow) =
        let elapsed = window.Duration - (window.ResetsAt - now)
        let consumedMinutes = window.Duration.TotalMinutes * float window.UsedPercent / 100.0
        TimeSpan.FromMinutes(consumedMinutes - elapsed.TotalMinutes)

    let private applicable model window =
        QuotaScope.appliesTo model window.Scope

    let private stale freshness =
        match freshness with
        | ObservationFreshness.Fresh -> false
        | ObservationFreshness.Stale _
        | ObservationFreshness.Unavailable _ -> true

    let private stableHoldKey (window: QuotaWindow) = $"{window.Provider}/{window.Key}"

    let private sameProvider provider (hold: PacingHold) =
        String.Equals(hold.Provider, provider, StringComparison.OrdinalIgnoreCase)

    let private applicableHold model (hold: PacingHold) =
        QuotaScope.appliesTo model hold.Scope

    let private reason key kind detail resumeAt =
        { WindowKey = key
          Kind = kind
          Detail = detail
          ResumeAt = resumeAt }

    let private binding reasons =
        reasons
        |> List.sortWith (fun left right ->
            match compare right.ResumeAt left.ResumeAt with
            | 0 -> String.CompareOrdinal(left.WindowKey, right.WindowKey)
            | order -> order)
        |> List.tryHead

    /// Provider-neutral pacing policy. The weekly latch follows the conservative
    /// Codex policy from jpwinans/usage-auto-pause: once established, stale or
    /// missing observations cannot clear it. Only a fresh weekly observation at
    /// or below the resume threshold releases the latch. Hard-limit observations
    /// remain binding until their reset even when the snapshot later becomes stale.
    let evaluate policy (request: PacingRequest) =
        if request.Override then
            { MayProceed = true
              Reasons = []
              BindingReason = None
              State = request.Existing
              LeadByWindow = Map.empty }
        else
            let windows =
                request.Windows
                |> List.filter (fun window ->
                    String.Equals(window.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
                    && applicable request.Model window
                    && isLive request.Now window)

            let leads =
                windows
                |> List.filter (isWeekly policy)
                |> List.map (fun window -> stableHoldKey window, lead request.Now window)
                |> Map.ofList

            let mutable holds = request.Existing.Holds
            let reasons = ResizeArray<PacingReason>()

            // A last-known exhausted window remains a hard stop until reset.
            for window in windows do
                if window.UsedPercent > policy.HardUsagePercent then
                    let prefix = if stale request.Freshness then "last known " else ""
                    reasons.Add(
                        reason
                            (stableHoldKey window)
                            PacingReasonKind.HardLimit
                            $"{prefix}{window.Label} {window.UsedPercent:0.##}% used; wait for reset"
                            window.ResetsAt)

            let weekly = windows |> List.filter (isWeekly policy)
            let observedHoldKeys = weekly |> List.map stableHoldKey |> Set.ofList

            if stale request.Freshness then
                // Uncertainty never creates a weekly latch, but it also never
                // authorizes release of one that fresh evidence already created.
                for hold in holds.Values do
                    if sameProvider request.Provider hold && applicableHold request.Model hold then
                        reasons.Add(
                            reason
                                hold.Key
                                PacingReasonKind.WeeklyLeadUnverified
                                "weekly pacing hold is awaiting a fresh reading at or below the resume threshold"
                                (request.Now + policy.PollInterval))
            else
                for window in weekly do
                    let key = stableHoldKey window
                    let currentLead = leads[key]
                    let held = holds.ContainsKey key

                    if currentLead >= policy.TriggerLead || (held && currentLead > policy.ResumeLead) then
                        if not held then
                            holds <-
                                holds.Add(
                                    key,
                                    { Key = key
                                      Provider = window.Provider
                                      Scope = window.Scope
                                      Since = request.Now })

                        let resumeAt =
                            request.Now + (currentLead - policy.ResumeLead)
                            |> min window.ResetsAt

                        reasons.Add(
                            reason
                                key
                                PacingReasonKind.WeeklyLead
                                $"{window.Label} is {currentLead.TotalHours:0.0}h ahead of pace"
                                resumeAt)
                    elif held then
                        holds <- holds.Remove key

                // A fresh provider response that omits a latched weekly window is
                // not positive evidence that capacity returned. Preserve the hold
                // until a weekly reading can explicitly release it.
                for hold in request.Existing.Holds.Values do
                    if sameProvider request.Provider hold
                       && applicableHold request.Model hold
                       && not (observedHoldKeys.Contains hold.Key)
                       && holds.ContainsKey hold.Key then
                        reasons.Add(
                            reason
                                hold.Key
                                PacingReasonKind.WeeklyLeadUnverified
                                "latched weekly window was not present in the fresh response; awaiting an explicit release reading"
                                (request.Now + policy.PollInterval))

            let state = { Holds = holds }
            let allReasons = reasons |> Seq.toList

            { MayProceed = allReasons.IsEmpty
              Reasons = allReasons
              BindingReason = binding allReasons
              State = state
              LeadByWindow = leads }
