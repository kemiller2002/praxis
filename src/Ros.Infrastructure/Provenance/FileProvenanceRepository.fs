namespace Ros.Infrastructure.Provenance

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Provenance
open Ros.Domain.Artifacts
open Ros.Domain.Provenance
open Ros.Domain.Telemetry
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Git
open Ros.Infrastructure.Work

/// The actor this process records, how its execution was bound, and the
/// identity-discovery mechanism that produced it.
type CurrentActor =
    { Actor: Actor
      Binding: ExecutionBinding
      Mechanism: string
      /// The work item of the bound execution, inherited by contributions.
      WorkItem: string option }

/// Reads every Praxis store that carries provenance -- the work event log,
/// the backlog queue, telemetry execution records, and canonical artifacts
/// -- into the format-neutral subjects and records the domain validates and
/// summarizes. Read-only apart from `recordArtifactContribution`.
[<RequireQualifiedAccess>]
module FileProvenanceRepository =
    let private stringField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private objectField (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonObject as child -> Some child
        | _ -> None

    // ---- policy ------------------------------------------------------------

    /// `ros.json` `provenance`: `{ "enforce": true, "requiredSince": "..." }`.
    /// Absent or unreadable configuration is legacy mode.
    let readPolicy (root: string) : ProvenancePolicy =
        let path = Path.Combine(root, "ros.json")

        if not (File.Exists path) then
            ProvenancePolicy.legacy
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as config ->
                    match objectField config "provenance" with
                    | Some section ->
                        { Enforce =
                            match section["enforce"] with
                            | :? JsonValue as value -> value.GetValueKind() = JsonValueKind.True
                            | _ -> false
                          RequiredSince = stringField section "requiredSince" |> Option.bind Timestamp.tryParse }
                    | None -> ProvenancePolicy.legacy
                | _ -> ProvenancePolicy.legacy
            with _ ->
                ProvenancePolicy.legacy

    // ---- execution identity ---------------------------------------------

    let private identityOf (node: JsonObject) : Identity =
        { Provider = stringField node "provider" |> Option.defaultValue Attribute.UnknownToken
          Model = stringField node "model"
          ModelVersion = stringField node "modelVersion"
          Runtime = stringField node "runtime" |> Option.defaultValue Attribute.UnknownToken
          RuntimeVersion = stringField node "runtimeVersion"
          SessionId = stringField node "sessionId"
          ConversationId = stringField node "conversationId"
          RunId = stringField node "runId"
          AgentId = stringField node "agentId"
          SubagentId = stringField node "subagentId"
          ParentExecutionId = stringField node "parentExecutionId" }

    let private candidateOf (record: JsonObject) : ExecutionCandidate option =
        match stringField record "executionId", objectField record "identity" with
        | Some executionId, Some identity ->
            Some
                { ExecutionId = executionId
                  WorkItemId = stringField record "workItemId"
                  Status = stringField record "status" |> Option.defaultValue "unknown"
                  Identity = identityOf identity
                  ActorKind =
                    objectField record "actor"
                    |> Option.bind (fun actor -> stringField actor "kind")
                    |> Option.bind ActorKind.tryParse }
        | _ -> None

    let readExecutionCandidates (root: string) : ExecutionCandidate list =
        FileTelemetryQueryRepository.readAll root |> List.choose candidateOf

    /// Resolves who the current process is: identity discovered from the
    /// environment exactly as `work begin` discovers it for a new execution
    /// (an explicit `--actor` wins for the stable id), bound to the most
    /// recent compatible active execution unless `ROS_EXECUTION_ID` pins it.
    let currentActor (root: string) (actorIdOverride: string option) : CurrentActor =
        let environment = FileTelemetryExecutionRepository.environmentIdentityInputs ()

        let inputs =
            match actorIdOverride |> Option.filter (fun value -> value.Trim().Length > 0) with
            | Some id -> { environment with AgentId = Some id }
            | None -> environment

        let identity, source = Identity.discover inputs
        let actorInputs = FileTelemetryExecutionRepository.environmentActorInputs ()
        let candidates = readExecutionCandidates root
        let binding = ActorResolution.bindExecution actorInputs identity candidates

        let workItem =
            match binding with
            | ExecutionBinding.Explicit executionId
            | ExecutionBinding.Matched executionId ->
                candidates
                |> List.tryFind (fun candidate -> candidate.ExecutionId = executionId)
                |> Option.bind _.WorkItemId
            | ExecutionBinding.Unbound -> None

        { Actor = ActorResolution.resolve actorInputs (identity, source) binding
          Binding = binding
          Mechanism = source.Mechanism
          WorkItem = workItem }

    let contribution
        (current: CurrentActor)
        (operation: Operation)
        (at: string)
        (workItem: string option)
        (reason: string option)
        (evidence: string list)
        (basis: string option)
        : Contribution =
        { Operation = operation
          At = at
          Actor = current.Actor
          WorkItem = workItem
          Reason = reason
          Evidence = evidence
          Basis = basis }

    // ---- subjects ------------------------------------------------------------

    let private eventsRelativePath = ".ros/events/events.jsonl"

    let private readJsonLines (path: string) =
        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.toList
            |> List.filter (fun line -> line.Trim().Length > 0)
            |> List.choose (fun line ->
                try
                    match JsonNode.Parse line with
                    | :? JsonObject as record -> Some record
                    | _ -> None
                with _ ->
                    None)

    let private declaredOf (node: JsonNode) (shape: ArtifactValue -> Declared) =
        match node with
        | null -> Declared.Absent
        | value -> ProvenanceJson.ofNode value |> Option.map shape |> Option.defaultValue Declared.Absent

    let eventSubjects (root: string) : ProvenanceSubject list =
        readJsonLines (Path.Combine(root, eventsRelativePath))
        |> List.map (fun event ->
            { Kind = RecordKind.Event
              RecordId =
                [ stringField event "type"; stringField event "workItem"; stringField event "eventId" ]
                |> List.choose id
                |> String.concat " "
              Path = eventsRelativePath
              Field = "actor"
              CreatedAt = stringField event "occurredAt"
              Declared = declaredOf event["actor"] Declared.ActorRecord
              LegacyAuthor = None })

    let private queueItems (root: string) =
        let path = Path.Combine(root, ".ros", "work", "queue.json")

        if not (File.Exists path) then
            []
        else
            try
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as queue ->
                    match queue["items"] with
                    | :? JsonArray as items -> items |> Seq.choose (function :? JsonObject as item -> Some item | _ -> None) |> Seq.toList
                    | _ -> []
                | _ -> []
            with _ ->
                []

    let backlogSubjects (root: string) : ProvenanceSubject list =
        queueItems root
        |> List.map (fun item ->
            { Kind = RecordKind.BacklogItem
              RecordId = stringField item "id" |> Option.defaultValue "(no id)"
              Path = ".ros/work/queue.json"
              Field = "provenance"
              CreatedAt = stringField item "createdAt"
              Declared = declaredOf item["provenance"] Declared.ProvenanceRecord
              LegacyAuthor = stringField item "createdBy" |> Option.filter (fun author -> author <> Attribute.UnknownToken) })

    let private loadArtifacts (root: string) =
        match (FileArtifactRepository.create root).Load() with
        | Ok loaded -> loaded.Documents
        | Error _ -> []

    let artifactSubjects (documents: ArtifactDocument list) : ProvenanceSubject list =
        documents
        |> List.map (fun document ->
            let text field =
                ArtifactDocument.tryMetadata field document
                |> Option.map ArtifactValue.display
                |> Option.filter (fun value -> value.Length > 0)

            { Kind = RecordKind.Artifact
              RecordId = ArtifactDocument.identifier document
              Path = document.RelativePath
              Field = "provenance"
              CreatedAt = text "created"
              Declared =
                ArtifactDocument.tryMetadata "provenance" document
                |> Option.map Declared.ProvenanceRecord
                |> Option.defaultValue Declared.Absent
              LegacyAuthor = text "author_agent" })

    // ---- records (for show/summary) ---------------------------------------

    let private provenanceOf (declared: Declared) =
        match declared with
        | Declared.ProvenanceRecord value -> ProvenanceCodec.readProvenance value |> fst
        | Declared.ActorRecord _
        | Declared.Absent -> Provenance.empty

    let private derivedFrom (document: ArtifactDocument) =
        match ArtifactDocument.tryMetadata "derived_from" document with
        | Some(ArtifactValue.Sequence items) -> items |> List.map ArtifactValue.display
        | Some(ArtifactValue.Text value) when value.Length > 0 -> [ value ]
        | _ -> []

    /// Events are single actions: each is one attributable `modified`
    /// contribution to its work item, so actor totals include them.
    let private eventRecord (subject: ProvenanceSubject) =
        let provenance =
            match subject.Declared with
            | Declared.ActorRecord value ->
                let actor, _ = ProvenanceCodec.readActor "" value

                { Contributions =
                    [ { Operation = Operation.Modified
                        At = subject.CreatedAt |> Option.defaultValue ""
                        Actor = actor
                        WorkItem = None
                        Reason = Some subject.RecordId
                        Evidence = []
                        Basis = None } ] }
            | _ -> Provenance.empty

        { Kind = subject.Kind
          RecordId = subject.RecordId
          Path = subject.Path
          Provenance = provenance
          DerivedFrom = [] }

    let attributedRecords (root: string) : AttributedRecord list =
        let documents = loadArtifacts root

        let artifacts =
            List.zip documents (artifactSubjects documents)
            |> List.map (fun (document, subject) ->
                { Kind = subject.Kind
                  RecordId = subject.RecordId
                  Path = subject.Path
                  Provenance = provenanceOf subject.Declared
                  DerivedFrom = derivedFrom document })

        let backlog =
            backlogSubjects root
            |> List.map (fun subject ->
                { Kind = subject.Kind
                  RecordId = subject.RecordId
                  Path = subject.Path
                  Provenance = provenanceOf subject.Declared
                  DerivedFrom = [] })

        artifacts @ backlog @ (eventSubjects root |> List.map eventRecord)

    // ---- validation ------------------------------------------------------------

    /// An event whose actor names a Praxis execution this repository has no
    /// record of cannot be traced back to its run.
    let private executionReferenceFindings (root: string) =
        let known = readExecutionCandidates root |> List.map _.ExecutionId |> Set.ofList

        eventSubjects root
        |> List.choose (fun subject ->
            match subject.Declared with
            | Declared.ActorRecord value ->
                match ProvenanceCodec.readActor "" value |> fst |> _.ExecutionId with
                | Attribute.Known executionId when executionId.StartsWith("EXE-", StringComparison.Ordinal) && not (known.Contains executionId) ->
                    Some
                        { Severity = Severity.Warning
                          Path = subject.Path
                          Field = "actor.executionId"
                          Code = "unknown-execution"
                          Message = $"event {subject.RecordId}: execution '{executionId}' has no telemetry record in this repository" }
                | _ -> None
            | _ -> None)

    /// For each canonical artifact whose working-tree content differs from
    /// its committed content, the committed contributions must survive as a
    /// prefix: provenance is append-only.
    let private preservationFindings (root: string) (policy: ProvenancePolicy) (documents: ArtifactDocument list) =
        documents
        |> List.collect (fun document ->
            match ProcessGitRepository.readCommittedFile root document.RelativePath with
            | None -> []
            | Some committed ->
                let current = File.ReadAllText(Path.Combine(root, document.RelativePath))

                if committed = current then
                    []
                else
                    match FrontMatterProvenance.read document.RelativePath committed, FrontMatterProvenance.read document.RelativePath current with
                    | Ok(before, _), Ok(after, _) ->
                        ProvenanceValidation.preservation document.RelativePath "provenance" before after
                        @ ProvenanceValidation.unrecordedModification policy document.RelativePath "provenance" before after
                    | _ -> [])

    let findings (root: string) : ProvenanceFinding list =
        let policy = readPolicy root
        let documents = loadArtifacts root

        let subjects = eventSubjects root @ backlogSubjects root @ artifactSubjects documents

        ProvenanceValidation.validate policy subjects
        @ executionReferenceFindings root
        @ preservationFindings root policy documents

    // ---- artifact contribution effect ------------------------------------

    /// Appends one contribution to a canonical artifact's front matter. The
    /// operation defaults from the repository's own history: a file with no
    /// committed version is `created`; any committed file -- including a
    /// legacy one with no provenance -- is `modified`.
    let recordArtifactContribution
        (root: string)
        (relativePath: string)
        (explicitOperation: Operation option)
        (build: Operation -> Contribution)
        : Result<Contribution * bool, string> =
        let normalized = relativePath.Replace('\\', '/')
        let fullPath = Path.Combine(root, normalized)

        if not (File.Exists fullPath) then
            Error $"'{normalized}' does not exist"
        else
            let text = File.ReadAllText fullPath

            match FrontMatterProvenance.read normalized text with
            | Error message -> Error $"'{normalized}': {message}"
            | Ok(existing, _) ->
                let isNew = (ProcessGitRepository.readCommittedFile root normalized).IsNone
                let operation = explicitOperation |> Option.defaultValue (Provenance.defaultOperation isNew existing)
                let contribution = build operation

                match Provenance.originalCreator existing with
                | Some creator when operation = Operation.Created && creator.Actor <> contribution.Actor ->
                    Error $"'{normalized}' already records its creator ({Actor.describe creator.Actor}); record 'modified' instead"
                | _ ->
                match FrontMatterProvenance.append normalized text contribution with
                | Error message -> Error $"'{normalized}': {message}"
                | Ok updated when updated = text -> Ok(contribution, false)
                | Ok updated ->
                    File.WriteAllText(fullPath, updated)
                    Ok(contribution, true)
