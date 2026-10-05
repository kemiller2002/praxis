namespace Praxis.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Praxis.Domain.Pacing
open Praxis.Application.Pacing
open Praxis.Infrastructure.Pacing

/// Adversarial pacing tests (PRX-QUAL-002/003/004/011): failure behaviour,
/// not happy-path policy. Every case asserts that uncertainty, corruption or
/// infrastructure faults cannot convert a safety hold into permission.
module PacingSafetyTests =
    let private t name run = { Name = $"pacing safety: {name}"; Run = run }

    let private now = DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.Zero)
    let private week = TimeSpan.FromDays 7.0
    let private sessionLength = TimeSpan.FromHours 5.0

    let private window key used duration reset =
        { Provider = "codex"
          Key = key
          Label = key
          Scope = QuotaScope.Global
          UsedPercent = used
          Duration = duration
          ResetsAt = reset
          ObservedAt = now }

    let private session used = window "session" used sessionLength (now + TimeSpan.FromHours 1.0)

    let private weeklyAt (reset: DateTimeOffset) (at: DateTimeOffset) leadHours =
        let elapsed = week - (reset - at)
        let used = decimal ((elapsed.TotalHours + leadHours) / week.TotalHours * 100.0)
        window "weekly" used week reset

    let private weekly leadHours = weeklyAt (now + TimeSpan.FromHours 84.0) now leadHours

    let private request at state integrity freshness windows overridden =
        { Now = at
          Provider = "codex"
          Model = None
          Windows = windows
          Freshness = freshness
          Existing = state
          StateIntegrity = integrity
          Override = overridden }

    let private evaluateAt at state freshness windows =
        Pacing.evaluate PacingPolicy.defaults (request at state StateIntegrity.Intact freshness windows false)

    let private evaluate state freshness windows = evaluateAt now state freshness windows

    let private fresh = ObservationFreshness.Fresh
    let private outage = ObservationFreshness.Unavailable "provider offline"
    let private stale = ObservationFreshness.Stale "refresh failed"

    let private kind (decision: PacingDecision) = decision.BindingReason |> Option.map _.Kind

    let private withDirectory (run: string -> unit) =
        let directory = Path.Combine(Path.GetTempPath(), "praxis-pacing-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore

        try
            run directory
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    let private snapshotOf windows freshness =
        { Provider = "codex"
          ObservedAt = now
          Windows = windows
          Coverage = []
          Freshness = freshness }

    let private hardHold used reset =
        { Key = "codex/session"
          Provider = "codex"
          Scope = QuotaScope.Global
          Since = now
          Basis = HoldBasis.HardLimit(used, reset) }

    let private domainTests =
        [ t "hard threshold below, at and above 98 percent" (fun () ->
              Assert.isTrue (evaluate PacingState.empty fresh [ session 97.99m ]).MayProceed "below the threshold proceeds"
              Assert.isTrue (evaluate PacingState.empty fresh [ session 98m ]).MayProceed "the threshold itself proceeds"
              let above = evaluate PacingState.empty fresh [ session 98.01m ]
              Assert.isTrue (not above.MayProceed) "above the threshold holds"
              Assert.equal (Some PacingReasonKind.HardLimit) (kind above))

          t "a hard limit becomes durable state rather than a cached reading" (fun () ->
              let decision = evaluate PacingState.empty fresh [ session 99m ]
              let hold = decision.State.Holds.Values |> Seq.toList |> Assert.single
              Assert.equal (HoldBasis.HardLimit(99m, now + TimeSpan.FromHours 1.0)) hold.Basis)

          t "a persisted hard hold survives a provider outage with no windows" (fun () ->
              let established = evaluate PacingState.empty fresh [ session 99m ]
              let outageDecision = evaluate established.State outage []
              Assert.isTrue (not outageDecision.MayProceed) "losing the provider must not authorize work"
              Assert.equal (Some PacingReasonKind.HardLimit) (kind outageDecision))

          t "a persisted hard hold survives a partial response that omits its window" (fun () ->
              let established = evaluate PacingState.empty fresh [ session 99m; weekly 0.0 ]
              let partial = evaluate established.State stale [ weekly 0.0 ]
              Assert.isTrue (not partial.MayProceed) "an omitted exhausted window must stay held")

          t "a stale low reading cannot release a hard hold" (fun () ->
              let established = evaluate PacingState.empty fresh [ session 99m ]
              let staleLow = evaluate established.State stale [ session 10m ]
              Assert.isTrue (not staleLow.MayProceed) "stale evidence is not trusted release evidence")

          t "a hard hold ends at its trusted reset time" (fun () ->
              let established = evaluate PacingState.empty fresh [ session 99m ]
              let afterReset = evaluateAt (now + TimeSpan.FromHours 1.5) established.State outage []
              Assert.isTrue afterReset.MayProceed "the reset time bounds the hold"
              Assert.equal 0 afterReset.State.Holds.Count)

          t "fresh evidence of the same window at or below the threshold releases a hard hold" (fun () ->
              let established = evaluate PacingState.empty fresh [ session 99m ]
              let released = evaluate established.State fresh [ session 40m ]
              Assert.isTrue released.MayProceed "a fresh reading is trusted release evidence")

          t "a weekly latch is not inherited by the next quota window" (fun () ->
              let firstReset = now + TimeSpan.FromHours 84.0
              let latched = evaluate PacingState.empty fresh [ weeklyAt firstReset now 9.0 ]
              Assert.isTrue (not latched.MayProceed) "+9h latches"
              let sameWindow = evaluate latched.State fresh [ weeklyAt firstReset now 6.0 ]
              Assert.isTrue (not sameWindow.MayProceed) "hysteresis keeps +6h held within the same window"
              let later = firstReset + TimeSpan.FromHours 30.0
              let nextReset = firstReset + week
              let nextWindow = evaluateAt later latched.State fresh [ weeklyAt nextReset later 6.0 ]
              Assert.isTrue nextWindow.MayProceed "+6h in a new window must not inherit the previous latch")

          t "a migrated legacy latch adopts the next fresh window identity" (fun () ->
              let legacy =
                  PacingState.ofHolds
                      [ { Key = "codex/weekly"
                          Provider = "codex"
                          Scope = QuotaScope.Global
                          Since = now
                          Basis = HoldBasis.WeeklyLead None } ]

              let retained = evaluate legacy fresh [ weekly 6.0 ]
              Assert.isTrue (not retained.MayProceed) "a legacy latch keeps hysteresis"
              let hold = retained.State.Holds.Values |> Seq.exactlyOne
              Assert.equal (HoldBasis.WeeklyLead(Some(now + TimeSpan.FromHours 84.0))) hold.Basis)

          t "indeterminate state fails closed and is not rewritten" (fun () ->
              let existing = (evaluate PacingState.empty fresh [ weekly 9.0 ]).State

              let decision =
                  Pacing.evaluate
                      PacingPolicy.defaults
                      (request now existing (StateIntegrity.Indeterminate "corrupt") fresh [ weekly 0.0 ] false)

              Assert.isTrue (not decision.MayProceed) "indeterminate state must not authorize work"
              Assert.equal (Some PacingReasonKind.StateIndeterminate) (kind decision)
              Assert.equal existing decision.State)

          t "an explicit override is the only way past indeterminate state" (fun () ->
              let decision =
                  Pacing.evaluate
                      PacingPolicy.defaults
                      (request now PacingState.empty (StateIntegrity.Indeterminate "corrupt") fresh [] true)

              Assert.isTrue decision.MayProceed "the operator override is explicit")

          t "equivalent inputs and state yield equivalent decisions" (fun () ->
              let state = (evaluate PacingState.empty fresh [ weekly 9.0; session 99m ]).State
              let first = evaluate state fresh [ weekly 7.0; session 99m ]
              let second = evaluate state fresh [ session 99m; weekly 7.0 ]
              Assert.equal first second) ]

    let private observationTests =
        [ t "a Codex response missing the weekly window is incomplete" (fun () ->
              let json = """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1791050400}}}}"""

              match PacingNormalization.normalizeCodexResult "codex" now json with
              | Error message -> failwith message
              | Ok reading ->
                  Assert.isTrue (not (WindowObservation.isComplete reading.Coverage)) "the primary window cannot vouch for the weekly one"
                  let secondary = reading.Coverage |> List.find (fun observation -> observation.Key = "codex:secondary")
                  Assert.equal WindowStatus.Missing secondary.Status)

          t "a malformed Codex window is invalid, not ignored" (fun () ->
              let json = """{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":1791050400},"secondary":{"usedPercent":140,"windowDurationMins":10080,"resetsAt":1791655200}}}}"""

              match PacingNormalization.normalizeCodexResult "codex" now json with
              | Error message -> failwith message
              | Ok reading ->
                  let secondary = reading.Coverage |> List.find (fun observation -> observation.Key = "codex:secondary")

                  match secondary.Status with
                  | WindowStatus.Invalid _ -> ()
                  | other -> failwith $"expected Invalid, got {other}")

          t "a disappearing Claude scoped window makes the reading incomplete" (fun () ->
              let json = """{"five_hour":{"utilization":20,"resets_at":"2026-10-03T20:00:00Z"},"seven_day":{"utilization":40,"resets_at":"2026-10-08T17:00:00Z"}}"""

              match PacingNormalization.normalizeClaudeUsage now [ "seven_day:Fable" ] json with
              | Error message -> failwith message
              | Ok reading ->
                  Assert.isTrue (not (WindowObservation.isComplete reading.Coverage)) "a previously seen scoped window must be accounted for"
                  let scoped = reading.Coverage |> List.find (fun observation -> observation.Key = "seven_day:Fable")
                  Assert.equal WindowStatus.Missing scoped.Status)

          t "malformed provider JSON is an explicit error" (fun () ->
              match PacingNormalization.normalizeCodexResult "codex" now "{\"rateLimits\":" with
              | Ok _ -> failwith "truncated JSON must not normalize"
              | Error _ -> ()) ]

    let private documentTests =
        let unreadable read =
            match read with
            | PacingStateRead.Unreadable _ -> ()
            | other -> failwith $"expected Unreadable, got {other}"

        [ t "corrupt state is unreadable, never empty" (fun () -> PacingStateDocument.parse "not json" |> unreadable)

          t "truncated state is unreadable" (fun () ->
              PacingStateDocument.parse """{"schemaVersion":2,"revision":3,"holds":[{"key":"codex/weekly" """ |> unreadable)

          t "a partially valid hold row makes the whole document unreadable" (fun () ->
              PacingStateDocument.parse
                  """{"schemaVersion":2,"revision":1,"holds":[{"key":"codex/weekly","since":"2026-10-03T17:00:00Z","basis":"weekly-lead","resetsAt":"2026-10-07T05:00:00Z"}]}"""
              |> unreadable)

          t "a newer schema is unsupported" (fun () ->
              Assert.equal (PacingStateRead.Unsupported 3) (PacingStateDocument.parse """{"schemaVersion":3,"revision":1,"holds":[]}"""))

          t "schema 1 state migrates its latches without inventing window identity" (fun () ->
              match PacingStateDocument.parse """{"holds":[{"key":"codex/weekly","provider":"codex","scopeKind":"global","since":"2026-10-03T17:00:00Z"}]}""" with
              | PacingStateRead.Migrated(state, 1) ->
                  let hold = state.Holds.Values |> Seq.exactlyOne
                  Assert.equal (HoldBasis.WeeklyLead None) hold.Basis
              | other -> failwith $"expected a schema 1 migration, got {other}")

          t "schema 2 round-trips holds and revision" (fun () ->
              let state =
                  PacingState.ofHolds
                      [ hardHold 99.5m (now + TimeSpan.FromHours 1.0)
                        { Key = "codex/weekly"
                          Provider = "codex"
                          Scope = QuotaScope.Model "Fable"
                          Since = now
                          Basis = HoldBasis.WeeklyLead None } ]

              Assert.equal (PacingStateRead.Current(state, 7L)) (PacingStateDocument.parse (PacingStateDocument.serialize state 7L)))

          t "unreadable state maps to indeterminate integrity and is never overwritten" (fun () ->
              let _, integrity = PacingStateIntegrity.ofRead (PacingStateRead.Unreadable "corrupt")

              match integrity with
              | StateIntegrity.Indeterminate _ -> ()
              | StateIntegrity.Intact -> failwith "unreadable state must be indeterminate"

              let transaction =
                  PacingOperations.decide "codex" None (snapshotOf [] fresh) now false (PacingStateRead.Unreadable "corrupt")

              Assert.equal None transaction.Write
              Assert.isTrue (not transaction.Decision.MayProceed) "unreadable state must not authorize work") ]

    let private storeTests =
        let decideWith (snapshot: ProviderSnapshot) read =
            PacingOperations.decide "codex" None snapshot now false read

        [ t "a corrupt state file blocks work and is preserved as evidence" (fun () ->
              withDirectory (fun directory ->
                  File.WriteAllText(PacingPersistence.holdPath directory, "{corrupt")
                  let store = PacingPersistence.stateStore directory

                  match store.Transact(decideWith (snapshotOf [ weekly 0.0 ] fresh)) with
                  | Error fault -> failwith (PacingStoreFault.message fault)
                  | Ok transaction ->
                      Assert.equal (Some PacingReasonKind.StateIndeterminate) (kind transaction.Decision)
                      Assert.equal "{corrupt" (File.ReadAllText(PacingPersistence.holdPath directory))))

          t "a hard hold survives a process restart" (fun () ->
              withDirectory (fun directory ->
                  let established = PacingPersistence.stateStore directory
                  established.Transact(decideWith (snapshotOf [ session 99m ] fresh)) |> ignore

                  let restarted = PacingPersistence.stateStore directory

                  match restarted.Transact(decideWith (snapshotOf [] outage)) with
                  | Error fault -> failwith (PacingStoreFault.message fault)
                  | Ok transaction ->
                      Assert.equal (Some PacingReasonKind.HardLimit) (kind transaction.Decision)))

          t "each persisted write advances the revision" (fun () ->
              withDirectory (fun directory ->
                  let store = PacingPersistence.stateStore directory

                  for _ in 1..3 do
                      store.Transact(decideWith (snapshotOf [ weekly 0.0 ] fresh)) |> ignore

                  match PacingPersistence.readState directory with
                  | PacingStateRead.Current(_, revision) -> Assert.equal 3L revision
                  | other -> failwith $"expected current state, got {other}"))

          t "concurrent transactions do not lose updates" (fun () ->
              withDirectory (fun directory ->
                  let workers = 8

                  let addHold index (read: PacingStateRead) =
                      let existing, integrity = PacingStateIntegrity.ofRead read

                      let hold =
                          { Key = $"codex/w{index}"
                            Provider = "codex"
                            Scope = QuotaScope.Global
                            Since = now
                            Basis = HoldBasis.WeeklyLead None }

                      let state = { Holds = existing.Holds |> Map.add (PacingHold.id hold) hold }

                      { Decision =
                          { MayProceed = false
                            Reasons = []
                            BindingReason = None
                            State = state
                            LeadByWindow = Map.empty }
                        Integrity = integrity
                        Write = Some state }

                  [ 1..workers ]
                  |> List.map (fun index ->
                      Task.Run(fun () ->
                          match (PacingPersistence.stateStore directory).Transact(addHold index) with
                          | Ok _ -> ()
                          | Error fault -> failwith (PacingStoreFault.message fault)))
                  |> Array.ofList
                  |> Task.WaitAll

                  match PacingPersistence.readState directory with
                  | PacingStateRead.Current(state, revision) ->
                      Assert.equal workers state.Holds.Count
                      Assert.equal (int64 workers) revision
                  | other -> failwith $"expected current state, got {other}"))

          t "lock contention is a typed fault, not a crash" (fun () ->
              withDirectory (fun directory ->
                  use _held =
                      new FileStream(Path.Combine(directory, "hold.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                  match (PacingPersistence.stateStore directory).Transact(decideWith (snapshotOf [] fresh)) with
                  | Error(PacingStoreFault.LockUnavailable _) -> ()
                  | other -> failwith $"expected a lock fault, got {other}"))

          t "a state write failure is a typed fault, not silent success" (fun () ->
              withDirectory (fun directory ->
                  // A directory where the state file belongs makes the atomic replace fail.
                  Directory.CreateDirectory(PacingPersistence.holdPath directory) |> ignore

                  match (PacingPersistence.stateStore directory).Transact(decideWith (snapshotOf [ weekly 9.0 ] fresh)) with
                  | Error(PacingStoreFault.WriteFailed _) -> ()
                  | other -> failwith $"expected a write fault, got {other}"))

          t "quarantine moves unreadable state aside and refuses readable state" (fun () ->
              withDirectory (fun directory ->
                  let store = PacingPersistence.stateStore directory
                  store.Transact(decideWith (snapshotOf [ weekly 0.0 ] fresh)) |> ignore

                  match store.Quarantine now with
                  | Error _ -> ()
                  | Ok _ -> failwith "readable state must not be quarantined"

                  File.WriteAllText(PacingPersistence.holdPath directory, "{corrupt")

                  match store.Quarantine now with
                  | Ok(Some path) ->
                      Assert.isTrue (File.Exists path) "the unreadable document is kept as evidence"
                      Assert.equal PacingStateRead.Absent (PacingPersistence.readState directory)
                  | other -> failwith $"expected a quarantine path, got {other}")) ]

    /// A deterministic runtime: injected clock, no real sleeping.
    let private fakeRuntime (transact: (PacingStateRead -> PacingTransaction) -> Result<PacingTransaction, PacingStoreFault>) (observe: DateTimeOffset -> ProviderSnapshot) =
        let clock = ref now
        let sleeps = ref []

        let runtime =
            { Observer = { Observe = fun at _ _ -> observe at }
              State = { Transact = transact; Quarantine = fun _ -> Ok None }
              Override = { IsEnabled = (fun () -> false); SetEnabled = fun _ -> Ok() }
              Clock =
                { Now = fun () -> clock.Value
                  Sleep =
                    fun delay ->
                        sleeps.Value <- delay :: sleeps.Value
                        clock.Value <- clock.Value + delay }
              Events = { Write = ignore }
              Context = { ResolveModel = fun _ explicitModel _ -> explicitModel }
              MaxGateWait = fun _ -> TimeSpan.FromHours 2.0 }

        runtime, sleeps

    let private gateTests =
        [ t "the gate fails closed on a state fault" (fun () ->
              let runtime, sleeps =
                  fakeRuntime (fun _ -> Error(PacingStoreFault.WriteFailed "disk full")) (fun _ -> snapshotOf [] fresh)

              match PacingOperations.gate runtime "codex" None with
              | PacingGateOutcome.Faulted(PacingStoreFault.WriteFailed _) -> Assert.equal [] sleeps.Value
              | other -> failwith $"expected a faulted gate, got {other}")

          t "the gate denies indeterminate state immediately rather than waiting" (fun () ->
              let runtime, sleeps =
                  fakeRuntime
                      (fun action -> Ok(action (PacingStateRead.Unreadable "corrupt")))
                      (fun _ -> snapshotOf [] fresh)

              match PacingOperations.gate runtime "codex" None with
              | PacingGateOutcome.Denied reason ->
                  Assert.equal PacingReasonKind.StateIndeterminate reason.Kind
                  Assert.equal [] sleeps.Value
              | other -> failwith $"expected an immediate denial, got {other}")

          t "the gate waits deterministically and releases when the hard window resets" (fun () ->
              let reset = now + TimeSpan.FromSeconds 12.0
              let state = ref PacingState.empty

              let transact (action: PacingStateRead -> PacingTransaction) =
                  let read =
                      if state.Value.Holds.IsEmpty then PacingStateRead.Absent
                      else PacingStateRead.Current(state.Value, 1L)

                  let transaction: PacingTransaction = action read
                  transaction.Write |> Option.iter (fun written -> state.Value <- written)
                  Ok transaction

              let observe at =
                  if at < reset then snapshotOf [ window "session" 99m sessionLength reset ] fresh
                  else snapshotOf [] fresh

              let runtime, sleeps = fakeRuntime transact observe

              match PacingOperations.gate runtime "codex" None with
              | PacingGateOutcome.Proceed ->
                  Assert.equal [ 5.0; 5.0; 2.0 ] (sleeps.Value |> List.rev |> List.map _.TotalSeconds)
              | other -> failwith $"expected release after reset, got {other}") ]

    let tests = domainTests @ observationTests @ documentTests @ storeTests @ gateTests
