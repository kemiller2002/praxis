namespace Ros.Contracts.Remote

open System
open System.Globalization
open System.Text.Json
open Ros.Contracts
open Ros.Domain.Provenance
open Ros.Domain.Remote

/// JSON form of the `praxis.remote` protocol
/// (`schemas/praxis-remote-request.schema.json`,
/// `schemas/praxis-remote-response.schema.json`, `docs/remote-protocol.md`).
///
/// Parsing is strict because the document is untrusted: unknown fields are
/// rejected (unknown semantics fail safely), except `x-`-prefixed extension
/// fields, which are tolerated and carry no meaning. The protocol and its
/// version are read *before* anything else, so a request from a newer
/// protocol version is reported as `unsupported-protocol` rather than as a
/// pile of unknown fields. Every structural problem is reported, not just
/// the first, so a caller can fix a request in one round trip.
[<RequireQualifiedAccess>]
module RemoteJson =
    /// Why a document could not become a typed `Request`, together with
    /// whatever identifying fields could still be read for the response.
    type ParseFailure =
        { Failure: RemoteFailure
          RequestId: string option
          Operation: string option
          ProtocolVersion: ProtocolVersion option
          Repository: RepositoryBinding }

    type private Parsed<'value> = 'value option * Problem list

    let private problem field message : Problem = { Field = field; Message = message }

    let private isExtension (name: string) = name.StartsWith "x-"

    let private unknownFields (field: string) (allowed: Set<string>) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.map (fun property -> property.Name)
        |> Seq.filter (fun name -> not (allowed.Contains name || isExtension name))
        |> Seq.map (fun name -> problem (if field = "" then name else $"{field}.{name}") "is not a recognised field")
        |> Seq.toList

    let private tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
        | _ -> None

    let private stringValue field (value: JsonElement) : Parsed<string> =
        if value.ValueKind = JsonValueKind.String then Some(value.GetString()), []
        else None, [ problem field "must be a string" ]

    let private optionalString field name element : Parsed<string option> =
        match tryProperty name element with
        | None -> Some None, []
        | Some value ->
            let parsed, problems = stringValue field value
            parsed |> Option.map Some, problems

    let private requiredString field name element : Parsed<string> =
        match tryProperty name element with
        | None -> None, [ problem field "is required" ]
        | Some value -> stringValue field value

    let private stringList field name element : Parsed<string list> =
        match tryProperty name element with
        | None -> Some [], []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let items =
                value.EnumerateArray()
                |> Seq.mapi (fun index item -> stringValue $"{field}[{index}]" item)
                |> Seq.toList

            let problems = items |> List.collect snd

            if problems.IsEmpty then Some(items |> List.choose fst), [] else None, problems
        | Some _ -> None, [ problem field "must be an array of strings" ]

    let private requiredStringList field name element : Parsed<string list> =
        match tryProperty name element with
        | None -> None, [ problem field "is required" ]
        | Some _ -> stringList field name element

    let private objectValue field (value: JsonElement) : Parsed<JsonElement> =
        if value.ValueKind = JsonValueKind.Object then Some value, []
        else None, [ problem field "must be an object" ]

    let private optionalObject field name element : Parsed<JsonElement option> =
        match tryProperty name element with
        | None -> Some None, []
        | Some value ->
            let parsed, problems = objectValue field value
            parsed |> Option.map Some, problems

    /// Combines independent reads, keeping every problem from every read.
    let private combine2 (first: Parsed<'a>) (second: Parsed<'b>) : Parsed<'a * 'b> =
        match first, second with
        | (Some a, []), (Some b, []) -> Some(a, b), []
        | (_, firstProblems), (_, secondProblems) -> None, firstProblems @ secondProblems

    let private map (mapping: 'a -> 'b) ((value, problems): Parsed<'a>) : Parsed<'b> =
        (if problems.IsEmpty then value |> Option.map mapping else None), problems

    let private withProblems (extra: Problem list) ((value, problems): Parsed<'a>) : Parsed<'a> =
        match problems @ extra with
        | [] -> value, []
        | all -> None, all

    let private parseRepository (root: JsonElement) : Parsed<RepositoryBinding> =
        match optionalObject "repository" "repository" root with
        | Some None, _ -> Some { Ref = None; ExpectedSha = None }, []
        | Some(Some element), _ ->
            combine2 (optionalString "repository.ref" "ref" element) (optionalString "repository.expectedSha" "expectedSha" element)
            |> map (fun (reference, expectedSha) -> { Ref = reference; ExpectedSha = expectedSha })
            |> withProblems (unknownFields "repository" (set [ "ref"; "expectedSha" ]) element)
        | None, problems -> None, problems

    let private parseActor (root: JsonElement) : Parsed<RequestActor option> =
        match optionalObject "actor" "actor" root with
        | Some None, _ -> Some None, []
        | None, problems -> None, problems
        | Some(Some element), _ ->
            let kindText, kindProblems = requiredString "actor.kind" "kind" element

            let kind, kindParseProblems =
                match kindText with
                | Some text ->
                    match ActorKind.tryParse text with
                    | Some kind -> Some kind, []
                    | None -> None, [ problem "actor.kind" "must be agent, human, automation, unknown, or x-<extension>" ]
                | None -> None, []

            let id, idProblems = optionalString "actor.id" "id" element
            let provider, providerProblems = optionalString "actor.provider" "provider" element
            let model, modelProblems = optionalString "actor.model" "model" element
            let runtime, runtimeProblems = optionalString "actor.runtime" "runtime" element
            let session, sessionProblems = optionalString "actor.sessionId" "sessionId" element

            let problems =
                kindProblems @ kindParseProblems @ idProblems @ providerProblems @ modelProblems @ runtimeProblems @ sessionProblems
                @ unknownFields "actor" (set [ "kind"; "id"; "provider"; "model"; "runtime"; "sessionId" ]) element

            match problems, kind with
            | [], Some kind ->
                // An absent value is recorded as `unknown`, never guessed; a
                // human has no provider/model/runtime at all
                // (`DF-ROS-2026-A036`).
                let orUnknown (value: string option option) =
                    value |> Option.flatten |> Option.defaultValue Actor.UnknownValue

                let applicable (value: string option option) =
                    match kind with
                    | ActorKind.Human -> Option.flatten value
                    | _ -> Some(orUnknown value)

                Some(
                    Some
                        { Actor =
                            { Kind = kind
                              Id = orUnknown id
                              Provider = applicable provider
                              Model = applicable model
                              Runtime = applicable runtime }
                          SessionId = Option.flatten session }
                ),
                []
            | _ -> None, problems

    let private parseExecution (root: JsonElement) : Parsed<string option> =
        match optionalObject "execution" "execution" root with
        | Some None, _ -> Some None, []
        | None, problems -> None, problems
        | Some(Some element), _ ->
            requiredString "execution.id" "id" element
            |> map Some
            |> withProblems (unknownFields "execution" (set [ "id" ]) element)

    let private parseRequestedAt (root: JsonElement) : Parsed<DateTimeOffset option> =
        match optionalString "requestedAt" "requestedAt" root with
        | Some None, _ -> Some None, []
        | Some(Some text), _ ->
            match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, value -> Some(Some value), []
            | _ -> None, [ problem "requestedAt" "must be an ISO-8601 timestamp" ]
        | None, problems -> None, problems

    let private parseDecimal field name element : Parsed<decimal> =
        match tryProperty name element with
        | None -> None, [ problem field "is required" ]
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetDecimal() with
            | true, number -> Some number, []
            | _ -> None, [ problem field "must be a finite number" ]
        | Some _ -> None, [ problem field "must be a number" ]

    /// `confidence` accepts the telemetry vocabulary's own forms: a number
    /// in [0, 1] or `low | medium | high`, carried as text.
    let private parseConfidence field name element : Parsed<string option> =
        match tryProperty name element with
        | None -> Some None, []
        | Some value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetDecimal() with
            | true, number when number >= 0m && number <= 1m -> Some(Some(number.ToString(CultureInfo.InvariantCulture))), []
            | _ -> None, [ problem field "must be a number between 0 and 1, or low, medium, or high" ]
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() with
            | "low"
            | "medium"
            | "high" as text -> Some(Some text), []
            | _ -> None, [ problem field "must be a number between 0 and 1, or low, medium, or high" ]
        | Some _ -> None, [ problem field "must be a number between 0 and 1, or low, medium, or high" ]

    let private parseEvidence (element: JsonElement) : Parsed<EvidenceArgument list> =
        match tryProperty "evidence" element with
        | None -> Some [], []
        | Some value when value.ValueKind = JsonValueKind.Array ->
            let items =
                value.EnumerateArray()
                |> Seq.mapi (fun index item ->
                    let field = $"arguments.evidence[{index}]"

                    match objectValue field item with
                    | Some entry, _ ->
                        combine2 (requiredString $"{field}.type" "type" entry) (requiredString $"{field}.path" "path" entry)
                        |> map (fun (evidenceType, path) -> { Type = evidenceType; Path = path })
                        |> withProblems (unknownFields field (set [ "type"; "path" ]) entry)
                    | None, problems -> None, problems)
                |> Seq.toList

            match items |> List.collect snd with
            | [] -> Some(items |> List.choose fst), []
            | problems -> None, problems
        | Some _ -> None, [ problem "arguments.evidence" "must be an array of {type, path} objects" ]

    let private argumentFields operation =
        let required, optional = Operation.arguments operation
        Set.ofList (required @ optional)

    let private parseArgumentsOf (operation: Operation) (element: JsonElement) : Parsed<Arguments> =
        let field name = $"arguments.{name}"

        match operation with
        | Operation.Describe
        | Operation.Status
        | Operation.Validate
        | Operation.ProvenanceIdentity -> Some Arguments.NoArguments, []
        | Operation.WorkContext -> requiredString (field "workItemId") "workItemId" element |> map Arguments.WorkContext
        | Operation.RequestStatus -> requiredString (field "requestId") "requestId" element |> map Arguments.RequestStatus
        | Operation.WorkStart ->
            combine2
                (requiredStringList (field "workItemIds") "workItemIds" element)
                (combine2 (optionalString (field "type") "type" element) (stringList (field "classifications") "classifications" element))
            |> map (fun (ids, (workType, classifications)) ->
                Arguments.WorkStart
                    { WorkItemIds = ids
                      Type = workType
                      Classifications = classifications })
        | Operation.WorkResume -> requiredStringList (field "workItemIds") "workItemIds" element |> map Arguments.WorkResume
        | Operation.WorkBlock ->
            combine2 (requiredStringList (field "workItemIds") "workItemIds" element) (requiredString (field "reason") "reason" element)
            |> map Arguments.WorkBlock
        | Operation.WorkComplete ->
            combine2
                (requiredStringList (field "workItemIds") "workItemIds" element)
                (combine2 (parseEvidence element) (optionalString (field "conclusion") "conclusion" element))
            |> map (fun (ids, (evidence, conclusion)) ->
                Arguments.WorkComplete
                    { WorkItemIds = ids
                      Evidence = evidence
                      Conclusion = conclusion })
        | Operation.TelemetryRecord ->
            let optional name = optionalString (field name) name element

            let core =
                combine2
                    (combine2 (optional "workItemId") (requiredString (field "metric") "metric" element))
                    (combine2 (parseDecimal (field "value") "value" element) (parseConfidence (field "confidence") "confidence" element))

            let descriptive =
                [ "unit"; "currency"; "quality"; "scope"; "sourceType"; "sourceName"; "mechanism"; "pricingSource"; "pricingVersion"; "collectedAt"; "step" ]
                |> List.map (fun name -> name, optional name)

            match core, descriptive |> List.collect (snd >> snd) with
            | (Some((workItemId, metric), (value, confidence)), []), [] ->
                let read name =
                    descriptive |> List.find (fst >> (=) name) |> snd |> fst |> Option.flatten

                Some(
                    Arguments.TelemetryRecord
                        { WorkItemId = workItemId
                          Metric = metric
                          Value = value
                          Unit = read "unit"
                          Currency = read "currency"
                          Quality = read "quality"
                          Confidence = confidence
                          Scope = read "scope"
                          SourceType = read "sourceType"
                          SourceName = read "sourceName"
                          Mechanism = read "mechanism"
                          PricingSource = read "pricingSource"
                          PricingVersion = read "pricingVersion"
                          CollectedAt = read "collectedAt"
                          Step = read "step" }
                ),
                []
            | (_, coreProblems), descriptiveProblems -> None, coreProblems @ descriptiveProblems
        | Operation.StepStart ->
            combine2 (requiredString (field "stepId") "stepId" element) (optionalString (field "name") "name" element)
            |> map (fun (stepId, name) -> Arguments.Step(stepId, name, None))
        | Operation.StepComplete
        | Operation.StepFail ->
            combine2 (requiredString (field "stepId") "stepId" element) (optionalString (field "reason") "reason" element)
            |> map (fun (stepId, reason) -> Arguments.Step(stepId, None, reason))
        | Operation.WorkReconcile ->
            combine2
                (combine2 (requiredString (field "workItemId") "workItemId" element) (requiredString (field "reason") "reason" element))
                (combine2
                    (stringList (field "commits") "commits" element)
                    (combine2 (stringList (field "ranges") "ranges" element) (stringList (field "paths") "paths" element)))
            |> map (fun ((workItemId, reason), (commits, (ranges, paths))) ->
                Arguments.WorkReconcile
                    { WorkItemId = workItemId
                      Reason = reason
                      Commits = commits
                      Ranges = ranges
                      Paths = paths })

    let private parseArguments (operation: Operation) (root: JsonElement) : Parsed<Arguments> =
        match optionalObject "arguments" "arguments" root with
        | None, problems -> None, problems
        | Some None, _ ->
            // An operation without arguments may omit the object entirely;
            // any other operation still reports its missing fields.
            use empty = JsonDocument.Parse "{}"
            parseArgumentsOf operation (empty.RootElement.Clone())
        | Some(Some element), _ ->
            parseArgumentsOf operation element
            |> withProblems (unknownFields "arguments" (argumentFields operation) element)

    let private topLevelFields =
        set [ "protocol"; "protocolVersion"; "requestId"; "operation"; "repository"; "actor"; "execution"; "arguments"; "requestedAt" ]

    let private failure code message problems =
        RemoteFailure.create code message problems

    /// Reads whatever identifying fields a rejected document still has, so
    /// even a rejection can be correlated with its request.
    let private salvage (root: JsonElement) =
        let text name =
            match tryProperty name root with
            | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
            | _ -> None

        let repository =
            match parseRepository root with
            | Some binding, _ -> binding
            | None, _ -> { Ref = None; ExpectedSha = None }

        text "requestId" |> Option.filter RequestValidation.isRequestId, text "operation", repository

    let parseRequest (executor: ProtocolVersion) (text: string) : Result<Request, ParseFailure> =
        let reject (root: JsonElement option) version code message problems =
            let requestId, operation, repository =
                root |> Option.map salvage |> Option.defaultValue (None, None, { Ref = None; ExpectedSha = None })

            Error
                { Failure = failure code message problems
                  RequestId = requestId
                  Operation = operation
                  ProtocolVersion = version
                  Repository = repository }

        let document =
            try
                Ok(JsonDocument.Parse(text))
            with :? JsonException as error ->
                Error error.Message

        match document with
        | Error message -> reject None None FailureCode.InvalidRequest "the request is not valid JSON" [ problem "$" message ]
        | Ok document ->
            use document = document
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                reject None None FailureCode.InvalidRequest "the request must be a JSON object" [ problem "$" "must be an object" ]
            else
                let supported = $"supported: {ProtocolVersion.Protocol} {ProtocolVersion.code executor} (and earlier {executor.Major}.x)"

                match requiredString "protocol" "protocol" root with
                | None, problems -> reject (Some root) None FailureCode.InvalidRequest "the request does not name its protocol" problems
                | Some protocol, _ when protocol <> ProtocolVersion.Protocol ->
                    reject (Some root) None FailureCode.UnsupportedProtocol $"unsupported protocol; {supported}" [ problem "protocol" $"must be '{ProtocolVersion.Protocol}'" ]
                | Some _, _ ->
                    match requiredString "protocolVersion" "protocolVersion" root with
                    | None, problems -> reject (Some root) None FailureCode.InvalidRequest "the request does not name its protocol version" problems
                    | Some versionText, _ ->
                        match ProtocolVersion.tryParse versionText with
                        | None ->
                            reject (Some root) None FailureCode.InvalidRequest "the protocol version is malformed" [ problem "protocolVersion" "must be MAJOR.MINOR" ]
                        | Some version when not (ProtocolVersion.isSupportedBy executor version) ->
                            reject
                                (Some root)
                                (Some version)
                                FailureCode.UnsupportedProtocol
                                $"protocol version {ProtocolVersion.code version} is not supported; {supported}"
                                [ problem "protocolVersion" "is not supported by this executor" ]
                        | Some version ->
                            match requiredString "operation" "operation" root with
                            | None, problems -> reject (Some root) (Some version) FailureCode.InvalidRequest "the request does not name an operation" problems
                            | Some operationText, _ ->
                                match Operation.tryParse operationText with
                                | Some operation when Operation.introducedIn operation <= version.Minor ->
                                    let requestId, requestIdProblems = requiredString "requestId" "requestId" root
                                    let repository, repositoryProblems = parseRepository root
                                    let actor, actorProblems = parseActor root
                                    let execution, executionProblems = parseExecution root
                                    let arguments, argumentProblems = parseArguments operation root
                                    let requestedAt, requestedAtProblems = parseRequestedAt root

                                    let problems =
                                        requestIdProblems @ repositoryProblems @ actorProblems @ executionProblems
                                        @ argumentProblems @ requestedAtProblems @ unknownFields "" topLevelFields root

                                    match problems, requestId, repository, actor, execution, arguments, requestedAt with
                                    | [], Some requestId, Some repository, Some actor, Some execution, Some arguments, Some requestedAt ->
                                        Ok
                                            { ProtocolVersion = version
                                              RequestId = requestId
                                              Operation = operation
                                              Repository = repository
                                              Actor = actor
                                              ExecutionId = execution
                                              Arguments = arguments
                                              RequestedAt = requestedAt }
                                    | _ -> reject (Some root) (Some version) FailureCode.InvalidRequest "the request is invalid" problems
                                | _ ->
                                    reject
                                        (Some root)
                                        (Some version)
                                        FailureCode.UnsupportedOperation
                                        $"operation '{operationText}' is not supported by protocol {ProtocolVersion.code version}"
                                        [ problem "operation" "is not a supported operation" ]

    let private writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let private writeFailure (writer: Utf8JsonWriter) ((value: RemoteFailure)) =
        writer.WriteStartObject("failure")
        writer.WriteString("code", FailureCode.code value.Code)
        writer.WriteString("decidedBy", (match FailureCode.decidedBy value.Code with DecidedBy.Praxis -> "praxis" | DecidedBy.Executor -> "executor"))
        writer.WriteString("retry", RetryAdvice.code (FailureCode.retry value.Code))
        writer.WriteString("message", value.Message)
        writer.WriteStartArray("problems")

        value.Problems
        |> List.iter (fun item ->
            writer.WriteStartObject()
            writer.WriteString("field", item.Field)
            writer.WriteString("message", item.Message)
            writer.WriteEndObject())

        writer.WriteEndArray()
        writer.WriteEndObject()

    /// Renders a response. `Result`, when present, must already be a JSON
    /// document (the executing command's own output); it is embedded as-is.
    let renderResponse (response: Response) =
        JsonRendering.renderIndented (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("protocol", ProtocolVersion.Protocol)
            writer.WriteString("protocolVersion", ProtocolVersion.code response.ProtocolVersion)
            writeOptionalString writer "requestId" response.RequestId
            writeOptionalString writer "operation" response.Operation
            writer.WriteString("outcome", Outcome.code response.Outcome)
            writer.WriteBoolean("replayed", response.Replayed)
            writer.WriteStartObject("repository")
            writeOptionalString writer "ref" response.Repository.Ref
            writeOptionalString writer "expectedSha" response.Repository.ExpectedSha
            writeOptionalString writer "observedSha" response.ObservedSha
            writer.WriteEndObject()
            writer.WriteString("praxisVersion", response.PraxisVersion)

            match response.Executor with
            | Some facts ->
                writer.WriteStartObject("executor")
                writer.WriteString("kind", facts.Kind)
                writeOptionalString writer "runId" facts.RunId
                writeOptionalString writer "runAttempt" facts.RunAttempt
                writeOptionalString writer "workflowRef" facts.WorkflowRef
                writeOptionalString writer "repository" facts.Repository
                writeOptionalString writer "host" facts.Host
                writeOptionalString writer "principal" facts.Principal
                writer.WriteString("praxisVersion", facts.PraxisVersion)
                writer.WriteString("assurance", "observed-by-executor")
                writer.WriteEndObject()
            | None -> ()

            writer.WriteStartObject("persistence")
            writer.WriteStartArray("paths")
            response.Persistence |> List.iter writer.WriteStringValue
            writer.WriteEndArray()
            writer.WriteEndObject()

            match response.Failure with
            | Some value -> writeFailure writer value
            | None -> writer.WriteNull("failure")

            match response.Result with
            | Some json ->
                writer.WritePropertyName("result")
                use result = JsonDocument.Parse json
                result.RootElement.WriteTo writer
            | None -> writer.WriteNull("result")

            writer.WriteEndObject())

    /// The response for a document that never became a typed request.
    let rejection (executor: ProtocolVersion) praxisVersion observedSha (failure: ParseFailure) =
        Response.rejected
            (failure.ProtocolVersion |> Option.defaultValue executor)
            praxisVersion
            failure.RequestId
            failure.Operation
            failure.Repository
            observedSha
            failure.Failure

    /// The findings of a `validate --json` document, reduced to what
    /// identifies each one. `None` when the text is not such a document.
    let parseValidationFindings (text: string) : ValidationFinding list option =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            match root.TryGetProperty "findings" with
            | true, findings when findings.ValueKind = JsonValueKind.Array ->
                let read (item: JsonElement) name =
                    match item.TryGetProperty(name: string) with
                    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                    | _ -> ""

                findings.EnumerateArray()
                |> Seq.map (fun item ->
                    { Severity = read item "severity"
                      Path = read item "path"
                      Field = read item "field"
                      Message = read item "message" })
                |> Seq.toList
                |> Some
            | _ -> None
        with :? JsonException ->
            None

    /// A command's standard output as a JSON value: embedded as-is when it
    /// is a JSON document, otherwise carried as text so nothing is lost.
    let commandResult (stdout: string) : string =
        let trimmed = stdout.Trim()

        let isJson =
            trimmed.Length > 0
            && (try
                    use _ = JsonDocument.Parse trimmed
                    true
                with :? JsonException ->
                    false)

        if isJson then
            trimmed
        else
            use stream = new IO.MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("text", stdout)
            writer.WriteEndObject()
            writer.Flush()
            Text.Encoding.UTF8.GetString(stream.ToArray())

/// The durable request journal (`.ros/remote/requests/<id>.json`,
/// `DF-ROS-2026-A041` section 5). One entry per accepted, successful
/// mutating request, committed in the same commit as the state it
/// describes, so a lost result is always recoverable from the repository
/// itself: request -> actor/executor -> execution -> events -> state.
[<RequireQualifiedAccess>]
module RemoteJournal =
    [<Literal>]
    let Schema = "praxis.remote-journal"

    [<Literal>]
    let SchemaVersion = 1

    type Entry =
        { Request: Request
          Fingerprint: string
          RecordedAt: string
          Principal: string option
          Response: string }

    let render (entry: Entry) =
        JsonRendering.renderIndented (fun writer ->
            let optional (name: string) (value: string option) =
                match value with
                | Some text -> writer.WriteString(name, text)
                | None -> writer.WriteNull(name)

            writer.WriteStartObject()
            writer.WriteString("schema", Schema)
            writer.WriteNumber("schemaVersion", SchemaVersion)
            writer.WriteString("requestId", entry.Request.RequestId)
            writer.WriteString("fingerprint", entry.Fingerprint)
            writer.WriteString("protocolVersion", ProtocolVersion.code entry.Request.ProtocolVersion)
            writer.WriteString("operation", Operation.code entry.Request.Operation)
            writer.WriteString("recordedAt", entry.RecordedAt)
            optional "requestedAt" (entry.Request.RequestedAt |> Option.map (fun value -> value.ToString("o", CultureInfo.InvariantCulture)))
            writer.WriteStartObject("repository")
            optional "ref" entry.Request.Repository.Ref
            optional "expectedSha" entry.Request.Repository.ExpectedSha
            writer.WriteEndObject()
            writer.WriteStartObject("requester")
            writer.WriteString("assurance", "asserted-by-request")

            match entry.Request.Actor with
            | Some requestActor ->
                writer.WritePropertyName("actor")
                writer.WriteRawValue((Ros.Contracts.Provenance.ActorJson.node requestActor.Actor).ToJsonString())
                optional "sessionId" requestActor.SessionId
            | None -> writer.WriteNull("actor")

            optional "execution" entry.Request.ExecutionId
            writer.WriteEndObject()
            optional "principal" entry.Principal
            writer.WritePropertyName("response")
            writer.WriteRawValue(entry.Response)
            writer.WriteEndObject())

    /// The recorded fingerprint and response of an entry, or an error when
    /// the entry is not a journal record this version understands (which
    /// fails closed: an unreadable record is never treated as absent).
    let read (text: string) : Result<string * string, string> =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            let property (name: string) =
                match root.TryGetProperty name with
                | true, value -> Some value
                | _ -> None

            match property "schema", property "schemaVersion", property "fingerprint", property "response" with
            | Some schema, Some version, Some fingerprint, Some response when
                schema.ValueKind = JsonValueKind.String
                && schema.GetString() = Schema
                && version.ValueKind = JsonValueKind.Number
                && version.GetInt32() = SchemaVersion
                && fingerprint.ValueKind = JsonValueKind.String
                && response.ValueKind = JsonValueKind.Object ->
                Ok(fingerprint.GetString(), response.GetRawText())
            | _ -> Error "not a praxis.remote-journal version 1 record"
        with :? JsonException as error ->
            Error error.Message

    /// The recorded response, flagged as a replay and re-rendered.
    let replayedResponse (recorded: string) =
        let node = Nodes.JsonNode.Parse recorded
        node["replayed"] <- Nodes.JsonValue.Create true

        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        + "\n"
