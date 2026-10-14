namespace Praxis.Domain.Git

open System

/// Repository policy for periodically refreshing one upstream branch. The
/// interval is wall-clock elapsed time: waiting, a stalled command, and time
/// spent outside the executor all count.
type UpstreamSyncConfig =
    { Enabled: bool
      Remote: string
      Branch: string
      MaxAge: TimeSpan }

[<RequireQualifiedAccess>]
type UpstreamSyncAttempt =
    | Never
    | Succeeded
    | Failed of GitFailure

[<RequireQualifiedAccess>]
type UpstreamSyncAvailability =
    | Disabled
    | Available
    | Unavailable of GitFailure

/// The complete read model reported by `praxis sync`. Optional facts are
/// unavailable, never silently treated as zero or unchanged.
type UpstreamSyncReport =
    { Config: UpstreamSyncConfig
      Availability: UpstreamSyncAvailability
      LastAttemptAt: DateTimeOffset option
      LastAttempt: UpstreamSyncAttempt
      LastSuccessfulCheckAt: DateTimeOffset option
      ElapsedSinceSuccessfulCheck: TimeSpan option
      DueAt: DateTimeOffset option
      Stale: bool
      HeadCommit: CommitId option
      StartingUpstreamCommit: CommitId option
      CurrentUpstreamCommit: CommitId option
      UpstreamChangedSinceStart: bool option
      Ahead: int option
      Behind: int option
      IncomingUpstreamPaths: string list
      UpstreamPathsSinceStart: string list
      LocalChangedPaths: string list
      OverlapPaths: string list
      IntegrationRequired: bool
      SafeForFinalValidation: bool }

[<RequireQualifiedAccess>]
module UpstreamSync =
    let defaultConfig =
        { Enabled = true
          Remote = "origin"
          Branch = "main"
          MaxAge = TimeSpan.FromMinutes 30.0 }

    /// Freshness is measured from the last successful fetch. A failed attempt
    /// never resets the clock, and the checkpoint becomes due at exactly the
    /// configured elapsed-time boundary.
    let freshness (now: DateTimeOffset) (maxAge: TimeSpan) (lastSuccessfulCheckAt: DateTimeOffset option) =
        match lastSuccessfulCheckAt with
        | None -> None, None, true
        | Some successfulAt ->
            let elapsed = max TimeSpan.Zero (now - successfulAt)
            Some elapsed, Some(successfulAt + maxAge), elapsed >= maxAge
