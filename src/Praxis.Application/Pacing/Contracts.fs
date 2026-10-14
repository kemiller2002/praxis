namespace Praxis.Application.Pacing

open System
open Praxis.Domain.Pacing

/// Observation status of one expected quota window. Completeness is a
/// per-window property: a response that carries one window cannot vouch for
/// another that is missing or malformed.
[<RequireQualifiedAccess>]
type WindowStatus =
    | Observed
    | Missing
    | Invalid of reason: string
    /// Carried from an earlier reading, or reported by the provider for a
    /// window that has already reset: not evidence about the current window.
    | Stale of reason: string
    /// Reported in a shape or kind this adapter does not support (response
    /// drift). Never silently dropped and never treated as observed.
    | Unsupported of reason: string

type WindowObservation = { Key: string; Status: WindowStatus }

[<RequireQualifiedAccess>]
module WindowObservation =
    let statusCode observation =
        match observation.Status with
        | WindowStatus.Observed -> "observed"
        | WindowStatus.Missing -> "missing"
        | WindowStatus.Invalid _ -> "invalid"
        | WindowStatus.Stale _ -> "stale"
        | WindowStatus.Unsupported _ -> "unsupported"

    let reason observation =
        match observation.Status with
        | WindowStatus.Observed
        | WindowStatus.Missing -> None
        | WindowStatus.Invalid reason
        | WindowStatus.Stale reason
        | WindowStatus.Unsupported reason -> Some reason

    let isComplete (coverage: WindowObservation list) =
        coverage |> List.forall (fun observation -> observation.Status = WindowStatus.Observed)

    /// Diagnostic naming every expected window that was not observed.
    let incompleteness (coverage: WindowObservation list) =
        coverage
        |> List.choose (fun observation ->
            match observation.Status with
            | WindowStatus.Observed -> None
            | WindowStatus.Missing -> Some $"{observation.Key} missing"
            | WindowStatus.Invalid reason -> Some $"{observation.Key} invalid ({reason})"
            | WindowStatus.Stale reason -> Some $"{observation.Key} stale ({reason})"
            | WindowStatus.Unsupported reason -> Some $"{observation.Key} unsupported ({reason})")
        |> String.concat "; "

/// A normalized provider response: the windows it justified plus the status of
/// every window the adapter expected.
type QuotaReading =
    { Windows: QuotaWindow list
      Coverage: WindowObservation list }

/// The adapter that produced a reading, observable in status (PRX-QUAL-005):
/// its identity, the version of its mapping rules, what it can observe, the
/// model families its rules recognize, and the quota bucket it selected.
type PacingAdapterInfo =
    { AdapterId: string
      RulesVersion: string
      Capabilities: string list
      ModelFamilies: ModelFamily list
      Bucket: QuotaBucket option }

type ProviderSnapshot =
    { Provider: ProviderId
      Adapter: PacingAdapterInfo
      ObservedAt: DateTimeOffset
      Windows: QuotaWindow list
      Coverage: WindowObservation list
      Freshness: ObservationFreshness }

/// Outcome of reading persisted pacing safety state. Unreadable and
/// unsupported state are explicit outcomes, never silently empty state.
[<RequireQualifiedAccess>]
type PacingStateRead =
    | Absent
    | Current of state: PacingState * revision: int64
    | Migrated of state: PacingState * fromSchema: int
    | Unreadable of reason: string
    | Unsupported of schemaVersion: int

/// Infrastructure faults while transacting on pacing state. Each carries a
/// stable code so operators and telemetry can distinguish them.
[<RequireQualifiedAccess>]
type PacingStoreFault =
    | LockUnavailable of reason: string
    | WriteFailed of reason: string

[<RequireQualifiedAccess>]
module PacingStoreFault =
    let code fault =
        match fault with
        | PacingStoreFault.LockUnavailable _ -> "PRAXIS-PACING-STATE-LOCK"
        | PacingStoreFault.WriteFailed _ -> "PRAXIS-PACING-STATE-WRITE"

    let message fault =
        match fault with
        | PacingStoreFault.LockUnavailable reason -> $"pacing state lock unavailable: {reason}"
        | PacingStoreFault.WriteFailed reason -> $"pacing state could not be persisted: {reason}"

/// Stable codes of pacing telemetry events (PRX-QUAL-008).
[<RequireQualifiedAccess>]
type PacingEventCode =
    | HoldStarted
    | HoldRetained
    | HoldReleased
    | HardLimit
    | ProviderUnavailable
    | OverrideEnabled
    | OverrideDisabled
    | StateFault

/// One typed pacing transition. Carries provider, scope, reason and reset
/// identity only: never credentials, tokens or raw provider payloads.
type PacingEvent =
    { Code: PacingEventCode
      OccurredAt: DateTimeOffset
      Provider: ProviderId option
      WindowKey: string option
      Reason: PacingReasonKind option
      Detail: string
      ResetsAt: DateTimeOffset option
      ObservedAt: DateTimeOffset option }

/// What a state transaction decided: the decision, and the state to persist.
/// `Write = None` leaves persisted state untouched (used for indeterminate
/// state, which must be preserved as evidence rather than overwritten).
type PacingTransaction =
    { Decision: PacingDecision
      Integrity: StateIntegrity
      Write: PacingState option
      /// The request's model as resolved against the adapter's and the
      /// observed scoped windows' families.
      Model: ModelIdentity
      /// Hold transitions between the state read and the state written
      /// (hold-started, hard-limit, hold-released). Empty when nothing is written.
      Events: PacingEvent list }

type QuotaObserver =
    { Observe: DateTimeOffset -> ProviderId -> string option -> ProviderSnapshot }

type PacingStateStore =
    { Transact: (PacingStateRead -> PacingTransaction) -> Result<PacingTransaction, PacingStoreFault>
      /// Moves unreadable or unsupported state aside so an operator can
      /// explicitly start from empty state. Returns the quarantine path, if any.
      Quarantine: DateTimeOffset -> Result<string option, string> }

type PacingOverrideStore =
    { IsEnabled: unit -> bool
      SetEnabled: bool -> Result<unit, string> }

type PacingClock =
    { Now: unit -> DateTimeOffset
      Sleep: TimeSpan -> unit }

/// Typed event sink. `Last` returns the most recently recorded event, so a
/// new process does not repeat a transition another process already recorded.
type PacingEventSink =
    { Record: PacingEvent -> unit
      Last: unit -> PacingEvent option }

type PacingContextResolver =
    { ResolveModel: ProviderId -> string option -> string -> string option }

type PacingRuntime =
    { Observer: QuotaObserver
      State: PacingStateStore
      Override: PacingOverrideStore
      Clock: PacingClock
      Events: PacingEventSink
      Context: PacingContextResolver
      MaxGateWait: ProviderId -> TimeSpan }

[<RequireQualifiedAccess>]
type PacingFreshnessState =
    | Fresh
    | Stale
    | Unavailable

[<RequireQualifiedAccess>]
module PacingFreshnessState =
    let code state =
        match state with
        | PacingFreshnessState.Fresh -> "fresh"
        | PacingFreshnessState.Stale -> "stale"
        | PacingFreshnessState.Unavailable -> "unavailable"

[<RequireQualifiedAccess>]
type PacingSafetyState =
    | Known
    | Indeterminate

[<RequireQualifiedAccess>]
module PacingSafetyState =
    let code state =
        match state with
        | PacingSafetyState.Known -> "known"
        | PacingSafetyState.Indeterminate -> "indeterminate"

type PacingStatusWindow =
    { Key: string
      Label: string
      UsedPercent: decimal
      DurationMinutes: float
      ResetsAt: DateTimeOffset
      PacePercent: decimal
      LeadHours: float option }

type PacingStatusHold =
    { Kind: string
      Detail: string
      ResumeAt: DateTimeOffset }

type PacingStatusCoverage = { Key: string; Status: string; Reason: string option }

type PacingStatusView =
    { SchemaVersion: int
      Provider: ProviderId
      Model: ModelIdentity
      Adapter: PacingAdapterInfo
      StateDirectory: string
      ObservedAt: DateTimeOffset
      FreshnessState: PacingFreshnessState
      FreshnessReason: string option
      SafetyState: PacingSafetyState
      ProviderError: string option
      MayProceed: bool
      Override: bool
      Windows: PacingStatusWindow list
      Coverage: PacingStatusCoverage list
      StateIntegrity: StateIntegrity
      Hold: PacingStatusHold option
      LastEvent: PacingEvent option }

[<RequireQualifiedAccess>]
type PacingGateOutcome =
    | Proceed
    | Denied of PacingReason
    /// The gate could not establish safe state; it fails closed.
    | Faulted of PacingStoreFault
