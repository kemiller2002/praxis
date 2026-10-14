namespace Praxis.Contracts.Installation

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Installation

/// Wire shapes of the installation-registration client:
/// `.echelon/administration.json` (`echelon.administration/v1`), the request
/// document (`echelon.installation.request/v1`) and the provider's result
/// (`echelon.installation.result/v1`).
[<RequireQualifiedAccess>]
module InstallationJson =
    [<Literal>]
    let ConfigSchema = "echelon.administration/v1"

    [<Literal>]
    let RequestSchema = "echelon.installation.request/v1"

    let options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private str (v: string) : JsonNode = JsonValue.Create v
    let private opt (v: string option) : JsonNode = v |> Option.map str |> Option.toObj

    let private obj (fields: (string * JsonNode) list) : JsonNode =
        let o = JsonObject()
        fields |> List.iter (fun (k, v) -> o[k] <- v)
        o

    let private member' (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as o ->
            let mutable found: JsonNode = null
            if o.TryGetPropertyValue(name, &found) then Option.ofObj found else None
        | _ -> None

    let private text name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None

    let private flag name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<bool>() with
            | true, b -> b
            | _ -> false
        | _ -> false

    /// Parse `.echelon/administration.json`. Relative local stores resolve
    /// against the configuration file's directory.
    let readConfig (baseDirectory: string) (json: string) : Result<AdministrationConfig, string> =
        try
            match JsonNode.Parse json with
            | null -> Error "empty administration configuration"
            | node ->
                if text "schema" node <> Some ConfigSchema then
                    Error $"administration configuration must declare schema {ConfigSchema}"
                else
                    let transport =
                        match member' "transport" node with
                        | None -> Error "administration configuration needs a transport"
                        | Some t ->
                            match text "kind" t with
                            | Some "local" ->
                                let command =
                                    match member' "command" t with
                                    | Some(:? JsonArray as a) ->
                                        a |> Seq.choose (fun x -> match x with :? JsonValue as v -> (match v.TryGetValue<string>() with | true, s -> Some s | _ -> None) | _ -> None) |> Seq.toList
                                    | _ -> [ "administration" ]

                                match text "store" t with
                                | Some store when command.Length > 0 ->
                                    Ok(Transport.LocalExecutable(command, IO.Path.GetFullPath(IO.Path.Combine(baseDirectory, store))))
                                | _ -> Error "a local transport needs a store (the Project Administration checkout)"
                            | Some "github-workflow" ->
                                match text "repository" t with
                                | Some repo when Target.validRepository repo ->
                                    Ok(
                                        Transport.GitHubWorkflow(
                                            repo,
                                            text "workflow" t |> Option.defaultValue "installation-request.yml",
                                            text "ref" t |> Option.defaultValue "main"
                                        )
                                    )
                                | _ -> Error "a github-workflow transport needs repository owner/name"
                            | other -> Error $"unknown administration transport '{defaultArg other String.Empty}'; expected local or github-workflow"

                    let environment = member' "environment" node |> Option.bind (text "id")

                    match environment with
                    | Some env when not (Target.validEnvironment env) -> Error $"environment.id '{env}' is not a valid logical id"
                    | _ ->
                        transport
                        |> Result.map (fun tr ->
                            { Provider = text "provider" node |> Option.defaultValue "project-administration"
                              Required = flag "required" node
                              EnvironmentId = environment
                              Catalog = text "catalog" node |> Option.map (fun c -> IO.Path.GetFullPath(IO.Path.Combine(baseDirectory, c)))
                              Transport = tr })
        with :? JsonException as ex ->
            Error $"administration configuration is not valid JSON: {ex.Message}"

    let request (r: RegistrationRequest) : JsonNode =
        let evidence = JsonArray()

        r.Evidence
        |> List.iter (fun (kind, reference) -> evidence.Add(obj [ "kind", str kind; "reference", str reference; "digest", null ]))

        obj
            [ "schema", str RequestSchema
              "capability", str r.Capability
              "operationId", str r.OperationId
              "correlationId", null
              "occurredAt", str (r.OccurredAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
              "systemId", str r.SystemId
              "systemVersion", str r.SystemVersion
              "observedState", opt r.ObservedState
              "target", obj [ "kind", str (Target.kindToWire r.Target.Kind); "id", str r.Target.Id ]
              "source",
              obj
                  [ "repository", opt r.SourceRepository
                    "distribution", opt r.Distribution
                    "release", opt r.Release
                    "artifact", opt r.Artifact
                    "digest", opt r.Digest ]
              "evidence", evidence
              "actor",
              (match r.ActorKind, r.ActorId with
               | Some kind, Some id when kind <> "unknown" ->
                   obj [ "kind", str kind; "id", str id; "provider", opt r.Provider; "model", opt r.Model; "runtime", opt r.Runtime ]
               | _ -> null)
              "execution",
              (match r.ExecutionId with
               | Some id -> obj [ "id", str id; "workItem", opt r.WorkItem; "repository", opt r.ExecutionRepository ]
               | None -> null) ]

    /// Interpret a provider result document.
    let readResult (json: string) : Result<RegistrationOutcome, string> =
        try
            match JsonNode.Parse json with
            | null -> Error "empty provider result"
            | node ->
                match text "status" node with
                | None -> Error "provider result has no status"
                | Some status ->
                    Ok(RegistrationOutcome.fromResult status (text "eventId" node) (text "operation" node) (text "code" node) (text "message" node))
        with :? JsonException as ex ->
            Error $"provider result is not valid JSON: {ex.Message}"

    let outcome (r: RegistrationRequest option) (o: RegistrationOutcome) (required: bool) (providerResult: JsonNode) : JsonNode =
        let detail =
            match o with
            | RegistrationOutcome.Recorded(eventId, operation) -> [ "eventId", str eventId; "operation", str operation ]
            | RegistrationOutcome.Replayed eventId -> [ "eventId", str eventId ]
            | RegistrationOutcome.Submitted d -> [ "message", str d ]
            | RegistrationOutcome.Refused(code, message)
            | RegistrationOutcome.Invalid(code, message) -> [ "code", str code; "message", str message ]
            | RegistrationOutcome.Unavailable reason
            | RegistrationOutcome.Misconfigured reason -> [ "message", str reason ]
            | RegistrationOutcome.Unchanged -> []

        obj (
            [ "schema", str "praxis.installation.outcome/v1"
              "outcome", str (RegistrationOutcome.toWire o)
              "required", JsonValue.Create required
              "request", (r |> Option.map request |> Option.toObj) ]
            @ detail
            @ [ "providerResult", providerResult ]
        )
