namespace Ros.Tests

open System
open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module TutelaTelemetryTests =
    let tests =
        [ { Name = "Tutela metrics preserve missing measurements as missing rather than zero"
            Run = fun () ->
                let snapshot =
                    { SubjectRef = "abc123"; CollectedAt = DateTimeOffset.Parse("2026-09-24T16:00:00Z")
                      Verified = Some 8; Violated = Some 1; Unknown = None; Stale = Some 2
                      UnknownSecurityEffects = Some 1; ActiveExceptions = None; ExpiredExceptions = Some 0
                      IndependentVerificationCoverage = Some 0.75; RecurringFindings = Some 3; SecuritySensitiveChurn = Some 4 }
                let metrics = TutelaMetrics.project snapshot
                Assert.equal false (metrics |> List.exists (fun m -> m.Id = "security.invariants" && m.Dimensions.TryFind "state" = Some "unknown"))
                Assert.equal true (metrics |> List.exists (fun m -> m.Id = "security.independent_verification" && m.Value = 0.75))
                Assert.equal true (metrics |> List.forall (fun m -> m.SubjectRef = "abc123")) } ]
