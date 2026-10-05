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

type WindowObservation = { Key: string; Status: WindowStatus }

[<RequireQualifiedAccess>]
module WindowObservation =
    let statusCode observation =
        match observation.Status with
        | WindowStatus.Observed -> "observed"
        | WindowStatus.Missing -> "missing"
        | WindowStatus.Invalid _ -> "invalid"

    let isComplete (coverage: WindowObservation list) =
        coverage |> List.forall (fun observation -> observation.Status = WindowStatus.Observed)

    /// Diagnostic naming every expected window that was not observed.
    let incompleteness (coverage: WindowObservation list) =
        coverage
        |> List.choose (fun observation ->
            match observation.Status with
            | WindowStatus.Observed -> None
            | WindowStatus.Missing -> Some $"{observation.Key} missing"
            | WindowStatus.Invalid reason -> Some $"{observation.Key} invalid ({reason})")
        |> String.concat "; "

/// A normalized provider response: the windows it justified plus the status of
/// every window the adapter expected.
type QuotaReading =
    { Windows: QuotaWindow list
      Coverage: WindowObservation list }

type ProviderSnapshot =
    { Provider: string
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

/// What a state transaction decided: the decision, and the state to persist.
/// `Write = None` leaves persisted state untouched (used for indeterminate
/// state, which must be preserved as evidence rather than overwritten).
type PacingTransaction =
    { Decision: PacingDecision
      Integrity: StateIntegrity
      Write: PacingState option }

type QuotaObserver =
    { Observe: DateTimeOffset -> string -> string option -> ProviderSnapshot }

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

type PacingEventSink =
    { Write: string -> unit }

type PacingContextResolver =
    { ResolveModel: string -> string option -> string -> string option }

type PacingRuntime =
    { Observer: QuotaObserver
      State: PacingStateStore
      Override: PacingOverrideStore
      Clock: PacingClock
      Events: PacingEventSink
      Context: PacingContextResolver
      MaxGateWait: string -> TimeSpan }

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
      Provider: string
      Model: string option
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
      Hold: PacingStatusHold option }

[<RequireQualifiedAccess>]
type PacingGateOutcome =
    | Proceed
    | Denied of PacingReason
    /// The gate could not establish safe state; it fails closed.
    | Faulted of PacingStoreFault
