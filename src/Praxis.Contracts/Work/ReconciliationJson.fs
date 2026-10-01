namespace Praxis.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts
open Praxis.Contracts.Provenance
open Praxis.Domain.Git
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// The durable form of a post-hoc attribution reconciliation: one
/// `work.attribution.reconciled` event in `.ros/events/events.jsonl`.
///
/// ```
/// {"schemaVersion":"1.0.0","type":"work.attribution.reconciled",
///  "workItem":"GH-80","repository":"...","protocolVersion":"1.0.0",
///  "occurredAt":"...","attribution":"post-hoc","reason":"...",
///  "paths":["src/a.fs"],
///  "gitEvidence":{"head":"<sha>",
///    "selectors":[{"type":"commit","ref":"abc123","head":"<sha>","commits":["<sha>"]}],
///    "commits":[{"sha":"<sha>","parents":["<sha>"],
///      "author":{"name":"...","email":"...","date":"..."},
///      "committer":{"name":"...","email":"...","date":"..."},
///      "subject":"...",
///      "changes":[{"status":"added","path":"src/a.fs","blob":"<sha>"}]}]},
///  "actor":{...},"publication":{"status":"pending"},"eventId":"..."}
/// ```
///
/// `paths` is the same durable file-attribution field every other work
/// event uses, so every event-log consumer that already understands
/// attribution sees these paths. `gitEvidence` deliberately does not reuse
/// the `evidence` name: on work events that field is a list of completion
/// evidence files, a different concept. `actor` is the reconciliation actor;
/// each commit's `author`/`committer` is the change author as Git recorded
/// it. Keys are always written in this order because `eventId` hashes the
/// serialized event.
[<RequireQualifiedAccess>]
module ReconciliationEventContract =
    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let private personNode (person: GitPerson) =
        let node = JsonObject()
        node["name"] <- JsonValue.Create person.Name
        node["email"] <- JsonValue.Create person.Email
        node["date"] <- JsonValue.Create person.Date
        node

    let private changeNode (change: GitCommitChange) =
        let node = JsonObject()
        node["status"] <- JsonValue.Create(GitCommitChangeKind.code change.Kind)
        node["path"] <- JsonValue.Create change.Path
        change.OriginalPath |> Option.iter (fun original -> node["originalPath"] <- JsonValue.Create original)
        change.Blob |> Option.iter (fun blob -> node["blob"] <- JsonValue.Create blob)
        node

    let commitNode (commit: GitCommit) =
        let node = JsonObject()
        node["sha"] <- JsonValue.Create commit.Sha
        node["parents"] <- strings commit.Parents
        node["author"] <- personNode commit.Author
        node["committer"] <- personNode commit.Committer
        node["subject"] <- JsonValue.Create commit.Subject
        let changes = JsonArray()
        commit.Changes |> List.iter (fun change -> changes.Add(changeNode change: JsonNode))
        node["changes"] <- changes
        node

    let private selectorNode (selector: ResolvedEvidenceSelector) =
        let node = JsonObject()

        node["type"] <-
            JsonValue.Create(
                match selector.Selector with
                | GitEvidenceSelector.Commit _ -> "commit"
                | GitEvidenceSelector.Range _ -> "range"
            )

        node["ref"] <- JsonValue.Create(GitEvidenceSelector.text selector.Selector)
        selector.BaseCommit |> Option.iter (fun baseCommit -> node["base"] <- JsonValue.Create baseCommit)
        node["head"] <- JsonValue.Create selector.HeadCommit
        node["commits"] <- strings selector.Commits
        node

    /// The event without its `eventId`; the caller hashes this exact node
    /// and appends the id last, like every other event writer.
    let node (repository: string) (protocolVersion: string) (occurredAt: string) (actor: Actor) (plan: ReconciliationPlan) =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"
        node["type"] <- JsonValue.Create WorkReconciliation.EventType
        node["workItem"] <- JsonValue.Create plan.WorkItemId
        node["repository"] <- JsonValue.Create repository
        node["protocolVersion"] <- JsonValue.Create protocolVersion
        node["occurredAt"] <- JsonValue.Create occurredAt
        node["attribution"] <- JsonValue.Create WorkReconciliation.PostHoc
        node["reason"] <- JsonValue.Create plan.Reason
        node["paths"] <- strings plan.ReconciledPaths
        let evidence = JsonObject()
        evidence["head"] <- JsonValue.Create plan.Evidence.Head
        let selectors = JsonArray()
        plan.Evidence.Selectors |> List.iter (fun selector -> selectors.Add(selectorNode selector: JsonNode))
        evidence["selectors"] <- selectors
        let commits = JsonArray()
        plan.RecordedCommits |> List.iter (fun commit -> commits.Add(commitNode commit: JsonNode))
        evidence["commits"] <- commits
        node["gitEvidence"] <- evidence
        node["actor"] <- ActorJson.node actor
        let publication = JsonObject()
        publication["status"] <- JsonValue.Create "pending"
        node["publication"] <- publication
        node

    // ---- reading back ----------------------------------------------------

    let private text (node: JsonNode) (name: string) : Result<string, string> =
        match node with
        | :? JsonObject as item ->
            match item[name] with
            | :? JsonValue as value ->
                match value.TryGetValue<string>() with
                | true, result -> Ok result
                | _ -> Error $"'{name}' must be a string"
            | _ -> Error $"'{name}' is required"
        | _ -> Error "expected an object"

    let private optionalText (node: JsonNode) (name: string) =
        match text node name with
        | Ok value -> Some value
        | Error _ -> None

    let private traverse (parse: 'input -> Result<'output, string>) (inputs: 'input list) =
        List.foldBack
            (fun input state ->
                match parse input, state with
                | Ok value, Ok values -> Ok(value :: values)
                | Error message, _
                | _, Error message -> Error message)
            inputs
            (Ok [])

    let private items (node: JsonNode) (name: string) : Result<JsonNode list, string> =
        match node with
        | :? JsonObject as item ->
            match item[name] with
            | :? JsonArray as array -> Ok(array |> Seq.toList)
            | _ -> Error $"'{name}' must be an array"
        | _ -> Error "expected an object"

    let private textItems (node: JsonNode) (name: string) =
        items node name
        |> Result.bind (
            traverse (fun entry ->
                match entry with
                | :? JsonValue as value ->
                    match value.TryGetValue<string>() with
                    | true, result -> Ok result
                    | _ -> Error $"'{name}' must contain only strings"
                | _ -> Error $"'{name}' must contain only strings")
        )

    let private objectField (node: JsonNode) (name: string) : Result<JsonNode, string> =
        match node with
        | :? JsonObject as item ->
            match item[name] with
            | :? JsonObject as value -> Ok(value :> JsonNode)
            | _ -> Error $"'{name}' must be an object"
        | _ -> Error "expected an object"

    let private person (node: JsonNode) : Result<GitPerson, string> =
        match text node "name", text node "email", text node "date" with
        | Ok name, Ok email, Ok date -> Ok { Name = name; Email = email; Date = date }
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> Error message

    let private change (node: JsonNode) : Result<GitCommitChange, string> =
        text node "status"
        |> Result.bind (fun status ->
            match GitCommitChangeKind.tryParse status with
            | None -> Error $"unknown change status '{status}'"
            | Some kind ->
                text node "path"
                |> Result.map (fun path ->
                    { Kind = kind
                      Path = path
                      OriginalPath = optionalText node "originalPath"
                      Blob = optionalText node "blob" }))

    let private commit (node: JsonNode) : Result<GitCommit, string> =
        match
            text node "sha",
            textItems node "parents",
            objectField node "author" |> Result.bind person,
            objectField node "committer" |> Result.bind person,
            text node "subject",
            items node "changes" |> Result.bind (traverse change)
        with
        | Ok sha, Ok parents, Ok author, Ok committer, Ok subject, Ok changes ->
            Ok
                { Sha = sha
                  Parents = parents
                  Author = author
                  Committer = committer
                  Subject = subject
                  Changes = changes }
        | Error message, _, _, _, _, _
        | _, Error message, _, _, _, _
        | _, _, Error message, _, _, _
        | _, _, _, Error message, _, _
        | _, _, _, _, Error message, _
        | _, _, _, _, _, Error message -> Error $"commit: {message}"

    let private selector (node: JsonNode) : Result<ResolvedEvidenceSelector, string> =
        match text node "type", text node "ref", text node "head", textItems node "commits" with
        | Ok kind, Ok reference, Ok head, Ok commits ->
            let parsed =
                match kind with
                | "commit" -> GitEvidenceSelector.tryParseCommit reference
                | "range" -> GitEvidenceSelector.tryParseRange reference
                | other -> Error $"unknown selector type '{other}'"

            parsed
            |> Result.map (fun selector ->
                { Selector = selector
                  BaseCommit = optionalText node "base"
                  HeadCommit = head
                  Commits = commits })
        | Error message, _, _, _
        | _, Error message, _, _
        | _, _, Error message, _
        | _, _, _, Error message -> Error $"selector: {message}"

    let private evidence (node: JsonNode) : Result<GitReconciliationEvidence, string> =
        match text node "head", items node "selectors" |> Result.bind (traverse selector), items node "commits" |> Result.bind (traverse commit) with
        | Ok head, Ok selectors, Ok commits ->
            Ok
                { Head = head
                  Selectors = selectors
                  Commits = commits }
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> Error $"gitEvidence: {message}"

    let isReconciliation (node: JsonObject) =
        match node["type"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, kind -> kind = WorkReconciliation.EventType
            | _ -> false
        | _ -> false

    /// Reads one stored reconciliation event. `integrityVerified` is decided
    /// by the caller, which owns the content-hash primitive.
    let read (line: int) (integrityVerified: bool) (node: JsonObject) : ReconciliationEventRead =
        let actor =
            match ActorJson.tryParse node["actor"] with
            | Ok actor -> Ok actor
            | Error message -> Error $"actor: {message}"

        match
            text node "eventId",
            text node "workItem",
            text node "occurredAt",
            text node "attribution",
            textItems node "paths",
            objectField node "gitEvidence" |> Result.bind evidence,
            actor
        with
        | Ok eventId, Ok workItem, Ok occurredAt, Ok attribution, Ok paths, Ok gitEvidence, Ok actor ->
            ReconciliationEventRead.Parsed
                { EventId = eventId
                  WorkItemId = workItem
                  OccurredAt = occurredAt
                  Attribution = attribution
                  Reason = optionalText node "reason" |> Option.defaultValue ""
                  Paths = paths
                  Evidence = gitEvidence
                  Actor = actor
                  IntegrityVerified = integrityVerified }
        | Error message, _, _, _, _, _, _
        | _, Error message, _, _, _, _, _
        | _, _, Error message, _, _, _, _
        | _, _, _, Error message, _, _, _
        | _, _, _, _, Error message, _, _
        | _, _, _, _, _, Error message, _
        | _, _, _, _, _, _, Error message -> ReconciliationEventRead.Malformed(line, message)

/// The summary `work show` prints for each reconciliation of a work item,
/// so post-hoc attribution stays visible next to the item itself.
[<RequireQualifiedAccess>]
module ReconciliationSummaryContract =
    let node (record: ReconciliationRecord) (valid: bool) : JsonObject =
        let node = JsonObject()
        node["eventId"] <- JsonValue.Create record.EventId
        node["attribution"] <- JsonValue.Create record.Attribution
        node["occurredAt"] <- JsonValue.Create record.OccurredAt
        node["reason"] <- JsonValue.Create record.Reason
        node["valid"] <- JsonValue.Create valid

        let paths = JsonArray()
        record.Paths |> List.iter (fun path -> paths.Add(JsonValue.Create path: JsonNode))
        node["paths"] <- paths

        let commits = JsonArray()

        record.Evidence.Commits
        |> List.iter (fun commit ->
            let entry = JsonObject()
            entry["sha"] <- JsonValue.Create commit.Sha
            entry["author"] <- JsonValue.Create $"{commit.Author.Name} <{commit.Author.Email}>"
            entry["committedAt"] <- JsonValue.Create commit.Committer.Date
            commits.Add(entry: JsonNode))

        node["commits"] <- commits

        node["actor"] <-
            match record.Actor with
            | Some actor -> ActorJson.node actor :> JsonNode
            | None -> null

        node

/// `work reconcile --json` output.
[<RequireQualifiedAccess>]
module ReconciliationOutputContract =
    let private writePerson (writer: Utf8JsonWriter) (name: string) (person: GitPerson) =
        writer.WriteStartObject name
        writer.WriteString("name", person.Name)
        writer.WriteString("email", person.Email)
        writer.WriteString("date", person.Date)
        writer.WriteEndObject()

    let renderJson
        (status: string)
        (workItemId: string)
        (eventId: string option)
        (evidence: GitReconciliationEvidence option)
        (paths: string list)
        (assessments: ReconciliationAssessment list)
        =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("status", status)
            writer.WriteString("workItem", workItemId)
            writer.WriteString("attribution", WorkReconciliation.PostHoc)

            match eventId with
            | Some id -> writer.WriteString("eventId", id)
            | None -> writer.WriteNull "eventId"

            match evidence with
            | Some value -> writer.WriteString("head", value.Head)
            | None -> writer.WriteNull "head"

            writer.WriteStartArray "paths"
            paths |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

            writer.WriteStartArray "commits"

            for commit in evidence |> Option.map _.Commits |> Option.defaultValue [] do
                writer.WriteStartObject()
                writer.WriteString("sha", commit.Sha)
                writePerson writer "author" commit.Author
                writePerson writer "committer" commit.Committer
                writer.WriteString("subject", commit.Subject)
                writer.WriteEndObject()

            writer.WriteEndArray()

            writer.WriteStartArray "assessments"

            for assessment in assessments do
                writer.WriteStartObject()
                writer.WriteString("commit", assessment.Commit)
                writer.WriteString("path", assessment.Path)
                writer.WriteString("change", GitCommitChangeKind.code assessment.Change.Kind)

                if assessment.Path <> assessment.Change.Path then
                    writer.WriteString("renamedTo", assessment.Change.Path)
                else
                    assessment.Change.OriginalPath |> Option.iter (fun original -> writer.WriteString("originalPath", original))

                writer.WriteString("disposition", ReconciliationDisposition.code assessment.Disposition)

                match assessment.Disposition with
                | ReconciliationDisposition.AlreadyReconciled eventId -> writer.WriteString("eventId", eventId)
                | ReconciliationDisposition.Conflict(workItem, eventId) ->
                    writer.WriteString("workItem", workItem)
                    writer.WriteString("eventId", eventId)
                | _ -> ()

                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())
