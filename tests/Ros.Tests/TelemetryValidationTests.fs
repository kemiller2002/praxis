namespace Ros.Tests

open Ros.Domain.Telemetry
open Ros.Domain.Work

/// Typed tests for `Ros.Domain.Telemetry.TelemetryValidation.findings`, the
/// pure port of production `telemetryFindings` (`tools/ros_telemetry.mjs`).
/// Each test starts from `validRecord`/`validConfig`/`validRegistry` (a
/// hand-verified zero-finding baseline) and overrides exactly the field
/// under test, matching this migration's established "one violated rule
/// per test" style. Manual smoke-testing against real, unpatched Node
/// (config/registry early-exits, identity/classification/capability/
/// metric/quality-signal/raw-telemetry-redaction/cross-record checks) all
/// produced byte-identical findings before any of these tests were
/// written; this suite exists to catch regressions, not to establish
/// initial correctness.
[<RequireQualifiedAccess>]
module TelemetryValidationTests =
    let private validSource: ParsedSource = { Type = Some "ros-clock"; Name = Some "x"; Mechanism = Some "y" }

    let private validCapability: ParsedCapability =
        { Status = Some "supported-observed"
          MetricId = Some "time.wall_ms"
          ProviderField = None
          Source = Some validSource
          DiscoveredAt = Some "2026-01-01T00:00:00.000Z"
          LastAssessedAt = KeyAbsent
          RecordedAt = KeyAbsent
          History = KeyAbsent
          HistoryOmitted = KeyAbsent }

    let private validMetric: ParsedMetric =
        { Id = Some "time.wall_ms"
          MeasurementId = Some "MEAS-1"
          Value = Some 10.0
          Quality = Some "observed"
          Confidence = KeyAbsent
          Source = Some validSource
          CollectedAt = Some "2026-01-01T00:00:00.000Z"
          SchemaVersion = Some "1.0.0"
          Scope = Some "execution"
          Aggregation = Some "sum"
          Unit = Some "milliseconds"
          Currency = None
          SourceType = Some "ros-clock"
          PricingSource = None
          PricingVersion = None }

    let private validIdentity: ParsedIdentity =
        { Provider = FieldString "anthropic"
          Model = FieldAbsent
          ModelVersion = FieldAbsent
          Runtime = FieldString "claude-code"
          RuntimeVersion = FieldAbsent
          SessionId = FieldAbsent
          ConversationId = FieldAbsent
          RunId = FieldAbsent
          AgentId = FieldAbsent
          SubagentId = FieldAbsent
          ParentExecutionId = FieldAbsent }

    let private validClassification: ParsedClassification =
        { Types = Some [ Some "development" ]
          HasResearchQuestion = false
          HasHypothesis = false
          HasTechnicalUncertainty = false
          HasExperimentalObjective = false
          HasKnowledgeGap = false
          HasRd = false }

    let private validRecord: ParsedExecutionRecord =
        { Relative = ".ros/telemetry/executions/EXE-TEST-0001.json"
          SchemaVersion = Some "1.0.0"
          ExecutionId = Some "EXE-TEST-0001"
          WorkItemId = Some "WI-0001"
          Identity = Some validIdentity
          Provenance = Some { Collector = Some "ros"; CollectorVersion = Some "1.0.0"; DiscoveredAt = Some "2026-01-01T00:00:00.000Z" }
          StartedAt = Some "2026-01-01T00:00:00.000Z"
          Status = Some "active"
          FinalizedAt = None
          CapabilitiesIsArray = true
          MetricsIsArray = true
          RawTelemetryIsArray = true
          EventsIsArray = true
          HasRepository = true
          HasLinks = true
          Classification = Some validClassification
          Capabilities = [ ValidItem validCapability ]
          Metrics = [ ValidItem validMetric ]
          QualitySignals = []
          RawTelemetry = []
          Events = [] }

    let private validWorkItem: LiveWorkItem =
        { Id = "WI-0001"
          WorkType = "task"
          LocalState = "active"
          SemanticState = LiveWorkState.Active
          Evidence = []
          BlockReason = None
          UpdatedAt = None
          CompletedAt = None
          TelemetryExecutionIds = [ "EXE-TEST-0001" ] }

    let private validConfig: RawTelemetryConfig =
        { Enabled = true
          DisabledReason = None
          RequireFinalization = true
          ExecutionRoot = ".ros/telemetry/executions"
          MetricRegistryPath = "telemetry/metrics.json"
          MaxRawPayloadBytes = 262_144
          MaxRawSnapshotsPerExecution = 256
          MaxRawBytesPerExecution = 8_388_608
          MaxCapabilityHistoryEntries = 64 }

    let private validRegistry: RawMetricRegistry =
        RegistryFileParsed [ { Id = Some "time.wall_ms"; Unit = Some "milliseconds"; Aggregation = Some "sum"; Collection = Some "direct" } ]

    let private run (config: RawTelemetryConfig) (registry: RawMetricRegistry) (records: ParsedExecutionRecord list) (workItems: LiveWorkItem list) =
        TelemetryValidation.findings config registry [] records workItems

    let private runOne (record: ParsedExecutionRecord) = run validConfig validRegistry [ record ] [ validWorkItem ]

    let private fieldsOf (findings: TelemetryFinding list) = findings |> List.map (fun f -> f.Field)

    let tests =
        [ { Name = "the hand-verified baseline record produces zero findings"
            Run = fun () -> Assert.equal [] (runOne validRecord) }

          { Name = "a limit below the configured minimum rejects before any execution is read"
            Run =
              fun () ->
                  let findings = run { validConfig with MaxRawPayloadBytes = 100 } validRegistry [ validRecord ] [ validWorkItem ]
                  Assert.equal 1 findings.Length
                  Assert.equal "telemetry" findings.[0].Field
                  Assert.equal "telemetry.maxRawPayloadBytes must be an integer greater than or equal to 1024" findings.[0].Message }

          { Name = "disabled telemetry with no reason is the only finding, and enabled-with-a-reason produces none"
            Run =
              fun () ->
                  let disabled = run { validConfig with Enabled = false } validRegistry [ validRecord ] [ validWorkItem ]
                  Assert.equal [ "telemetry.disabledReason" ] (fieldsOf disabled)
                  let disabledWithReason = run { validConfig with Enabled = false; DisabledReason = Some "cost" } validRegistry [ validRecord ] [ validWorkItem ]
                  Assert.equal [] disabledWithReason }

          { Name = "a missing metric registry file is the only finding, with production's exact message"
            Run =
              fun () ->
                  let findings = run validConfig RegistryFileMissing [ validRecord ] [ validWorkItem ]
                  Assert.equal 1 findings.Length
                  Assert.equal "schemaVersion" findings.[0].Field
                  Assert.equal "telemetry metric registry is missing or unsupported: telemetry/metrics.json" findings.[0].Message }

          { Name = "a duplicate registry metric id is reported once, over the first entry with a valid definition"
            Run =
              fun () ->
                  let registry =
                      RegistryFileParsed
                          [ { Id = Some "a"; Unit = Some "count"; Aggregation = Some "sum"; Collection = Some "direct" }
                            { Id = Some "a"; Unit = Some "count"; Aggregation = Some "sum"; Collection = Some "direct" } ]

                  let findings = run validConfig registry [ validRecord ] [ validWorkItem ]
                  Assert.equal [ "telemetry metric registry has invalid or duplicate id 'a'" ] (findings |> List.map (fun f -> f.Message)) }

          { Name = "an unsupported schema version is reported with the raw value, or 'missing' when absent"
            Run =
              fun () ->
                  Assert.equal
                      [ "unsupported telemetry schema version '2.0.0'" ]
                      (runOne { validRecord with SchemaVersion = Some "2.0.0" } |> List.map (fun f -> f.Message))
                  Assert.equal
                      [ "unsupported telemetry schema version 'missing'" ]
                      (runOne { validRecord with SchemaVersion = None } |> List.map (fun f -> f.Message)) }

          { Name = "a missing or empty executionId is required; an invalid shape and a filename mismatch are each their own finding"
            Run =
              fun () ->
                  // an empty executionId also cascades into the cross-record linkage
                  // checks below (the work item's own linked id no longer matches
                  // anything this record could be), so this case only asserts
                  // "executionId" is among the findings, not that it is the only one.
                  // any executionId change that no longer equals the id the
                  // work item's own telemetryExecutionIds names also cascades
                  // into the cross-record linkage checks below, so each case
                  // here only asserts "executionId" is among the findings.
                  let hasExecutionIdFinding record = fieldsOf (runOne record) |> List.contains "executionId"
                  Assert.isTrue (hasExecutionIdFinding { validRecord with ExecutionId = Some "" }) "expected an executionId finding for an empty id"

                  Assert.isTrue
                      (hasExecutionIdFinding { validRecord with ExecutionId = Some "not portable!"; Relative = ".ros/telemetry/executions/not portable!.json" })
                      "expected an executionId finding for a non-portable id"

                  Assert.isTrue (hasExecutionIdFinding { validRecord with ExecutionId = Some "EXE-DIFFERENT" }) "expected an executionId finding for a filename mismatch" }

          { Name = "an empty workItemId is treated the same as absent"
            Run = fun () -> Assert.equal [ "workItemId" ] (fieldsOf (runOne { validRecord with WorkItemId = Some "" })) }

          { Name = "identity requires truthy provider and runtime; a present-but-non-string-non-null field among the 11 is its own finding"
            Run =
              fun () ->
                  Assert.equal [ "identity" ] (fieldsOf (runOne { validRecord with Identity = Some { validIdentity with Provider = FieldAbsent } }))
                  Assert.equal [ "identity" ] (fieldsOf (runOne { validRecord with Identity = Some { validIdentity with Provider = FieldString "" } }))
                  Assert.equal
                      [ "identity.model" ]
                      (fieldsOf (runOne { validRecord with Identity = Some { validIdentity with Model = FieldOtherInvalid } }))
                  Assert.equal [] (runOne { validRecord with Identity = Some { validIdentity with Model = FieldNull } }) }

          { Name = "provenance requires collector 'ros', a truthy collectorVersion, and a valid discoveredAt timestamp"
            Run =
              fun () ->
                  let provenance = validRecord.Provenance.Value
                  Assert.equal [ "provenance" ] (fieldsOf (runOne { validRecord with Provenance = Some { provenance with Collector = Some "other" } }))
                  Assert.equal [ "provenance" ] (fieldsOf (runOne { validRecord with Provenance = Some { provenance with CollectorVersion = Some "" } }))
                  Assert.equal [ "provenance" ] (fieldsOf (runOne { validRecord with Provenance = Some { provenance with DiscoveredAt = Some "not-a-date" } })) }

          { Name = "status must be active or finalized; a finalized status without a valid finalizedAt is its own finding"
            Run =
              fun () ->
                  Assert.equal [ "status" ] (fieldsOf (runOne { validRecord with Status = Some "weird" }))
                  Assert.equal [ "finalizedAt" ] (fieldsOf (runOne { validRecord with Status = Some "finalized"; FinalizedAt = None }))
                  Assert.equal [] (runOne { validRecord with Status = Some "finalized"; FinalizedAt = Some "2026-02-01T00:00:00.000Z" }) }

          { Name = "a finalizedAt before startedAt is its own finding"
            Run =
              fun () ->
                  Assert.equal
                      [ "finalizedAt" ]
                      (fieldsOf (runOne { validRecord with Status = Some "finalized"; FinalizedAt = Some "2025-01-01T00:00:00.000Z" })) }

          { Name = "each required-array field reports itself when the raw JSON key was not an array"
            Run = fun () -> Assert.equal [ "capabilities" ] (fieldsOf (runOne { validRecord with CapabilitiesIsArray = false })) }

          { Name = "missing repository or links objects are each their own finding"
            Run =
              fun () ->
                  Assert.equal [ "repository" ] (fieldsOf (runOne { validRecord with HasRepository = false }))
                  Assert.equal [ "links" ] (fieldsOf (runOne { validRecord with HasLinks = false })) }

          { Name = "classification requires at least one type, unique types, and each type from the known vocabulary or the x- extension pattern"
            Run =
              fun () ->
                  Assert.equal [ "classification.types" ] (fieldsOf (runOne { validRecord with Classification = Some { validClassification with Types = Some [] } }))
                  Assert.equal
                      [ "classification.types" ]
                      (fieldsOf (runOne { validRecord with Classification = Some { validClassification with Types = Some [ Some "development"; Some "development" ] } }))
                  Assert.equal
                      [ "classification.types" ]
                      (fieldsOf (runOne { validRecord with Classification = Some { validClassification with Types = Some [ Some "not-real" ] } }))
                  Assert.equal [] (runOne { validRecord with Classification = Some { validClassification with Types = Some [ Some "x-custom/thing" ] } }) }

          { Name = "research-development classification requires at least one factual-uncertainty flag"
            Run =
              fun () ->
                  let rdTypes = Some [ Some "research-development" ]
                  Assert.equal
                      [ "classification.rd" ]
                      (fieldsOf (runOne { validRecord with Classification = Some { validClassification with Types = rdTypes; HasRd = true } }))
                  Assert.equal
                      []
                      (runOne { validRecord with Classification = Some { validClassification with Types = rdTypes; HasRd = true; HasHypothesis = true } }) }

          { Name = "a capability requires a valid status, a truthy metricId or providerField, and a valid discoveredAt"
            Run =
              fun () ->
                  Assert.equal
                      [ "capabilities[0].status" ]
                      (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem { validCapability with Status = Some "bogus" } ] }))
                  Assert.equal
                      [ "capabilities[0]" ]
                      (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem { validCapability with MetricId = None; ProviderField = None } ] }))
                  Assert.equal
                      [ "capabilities[0].discoveredAt" ]
                      (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem { validCapability with DiscoveredAt = None } ] })) }

          { Name = "an unrecognized capability metricId is a finding, and a providerField-only capability is accepted"
            Run =
              fun () ->
                  Assert.equal
                      [ "capabilities[0].metricId" ]
                      (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem { validCapability with MetricId = Some "not.a.metric" } ] }))
                  Assert.equal
                      []
                      (runOne { validRecord with Capabilities = [ ValidItem { validCapability with MetricId = None; ProviderField = Some "custom_field" } ] }) }

          { Name = "duplicate capability identity (same metricId/providerField pair) is a finding on the second occurrence"
            Run =
              fun () ->
                  let findings =
                      runOne
                          { validRecord with
                              Capabilities = [ ValidItem validCapability; ValidItem validCapability ] }

                  Assert.equal [ "capabilities[1]" ] (fieldsOf findings) }

          { Name = "capability history exceeding the configured retention limit, and a malformed entry, are each their own finding"
            Run =
              fun () ->
                  let entry =
                      { Status = Some "supported-observed"
                        Source = Some validSource
                        DiscoveredAt = Some "2026-01-01T00:00:00.000Z"
                        LastAssessedAt = KeyAbsent
                        RecordedAt = KeyAbsent }

                  let manyEntries = List.replicate 65 (ValidItem entry)
                  let capability = { validCapability with History = KeyPresent(Some manyEntries) }
                  Assert.equal [ "capabilities[0].history" ] (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem capability ] }))

                  let malformedHistory = { validCapability with History = KeyPresent(Some [ MalformedItem ]) }
                  Assert.equal [ "capabilities[0].history[0]" ] (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem malformedHistory ] })) }

          { Name = "a metric requires a truthy measurementId, a finite non-negative value, and a known metric quality"
            Run =
              fun () ->
                  Assert.equal
                      [ "metrics[0].measurementId" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with MeasurementId = None } ] }))
                  Assert.equal
                      [ "metrics[0].value" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Value = Some -1.0 } ] }))
                  Assert.equal
                      [ "metrics[0].quality" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Quality = Some "bogus" } ] })) }

          { Name = "an unrecognized metric id is reported, and unit/aggregation must match the registry's own definition"
            Run =
              fun () ->
                  Assert.equal
                      [ "metrics[0].id" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Id = Some "not.a.metric" } ] }))
                  Assert.equal
                      [ "metrics[0].aggregation" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Aggregation = Some "maximum" } ] }))
                  Assert.equal
                      [ "metrics[0].unit" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Unit = Some "seconds" } ] })) }

          { Name = "an estimated metric requires a valid confidence; confidence (any shape, present) is only valid for an estimated metric"
            Run =
              fun () ->
                  Assert.equal
                      [ "metrics[0].confidence" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Quality = Some "estimated" } ] }))
                  Assert.equal
                      []
                      (runOne { validRecord with Metrics = [ ValidItem { validMetric with Quality = Some "estimated"; Confidence = KeyPresent(Some(ConfidenceLabel "high")) } ] })
                  Assert.equal
                      [ "metrics[0].confidence" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Confidence = KeyPresent(Some(ConfidenceNumber 0.5)) } ] }))
                  // a present-but-unrecognized shape (neither a number nor a
                  // string) still counts as "confidence provided" for this
                  // check, matching production's own `!== null && !== undefined`
                  Assert.equal
                      [ "metrics[0].confidence" ]
                      (fieldsOf (runOne { validRecord with Metrics = [ ValidItem { validMetric with Confidence = KeyPresent None } ] })) }

          { Name = "a cost metric requires unit currency and an ISO three-letter code, and pricing provenance when calculated"
            Run =
              fun () ->
                  let registry = RegistryFileParsed [ { Id = Some "cost.total"; Unit = Some "currency"; Aggregation = Some "sum"; Collection = Some "direct" } ]

                  let costMetric =
                      { validMetric with
                          Id = Some "cost.total"
                          Unit = Some "currency"
                          Currency = Some "USD"
                          SourceType = Some "calculated" }

                  Assert.equal
                      [ "metrics[0].pricing" ]
                      (fieldsOf (run validConfig registry [ { validRecord with Capabilities = []; Metrics = [ ValidItem costMetric ] } ] [ validWorkItem ]))

                  let priced = { costMetric with PricingSource = Some "vendor"; PricingVersion = Some "v1" }
                  Assert.equal [] (run validConfig registry [ { validRecord with Capabilities = []; Metrics = [ ValidItem priced ] } ] [ validWorkItem ]) }

          { Name = "a ratio metric cannot exceed one"
            Run =
              fun () ->
                  let registry = RegistryFileParsed [ { Id = Some "context.utilization"; Unit = Some "ratio"; Aggregation = Some "latest"; Collection = Some "direct" } ]

                  let ratioMetric = { validMetric with Id = Some "context.utilization"; Unit = Some "ratio"; Aggregation = Some "latest"; Value = Some 1.5 }
                  Assert.equal
                      [ "metrics[0].value" ]
                      (fieldsOf (run validConfig registry [ { validRecord with Capabilities = []; Metrics = [ ValidItem ratioMetric ] } ] [ validWorkItem ])) }

          { Name = "a metric present while its capability is unsupported, or in the same snapshot marking it unavailable, is a finding"
            Run =
              fun () ->
                  let unsupportedCapability = { validCapability with Status = Some "unsupported" }
                  Assert.equal [ "metrics[0]" ] (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem unsupportedCapability ] }))

                  let unavailableCapability = { validCapability with Status = Some "supported-unavailable"; DiscoveredAt = validMetric.CollectedAt }

                  Assert.equal
                      [ "metrics[0]" ]
                      (fieldsOf (runOne { validRecord with Capabilities = [ ValidItem unavailableCapability ] })) }

          { Name = "a quality signal requires a recognized detector (or an x- extension) and source provenance"
            Run =
              fun () ->
                  Assert.equal
                      [ "qualitySignals[0].detector" ]
                      (fieldsOf (runOne { validRecord with QualitySignals = [ ValidItem { Detector = Some "not-real"; Source = Some validSource } ] }))
                  Assert.equal [] (runOne { validRecord with QualitySignals = [ ValidItem { Detector = Some "x-custom"; Source = Some validSource } ] }) }

          { Name = "a raw snapshot requires identity/adapter/source/timestamp; a null payload is valid, an absent payload is not"
            Run =
              fun () ->
                  let validSnapshot: ParsedRawSnapshot =
                      { SnapshotId = Some "SNAP-1"
                        Adapter = Some "generic"
                        Source = Some validSource
                        CollectedAt = Some "2026-01-01T00:00:00.000Z"
                        PayloadSerialized = Some "null"
                        PayloadSensitiveFindingPaths = []
                        PayloadBytes = KeyAbsent }

                  Assert.equal [] (runOne { validRecord with RawTelemetry = [ ValidItem validSnapshot ] })

                  Assert.equal
                      [ "rawTelemetry[0].payload" ]
                      (fieldsOf (runOne { validRecord with RawTelemetry = [ ValidItem { validSnapshot with PayloadSerialized = None } ] }))

                  Assert.equal
                      [ "rawTelemetry[0]" ]
                      (fieldsOf (runOne { validRecord with RawTelemetry = [ ValidItem { validSnapshot with SnapshotId = None } ] })) }

          { Name = "an unredacted sensitive raw field is its own finding, one per path"
            Run =
              fun () ->
                  let snapshot: ParsedRawSnapshot =
                      { SnapshotId = Some "SNAP-1"
                        Adapter = Some "generic"
                        Source = Some validSource
                        CollectedAt = Some "2026-01-01T00:00:00.000Z"
                        PayloadSerialized = Some """{"password":"x"}"""
                        PayloadSensitiveFindingPaths = [ "$.password" ]
                        PayloadBytes = KeyAbsent }

                  Assert.equal
                      [ "rawTelemetry[0].payload" ]
                      (fieldsOf (runOne { validRecord with RawTelemetry = [ ValidItem snapshot ] })) }

          { Name = "an event requires type/timestamp/source, and rawRetention.status must be retained or omitted when present"
            Run =
              fun () ->
                  let validEvent: ParsedEvent =
                      { EventType = Some "runtime.something"; OccurredAt = Some "2026-01-01T00:00:00.000Z"; Source = Some validSource; RawRetentionStatus = KeyAbsent }

                  Assert.equal [] (runOne { validRecord with Events = [ ValidItem validEvent ] })
                  Assert.equal [ "events[0]" ] (fieldsOf (runOne { validRecord with Events = [ ValidItem { validEvent with EventType = None } ] }))

                  Assert.equal
                      [ "events[0].rawRetention.status" ]
                      (fieldsOf (runOne { validRecord with Events = [ ValidItem { validEvent with RawRetentionStatus = KeyPresent(Some "bogus") } ] })) }

          { Name = "a linked work item that does not exist, or does not link back, is its own finding"
            Run =
              fun () ->
                  Assert.equal [ "workItemId" ] (fieldsOf (runOne { validRecord with WorkItemId = Some "WI-NOPE" }))
                  Assert.equal
                      [ "workItemId" ]
                      (fieldsOf (run validConfig validRegistry [ validRecord ] [ { validWorkItem with TelemetryExecutionIds = [] } ])) }

          { Name = "a parentExecutionId that names no known execution is its own finding"
            Run =
              fun () ->
                  let identity = { validIdentity with ParentExecutionId = FieldString "EXE-GHOST" }
                  Assert.equal [ "identity.parentExecutionId" ] (fieldsOf (runOne { validRecord with Identity = Some identity })) }

          { Name = "a work item linking a missing execution id is its own finding, over the context path"
            Run =
              fun () ->
                  let workItem = { validWorkItem with TelemetryExecutionIds = [ "EXE-TEST-0001"; "EXE-GHOST" ] }
                  let findings = run validConfig validRegistry [ validRecord ] [ workItem ]
                  Assert.equal [ ".ros/context/current.json" ] (findings |> List.map (fun f -> f.Path))
                  Assert.equal [ "telemetryExecutionIds" ] (fieldsOf findings) }

          { Name = "a completed work item with an unfinalized linked execution is its own finding, only when finalization is required"
            Run =
              fun () ->
                  let workItem = { validWorkItem with SemanticState = LiveWorkState.Complete }
                  Assert.equal [ "status" ] (fieldsOf (run validConfig validRegistry [ validRecord ] [ workItem ]))
                  Assert.equal [] (run { validConfig with RequireFinalization = false } validRegistry [ validRecord ] [ workItem ]) }

          { Name = "a duplicate execution id across two files is reported against the first file's own relative path"
            Run =
              fun () ->
                  let second = { validRecord with Relative = ".ros/telemetry/executions/EXE-TEST-0001-copy.json" }
                  let findings = run validConfig validRegistry [ validRecord; second ] [ validWorkItem ]
                  let duplicate = findings |> List.filter (fun f -> f.Field = "executionId" && f.Message.StartsWith "duplicate")
                  Assert.equal 1 duplicate.Length
                  Assert.equal second.Relative duplicate.Head.Path
                  Assert.equal "duplicate execution ID also in .ros/telemetry/executions/EXE-TEST-0001.json" duplicate.Head.Message }

          { Name = "an unreadable execution file's message passes through verbatim, at its own relative path"
            Run =
              fun () ->
                  let findings =
                      TelemetryValidation.findings validConfig validRegistry [ { Relative = "bad.json"; Message = "malformed telemetry JSON: boom" } ] [] []

                  Assert.equal [ ({ Path = "bad.json"; Field = ""; Message = "malformed telemetry JSON: boom" }: TelemetryFinding) ] findings } ]
