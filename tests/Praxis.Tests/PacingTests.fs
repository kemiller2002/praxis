namespace Praxis.Tests

open System
open Praxis.Domain.Pacing

module PacingTests =
    let private t name run = { Name = $"pacing: {name}"; Run = run }

    let private now = DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero)
    let private week = TimeSpan.FromDays 7.0

    let private usedForLead hours =
        let reset = now + TimeSpan.FromHours 84.0
        let elapsed = week - (reset - now)
        decimal ((elapsed.TotalHours + hours) / week.TotalHours * 100.0)

    let private window key used duration reset scope =
        { Provider = "codex"
          Key = key
          Label = key
          Scope = scope
          UsedPercent = used
          Duration = duration
          ResetsAt = reset
          ObservedAt = now }

    let private weekly key leadHours scope =
        window key (usedForLead leadHours) week (now + TimeSpan.FromHours 84.0) scope

    let private evaluate state freshness windows model overridden =
        Pacing.evaluate
            PacingPolicy.defaults
            { Now = now
              Provider = "codex"
              Model = model
              Windows = windows
              Freshness = freshness
              Existing = state
              Override = overridden }

    let private held result = result.State.Holds.Count > 0

    let tests =
        [ t "on-pace weekly usage proceeds" (fun () ->
              let result = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 0.0 QuotaScope.Global ] None false
              Assert.isTrue result.MayProceed "on-pace usage should proceed"
              Assert.isTrue (not (held result)) "on-pace usage must not create a hold")

          t "weekly trigger starts a shared hold" (fun () ->
              let result = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 8.4 QuotaScope.Global ] None false
              Assert.isTrue (not result.MayProceed) "8.4h lead should hold"
              Assert.isTrue (held result) "trigger must latch"
              Assert.equal PacingReasonKind.WeeklyLead result.BindingReason.Value.Kind
              Assert.isTrue (Math.Abs(result.BindingReason.Value.ResumeAt.Subtract(now).TotalHours - 4.4) < 0.01) "resume estimate should target +4h lead")

          t "hysteresis keeps +6h held but does not create a new +6h hold" (fun () ->
              let triggered = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 8.4 QuotaScope.Global ] None false
              let retained = evaluate triggered.State ObservationFreshness.Fresh [ weekly "weekly" 6.0 QuotaScope.Global ] None false
              Assert.isTrue (not retained.MayProceed && held retained) "existing hold must survive at +6h"
              let unlatched = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 6.0 QuotaScope.Global ] None false
              Assert.isTrue unlatched.MayProceed "fresh +6h must not create a new hold")

          t "fresh <=4h releases the weekly hold" (fun () ->
              let triggered = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 8.4 QuotaScope.Global ] None false
              let released = evaluate triggered.State ObservationFreshness.Fresh [ weekly "weekly" 3.9 QuotaScope.Global ] None false
              Assert.isTrue released.MayProceed "fresh +3.9h should release"
              Assert.equal 0 released.State.Holds.Count)

          t "98 percent is not hard while 98.1 is" (fun () ->
              let session used = window "session" used (TimeSpan.FromHours 5.0) (now + TimeSpan.FromHours 1.0) QuotaScope.Global
              Assert.isTrue (evaluate PacingState.empty ObservationFreshness.Fresh [ session 98m ] None false).MayProceed "98% is the strict boundary"
              let stopped = evaluate PacingState.empty ObservationFreshness.Fresh [ session 98.1m ] None false
              Assert.isTrue (not stopped.MayProceed) "98.1% must hard hold"
              Assert.equal PacingReasonKind.HardLimit stopped.BindingReason.Value.Kind)

          t "stale data cannot clear an established weekly hold" (fun () ->
              let triggered = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 8.4 QuotaScope.Global ] None false
              let stale = evaluate triggered.State (ObservationFreshness.Stale "refresh failed") [ weekly "weekly" 3.0 QuotaScope.Global ] None false
              Assert.isTrue (not stale.MayProceed) "stale headroom is not release evidence"
              Assert.equal PacingReasonKind.WeeklyLeadUnverified stale.BindingReason.Value.Kind
              Assert.isTrue (held stale) "stale read must preserve the latch")

          t "stale data never creates a new weekly lead hold" (fun () ->
              let stale = evaluate PacingState.empty (ObservationFreshness.Stale "refresh failed") [ weekly "weekly" 12.0 QuotaScope.Global ] None false
              Assert.isTrue stale.MayProceed "a stale lead alone must not create a latch"
              Assert.equal 0 stale.State.Holds.Count)

          t "known hard exhaustion remains binding when stale" (fun () ->
              let hard = window "session" 99m (TimeSpan.FromHours 5.0) (now + TimeSpan.FromHours 1.0) QuotaScope.Global
              let stale = evaluate PacingState.empty (ObservationFreshness.Stale "refresh failed") [ hard ] None false
              Assert.isTrue (not stale.MayProceed) "last-known >98% must remain held until reset"
              Assert.equal PacingReasonKind.HardLimit stale.BindingReason.Value.Kind)

          t "model-scoped holds do not stop unrelated models" (fun () ->
              let fable = weekly "weekly:fable" 9.0 (QuotaScope.Model "Fable")
              let triggered = evaluate PacingState.empty ObservationFreshness.Fresh [ fable ] (Some "claude-fable-5-1") false
              Assert.isTrue (not triggered.MayProceed) "Fable should trigger its scoped hold"
              let opus = evaluate triggered.State ObservationFreshness.Fresh [ fable ] (Some "claude-opus-5") false
              Assert.isTrue opus.MayProceed "Opus should not be blocked by a Fable-scoped hold"
              Assert.equal 1 opus.State.Holds.Count)

          t "unknown model checks scoped windows conservatively" (fun () ->
              let fable = weekly "weekly:fable" 9.0 (QuotaScope.Model "Fable")
              let result = evaluate PacingState.empty ObservationFreshness.Fresh [ fable ] None false
              Assert.isTrue (not result.MayProceed) "unknown model must consider every reported scoped limit")

          t "override bypasses without erasing the latch" (fun () ->
              let triggered = evaluate PacingState.empty ObservationFreshness.Fresh [ weekly "weekly" 9.0 QuotaScope.Global ] None false
              let bypassed = evaluate triggered.State ObservationFreshness.Fresh [ weekly "weekly" 9.0 QuotaScope.Global ] None true
              Assert.isTrue bypassed.MayProceed "override should bypass the gate"
              Assert.equal triggered.State bypassed.State) ]
