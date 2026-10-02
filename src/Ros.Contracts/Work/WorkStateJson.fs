namespace Ros.Contracts.Work

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Work

/// The versioned control-plane work-state contract (`praxis.work-state`,
/// PRX-CTL-005/PRX-CTL-011) served by `GET /api/v1/work` and
/// `GET /api/v1/work/ID`; documented in `docs/web-interface.md`. A breaking
/// change bumps `version` (and the route's `/v1/`); adding fields does not.
[<RequireQualifiedAccess>]
module WorkStateJson =
    let contract = "praxis.work-state"
    let version = 1
    let errorContract = "praxis.error"

    let private options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let render (node: JsonNode) = node.ToJsonString(options)

    let private text (value: string) : JsonNode = JsonValue.Create value
    let private optionalText (value: string option) : JsonNode = value |> Option.map text |> Option.toObj
    let private boolean (value: bool) : JsonNode = JsonValue.Create value
    let private integer (value: int) : JsonNode = JsonValue.Create value

    let private array (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter result.Add
        result

    let private texts (values: string list) = values |> List.map text |> array

    let private record (fields: (string * JsonNode) list) : JsonObject =
        let result = JsonObject()
        fields |> List.iter (fun (name, value) -> result[name] <- value)
        result

    let governedByCode (kernel: GoverningKernel) =
        match kernel with
        | GoverningKernel.Live -> "live"
        | GoverningKernel.Backlog -> "backlog"

    let private attachmentNode (attachment: WorkAttachmentSummary) : JsonNode =
        record
            [ "id", text attachment.Id
              "name", text attachment.Name
              "size", integer attachment.Size
              "contentType", optionalText attachment.ContentType
              "uploadedAt", text attachment.UploadedAt ]

    let backlogNode (backlog: QueueItemDetail) : JsonNode =
        record
            [ "status", text backlog.Status
              "blockedReason", optionalText backlog.BlockedReason
              "description", optionalText backlog.Description
              "tags", texts backlog.Tags
              "priority", optionalText backlog.Priority
              "attachments", backlog.Attachments |> List.map attachmentNode |> array ]

    let liveNode (live: LiveWorkItem) : JsonNode =
        record
            [ "localState", text live.LocalState
              "semanticState", text (WorkListView.stateCode live.SemanticState)
              "workType", text live.WorkType
              "blockReason", optionalText live.BlockReason
              "evidence", live.Evidence |> List.map (fun evidence -> record [ "type", text evidence.Type; "path", text evidence.Path ] :> JsonNode) |> array
              "updatedAt", optionalText live.UpdatedAt
              "completedAt", optionalText live.CompletedAt
              "telemetryExecutionIds", texts live.TelemetryExecutionIds ]

    let actionNode (action: ActionState) : JsonNode =
        match action.Availability with
        | ActionAvailability.Legal requirements ->
            record
                [ "action", text action.Action
                  "legal", boolean true
                  "requires", record [ "reason", boolean requirements.Reason; "evidenceTypes", texts requirements.EvidenceTypes ] ]
        | ActionAvailability.Refused(code, message) ->
            record
                [ "action", text action.Action
                  "legal", boolean false
                  "refusal", record [ "code", text code; "message", text message ] ]

    /// `{availability: "available", sources, items}` or
    /// `{availability: "unavailable", reason}` -- never a bare empty list.
    let recordedNode (item: 'T -> JsonNode) (recorded: Recorded<'T>) : JsonNode =
        match recorded with
        | Recorded.Available(sources, items) ->
            record [ "availability", text "available"; "sources", texts sources; "items", items |> List.map item |> array ]
        | Recorded.Unavailable reason -> record [ "availability", text "unavailable"; "reason", text reason ]

    let private obligationNode (obligation: RecordedObligation) : JsonNode =
        record
            [ "code", text obligation.Code
              "description", text obligation.Description
              "evidenceTypes", texts obligation.EvidenceTypes ]

    let private unknownNode (unknown: RecordedUnknown) : JsonNode =
        record [ "code", text unknown.Code; "description", text unknown.Description ]

    let itemNode (item: WorkItemState) : JsonObject =
        record
            [ "id", text item.Id
              "title", text item.Title
              "semanticState", text item.SemanticState
              "governedBy", text (governedByCode item.GovernedBy)
              "backlog", item.Backlog |> Option.map backlogNode |> Option.toObj
              "live", item.Live |> Option.map liveNode |> Option.toObj
              "actions", item.Actions |> List.map actionNode |> array
              "obligations", recordedNode obligationNode item.Obligations
              "unknowns", recordedNode unknownNode item.Unknowns ]

    let private envelope (kind: string) (fields: (string * JsonNode) list) : JsonNode =
        record ([ "contract", text contract; "version", integer version; "kind", text kind ] @ fields)

    let listDocument (items: WorkItemState list) : JsonNode =
        envelope "work-list" [ "items", items |> List.map (fun item -> itemNode item :> JsonNode) |> array ]

    let itemDocument (item: WorkItemState) (detail: string option) : JsonNode =
        let node = itemNode item
        node["detail"] <- optionalText detail
        envelope "work-item" [ "item", node ]

    /// The structured error body. `error` keeps the message so clients that
    /// read the older `{"error": "..."}` shape keep working.
    let errorDocument (code: string) (message: string) (fields: (string * JsonNode) list) : JsonNode =
        record ([ "contract", text errorContract; "version", integer version; "code", text code; "error", text message ] @ fields)

    let workItemNotFound (id: string) : JsonNode =
        errorDocument "work-item-not-found" $"work item '{id}' was not found" [ "workItemId", text id ]
