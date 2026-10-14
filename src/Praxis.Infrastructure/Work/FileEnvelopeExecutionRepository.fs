namespace Praxis.Infrastructure.Work

open System
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Telemetry
open Praxis.Domain.Work
open Praxis.Infrastructure.Json

/// Projects a validated runtime-free execution onto the same canonical
/// execution JSON read by the native telemetry and step query paths. The
/// envelope remains untrusted input; this module is a deterministic renderer
/// and never writes a file by itself.
[<RequireQualifiedAccess>]
module FileEnvelopeExecutionRepository =
    let private options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private stringNode (value: string option) : JsonNode =
        value |> Option.map (fun text -> JsonValue.Create(text) :> JsonNode) |> Option.defaultValue null

    let private actorNode (actor: EnvelopeActor) =
        let node = JsonObject()
        node["kind"] <- JsonValue.Create actor.ActorKind
        node["id"] <- JsonValue.Create actor.ActorId
        node["provider"] <- stringNode actor.Provider
        node["model"] <- stringNode actor.Model
        node["runtime"] <- stringNode actor.Runtime
        node

    let private identityNode (actor: EnvelopeActor) =
        let node = JsonObject()
        node["provider"] <- JsonValue.Create(actor.Provider |> Option.defaultValue "unknown")
        node["model"] <- stringNode actor.Model
        node["modelVersion"] <- null
        node["runtime"] <- JsonValue.Create(actor.Runtime |> Option.defaultValue "unknown")
        node["runtimeVersion"] <- null
        node["sessionId"] <- null
        node["conversationId"] <- null
        node["runId"] <- null
        node["agentId"] <- JsonValue.Create actor.ActorId
        node["subagentId"] <- null
        node["parentExecutionId"] <- null
        node["actorKind"] <- JsonValue.Create actor.ActorKind
        node["orchestration"] <- JsonObject()
        node

    let private sourceNode (transactionId: string) =
        let node = JsonObject()
        node["type"] <- JsonValue.Create "agent-report"
        node["name"] <- JsonValue.Create transactionId
        node["mechanism"] <- JsonValue.Create "runtime-free-reconciliation"
        node

    let private measurementSourceNode (transactionId: string) (availability: string) =
        let node = sourceNode transactionId
        if availability = "calculated" then node["type"] <- JsonValue.Create "calculated"
        node

    let private capabilityStatus (availability: string) =
        match availability with
        | "measured"
        | "reported" -> "supported-observed"
        | "calculated" -> "derived"
        | "estimated" -> "estimated"
        | "unavailable" -> "supported-unavailable"
        | "unsupported" -> "unsupported"
        | _ -> "unknown"

    let private capabilityNode (transactionId: string) (discoveredAt: string) (measurement: EnvelopeStepMeasurement) =
        let node = JsonObject()
        node["metricId"] <- JsonValue.Create measurement.MetricId
        node["status"] <- JsonValue.Create(capabilityStatus measurement.Availability)
        node["reason"] <-
            match measurement.Availability with
            | "unavailable" -> JsonValue.Create "runtime reported no value for this execution step"
            | "unsupported" -> JsonValue.Create "runtime reported this measurement as unsupported"
            | "unknown" -> JsonValue.Create "runtime capability or value was not known"
            | _ -> null
        node["source"] <- measurementSourceNode transactionId measurement.Availability
        node["discoveredAt"] <- JsonValue.Create discoveredAt
        node

    let private metricQuality (availability: string) =
        match availability with
        | "calculated" -> "derived"
        | "estimated" -> "estimated"
        | _ -> "observed"

    let private metricNode (definitions: Map<string, MetricDefinition>) (transactionId: string) (collectedAt: string) (stepId: string) (measurement: EnvelopeStepMeasurement) =
        match measurement.Value with
        | None -> None
        | Some value ->
            let definition = definitions[measurement.MetricId]
            let node = JsonObject()
            node["measurementId"] <- JsonValue.Create measurement.MeasurementId
            node["schemaVersion"] <- JsonValue.Create "1.0.0"
            node["id"] <- JsonValue.Create measurement.MetricId
            node["value"] <- JsonValue.Create value
            node["unit"] <- JsonValue.Create definition.Unit
            node["currency"] <- stringNode measurement.Currency
            node["quality"] <- JsonValue.Create(metricQuality measurement.Availability)
            node["confidence"] <-
                if measurement.Availability <> "estimated" then
                    null
                else
                    try
                        use document = JsonDocument.Parse measurement.RawJson
                        match document.RootElement.TryGetProperty "confidence" with
                        | true, confidence -> JsonNode.Parse(confidence.GetRawText())
                        | _ -> null
                    with _ -> null
            node["scope"] <- JsonValue.Create "step"
            node["stepId"] <- JsonValue.Create stepId
            node["aggregation"] <- JsonValue.Create definition.Aggregation
            node["dimensions"] <- JsonObject()
            node["source"] <- measurementSourceNode transactionId measurement.Availability
            node["collectedAt"] <- JsonValue.Create collectedAt
            try
                use document = JsonDocument.Parse measurement.RawJson
                match document.RootElement.TryGetProperty "pricing" with
                | true, pricing when pricing.ValueKind = JsonValueKind.Object -> node["pricing"] <- JsonNode.Parse(pricing.GetRawText())
                | _ -> ()
            with _ -> ()
            Some node

    let private rawSnapshotId (executionId: string) (stepId: string) (index: int) (raw: string) =
        "SNAP-" + CanonicalJson.sha256HexPrefix 24 $"{executionId}|{stepId}|{index}|{raw}"

    let private rawSnapshotNode (transactionId: string) (executionId: string) (step: EnvelopeStep) (index: int) (raw: string) =
        let id = rawSnapshotId executionId step.StepId index raw
        let parsed =
            try JsonNode.Parse raw
            with _ -> JsonValue.Create raw
        let redactions = ResizeArray<string>()
        let payload = RawTelemetrySanitizer.sanitizeInto parsed "$" redactions
        let serialized = payload.ToJsonString()
        let node = JsonObject()
        node["snapshotId"] <- JsonValue.Create id
        node["adapter"] <- JsonValue.Create "fallback-envelope"
        node["schemaVersion"] <- null
        let collectedAt = (step.EndedAt |> Option.orElse step.StartedAt |> Option.defaultValue step.PlannedAt).ToString("O")
        node["collectedAt"] <- JsonValue.Create collectedAt
        node["stepId"] <- JsonValue.Create step.StepId
        node["source"] <- sourceNode transactionId
        node["payload"] <- payload
        node["payloadBytes"] <- JsonValue.Create(Encoding.UTF8.GetByteCount serialized)
        let discoveredFields = JsonArray()
        RawTelemetrySanitizer.leafPaths payload |> List.iter (fun value -> discoveredFields.Add(JsonValue.Create value: JsonNode))
        node["discoveredFields"] <- discoveredFields
        let redactionPaths = JsonArray()
        redactions |> Seq.iter (fun value -> redactionPaths.Add(JsonValue.Create value: JsonNode))
        node["redactions"] <- redactionPaths
        id, node

    let private stringArray (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private relationshipsNode (evidence: string list) =
        let node = JsonObject()
        for name in [ "filesObserved"; "filesChanged"; "commits"; "commands"; "tests"; "validations"; "decisions"; "requirements"; "outcomes" ] do
            node[name] <- JsonArray()
        node["evidence"] <- stringArray evidence
        node

    let private transitionNode (step: EnvelopeStep) (transition: EnvelopeStepTransition) =
        let node = JsonObject()
        node["from"] <- stringNode transition.FromStatus
        node["to"] <- JsonValue.Create transition.ToStatus
        node["occurredAt"] <- JsonValue.Create(transition.Timestamp.ToString("O"))
        node["reason"] <- null
        node["actor"] <- actorNode step.Actor
        node

    let private stepNode (transactionId: string) (instanceId: string option) (executionId: string) (siblingSequence: int) (step: EnvelopeStep) =
        // Preserve both explicit raw snapshots and every complete measurement
        // object. The latter keeps provider-specific/pricing fields that are
        // intentionally outside the normalized model.
        let rawInputs = step.RawTelemetry @ (step.Measurements |> List.map _.RawJson)
        let raw = rawInputs |> List.mapi (rawSnapshotNode transactionId executionId step)
        let measurements = step.Measurements |> List.map _.MeasurementId
        let capabilities = JsonArray()
        step.Measurements
        |> List.iter (fun measurement -> capabilities.Add(capabilityNode transactionId (step.PlannedAt.ToString("O")) measurement: JsonNode))

        let telemetry = JsonObject()
        telemetry["measurementIds"] <- stringArray measurements
        telemetry["rawSnapshotIds"] <- stringArray (raw |> List.map fst)
        telemetry["checkpoints"] <- JsonArray()
        telemetry["deltas"] <- JsonArray()
        telemetry["capabilities"] <- capabilities

        let transitions = JsonArray()
        step.Transitions |> List.iter (fun transition -> transitions.Add(transitionNode step transition: JsonNode))

        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["stepId"] <- JsonValue.Create step.StepId
        node["executionId"] <- JsonValue.Create executionId
        node["workItemId"] <- JsonValue.Create step.WorkItemId
        node["instanceId"] <- stringNode instanceId
        node["sequence"] <- JsonValue.Create step.Sequence
        node["siblingSequence"] <- JsonValue.Create siblingSequence
        node["parentStepId"] <- stringNode step.ParentStepId
        node["name"] <- JsonValue.Create step.Name
        node["description"] <- stringNode step.Description
        node["classification"] <- stringArray step.Classifications
        node["status"] <- JsonValue.Create step.Status
        node["plannedAt"] <- JsonValue.Create(step.PlannedAt.ToString("O"))
        node["startedAt"] <- stringNode (step.StartedAt |> Option.map _.ToString("O"))
        node["completedAt"] <- stringNode (step.CompletedAt |> Option.map _.ToString("O"))
        node["endedAt"] <- stringNode (step.EndedAt |> Option.map _.ToString("O"))
        node["updatedAt"] <- JsonValue.Create((step.EndedAt |> Option.orElse step.StartedAt |> Option.defaultValue step.PlannedAt).ToString("O"))
        node["actor"] <- actorNode step.Actor
        node["identity"] <- identityNode step.Actor
        node["telemetry"] <- telemetry
        node["relationships"] <- relationshipsNode step.Evidence
        node["transitions"] <- transitions
        node, raw |> List.map snd

    let render (metricDefinitions: MetricDefinition list) (repositoryName: string) (headCommit: string) (envelope: EnvelopeReconciliationInput) (execution: EnvelopeExecution) =
        let definitions = metricDefinitions |> List.map (fun definition -> definition.Id, definition) |> Map.ofList
        let siblingSequence step =
            execution.Steps
            |> List.filter (fun candidate -> candidate.Sequence <= step.Sequence && candidate.ParentStepId = step.ParentStepId)
            |> List.length

        let renderedSteps = execution.Steps |> List.map (fun step -> stepNode envelope.TransactionId envelope.PraxisInstanceId execution.ExecutionId (siblingSequence step) step)
        let stepArray = JsonArray()
        renderedSteps |> List.iter (fun (step, _) -> stepArray.Add(step: JsonNode))

        let rawArray = JsonArray()
        renderedSteps |> List.collect snd |> List.iter (fun raw -> rawArray.Add(raw: JsonNode))

        let metrics = JsonArray()
        for step in execution.Steps do
            let collectedAt = (step.EndedAt |> Option.orElse step.StartedAt |> Option.defaultValue step.PlannedAt).ToString("O")
            for measurement in step.Measurements do
                metricNode definitions envelope.TransactionId collectedAt step.StepId measurement
                |> Option.iter (fun node -> metrics.Add(node: JsonNode))

        let capabilities = JsonArray()
        execution.Steps
        |> List.collect (fun step -> step.Measurements |> List.map (fun measurement -> step, measurement))
        |> List.groupBy (fun (_, measurement) -> measurement.MetricId)
        |> List.sortBy fst
        |> List.iter (fun (_, observations) ->
            let step, measurement =
                observations
                |> List.sortBy (fun (step, _) -> step.Sequence)
                |> List.tryFind (fun (_, value) -> value.Value.IsSome)
                |> Option.defaultValue observations.Head
            capabilities.Add(capabilityNode envelope.TransactionId (step.PlannedAt.ToString("O")) measurement: JsonNode))

        let terminal = execution.Steps |> List.forall (fun step -> step.Status = "completed" || step.Status = "abandoned")
        let finalizedAt =
            if terminal && not execution.Steps.IsEmpty then
                execution.Steps
                |> List.choose (fun step -> step.EndedAt |> Option.orElse step.CompletedAt)
                |> List.sort
                |> List.tryLast
            else None

        let workType =
            envelope.Requests
            |> List.tryPick (fun request -> request.WorkType)
            |> Option.defaultValue "task"
        let classifications = [ WorkClassification.defaultFor workType ]

        let provenance = JsonObject()
        provenance["collector"] <- JsonValue.Create "ros"
        provenance["collectorVersion"] <- JsonValue.Create "1.0.0"
        provenance["discoveredAt"] <- JsonValue.Create(execution.StartedAt.ToString("O"))
        let sources = JsonArray()
        sources.Add(sourceNode envelope.TransactionId: JsonNode)
        provenance["sources"] <- sources

        let classification = JsonObject()
        classification["types"] <- stringArray classifications
        classification["rationale"] <- JsonValue.Create "agent-authored runtime-free execution plan"
        classification["evidence"] <- JsonArray()
        classification["rd"] <- null

        let start = JsonObject()
        start["available"] <- JsonValue.Create true
        start["repository"] <- JsonValue.Create repositoryName
        start["branch"] <- JsonValue.Create envelope.Branch
        start["commit"] <- JsonValue.Create envelope.BaseCommit
        start["dirty"] <- null
        start["dirtyPaths"] <- JsonArray()
        let finish = JsonObject()
        finish["available"] <- JsonValue.Create true
        finish["repository"] <- JsonValue.Create repositoryName
        finish["branch"] <- JsonValue.Create envelope.Branch
        finish["commit"] <- JsonValue.Create headCommit
        finish["dirty"] <- null
        finish["dirtyPaths"] <- JsonArray()
        let changeSummary = JsonObject()
        changeSummary["available"] <- JsonValue.Create false
        changeSummary["reason"] <- JsonValue.Create "fallback envelope did not observe repository dirty state; no file-change attribution was inferred"
        let repository = JsonObject()
        repository["start"] <- start
        repository["end"] <- finish
        repository["changeSummary"] <- changeSummary

        let evidence = execution.Steps |> List.collect _.Evidence |> List.distinct
        let links = JsonObject()
        links["workItemId"] <- JsonValue.Create envelope.WorkItem
        links["parentWorkItemId"] <- null
        for name in [ "requirements"; "acceptanceCriteria"; "pullRequests"; "experiments"; "researchQuestions"; "decisions"; "defects"; "dependencies" ] do
            links[name] <- JsonArray()
        links["commits"] <- stringArray ([ envelope.BaseCommit; headCommit ] |> List.distinct)
        links["evidence"] <- stringArray evidence

        let events = JsonArray()
        envelope.Timeline
        |> List.iter (fun item ->
            let node = JsonObject()
            node["type"] <- JsonValue.Create item.Action
            node["occurredAt"] <- JsonValue.Create(item.Timestamp.ToString("O"))
            node["transactionId"] <- JsonValue.Create envelope.TransactionId
            node["source"] <- sourceNode envelope.TransactionId
            events.Add(node: JsonNode))

        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create "1.0.0"
        root["executionId"] <- JsonValue.Create execution.ExecutionId
        root["workItemId"] <- JsonValue.Create envelope.WorkItem
        root["instanceId"] <- stringNode envelope.PraxisInstanceId
        root["status"] <- JsonValue.Create(if finalizedAt.IsSome then "finalized" else "active")
        root["startedAt"] <- JsonValue.Create(execution.StartedAt.ToString("O"))
        root["finalizedAt"] <- stringNode (finalizedAt |> Option.map _.ToString("O"))
        root["identity"] <- identityNode envelope.Agent
        root["provenance"] <- provenance
        root["classification"] <- classification
        root["capabilities"] <- capabilities
        root["metrics"] <- metrics
        root["rawTelemetry"] <- rawArray
        root["events"] <- events
        root["repository"] <- repository
        root["links"] <- links
        root["steps"] <- stepArray
        root.ToJsonString(options) + "\n"
