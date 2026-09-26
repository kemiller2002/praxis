namespace Ros.Infrastructure.Provenance

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Json
open Ros.Infrastructure.Work

/// One execution record projected to what provenance needs: which work item
/// it served, whether it is still running, and who performed it.
type ExecutionRecordView =
    { ExecutionId: string
      WorkItemId: string
      Status: string
      StartedAt: string
      Actor: Actor
      Identity: Identity }

type ContributionRecordRequest =
    { Target: string
      Operation: ContributionOperation
      Reason: string option
      Evidence: string list
      DerivedFrom: string list
      ExecutionId: string option
      OccurredAt: string
      IdentityOverrides: IdentityInputs }

type ContributionRecordOutcome =
    { Path: string
      ArtifactId: string option
      Contribution: Contribution
      Changed: bool
      EventId: string option
      BeforeSha256: string
      AfterSha256: string }

/// File-backed provenance effects: reading the repository's provenance
/// policy, projecting execution records to actors, reading event
/// attribution, and recording a contribution into an artifact's front
/// matter plus an `artifact.contributed` event in `.ros/events/events.jsonl`.
[<RequireQualifiedAccess>]
module FileProvenanceRepository =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"

    let private compactOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private stringField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    let private objectField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as item -> Some item
        | _ -> None

    let private stringArray (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as items ->
            items
            |> Seq.choose (fun item ->
                match item with
                | :? JsonValue as value ->
                    match value.TryGetValue<string>() with
                    | true, text -> Some text
                    | _ -> None
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let sha256 (text: string) =
        text |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    // ---- policy ---------------------------------------------------------

    /// Reads `ros.json` `provenance`:
    /// `{"version":"1.0.0","enforce":true,"requiredFrom":"yyyy-MM-dd","requireOriginator":["RQ"]}`.
    /// A missing block means "not configured" (legacy-compatible); an
    /// enforced block without a valid `requiredFrom` date is a
    /// configuration error rather than silently unenforced.
    let readPolicy (root: string) : Result<ProvenancePolicy, string> =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            Ok ProvenancePolicy.notConfigured
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as config ->
                    match objectField config "provenance" with
                    | None -> Ok ProvenancePolicy.notConfigured
                    | Some block ->
                        let enforce =
                            match block["enforce"] with
                            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
                            | _ -> false

                        let requiredFrom = stringField block "requiredFrom"

                        let requireOriginator =
                            match block["requireOriginator"] with
                            | :? JsonArray -> stringArray block "requireOriginator" |> Set.ofList
                            | _ -> ProvenancePolicy.defaultRequireOriginator

                        match enforce, requiredFrom |> Option.bind ProvenancePolicy.dateOf with
                        | true, None ->
                            Error "ros.json provenance.enforce is true but provenance.requiredFrom is not a yyyy-MM-dd date"
                        | _, date ->
                            Ok
                                { Enforced = enforce
                                  RequiredFrom = date
                                  RequireOriginator = requireOriginator }
                | _ -> Ok ProvenancePolicy.notConfigured
            with error ->
                Error $"ros.json could not be read for provenance policy: {error.Message}"

    // ---- executions -----------------------------------------------------

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

    /// The identity-discovery mechanism the record itself preserved, used to
    /// project records written before `identity.actorKind` existed.
    let private discoveryMechanism (record: JsonObject) =
        objectField record "provenance"
        |> Option.bind (fun provenance ->
            match provenance["sources"] with
            | :? JsonArray as sources ->
                sources
                |> Seq.tryPick (fun source ->
                    match source with
                    | :? JsonObject as item when stringField item "name" = Some "runtime-identity" -> stringField item "mechanism"
                    | _ -> None)
            | _ -> None)

    let executionView (record: JsonObject) : ExecutionRecordView option =
        match stringField record "executionId", objectField record "identity" with
        | Some executionId, Some identityNode ->
            let identity = identityOf identityNode

            Some
                { ExecutionId = executionId
                  WorkItemId = stringField record "workItemId" |> Option.defaultValue ""
                  Status = stringField record "status" |> Option.defaultValue ""
                  StartedAt = stringField record "startedAt" |> Option.defaultValue ""
                  Actor = ActorResolution.ofExecutionRecord (stringField identityNode "actorKind") (discoveryMechanism record) identity
                  Identity = identity }
        | _ -> None

    let readExecutions (root: string) : ExecutionRecordView list =
        FileTelemetryQueryRepository.readAll root |> List.choose executionView

    let readExecutionActors (root: string) : Map<string, Actor> =
        readExecutions root |> List.map (fun view -> view.ExecutionId, view.Actor) |> Map.ofList

    // ---- events ---------------------------------------------------------

    let private readEventNodes (root: string) : JsonObject list =
        let path = Path.Combine(root, eventsRelativePath)

        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.filter (fun line -> line.Trim().Length > 0)
            |> Array.choose (fun line ->
                try
                    match JsonNode.Parse line with
                    | :? JsonObject as item -> Some item
                    | _ -> None
                with _ ->
                    None)
            |> Array.toList

    let readEventViews (root: string) : EventActorView list =
        readEventNodes root
        |> List.map (fun node ->
            { EventId = stringField node "eventId" |> Option.defaultValue "?"
              EventType = stringField node "type" |> Option.defaultValue "?"
              OccurredAt = stringField node "occurredAt" |> Option.defaultValue ""
              TelemetryExecutionIds = stringArray node "telemetryExecutions"
              Actor = ActorJson.tryParse node["actor"] })

    /// Every `artifact.contributed` event in the log (for `provenance show`
    /// and exported history).
    let readContributionEvents (root: string) : JsonObject list =
        readEventNodes root |> List.filter (fun node -> stringField node "type" = Some "artifact.contributed")

    /// Backlog items with and without a structured `createdByActor`.
    let readBacklogActorCoverage (root: string) : int * int =
        let path = Path.Combine(root, ".ros", "work", "queue.json")

        if not (File.Exists path) then
            0, 0
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as queue ->
                    match queue["items"] with
                    | :? JsonArray as items ->
                        let all = items |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
                        all.Length, all |> List.filter (fun item -> item["createdByActor"] <> null) |> List.length
                    | _ -> 0, 0
                | _ -> 0, 0
            with _ ->
                0, 0

    // ---- recording ------------------------------------------------------

    let private writeAtomic (file: string) (content: string) =
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")
        File.WriteAllText(temporary, content, UTF8Encoding(false))
        File.Move(temporary, file, true)

    /// A contribution made outside any execution is keyed by its UTC day and
    /// a digest of its actor: one actor's recordings on one day form one entry
    /// (merging operations and advancing `last`, exactly like an execution's
    /// entry), while a different actor or day is a different contribution.
    let private contributionKey (occurredAt: string) (actor: Actor) =
        let stamp = occurredAt.Substring(0, min 10 occurredAt.Length).Replace("-", "")
        let suffix = sha256 ($"{ActorKind.code actor.Kind}\u0000{actor.Id}") |> fun digest -> digest.Substring(0, 8)
        $"CTB-{stamp}-{suffix}"

    /// Resolves the artifact file: an artifact ID is looked up among the
    /// repository's canonical artifacts; anything else is a repository-
    /// relative Markdown path that must stay inside the repository.
    let private resolveTarget (root: string) (target: string) : Result<string, string> =
        if ArtifactPolicy.isValidIdentifier target then
            match (FileArtifactRepository.create root).Load() with
            | Error failure -> Error failure.Message
            | Ok loaded ->
                match loaded.Documents |> List.filter (fun document -> ArtifactDocument.identifier document = target) with
                | [ document ] -> Ok document.RelativePath
                | [] -> Error $"no canonical artifact has id '{target}'"
                | _ -> Error $"artifact id '{target}' is ambiguous (duplicate ids); pass --path instead"
        else
            let full = Path.GetFullPath(Path.Combine(root, target))
            let rootFull = Path.GetFullPath root + string Path.DirectorySeparatorChar

            if not (full.StartsWith(rootFull, StringComparison.Ordinal)) then
                Error $"'{target}' is outside the repository"
            elif not (full.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) then
                Error $"'{target}' is not a Markdown artifact"
            elif not (File.Exists full) then
                Error $"'{target}' does not exist"
            else
                Ok(Path.GetRelativePath(root, full).Replace('\\', '/'))

    /// Chooses the execution a contribution belongs to and the actor it is
    /// attributed to. Identity is inherited from the execution record the
    /// agent established at `work begin`, so later commands need not repeat
    /// it -- but only by a process that is plausibly that same run: its own
    /// identity (declared by flag or `ROS_ACTOR`/`ROS_ACTOR_KIND`, or
    /// discovered from a known agent runtime) must agree with the execution's
    /// actor, and a known session/run ID must match. A process with no
    /// identity at all inherits nothing implicitly; it must name the
    /// execution with `--execution`. With several matching executions,
    /// `--execution` is required. Outside any execution only a declared
    /// non-agent actor may contribute (keyed `CTB-...`); an agent must begin
    /// its own execution.
    let private resolveAttribution (root: string) (request: ContributionRecordRequest) : Result<string * Actor, string> =
        match FileTelemetryExecutionRepository.resolveIdentity request.IdentityOverrides with
        | Error message -> Error message
        | Ok(current, currentIdentity, _) ->
            let declared = ActorResolution.isDeclared current
            let executions = readExecutions root

            let matches (execution: ExecutionRecordView) =
                Actor.agrees current execution.Actor && ActorResolution.sameRun currentIdentity execution.Identity

            // Implicit inheritance additionally needs positive evidence of
            // being that run; naming an execution with --execution is an
            // explicit assertion and needs only agreement.
            let inherits (execution: ExecutionRecordView) =
                matches execution
                && ActorResolution.evidentlySameRun current currentIdentity execution.Actor execution.Identity

            let attribute (execution: ExecutionRecordView) =
                if not declared || matches execution then
                    Ok(execution.ExecutionId, execution.Actor)
                else
                    let session = execution.Identity.SessionId |> Option.defaultValue Actor.UnknownValue

                    Error
                        $"this process identifies as {Actor.describe current}, which does not match execution '{execution.ExecutionId}' ({Actor.describe execution.Actor}, session {session}); a contribution cannot be attributed to another actor's or another run's execution"

            match request.ExecutionId with
            | Some executionId ->
                match executions |> List.tryFind (fun view -> view.ExecutionId = executionId) with
                | Some execution -> attribute execution
                | None -> Error $"execution '{executionId}' has no record under .ros/telemetry/executions"
            | None ->
                let active = executions |> List.filter (fun view -> view.Status = "active")
                let candidates = active |> List.filter inherits

                match declared, candidates, active with
                | false, _, [] ->
                    Error
                        "this process has no identity and no execution is active; declare yourself (ROS_ACTOR_KIND and ROS_ACTOR, or --actor-kind and --actor) or begin a work execution"
                | false, _, _ ->
                    Error
                        "this process has no identity of its own, so it cannot inherit an active execution implicitly; declare yourself (ROS_ACTOR_KIND and ROS_ACTOR, or --actor-kind and --actor). Only the actor running an execution may name it with --execution EXE-..."
                | true, [ execution ], _ -> attribute execution
                | true, [], _ when current.Kind <> ActorKind.Agent ->
                    // A declared human/automation acting outside any execution
                    // (for example, approving while an agent is mid-run).
                    Ok(contributionKey request.OccurredAt current, current)
                | true, [], [] ->
                    Error
                        "no active execution: an agent contribution must be recorded inside a work execution; run './ros work begin --id ID --occurred-at TIMESTAMP' first"
                | true, [], others ->
                    let ids = others |> List.map (fun view -> $"{view.ExecutionId} ({Actor.describe view.Actor})") |> String.concat ", "

                    Error
                        $"this process identifies as {Actor.describe current}, but no active execution is evidently this actor's run: {ids}; begin your own work execution, or name yours with --execution EXE-..."
                | true, many, _ ->
                    let ids = many |> List.map _.ExecutionId |> String.concat ", "
                    Error $"several executions are active for this actor ({ids}); pass --execution EXE-... to name the one responsible"

    let private contributionEvent
        (repositoryId: string)
        (path: string)
        (artifactId: string option)
        (contribution: Contribution)
        (derivedFrom: string list)
        (version: string option)
        (beforeSha: string)
        (afterSha: string)
        : JsonObject =
        let stringArrayNode (values: string list) =
            let array = JsonArray()
            values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
            array

        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create "artifact.contributed"
        node["repository"] <- JsonValue.Create repositoryId
        node["occurredAt"] <- JsonValue.Create contribution.At
        let artifact = JsonObject()
        artifactId |> Option.iter (fun id -> artifact["id"] <- JsonValue.Create id)
        artifact["path"] <- JsonValue.Create path
        version |> Option.iter (fun value -> artifact["version"] <- JsonValue.Create value)
        node["artifact"] <- artifact
        node["contribution"] <- JsonValue.Create contribution.Key
        node["operations"] <- stringArrayNode (contribution.Operations |> List.map ContributionOperation.code)
        node["telemetryExecutions"] <- stringArrayNode (Contribution.execution contribution |> Option.toList)
        node["actor"] <- ActorJson.node contribution.Actor
        contribution.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create reason)
        node["evidence"] <- stringArrayNode contribution.Evidence
        node["derivedFrom"] <- stringArrayNode derivedFrom
        let previous = JsonObject()
        previous["sha256"] <- JsonValue.Create beforeSha
        node["previous"] <- previous
        let current = JsonObject()
        current["sha256"] <- JsonValue.Create afterSha
        node["current"] <- current
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        let eventId = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node)
        node["eventId"] <- JsonValue.Create eventId
        node

    /// Appends one event through the shared `work-state` journal so it never
    /// interleaves with a concurrent work transition's own event write.
    let private appendEvent (root: string) (event: JsonObject) : Result<unit, string> =
        let eventsPath = Path.Combine(root, eventsRelativePath)
        let contextPath = Path.Combine(root, contextRelativePath)
        let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""
        let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
        let eventsContent = $"{current}{separator}{event.ToJsonString compactOptions}\n"

        if File.Exists contextPath then
            let writes: Ros.Application.Work.WorkStateWrite list =
                [ { Path = eventsRelativePath; Content = eventsContent }
                  { Path = contextRelativePath; Content = File.ReadAllText contextPath } ]

            match WorkStateTransaction.prepare root writes with
            | Error failure -> Error failure.Message
            | Ok() ->
                match WorkStateTransaction.recover root with
                | Error failure -> Error failure.Message
                | Ok() -> Ok()
        else
            Directory.CreateDirectory(Path.GetDirectoryName eventsPath) |> ignore
            writeAtomic eventsPath eventsContent
            Ok()

    let private metadataOf (relativePath: string) (text: string) =
        FrontMatter.parse relativePath text
        |> Result.mapError (fun message -> $"{relativePath}: front matter: {message}")

    /// Proves the rewritten document says exactly what was intended: the
    /// provenance reads back as the merged model, lineage reads back as
    /// exactly the expected list, no other front-matter field changed, and
    /// the document body is byte-for-byte unchanged.
    let private verify
        (relativePath: string)
        (before: ArtifactDocument)
        (originalText: string)
        (expected: ArtifactProvenance)
        (expectedLineage: string list)
        (text: string)
        : Result<unit, string> =
        match metadataOf relativePath text with
        | Error message -> Error message
        | Ok after ->
            let untouched (document: ArtifactDocument) =
                document.Metadata |> Map.remove ArtifactProvenance.FieldName |> Map.remove Lineage.FieldName

            match ArtifactProvenance.parse after.Metadata with
            | Ok(Some actual) when actual = expected ->
                if untouched before <> untouched after then
                    Error "internal error: recording provenance would change unrelated front-matter fields; nothing was written"
                elif Lineage.sources after <> expectedLineage then
                    Error "internal error: derived_from did not read back as written; nothing was written"
                elif ProvenanceFrontMatter.bodyOf originalText <> ProvenanceFrontMatter.bodyOf text then
                    Error "internal error: recording provenance would change the document body; nothing was written"
                else
                    Ok()
            | _ -> Error "internal error: provenance did not read back as written; nothing was written"

    /// Records one contribution. Idempotent: re-recording an operation the
    /// execution already recorded changes nothing and writes no event.
    let record (root: string) (request: ContributionRecordRequest) : Result<ContributionRecordOutcome, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match resolveTarget root request.Target with
                        | Error message -> Error message
                        | Ok relativePath ->
                            let file = Path.Combine(root, relativePath)
                            let text = File.ReadAllText file
                            let beforeSha = sha256 text

                            match metadataOf relativePath text with
                            | Error message -> Error message
                            | Ok document ->
                                match ArtifactProvenance.parse document.Metadata with
                                | Error problems ->
                                    let detail = problems |> List.map (fun problem -> $"{problem.Field}: {problem.Message}") |> String.concat "; "
                                    Error $"{relativePath} has malformed provenance ({detail}); correct it before recording more"
                                | Ok existing ->
                                    match resolveAttribution root request with
                                    | Error message -> Error message
                                    | Ok(key, actor) ->
                                        // Front matter is line-structured: a
                                        // reason cannot span lines, and a
                                        // blank reason is no reason.
                                        let reason =
                                            request.Reason
                                            |> Option.map ProvenanceFrontMatter.normalize
                                            |> Option.filter (fun text -> text.Trim().Length > 0)

                                        let contribution =
                                            { Key = key
                                              Operations = [ request.Operation ]
                                              At = request.OccurredAt
                                              Last = None
                                              Actor = actor
                                              Reason = reason
                                              Evidence = request.Evidence |> List.distinct }

                                        let current = existing |> Option.defaultValue ArtifactProvenance.empty

                                        match ArtifactProvenance.record contribution current with
                                        | Error message -> Error message
                                        | Ok updated ->
                                            let merged = updated.Contributions |> List.find (fun item -> item.Key = key)
                                            let existingSources = Lineage.sources document
                                            let newLineage = request.DerivedFrom |> List.distinct |> List.filter (fun item -> not (List.contains item existingSources))
                                            let artifactId = ArtifactDocument.identifier document |> Option.ofObj |> Option.filter (fun id -> id.Length > 0)

                                            if Some updated = existing && newLineage.IsEmpty then
                                                Ok
                                                    { Path = relativePath
                                                      ArtifactId = artifactId
                                                      Contribution = merged
                                                      Changed = false
                                                      EventId = None
                                                      BeforeSha256 = beforeSha
                                                      AfterSha256 = beforeSha }
                                            else
                                                let rewritten =
                                                    ProvenanceFrontMatter.writeContribution merged text
                                                    |> Result.bind (ProvenanceFrontMatter.addDerivedFrom existingSources newLineage)

                                                match rewritten with
                                                | Error message -> Error message
                                                | Ok newText ->
                                                    match verify relativePath document text updated (existingSources @ newLineage) newText with
                                                    | Error message -> Error message
                                                    | Ok() ->
                                                        writeAtomic file newText
                                                        let afterSha = sha256 newText

                                                        let version =
                                                            match ArtifactDocument.tryMetadata "version" document with
                                                            | Some value -> Some(ArtifactValue.display value)
                                                            | None -> None

                                                        let event =
                                                            contributionEvent
                                                                (FileWorkConfigRepository.readRepositoryId root)
                                                                relativePath
                                                                artifactId
                                                                contribution
                                                                request.DerivedFrom
                                                                version
                                                                beforeSha
                                                                afterSha

                                                        match appendEvent root event with
                                                        | Error message ->
                                                            Error $"{relativePath} was attributed, but its artifact.contributed event could not be written: {message}"
                                                        | Ok() ->
                                                            Ok
                                                                { Path = relativePath
                                                                  ArtifactId = artifactId
                                                                  Contribution = merged
                                                                  Changed = true
                                                                  EventId = stringField event "eventId"
                                                                  BeforeSha256 = beforeSha
                                                                  AfterSha256 = afterSha }
                with error ->
                    Error error.Message

            match lease.Release(), result with
            | Error releaseFailure, Ok _ -> Error releaseFailure.Message
            | _, outcome -> outcome
