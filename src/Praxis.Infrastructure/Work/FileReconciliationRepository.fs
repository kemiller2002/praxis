namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Git
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Json

/// What `work reconcile` was asked to do, already parsed.
type ReconciliationCommand =
    { WorkItemId: string
      Reason: string
      Selectors: GitEvidenceSelector list
      PathRestriction: string list
      OccurredAt: string
      Actor: Actor
      DryRun: bool }

[<RequireQualifiedAccess>]
type ReconciliationOutcome =
    | Recorded of plan: ReconciliationPlan * eventId: string
    | Planned of ReconciliationPlan
    | NothingToReconcile of GitReconciliationEvidence * ReconciliationAssessment list
    | Rejected of ReconciliationRejection list

/// Everything the event log currently says about attribution, split by kind
/// so the contemporaneous/post-hoc distinction is never lost.
type RecordedAttribution =
    { /// Paths named by any event other than a reconciliation event.
      Contemporaneous: Set<string>
      /// Reconciliation events that passed every validation check.
      Reconciliations: ReconciliationRecord list
      /// Problems with reconciliation events; each is a validation error.
      Findings: WorkAttributionFinding list }

[<RequireQualifiedAccess>]
module RecordedAttribution =
    /// The set validation treats as attributed among `observedPaths`:
    /// contemporaneous paths, plus reconciled paths whose current content
    /// the valid reconciliation evidence vouches for (see
    /// `ReconciliationCoverage`). An invalid reconciliation event, or a
    /// reconciled path changed again since, attributes nothing.
    let attributedPaths (readStates: string list -> Map<string, PathContentState>) (observedPaths: string list) (attribution: RecordedAttribution) =
        let accepted = ReconciliationCoverage.acceptedStates attribution.Reconciliations

        let candidates =
            observedPaths
            |> List.filter (fun path -> accepted.ContainsKey path && not (attribution.Contemporaneous.Contains path))

        match candidates with
        | [] -> attribution.Contemporaneous
        | _ -> ReconciliationCoverage.covered accepted (readStates candidates) |> Set.union attribution.Contemporaneous

/// Reads and appends `work.attribution.reconciled` events in
/// `.ros/events/events.jsonl` (issue #80). Appending goes through the same
/// `work-protocol` lock and `work-state` recovery journal every other event
/// writer uses, so it never interleaves with a concurrent transition.
[<RequireQualifiedAccess>]
module FileReconciliationRepository =
    let private eventsRelativePath = ".ros/events/events.jsonl"
    let private contextRelativePath = ".ros/context/current.json"

    let private compactOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private eventLines (root: string) =
        let path = Path.Combine(root, eventsRelativePath)

        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.toList
            |> List.mapi (fun index line -> index + 1, line)
            |> List.filter (fun (_, line) -> line.Trim().Length > 0)

    let private eventId (node: JsonObject) =
        match node["eventId"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, id -> Some id
            | _ -> None
        | _ -> None

    /// An event's `eventId` is the content hash of every field written
    /// before it; recomputing it proves the stored event was not edited.
    let private integrityVerified (node: JsonObject) =
        match eventId node with
        | None -> false
        | Some stored ->
            let content = node.DeepClone() :?> JsonObject
            content.Remove "eventId" |> ignore
            CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact content) = stored

    let private stringPaths (node: JsonObject) =
        match node["paths"] with
        | :? JsonArray as paths ->
            paths
            |> Seq.choose (fun entry ->
                match entry with
                | :? JsonValue as value ->
                    match value.TryGetValue<string>() with
                    | true, path -> Some path
                    | _ -> None
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private parseEventObject (line: int) (text: string) =
        try
            match JsonNode.Parse text with
            | :? JsonObject as node -> Ok node
            | _ -> Error(ReconciliationEventRead.Malformed(line, "event must be a JSON object"))
        with :? JsonException ->
            Error(ReconciliationEventRead.Malformed(line, "event is not valid JSON"))

    /// Every reconciliation event in the log, parsed and integrity-checked.
    let readEvents (root: string) : ReconciliationEventRead list =
        eventLines root
        |> List.choose (fun (line, text) ->
            match parseEventObject line text with
            | Ok node when ReconciliationEventContract.isReconciliation node ->
                Some(ReconciliationEventContract.read line (integrityVerified node) node)
            | Ok _ -> None
            | Error malformed -> Some malformed)

    /// Paths named by every event that is not a reconciliation event.
    let readContemporaneousPaths (root: string) : Set<string> =
        eventLines root
        |> List.collect (fun (_, text) ->
            match parseEventObject 0 text with
            | Ok node when not (ReconciliationEventContract.isReconciliation node) -> stringPaths node
            | _ -> [])
        |> Set.ofList

    let private parseSemanticState (value: string) =
        match value with
        | "ready" -> Some LiveWorkState.Ready
        | "active" -> Some LiveWorkState.Active
        | "blocked" -> Some LiveWorkState.Blocked
        | "complete" -> Some LiveWorkState.Complete
        | _ -> None

    let private liveStates (root: string) : Map<string, LiveWorkState> =
        let path = Path.Combine(root, contextRelativePath)

        if not (File.Exists path) then
            Map.empty
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as context ->
                match context["workItems"] with
                | :? JsonArray as items ->
                    items
                    |> Seq.choose (fun node ->
                        match node with
                        | :? JsonObject as item ->
                            match item["id"], item["semanticState"] with
                            | (:? JsonValue as id), (:? JsonValue as state) ->
                                match id.TryGetValue<string>(), state.TryGetValue<string>() with
                                | (true, idText), (true, stateText) ->
                                    parseSemanticState stateText |> Option.map (fun parsed -> idText, parsed)
                                | _ -> None
                            | _ -> None
                        | _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty
            | _ -> Map.empty

    /// Looks a work item up in live work context first (it is authoritative
    /// once work has begun), then in the backlog.
    let readTargets (root: string) : string -> ReconciliationTarget =
        let live = liveStates root

        let backlog =
            FileBacklogQueueRepository.readItems root |> List.map (fun item -> item.Id, item.Status) |> Map.ofList

        fun id ->
            match Map.tryFind id live, Map.tryFind id backlog with
            | Some state, _ -> ReconciliationTarget.Live state
            | None, Some status -> ReconciliationTarget.Backlog status
            | None, None -> ReconciliationTarget.Unknown

    /// Work context and the backlog are only read when there is a
    /// reconciliation event to check, so repositories that never reconcile
    /// see exactly the reads they saw before.
    let readAttribution (root: string) : RecordedAttribution =
        let assessed =
            match readEvents root with
            | [] -> { Findings = []; Valid = [] }
            | reads -> ReconciliationValidation.assess (readTargets root) reads

        { Contemporaneous = readContemporaneousPaths root
          Reconciliations = assessed.Valid
          Findings = assessed.Findings }

    /// Every reconciliation recorded for one work item, each paired with
    /// whether validation accepts it.
    let readForWorkItem (root: string) (workItemId: string) : (ReconciliationRecord * bool) list =
        let reads = readEvents root

        let mine =
            reads
            |> List.choose (function
                | ReconciliationEventRead.Parsed record when record.WorkItemId = workItemId -> Some record
                | _ -> None)

        match mine with
        | [] -> []
        | _ ->
            let valid = (ReconciliationValidation.assess (readTargets root) reads).Valid |> List.map _.EventId |> Set.ofList
            mine |> List.map (fun record -> record, valid.Contains record.EventId)

    let private writeAtomic (file: string) (content: string) =
        let directory = Path.GetDirectoryName file
        Directory.CreateDirectory directory |> ignore
        let temporary = Path.Combine(directory, $".{Path.GetFileName file}.{Guid.NewGuid():N}.tmp")
        File.WriteAllText(temporary, content, UTF8Encoding(false))
        File.Move(temporary, file, true)

    let private appendEvent (root: string) (event: JsonObject) : Result<unit, string> =
        let eventsPath = Path.Combine(root, eventsRelativePath)
        let contextPath = Path.Combine(root, contextRelativePath)
        let current = if File.Exists eventsPath then File.ReadAllText eventsPath else ""
        let separator = if current.Length > 0 && not (current.EndsWith "\n") then "\n" else ""
        let eventsContent = $"{current}{separator}{event.ToJsonString compactOptions}\n"

        if File.Exists contextPath then
            let writes: WorkStateWrite list =
                [ { Path = eventsRelativePath; Content = eventsContent }
                  { Path = contextRelativePath; Content = File.ReadAllText contextPath } ]

            match WorkStateTransaction.prepare root writes with
            | Error failure -> Error failure.Message
            | Ok() -> WorkStateTransaction.recover root |> Result.mapError _.Message
        else
            writeAtomic eventsPath eventsContent
            Ok()

    let private eventFor (root: string) (command: ReconciliationCommand) (plan: ReconciliationPlan) =
        let node =
            ReconciliationEventContract.node
                (FileWorkConfigRepository.readRepositoryId root)
                (FileWorkConfigRepository.readProtocolVersion root)
                command.OccurredAt
                command.Actor
                plan

        let id = CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact node)
        node["eventId"] <- JsonValue.Create id
        node, id

    let private decide (root: string) (history: GitHistory) (command: ReconciliationCommand) =
        let attribution = readAttribution root

        WorkReconciliationOperations.plan history command.Selectors (fun evidence ->
            { WorkItemId = command.WorkItemId
              Target = readTargets root command.WorkItemId
              Reason = command.Reason
              OccurredAt = command.OccurredAt
              Evidence = evidence
              PathRestriction = command.PathRestriction
              PathFilterConfig = FileWorkConfigRepository.readPathFilterConfig root
              // Only contemporaneous attribution short-circuits a path here:
              // a reconciled path changed again by a later commit is new work
              // that needs its own evidence, and the `(commit, path)` claims
              // below make re-reconciling the same change idempotent.
              AttributedPaths = attribution.Contemporaneous
              ExistingClaims = ReconciliationValidation.claimsOf attribution.Reconciliations })

    /// Decides and, unless it is a dry run, records one reconciliation. The
    /// decision reads the event log under the same lock that guards the
    /// append, so two concurrent reconciliations cannot both claim a change.
    let reconcile (root: string) (history: GitHistory) (command: ReconciliationCommand) : Result<ReconciliationOutcome, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    match WorkStateTransaction.recover root with
                    | Error failure -> Error failure.Message
                    | Ok() ->
                        match decide root history command with
                        | ReconciliationDecision.Rejected rejections -> Ok(ReconciliationOutcome.Rejected rejections)
                        | ReconciliationDecision.NothingToReconcile(evidence, assessments) ->
                            Ok(ReconciliationOutcome.NothingToReconcile(evidence, assessments))
                        | ReconciliationDecision.Reconcile plan when command.DryRun -> Ok(ReconciliationOutcome.Planned plan)
                        | ReconciliationDecision.Reconcile plan ->
                            let node, id = eventFor root command plan
                            appendEvent root node |> Result.map (fun () -> ReconciliationOutcome.Recorded(plan, id))
                with error ->
                    Error error.Message

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, outcome -> outcome
