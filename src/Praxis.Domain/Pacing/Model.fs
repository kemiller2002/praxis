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

/// Why a hold exists. Each basis carries the evidence that bounds it, so a
/// hold can be released only by evidence about the same quota window.
[<RequireQualifiedAccess>]
type HoldBasis =
    /// Weekly pacing latch. `resetsAt` is the quota window's reset identity;
    /// `None` is a legacy (schema 1) hold whose window identity was never recorded.
    | WeeklyLead of resetsAt: DateTimeOffset option
    /// Exhausted (> hard threshold) window. Binding until its reset unless a
    /// fresh reading of the same window proves capacity returned.
    | HardLimit of usedPercent: decimal * resetsAt: DateTimeOffset

type PacingHold =
    { Key: string
      Provider: string
      Scope: QuotaScope
      Since: DateTimeOffset
      Basis: HoldBasis }

[<RequireQualifiedAccess>]
module PacingHold =
    let basisCode hold =
        match hold.Basis with
        | HoldBasis.WeeklyLead _ -> "weekly-lead"
        | HoldBasis.HardLimit _ -> "hard-limit"

    /// Identity within persisted state. A window may carry both a weekly latch
    /// and a hard-limit hold, so the basis is part of the identity.
    let id hold = basisCode hold + "|" + hold.Key

type PacingState = { Holds: Map<string, PacingHold> }

[<RequireQualifiedAccess>]
module PacingState =
    let empty = { Holds = Map.empty }

    let ofHolds (holds: PacingHold seq) =
        { Holds = holds |> Seq.map (fun hold -> PacingHold.id hold, hold) |> Map.ofSeq }

/// Whether the persisted safety state could be trusted. Persistence failures
/// are never converted into empty state: an indeterminate state blocks work
/// until an operator overrides or explicitly recovers it.
[<RequireQualifiedAccess>]
type StateIntegrity =
    | Intact
    | Indeterminate of reason: string

[<RequireQualifiedAccess>]
type PacingReasonKind =
    | HardLimit
    | WeeklyLead
    | WeeklyLeadUnverified
    | StateIndeterminate

[<RequireQualifiedAccess>]
module PacingReasonKind =
    let code reason =
        match reason with
        | PacingReasonKind.HardLimit -> "hard-limit"
        | PacingReasonKind.WeeklyLead -> "weekly-lead"
        | PacingReasonKind.WeeklyLeadUnverified -> "weekly-lead-unverified"
        | PacingReasonKind.StateIndeterminate -> "state-indeterminate"

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
      StateIntegrity: StateIntegrity
      Override: bool }

type PacingDecision =
    { MayProceed: bool
      Reasons: PacingReason list
      BindingReason: PacingReason option
      State: PacingState
      LeadByWindow: Map<string, TimeSpan> }

[<RequireQualifiedAccess>]
module Pacing =
    let private almostEqual (left: float) (right: float) = Math.Abs(left - right) < 0.5

    /// Two reset timestamps identify the same quota window when they differ by
    /// less than this tolerance (providers round reset times differently).
    let private sameWindowTolerance = TimeSpan.FromHours 1.0

    let isWeekly policy (window: QuotaWindow) =
        almostEqual window.Duration.TotalMinutes policy.WeeklyWindow.TotalMinutes

    let isLive (now: DateTimeOffset) (window: QuotaWindow) =
        window.Duration > TimeSpan.Zero
        && window.ResetsAt > now
        && window.UsedPercent >= 0m
        && window.UsedPercent <= 100m

    let idealPercent (now: DateTimeOffset) (window: QuotaWindow) =
        let remaining = window.ResetsAt - now
        let elapsedMinutes = window.Duration.TotalMinutes - remaining.TotalMinutes
        let percent = 100.0 * elapsedMinutes / window.Duration.TotalMinutes
        decimal (Math.Clamp(percent, 0.0, 100.0))

    let lead (now: DateTimeOffset) (window: QuotaWindow) =
        let elapsed = window.Duration - (window.ResetsAt - now)
        let consumedMinutes = window.Duration.TotalMinutes * float window.UsedPercent / 100.0
        TimeSpan.FromMinutes(consumedMinutes - elapsed.TotalMinutes)

    let private isFresh (freshness: ObservationFreshness) =
        match freshness with
        | ObservationFreshness.Fresh -> true
        | ObservationFreshness.Stale _
        | ObservationFreshness.Unavailable _ -> false

    let private stableHoldKey (window: QuotaWindow) = $"{window.Provider}/{window.Key}"

    let private relevant (request: PacingRequest) (hold: PacingHold) =
        String.Equals(hold.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
        && QuotaScope.appliesTo request.Model hold.Scope

    let private sameWindow (resetsAt: DateTimeOffset option) (window: QuotaWindow) =
        match resetsAt with
        | None -> true
        | Some known -> (known - window.ResetsAt).Duration() < sameWindowTolerance

    let private reason key kind detail resumeAt =
        { WindowKey = key
          Kind = kind
          Detail = detail
          ResumeAt = resumeAt }

    let private percentText (value: decimal) =
        value.ToString("0.##", Globalization.CultureInfo.InvariantCulture)

    let private binding (reasons: PacingReason list) =
        reasons
        |> List.sortWith (fun left right ->
            match compare right.ResumeAt left.ResumeAt with
            | 0 -> String.CompareOrdinal(left.WindowKey, right.WindowKey)
            | order -> order)
        |> List.tryHead

    let private decide (state: PacingState) (leads: Map<string, TimeSpan>) (reasons: PacingReason list) =
        { MayProceed = reasons.IsEmpty
          Reasons = reasons
          BindingReason = binding reasons
          State = state
          LeadByWindow = leads }

    /// Hard limits: an observed exhausted window creates or refreshes a durable
    /// hold. A persisted hard hold survives provider outage, partial responses
    /// and restarts; it ends only at its reset time or when a fresh reading of
    /// the same window shows usage at or below the threshold.
    let private hardHolds policy (request: PacingRequest) (observed: Map<string, QuotaWindow>) =
        let fresh = isFresh request.Freshness

        let existingHard =
            request.Existing.Holds.Values
            |> Seq.filter (relevant request)
            |> Seq.choose (fun hold ->
                match hold.Basis with
                | HoldBasis.HardLimit(used, resetsAt) -> Some(hold, used, resetsAt)
                | HoldBasis.WeeklyLead _ -> None)
            |> Seq.toList

        let sinceOf key =
            existingHard
            |> List.tryFind (fun (hold, _, _) -> hold.Key = key)
            |> Option.map (fun (hold, _, _) -> hold.Since)
            |> Option.defaultValue request.Now

        let observedExhausted =
            observed
            |> Map.toList
            |> List.filter (fun (_, window) -> window.UsedPercent > policy.HardUsagePercent)
            |> List.map (fun (key, window) ->
                let hold =
                    { Key = key
                      Provider = window.Provider
                      Scope = window.Scope
                      Since = sinceOf key
                      Basis = HoldBasis.HardLimit(window.UsedPercent, window.ResetsAt) }

                let prefix = if fresh then "" else "last known "
                let detail = $"{prefix}{window.Label} {percentText window.UsedPercent}%% used; wait for reset"
                hold, reason key PacingReasonKind.HardLimit detail window.ResetsAt)

        let refreshedKeys = observedExhausted |> List.map (fun (hold, _) -> hold.Key) |> Set.ofList

        let releasedByFreshEvidence key =
            fresh
            && (observed
                |> Map.tryFind key
                |> Option.exists (fun window -> window.UsedPercent <= policy.HardUsagePercent))

        let retained =
            existingHard
            |> List.filter (fun (hold, _, resetsAt) ->
                resetsAt > request.Now
                && not (refreshedKeys.Contains hold.Key)
                && not (releasedByFreshEvidence hold.Key))
            |> List.map (fun (hold, used, resetsAt) ->
                let detail =
                    $"persisted hard limit {hold.Key} at {percentText used}%% used remains binding until reset"

                hold, reason hold.Key PacingReasonKind.HardLimit detail resetsAt)

        observedExhausted @ retained

    /// Weekly pacing latch with hysteresis. Uncertainty never creates a latch
    /// and never releases one; a latch is bound to its window's reset identity,
    /// so a new quota window never inherits the previous window's latch.
    let private weeklyHolds policy (request: PacingRequest) (observed: Map<string, QuotaWindow>) (leads: Map<string, TimeSpan>) =
        let existingWeekly =
            request.Existing.Holds.Values
            |> Seq.filter (relevant request)
            |> Seq.choose (fun hold ->
                match hold.Basis with
                | HoldBasis.WeeklyLead resetsAt -> Some(hold, resetsAt)
                | HoldBasis.HardLimit _ -> None)
            |> Seq.toList

        let unverified (hold: PacingHold) detail =
            hold, reason hold.Key PacingReasonKind.WeeklyLeadUnverified detail (request.Now + policy.PollInterval)

        if not (isFresh request.Freshness) then
            existingWeekly
            |> List.map (fun (hold, _) ->
                unverified hold "weekly pacing hold is awaiting a fresh reading at or below the resume threshold")
        else
            let latchFor key (window: QuotaWindow) =
                existingWeekly
                |> List.tryFind (fun (hold, resetsAt) -> hold.Key = key && sameWindow resetsAt window)
                |> Option.map fst

            let evaluated =
                observed
                |> Map.toList
                |> List.filter (fun (_, window) -> isWeekly policy window)
                |> List.choose (fun (key, window) ->
                    let currentLead = leads[key]
                    let latch = latchFor key window

                    if currentLead >= policy.TriggerLead || (latch.IsSome && currentLead > policy.ResumeLead) then
                        let hold =
                            { Key = key
                              Provider = window.Provider
                              Scope = window.Scope
                              Since = latch |> Option.map _.Since |> Option.defaultValue request.Now
                              Basis = HoldBasis.WeeklyLead(Some window.ResetsAt) }

                        let resumeAt = request.Now + (currentLead - policy.ResumeLead) |> min window.ResetsAt
                        let leadHours = currentLead.TotalHours.ToString("0.0", Globalization.CultureInfo.InvariantCulture)
                        Some(hold, reason key PacingReasonKind.WeeklyLead $"{window.Label} is {leadHours}h ahead of pace" resumeAt)
                    else
                        None)

            // A fresh response that omits a latched weekly window is not
            // positive evidence that capacity returned.
            let omitted =
                existingWeekly
                |> List.filter (fun (hold, _) -> not (observed.ContainsKey hold.Key))
                |> List.map (fun (hold, _) ->
                    unverified hold "latched weekly window was not present in the fresh response; awaiting an explicit release reading")

            evaluated @ omitted

    let private evaluateIntact (policy: PacingPolicy) (request: PacingRequest) =
        let observed =
            request.Windows
            |> List.filter (fun window ->
                String.Equals(window.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
                && QuotaScope.appliesTo request.Model window.Scope
                && isLive request.Now window)
            |> List.map (fun window -> stableHoldKey window, window)
            |> Map.ofList

        let leads =
            observed
            |> Map.filter (fun _ window -> isWeekly policy window)
            |> Map.map (fun _ window -> lead request.Now window)

        let held = hardHolds policy request observed @ weeklyHolds policy request observed leads

        // Holds for other providers, models or scopes are carried unchanged.
        let untouched =
            request.Existing.Holds.Values
            |> Seq.filter (fun hold -> not (relevant request hold))
            |> Seq.toList

        let state = PacingState.ofHolds (untouched @ (held |> List.map fst))
        decide state leads (held |> List.map snd)

    /// Provider-neutral pacing policy. The weekly latch follows the conservative
    /// Codex policy from jpwinans/usage-auto-pause: once established, stale or
    /// missing observations cannot clear it. Only a fresh weekly observation of
    /// the same window at or below the resume threshold releases the latch.
    /// Hard-limit holds are durable until reset. Indeterminate persisted state
    /// fails closed; only an explicit operator override proceeds past it.
    let evaluate (policy: PacingPolicy) (request: PacingRequest) =
        if request.Override then
            decide request.Existing Map.empty []
        else
            match request.StateIntegrity with
            | StateIntegrity.Indeterminate cause ->
                let detail = $"pacing safety state is indeterminate ({cause}); repair it with 'praxis pacing state quarantine' or enable the override"
                decide request.Existing Map.empty [ reason "state" PacingReasonKind.StateIndeterminate detail (request.Now + policy.PollInterval) ]
            | StateIntegrity.Intact -> evaluateIntact policy request
