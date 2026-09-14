namespace Ros.ProjectAdministration

open System

/// One durable outbound delivery attempt -- the "outbox" pattern applied
/// to Central's own storage instead of a separate message bus, per
/// docs/migrations/central-integration/MIGRATION-PLAN.md Phase 5. Every
/// state change here goes through `OutboundDeliveryState.transition`, so
/// the legal-transition rule lives in exactly one place.
type OutboundIntegration =
    { Id: string
      Target: IntegrationTarget
      SourceRecordId: string
      ContractVersion: string
      Status: OutboundDeliveryState
      AttemptCount: int
      CreatedAt: DateTimeOffset
      LastAttemptAt: DateTimeOffset option
      CompletedAt: DateTimeOffset option
      LastError: string option }

[<RequireQualifiedAccess>]
module OutboundIntegration =
    /// Creates a new outbox entry in its initial state: `NotReady` when
    /// no adapter for `target` exists yet (MIGRATION-PLAN.md Phase 8),
    /// `Pending` once one does.
    let create (id: string) (target: IntegrationTarget) (sourceRecordId: string) (contractVersion: string) (adapterReady: bool) (now: DateTimeOffset) : OutboundIntegration =
        { Id = id
          Target = target
          SourceRecordId = sourceRecordId
          ContractVersion = contractVersion
          Status = (if adapterReady then Pending else NotReady)
          AttemptCount = 0
          CreatedAt = now
          LastAttemptAt = None
          CompletedAt = None
          LastError = None }

    /// Marks a `NotReady` entry `Pending` once its adapter becomes
    /// available -- see MIGRATION-PLAN.md Phase 9's "Chrona delivery
    /// state NotReady until the adapter is installed."
    let markReady (entry: OutboundIntegration) : Result<OutboundIntegration, string> =
        match OutboundDeliveryState.transition entry.Status Pending with
        | Error message -> Error message
        | Ok pending -> Ok { entry with Status = pending }

    /// Marks one send attempt starting, from either `Pending` or a
    /// prior `Failed` (the retry loop).
    let beginSending (now: DateTimeOffset) (entry: OutboundIntegration) : Result<OutboundIntegration, string> =
        match OutboundDeliveryState.transition entry.Status Sending with
        | Error message -> Error message
        | Ok sending ->
            Ok
                { entry with
                    Status = sending
                    AttemptCount = entry.AttemptCount + 1
                    LastAttemptAt = Some now }

    let markDelivered (now: DateTimeOffset) (entry: OutboundIntegration) : Result<OutboundIntegration, string> =
        match OutboundDeliveryState.transition entry.Status Delivered with
        | Error message -> Error message
        | Ok delivered ->
            Ok
                { entry with
                    Status = delivered
                    CompletedAt = Some now
                    LastError = None }

    let markFailed (error: string) (entry: OutboundIntegration) : Result<OutboundIntegration, string> =
        match OutboundDeliveryState.transition entry.Status (Failed(entry.AttemptCount, error)) with
        | Error message -> Error message
        | Ok failed -> Ok { entry with Status = failed; LastError = Some error }
