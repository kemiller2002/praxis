namespace Ros.Contracts.Work

open Ros.Contracts
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkPlanContract =
    let internal writeEvidence (writer: System.Text.Json.Utf8JsonWriter) evidence =
        writer.WriteStartObject()
        writer.WriteString("type", evidence.Type)
        writer.WriteString("path", evidence.Path)
        writer.WriteEndObject()

    let internal writeOptional (writer: System.Text.Json.Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull name

    let internal writeIntent (writer: System.Text.Json.Utf8JsonWriter) intent =
        writer.WriteStartObject()

        match intent with
        | TelemetryIntent.EnsureActiveExecution -> writer.WriteString("kind", "ensure-active-execution")
        | TelemetryIntent.EnsureCompletableExecution -> writer.WriteString("kind", "ensure-completable-execution")
        | TelemetryIntent.RecordBlocked reason ->
            writer.WriteString("kind", "record-blocked")
            writer.WriteString("reason", reason)
        | TelemetryIntent.RecordResumed -> writer.WriteString("kind", "record-resumed")
        | TelemetryIntent.FinalizeExecutions -> writer.WriteString("kind", "finalize-executions")

        writer.WriteEndObject()

    let internal writeItem (writer: System.Text.Json.Utf8JsonWriter) item =
        writer.WriteStartObject()
        writer.WriteString("id", item.Id)
        writer.WriteString("type", item.WorkType)
        writer.WriteString("state", item.LocalState)
        writer.WriteString("semanticState", WorkDecisionContract.stateName item.SemanticState)
        writer.WriteStartArray("evidence")
        item.Evidence |> List.iter (writeEvidence writer)
        writer.WriteEndArray()
        writeOptional writer "blockReason" item.BlockReason
        writeOptional writer "updatedAt" item.UpdatedAt
        writeOptional writer "completedAt" item.CompletedAt
        writer.WriteStartArray("telemetryExecutionIds")
        item.TelemetryExecutionIds |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteEndObject()

    let internal writeEvent (writer: System.Text.Json.Utf8JsonWriter) event =
        writer.WriteStartObject()
        writer.WriteString("type", event.EventType)
        writer.WriteString("workItem", event.WorkItemId)
        writer.WriteString("repository", event.Repository)
        writer.WriteString("protocolVersion", event.ProtocolVersion)
        writer.WriteString("occurredAt", event.OccurredAt)
        writeOptional writer "reason" event.Reason
        writer.WriteStartArray("evidence")
        event.Evidence |> List.iter (writeEvidence writer)
        writer.WriteEndArray()
        writer.WriteStartArray("paths")
        event.Paths |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteStartArray("telemetryExecutions")
        event.TelemetryExecutionIds |> List.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteEndObject()

    let renderJson outcome =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match outcome with
            | WorkPlanOutcome.Rejected rejection ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")
                WorkDecisionContract.writeRejection writer rejection
                writer.WriteEndObject()
            | WorkPlanOutcome.Planned plan ->
                writer.WriteString("outcome", "planned")
                writer.WriteStartObject("plan")
                writer.WritePropertyName("item")
                writeItem writer plan.Item
                writer.WritePropertyName("event")
                writeEvent writer plan.Event
                writer.WriteStartArray("telemetry")
                plan.Telemetry |> List.iter (writeIntent writer)
                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteNull("rejection")

            writer.WriteEndObject())

    let renderVerifiedJson outcome =
        match outcome with
        | VerifiedWorkPlanOutcome.Planned plan -> renderJson (WorkPlanOutcome.Planned plan)
        | VerifiedWorkPlanOutcome.TransitionRejected rejection -> renderJson (WorkPlanOutcome.Rejected rejection)
        | VerifiedWorkPlanOutcome.EvidenceRejected issues ->
            JsonRendering.renderIndented (fun writer ->
                writer.WriteStartObject()
                writer.WriteString("schemaVersion", "1.0.0")
                writer.WriteString("outcome", "evidence-rejected")
                writer.WriteNull("plan")
                writer.WriteStartObject("rejection")
                writer.WriteString("reason", "evidence-path-rejected")
                writer.WriteStartArray("evidenceIssues")

                for issue in issues do
                    writer.WriteStartObject()

                    match issue with
                    | EvidenceIssue.Missing evidence ->
                        writer.WriteString("outcome", "missing")
                        writer.WriteStartObject("evidence")
                        writer.WriteString("type", evidence.Type)
                        writer.WriteString("path", evidence.Path)
                        writer.WriteEndObject()
                        writer.WriteNull("message")
                    | EvidenceIssue.Unavailable(evidence, message) ->
                        writer.WriteString("outcome", "unavailable")
                        writer.WriteStartObject("evidence")
                        writer.WriteString("type", evidence.Type)
                        writer.WriteString("path", evidence.Path)
                        writer.WriteEndObject()
                        writer.WriteString("message", message)

                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteEndObject())

    let internal writeResolutionRejection (writer: System.Text.Json.Utf8JsonWriter) rejection =
        match rejection with
        | TelemetryResolutionRejection.DetachedConflict executionIds ->
            writer.WriteString("reason", "detached-conflict")
            writer.WriteStartArray("executionIds")
            executionIds |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
        | TelemetryResolutionRejection.Ambiguous executionIds ->
            writer.WriteString("reason", "ambiguous")
            writer.WriteStartArray("executionIds")
            executionIds |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

    let renderResolvedTelemetryJson outcome =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "1.0.0")

            match outcome with
            | ResolvedTelemetryOutcome.Resolved plan ->
                writer.WriteString("outcome", "resolved")
                writer.WriteStartObject("plan")
                writer.WritePropertyName("item")
                writeItem writer plan.Item
                writer.WritePropertyName("event")
                writeEvent writer plan.Event
                writer.WriteEndObject()
                writer.WriteNull("workItem")
                writer.WriteNull("rejection")
            | ResolvedTelemetryOutcome.PendingNewExecution workItemId ->
                writer.WriteString("outcome", "pending-new-execution")
                writer.WriteNull("plan")
                writer.WriteString("workItem", workItemId)
                writer.WriteNull("rejection")
            | ResolvedTelemetryOutcome.Rejected(workItemId, rejection) ->
                writer.WriteString("outcome", "rejected")
                writer.WriteNull("plan")
                writer.WriteString("workItem", workItemId)
                writer.WriteStartObject("rejection")
                writeResolutionRejection writer rejection
                writer.WriteEndObject()

            writer.WriteEndObject())
