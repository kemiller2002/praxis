namespace Ros.Domain.Telemetry

open System

/// Security telemetry projection consumed by Praxis dashboards/trends.
/// Counts preserve unknown/missing state instead of coercing it to zero.
type TutelaMetric =
    { Id: string
      Value: float
      Unit: string
      Dimensions: Map<string,string>
      CollectedAt: DateTimeOffset
      SubjectRef: string }

type TutelaSnapshot =
    { SubjectRef: string
      CollectedAt: DateTimeOffset
      Verified: int option
      Violated: int option
      Unknown: int option
      Stale: int option
      UnknownSecurityEffects: int option
      ActiveExceptions: int option
      ExpiredExceptions: int option
      IndependentVerificationCoverage: float option
      RecurringFindings: int option
      SecuritySensitiveChurn: int option }

[<RequireQualifiedAccess>]
module TutelaMetrics =
    let private count id state value snapshot =
        value |> Option.map (fun v ->
            { Id = id; Value = float v; Unit = "count"
              Dimensions = Map [ "state", state ]
              CollectedAt = snapshot.CollectedAt; SubjectRef = snapshot.SubjectRef })

    let project snapshot =
        [ count "security.invariants" "verified" snapshot.Verified snapshot
          count "security.invariants" "violated" snapshot.Violated snapshot
          count "security.invariants" "unknown" snapshot.Unknown snapshot
          count "security.invariants" "stale" snapshot.Stale snapshot
          count "security.unknown_effects" "open" snapshot.UnknownSecurityEffects snapshot
          count "security.exceptions" "active" snapshot.ActiveExceptions snapshot
          count "security.exceptions" "expired" snapshot.ExpiredExceptions snapshot
          snapshot.IndependentVerificationCoverage |> Option.map (fun v ->
            { Id="security.independent_verification"; Value=v; Unit="ratio"; Dimensions=Map.empty
              CollectedAt=snapshot.CollectedAt; SubjectRef=snapshot.SubjectRef })
          count "security.findings" "recurring" snapshot.RecurringFindings snapshot
          count "security.churn" "security-sensitive" snapshot.SecuritySensitiveChurn snapshot ]
        |> List.choose id
