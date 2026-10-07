namespace Ros.Tests

open Ros.Domain.Telemetry

[<RequireQualifiedAccess>]
module TelemetrySummaryTests =
    let private execution executionId provider runtime sessionId startedAt finalizedAt metrics : SummaryExecution =
        { ExecutionId = executionId
          Provider = provider
          Runtime = runtime
          SessionId = sessionId
          StartedAt = startedAt
          FinalizedAt = finalizedAt
          Metrics = metrics }

    let private measurement id unit value collectedAt aggregation : SummaryMetricMeasurement =
        { Id = id
          Unit = unit
          Currency = None
          DimensionsKey = "{}"
          Value = value
          CollectedAt = collectedAt
          RegistryAggregation = Some aggregation
          SelfAggregation = aggregation }

    let tests =
        [ { Name = "TimingSummary.compute with no executions reports zero active/finalized and every timing figure null"
            Run = fun () ->
                let timing = TimingSummary.compute []
                Assert.equal false timing.FullyFinalized
                Assert.equal 0 timing.FinalizedExecutionCount
                Assert.equal 0 timing.ActiveExecutionCount
                Assert.equal None timing.EarliestStartedAt
                Assert.equal None timing.LatestFinalizedAt
                Assert.equal None timing.CalendarSpanMs }

          { Name = "TimingSummary.compute with a still-active execution nulls every finalized-only figure but still reports earliestStartedAt"
            Run = fun () ->
                let executions =
                    [ execution "EXE-1" "anthropic" "claude-code" None (Some "2026-01-01T00:00:00.000Z") None []
                      execution "EXE-2" "anthropic" "claude-code" None (Some "2026-01-01T00:01:00.000Z") (Some "2026-01-01T00:05:00.000Z") [] ]

                let timing = TimingSummary.compute executions
                Assert.equal false timing.FullyFinalized
                Assert.equal 1 timing.FinalizedExecutionCount
                Assert.equal 1 timing.ActiveExecutionCount
                Assert.equal (Some "2026-01-01T00:00:00.000Z") timing.EarliestStartedAt
                Assert.equal (Some "2026-01-01T00:05:00.000Z") timing.LatestFinalizedAt
                Assert.equal None timing.CalendarSpanMs
                Assert.equal None timing.TotalExecutionWallMs
                Assert.equal None timing.OverlappingExecutionMs }

          { Name = "TimingSummary.compute over two non-overlapping finalized spans sums wall time equal to the calendar span, with zero overlap"
            Run = fun () ->
                let executions =
                    [ execution "EXE-1" "a" "r" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:05:00.000Z") []
                      execution "EXE-2" "a" "r" None (Some "2026-01-01T00:05:00.000Z") (Some "2026-01-01T00:10:00.000Z") [] ]

                let timing = TimingSummary.compute executions
                Assert.equal true timing.FullyFinalized
                Assert.equal (Some 600000L) timing.CalendarSpanMs
                Assert.equal (Some 600000L) timing.TotalExecutionWallMs
                Assert.equal (Some 0L) timing.OverlappingExecutionMs }

          { Name = "TimingSummary.compute over two overlapping finalized spans reports overlap as the excess of total wall time over the union"
            Run = fun () ->
                let executions =
                    [ execution "EXE-1" "a" "r" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:10:00.000Z") []
                      execution "EXE-2" "a" "r" None (Some "2026-01-01T00:05:00.000Z") (Some "2026-01-01T00:15:00.000Z") [] ]

                let timing = TimingSummary.compute executions
                Assert.equal true timing.FullyFinalized
                // union = 00:00-00:15 = 900000ms; total wall = 600000+600000 = 1200000ms; overlap = 300000ms
                Assert.equal (Some 900000L) timing.CalendarSpanMs
                Assert.equal (Some 1200000L) timing.TotalExecutionWallMs
                Assert.equal (Some 300000L) timing.OverlappingExecutionMs }

          { Name = "MetricAggregation.compute sums a 'sum' metric across samples and flags time.wall_ms with its own note"
            Run = fun () ->
                let samples: MetricAggregation.Sample list =
                    [ { CollectedAt = "2026-01-01T00:00:00.000Z"; Value = 3.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-1" }
                      { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 4.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-2" } ]

                let value, note = MetricAggregation.compute "time.wall_ms" "sum" samples
                Assert.equal (Some 7.0) value
                Assert.equal (Some "sum of execution spans; may exceed calendarSpanMs when executions overlap") note

                let value2, note2 = MetricAggregation.compute "git.files_added" "sum" samples
                Assert.equal (Some 7.0) value2
                Assert.equal None note2 }

          { Name = "MetricAggregation.compute takes the maximum for a 'maximum' metric, with no note"
            Run = fun () ->
                let samples: MetricAggregation.Sample list =
                    [ { CollectedAt = "T0"; Value = 3.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-1" }
                      { CollectedAt = "T1"; Value = 9.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-2" }
                      { CollectedAt = "T2"; Value = 5.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-3" } ]

                let value, note = MetricAggregation.compute "git.baseline_dirty_files" "maximum" samples
                Assert.equal (Some 9.0) value
                Assert.equal None note }

          { Name = "MetricAggregation.compute for 'none' never aggregates, reporting no value and its fixed note"
            Run = fun () ->
                let samples: MetricAggregation.Sample list = [ { CollectedAt = "T0"; Value = 3.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-1" } ]
                let value, note = MetricAggregation.compute "tokens.input" "none" samples
                Assert.equal None value
                Assert.equal (Some "not aggregated; inspect per-execution measurements") note }

          { Name = "MetricAggregation.compute for an unmapped/default aggregation takes the value with the latest collectedAt"
            Run = fun () ->
                let samples: MetricAggregation.Sample list =
                    [ { CollectedAt = "2026-01-01T00:00:02.000Z"; Value = 3.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-1" }
                      { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 9.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-2" } ]

                let value, note = MetricAggregation.compute "cost.usd" "latest" samples
                Assert.equal (Some 3.0) value
                Assert.equal None note }

          { Name = "MetricAggregation.compute for 'latest-per-session' sums only the latest measurement per unique provider/runtime/session, keyed by execution id when no session is set"
            Run = fun () ->
                let samples: MetricAggregation.Sample list =
                    [ { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 10.0; Provider = "anthropic"; Runtime = "claude-code"; SessionId = Some "sess-1"; ExecutionId = "EXE-1" }
                      { CollectedAt = "2026-01-01T00:00:02.000Z"; Value = 15.0; Provider = "anthropic"; Runtime = "claude-code"; SessionId = Some "sess-1"; ExecutionId = "EXE-2" }
                      { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 4.0; Provider = "a"; Runtime = "r"; SessionId = None; ExecutionId = "EXE-3" } ]

                let value, note = MetricAggregation.compute "tokens.input" "latest-per-session" samples
                // session sess-1's latest (15.0, EXE-2) plus the no-session sample keyed by its own execution id (4.0).
                Assert.equal (Some 19.0) value
                Assert.equal (Some "latest value per unique provider session; cumulative snapshots are not summed") note }

          { Name = "MetricAggregation.compute for 'latest-per-session' keeps the first-seen sample on an exact collectedAt tie"
            Run = fun () ->
                let samples: MetricAggregation.Sample list =
                    [ { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 10.0; Provider = "a"; Runtime = "r"; SessionId = Some "sess-1"; ExecutionId = "EXE-1" }
                      { CollectedAt = "2026-01-01T00:00:01.000Z"; Value = 99.0; Provider = "a"; Runtime = "r"; SessionId = Some "sess-1"; ExecutionId = "EXE-2" } ]

                let value, _ = MetricAggregation.compute "tokens.input" "latest-per-session" samples
                Assert.equal (Some 10.0) value }

          { Name = "TelemetrySummary.summarize resolves each group's aggregation from the registry when known, else falls back to the sample's own aggregation field"
            Run = fun () ->
                let executions =
                    [ execution "EXE-1" "anthropic" "claude-code" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:01:00.000Z") [ { measurement "custom.metric" "count" 5.0 "T0" "sum" with RegistryAggregation = None; SelfAggregation = "maximum" } ] ]

                let summary = TelemetrySummary.summarize None executions
                let entry = Assert.single summary.Metrics
                Assert.equal "maximum" entry.Aggregation
                Assert.equal (Some 5.0) entry.Value }

          { Name = "TelemetrySummary.summarize groups distinct metric ids separately and sorts the output by id"
            Run = fun () ->
                let executions =
                    [ execution
                          "EXE-1"
                          "anthropic"
                          "claude-code"
                          None
                          (Some "2026-01-01T00:00:00.000Z")
                          (Some "2026-01-01T00:01:00.000Z")
                          [ measurement "zeta.metric" "count" 1.0 "T0" "sum"; measurement "alpha.metric" "count" 2.0 "T0" "sum" ] ]

                let summary = TelemetrySummary.summarize None executions
                Assert.equal 2 summary.Metrics.Length
                Assert.equal "alpha.metric" summary.Metrics[0].Id
                Assert.equal "zeta.metric" summary.Metrics[1].Id }

          { Name = "TelemetrySummary.summarize reports distinct sorted providers/runtimes and passes the workItemId filter through verbatim"
            Run = fun () ->
                let executions =
                    [ execution "EXE-1" "openai" "codex" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:01:00.000Z") []
                      execution "EXE-2" "anthropic" "claude-code" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:01:00.000Z") []
                      execution "EXE-3" "anthropic" "claude-code" None (Some "2026-01-01T00:00:00.000Z") (Some "2026-01-01T00:01:00.000Z") [] ]

                let summary = TelemetrySummary.summarize (Some "WI-0001") executions
                Assert.equal (Some "WI-0001") summary.WorkItemId
                Assert.equal 3 summary.ExecutionCount
                Assert.equal [ "anthropic"; "openai" ] summary.Providers
                Assert.equal [ "claude-code"; "codex" ] summary.Runtimes } ]
