namespace Praxis.Domain.Telemetry

/// How an execution's candidate was accepted. First-pass and repaired
/// acceptance are different outcomes, and a repaired acceptance carries its
/// repair-cycle count (CONSTRUCT-010 Phase 6).
[<RequireQualifiedAccess>]
type AcceptanceOutcome =
    | FirstPass
    | Repaired of cycles: int64 option
    | NotAccepted of repairCycles: int64 option
    /// Nothing recorded: unknown, never "not accepted".
    | Unknown

[<RequireQualifiedAccess>]
module Acceptance =
    let firstPassMetric = "acceptance.first_pass"
    let repairedMetric = "acceptance.repaired"
    let repairCyclesMetric = "acceptance.repair_cycles"

    let code (outcome: AcceptanceOutcome) =
        match outcome with
        | AcceptanceOutcome.FirstPass -> "first-pass"
        | AcceptanceOutcome.Repaired _ -> "repaired"
        | AcceptanceOutcome.NotAccepted _ -> "not-accepted"
        | AcceptanceOutcome.Unknown -> "unknown"

    /// The outcome of one execution from its recorded totals, or why those
    /// totals contradict each other. `None` is "not recorded", never zero.
    let outcome (firstPass: int64 option) (repaired: int64 option) (repairCycles: int64 option) : Result<AcceptanceOutcome, string> =
        let positive = Option.exists (fun value -> value > 0L)

        match firstPass, repaired, repairCycles with
        | _ when positive firstPass && positive repaired ->
            Error "a candidate is accepted either on its first pass or after repair, never both"
        | _ when positive firstPass && positive repairCycles ->
            Error "a first-pass acceptance has no repair cycles"
        | _, _, Some 0L when positive repaired ->
            Error "a repaired acceptance requires at least one repair cycle"
        | _ when positive firstPass -> Ok AcceptanceOutcome.FirstPass
        | _ when positive repaired -> Ok(AcceptanceOutcome.Repaired repairCycles)
        | Some 0L, Some 0L, cycles -> Ok(AcceptanceOutcome.NotAccepted cycles)
        | _ -> Ok AcceptanceOutcome.Unknown
