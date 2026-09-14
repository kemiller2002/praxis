namespace Ros.ProjectAdministration

/// Lifecycle of one inbound external activity submission, per the
/// migration spec's explicit state modeling requirement
/// (docs/migrations/central-integration/MIGRATION-PLAN.md Phase 3).
type ExternalActivityState =
    | Received
    | Validated
    | Rejected of reason: string
    | Recorded

/// Only the named transitions below are legal. There is no "set status
/// to X" function anywhere in this module -- `transition` is the only
/// way to move between states, and it refuses anything not on this
/// list.
[<RequireQualifiedAccess>]
module ExternalActivityState =
    let private isLegal =
        function
        | Received, Validated -> true
        | Received, Rejected _ -> true
        | Validated, Recorded -> true
        | Validated, Rejected _ -> true
        | _ -> false

    let transition (current: ExternalActivityState) (next: ExternalActivityState) : Result<ExternalActivityState, string> =
        if isLegal (current, next) then
            Ok next
        else
            Error $"illegal ExternalActivityState transition from {current} to {next}"
