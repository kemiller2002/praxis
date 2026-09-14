namespace EchelonFoundry.Ros.Integration

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// A payload that could not become an `ActivityObservation` at all --
/// either the text was not well-formed JSON, or it parsed but failed
/// `ActivityObservation.create`'s structural checks.
type ActivityDeserializationError =
    | MalformedJson of string
    | InvalidActivity of ActivityObservationError list

/// The wire format ROS owns for an `ActivityObservation`. This is the
/// serialized contract itself (see
/// docs/migrations/central-integration/INTEGRATION-CONTRACT-STANDARD.md);
/// every payload this module writes carries its own `contractVersion`
/// field, independent of this package's NuGet version.
[<RequireQualifiedAccess>]
module ActivitySerialization =
    let private writerOptions =
        JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private writeInstant (writer: Utf8JsonWriter) (name: string) (value: DateTimeOffset) =
        writer.WriteString(name, value.ToString("o", CultureInfo.InvariantCulture))

    let private writeEvidence (writer: Utf8JsonWriter) (evidence: Evidence list) =
        writer.WriteStartArray "evidence"

        for item in evidence do
            writer.WriteStartObject()
            writer.WriteString("kind", item.Kind)
            writer.WriteString("reference", item.Reference)
            writer.WriteEndObject()

        writer.WriteEndArray()

    /// Serializes an already-validated `ActivityObservation` to its
    /// canonical JSON wire shape. There is no lossy or partial mode: an
    /// `ActivityObservation` only exists via `ActivityObservation.create`,
    /// so every value here is guaranteed structurally valid already.
    let serializeActivity (activity: ActivityObservation) : string =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, writerOptions)

        writer.WriteStartObject()
        writer.WriteString("contractVersion", ContractVersion.value activity.ContractVersion)
        writer.WriteString("activityId", ActivityId.value activity.ActivityId)
        writer.WriteString("organizationId", OrganizationId.value activity.OrganizationId)
        writer.WriteString("projectId", ProjectId.value activity.ProjectId)
        activity.RepositoryId |> Option.iter (fun v -> writer.WriteString("repositoryId", RepositoryId.value v))
        activity.WorkItemId |> Option.iter (fun v -> writer.WriteString("workItemId", WorkItemId.value v))
        activity.ActorId |> Option.iter (fun v -> writer.WriteString("actorId", ActorId.value v))
        activity.StartedAt |> Option.iter (writeInstant writer "startedAt")
        activity.EndedAt |> Option.iter (writeInstant writer "endedAt")
        activity.Description |> Option.iter (fun v -> writer.WriteString("description", v))
        writeEvidence writer activity.Evidence
        writer.WriteEndObject()

        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private tryGetString (element: JsonElement) (name: string) : string option =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let private tryGetInstant (element: JsonElement) (name: string) : DateTimeOffset option =
        tryGetString element name
        |> Option.bind (fun raw ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, value -> Some value
            | false, _ -> None)

    let private parseEvidence (element: JsonElement) : Evidence list =
        match element.TryGetProperty "evidence" with
        | true, evidence when evidence.ValueKind = JsonValueKind.Array ->
            evidence.EnumerateArray()
            |> Seq.choose (fun item ->
                match tryGetString item "kind", tryGetString item "reference" with
                | Some kind, Some reference -> Some { Kind = kind; Reference = reference }
                | _ -> None)
            |> Seq.toList
        | _ -> []

    /// The V1 wire shape, isolated from every other version this package
    /// may one day support so a future V2 never has to share parsing
    /// logic with -- or contaminate -- this one. Unrecognized JSON fields
    /// are silently ignored (a forward-compatibility policy, not an
    /// oversight): a producer sending a field this version predates it
    /// must not fail deserialization.
    module private V1 =
        let toInput (element: JsonElement) : ActivityObservationInput =
            { ContractVersion = tryGetString element "contractVersion" |> Option.defaultValue ""
              ActivityId = tryGetString element "activityId" |> Option.defaultValue ""
              OrganizationId = tryGetString element "organizationId" |> Option.defaultValue ""
              ProjectId = tryGetString element "projectId" |> Option.defaultValue ""
              RepositoryId = tryGetString element "repositoryId"
              WorkItemId = tryGetString element "workItemId"
              ActorId = tryGetString element "actorId"
              StartedAt = tryGetInstant element "startedAt"
              EndedAt = tryGetInstant element "endedAt"
              Description = tryGetString element "description"
              Evidence = parseEvidence element }

    /// Adapts any wire version this package still supports into the
    /// current `ActivityObservationInput` shape, so `create` (and every
    /// domain-facing caller beyond it) only ever has to understand one
    /// shape. Today the only supported versions ("1"/"1.0") share the V1
    /// wire shape, so this is the identity adapter for V1 -- the seam a
    /// future V2 hangs off of without touching `V1` or `create` above.
    module private Adapter =
        let fromV1 = V1.toInput

    /// Parses `json`, adapts it from whatever wire version it names, and
    /// runs it through `ActivityObservation.create`. A syntactically
    /// invalid document fails with `MalformedJson`; a well-formed
    /// document that fails structural validation (including an
    /// unsupported `contractVersion`) fails with `InvalidActivity`,
    /// carrying every violation `create` found, not just the first.
    let deserializeActivity (json: string) : Result<ActivityObservation, ActivityDeserializationError> =
        try
            use document = JsonDocument.Parse json
            let input = Adapter.fromV1 document.RootElement

            match ActivityObservation.create input with
            | Ok activity -> Ok activity
            | Error errors -> Error(InvalidActivity errors)
        with :? JsonException as ex ->
            Error(MalformedJson ex.Message)
