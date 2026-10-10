namespace Praxis.Application.Work

open System.Threading
open System.Threading.Tasks

/// Host-selected process specification, never decoded from model prose.
/// The future controller owns adapter selection, dependency pins, worktree,
/// environment allowlist, containment and fresh native/ECIR authorization.
type LocalWorkerProcessSpec =
    { Executable: string; ExecutableDigest: string; Arguments: string list
      WorkingDirectory: string; Environment: Map<string, string>; Input: string
      TimeoutMilliseconds: int; MaxOutputBytes: int }
[<RequireQualifiedAccess>]
type LocalWorkerProcessOutcome =
    | PreflightRefused of string | StartUncertain of string
    | Exited of int | TimedOut | Cancelled | OutputLimitExceeded
    | ObservationFailed of string | StreamFailed of string
type LocalWorkerProcessObservation =
    { ProcessIdentity: string option; ProcessStarted: bool option; RootExitObserved: bool
      Outcome: LocalWorkerProcessOutcome; StandardOutput: string; StandardError: string
      CapturedBytes: int }
type LocalWorkerProcessPort =
    { Run: LocalWorkerProcessSpec -> (string -> Result<unit, string>) -> CancellationToken -> Task<LocalWorkerProcessObservation> }
