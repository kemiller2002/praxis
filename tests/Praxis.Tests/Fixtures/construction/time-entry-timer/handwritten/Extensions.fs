namespace TimerPilot

open System
open Generated.Timer

/// Handwritten implementations of the extension contracts required by the
/// generated Timer workflow (IMPLEMENT:ITimerPorts.CurrentTime and
/// IMPLEMENT:ITimerPorts.Perform/Stop).
///
/// Timer state is per-session and not durable (L145-L148): no persistence port
/// exists, and these ports keep nothing beyond what the host passes in.
module TimerExtensions =

    /// Ports backed by host-supplied functions.
    /// `clock` reads the current instant (every command reads it).
    /// `completeStop` performs the Stop completion effect, i.e. hands the
    /// stopped timer to the shared entry-creation path (the same
    /// `Commands.create` used by manual entry). It is supplied by the host
    /// because the creation path lives outside this workflow.
    type TimerPorts(clock: unit -> DateTimeOffset, completeStop: TimerDecision -> TimerEffectOutcome) =
        interface ITimerPorts with
            member _.CurrentTime() = clock ()
            member _.Perform(effect, decision) =
                match effect with
                | TimerEffect.Stop -> completeStop decision

    /// External (oracle-facing) name of each requested effect.
    let effectName (effect: TimerEffect) : string =
        match effect with
        | TimerEffect.Stop -> "complete-entry"
