namespace Ros.Contracts.Provenance

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts
open Ros.Domain.Artifacts
open Ros.Domain.Provenance

/// The canonical JSON serialization of `praxis.actor/1` and
/// `praxis.provenance/1`, shared by events, backlog items, execution
/// records, handoffs, and every CLI projection so provenance crosses each
/// boundary in one shape.
[<RequireQualifiedAccess>]
module ProvenanceJson =
    let rec toNode (value: OrderedValue) : JsonNode =
        match value with
        | OrderedValue.Text text -> JsonValue.Create text
        | OrderedValue.List items ->
            let array = JsonArray()
            items |> List.iter (fun item -> array.Add(toNode item))
            array
        | OrderedValue.Fields fields ->
            let node = JsonObject()
            fields |> List.iter (fun (name, item) -> node[name] <- toNode item)
            node

    let actorNode (actor: Actor) = ProvenanceCodec.actorValue actor |> toNode :?> JsonObject

    let contributionNode (contribution: Contribution) =
        ProvenanceCodec.contributionValue contribution |> toNode :?> JsonObject

    let provenanceNode (provenance: Provenance) =
        ProvenanceCodec.provenanceValue provenance |> toNode :?> JsonObject

    /// JSON into the format-neutral tree the provenance reader consumes. A
    /// JSON `null` is absence, never the text "null".
    let rec ofNode (node: JsonNode) : ArtifactValue option =
        match node with
        | null -> None
        | :? JsonObject as record ->
            record
            |> Seq.choose (fun pair -> ofNode pair.Value |> Option.map (fun value -> pair.Key, value))
            |> Map.ofSeq
            |> ArtifactValue.Mapping
            |> Some
        | :? JsonArray as items -> items |> Seq.choose ofNode |> Seq.toList |> ArtifactValue.Sequence |> Some
        | :? JsonValue as scalar ->
            match scalar.GetValueKind() with
            | JsonValueKind.String -> Some(ArtifactValue.Text(scalar.GetValue<string>()))
            | JsonValueKind.Number -> Some(ArtifactValue.Number(scalar.GetValue<float>()))
            | JsonValueKind.True -> Some(ArtifactValue.Boolean true)
            | JsonValueKind.False -> Some(ArtifactValue.Boolean false)
            | _ -> None
        | _ -> None

    /// Appends one contribution to a JSON record's `provenance` block,
    /// creating the block when the record has none. Existing contributions
    /// are never touched; an identical retried contribution is not
    /// duplicated. A malformed existing block is refused rather than
    /// overwritten, so no prior contributor is ever lost.
    let appendContribution (record: JsonObject) (contribution: Contribution) : Result<unit, string> =
        let existing =
            match record["provenance"] with
            | null -> Ok Provenance.empty
            | node ->
                match ofNode node with
                | Some value ->
                    match ProvenanceCodec.readProvenance value with
                    | provenance, [] -> Ok provenance
                    | _, issues when issues |> List.forall (fun issue -> issue.Code = "empty-provenance") -> Ok Provenance.empty
                    | _, issue :: _ -> Error $"existing provenance is malformed ({issue.Field}: {issue.Message}); repair it before recording a new contribution"
                | None -> Ok Provenance.empty

        existing
        |> Result.map (fun provenance ->
            let updated = Provenance.append contribution provenance

            if updated.Contributions.Length <> provenance.Contributions.Length then
                match record["provenance"] with
                | :? JsonObject as block ->
                    match block["contributions"] with
                    | :? JsonArray as contributions -> contributions.Add(contributionNode contribution: JsonNode)
                    | _ -> block["contributions"] <- JsonArray(contributionNode contribution)
                | _ -> record["provenance"] <- provenanceNode updated)

    let private writeActorFields (writer: Utf8JsonWriter) (actor: Actor) =
        Actor.fields actor |> List.iter (fun (name, value) -> writer.WriteString(name, value))

    let renderFindings (findings: ProvenanceFinding list) =
        let count severity = findings |> List.filter (fun finding -> finding.Severity = severity) |> List.length

        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteBoolean("valid", count Severity.Error = 0)
            writer.WriteNumber("errors", count Severity.Error)
            writer.WriteNumber("warnings", count Severity.Warning)
            writer.WriteNumber("info", count Severity.Info)
            writer.WriteStartArray("findings")

            for finding in findings do
                writer.WriteStartObject()
                writer.WriteString("severity", Severity.code finding.Severity)
                writer.WriteString("path", finding.Path)
                writer.WriteString("field", finding.Field)
                writer.WriteString("code", finding.Code)
                writer.WriteString("message", finding.Message)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())

    let private bindingCode binding =
        match binding with
        | ExecutionBinding.Explicit _ -> "explicit"
        | ExecutionBinding.Matched _ -> "matched-active-execution"
        | ExecutionBinding.Unbound -> "unbound"

    /// `identity` output: the actor Praxis will stamp on everything this
    /// process records, how its execution was bound, and where identity was
    /// discovered from.
    let renderIdentity (actor: Actor) (binding: ExecutionBinding) (discoveryMechanism: string) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", Actor.SchemaVersion)
            writer.WriteStartObject("actor")
            writeActorFields writer actor
            writer.WriteEndObject()
            writer.WriteString("executionBinding", bindingCode binding)
            writer.WriteString("discovery", discoveryMechanism)
            writer.WriteString("assurance", "self-reported provenance; not authentication or attestation")
            writer.WriteEndObject())

    let renderRecord (record: AttributedRecord) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("schema", Provenance.SchemaVersion)
            writer.WriteString("kind", RecordKind.code record.Kind)
            writer.WriteString("id", record.RecordId)
            writer.WriteString("path", record.Path)

            match Provenance.originalCreator record.Provenance with
            | Some creator ->
                writer.WriteStartObject("creator")
                writeActorFields writer creator.Actor
                writer.WriteEndObject()
            | None -> writer.WriteNull("creator")

            writer.WriteStartArray("contributors")

            for actor in Provenance.contributors record.Provenance do
                writer.WriteStartObject()
                writeActorFields writer actor
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("involvement")
            Provenance.involvementLabels record.Provenance |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteStartArray("derivedFrom")
            record.DerivedFrom |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WritePropertyName("provenance")
            (provenanceNode record.Provenance).WriteTo(writer)
            writer.WriteEndObject())

    let renderSummary (summary: ProvenanceSummary) =
        let stringArray (writer: Utf8JsonWriter) (name: string) (values: string list) =
            writer.WriteStartArray(name)
            values |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("records", summary.Records)
            writer.WriteNumber("attributedRecords", summary.AttributedRecords)
            writer.WriteNumber("unattributedRecords", summary.UnattributedRecords)
            writer.WriteStartArray("actors")

            for totals in summary.Actors do
                writer.WriteStartObject()
                writer.WriteString("kind", totals.Kind)
                writer.WriteString("id", totals.Id)
                stringArray writer "providers" totals.Providers
                stringArray writer "models" totals.Models
                stringArray writer "executions" totals.Executions
                writer.WriteNumber("recordsCreated", totals.RecordsCreated)
                writer.WriteNumber("recordsModified", totals.RecordsModified)
                writer.WriteStartArray("operations")

                for KeyValue((recordKind, operation), count) in totals.Operations do
                    writer.WriteStartObject()
                    writer.WriteString("recordKind", recordKind)
                    writer.WriteString("operation", operation)
                    writer.WriteNumber("count", count)
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()
            stringArray writer "humanCorrectionsOfAgentWork" summary.HumanCorrectionsOfAgentWork
            stringArray writer "agentToAgentRevisions" summary.AgentToAgentRevisions
            stringArray writer "humanApprovedAgentWork" summary.HumanApprovedAgentWork
            writer.WriteStartArray("derivedRecords")

            for id, sources in summary.DerivedRecords do
                writer.WriteStartObject()
                writer.WriteString("id", id)
                stringArray writer "derivedFrom" sources
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("hotspots")

            for id, count in summary.Hotspots do
                writer.WriteStartObject()
                writer.WriteString("id", id)
                writer.WriteNumber("contributions", count)
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject())
