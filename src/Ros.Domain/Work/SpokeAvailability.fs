namespace Ros.Domain.Work

/// Whether an aggregating control plane could obtain a registered
/// repository's state from that repository's own Praxis (PRX-CTL-007).
/// Each repository is reported on its own: one repository's availability
/// never decides another's.
[<RequireQualifiedAccess>]
type SpokeAvailability =
    /// Its Praxis answered with the expected versioned contract.
    | Available
    /// Its Praxis could not be run: the registered path or launcher is gone,
    /// or the process could not be started.
    | Unreachable of reason: string
    /// Its Praxis ran but does not speak the contract (an older Praxis, or
    /// another contract version).
    | Incompatible of reason: string
    /// Its Praxis speaks the contract but reported that its own recorded
    /// state could not be read.
    | Unreadable of reason: string

[<RequireQualifiedAccess>]
module SpokeAvailability =
    let code (availability: SpokeAvailability) =
        match availability with
        | SpokeAvailability.Available -> "available"
        | SpokeAvailability.Unreachable _ -> "unreachable"
        | SpokeAvailability.Incompatible _ -> "incompatible"
        | SpokeAvailability.Unreadable _ -> "unreadable"

    let reason (availability: SpokeAvailability) : string option =
        match availability with
        | SpokeAvailability.Available -> None
        | SpokeAvailability.Unreachable reason
        | SpokeAvailability.Incompatible reason
        | SpokeAvailability.Unreadable reason -> Some reason

    let isAvailable (availability: SpokeAvailability) = availability = SpokeAvailability.Available
