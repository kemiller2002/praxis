namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Tutela
open Praxis.Cli

/// Tutela security metrics wired end to end (TUT-1..5): ingest an
/// assessment with repository/ref/time provenance, derive every listed
/// metric over time, report missing as unknown, and never a single score.
[<RequireQualifiedAccess>]
module TutelaMetricsTests =
    let private at (text: string) = DateTimeOffset.Parse(text + "T12:00:00Z")

    let private assessment (reference: string) (invariants: (string * string) list) (effects: string list) (findings: string list) (exceptions: (string * string) list) =
        let invariantJson =
            invariants
            |> List.map (fun (id, state) ->
                let independent =
                    if id = "SEC-INV-001" then
                        ", \"requiresIndependentVerification\": true, \"verifierAttestations\": [{\"verifier\":\"x\"}]"
                    elif id = "SEC-INV-002" then
                        ", \"requiresIndependentVerification\": true"
                    else
                        ""

                $"{{\"id\":\"{id}\",\"state\":\"{state}\",\"evidence\":[]{independent}}}")
            |> String.concat ","

        let list items = items |> List.map (sprintf "\"%s\"") |> String.concat ","

        let exceptionJson =
            exceptions
            |> List.map (fun (id, expires) -> $"{{\"id\":\"{id}\",\"createdAt\":\"2026-09-01T00:00:00Z\",\"expiresAt\":\"{expires}T00:00:00Z\"}}")
            |> String.concat ","

        $"""{{"schemaVersion":1,"subject":{{"repository":"example/app","ref":"{reference}"}},"posture":"BLOCKED","invariantResults":[{invariantJson}],"findings":[{list findings}],"evidence":[{{"id":"SEC-EVD-001","observedAt":"2026-09-20T12:00:00Z"}}],"unknownSecurityEffects":[{list effects}],"staleEvidence":[],"exceptions":[{exceptionJson}]}}"""

    let private value (point: SecurityMetricPoint) id (dimensions: (string * string) list) =
        point.Metrics
        |> List.tryFind (fun m -> m.Id = id && m.Dimensions = dimensions)
        |> Option.map _.Value
        |> Option.defaultWith (fun () -> failwith $"no metric {id} {dimensions}")

    let private measured point id dimensions =
        match value point id dimensions with
        | MetricValue.Measured v -> v
        | MetricValue.Unknown reason -> failwith $"{id} {dimensions} unknown: {reason}"

    let private isUnknown point id dimensions =
        match value point id dimensions with
        | MetricValue.Unknown _ -> true
        | MetricValue.Measured _ -> false

    let private parse text when' =
        match TutelaStore.parseAssessment text when' with
        | Ok o -> o
        | Error message -> failwith message

    let tests =
        [ { Name = "tutela metrics: an assessment keeps repository, ref, time and source provenance"
            Run =
              fun () ->
                  let text = assessment "abc123" [ "SEC-INV-001", "Verified"; "SEC-INV-002", "Unknown" ] [ "SEC-UNK-001" ] [] []
                  let o = parse text (at "2026-09-25")
                  Assert.equal "example/app" o.Repository
                  Assert.equal "abc123" o.Ref
                  Assert.equal (at "2026-09-25") o.CollectedAt
                  Assert.equal (TutelaStore.sha256 text) o.SourceSha256
                  Assert.equal (Some 2) o.IndependentRequired
                  Assert.equal (Some 1) o.IndependentAttested

                  match TutelaStore.parseAssessment """{"schemaVersion":1,"subject":{"repository":"x"}}""" (at "2026-09-25") with
                  | Ok _ -> failwith "an assessment without a ref has no provenance"
                  | Error message -> Assert.isTrue (message.Contains "ref") message }
          { Name = "tutela metrics: every listed metric is derived over time, and missing is unknown"
            Run =
              fun () ->
                  let one =
                      parse
                          (assessment "r1" [ "SEC-INV-001", "Verified"; "SEC-INV-002", "Violated"; "SEC-INV-003", "Unknown" ] [ "U1"; "U2" ] [ "F1" ] [ "EX-1", "2026-10-30" ])
                          (at "2026-09-25")

                  let two =
                      parse
                          (assessment "r2" [ "SEC-INV-001", "Verified"; "SEC-INV-002", "Verified"; "SEC-INV-003", "Stale" ] [ "U2"; "U3" ] [] [ "EX-1", "2026-10-30"; "EX-2", "2026-09-27" ])
                          (at "2026-09-29")

                  let three =
                      parse (assessment "r3" [ "SEC-INV-001", "Verified"; "SEC-INV-002", "Verified"; "SEC-INV-003", "Verified" ] [] [ "F1" ] [ "EX-1", "2026-10-30" ]) (at "2026-10-05")

                  let points = TutelaHistory.derive false (fun _ _ -> 1) [ three; one; two ]
                  Assert.equal [ "r1"; "r2"; "r3" ] (points |> List.map _.Ref)
                  let p1, p2, p3 = points[0], points[1], points[2]

                  // Invariant state counts and transitions.
                  Assert.equal 1.0 (measured p1 "security.invariants" [ "state", "Violated" ])
                  Assert.isTrue (isUnknown p1 "security.invariant_transitions" []) "no transitions without history"
                  Assert.equal 2.0 (measured p2 "security.invariant_transitions" [])
                  Assert.equal 1.0 (measured p2 "security.invariant_transitions" [ "from", "Violated"; "to", "Verified" ])
                  // Unknown effects created and resolved.
                  Assert.equal 1.0 (measured p2 "security.unknown_effects" [ "state", "created" ])
                  Assert.equal 1.0 (measured p2 "security.unknown_effects" [ "state", "resolved" ])
                  Assert.equal 2.0 (measured p3 "security.unknown_effects" [ "state", "resolved" ])
                  // Evidence age and stale evidence.
                  Assert.equal 5.0 (measured p1 "security.evidence_age" [ "statistic", "max" ])
                  Assert.equal 0.0 (measured p1 "security.evidence" [ "state", "stale" ])
                  // Exceptions created/expired and time-to-expiry.
                  Assert.equal 1.0 (measured p2 "security.exceptions" [ "state", "created" ])
                  Assert.equal 1.0 (measured p2 "security.exceptions" [ "state", "active" ])
                  Assert.equal 1.0 (measured p2 "security.exceptions" [ "state", "expired" ])
                  Assert.equal 30.5 (measured p2 "security.exception_time_to_expiry" [ "statistic", "min" ])
                  Assert.equal 0.0 (measured p3 "security.exceptions" [ "state", "created" ])
                  // Recurring and reopened findings.
                  Assert.equal 1.0 (measured p3 "security.findings" [ "state", "reopened" ])
                  Assert.equal 1.0 (measured p3 "security.findings" [ "state", "recurring" ])
                  // Remediation lead time: U1 (opened 09-25) and F1, violated SEC-INV-002 closed 09-29.
                  Assert.equal 3.0 (measured p2 "security.remediated" [])
                  Assert.equal 4.0 (measured p2 "security.remediation_lead_time" [ "statistic", "max" ])
                  // Boundary changes: Praxis scope expansions between observations.
                  Assert.equal 1.0 (measured p2 "security.boundary_changes" [ "source", "execution-scope-expansion" ])
                  // Independent verification coverage.
                  Assert.equal 0.5 (measured p1 "security.independent_verification" [])
                  // Churn without configured sensitive paths is unknown, never zero.
                  Assert.isTrue (isUnknown p2 "security.sensitive_churn" []) "churn unknown without configuration"
                  Assert.isTrue (isUnknown p2 "security.hotspots" []) "hotspots unknown without configuration" }
          { Name = "tutela metrics: ingest resolves the ref, measures sensitive churn and hotspots, and is idempotent"
            Run =
              fun () ->
                  let root = GitFixture.temporaryDirectory "tutela"

                  try
                      GitFixture.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      GitFixture.configureIdentity root
                      File.WriteAllText(Path.Combine(root, "ros.json"), """{"tutela":{"sensitivePaths":["src/auth/**"]}}""")
                      Directory.CreateDirectory(Path.Combine(root, "src", "auth")) |> ignore
                      let commit message =
                          GitFixture.git root [ "add"; "-A" ] |> ignore
                          GitFixture.git root [ "commit"; "-q"; "-m"; message ] |> ignore
                          GitFixture.git root [ "rev-parse"; "HEAD" ]

                      File.WriteAllText(Path.Combine(root, "src", "auth", "login.fs"), "1")
                      let c1 = commit "one"
                      File.WriteAllText(Path.Combine(root, "src", "auth", "login.fs"), "2")
                      File.WriteAllText(Path.Combine(root, "README.md"), "x")
                      let c2 = commit "two"
                      File.WriteAllText(Path.Combine(root, "src", "auth", "login.fs"), "3")
                      let c3 = commit "three"

                      let ingest reference day =
                          let text = assessment reference [ "SEC-INV-001", "Verified" ] [] [] []

                          match TutelaStore.ingest root text (at day) with
                          | Ok(TutelaStore.Recorded o) -> o
                          | Ok(TutelaStore.AlreadyRecorded _) -> failwith "unexpected duplicate"
                          | Error message -> failwith message

                      let first = ingest c1 "2026-09-25"
                      Assert.equal (Some c1) first.Commit
                      Assert.equal None first.SensitiveChanges
                      let second = ingest c2 "2026-09-26"
                      Assert.equal (Some [ "src/auth/login.fs" ]) second.SensitiveChanges
                      ingest c3 "2026-09-27" |> ignore

                      match TutelaStore.ingest root (assessment c3 [ "SEC-INV-001", "Verified" ] [] [] []) (at "2026-09-28") with
                      | Ok(TutelaStore.AlreadyRecorded _) -> ()
                      | other -> failwith $"re-ingest must be a no-op: {other}"

                      match TutelaStore.metrics root with
                      | Error message -> failwith message
                      | Ok points ->
                          Assert.equal 3 points.Length
                          Assert.equal 1.0 (measured points[1] "security.sensitive_churn" [])
                          Assert.equal 1.0 (measured points[2] "security.hotspots" [])
                          Assert.isTrue (isUnknown points[0] "security.sensitive_churn" []) "first observation has no earlier commit"
                          let json = JsonNode.Parse(TutelaStore.render points)
                          Assert.equal "praxis.tutela-metrics/1" (json["schema"].GetValue<string>())
                          Assert.isTrue ((json["disclaimer"].GetValue<string>()).Contains "not a certification") "decision support, not certification"
                          Assert.isTrue (isNull json["score"]) "no single security score"

                          let unknownMetric =
                              (json["observations"].[0].["metrics"]).AsArray()
                              |> Seq.find (fun m -> m["id"].GetValue<string>() = "security.sensitive_churn")

                          Assert.equal "unknown" (unknownMetric["status"].GetValue<string>())
                          Assert.isTrue (isNull unknownMetric["value"]) "an unknown has no value, not zero"
                  finally
                      Directory.Delete(root, true) }
          { Name = "tutela metrics: Praxis scope expansions count as boundary changes in their window"
            Run =
              fun () ->
                  let root = GitFixture.temporaryDirectory "tutela-scope"

                  try
                      ExecutionStore.appendRecord root "EXE-1" "scope-expanded" [ "expansion", Some "X1" ] (at "2026-09-26")
                      ExecutionStore.appendRecord root "EXE-1" "scope-expanded" [ "expansion", Some "X2" ] (at "2026-09-30")
                      Directory.CreateDirectory(Path.Combine(root, ".ros", "executions", "EXE-1")) |> ignore
                      File.WriteAllText(Path.Combine(root, ".ros", "executions", "EXE-1", "envelope.json"), "{}")
                      Assert.equal 1 (TutelaStore.scopeExpansionsBetween root (at "2026-09-25") (at "2026-09-27"))
                      Assert.equal 2 (TutelaStore.scopeExpansionsBetween root (at "2026-09-25") (at "2026-10-01"))
                  finally
                      Directory.Delete(root, true) }
          { Name = "tutela metrics: the CLI refuses an ingest without input and reports an empty history"
            Run =
              fun () ->
                  let root = GitFixture.temporaryDirectory "tutela-cli"

                  try
                      Assert.equal 2 (TutelaCommands.run root [ "ingest" ])
                      Assert.equal 1 (TutelaCommands.run root [ "ingest"; "--input"; "missing.json" ])
                      Assert.equal 0 (TutelaCommands.run root [ "metrics"; "--json" ])
                      Assert.equal 2 (TutelaCommands.run root [ "score" ])
                  finally
                      Directory.Delete(root, true) } ]
