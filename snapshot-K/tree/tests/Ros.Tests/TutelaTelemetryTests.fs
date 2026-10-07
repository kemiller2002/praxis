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
                Assert.equal true (metrics |> List.forall (fun m -> m.SubjectRef = "abc123")) }
          { Name = "Tutela trend reports regressions without inventing missing measurements"
            Run = fun () ->
                let at n = DateTimeOffset.Parse(sprintf "2026-09-2%dT16:00:00Z" n)
                let first =
                    { SubjectRef="a"; CollectedAt=at 3; Verified=Some 10; Violated=Some 0; Unknown=None; Stale=Some 0
                      UnknownSecurityEffects=Some 0; ActiveExceptions=Some 0; ExpiredExceptions=Some 0
                      IndependentVerificationCoverage=Some 0.90; RecurringFindings=Some 0; SecuritySensitiveChurn=Some 2 }
                let second =
                    { first with SubjectRef="b"; CollectedAt=at 4; Violated=Some 1; UnknownSecurityEffects=Some 2
                                 IndependentVerificationCoverage=Some 0.70 }
                let trend = TutelaTrend.derive [ second; first ]
                Assert.equal (Some "b") (trend.Latest |> Option.map (fun p -> p.SubjectRef))
                Assert.equal true (trend.Regressions |> List.contains "violated invariants increased")
                Assert.equal true (trend.Regressions |> List.contains "unknown security effects increased")
                Assert.equal true (trend.Regressions |> List.contains "independent verification coverage decreased")
                Assert.equal None (trend.Latest |> Option.bind (fun p -> p.Unknown)) } ]
