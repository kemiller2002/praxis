namespace Praxis.Contracts.Identity

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Identity

/// JSON shapes of repository, work-item and instance identity
/// (PRX-REMOTE-047; DER-16..26). A work-item reference is carried
/// structurally as `{repositoryId, repository, localId}`; consumers never
/// parse the `owner/repo:ID` display string. A legacy reference has
/// `repositoryId: null` and names its provider explicitly.
[<RequireQualifiedAccess>]
module IdentityJson =
    [<Literal>]
    let InstanceSchema = "praxis.instance/1"

    [<Literal>]
    let ProjectionSchema = "praxis.instance-projection/1"

    let options = JsonSerializerOptions(WriteIndented = true, IndentSize = 2)

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.defaultValue null

    let private property (name: string) (node: JsonObject) : JsonNode option =
        let mutable value: JsonNode = null

        if node.TryGetPropertyValue(name, &value) && not (isNull value) then Some value else None

    let private stringProperty (name: string) (node: JsonObject) : Result<string option, string> =
        match property name node with
        | None -> Ok None
        | Some value ->
            match value.GetValueKind() with
            | JsonValueKind.String -> Ok(Some(value.GetValue<string>()))
            | _ -> Error $"'{name}' must be a string"

    // ---- repository identity ----

    /// `{provider, repositoryId, repository}`; `repositoryId` is `null` for a
    /// legacy identity.
    let renderRepository (identity: RepositoryIdentity) : JsonObject =
        let node = JsonObject()
        node["provider"] <- text (RepositoryProvider.value identity.Provider)
        node["repositoryId"] <- optionalText (RepositoryIdentity.repositoryId identity)
        node["repository"] <- optionalText (identity.Locator |> Option.map RepositoryLocator.value)
        node

    let renderOptionalRepository (identity: RepositoryIdentity option) : JsonNode =
        identity |> Option.map (fun value -> renderRepository value :> JsonNode) |> Option.defaultValue null

    /// Reads `{repositoryId?, repository?, provider?}` from an object that may
    /// also carry other fields. `Ok None` when it names no repository at all.
    let private readRepositoryFields (defaultProvider: RepositoryProvider option) (node: JsonObject) : Result<RepositoryIdentity option, string> =
        match stringProperty "repositoryId" node, stringProperty "repository" node, stringProperty "provider" node with
        | Error message, _, _
        | _, Error message, _
        | _, _, Error message -> Error message
        | Ok None, Ok None, _ -> Ok None
        | Ok repositoryId, Ok locatorText, Ok providerText ->
            let locator =
                match locatorText with
                | None -> Ok None
                | Some value ->
                    match RepositoryLocator.tryCreate value with
                    | Some locator -> Ok(Some locator)
                    | None -> Error "'repository' must be an owner/repo locator"

            let stable =
                match repositoryId with
                | None -> Ok None
                | Some value ->
                    match RepositoryIdentity.tryParseRepositoryId value with
                    | Some parsed -> Ok(Some parsed)
                    | None -> Error "'repositoryId' must be PROVIDER:PROVIDER-ID, for example github:123456789"

            let explicitProvider =
                match providerText with
                | None -> Ok None
                | Some value ->
                    match RepositoryProvider.tryCreate value with
                    | Some provider -> Ok(Some provider)
                    | None -> Error "'provider' must be a lowercase provider name"

            match locator, stable, explicitProvider with
            | Error message, _, _
            | _, Error message, _
            | _, _, Error message -> Error message
            | Ok locator, Ok(Some(provider, providerId)), Ok declared ->
                match declared with
                | Some other when other <> provider -> Error "'provider' contradicts 'repositoryId'"
                | _ -> RepositoryIdentity.create provider (Some providerId) locator |> Result.map Some
            | Ok locator, Ok None, Ok declared ->
                match declared |> Option.orElse defaultProvider with
                | None -> Error "a reference without 'repositoryId' must name its 'provider'"
                | Some provider -> RepositoryIdentity.create provider None locator |> Result.map Some

    let parseRepository (defaultProvider: RepositoryProvider option) (node: JsonNode) : Result<RepositoryIdentity, string> =
        match node with
        | :? JsonObject as obj ->
            match readRepositoryFields defaultProvider obj with
            | Ok(Some identity) -> Ok identity
            | Ok None -> Error "a repository identity needs 'repositoryId' or 'repository'"
            | Error message -> Error message
        | _ -> Error "a repository identity must be an object"

    // ---- work-item identity ----

    let renderWorkItem (identity: WorkItemIdentity) : JsonObject =
        let node = JsonObject()
        node["repositoryId"] <- optionalText (RepositoryIdentity.repositoryId identity.Repository)
        node["repository"] <- optionalText (identity.Repository.Locator |> Option.map RepositoryLocator.value)

        if RepositoryIdentity.isLegacy identity.Repository then
            node["provider"] <- text (RepositoryProvider.value identity.Repository.Provider)

        node["localId"] <- text (LocalWorkItemId.value identity.LocalId)
        node

    /// The canonical reference of a local ID in a repository context, or a
    /// legacy-explicit one (`repositoryId: null, repository: null`) when the
    /// repository identity is not established (PRX-REMOTE-048).
    let renderLocalWorkItem (context: RepositoryIdentity option) (localId: string) : JsonObject =
        match context, LocalWorkItemId.tryCreate localId with
        | Some repository, Some id -> renderWorkItem (WorkItemIdentity.create repository id)
        | _ ->
            let node = JsonObject()
            node["repositoryId"] <- null
            node["repository"] <- null
            node["localId"] <- text localId
            node

    /// A reference as written: a bare string is an unqualified local ID; an
    /// object is `{repositoryId?, repository?, provider?, localId}`. A string
    /// containing a repository (`owner/repo:ID`) is refused: qualified
    /// references are structural.
    let parseWorkItemReference (defaultProvider: RepositoryProvider option) (node: JsonNode) : Result<WorkItemReference, string> =
        let local (value: string) =
            match LocalWorkItemId.tryCreate value with
            | Some id -> Ok id
            | None when not (isNull value) && value.Contains ':' ->
                Error "a qualified work-item reference must be the object {repositoryId, repository, localId}, not a display string"
            | None -> Error "'localId' must be a work-item ID (letters, digits, '.', '_' or '-')"

        match node with
        | null -> Error "a work-item reference is required"
        | :? JsonObject as obj ->
            match stringProperty "localId" obj with
            | Error message -> Error message
            | Ok None -> Error "a work-item reference needs 'localId'"
            | Ok(Some value) ->
                match local value, readRepositoryFields defaultProvider obj with
                | Error message, _
                | _, Error message -> Error message
                | Ok id, Ok None -> Ok(WorkItemReference.Unqualified id)
                | Ok id, Ok(Some repository) -> Ok(WorkItemReference.Qualified(WorkItemIdentity.create repository id))
        | value when value.GetValueKind() = JsonValueKind.String -> local (value.GetValue<string>()) |> Result.map WorkItemReference.Unqualified
        | _ -> Error "a work-item reference must be a string or an object"

    let renderWorkItemReference (reference: WorkItemReference) : JsonNode =
        match reference with
        | WorkItemReference.Unqualified id -> text (LocalWorkItemId.value id)
        | WorkItemReference.Qualified identity -> renderWorkItem identity

    // ---- instance record ----

    let private timestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let renderInstance (record: InstanceRecord) =
        let node = JsonObject()
        node["schema"] <- text InstanceSchema
        node["instanceId"] <- text (InstanceId.value record.InstanceId)
        node["createdAt"] <- optionalText (record.CreatedAt |> Option.map timestamp)
        node["createdWith"] <- text record.CreatedWith
        node["repository"] <- renderOptionalRepository record.Repository
        let predecessors = JsonArray()

        for predecessor in record.Predecessors do
            let entry = JsonObject()
            entry["instanceId"] <- text (InstanceId.value predecessor.InstanceId)
            entry["reason"] <- text predecessor.Reason
            entry["replacedAt"] <- text (timestamp predecessor.ReplacedAt)
            predecessors.Add(entry :> JsonNode)

        node["predecessors"] <- predecessors
        node.ToJsonString options + "\n"

    let private parseTimestamp (name: string) (value: string option) =
        match value with
        | None -> Error $"'{name}' is required"
        | Some raw ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, parsed -> Ok parsed
            | _ -> Error $"'{name}' is not a timestamp"

    let private parseInstanceId (name: string) (value: string option) =
        match value |> Option.bind InstanceId.tryCreate with
        | Some id -> Ok id
        | None -> Error $"'{name}' must be an instance ID"

    /// Parses `.praxis/instance.json`. Every problem is a typed error; a
    /// document written before this schema (`{instanceId}` only) is read
    /// with no repository binding and no lineage.
    let parseInstance (json: string) : Result<InstanceRecord, string> =
        let parsed =
            try
                Ok(JsonNode.Parse json)
            with :? JsonException as error ->
                Error $"not valid JSON: {error.Message}"

        match parsed with
        | Error message -> Error message
        | Ok(:? JsonObject as root) ->
            let field name = stringProperty name root

            match field "schema", field "instanceId", field "createdAt", field "createdWith" with
            | Error message, _, _, _
            | _, Error message, _, _
            | _, _, Error message, _
            | _, _, _, Error message -> Error message
            | Ok(Some schema), _, _, _ when schema <> InstanceSchema -> Error $"unsupported schema '{schema}' (expected {InstanceSchema})"
            | Ok _, Ok instanceId, Ok createdAt, Ok createdWith ->
                let repository =
                    match property "repository" root with
                    | None -> Ok None
                    | Some node -> parseRepository None node |> Result.map Some

                let predecessors =
                    match property "predecessors" root with
                    | None -> Ok []
                    | Some(:? JsonArray as items) ->
                        items
                        |> Seq.map (fun item ->
                            match item with
                            | :? JsonObject as entry ->
                                match stringProperty "instanceId" entry, stringProperty "reason" entry, stringProperty "replacedAt" entry with
                                | Ok id, Ok(Some reason), Ok replacedAt ->
                                    match parseInstanceId "predecessors[].instanceId" id, parseTimestamp "predecessors[].replacedAt" replacedAt with
                                    | Ok id, Ok at -> Ok { InstanceId = id; Reason = reason; ReplacedAt = at }
                                    | Error message, _
                                    | _, Error message -> Error message
                                | _ -> Error "each predecessor needs instanceId, reason and replacedAt strings"
                            | _ -> Error "each predecessor must be an object")
                        |> Seq.fold (fun acc item -> acc |> Result.bind (fun list -> item |> Result.map (fun value -> value :: list))) (Ok [])
                        |> Result.map List.rev
                    | Some _ -> Error "'predecessors' must be an array"

                match parseInstanceId "instanceId" instanceId, repository, predecessors with
                | Error message, _, _
                | _, Error message, _
                | _, _, Error message -> Error message
                | Ok id, Ok repository, Ok predecessors ->
                    let created =
                        match createdAt with
                        | None -> Ok None
                        | value -> parseTimestamp "createdAt" value |> Result.map Some

                    created
                    |> Result.map (fun createdAt ->
                        { InstanceId = id
                          CreatedAt = createdAt
                          CreatedWith = createdWith |> Option.defaultValue "unknown"
                          Repository = repository
                          Predecessors = predecessors })
        | Ok _ -> Error "the instance record must be a JSON object"

    // ---- status documents ----

    let renderInstanceStatus (local: LocalInstance) : JsonObject =
        let node = JsonObject()

        match local with
        | LocalInstance.Missing ->
            node["state"] <- text "missing"
            node["instanceId"] <- null
        | LocalInstance.Unreadable reason ->
            node["state"] <- text "unreadable"
            node["instanceId"] <- null
            node["reason"] <- text reason
        | LocalInstance.Present(record, binding) ->
            node["state"] <- text (InstanceBinding.code binding)
            node["instanceId"] <- text (InstanceId.value record.InstanceId)
            node["createdAt"] <- optionalText (record.CreatedAt |> Option.map timestamp)
            node["createdWith"] <- text record.CreatedWith
            node["repository"] <- renderOptionalRepository record.Repository
            node["predecessors"] <- JsonValue.Create record.Predecessors.Length

            match binding with
            | InstanceBinding.Unverified reason -> node["reason"] <- text reason
            | InstanceBinding.Foreign(_, current) -> node["currentRepository"] <- renderRepository current
            | InstanceBinding.Bound -> ()

        node

    let renderRepositoryStatus (status: RepositoryIdentityStatus) : JsonObject =
        let node = JsonObject()
        node["state"] <- text (RepositoryIdentityStatus.code status)
        node["identity"] <- renderOptionalRepository (RepositoryIdentityStatus.current status)

        match status with
        | RepositoryIdentityStatus.Contradicted(_, observed) -> node["observed"] <- renderRepository observed
        | RepositoryIdentityStatus.LocatorChanged(_, locator) -> node["observedLocator"] <- text (RepositoryLocator.value locator)
        | _ -> ()

        node

    /// `praxis.instance-projection/1`: exactly the allow-listed fields.
    let renderProjection (projection: InstanceProjection) : JsonObject =
        let node = JsonObject()
        node["schema"] <- text ProjectionSchema
        node["instanceId"] <- text (InstanceId.value projection.InstanceId)
        node["repository"] <- renderOptionalRepository projection.Repository
        node["praxisVersion"] <- text projection.PraxisVersion
        let protocols = JsonObject()
        protocols["reconciliation"] <- text projection.ReconciliationProtocolVersion
        protocols["remote"] <- text projection.RemoteProtocolVersion
        node["protocols"] <- protocols
        let capabilities = JsonArray()

        for capability in projection.Capabilities do
            capabilities.Add(text capability)

        node["capabilities"] <- capabilities
        let integrations = JsonObject()

        for name, availability in projection.Integrations do
            integrations[name] <- text (IntegrationAvailability.code availability)

        node["integrations"] <- integrations
        node
