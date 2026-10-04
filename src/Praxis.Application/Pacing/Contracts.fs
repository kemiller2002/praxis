namespace Praxis.Application.Pacing

open System
open Praxis.Domain.Pacing

type ProviderSnapshot =
    { Provider: string
      ObservedAt: DateTimeOffset
      Windows: QuotaWindow list
      Freshness: ObservationFreshness }

type QuotaObserver =
    { Observe: DateTimeOffset -> string -> string option -> ProviderSnapshot }

type PacingStateStore =
    { WithState: (PacingState -> PacingDecision) -> PacingDecision }

type PacingOverrideStore =
    { IsEnabled: unit -> bool
      SetEnabled: bool -> unit }

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
      Hold: PacingStatusHold option }

[<RequireQualifiedAccess>]
type PacingGateOutcome =
    | Proceed
    | Denied of PacingReason
