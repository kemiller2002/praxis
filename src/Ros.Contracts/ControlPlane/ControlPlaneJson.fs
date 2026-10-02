namespace Ros.Contracts.ControlPlane

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// What a control-plane document was derived from (PRX-CTL-008): a client
/// compares `stateFingerprint` (a digest of the durable Praxis records) and
/// `commit` to detect a stale view. Nothing here is host state.
type SourceIdentity =
    { Repository: string
      Commit: string option
      Branch: string option
      StateFingerprint: string }

/// A source Praxis may or may not record. `Unavailable` is reported
/// explicitly, never collapsed into an empty value.
[<RequireQualifiedAccess>]
type Availability<'T> =
    | Available of 'T
    | Unavailable of reason: string

/// The machine-readable refusal categories of the control-plane API.
[<RequireQualifiedAccess>]
type RefusalCategory =
    /// The work item, execution or repository does not exist.
    | NotFound
    /// The request itself is unusable (unknown action, malformed arguments).
    | InvalidRequest
    /// The kernel's transition rules do not allow the action from the
    /// recorded state.
    | IllegalTransition
    /// The kernel allows the action only with an argument the request lacks
    /// (a reason, completion evidence).
    | MissingArgument
    /// The CLI's transition path refused the request when it ran.
    | Rejected
    /// The repository's Praxis CLI could not be run.
    | Unavailable
    /// The repository's Praxis CLI does not speak this contract version.
    | Incompatible

/// A refused or failed request. `Code` is the kernel's own rejection code
/// (`illegal-transition`, `block-reason-required`, `missing-evidence`, ...)
/// when it gave one.
type Refusal =
    { Category: RefusalCategory
      Code: string option
      Message: string
      Action: string option
      WorkItemId: string option }

/// The kernel's answer for one action on one item: whether it is legal from
/// the recorded state, what it still requires, and -- when the kernel
/// refuses it and can say why -- that reason.
type ActionView =
    { Action: string
      Legal: bool
      Requires: string list
      Refusal: (string * string) option }

type BacklogStateView =
    { Status: string
      BlockedReason: string option }

type LiveStateView =
    { State: string
      SemanticState: string
      WorkType: string option }

type Obligations =
    { RequiredEvidenceForCompletion: string list
      MissingEvidenceForCompletion: string list
      NextAction: string option }

type WorkItemView =
    { Id: string
      Title: string
      Description: string option
      Tags: string list
      Priority: string option
      SemanticState: string
      Backlog: BacklogStateView option
      Live: LiveStateView option
      BlockedReason: string option
      Actions: ActionView list
      Obligations: Availability<Obligations>
      Unknowns: Availability<string list> }

/// `praxis.control-plane` version 1: the typed documents `praxis
/// control-plane ...` prints and the web and hub hosts relay. See
/// docs/control-plane-api.md.
[<RequireQualifiedAccess>]
module ControlPlaneJson =
    [<Literal>]
    let Schema = "praxis.control-plane"

    [<Literal>]
    let SchemaVersion = 1

    /// The six actions a transition request may name.
    let actions = [ "ready"; "block"; "abandon"; "start"; "resume"; "complete" ]

    let options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let render (node: JsonNode) = node.ToJsonString options

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private opt (value: string option) : JsonNode = value |> Option.map str |> Option.toObj
    let private strings (values: string list) : JsonNode = (let array = JsonArray() in values |> List.iter (str >> array.Add); array)
    let private nodes (values: JsonNode list) : JsonNode = (let array = JsonArray() in values |> List.iter array.Add; array)

    let private obj (fields: (string * JsonNode) list) : JsonObject =
        let node = JsonObject()
        fields |> List.iter (fun (key, value) -> node[key] <- value)
        node

    let categoryCode (category: RefusalCategory) =
        match category with
        | RefusalCategory.NotFound -> "not-found"
        | RefusalCategory.InvalidRequest -> "invalid-request"
        | RefusalCategory.IllegalTransition -> "illegal-transition"
        | RefusalCategory.MissingArgument -> "missing-argument"
        | RefusalCategory.Rejected -> "rejected"
        | RefusalCategory.Unavailable -> "unavailable"
        | RefusalCategory.Incompatible -> "incompatible"

    let tryParseCategory (code: string) =
        [ RefusalCategory.NotFound
          RefusalCategory.InvalidRequest
          RefusalCategory.IllegalTransition
          RefusalCategory.MissingArgument
          RefusalCategory.Rejected
          RefusalCategory.Unavailable
          RefusalCategory.Incompatible ]
        |> List.tryFind (fun category -> categoryCode category = code)

    /// The HTTP status a host answers a refusal with.
    let httpStatus (category: RefusalCategory) =
        match category with
        | RefusalCategory.NotFound -> 404
        | RefusalCategory.InvalidRequest -> 400
        | RefusalCategory.IllegalTransition -> 409
        | RefusalCategory.MissingArgument
        | RefusalCategory.Rejected -> 422
        | RefusalCategory.Unavailable -> 503
        | RefusalCategory.Incompatible -> 502

    let availability (render: 'T -> JsonNode) (value: Availability<'T>) : JsonNode =
        match value with
        | Availability.Available item -> obj [ "status", str "available"; "value", render item ]
        | Availability.Unavailable reason -> obj [ "status", str "unavailable"; "reason", str reason ]

    let source (identity: SourceIdentity) : JsonNode =
        obj
            [ "repository", str identity.Repository
              "commit", opt identity.Commit
              "branch", opt identity.Branch
              "stateFingerprint", str identity.StateFingerprint ]

    let refusal (value: Refusal) : JsonNode =
        obj
            [ "category", str (categoryCode value.Category)
              "code", opt value.Code
              "message", str value.Message
              "action", opt value.Action
              "workItemId", opt value.WorkItemId ]

    let action (view: ActionView) : JsonNode =
        obj
            [ "action", str view.Action
              "legal", JsonValue.Create view.Legal
              "requires", strings view.Requires
              "refusal",
              (match view.Refusal with
               | Some(code, message) -> obj [ "code", str code; "message", str message ] :> JsonNode
               | None -> null) ]

    let obligations (value: Obligations) : JsonNode =
        obj
            [ "requiredEvidenceForCompletion", strings value.RequiredEvidenceForCompletion
              "missingEvidenceForCompletion", strings value.MissingEvidenceForCompletion
              "nextAction", opt value.NextAction ]

    let workItem (view: WorkItemView) : JsonNode =
        obj
            [ "id", str view.Id
              "title", str view.Title
              "description", opt view.Description
              "tags", strings view.Tags
              "priority", opt view.Priority
              "semanticState", str view.SemanticState
              "backlog",
              (view.Backlog
               |> Option.map (fun backlog -> obj [ "status", str backlog.Status; "blockedReason", opt backlog.BlockedReason ] :> JsonNode)
               |> Option.toObj)
              "live",
              (view.Live
               |> Option.map (fun live ->
                   obj [ "state", str live.State; "semanticState", str live.SemanticState; "workType", opt live.WorkType ] :> JsonNode)
               |> Option.toObj)
              "blockedReason", opt view.BlockedReason
              "legalActions", nodes (view.Actions |> List.filter _.Legal |> List.map (fun item -> str item.Action))
              "actions", nodes (view.Actions |> List.map action)
              "obligations", availability obligations view.Obligations
              "unknowns", availability strings view.Unknowns ]

    let workList (views: WorkItemView list) : JsonNode = nodes (views |> List.map workItem)

    /// A successful document: `{schema, schemaVersion, kind, source, data}`.
    let document (kind: string) (identity: SourceIdentity) (data: JsonNode) : JsonNode =
        obj
            [ "schema", str Schema
              "schemaVersion", JsonValue.Create SchemaVersion
              "kind", str kind
              "source", source identity
              "data", data ]

    /// A refused request, still carrying the source it was judged against
    /// when one could be read.
    let refusalDocument (kind: string) (identity: SourceIdentity option) (value: Refusal) : JsonNode =
        obj
            [ "schema", str Schema
              "schemaVersion", JsonValue.Create SchemaVersion
              "kind", str kind
              "source", (identity |> Option.map source |> Option.toObj)
              "refusal", refusal value ]

    /// A hub document aggregates several repositories, each carrying its
    /// own `source`, so the envelope itself has none.
    let aggregate (kind: string) (data: JsonNode) : JsonNode =
        obj
            [ "schema", str Schema
              "schemaVersion", JsonValue.Create SchemaVersion
              "kind", str kind
              "source", null
              "data", data ]

    /// One registered repository as a hub reports it: whether its own Praxis
    /// could answer (`available`), not (`unreachable`), or answered with
    /// another contract version (`incompatible`), and the per-repository
    /// document it answered with.
    let repositoryEntry (id: string) (name: string) (path: string) (outcome: Result<JsonNode, Refusal>) : JsonNode =
        let availability =
            match outcome with
            | Ok _ -> obj [ "status", str "available"; "reason", null ]
            | Error refused when refused.Category = RefusalCategory.Incompatible -> obj [ "status", str "incompatible"; "reason", str refused.Message ]
            | Error refused -> obj [ "status", str "unreachable"; "reason", str refused.Message ]

        obj
            [ "id", str id
              "name", str name
              "path", str path
              "availability", availability
              "document",
              (match outcome with
               | Ok document -> document.DeepClone()
               | Error _ -> null) ]

    // ---- reading (hosts relay documents; they only need their outcome) ----

    let private textOf (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as item ->
            match item[name] with
            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | _ -> None

    /// Whether a parsed document is one this contract version can relay.
    let isCompatible (node: JsonNode) =
        match node with
        | :? JsonObject as item ->
            textOf item "schema" = Some Schema
            && (match item["schemaVersion"] with
                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> value.GetValue<int>() = SchemaVersion
                | _ -> false)
        | _ -> false

    /// The refusal category a document reports, if it is a refusal.
    let refusalCategory (node: JsonNode) : RefusalCategory option =
        match node with
        | :? JsonObject as item ->
            match item["refusal"] with
            | :? JsonObject as refused -> textOf refused "category" |> Option.bind tryParseCategory
            | _ -> None
        | _ -> None

    /// The HTTP status a host answers a relayed document with.
    let statusOf (node: JsonNode) =
        refusalCategory node |> Option.map httpStatus |> Option.defaultValue 200
