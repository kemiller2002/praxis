namespace Ros.Infrastructure.Work

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Json

type StepCreateRequest =
    { ExecutionId: string option
      Name: string
      Description: string option
      Classifications: string list
      ParentStepId: string option
      Status: StepStatus
      OccurredAt: string
      Actor: Actor
      Identity: Identity }

type StepTransitionRequest =
    { ExecutionId: string option
      StepId: string option
      Target: StepStatus
      Reason: string option
      OccurredAt: string
      Actor: Actor
      Identity: Identity }

type StepMetricRequest =
    { ExecutionId: string option
      StepId: string option
      MetricId: string
      Value: float
      Unit: string option
      Currency: string option
      Quality: string
      Confidence: JsonNode
      CollectedAt: string
      Source: CapabilitySource
      PricingSource: string option
      PricingVersion: string option
      PricingEffectiveAt: string option
      CalculationMethod: string option
      Model: string option
      TokenMeasurementIds: string list
      Actor: Actor
      Identity: Identity }

type StepAvailabilityRequest =
    { ExecutionId: string option
      StepId: string option
      MetricId: string
      Status: string
      Reason: string
      OccurredAt: string
      Actor: Actor
      Identity: Identity }

type StepCheckpointRequest =
    { ExecutionId: string option
      StepId: string option
      Phase: string
      MeasurementIds: string list
      SnapshotIds: string list
      OccurredAt: string
      Actor: Actor
      Identity: Identity }

type StepLinkRequest =
    { ExecutionId: string option
      StepId: string option
      Kind: string
      Value: string
      Source: string
      OccurredAt: string
      Actor: Actor
      Identity: Identity }

[<RequireQualifiedAccess>]
module FileStepRepository =
    let private serializerOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private compactOptions =
        JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private intField (node: JsonObject) (name: string) : int option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<int>() with
            | true, number -> Some number
            | _ -> None
        | _ -> None

    let private doubleField (node: JsonObject) (name: string) : float option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<float>() with
            | true, number -> Some number
            | _ -> None
        | _ -> None

    let private objectField (node: JsonObject) (name: string) : JsonObject option =
        match node[name] with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private arrayField (node: JsonObject) (name: string) : JsonArray =
        match node[name] with
        | :? JsonArray as value -> value
        | _ ->
            let value = JsonArray()
            node[name] <- value
            value

    let private stringArray (values: string list) =
        let result = JsonArray()
        values |> List.iter (fun value -> result.Add(JsonValue.Create value: JsonNode))
        result

    let private optionalString (value: string option) : JsonNode =
        match value with
        | Some text -> JsonValue.Create text
        | None -> null

    let private executionFile (root: string) (executionId: string) =
        Path.Combine(root, ".ros", "telemetry", "executions", $"{executionId}.json")

    let private identityOf (node: JsonObject) : Identity =
        { Provider = stringField node "provider" |> Option.defaultValue Actor.UnknownValue
          Model = stringField node "model"
          ModelVersion = stringField node "modelVersion"
          Runtime = stringField node "runtime" |> Option.defaultValue Actor.UnknownValue
          RuntimeVersion = stringField node "runtimeVersion"
          SessionId = stringField node "sessionId"
          ConversationId = stringField node "conversationId"
          RunId = stringField node "runId"
          AgentId = stringField node "agentId"
          SubagentId = stringField node "subagentId"
          ParentExecutionId = stringField node "parentExecutionId" }

    let private discoveryMechanism (record: JsonObject) : string option =
        objectField record "provenance"
        |> Option.bind (fun provenance ->
            match provenance["sources"] with
            | :? JsonArray as sources ->
                sources
                |> Seq.tryPick (function
                    | :? JsonObject as source when stringField source "name" = Some "runtime-identity" -> stringField source "mechanism"
                    | _ -> None)
            | _ -> None)

    let private actorAndIdentityOf (record: JsonObject) : (Actor * Identity) option =
        objectField record "identity"
        |> Option.map (fun node ->
            let identity = identityOf node
            let actor = ActorResolution.ofExecutionRecord (stringField node "actorKind") (discoveryMechanism record) identity
            actor, identity)

    let private ownedBy (actor: Actor) (identity: Identity) (record: JsonObject) =
        match actorAndIdentityOf record with
        | Some(storedActor, storedIdentity) ->
            ActorResolution.isDeclared actor
            && Actor.agrees actor storedActor
            && ActorResolution.evidentlySameRun actor identity storedActor storedIdentity
        | None -> false

    let private resolveExecution (root: string) (requested: string option) (actor: Actor) (identity: Identity) : Result<JsonObject, string> =
        let active =
            FileTelemetryQueryRepository.readAll root
            |> List.filter (fun record -> stringField record "status" = Some "active")

        match requested with
        | Some executionId ->
            match active |> List.tryFind (fun record -> stringField record "executionId" = Some executionId) with
            | None -> Error $"active telemetry execution '{executionId}' was not found"
            | Some record when not (ownedBy actor identity record) -> Error $"current actor/session does not own execution '{executionId}'"
            | Some record -> Ok record
        | None ->
            let owned = active |> List.filter (ownedBy actor identity)

            match owned with
            | [ record ] -> Ok record
            | [] -> Error "no active execution belongs to the current actor/session; pass --execution after establishing the correct identity"
            | records ->
                let ids = records |> List.choose (fun record -> stringField record "executionId") |> String.concat ", "
                Error $"multiple active executions belong to the current actor/session ({ids}); pass --execution"

    let private parseStepState (node: JsonObject) : StepState option =
        match stringField node "stepId", intField node "sequence", stringField node "status" |> Option.bind StepStatus.tryParse, stringField node "plannedAt" with
        | Some stepId, Some sequence, Some status, Some plannedAt ->
            Some
                { StepId = stepId
                  Sequence = sequence
                  ParentStepId = stringField node "parentStepId"
                  Status = status
                  PlannedAt = plannedAt
                  StartedAt = stringField node "startedAt"
                  CompletedAt = stringField node "completedAt"
                  EndedAt = stringField node "endedAt" }
        | _ -> None

    let private stepNodes (record: JsonObject) : JsonObject list =
        match record["steps"] with
        | :? JsonArray as steps -> steps |> Seq.choose (function :? JsonObject as step -> Some step | _ -> None) |> Seq.toList
        | _ -> []

    let private states (record: JsonObject) : StepState list = stepNodes record |> List.choose parseStepState

    let unresolvedStepIds (record: JsonObject) =
        states record |> Step.unresolved |> List.map _.StepId

    let private ensureSteps (record: JsonObject) = arrayField record "steps"

    let private hasArrayValues (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values.Count > 0
        | _ -> false

    let private hasDirectTelemetry (step: JsonObject) =
        match objectField step "telemetry" with
        | Some telemetry ->
            hasArrayValues telemetry "measurementIds"
            || hasArrayValues telemetry "checkpoints"
            || hasArrayValues telemetry "deltas"
        | None -> false

    let private identityNode (identity: Identity) =
        let node = JsonObject()
        node["provider"] <- JsonValue.Create identity.Provider
        node["model"] <- optionalString identity.Model
        node["modelVersion"] <- optionalString identity.ModelVersion
        node["runtime"] <- JsonValue.Create identity.Runtime
        node["runtimeVersion"] <- optionalString identity.RuntimeVersion
        node["sessionId"] <- optionalString identity.SessionId
        node["conversationId"] <- optionalString identity.ConversationId
        node["runId"] <- optionalString identity.RunId
        node["agentId"] <- optionalString identity.AgentId
        node["subagentId"] <- optionalString identity.SubagentId
        node["parentExecutionId"] <- optionalString identity.ParentExecutionId
        node

    let private newStepId (occurredAt: string) =
        let timestamp =
            match DateTimeOffset.TryParse occurredAt with
            | true, parsed -> parsed.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            | _ -> DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)

        let suffix = RandomNumberGenerator.GetBytes 4 |> Convert.ToHexString |> _.ToLowerInvariant()
        $"STEP-{timestamp}-{suffix}"

    let private appendExecutionEvent (record: JsonObject) (eventType: string) (occurredAt: string) (stepId: string) (actor: Actor) =
        let event = JsonObject()
        event["type"] <- JsonValue.Create eventType
        event["occurredAt"] <- JsonValue.Create occurredAt
        event["stepId"] <- JsonValue.Create stepId
        event["actor"] <- ActorJson.node actor
        let source: CapabilitySource = { Type = "ros-cli"; Name = "step"; Mechanism = "step-lifecycle" }
        event["source"] <- FileTelemetryExecutionRepository.sourceNode source
        arrayField record "events" |> _.Add(event: JsonNode)

    let private transitionNode (fromStatus: string option) (toStatus: string) (occurredAt: string) (reason: string option) (actor: Actor) =
        let node = JsonObject()
        node["from"] <- optionalString fromStatus
        node["to"] <- JsonValue.Create toStatus
        node["occurredAt"] <- JsonValue.Create occurredAt
        node["reason"] <- optionalString reason
        node["actor"] <- ActorJson.node actor
        node

    let private telemetryNodeFromExecution (record: JsonObject) =
        let node = JsonObject()
        node["measurementIds"] <- JsonArray()
        node["rawSnapshotIds"] <- JsonArray()
        node["checkpoints"] <- JsonArray()
        node["deltas"] <- JsonArray()
        let capabilities = JsonArray()

        match record["capabilities"] with
        | :? JsonArray as existing ->
            existing
            |> Seq.iter (function
                | :? JsonObject as capability ->
                    match stringField capability "metricId" with
                    | Some metricId when metricId.StartsWith("tokens.") || metricId.StartsWith("session.tokens.") || metricId.StartsWith("cost.") ->
                        capabilities.Add(capability.DeepClone())
                    | _ -> ()
                | _ -> ())
        | _ -> ()

        node["capabilities"] <- capabilities
        node

    let private relationshipsNode () =
        let node = JsonObject()

        [ "filesObserved"; "filesChanged"; "commits"; "commands"; "tests"; "validations"; "evidence"; "decisions"; "requirements"; "outcomes" ]
        |> List.iter (fun name -> node[name] <- JsonArray())

        node

    let private withExecutionLock (root: string) (executionId: string) (action: JsonObject -> string -> Result<JsonObject, string>) =
        match RegistryLock.acquire root $"telemetry-execution:{executionId}" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    let file = executionFile root executionId

                    match JsonNode.Parse(File.ReadAllText file) with
                    | :? JsonObject as record when stringField record "status" = Some "active" -> action record file
                    | :? JsonObject -> Error $"telemetry execution '{executionId}' is already finalized"
                    | _ -> Error "execution record must be a JSON object"
                with error -> Error error.Message

            match lease.Release(), result with
            | Error releaseFailure, Ok _ -> Error releaseFailure.Message
            | _, outcome -> outcome

    let private save (file: string) (record: JsonObject) =
        File.WriteAllText(file, record.ToJsonString serializerOptions + "\n")

    let create (root: string) (request: StepCreateRequest) : Result<JsonObject, string> =
        if String.IsNullOrWhiteSpace request.Name then
            Error "step name must not be empty"
        elif request.Classifications |> List.exists String.IsNullOrWhiteSpace then
            Error "step classifications must not be empty"
        elif request.Status <> StepStatus.Planned && request.Status <> StepStatus.Active then
            Error "a new step must be planned or active"
        else
            match resolveExecution root request.ExecutionId request.Actor request.Identity with
            | Error message -> Error message
            | Ok selected ->
                let executionId = stringField selected "executionId" |> Option.get

                withExecutionLock root executionId (fun record file ->
                    let existing = states record
                    let startedAt = stringField record "startedAt" |> Option.defaultValue request.OccurredAt

                    let parentAlreadyMeasured =
                        request.ParentStepId
                        |> Option.bind (fun parentId -> stepNodes record |> List.tryFind (fun step -> stringField step "stepId" = Some parentId))
                        |> Option.exists hasDirectTelemetry

                    if parentAlreadyMeasured then
                        Error "a measured step cannot become a nesting parent; create a separate grouping step before recording leaf telemetry"
                    else
                        match Step.validateNew startedAt request.OccurredAt request.ParentStepId request.Status existing with
                        | Error message -> Error message
                        | Ok() ->
                            let stepId = newStepId request.OccurredAt
                            let sequence = (if existing.IsEmpty then 0 else existing |> List.maxBy _.Sequence |> _.Sequence) + 1
                            let siblingSequence =
                                existing
                                |> List.filter (fun step -> step.ParentStepId = request.ParentStepId)
                                |> List.length
                                |> (+) 1

                            let node = JsonObject()
                            node["schemaVersion"] <- JsonValue.Create "1.0.0"
                            node["stepId"] <- JsonValue.Create stepId
                            node["executionId"] <- JsonValue.Create executionId
                            node["workItemId"] <- (record["workItemId"] |> _.DeepClone())
                            node["instanceId"] <- (match record["instanceId"] with null -> null | value -> value.DeepClone())
                            node["sequence"] <- JsonValue.Create sequence
                            node["siblingSequence"] <- JsonValue.Create siblingSequence
                            node["parentStepId"] <- optionalString request.ParentStepId
                            node["name"] <- JsonValue.Create(request.Name.Trim())
                            node["description"] <- request.Description |> Option.map _.Trim() |> Option.filter (String.IsNullOrWhiteSpace >> not) |> optionalString
                            node["classification"] <- stringArray (request.Classifications |> List.map _.Trim() |> List.distinct)
                            node["status"] <- JsonValue.Create(StepStatus.code request.Status)
                            node["plannedAt"] <- JsonValue.Create request.OccurredAt
                            node["startedAt"] <- if request.Status = StepStatus.Active then JsonValue.Create request.OccurredAt else null
                            node["completedAt"] <- null
                            node["endedAt"] <- null
                            node["updatedAt"] <- JsonValue.Create request.OccurredAt
                            node["actor"] <- ActorJson.node request.Actor
                            node["identity"] <- identityNode request.Identity
                            node["telemetry"] <- telemetryNodeFromExecution record
                            node["relationships"] <- relationshipsNode ()
                            let transitions = JsonArray()
                            transitions.Add(transitionNode None (StepStatus.code request.Status) request.OccurredAt None request.Actor: JsonNode)
                            node["transitions"] <- transitions
                            ensureSteps record |> _.Add(node: JsonNode)
                            appendExecutionEvent record (if request.Status = StepStatus.Active then "step.started" else "step.planned") request.OccurredAt stepId request.Actor
                            save file record
                            Ok node)

    let private findStepNode (stepId: string) (record: JsonObject) : JsonObject option =
        stepNodes record |> List.tryFind (fun step -> stringField step "stepId" = Some stepId)

    let private optionToResult message value =
        match value with
        | Some item -> Ok item
        | None -> Error message

    let private selectMutationStep (requested: string option) (record: JsonObject) : Result<JsonObject, string> =
        match requested with
        | Some stepId ->
            findStepNode stepId record |> optionToResult $"step '{stepId}' was not found in this execution"
        | None ->
            match states record |> Step.activeLeaf with
            | Some leaf -> findStepNode leaf.StepId record |> optionToResult $"step '{leaf.StepId}' was not found"
            | None -> Error "no step is active; pass --id for a planned or blocked step"

    let transition (root: string) (request: StepTransitionRequest) : Result<JsonObject, string> =
        if request.Target = StepStatus.Blocked && request.Reason |> Option.forall String.IsNullOrWhiteSpace then
            Error "blocking a step requires --reason"
        else
            match resolveExecution root request.ExecutionId request.Actor request.Identity with
            | Error message -> Error message
            | Ok selected ->
                let executionId = stringField selected "executionId" |> Option.get

                withExecutionLock root executionId (fun record file ->
                    match selectMutationStep request.StepId record with
                    | Error message -> Error message
                    | Ok node ->
                        match parseStepState node with
                        | None -> Error "stored step is structurally invalid"
                        | Some current ->
                            let all = states record

                            let timestampOrder =
                                match stringField node "updatedAt", DateTimeOffset.TryParse request.OccurredAt with
                                | Some prior, (true, next) ->
                                    match DateTimeOffset.TryParse prior with
                                    | true, previous when next < previous -> Error "transition timestamp must not be before the prior transition"
                                    | true, _ -> Ok()
                                    | _ -> Error "stored step updatedAt is not a valid timestamp"
                                | _, (false, _) -> Error $"transition timestamp '{request.OccurredAt}' is not a valid timestamp"
                                | None, _ -> Ok()

                            match timestampOrder |> Result.bind (fun () -> Step.transition request.OccurredAt request.Target current all) with
                            | Error message -> Error message
                            | Ok() ->
                                let fromStatus = StepStatus.code current.Status
                                let toStatus = StepStatus.code request.Target
                                node["status"] <- JsonValue.Create toStatus
                                node["updatedAt"] <- JsonValue.Create request.OccurredAt
                                // A blocked step resumes the same unit of work. Preserve its
                                // original start boundary; only planned -> active establishes it.
                                if request.Target = StepStatus.Active && current.Status = StepStatus.Planned then node["startedAt"] <- JsonValue.Create request.OccurredAt
                                if request.Target = StepStatus.Completed then node["completedAt"] <- JsonValue.Create request.OccurredAt
                                if StepStatus.isTerminal request.Target then node["endedAt"] <- JsonValue.Create request.OccurredAt
                                if request.Target = StepStatus.Blocked then node["blockedReason"] <- optionalString request.Reason
                                arrayField node "transitions" |> _.Add(transitionNode (Some fromStatus) toStatus request.OccurredAt request.Reason request.Actor: JsonNode)
                                appendExecutionEvent record $"step.{toStatus}" request.OccurredAt current.StepId request.Actor
                                save file record
                                Ok node)

    let private resolveStepForObservation (requested: string option) (record: JsonObject) : Result<JsonObject, string> =
        match requested with
        | Some stepId -> findStepNode stepId record |> optionToResult $"step '{stepId}' was not found in this execution"
        | None -> selectMutationStep None record

    let private ensureLeafMetricTarget (requested: string option) (record: JsonObject) (step: JsonObject) =
        let stepId = stringField step "stepId" |> Option.defaultValue "?"
        let state = parseStepState step
        let hasChildren = states record |> List.exists (fun candidate -> candidate.ParentStepId = Some stepId)

        if hasChildren then
            Error $"step '{stepId}' is a grouping parent; record telemetry on a leaf step"
        else
            match state |> Option.map _.Status, requested with
            | Some StepStatus.Active, _ when states record |> Step.activeLeaf |> Option.map _.StepId = Some stepId -> Ok()
            | Some StepStatus.Active, _ -> Error $"step '{stepId}' is not the active leaf"
            | Some(StepStatus.Completed | StepStatus.Abandoned), Some _ -> Ok()
            | Some(StepStatus.Completed | StepStatus.Abandoned), None -> Error "late telemetry for a terminal step requires explicit --id"
            | Some _, _ -> Error $"step '{stepId}' must be active before telemetry can be recorded"
            | None, _ -> Error "stored step is structurally invalid"

    let private capabilityStatus (quality: string) =
        match quality with
        | "derived" -> "derived"
        | "estimated" -> "estimated"
        | _ -> "supported-observed"

    let private observationNotBeforeStep (field: string) (occurredAt: string) (step: JsonObject) =
        match stringField step "plannedAt", DateTimeOffset.TryParse occurredAt with
        | Some plannedAt, (true, observed) ->
            match DateTimeOffset.TryParse plannedAt with
            | true, planned when observed < planned -> Error $"{field} must not be before step plannedAt"
            | true, _ -> Ok()
            | _ -> Error "stored step plannedAt is not a valid timestamp"
        | _, (false, _) -> Error $"{field} '{occurredAt}' is not a valid timestamp"
        | None, _ -> Error "stored step requires plannedAt"

    let private pricingNode (source: string option) (version: string option) (effectiveAt: string option) (methodName: string option) (model: string option) (tokenIds: string list) : JsonNode =
        match source, version, effectiveAt, methodName, model, tokenIds with
        | None, None, None, None, None, [] -> null
        | _ ->
            let node = JsonObject()
            node["source"] <- optionalString source
            node["version"] <- optionalString version
            node["effectiveAt"] <- optionalString effectiveAt
            node["method"] <- optionalString methodName
            node["model"] <- optionalString model
            node["tokenMeasurementIds"] <- stringArray tokenIds
            node :> JsonNode

    let private metricDigestNode (definition: MetricDefinition) (request: StepMetricRequest) (stepId: string) =
        let node = JsonObject()
        node["aggregation"] <- JsonValue.Create definition.Aggregation
        node["collectedAt"] <- JsonValue.Create request.CollectedAt
        node["confidence"] <- (match request.Confidence with null -> null | value -> value.DeepClone())
        node["currency"] <- optionalString request.Currency
        node["dimensions"] <- JsonObject()
        node["id"] <- JsonValue.Create request.MetricId
        node["measurementId"] <- JsonValue.Create ""
        node["pricing"] <- pricingNode request.PricingSource request.PricingVersion request.PricingEffectiveAt request.CalculationMethod request.Model request.TokenMeasurementIds
        node["quality"] <- JsonValue.Create request.Quality
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["scope"] <- JsonValue.Create "step"
        node["stepId"] <- JsonValue.Create stepId
        node["source"] <- FileTelemetryExecutionRepository.sourceNode request.Source
        node["unit"] <- JsonValue.Create(request.Unit |> Option.defaultValue definition.Unit)
        node["value"] <- JsonValue.Create request.Value
        node

    let private upsertStepCapability (step: JsonObject) (metricId: string) (status: string) (reason: string) (occurredAt: string) (source: CapabilitySource) =
        let telemetry = objectField step "telemetry" |> Option.defaultWith (fun () -> let node = JsonObject() in step["telemetry"] <- node; node)
        let capabilities = arrayField telemetry "capabilities"
        let existing =
            capabilities
            |> Seq.tryPick (function
                | :? JsonObject as item when stringField item "metricId" = Some metricId -> Some item
                | _ -> None)

        let target =
            match existing with
            | Some item -> item
            | None ->
                let item = JsonObject()
                item["metricId"] <- JsonValue.Create metricId
                capabilities.Add(item: JsonNode)
                item

        target["status"] <- JsonValue.Create status
        target["reason"] <- JsonValue.Create reason
        target["discoveredAt"] <- JsonValue.Create occurredAt
        target["lastAssessedAt"] <- JsonValue.Create occurredAt
        target["recordedAt"] <- JsonValue.Create occurredAt
        target["source"] <- FileTelemetryExecutionRepository.sourceNode source

    let private upsertExecutionCapability (record: JsonObject) (metricId: string) (status: string) (occurredAt: string) (source: CapabilitySource) =
        let capabilities = arrayField record "capabilities"
        let existing =
            capabilities
            |> Seq.tryPick (function
                | :? JsonObject as item when stringField item "metricId" = Some metricId -> Some item
                | _ -> None)

        let target =
            match existing with
            | Some item -> item
            | None ->
                let item = JsonObject()
                item["metricId"] <- JsonValue.Create metricId
                item["providerField"] <- null
                item["history"] <- JsonArray()
                capabilities.Add(item: JsonNode)
                item

        target["status"] <- JsonValue.Create status
        target["reason"] <- JsonValue.Create "step-attributed normalized measurement recorded"
        if target["discoveredAt"] = null then target["discoveredAt"] <- JsonValue.Create occurredAt
        target["lastAssessedAt"] <- JsonValue.Create occurredAt
        target["recordedAt"] <- JsonValue.Create occurredAt
        target["source"] <- FileTelemetryExecutionRepository.sourceNode source

    let private appendMetric (root: string) (record: JsonObject) (step: JsonObject) (request: StepMetricRequest) (derivation: JsonNode) : Result<string, string> =
        match FileMetricRegistryRepository.read root |> List.tryFind (fun definition -> definition.Id = request.MetricId) with
        | None -> Error $"unknown normalized metric '{request.MetricId}'; preserve it in raw telemetry until it is registered"
        | Some _ when not (Double.IsFinite request.Value) || request.Value < 0.0 -> Error $"metric '{request.MetricId}' requires a finite non-negative value"
        | Some definition when request.Quality <> "observed" && request.Quality <> "derived" && request.Quality <> "estimated" ->
            Error "metric quality must be observed, derived, or estimated"
        | Some _ when request.MetricId.StartsWith("cost.") && request.Currency.IsNone -> Error "cost metric requires --currency"
        | Some _ when request.MetricId.StartsWith("cost.") && request.Quality = "derived" && request.PricingSource.IsNone ->
            Error "calculated cost requires --pricing-source (use provider-cumulative for a checkpoint delta)"
        | Some _ when request.MetricId.StartsWith("cost.") && request.Quality = "derived" && request.PricingVersion.IsNone ->
            Error "calculated cost requires --pricing-version (use provider-reported for a checkpoint delta)"
        | Some _ when request.Quality = "estimated" && isNull request.Confidence ->
            Error "estimated measurement requires --confidence"
        | Some definition ->
            let stepId = stringField step "stepId" |> Option.get
            let digestNode = metricDigestNode definition request stepId
            let measurementId = "MEAS-" + CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact digestNode)
            let metrics = arrayField record "metrics"
            let exists =
                metrics
                |> Seq.exists (function :? JsonObject as metric -> stringField metric "measurementId" = Some measurementId | _ -> false)

            if not exists then
                digestNode["measurementId"] <- JsonValue.Create measurementId
                if derivation <> null then digestNode["derivation"] <- derivation.DeepClone()
                metrics.Add(digestNode: JsonNode)

            let telemetry = objectField step "telemetry" |> Option.get
            let refs = arrayField telemetry "measurementIds"

            if refs |> Seq.exists (fun value -> value <> null && value.GetValue<string>() = measurementId) |> not then
                refs.Add(JsonValue.Create measurementId: JsonNode)

            let status = capabilityStatus request.Quality
            upsertStepCapability step request.MetricId status "normalized measurement attributed to step" request.CollectedAt request.Source
            upsertExecutionCapability record request.MetricId status request.CollectedAt request.Source
            Ok measurementId

    let recordMetric (root: string) (request: StepMetricRequest) : Result<JsonObject, string> =
        match resolveExecution root request.ExecutionId request.Actor request.Identity with
        | Error message -> Error message
        | Ok selected ->
            let executionId = stringField selected "executionId" |> Option.get

            withExecutionLock root executionId (fun record file ->
                match resolveStepForObservation request.StepId record with
                | Error message -> Error message
                | Ok step ->
                    match observationNotBeforeStep "collectedAt" request.CollectedAt step with
                    | Error message -> Error message
                    | Ok() ->
                        match ensureLeafMetricTarget request.StepId record step with
                        | Error message -> Error message
                        | Ok() ->
                            match appendMetric root record step request null with
                            | Error message -> Error message
                            | Ok measurementId ->
                                appendExecutionEvent record "step.telemetry.recorded" request.CollectedAt (stringField step "stepId" |> Option.get) request.Actor
                                save file record
                                let result = JsonObject()
                                result["step"] <- step.DeepClone()
                                result["measurementId"] <- JsonValue.Create measurementId
                                Ok result)

    let recordAvailability (root: string) (request: StepAvailabilityRequest) : Result<JsonObject, string> =
        let permitted = Set.ofList [ "supported-unavailable"; "unsupported"; "unknown" ]

        if not (permitted.Contains request.Status) then
            Error "availability status must be supported-unavailable, unsupported, or unknown"
        elif String.IsNullOrWhiteSpace request.Reason then
            Error "availability requires --reason"
        elif FileMetricRegistryRepository.read root |> List.exists (fun definition -> definition.Id = request.MetricId) |> not then
            Error $"unknown normalized metric '{request.MetricId}'"
        else
            match resolveExecution root request.ExecutionId request.Actor request.Identity with
            | Error message -> Error message
            | Ok selected ->
                let executionId = stringField selected "executionId" |> Option.get

                withExecutionLock root executionId (fun record file ->
                    match resolveStepForObservation request.StepId record with
                    | Error message -> Error message
                    | Ok step ->
                        match observationNotBeforeStep "availability timestamp" request.OccurredAt step with
                        | Error message -> Error message
                        | Ok() ->
                            let telemetry = objectField step "telemetry" |> Option.get
                            let referencedIds =
                                match telemetry["measurementIds"] with
                                | :? JsonArray as ids -> ids |> Seq.choose (fun item -> if isNull item then None else Some(item.GetValue<string>())) |> Set.ofSeq
                                | _ -> Set.empty
                            let hasMeasurement =
                                match record["metrics"] with
                                | :? JsonArray as metrics ->
                                    metrics
                                    |> Seq.exists (function
                                        | :? JsonObject as metric ->
                                            referencedIds.Contains(stringField metric "measurementId" |> Option.defaultValue "")
                                            && stringField metric "id" = Some request.MetricId
                                        | _ -> false)
                                | _ -> false

                            if hasMeasurement then
                                Error $"step already has a value for '{request.MetricId}'; unavailable, unsupported, or unknown would contradict it"
                            else
                                let source: CapabilitySource = { Type = "agent-report"; Name = "step-availability"; Mechanism = "explicit" }
                                upsertStepCapability step request.MetricId request.Status request.Reason request.OccurredAt source
                                appendExecutionEvent record "step.telemetry.availability" request.OccurredAt (stringField step "stepId" |> Option.get) request.Actor
                                save file record
                                Ok step)

    let private metricById (record: JsonObject) (measurementId: string) : JsonObject option =
        match record["metrics"] with
        | :? JsonArray as metrics ->
            metrics |> Seq.tryPick (function :? JsonObject as metric when stringField metric "measurementId" = Some measurementId -> Some metric | _ -> None)
        | _ -> None

    let private rawById (record: JsonObject) (snapshotId: string) =
        match record["rawTelemetry"] with
        | :? JsonArray as snapshots ->
            snapshots |> Seq.exists (function :? JsonObject as item -> stringField item "snapshotId" = Some snapshotId | _ -> false)
        | _ -> false

    let private cumulativeDeltaMetric =
        Map.ofList
            [ "session.tokens.cumulative", "tokens.total"
              "session.tokens.input_cumulative", "tokens.input"
              "session.tokens.output_cumulative", "tokens.output"
              "session.tokens.cached_input_cumulative", "tokens.cached_input"
              "session.tokens.cached_output_cumulative", "tokens.cached_output"
              "session.tokens.reasoning_cumulative", "tokens.reasoning"
              "cost.session_cumulative", "cost.step_total" ]

    let private stringOrNull (node: JsonObject) (name: string) = stringField node name |> Option.defaultValue ""

    let private compatibleObservations (startMetric: JsonObject) (endMetric: JsonObject) =
        stringOrNull startMetric "id" = stringOrNull endMetric "id"
        && stringOrNull startMetric "unit" = stringOrNull endMetric "unit"
        && stringOrNull startMetric "currency" = stringOrNull endMetric "currency"
        && (match startMetric["source"], endMetric["source"] with
            | (:? JsonObject as left), (:? JsonObject as right) -> left.ToJsonString(compactOptions) = right.ToJsonString(compactOptions)
            | _ -> false)

    let checkpoint (root: string) (request: StepCheckpointRequest) : Result<JsonObject, string> =
        if request.Phase <> "begin" && request.Phase <> "end" then
            Error "checkpoint phase must be begin or end"
        elif request.MeasurementIds.IsEmpty && request.SnapshotIds.IsEmpty then
            Error "checkpoint requires at least one --measurement or --snapshot"
        else
            match resolveExecution root request.ExecutionId request.Actor request.Identity with
            | Error message -> Error message
            | Ok selected ->
                let executionId = stringField selected "executionId" |> Option.get

                withExecutionLock root executionId (fun record file ->
                    match resolveStepForObservation request.StepId record with
                    | Error message -> Error message
                    | Ok step ->
                        let telemetry = objectField step "telemetry" |> Option.get
                        let checkpoints = arrayField telemetry "checkpoints"
                        let beginAt =
                            checkpoints
                            |> Seq.tryPick (function :? JsonObject as item when stringField item "phase" = Some "begin" -> stringField item "occurredAt" | _ -> None)

                        let timing =
                            observationNotBeforeStep "checkpoint timestamp" request.OccurredAt step
                            |> Result.bind (fun () ->
                                match ensureLeafMetricTarget request.StepId record step with
                                | Error message -> Error message
                                | Ok() ->
                                    if request.Phase = "end" then
                                        match beginAt, DateTimeOffset.TryParse request.OccurredAt with
                                        | Some started, (true, ending) ->
                                            match DateTimeOffset.TryParse started with
                                            | true, beginning when ending < beginning -> Error "end checkpoint must not be before begin checkpoint"
                                            | _ -> Ok()
                                        | _ -> Ok()
                                    else Ok())

                        let missingMeasurements = request.MeasurementIds |> List.filter (fun id -> metricById record id |> Option.isNone)
                        let missingSnapshots = request.SnapshotIds |> List.filter (rawById record >> not)

                        match timing with
                        | Error message -> Error message
                        | Ok() when not missingMeasurements.IsEmpty -> Error $"checkpoint measurement '{missingMeasurements.Head}' was not found in this execution"
                        | Ok() when not missingSnapshots.IsEmpty -> Error $"checkpoint snapshot '{missingSnapshots.Head}' was not found in this execution"
                        | Ok() ->
                            let existingPhase =
                                checkpoints
                                |> Seq.tryPick (function :? JsonObject as item when stringField item "phase" = Some request.Phase -> Some item | _ -> None)

                            if existingPhase.IsSome then Error $"step already has a {request.Phase} checkpoint"
                            else
                                let checkpointStepId = stringField step "stepId" |> Option.get
                                let checkpointMeasurements = String.concat "," request.MeasurementIds
                                let checkpointDigestInput = $"{executionId}|{checkpointStepId}|{request.Phase}|{request.OccurredAt}|{checkpointMeasurements}"
                                let checkpointId = "CHK-" + CanonicalJson.sha256HexPrefix 24 checkpointDigestInput
                                let node = JsonObject()
                                node["checkpointId"] <- JsonValue.Create checkpointId
                                node["phase"] <- JsonValue.Create request.Phase
                                node["occurredAt"] <- JsonValue.Create request.OccurredAt
                                node["measurementIds"] <- stringArray request.MeasurementIds
                                node["rawSnapshotIds"] <- stringArray request.SnapshotIds
                                node["actor"] <- ActorJson.node request.Actor
                                node["deltaStatus"] <- JsonValue.Create(if request.Phase = "begin" then "not-applicable" else "insufficient")
                                node["deltaReasons"] <- JsonArray()
                                checkpoints.Add(node: JsonNode)

                                let rawRefs = arrayField telemetry "rawSnapshotIds"
                                request.SnapshotIds
                                |> List.iter (fun id ->
                                    if rawRefs |> Seq.exists (fun value -> value <> null && value.GetValue<string>() = id) |> not then rawRefs.Add(JsonValue.Create id: JsonNode))

                                if request.Phase = "end" then
                                    let beginCheckpoint =
                                        checkpoints
                                        |> Seq.tryPick (function :? JsonObject as item when stringField item "phase" = Some "begin" -> Some item | _ -> None)

                                    let beginIds =
                                        beginCheckpoint
                                        |> Option.map (fun item ->
                                            match item["measurementIds"] with
                                            | :? JsonArray as ids -> ids |> Seq.choose (fun id -> if id = null then None else Some(id.GetValue<string>())) |> Seq.toList
                                            | _ -> [])
                                        |> Option.defaultValue []

                                    let mutable derivedCount = 0
                                    let reasons = arrayField node "deltaReasons"

                                    for endId in request.MeasurementIds do
                                        let endMetric = metricById record endId |> Option.get
                                        let metricId = stringField endMetric "id" |> Option.defaultValue ""

                                        match cumulativeDeltaMetric |> Map.tryFind metricId with
                                        | None -> reasons.Add(JsonValue.Create $"{metricId}:not-cumulative": JsonNode)
                                        | Some deltaMetricId ->
                                            let startCandidates =
                                                beginIds
                                                |> List.choose (metricById record)
                                                |> List.filter (fun start -> compatibleObservations start endMetric)

                                            match startCandidates with
                                            | [ startMetric ] ->
                                                let startValue = doubleField startMetric "value" |> Option.get
                                                let endValue = doubleField endMetric "value" |> Option.get

                                                if endValue < startValue then
                                                    reasons.Add(JsonValue.Create $"{metricId}:counter-decreased": JsonNode)
                                                else
                                                    let derivation = JsonObject()
                                                    derivation["method"] <- JsonValue.Create "cumulative-checkpoint-delta"
                                                    derivation["beginMeasurementId"] <- startMetric["measurementId"].DeepClone()
                                                    derivation["endMeasurementId"] <- endMetric["measurementId"].DeepClone()
                                                    derivation["beginValue"] <- JsonValue.Create startValue
                                                    derivation["endValue"] <- JsonValue.Create endValue
                                                    let pricingSource, pricingVersion =
                                                        if deltaMetricId.StartsWith("cost.") then Some "provider-cumulative", Some "provider-reported"
                                                        else None, None

                                                    let deltaRequest =
                                                        { ExecutionId = Some executionId
                                                          StepId = stringField step "stepId"
                                                          MetricId = deltaMetricId
                                                          Value = endValue - startValue
                                                          Unit = stringField endMetric "unit"
                                                          Currency = stringField endMetric "currency"
                                                          Quality = "derived"
                                                          Confidence = null
                                                          CollectedAt = request.OccurredAt
                                                          Source = ({ Type = "calculated"; Name = "step-checkpoint"; Mechanism = "cumulative-delta" }: CapabilitySource)
                                                          PricingSource = pricingSource
                                                          PricingVersion = pricingVersion
                                                          PricingEffectiveAt = None
                                                          CalculationMethod = Some "cumulative-checkpoint-delta"
                                                          Model = request.Identity.Model
                                                          TokenMeasurementIds = [ stringField startMetric "measurementId" |> Option.get; endId ]
                                                          Actor = request.Actor
                                                          Identity = request.Identity }

                                                    match appendMetric root record step deltaRequest derivation with
                                                    | Ok measurementId ->
                                                        arrayField telemetry "deltas" |> _.Add(JsonValue.Create measurementId: JsonNode)
                                                        derivedCount <- derivedCount + 1
                                                    | Error message -> reasons.Add(JsonValue.Create $"{metricId}:{message}": JsonNode)
                                            | [] -> reasons.Add(JsonValue.Create $"{metricId}:missing-compatible-begin": JsonNode)
                                            | _ -> reasons.Add(JsonValue.Create $"{metricId}:ambiguous-compatible-begin": JsonNode)

                                    node["deltaStatus"] <- JsonValue.Create(if derivedCount > 0 then "derived" else "insufficient")

                                appendExecutionEvent record $"step.checkpoint.{request.Phase}" request.OccurredAt (stringField step "stepId" |> Option.get) request.Actor
                                save file record
                                Ok node)

    let private relationshipField (kind: string) =
        match kind with
        | "file-observed" -> Some "filesObserved"
        | "file-changed" -> Some "filesChanged"
        | "commit" -> Some "commits"
        | "command" -> Some "commands"
        | "test" -> Some "tests"
        | "validation" -> Some "validations"
        | "evidence" -> Some "evidence"
        | "decision" -> Some "decisions"
        | "requirement" -> Some "requirements"
        | "outcome" -> Some "outcomes"
        | _ -> None

    let link (root: string) (request: StepLinkRequest) : Result<JsonObject, string> =
        match relationshipField request.Kind with
        | None -> Error "relationship kind must be file-observed, file-changed, commit, command, test, validation, evidence, decision, requirement, or outcome"
        | Some _ when String.IsNullOrWhiteSpace request.Value -> Error "relationship value must not be empty"
        | Some field ->
            match resolveExecution root request.ExecutionId request.Actor request.Identity with
            | Error message -> Error message
            | Ok selected ->
                let executionId = stringField selected "executionId" |> Option.get

                withExecutionLock root executionId (fun record file ->
                    match resolveStepForObservation request.StepId record with
                    | Error message -> Error message
                    | Ok step ->
                        match observationNotBeforeStep "relationship timestamp" request.OccurredAt step with
                        | Error message -> Error message
                        | Ok() ->
                            let relationships = objectField step "relationships" |> Option.defaultWith (fun () -> let node = relationshipsNode () in step["relationships"] <- node; node)
                            let items = arrayField relationships field
                            let duplicate =
                                items
                                |> Seq.exists (function
                                    | :? JsonObject as item -> stringField item "value" = Some request.Value && stringField item "source" = Some request.Source
                                    | _ -> false)

                            if not duplicate then
                                let item = JsonObject()
                                item["value"] <- JsonValue.Create request.Value
                                item["source"] <- JsonValue.Create request.Source
                                item["recordedAt"] <- JsonValue.Create request.OccurredAt
                                item["actor"] <- ActorJson.node request.Actor
                                items.Add(item: JsonNode)

                            appendExecutionEvent record "step.relationship.recorded" request.OccurredAt (stringField step "stepId" |> Option.get) request.Actor
                            save file record
                            Ok step)

    let private metricSummary (record: JsonObject) (step: JsonObject) =
        let telemetry = objectField step "telemetry"
        let ids =
            telemetry
            |> Option.map (fun value ->
                match value["measurementIds"] with
                | :? JsonArray as items -> items |> Seq.choose (fun item -> if item = null then None else Some(item.GetValue<string>())) |> Set.ofSeq
                | _ -> Set.empty)
            |> Option.defaultValue Set.empty

        let measurements =
            match record["metrics"] with
            | :? JsonArray as metrics ->
                metrics
                |> Seq.choose (function :? JsonObject as metric when ids.Contains(stringField metric "measurementId" |> Option.defaultValue "") -> Some(metric.DeepClone()) | _ -> None)
            | _ -> Seq.empty

        let node = JsonObject()
        let array = JsonArray()
        measurements |> Seq.iter (fun measurement -> array.Add measurement)
        node["measurements"] <- array
        node["measurementCount"] <- JsonValue.Create array.Count
        node

    let private queryNode (record: JsonObject) (step: JsonObject) =
        let result = step.DeepClone().AsObject()
        result["summary"] <- metricSummary record step

        match stringField step "startedAt", stringField step "endedAt" with
        | Some startedAt, Some endedAt ->
            match DateTimeOffset.TryParse startedAt, DateTimeOffset.TryParse endedAt with
            | (true, start), (true, finish) -> result["durationMs"] <- JsonValue.Create(max 0L (finish.ToUnixTimeMilliseconds() - start.ToUnixTimeMilliseconds()))
            | _ -> result["durationMs"] <- null
        | _ -> result["durationMs"] <- null

        result

    let list (root: string) (executionId: string option) (workItemId: string option) : JsonObject list =
        let records =
            FileTelemetryQueryRepository.readAll root
            |> List.filter (fun record ->
                (executionId |> Option.forall (fun id -> stringField record "executionId" = Some id))
                && (workItemId |> Option.forall (fun id -> stringField record "workItemId" = Some id)))

        records
        |> List.collect (fun record -> stepNodes record |> List.map (queryNode record))
        |> List.sortBy (fun step -> stringField step "executionId" |> Option.defaultValue "", intField step "sequence" |> Option.defaultValue 0)

    let show (root: string) (stepId: string) : Result<JsonObject, string> =
        FileTelemetryQueryRepository.readAll root
        |> List.tryPick (fun record -> findStepNode stepId record |> Option.map (queryNode record))
        |> optionToResult $"step '{stepId}' was not found"

    let findings (record: JsonObject) : string list =
        let executionId = stringField record "executionId" |> Option.defaultValue "?"
        let workItemId = stringField record "workItemId" |> Option.defaultValue ""
        let startedAt = stringField record "startedAt" |> Option.defaultValue ""
        let stepItems = stepNodes record
        let stepStates = stepItems |> List.choose parseStepState
        let mutable results = Step.validateStored startedAt stepStates |> List.map (fun message -> $"{executionId}: {message}")

        for step in stepItems do
            if parseStepState step |> Option.isNone then
                let invalidStepId = stringField step "stepId" |> Option.defaultValue "?"
                results <- $"{executionId}: step '{invalidStepId}' is structurally invalid" :: results

        let knownStepIds = stepStates |> List.map _.StepId |> Set.ofList
        let executionIdentity = objectField record "identity"
        let executionActor = actorAndIdentityOf record |> Option.map fst
        let measurementOwners = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let metricStepIds = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let metricNames = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let knownMeasurements =
            match record["metrics"] with
            | :? JsonArray as metrics ->
                metrics
                |> Seq.choose (function
                    | :? JsonObject as metric ->
                        match stringField metric "measurementId" with
                        | Some id ->
                            match stringField metric "id" with
                            | Some metricId -> metricNames[id] <- metricId
                            | None -> ()
                            match stringField metric "stepId" with
                            | Some stepId when not (knownStepIds.Contains stepId) -> results <- $"{executionId}: metric '{id}' references unknown step '{stepId}'" :: results
                            | Some stepId -> metricStepIds[id] <- stepId
                            | _ when stringField metric "scope" = Some "step" -> results <- $"{executionId}: step-scoped metric '{id}' requires stepId" :: results
                            | _ -> ()
                            Some id
                        | None -> None
                    | _ -> None)
                |> Set.ofSeq
            | _ -> Set.empty

        let knownRawSnapshots =
            match record["rawTelemetry"] with
            | :? JsonArray as snapshots ->
                snapshots
                |> Seq.choose (function :? JsonObject as snapshot -> stringField snapshot "snapshotId" | _ -> None)
                |> Set.ofSeq
            | _ -> Set.empty

        stepStates
        |> List.groupBy _.ParentStepId
        |> List.iter (fun (_, siblings) ->
            siblings
            |> List.sortBy _.Sequence
            |> List.iteri (fun index state ->
                match stepItems |> List.tryFind (fun item -> stringField item "stepId" = Some state.StepId) |> Option.bind (fun item -> intField item "siblingSequence") with
                | Some actual when actual = index + 1 -> ()
                | _ -> results <- $"{executionId}: step '{state.StepId}' has invalid siblingSequence" :: results))

        for step in stepItems do
            let stepId = stringField step "stepId" |> Option.defaultValue "?"
            if stringField step "executionId" <> Some executionId then results <- $"{executionId}: step '{stepId}' executionId mismatch" :: results
            if stringField step "workItemId" <> Some workItemId then results <- $"{executionId}: step '{stepId}' workItemId mismatch" :: results
            if stringField step "instanceId" <> stringField record "instanceId" then results <- $"{executionId}: step '{stepId}' instanceId mismatch" :: results

            match executionIdentity, objectField step "identity" with
            | Some expected, Some actual ->
                [ "provider"; "model"; "modelVersion"; "runtime"; "runtimeVersion"; "sessionId"; "conversationId"; "runId"; "agentId"; "subagentId"; "parentExecutionId" ]
                |> List.iter (fun field ->
                    if stringField expected field <> stringField actual field then
                        results <- $"{executionId}: step '{stepId}' identity.{field} does not match its execution" :: results)
            | Some _, None -> results <- $"{executionId}: step '{stepId}' requires identity" :: results
            | _ -> ()

            match executionActor, objectField step "actor" with
            | Some expected, Some actual ->
                let expectedNode = ActorJson.node expected
                [ "kind"; "id"; "provider"; "model"; "runtime" ]
                |> List.iter (fun field ->
                    if stringField expectedNode field <> stringField actual field then
                        results <- $"{executionId}: step '{stepId}' actor.{field} does not match its execution" :: results)
            | Some _, None -> results <- $"{executionId}: step '{stepId}' requires actor" :: results
            | _ -> ()

            match step["transitions"] with
            | :? JsonArray as transitions ->
                let mutable priorStatus: string option = None
                let mutable priorAt: DateTimeOffset option = None

                for index, item in transitions |> Seq.indexed do
                    match item with
                    | :? JsonObject as transition ->
                        let fromStatus = stringField transition "from"
                        let toStatus = stringField transition "to"
                        let occurredAt = stringField transition "occurredAt"

                        if index = 0 && fromStatus.IsSome then
                            results <- $"{executionId}: step '{stepId}' initial transition must not have from status" :: results
                        elif index > 0 && fromStatus <> priorStatus then
                            results <- $"{executionId}: step '{stepId}' transition {index + 1} does not continue prior status" :: results

                        if index = 0 && toStatus <> Some "planned" && toStatus <> Some "active" then
                            results <- $"{executionId}: step '{stepId}' initial transition must create a planned or active step" :: results
                        elif index > 0 then
                            match priorStatus |> Option.bind StepStatus.tryParse, toStatus |> Option.bind StepStatus.tryParse with
                            | Some previous, Some target when Step.legalTransition previous target -> ()
                            | _ -> results <- $"{executionId}: step '{stepId}' transition {index + 1} is illegal" :: results

                        match occurredAt |> Option.bind (fun value -> match DateTimeOffset.TryParse value with true, parsed -> Some parsed | _ -> None) with
                        | None -> results <- $"{executionId}: step '{stepId}' transition {index + 1} requires a valid occurredAt" :: results
                        | Some parsed when priorAt |> Option.exists (fun previous -> parsed < previous) ->
                            results <- $"{executionId}: step '{stepId}' transitions must be chronological" :: results
                        | Some parsed -> priorAt <- Some parsed

                        priorStatus <- toStatus
                    | _ -> results <- $"{executionId}: step '{stepId}' transition {index + 1} must be an object" :: results

                if priorStatus <> stringField step "status" then
                    results <- $"{executionId}: step '{stepId}' status does not match its last transition" :: results
                match transitions |> Seq.tryLast with
                | Some(:? JsonObject as last) when stringField last "occurredAt" <> stringField step "updatedAt" ->
                    results <- $"{executionId}: step '{stepId}' updatedAt does not match its last transition" :: results
                | _ -> ()
            | _ -> results <- $"{executionId}: step '{stepId}' transitions must be an array" :: results

            match objectField step "telemetry" with
            | Some telemetry ->
                let referencedMetricNames = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                match telemetry["measurementIds"] with
                | :? JsonArray as refs ->
                    for item in refs do
                        if item <> null then
                            let id = item.GetValue<string>()
                            if not (knownMeasurements.Contains id) then results <- $"{executionId}: step '{stepId}' references unknown measurement '{id}'" :: results
                            if metricNames.ContainsKey id then referencedMetricNames.Add(metricNames[id]) |> ignore
                            if metricStepIds.ContainsKey id && metricStepIds[id] <> stepId then results <- $"{executionId}: measurement '{id}' stepId does not match owning step '{stepId}'" :: results
                            if measurementOwners.ContainsKey id && measurementOwners[id] <> stepId then results <- $"{executionId}: measurement '{id}' is attributed to multiple steps" :: results
                            else measurementOwners[id] <- stepId
                | _ -> results <- $"{executionId}: step '{stepId}' telemetry.measurementIds must be an array" :: results

                match telemetry["capabilities"] with
                | :? JsonArray as capabilities ->
                    for item in capabilities do
                        match item with
                        | :? JsonObject as capability ->
                            let status = stringField capability "status" |> Option.defaultValue ""
                            let metricId = stringField capability "metricId" |> Option.defaultValue ""
                            if (status = "supported-unavailable" || status = "unsupported" || status = "unknown") && referencedMetricNames.Contains metricId then
                                results <- $"{executionId}: step '{stepId}' capability for '{metricId}' contradicts a recorded value" :: results
                        | _ -> results <- $"{executionId}: step '{stepId}' telemetry capability must be an object" :: results
                | _ -> results <- $"{executionId}: step '{stepId}' telemetry.capabilities must be an array" :: results

                match telemetry["rawSnapshotIds"] with
                | :? JsonArray as refs ->
                    for item in refs do
                        if item <> null then
                            let id = item.GetValue<string>()
                            if not (knownRawSnapshots.Contains id) then results <- $"{executionId}: step '{stepId}' references unknown raw snapshot '{id}'" :: results
                | _ -> results <- $"{executionId}: step '{stepId}' telemetry.rawSnapshotIds must be an array" :: results

                match telemetry["deltas"] with
                | :? JsonArray as refs ->
                    for item in refs do
                        if item <> null && not (knownMeasurements.Contains(item.GetValue<string>())) then
                            results <- $"{executionId}: step '{stepId}' references unknown delta measurement '{item.GetValue<string>()}'" :: results
                | _ -> results <- $"{executionId}: step '{stepId}' telemetry.deltas must be an array" :: results

                match telemetry["checkpoints"] with
                | :? JsonArray as checkpoints ->
                    let phases = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                    for checkpoint in checkpoints do
                        match checkpoint with
                        | :? JsonObject as value ->
                            let phase = stringField value "phase" |> Option.defaultValue ""
                            if (phase <> "begin" && phase <> "end") || not (phases.Add phase) then
                                results <- $"{executionId}: step '{stepId}' has invalid or duplicate checkpoint phase '{phase}'" :: results
                            match value["measurementIds"] with
                            | :? JsonArray as ids ->
                                for id in ids do
                                    if id <> null && not (knownMeasurements.Contains(id.GetValue<string>())) then
                                        results <- $"{executionId}: step '{stepId}' checkpoint references unknown measurement '{id.GetValue<string>()}'" :: results
                            | _ -> results <- $"{executionId}: step '{stepId}' checkpoint measurementIds must be an array" :: results
                            match value["rawSnapshotIds"] with
                            | :? JsonArray as ids ->
                                for id in ids do
                                    if id <> null && not (knownRawSnapshots.Contains(id.GetValue<string>())) then
                                        results <- $"{executionId}: step '{stepId}' checkpoint references unknown raw snapshot '{id.GetValue<string>()}'" :: results
                            | _ -> results <- $"{executionId}: step '{stepId}' checkpoint rawSnapshotIds must be an array" :: results
                        | _ -> results <- $"{executionId}: step '{stepId}' checkpoint must be an object" :: results
                | _ -> results <- $"{executionId}: step '{stepId}' telemetry.checkpoints must be an array" :: results
            | None -> results <- $"{executionId}: step '{stepId}' requires telemetry" :: results

            let hasChildren = stepStates |> List.exists (fun candidate -> candidate.ParentStepId = Some stepId)
            if hasChildren && hasDirectTelemetry step then
                results <- $"{executionId}: grouping step '{stepId}' must not carry direct telemetry" :: results

        for KeyValue(measurementId, stepId) in metricStepIds do
            if not (measurementOwners.ContainsKey measurementId) then
                results <- $"{executionId}: step metric '{measurementId}' is not referenced by its owning step '{stepId}'" :: results

        if stringField record "status" = Some "finalized" then
            let unresolved = Step.unresolved stepStates
            if not unresolved.IsEmpty then results <- $"{executionId}: finalized execution has unresolved step '{unresolved.Head.StepId}'" :: results

        results |> List.rev
