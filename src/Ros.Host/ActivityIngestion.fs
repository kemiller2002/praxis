namespace Ros.Host

open System
open EchelonFoundry.Ros.Integration
open Ros.ProjectAdministration
open Ros.Persistence

/// The result of one ingestion attempt, already decided in terms an
/// HTTP layer can map directly to a status code -- see `handle` below
/// and docs/migrations/central-integration/MIGRATION-PLAN.md Phase 4's
/// "400 malformed / 422 invalid / 409 conflict / 500 unexpected"
/// pipeline. There is deliberately no case here that maps to 500: an
/// unexpected failure is an exception this module lets propagate, not
/// a modeled outcome, since by definition it was not anticipated.
type IngestOutcome =
    | Accepted of activityId: string
    | Malformed of string
    | Invalid of ActivityObservationError list
    | Conflict of string

/// The transport-independent activity ingestion pipeline: deserialize
/// -> contract validation -> domain state transition -> idempotent
/// persistence. Dependency-injected as plain functions (matching this
/// repository's existing `Ros.Application` operations, which take
/// repository functions rather than an interface/DI container) so this
/// is testable without an HTTP server or a real file on disk -- see
/// tests/Ros.Host.Tests/ActivityIngestionTests.fs.
[<RequireQualifiedAccess>]
module ActivityIngestion =
    let handle
        (findExisting: string -> string -> StoredActivity option)
        (save: StoredActivity -> Result<unit, string>)
        (now: unit -> DateTimeOffset)
        (source: string)
        (json: string)
        : IngestOutcome =
        match ActivitySerialization.deserializeActivity json with
        | Error(MalformedJson message) -> Malformed message
        | Error(InvalidActivity errors) -> Invalid errors
        | Ok activity ->
            let activityId = ActivityId.value activity.ActivityId

            match findExisting source activityId with
            | Some existing ->
                // Idempotent retry: this exact (source, activityId) pair is
                // already recorded. Return the same accepted outcome rather
                // than creating a duplicate or erroring -- a retry must
                // never fail just because it already succeeded once.
                Accepted(ActivityId.value existing.Activity.ActivityId)
            | None ->
                match ExternalActivityState.transition Received Validated with
                | Error message -> Conflict message
                | Ok validated ->
                    match ExternalActivityState.transition validated Recorded with
                    | Error message -> Conflict message
                    | Ok recorded ->
                        let stored =
                            { Source = source
                              Activity = activity
                              State = recorded
                              ReceivedAt = now () }

                        match save stored with
                        | Ok() -> Accepted activityId
                        | Error message -> Conflict message
