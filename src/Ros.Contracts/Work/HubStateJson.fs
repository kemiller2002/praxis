namespace Ros.Contracts.Work

open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Work

/// What a registered repository's own Praxis answered: its availability and,
/// when it answered with a versioned document, that document unchanged.
type SpokeAnswer =
    { Availability: SpokeAvailability
      Document: JsonObject option }

/// The versioned multi-repository control-plane contract (`praxis.hub-state`,
/// PRX-CTL-007) served by `praxis hub serve`'s `/api/v1` routes and printed
/// by `praxis hub state`; documented in `docs/project-administration-hub.md`.
/// A hub document says which registered repository a per-repository document
/// came from and whether that repository could answer; the per-repository
/// document itself is the repository's own `praxis.work-state` document,
/// nested unchanged. A breaking change bumps `version`; adding fields does not.
[<RequireQualifiedAccess>]
module HubStateJson =
    let contract = "praxis.hub-state"
    let version = 1

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj

    let private record (fields: (string * JsonNode) list) : JsonObject =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    let private array (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter result.Add
        result

    let private stringField (name: string) (node: JsonObject) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private integerField (name: string) (node: JsonObject) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number ->
            match value.TryGetValue<int>() with
            | true, number -> Some number
            | _ -> None
        | _ -> None

    let private parseObject (output: string) =
        try
            match JsonNode.Parse output with
            | :? JsonObject as node -> Some node
            | _ -> None
        with _ ->
            None

    // ---- classifying a repository's answer (pure) ----

    let private versionText (version: int option) =
        version |> Option.map string |> Option.defaultValue "none"

    /// Classifies what `praxis COMMAND` printed in a registered repository
    /// (exit status `exit`, stdout `output`, `failure` its error message).
    /// A `praxis.work-state` document of an `expectedKinds` kind with exit 0
    /// is available; a `praxis.error` document with a non-zero exit is the
    /// repository's own answer (available), except `work-state-unreadable`;
    /// another contract version, or output that is not the contract at all
    /// (a Praxis that predates the command prints its usage), is
    /// incompatible. The document is never altered.
    let classify (command: string) (expectedKinds: string list) (exit: int) (output: string) (failure: string) : SpokeAnswer =
        let incompatible reason =
            { Availability = SpokeAvailability.Incompatible reason
              Document = None }

        let expected = expectedKinds |> String.concat " or "

        match parseObject output with
        | Some document when stringField "contract" document = Some WorkStateJson.contract ->
            match integerField "version" document, stringField "kind" document with
            | Some found, Some kind when found = WorkStateJson.version && exit = 0 && List.contains kind expectedKinds ->
                { Availability = SpokeAvailability.Available
                  Document = Some document }
            | Some found, kind when found = WorkStateJson.version ->
                incompatible $"""`praxis {command}` answered {WorkStateJson.contract} kind '{kind |> Option.defaultValue "none"}' (exit {exit}) where {expected} was expected"""
            | found, _ ->
                incompatible
                    $"`praxis {command}` speaks {WorkStateJson.contract} version {versionText found}; this hub reads version {WorkStateJson.version}"
        | Some document when stringField "contract" document = Some WorkStateJson.errorContract && exit <> 0 ->
            match integerField "version" document with
            | Some found when found = WorkStateJson.version ->
                match stringField "code" document with
                | Some "work-state-unreadable" ->
                    { Availability =
                        SpokeAvailability.Unreadable(stringField "error" document |> Option.defaultValue "its recorded work state could not be read")
                      Document = Some document }
                | _ ->
                    { Availability = SpokeAvailability.Available
                      Document = Some document }
            | found ->
                incompatible
                    $"`praxis {command}` speaks {WorkStateJson.errorContract} version {versionText found}; this hub reads version {WorkStateJson.version}"
        | _ ->
            incompatible
                $"its Praxis does not provide `praxis {command}` ({WorkStateJson.contract} version {WorkStateJson.version}); exit {exit}: {failure}"

    // ---- documents ----

    let availabilityNode (availability: SpokeAvailability) : JsonNode =
        record
            [ "status", text (SpokeAvailability.code availability)
              "reason", optionalText (SpokeAvailability.reason availability) ]

    /// A registered repository: its registration entry (as the registry
    /// records it), its availability and `repositorySource`, the repository's
    /// own state identity the answer was read under.
    let repositoryNode (registration: JsonObject) (availability: SpokeAvailability) (repositorySource: JsonObject) : JsonObject =
        let node = registration.DeepClone().AsObject()
        node["availability"] <- availabilityNode availability
        node["repositorySource"] <- repositorySource.DeepClone()
        node

    let private envelope (kind: string) (fields: (string * JsonNode) list) : JsonNode =
        record ([ "contract", text contract; "version", JsonValue.Create version; "kind", text kind ] @ fields)

    let private documentNode (document: JsonObject option) : JsonNode =
        document |> Option.map (fun value -> value.DeepClone()) |> Option.toObj

    let repositoryListDocument (repositories: JsonObject list) : JsonNode =
        envelope "repository-list" [ "repositories", repositories |> List.map (fun node -> node :> JsonNode) |> array ]

    let repositoryWorkListKind = "repository-work-list"
    let repositoryWorkItemKind = "repository-work-item"
    let repositoryWorkTransitionKind = "repository-work-transition"

    /// One repository's own document (`null` when it could not answer).
    let repositoryDocument (kind: string) (repository: JsonObject) (document: JsonObject option) : JsonNode =
        envelope kind [ "repository", repository; "document", documentNode document ]

    /// Every requested repository, each with its own work-list document or
    /// `null` and the availability that says why.
    let workListDocument (entries: (JsonObject * JsonObject option) list) : JsonNode =
        envelope
            "work-list"
            [ "repositories",
              entries
              |> List.map (fun (repository, document) -> record [ "repository", repository; "document", documentNode document ] :> JsonNode)
              |> array ]

    let repositoryNotRegistered (id: string) : JsonNode =
        WorkStateJson.errorDocument "repository-not-registered" $"no registered repository with id '{id}'" [ "repositoryId", text id ]

    /// A single-repository read or transition whose repository could not
    /// answer; `repository` carries the availability that says why.
    let repositoryUnavailable (repository: JsonObject) (reason: string) : JsonNode =
        WorkStateJson.errorDocument "repository-unavailable" reason [ "repository", repository ]

    let invalidRequest (message: string) : JsonNode =
        WorkStateJson.errorDocument "invalid-request" message []
