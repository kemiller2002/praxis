namespace Praxis.Domain.Work

open System
open Praxis.Domain.Execution

/// The outcome of one containment probe, judged by the host. `Unknown` means
/// the host could not observe the result (the probe did not run, its report
/// was missing or malformed, or the host observation was unavailable). It is
/// never treated as a pass, and it is never reported as a failure either.
[<RequireQualifiedAccess>]
type ContainmentProbeOutcome =
    | Pass
    | Fail
    | Unknown

[<RequireQualifiedAccess>]
module ContainmentProbeOutcome =
    let toWire outcome =
        match outcome with
        | ContainmentProbeOutcome.Pass -> "pass"
        | ContainmentProbeOutcome.Fail -> "fail"
        | ContainmentProbeOutcome.Unknown -> "unknown"

    let tryParse value =
        match value with
        | "pass" -> Some ContainmentProbeOutcome.Pass
        | "fail" -> Some ContainmentProbeOutcome.Fail
        | "unknown" -> Some ContainmentProbeOutcome.Unknown
        | _ -> None

/// One probe as launched through the confined adapter. `Expected` and
/// `Observed` are short host-chosen descriptions (for example `denied`,
/// `allowed`, `isolated`, `absent`); the outcome is the host's verdict.
type ContainmentProbeResult =
    { Id: string
      Dimension: string
      Expected: string
      Observed: string
      Outcome: ContainmentProbeOutcome
      Detail: string }

/// A pinned host tool the mechanism executes, digested at qualification.
type ContainmentTool = { Name: string; Path: string; Digest: string }

/// A qualification run on this host: the profile and mechanism, the platform
/// and the tool closure it ran with, and every probe and its result. Only
/// probes establish enforcement; nothing here is inferred from configuration.
type ContainmentQualification =
    { Host: string
      Profile: string
      ProfileDigest: string
      Mechanism: string
      OperatingSystem: string
      Kernel: string
      Architecture: string
      Tools: ContainmentTool list
      Probes: ContainmentProbeResult list
      Unsupported: string list
      QualifiedAt: DateTimeOffset }

/// Derives the existing `ContainmentProfile` (PRX-SEC-010/011) from probes.
/// A dimension is enforced only when it has probes and every one passed; a
/// failed probe makes it `unrestricted` (demonstrably not enforced); an
/// unknown probe leaves it `unknown`; an unsupported platform reports
/// `unavailable`. Unknown is never upgraded and never collapsed into failure.
[<RequireQualifiedAccess>]
module LocalContainment =
    let private statusOf (probes: ContainmentProbeResult list) =
        if probes.IsEmpty then RestrictionStatus.Unknown
        elif probes |> List.exists (fun p -> p.Outcome = ContainmentProbeOutcome.Fail) then RestrictionStatus.Unrestricted
        elif probes |> List.exists (fun p -> p.Outcome = ContainmentProbeOutcome.Unknown) then RestrictionStatus.Unknown
        else RestrictionStatus.Enforced

    let restrictions (qualification: ContainmentQualification) : ContainmentRestriction list =
        ContainmentProfile.dimensions
        |> List.map (fun dimension ->
            let probes = qualification.Probes |> List.filter (fun p -> p.Dimension = dimension)
            let status =
                if qualification.Tools.IsEmpty && qualification.Probes.IsEmpty then RestrictionStatus.Unavailable
                else statusOf probes
            { Dimension = dimension
              Status = status
              Mechanism = (if status = RestrictionStatus.Unavailable then None else Some qualification.Mechanism)
              Evidence =
                match status with
                | RestrictionStatus.Enforced ->
                    Some(
                        sprintf "%d probe(s) passed under %s at %s: %s" probes.Length qualification.ProfileDigest
                            (qualification.QualifiedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                            (probes |> List.map _.Id |> String.concat ",")
                    )
                | RestrictionStatus.Unrestricted ->
                    Some("failed probe(s): " + (probes |> List.filter (fun p -> p.Outcome = ContainmentProbeOutcome.Fail) |> List.map _.Id |> String.concat ","))
                | RestrictionStatus.Unknown when not probes.IsEmpty ->
                    Some("unobserved probe(s): " + (probes |> List.filter (fun p -> p.Outcome = ContainmentProbeOutcome.Unknown) |> List.map _.Id |> String.concat ","))
                | _ -> None })

    /// The existing containment profile the qualification supports.
    let profile (qualification: ContainmentQualification) =
        ContainmentProfile.fromReport qualification.Host (restrictions qualification)

    /// Every containment dimension enforced by passing probes.
    let isQualified (qualification: ContainmentQualification) =
        restrictions qualification |> List.forall (fun r -> r.Status = RestrictionStatus.Enforced)

    /// An honest record for a platform with no implemented, probed mechanism:
    /// no tools, no probes, every dimension `unavailable`, never qualified.
    let unsupported host operatingSystem kernel architecture (unsupported: string list) at =
        { Host = host
          Profile = "none"
          ProfileDigest = "none"
          Mechanism = "none"
          OperatingSystem = operatingSystem
          Kernel = kernel
          Architecture = architecture
          Tools = []
          Probes = []
          Unsupported = unsupported
          QualifiedAt = at }
