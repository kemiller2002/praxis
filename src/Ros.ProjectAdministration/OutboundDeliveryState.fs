namespace Ros.ProjectAdministration

/// Which downstream application an outbound delivery is headed to.
/// `Other` exists so a not-yet-named application can be tracked before
/// it earns its own case -- adding a named case later is additive, not
/// a breaking change to anything that already matched on `Other`.
type IntegrationTarget =
    | Chrona
    | Summa
    | Other of string

/// Lifecycle of one outbound delivery attempt (e.g. ROS -> Chrona), per
/// the migration spec's explicit state modeling requirement. `NotReady`
/// covers a target with no adapter installed yet (see
/// docs/migrations/central-integration/MIGRATION-PLAN.md Phase 8) --
/// Central can hold a delivery there indefinitely without it ever being
/// attempted, which is why it is a distinct state from `Pending` rather
/// than deferring the decision to a null adapter lookup at send time.
type OutboundDeliveryState =
    | NotReady
    | Pending
    | Sending
    | Delivered
    | Failed of attempts: int * lastError: string

/// Only the named transitions below are legal, including the retry
/// loop `Sending -> Failed -> Sending`. There is no "set status to X"
/// function anywhere in this module.
[<RequireQualifiedAccess>]
module OutboundDeliveryState =
    let private isLegal =
        function
        | NotReady, Pending -> true
        | Pending, Sending -> true
        | Sending, Delivered -> true
        | Sending, Failed _ -> true
        | Failed _, Sending -> true
        | _ -> false

    let transition (current: OutboundDeliveryState) (next: OutboundDeliveryState) : Result<OutboundDeliveryState, string> =
        if isLegal (current, next) then
            Ok next
        else
            Error $"illegal OutboundDeliveryState transition from {current} to {next}"
