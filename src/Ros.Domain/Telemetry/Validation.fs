namespace Ros.Domain.Telemetry

open Ros.Domain.Work

/// Mirrors production `finding(file, field, message)` (`tools/ros_telemetry.mjs`):
/// the same `{Path; Field; Message}` shape every ROS validator in this
/// migration returns.
type TelemetryFinding =
    { Path: string
      Field: string
      Message: string }

/// `value !== undefined`: distinguishes a JSON key that is genuinely
/// absent from one that is present (even as an unparseable shape, or as
/// JSON `null`) -- a distinction a plain `option` alone cannot make,
/// needed for the handful of `telemetryFindings` checks that only fire
/// "if defined" (`lastAssessedAt`, `recordedAt`, `history`,
/// `historyOmitted`, `payloadBytes`, `confidence`).
type FieldPresence<'a> =
    | KeyAbsent
    | KeyPresent of 'a option

/// Whether one JSON array element parsed as the object shape its field
/// expects, mirroring the repeated `typeof x !== "object"` guard on every
/// array (`capabilities`, `metrics`, `qualitySignals`, `rawTelemetry`,
/// `events`, and each capability's own `history`).
type ParsedItem<'a> =
    | ValidItem of 'a
    | MalformedItem

/// One of the 11 identity fields' exact three-way JS shape: `undefined`
/// (absent, fine), `null` (present, fine), a `string` (fine), or anything
/// else (a finding) -- production's `value !== null && value !==
/// undefined && typeof value !== "string"`.
type IdentityField =
    | FieldAbsent
    | FieldNull
    | FieldString of string
    | FieldOtherInvalid

/// A source-provenance object (`{type, name, mechanism}`), reused
/// verbatim across capabilities, metrics, quality signals, raw snapshots,
/// and events.
type ParsedSource =
    { Type: string option
      Name: string option
      Mechanism: string option }

type ParsedCapabilityHistoryEntry =
    { Status: string option
      Source: ParsedSource option
      DiscoveredAt: string option
      LastAssessedAt: FieldPresence<string>
      RecordedAt: FieldPresence<string> }

type ParsedCapability =
    { Status: string option
      MetricId: string option
      ProviderField: string option
      Source: ParsedSource option
      DiscoveredAt: string option
      LastAssessedAt: FieldPresence<string>
      RecordedAt: FieldPresence<string>
      History: FieldPresence<ParsedItem<ParsedCapabilityHistoryEntry> list>
      HistoryOmitted: FieldPresence<float> }

/// The two JSON kinds `confidence` may legally take -- a `[0,1]` number
/// or a `low`/`medium`/`high` label -- represented as a plain sum so
/// Domain can decide validity without re-inspecting a raw JSON value.
type ParsedConfidence =
    | ConfidenceNumber of float
    | ConfidenceLabel of string
    | ConfidenceOtherShape

type ParsedMetric =
    { Id: string option
      MeasurementId: string option
      Value: float option
      Quality: string option
      Confidence: FieldPresence<ParsedConfidence>
      Source: ParsedSource option
      CollectedAt: string option
      SchemaVersion: string option
      Scope: string option
      Aggregation: string option
      Unit: string option
      Currency: string option
      SourceType: string option
      PricingSource: string option
      PricingVersion: string option }

type ParsedQualitySignal =
    { Detector: string option
      Source: ParsedSource option }

type ParsedRawSnapshot =
    { SnapshotId: string option
      Adapter: string option
      Source: ParsedSource option
      CollectedAt: string option
      /// `None` when the `payload` key is genuinely absent. `Some
      /// serialized` otherwise -- including `Some "null"` for an explicit
      /// JSON `null` payload, matching production's own
      /// `JSON.stringify(null) === "null"` (defined, four bytes), not a
      /// missing-payload rejection.
      PayloadSerialized: string option
      PayloadSensitiveFindingPaths: string list
      PayloadBytes: FieldPresence<float> }

type ParsedEvent =
    { EventType: string option
      OccurredAt: string option
      Source: ParsedSource option
      RawRetentionStatus: FieldPresence<string> }

type ParsedIdentity =
    { Provider: IdentityField
      Model: IdentityField
      ModelVersion: IdentityField
      Runtime: IdentityField
      RuntimeVersion: IdentityField
      SessionId: IdentityField
      ConversationId: IdentityField
      RunId: IdentityField
      AgentId: IdentityField
      SubagentId: IdentityField
      ParentExecutionId: IdentityField }

type ParsedProvenance =
    { Collector: string option
      CollectorVersion: string option
      DiscoveredAt: string option }

type ParsedClassification =
    { Types: string option list option
      HasResearchQuestion: bool
      HasHypothesis: bool
      HasTechnicalUncertainty: bool
      HasExperimentalObjective: bool
      HasKnowledgeGap: bool
      HasRd: bool }

type ParsedExecutionRecord =
    { Relative: string
      SchemaVersion: string option
      ExecutionId: string option
      WorkItemId: string option
      Identity: ParsedIdentity option
      Provenance: ParsedProvenance option
      StartedAt: string option
      Status: string option
      FinalizedAt: string option
      CapabilitiesIsArray: bool
      MetricsIsArray: bool
      RawTelemetryIsArray: bool
      EventsIsArray: bool
      HasRepository: bool
      HasLinks: bool
      Classification: ParsedClassification option
      Capabilities: ParsedItem<ParsedCapability> list
      Metrics: ParsedItem<ParsedMetric> list
      QualitySignals: ParsedItem<ParsedQualitySignal> list
      RawTelemetry: ParsedItem<ParsedRawSnapshot> list
      Events: ParsedItem<ParsedEvent> list }

/// A malformed execution file (unparseable JSON, or a JSON value that
/// isn't an object at all) that never reaches per-field validation --
/// mirrors production's own per-file `try`/`catch` around `readJson`.
type UnreadableExecutionFile =
    { Relative: string
      Message: string }

// ---- Config / registry state machine ----

type RawTelemetryConfig =
    { Enabled: bool
      DisabledReason: string option
      RequireFinalization: bool
      ExecutionRoot: string
      MetricRegistryPath: string
      MaxRawPayloadBytes: int
      MaxRawSnapshotsPerExecution: int
      MaxRawBytesPerExecution: int
      MaxCapabilityHistoryEntries: int }

type RawRegistryMetric =
    { Id: string option
      Unit: string option
      Aggregation: string option
      Collection: string option }

/// Mirrors production `loadMetricRegistry`'s three distinct failure
/// shapes, kept closed rather than collapsed into a single "invalid"
/// case, since each produces a different message.
type RawMetricRegistry =
    | RegistryFileMissing
    | RegistryFileUnparseable
    | RegistryFileWrongShape
    | RegistryFileParsed of metrics: RawRegistryMetric list

type TelemetryValidationOutcome =
    | ConfigInvalid of TelemetryFinding
    | TelemetryDisabled of TelemetryFinding option
    | RegistryInvalid of TelemetryFinding
    | ReadyToValidate of
        registry: Map<string, MetricDefinition> *
        requireFinalization: bool *
        maxCapabilityHistoryEntries: int *
        maxRawPayloadBytes: int *
        maxRawSnapshotsPerExecution: int *
        maxRawBytesPerExecution: int
