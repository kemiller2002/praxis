namespace Ros.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Execution
open Ros.Contracts.Work
open Ros.Domain.Execution
open Ros.Domain.Work
open Ros.Infrastructure.Execution
open Ros.Infrastructure.Work

type EvidenceInput = { Type: string; Path: string }

type CaptureInput =
    { Title: string
      Tags: string list
      Priority: string option
      Description: string option
      Id: string option
      Actor: string option
      Source: string option
      SourceReference: string option }

type UpdateInput =
    { Title: string option
      Description: string option
      /// `Some []` clears the item's tags; `None` leaves them unchanged.
      Tags: string list option
      Priority: string option }

/// Every work operation the web interface offers. Each is exactly one CLI
/// command line (`WebInterface.commandLine`); the web layer adds no rules.
[<RequireQualifiedAccess>]
type WorkOperation =
    | List of tags: string list * status: string option
    | Show of id: string
    | Capture of CaptureInput * Upload list
    | Ready of id: string
    | Block of id: string * reason: string option
    | Abandon of id: string * reason: string option
    /// `work abandon`: the one CLI path that abandons live work.
    | AbandonWork of id: string * reason: string option * actor: string option
    | Update of id: string * UpdateInput
    | Attach of id: string * Upload list
    | Start of id: string * workType: string option * actor: string option
    | Resume of id: string * actor: string option
    | Complete of id: string * evidence: EvidenceInput list * conclusion: string option * actor: string option
    | Validate
    | Status

/// Actions a work row offers, projected from what the kernel reports as
/// allowed (`liveWorkItem.allowedActions`, else `backlogActions`).
[<RequireQualifiedAccess>]
type RowAction =
    | Ready
    | Block
    | Start
    | Abandon
    | Resume
    | Complete

/// The actions `POST /api/v1/work/ID/transitions` accepts.
[<RequireQualifiedAccess>]
type TransitionAction =
    | Ready
    | Block
    | Abandon
    | Start
    | Resume
    | Complete

/// A transition request: the action, the operation its arguments describe
/// (`WebInterface.itemOperation`), and the explicit `actor` argument, if any.
type TransitionIntent =
    { Id: string
      Action: TransitionAction
      Operation: WorkOperation
      Actor: string option }

/// Why a transition request was refused, as a machine-readable category.
[<RequireQualifiedAccess>]
type RefusalCategory =
    | InvalidRequest
    | WorkItemNotFound
    | IllegalTransition
    | ReasonRequired
    | EvidenceRequired
    | TransitionRefused
    | StateUnreadable
    | ExecutionFailed

type TransitionRefusal =
    { Category: RefusalCategory
      Message: string
      RequestedAction: string option
      WorkItemId: string }

/// A read of the versioned control-plane work-state contract
/// (`Ros.Contracts.Work.WorkStateJson`), answered in-process from the
/// recorded state without running any command.
[<RequireQualifiedAccess>]
type StateQuery =
    | List of tags: string list * status: string option
    | Item of id: string

/// A read of executions, receipts, evidence, checkpoints or telemetry
/// (`praxis.execution-state` and the evidence/telemetry kinds of
/// `praxis.work-state`), answered in-process through the same read paths as
/// `execution show|list`, `work context`, `work checkpoint show` and
/// `telemetry usage`.
[<RequireQualifiedAccess>]
type ControlQuery =
    | Executions of workItem: string option
    | Execution of id: string
    | Evidence of workItemId: string * offline: bool
    | Telemetry of workItemId: string

[<RequireQualifiedAccess>]
type WebRoute =
    | Api of WorkOperation
    | State of StateQuery
    | Control of ControlQuery
    | Transition of Result<TransitionIntent, TransitionRefusal>
    | ApiDownload of id: string * attachmentId: string
    | ApiError of status: int * message: string
    | Home of query: (string * string) list
    | Detail of id: string * query: (string * string) list
    | ValidationPage
    | Stylesheet
    | FormPost of WorkOperation * returnTo: string
    | FormError of returnTo: string * message: string
    | NotFound
    | MethodNotAllowed

type StatusSummary =
    { Repository: string
      ProtocolVersion: string
      Validation: string }

[<RequireQualifiedAccess>]
module WebInterface =
    let defaultPort = 4310
    let defaultHost = "127.0.0.1"

    let workTypes = [ "feature"; "bug"; "research"; "mechanical"; "maintenance"; "infrastructure" ]
    let statuses = [ "captured"; "ready"; "blocked"; "active"; "complete"; "abandoned" ]
    let priorities = [ "high"; "medium"; "low" ]

    // ------------------------------------------------------------------
    // Pure: request -> route
    // ------------------------------------------------------------------

    let private queryTags (query: (string * string) list) =
        query |> List.filter (fst >> (=) "tag") |> List.collect (snd >> HttpMessages.splitTags)

    let private queryStatus (query: (string * string) list) =
        HttpMessages.field "status" query |> HttpMessages.nonBlank

    /// `offline=true` (or `1`) reads checkpoints without contacting the remote,
    /// as `work checkpoint show --offline` does.
    let private queryOffline (query: (string * string) list) =
        match HttpMessages.field "offline" query with
        | Some("true" | "1") -> true
        | _ -> false

    let private text name body =
        HttpMessages.jsonString name body |> HttpMessages.nonBlank

    let private evidenceFrom (body: RequestBody) : EvidenceInput list =
        match body with
        | JsonBody(:? JsonObject as node) ->
            match node["evidence"] with
            | :? JsonArray as entries ->
                entries
                |> Seq.choose (fun entry ->
                    match entry with
                    | :? JsonObject as item ->
                        let read (name: string) =
                            match item[name] with
                            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>().Trim())
                            | _ -> None

                        match read "type", read "path" with
                        | Some evidenceType, Some path when evidenceType <> "" && path <> "" -> Some { Type = evidenceType; Path = path }
                        | _ -> None
                    | _ -> None)
                |> Seq.toList
            | _ -> []
        | FormBody(fields, _) ->
            let types = HttpMessages.fieldValues "evidence-type" fields
            let paths = HttpMessages.fieldValues "evidence-path" fields

            List.zip (List.truncate paths.Length types) (List.truncate types.Length paths)
            |> List.map (fun (evidenceType, path) -> evidenceType.Trim(), path.Trim())
            |> List.filter (fun (evidenceType, path) -> evidenceType <> "" && path <> "")
            |> List.map (fun (evidenceType, path) -> { Type = evidenceType; Path = path })
        | _ -> []

    /// The capture input a body describes; a blank title is passed through
    /// so the kernel reports its own "non-empty title" rejection.
    let captureFrom (body: RequestBody) : WorkOperation =
        WorkOperation.Capture(
            { Title = HttpMessages.jsonString "title" body |> Option.map (fun value -> value.Trim()) |> Option.defaultValue ""
              Tags = HttpMessages.stringList "tags" body |> Option.defaultValue []
              Priority = text "priority" body
              Description = text "description" body
              Id = text "id" body
              Actor = text "actor" body
              Source = text "source" body
              SourceReference = text "sourceReference" body },
            HttpMessages.uploads body
        )

    /// The operation `POST .../work/ID/ACTION` names, if any.
    let itemOperation (id: string) (action: string) (body: RequestBody) : WorkOperation option =
        match action with
        | "ready" -> Some(WorkOperation.Ready id)
        | "block" -> Some(WorkOperation.Block(id, text "reason" body))
        | "abandon" -> Some(WorkOperation.Abandon(id, text "reason" body))
        | "update" ->
            Some(
                WorkOperation.Update(
                    id,
                    { Title = text "title" body
                      Description = HttpMessages.jsonString "description" body |> Option.map (fun value -> value.Trim())
                      Tags = HttpMessages.stringList "tags" body
                      Priority = text "priority" body }
                )
            )
        | "attachments" -> Some(WorkOperation.Attach(id, HttpMessages.uploads body))
        | "start" -> Some(WorkOperation.Start(id, text "type" body, text "actor" body))
        | "resume" -> Some(WorkOperation.Resume(id, text "actor" body))
        | "complete" -> Some(WorkOperation.Complete(id, evidenceFrom body, text "conclusion" body, text "actor" body))
        | _ -> None

    let transitionActions =
        [ TransitionAction.Ready
          TransitionAction.Block
          TransitionAction.Abandon
          TransitionAction.Start
          TransitionAction.Resume
          TransitionAction.Complete ]

    let transitionCode (action: TransitionAction) =
        match action with
        | TransitionAction.Ready -> "ready"
        | TransitionAction.Block -> "block"
        | TransitionAction.Abandon -> "abandon"
        | TransitionAction.Start -> "start"
        | TransitionAction.Resume -> "resume"
        | TransitionAction.Complete -> "complete"

    let parseTransitionAction (text: string) =
        transitionActions |> List.tryFind (transitionCode >> (=) text)

    let refusalCode (category: RefusalCategory) =
        match category with
        | RefusalCategory.InvalidRequest -> "invalid-request"
        | RefusalCategory.WorkItemNotFound -> "work-item-not-found"
        | RefusalCategory.IllegalTransition -> "illegal-transition"
        | RefusalCategory.ReasonRequired -> "reason-required"
        | RefusalCategory.EvidenceRequired -> "evidence-required"
        | RefusalCategory.TransitionRefused -> "transition-refused"
        | RefusalCategory.StateUnreadable -> "work-state-unreadable"
        | RefusalCategory.ExecutionFailed -> "execution-failed"

    let refusalStatus (category: RefusalCategory) =
        match category with
        | RefusalCategory.InvalidRequest -> 400
        | RefusalCategory.WorkItemNotFound -> 404
        | RefusalCategory.IllegalTransition
        | RefusalCategory.TransitionRefused -> 409
        | RefusalCategory.ReasonRequired
        | RefusalCategory.EvidenceRequired -> 422
        | RefusalCategory.StateUnreadable
        | RefusalCategory.ExecutionFailed -> 500

    /// The transition a `POST /api/v1/work/ID/transitions` body requests:
    /// `action` plus the same argument fields as the per-action routes.
    let transitionIntent (id: string) (body: RequestBody) : Result<TransitionIntent, TransitionRefusal> =
        let requested = text "action" body
        let refuse message = Error { Category = RefusalCategory.InvalidRequest; Message = message; RequestedAction = requested; WorkItemId = id }
        let expected = transitionActions |> List.map transitionCode |> String.concat ", "

        match requested with
        | None -> refuse $"a transition request requires an action (one of {expected})"
        | Some actionText ->
            match parseTransitionAction actionText, itemOperation id actionText body with
            | Some action, Some operation -> Ok { Id = id; Action = action; Operation = operation; Actor = text "actor" body }
            | _ -> refuse $"unknown action '{actionText}' (expected one of {expected})"

    /// The one CLI command a transition runs under the governing kernel:
    /// live work is abandoned by `work abandon`, backlog items by the
    /// backlog transition; every other action has a single command.
    let transitionOperation (kernel: GoverningKernel) (intent: TransitionIntent) : WorkOperation =
        match kernel, intent.Operation with
        | GoverningKernel.Live, WorkOperation.Abandon(id, reason) -> WorkOperation.AbandonWork(id, reason, intent.Actor)
        | _, operation -> operation

    /// The code the governing kernel's projection uses for an action (the
    /// live kernel calls `start` `begin`).
    let kernelActionCode (kernel: GoverningKernel) (action: TransitionAction) =
        match kernel, action with
        | GoverningKernel.Live, TransitionAction.Start -> "begin"
        | _ -> transitionCode action

    let private suppliedReason (operation: WorkOperation) =
        match operation with
        | WorkOperation.Block(_, reason)
        | WorkOperation.Abandon(_, reason)
        | WorkOperation.AbandonWork(_, reason, _) -> reason
        | _ -> None

    let private suppliedEvidence (operation: WorkOperation) =
        match operation with
        | WorkOperation.Complete(_, evidence, _, _) -> evidence |> List.map _.Type |> Set.ofList
        | _ -> Set.empty

    /// Categorizes a transition the CLI refused (exit status `exit`), from
    /// what the kernels' own projection said of the action before the
    /// request. It never decides whether the transition happens.
    let refusalCategory (item: WorkItemState) (intent: TransitionIntent) (exit: int) : RefusalCategory =
        let code = kernelActionCode item.GovernedBy intent.Action

        match item.Actions |> List.tryFind (fun state -> state.Action = code) |> Option.map _.Availability with
        | None
        | Some(ActionAvailability.Refused _) -> RefusalCategory.IllegalTransition
        | Some(ActionAvailability.Legal requirements) when requirements.Reason && (suppliedReason intent.Operation).IsNone ->
            RefusalCategory.ReasonRequired
        | Some(ActionAvailability.Legal requirements) when not (Set.isSubset (Set.ofList requirements.EvidenceTypes) (suppliedEvidence intent.Operation)) ->
            RefusalCategory.EvidenceRequired
        | Some _ when exit = 2 -> RefusalCategory.InvalidRequest
        | Some _ -> RefusalCategory.TransitionRefused

    let refusalResponse (refusal: TransitionRefusal) : int * JsonNode =
        refusalStatus refusal.Category,
        WorkStateJson.transitionRefusal (refusalCode refusal.Category) refusal.Message refusal.RequestedAction refusal.WorkItemId

    let private detailPath (id: string) = $"/work/{Html.segment id}"

    /// Pure routing: every request maps to exactly one route value.
    let route (request: HttpRequestData) : WebRoute =
        let body () = HttpMessages.parseBody request

        match request.Method, request.Segments with
        | "GET", [ "api"; "v1"; "work" ] -> WebRoute.State(StateQuery.List(queryTags request.Query, queryStatus request.Query))
        | "GET", [ "api"; "v1"; "work"; id ] -> WebRoute.State(StateQuery.Item id)
        | "GET", [ "api"; "v1"; "work"; id; "evidence" ] -> WebRoute.Control(ControlQuery.Evidence(id, queryOffline request.Query))
        | "GET", [ "api"; "v1"; "work"; id; "telemetry" ] -> WebRoute.Control(ControlQuery.Telemetry id)
        | "GET", [ "api"; "v1"; "executions" ] -> WebRoute.Control(ControlQuery.Executions(HttpMessages.field "workItem" request.Query |> HttpMessages.nonBlank))
        | "GET", [ "api"; "v1"; "executions"; id ] -> WebRoute.Control(ControlQuery.Execution id)
        | "POST", [ "api"; "v1"; "work"; id; "transitions" ] ->
            match body () with
            | Error message ->
                WebRoute.Transition(Error { Category = RefusalCategory.InvalidRequest; Message = message; RequestedAction = None; WorkItemId = id })
            | Ok parsed -> WebRoute.Transition(transitionIntent id parsed)
        | "GET", [ "api"; "work" ] -> WebRoute.Api(WorkOperation.List(queryTags request.Query, queryStatus request.Query))
        | "GET", [ "api"; "work"; "ready" ] -> WebRoute.Api(WorkOperation.List(queryTags request.Query, Some "ready"))
        | "GET", [ "api"; "work"; id ] -> WebRoute.Api(WorkOperation.Show id)
        | "GET", [ "api"; "work"; id; "attachments"; attachmentId ] -> WebRoute.ApiDownload(id, attachmentId)
        | "GET", [ "api"; "validate" ] -> WebRoute.Api WorkOperation.Validate
        | "GET", [ "api"; "status" ] -> WebRoute.Api WorkOperation.Status
        | "POST", [ "api"; "work" ] ->
            match body () with
            | Error message -> WebRoute.ApiError(400, message)
            | Ok parsed -> WebRoute.Api(captureFrom parsed)
        | "POST", [ "api"; "work"; id; action ] ->
            match body () with
            | Error message -> WebRoute.ApiError(400, message)
            | Ok parsed ->
                match itemOperation id action parsed with
                | Some operation -> WebRoute.Api operation
                | None -> WebRoute.ApiError(404, $"no route for POST /api/work/{id}/{action}")
        | methodName, "api" :: rest -> WebRoute.ApiError(404, $"""no route for {methodName} /api/{String.Join("/", rest)}""")
        | "GET", []
        | "GET", [ "index.html" ] -> WebRoute.Home request.Query
        | "GET", [ "work"; id ] -> WebRoute.Detail(id, request.Query)
        | "GET", [ "validate" ] -> WebRoute.ValidationPage
        | "GET", [ "styles.css" ] -> WebRoute.Stylesheet
        | "POST", [ "work" ] ->
            match body () with
            | Error message -> WebRoute.FormError("/", message)
            | Ok parsed -> WebRoute.FormPost(captureFrom parsed, "/")
        | "POST", [ "work"; id; action ] ->
            match body () with
            | Error message -> WebRoute.FormError(detailPath id, message)
            | Ok parsed ->
                match itemOperation id action parsed with
                | Some operation -> WebRoute.FormPost(operation, detailPath id)
                | None -> WebRoute.NotFound
        | "GET", _ -> WebRoute.NotFound
        | _ -> WebRoute.MethodNotAllowed

    // ------------------------------------------------------------------
    // Pure: operation -> CLI command line
    // ------------------------------------------------------------------

    let private flag name value =
        match value with
        | Some text -> [ name; text ]
        | None -> []

    let private tagFlags (tags: string list) = tags |> List.collect (fun tag -> [ "--tag"; tag ])

    /// The one CLI command line an operation runs. `now` is the event time the
    /// CLI records; `files` are the uploads already written to disk as
    /// `(path, display name)`.
    let commandLine (now: string) (files: (string * string) list) (operation: WorkOperation) : string list =
        match operation with
        | WorkOperation.List(tags, status) -> [ "work"; "list" ] @ tagFlags tags @ flag "--status" status
        | WorkOperation.Show id -> [ "work"; "show"; id ]
        | WorkOperation.Capture(input, _) ->
            [ "work"; "capture"; "--title"; input.Title; "--occurred-at"; now ]
            @ flag "--id" input.Id
            @ flag "--priority" input.Priority
            @ flag "--description" input.Description
            @ tagFlags input.Tags
            @ flag "--actor" input.Actor
            @ flag "--source" input.Source
            @ flag "--source-reference" input.SourceReference
        | WorkOperation.Ready id -> [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now ]
        | WorkOperation.Block(id, reason) -> [ "work"; "block"; "--id"; id; "--occurred-at"; now ] @ flag "--reason" reason
        | WorkOperation.Abandon(id, reason) ->
            [ "work"; "backlog-transition"; "--id"; id; "--action"; "abandon"; "--occurred-at"; now ]
            @ flag "--reason" reason
        | WorkOperation.AbandonWork(id, reason, actor) ->
            [ "work"; "abandon"; "--id"; id; "--occurred-at"; now ] @ flag "--reason" reason @ flag "--actor" actor
        | WorkOperation.Update(id, input) ->
            // `work update` changes tags whenever `--tag` is present; a bare
            // trailing `--tag` (no value) is its "set tags to none".
            let tags =
                match input.Tags with
                | None -> []
                | Some [] -> [ "--tag" ]
                | Some tags -> tagFlags tags

            [ "work"; "update"; "--id"; id; "--occurred-at"; now ]
            @ flag "--title" input.Title
            @ flag "--description" input.Description
            @ flag "--priority" input.Priority
            @ tags
        | WorkOperation.Attach(id, _) ->
            [ "work"; "attach"; "--id"; id; "--occurred-at"; now ]
            @ (files |> List.collect (fun (path, name) -> [ "--file"; $"{path}={name}" ]))
        | WorkOperation.Start(id, workType, actor) ->
            [ "work"; "start"; "--id"; id; "--occurred-at"; now ] @ flag "--type" workType @ flag "--actor" actor
        | WorkOperation.Resume(id, actor) -> [ "work"; "resume"; "--id"; id; "--occurred-at"; now ] @ flag "--actor" actor
        | WorkOperation.Complete(id, evidence, conclusion, actor) ->
            [ "work"; "complete"; "--id"; id; "--occurred-at"; now ]
            @ (evidence |> List.collect (fun item -> [ "--evidence"; $"{item.Type}={item.Path}" ]))
            @ flag "--conclusion" conclusion
            @ flag "--actor" actor
        | WorkOperation.Validate -> [ "validate"; "--json" ]
        | WorkOperation.Status -> [ "status"; "--json" ]

    /// The item a mutation leaves to show afterwards (`None` for reads and
    /// capture, whose id is only known once the CLI has assigned it).
    let subjectId (operation: WorkOperation) =
        match operation with
        | WorkOperation.Ready id
        | WorkOperation.Block(id, _)
        | WorkOperation.Abandon(id, _)
        | WorkOperation.AbandonWork(id, _, _)
        | WorkOperation.Update(id, _)
        | WorkOperation.Attach(id, _)
        | WorkOperation.Start(id, _, _)
        | WorkOperation.Resume(id, _)
        | WorkOperation.Complete(id, _, _, _) -> Some id
        | _ -> None

    /// Past-tense notice shown after a successful form post.
    let notice (operation: WorkOperation) (id: string) =
        match operation with
        | WorkOperation.Capture _ -> $"Captured {id}."
        | WorkOperation.Ready _ -> $"Marked {id} ready."
        | WorkOperation.Block _ -> $"Blocked {id}."
        | WorkOperation.Abandon _
        | WorkOperation.AbandonWork _ -> $"Abandoned {id}."
        | WorkOperation.Update _ -> $"Updated {id}."
        | WorkOperation.Attach _ -> $"Attached files to {id}."
        | WorkOperation.Start _ -> $"Started {id}."
        | WorkOperation.Resume _ -> $"Resumed {id}."
        | WorkOperation.Complete _ -> $"Completed {id}."
        | _ -> "Done."

    // ------------------------------------------------------------------
    // Pure: CLI JSON -> typed rows
    // ------------------------------------------------------------------

    let private stringOf (node: JsonNode) =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private field (item: JsonObject) (name: string) = item[name] |> Option.ofObj |> Option.bind stringOf

    let private strings (item: JsonObject) (name: string) =
        match item[name] with
        | :? JsonArray as values -> values |> Seq.choose (Option.ofObj >> Option.bind stringOf) |> Seq.toList
        | _ -> []

    /// Reads one row of `work list`/`work show` JSON.
    let parseRow (item: JsonObject) : WorkListRow =
        let attachments =
            match item["attachments"] with
            | :? JsonArray as entries ->
                entries
                |> Seq.choose (fun entry ->
                    match entry with
                    | :? JsonObject as attachment ->
                        Some
                            { Id = field attachment "id" |> Option.defaultValue ""
                              Name = field attachment "name" |> Option.defaultValue ""
                              Size =
                                match attachment["size"] with
                                | :? JsonValue as value when value.GetValueKind() = JsonValueKind.Number -> value.GetValue<int>()
                                | _ -> 0
                              ContentType = field attachment "contentType"
                              UploadedAt = field attachment "uploadedAt" |> Option.defaultValue "" }
                    | _ -> None)
                |> Seq.toList
            | _ -> []

        { Id = field item "id" |> Option.defaultValue ""
          Title = field item "title" |> Option.defaultValue ""
          Description = field item "description"
          Tags = strings item "tags"
          Priority = field item "priority"
          Status = field item "status" |> Option.defaultValue ""
          BlockedReason = field item "blockedReason"
          BacklogActions = strings item "backlogActions"
          Attachments = attachments
          LiveWorkItem =
            match item["liveWorkItem"] with
            | :? JsonObject as live ->
                Some
                    { State = field live "state" |> Option.defaultValue ""
                      SemanticState = field live "semanticState" |> Option.defaultValue ""
                      AllowedActions = strings live "allowedActions" }
            | _ -> None }

    let parseRows (json: string) : Result<WorkListRow list, string> =
        try
            match JsonNode.Parse json with
            | :? JsonArray as rows -> rows |> Seq.choose (fun row -> match row with :? JsonObject as item -> Some(parseRow item) | _ -> None) |> Seq.toList |> Ok
            | _ -> Error "work list did not return an array"
        with error ->
            Error error.Message

    let parseStatus (json: string) : StatusSummary option =
        try
            match JsonNode.Parse json with
            | :? JsonObject as status ->
                Some
                    { Repository = field status "repository" |> Option.defaultValue ""
                      ProtocolVersion = field status "protocolVersion" |> Option.defaultValue ""
                      Validation = field status "validation" |> Option.defaultValue "" }
            | _ -> None
        with _ ->
            None

    /// The actions a row offers: live work reports `allowedActions`, backlog
    /// items `backlogActions` -- the same projection the CLI's list shows.
    let availableActions (row: WorkListRow) : RowAction list =
        match row.LiveWorkItem with
        | Some live ->
            live.AllowedActions
            |> List.choose (function
                | "block" -> Some RowAction.Block
                | "resume" -> Some RowAction.Resume
                | "complete" -> Some RowAction.Complete
                | _ -> None)
        | None ->
            row.BacklogActions
            |> List.choose (function
                | "ready" -> Some RowAction.Ready
                | "block" -> Some RowAction.Block
                | "start" -> Some RowAction.Start
                | "abandon" -> Some RowAction.Abandon
                | _ -> None)

    // ------------------------------------------------------------------
    // Pure: HTML rendering
    // ------------------------------------------------------------------

    let private e = Html.escape

    let statusPill (status: string) =
        $"<span class=\"status-pill status-{e status}\">{e status}</span>"

    let tagList (tags: string list) =
        tags |> List.map (fun tag -> $"<span class=\"tag\">{e tag}</span>") |> String.concat ""

    let private header (status: StatusSummary option) =
        let label =
            match status with
            | Some summary -> $"{e summary.Repository} · protocol {e summary.ProtocolVersion} · validation {e summary.Validation}"
            | None -> ""

        String.concat
            "\n"
            [ "<header>"
              "<h1>Work Backlog</h1>"
              $"<p id=\"repository-label\" class=\"muted\">{label}</p>"
              "<nav><a href=\"/\">Queue</a> · <a href=\"/validate\">Validate</a></nav>"
              "</header>" ]

    let private fileRows (count: int) =
        [ 1..count ]
        |> List.map (fun _ ->
            "<div class=\"file-row\"><input type=\"file\" name=\"file\" /><input type=\"text\" name=\"name\" placeholder=\"name (optional)\" /></div>")
        |> String.concat "\n"

    let private buttonForm (id: string) (action: string) (label: string) =
        $"<form method=\"post\" action=\"/work/{Html.segment id}/{action}\"><button type=\"submit\">{e label}</button></form>"

    let private rowActionCell (row: WorkListRow) =
        let quick =
            availableActions row
            |> List.map (function
                | RowAction.Ready -> buttonForm row.Id "ready" "Mark ready"
                | RowAction.Resume -> buttonForm row.Id "resume" "Resume"
                | RowAction.Block -> $"<a href=\"/work/{Html.segment row.Id}#block\">Block</a>"
                | RowAction.Start -> $"<a href=\"/work/{Html.segment row.Id}#start\">Start</a>"
                | RowAction.Abandon -> $"<a href=\"/work/{Html.segment row.Id}#abandon\">Abandon</a>"
                | RowAction.Complete -> $"<a href=\"/work/{Html.segment row.Id}#complete\">Complete</a>")

        ($"<a href=\"/work/{Html.segment row.Id}\">Show</a>" :: quick) |> String.concat " "

    let private queueRow (row: WorkListRow) =
        String.concat
            ""
            [ $"<tr data-id=\"{e row.Id}\">"
              $"<td><a href=\"/work/{Html.segment row.Id}\">{e row.Id}</a></td>"
              $"<td>{e row.Title}</td>"
              $"<td>{statusPill row.Status}</td>"
              $"<td>{tagList row.Tags}</td>"
              $"""<td>{e (row.Priority |> Option.defaultValue "")}</td>"""
              $"<td class=\"row-actions\">{rowActionCell row}</td>"
              "</tr>" ]

    /// The queue page: capture form, filter, and the (filtered) queue.
    let renderHome
        (status: StatusSummary option)
        (rows: Result<WorkListRow list, string>)
        (query: (string * string) list)
        : string =
        let tagFilter = HttpMessages.field "tag" query |> Option.defaultValue ""
        let statusFilter = HttpMessages.field "status" query |> Option.defaultValue ""

        let table =
            match rows with
            | Error message -> $"<p class=\"error\" role=\"alert\">{e message}</p>"
            | Ok [] -> "<p class=\"muted\">No work items match.</p>"
            | Ok items ->
                String.concat
                    "\n"
                    [ "<table id=\"work-table\">"
                      "<thead><tr><th>ID</th><th>Work</th><th>Status</th><th>Tags</th><th>Priority</th><th>Actions</th></tr></thead>"
                      "<tbody>"
                      items |> List.map queueRow |> String.concat "\n"
                      "</tbody>"
                      "</table>" ]

        Html.page
            "Praxis Work Backlog"
            (String.concat
                "\n"
                [ header status
                  "<main>"
                  Html.flash query
                  "<section id=\"capture\" aria-label=\"Capture new work\">"
                  "<h2>Capture</h2>"
                  "<form id=\"add-form\" method=\"post\" action=\"/work\" enctype=\"multipart/form-data\">"
                  "<input id=\"add-title\" name=\"title\" type=\"text\" placeholder=\"Describe the work\" required />"
                  "<input name=\"tags\" type=\"text\" placeholder=\"tags, comma, separated\" />"
                  $"""<select name="priority">{Html.options "medium" priorities}</select>"""
                  "<textarea name=\"description\" placeholder=\"Optional longer description\"></textarea>"
                  "<div class=\"file-rows\">"
                  fileRows 3
                  "</div>"
                  "<button type=\"submit\" class=\"primary\">Add</button>"
                  "</form>"
                  "</section>"
                  "<section id=\"filters\" aria-label=\"Filter work\">"
                  "<h2>Filter</h2>"
                  "<form method=\"get\" action=\"/\">"
                  $"<label>Tag <input name=\"tag\" type=\"text\" placeholder=\"e.g. wasm\" value=\"{e tagFilter}\" /></label>"
                  $"""<label>Status <select name="status"><option value="">any</option>{Html.options statusFilter statuses}</select></label>"""
                  "<button type=\"submit\">Filter</button> <a href=\"/\">Clear</a>"
                  "</form>"
                  "</section>"
                  "<section id=\"queue\" aria-label=\"Work queue\">"
                  "<h2>Queue</h2>"
                  table
                  "</section>"
                  "</main>" ])

    let private attachmentList (row: WorkListRow) =
        match row.Attachments with
        | [] -> "<p class=\"muted\">No attachments.</p>"
        | attachments ->
            attachments
            |> List.map (fun attachment ->
                let href = $"/api/work/{Html.segment row.Id}/attachments/{Html.segment attachment.Id}"
                $"<li><a href=\"{e href}\" download>{e attachment.Name}</a> ({Html.formatSize (int64 attachment.Size)})</li>")
            |> String.concat "\n"
            |> sprintf "<ul class=\"attachment-list\">\n%s\n</ul>"

    let private actionForm (row: WorkListRow) (action: RowAction) =
        let target name = $"/work/{Html.segment row.Id}/{name}"

        match action with
        | RowAction.Ready -> $"""<section id="ready"><h3>Mark ready</h3>{buttonForm row.Id "ready" "Mark ready"}</section>"""
        | RowAction.Resume -> $"""<section id="resume"><h3>Resume</h3>{buttonForm row.Id "resume" "Resume"}</section>"""
        | RowAction.Block ->
            $"""<section id="block"><h3>Block</h3><form method="post" action="{target "block"}"><textarea name="reason" placeholder="Reason for blocking"></textarea><button type="submit">Block</button></form></section>"""
        | RowAction.Abandon ->
            $"""<section id="abandon"><h3>Abandon</h3><form method="post" action="{target "abandon"}"><textarea name="reason" placeholder="Reason for abandoning"></textarea><button type="submit">Abandon</button></form></section>"""
        | RowAction.Start ->
            $"""<section id="start"><h3>Start</h3><form method="post" action="{target "start"}"><label>Type <select name="type">{Html.options "feature" workTypes}</select></label><button type="submit">Start</button></form></section>"""
        | RowAction.Complete ->
            let evidenceRows =
                [ "implementation"; "tests"; ""; "" ]
                |> List.map (fun placeholder ->
                    let hint = if placeholder = "" then "type" else placeholder
                    $"<div class=\"evidence-row\"><input name=\"evidence-type\" placeholder=\"{hint}\" /><input name=\"evidence-path\" placeholder=\"path\" /></div>")
                |> String.concat ""

            $"""<section id="complete"><h3>Complete</h3><form method="post" action="{target "complete"}">{evidenceRows}<label>Conclusion (research only) <input name="conclusion" type="text" placeholder="e.g. inconclusive" /></label><button type="submit">Complete</button></form></section>"""

    /// One item: summary, attachments, the actions it allows, edit and attach.
    let renderDetail
        (status: StatusSummary option)
        (row: WorkListRow)
        (detail: string option)
        (rawJson: string)
        (query: (string * string) list)
        : string =
        let target name = $"/work/{Html.segment row.Id}/{name}"

        let blocked =
            match row.BlockedReason with
            | Some reason -> $"<p><strong>Blocked:</strong> {e reason}</p>"
            | None -> ""

        let live =
            match row.LiveWorkItem with
            | Some item -> $"<p class=\"muted\">Live work state: {e item.SemanticState}</p>"
            | None -> ""

        let raw =
            match detail with
            | Some text -> $"{rawJson}\n\n{text}"
            | None -> rawJson

        Html.page
            $"{row.Id} · Praxis Work Backlog"
            (String.concat
                "\n"
                [ header status
                  "<main>"
                  Html.flash query
                  "<p><a href=\"/\">&larr; Queue</a></p>"
                  "<section id=\"detail\" aria-label=\"Selected item detail\">"
                  $"<h2>{e row.Id}: {e row.Title}</h2>"
                  $"""<p>{statusPill row.Status} {tagList row.Tags} <span class="muted">{e (row.Priority |> Option.defaultValue "")}</span></p>"""
                  live
                  blocked
                  $"""<p>{e (row.Description |> Option.defaultValue "No description.")}</p>"""
                  attachmentList row
                  "</section>"
                  "<section id=\"actions\" aria-label=\"Allowed actions\">"
                  availableActions row |> List.map (actionForm row) |> String.concat "\n"
                  "</section>"
                  "<section id=\"edit\" aria-label=\"Edit work item\">"
                  "<h3>Edit</h3>"
                  $"""<form method="post" action="{target "update"}">"""
                  $"<label>Title <input name=\"title\" type=\"text\" value=\"{e row.Title}\" /></label>"
                  $"""<label>Description <textarea name="description">{e (row.Description |> Option.defaultValue "")}</textarea></label>"""
                  $"""<label>Tags <input name="tags" type="text" value="{e (String.Join(", ", row.Tags))}" /></label>"""
                  $"""<label>Priority <select name="priority">{Html.options (row.Priority |> Option.defaultValue "medium") priorities}</select></label>"""
                  "<button type=\"submit\">Save</button>"
                  "</form>"
                  "</section>"
                  "<section id=\"attach\" aria-label=\"Attach files\">"
                  "<h3>Attach files</h3>"
                  $"""<form method="post" action="{target "attachments"}" enctype="multipart/form-data">"""
                  "<div class=\"file-rows\">"
                  fileRows 3
                  "</div>"
                  "<button type=\"submit\">Attach</button>"
                  "</form>"
                  "</section>"
                  "<details><summary>Raw record</summary>"
                  $"<pre id=\"detail-body\">{e raw}</pre>"
                  "</details>"
                  "</main>" ])

    let renderMissing (status: StatusSummary option) (message: string) =
        Html.page
            "Not found · Praxis Work Backlog"
            (String.concat "\n" [ header status; "<main>"; $"<p class=\"error\" role=\"alert\">{e message}</p>"; "<p><a href=\"/\">&larr; Queue</a></p>"; "</main>" ])

    /// The `validate --json` result as a findings table.
    let renderValidation (status: StatusSummary option) (result: Result<string, string>) =
        let body =
            match result with
            | Error message -> $"<p class=\"error\" role=\"alert\">{e message}</p>"
            | Ok json ->
                match (try JsonNode.Parse json with _ -> null) with
                | :? JsonObject as report ->
                    let valid =
                        match report["valid"] with
                        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.True -> true
                        | _ -> false

                    let findings =
                        match report["findings"] with
                        | :? JsonArray as entries -> entries |> Seq.choose (fun entry -> match entry with :? JsonObject as finding -> Some finding | _ -> None) |> Seq.toList
                        | _ -> []

                    let rows =
                        findings
                        |> List.map (fun finding ->
                            let get name = field finding name |> Option.defaultValue ""
                            $"""<tr><td>{e (get "severity")}</td><td>{e (get "path")}</td><td>{e (get "field")}</td><td>{e (get "message")}</td><td>{e (get "repair")}</td></tr>""")
                        |> String.concat "\n"

                    let summary =
                        if valid then "<p class=\"notice\">Validation passed.</p>"
                        else "<p class=\"error\" role=\"alert\">Validation failed.</p>"

                    if findings.IsEmpty then
                        summary
                    else
                        $"{summary}\n<table><thead><tr><th>Severity</th><th>Path</th><th>Field</th><th>Message</th><th>Repair</th></tr></thead><tbody>\n{rows}\n</tbody></table>"
                | _ -> $"<pre>{e json}</pre>"

        Html.page
            "Validation · Praxis Work Backlog"
            (String.concat "\n" [ header status; "<main>"; "<section id=\"validation\"><h2>Validation</h2>"; body; "</section>"; "</main>" ])

    // ------------------------------------------------------------------
    // Pure: recorded state -> versioned work-state response
    // ------------------------------------------------------------------

    /// The status and body a state query answers with, given the recorded
    /// sources and a reader for an item's detail markdown.
    let stateResponse
        (sources: Result<WorkStateConfiguration * QueueItemDetail list * LiveWorkItem list, string>)
        (detail: string -> string option)
        (query: StateQuery)
        : int * JsonNode =
        match sources with
        | Error message -> 500, WorkStateJson.errorDocument "work-state-unreadable" message []
        | Ok(configuration, queueItems, contextItems) ->
            match query with
            | StateQuery.List(tags, status) ->
                200, WorkStateJson.listDocument (WorkStateView.projectFiltered configuration queueItems contextItems tags status)
            | StateQuery.Item id ->
                match WorkStateView.project configuration queueItems contextItems |> List.tryFind (fun item -> item.Id = id) with
                | Some item -> 200, WorkStateJson.itemDocument item (detail id)
                | None -> 404, WorkStateJson.workItemNotFound id

    /// Whether the recorded state knows `id`; `None` when it cannot be read
    /// (the CLI then reports its own error).
    let isKnown (sources: Result<WorkStateConfiguration * QueueItemDetail list * LiveWorkItem list, string>) (id: string) : bool option =
        sources
        |> Result.toOption
        |> Option.map (fun (_, queueItems, contextItems) ->
            WorkListView.mergedRows queueItems contextItems |> List.exists (fun row -> row.Id = id))

    /// The execution list: the readable envelopes of the (qualified) work
    /// item, and every stored envelope that could not be read.
    let executionsResponse (qualifiedWorkItem: string option) (envelopes: (string * Result<ExecutionEnvelope, string>) list) : int * JsonNode =
        let unreadable =
            envelopes
            |> List.choose (fun (id, read) ->
                match read with
                | Error reason -> Some(id, reason)
                | Ok _ -> None)

        200, ExecutionStateJson.listDocument qualifiedWorkItem (ExecutionReads.selectReadable qualifiedWorkItem envelopes) unreadable

    let executionResponse (snapshot: Result<ExecutionSnapshot, ExecutionReadFailure>) : int * JsonNode =
        match snapshot with
        | Ok s -> 200, ExecutionStateJson.executionDocument s.Envelope s.Steps s.Effects s.Verification
        | Error(ExecutionReadFailure.NotFound id) -> 404, ExecutionStateJson.executionNotFound id
        | Error(ExecutionReadFailure.Unreadable(id, reason)) -> 500, ExecutionStateJson.executionUnreadable id reason

    let private contextItem (view: JsonObject) =
        match view["workItems"] with
        | :? JsonArray as items -> items |> Seq.tryHead |> Option.bind (function :? JsonObject as item -> Some item | _ -> None)
        | _ -> None

    let private nodesOf (item: JsonObject) (name: string) : JsonNode list =
        match item[name] with
        | :? JsonArray as values -> values |> Seq.choose Option.ofObj |> Seq.map _.DeepClone() |> Seq.toList
        | _ -> []

    let private stringsOf (item: JsonObject) (name: string) : string list =
        nodesOf item name
        |> List.choose (function
            | :? JsonValue as value ->
                match value.TryGetValue<string>() with
                | true, text -> Some text
                | _ -> None
            | _ -> None)

    /// A work item's recorded evidence (from the `work context` view) and its
    /// durable checkpoints (from the `work checkpoint show` read). A known
    /// item with no live context record has never started: neither exists.
    let evidenceResponse
        (id: string)
        (known: bool option)
        (context: unit -> Result<JsonObject, string>)
        (checkpoints: unit -> Result<CheckpointShow, CheckpointShowFailure>)
        : int * JsonNode =
        match known with
        | None -> 500, WorkStateJson.errorDocument "work-state-unreadable" "the recorded work state could not be read" [ "workItemId", JsonValue.Create id ]
        | Some false -> 404, WorkStateJson.workItemNotFound id
        | Some true ->
            match checkpoints () with
            | Error(CheckpointShowFailure.NotInContext _) ->
                let reason = $"work item '{id}' has not been started; evidence and durable checkpoints are recorded on live work"
                200, WorkStateJson.evidenceDocument id (WorkStateJson.unavailable reason) (WorkStateJson.unavailable reason)
            | Error(CheckpointShowFailure.Unreadable message) -> 500, WorkStateJson.errorDocument "work-state-unreadable" message [ "workItemId", JsonValue.Create id ]
            | shown ->
                match context () |> Result.map contextItem with
                | Error message -> 500, WorkStateJson.errorDocument "work-state-unreadable" message [ "workItemId", JsonValue.Create id ]
                | Ok None -> 500, WorkStateJson.errorDocument "work-state-unreadable" $"work item '{id}' is not in repository context" [ "workItemId", JsonValue.Create id ]
                | Ok(Some item) ->
                    let evidence = WorkStateJson.recordedEvidence (nodesOf item "evidence") (stringsOf item "requiredEvidenceForCompletion")

                    let checkpointBlock =
                        match shown with
                        | Ok show -> WorkStateJson.recordedCheckpoints show.RemoteObserved (CheckpointShowReads.continuity show) (CheckpointShowReads.history show)
                        | Error(CheckpointShowFailure.InvalidCheckpoint problems) ->
                            WorkStateJson.unavailable ("the recorded latestCheckpoint is invalid: " + String.concat "; " problems)
                        | Error _ -> WorkStateJson.unavailable "durable checkpoints could not be read"

                    200, WorkStateJson.evidenceDocument id evidence checkpointBlock

    /// Usage and cost coverage over the work item's telemetry executions,
    /// from the same aggregation as `telemetry usage`, plus its
    /// `--by execution` groups.
    let telemetryResponse
        (id: string)
        (known: bool option)
        (registry: unit -> Ros.Domain.Telemetry.MetricDefinition list)
        (byWorkItem: unit -> FileTelemetryUsageRepository.Scope * Ros.Domain.Telemetry.UsageGroup list)
        (byExecution: unit -> Ros.Domain.Telemetry.UsageGroup list)
        : int * JsonNode =
        match known with
        | None -> 500, WorkStateJson.errorDocument "work-state-unreadable" "the recorded work state could not be read" [ "workItemId", JsonValue.Create id ]
        | Some false -> 404, WorkStateJson.workItemNotFound id
        | Some true ->
            let scope, groups = byWorkItem ()
            let executions = scope.ExecutionsByKey |> Map.toList |> List.collect snd |> List.filter ((<>) "") |> List.distinct |> List.sort
            let definitions = registry ()

            let coverage unit =
                Ros.Domain.Telemetry.UsageCoverage.coverage (Ros.Domain.Telemetry.UsageCoverage.metricsWithUnit unit definitions) executions groups
                |> List.map (fun value -> TelemetryUsageJson.coverage value :> JsonNode)

            200,
            WorkStateJson.telemetryDocument
                id
                executions
                (coverage Ros.Domain.Telemetry.UsageCoverage.usageUnit)
                (coverage Ros.Domain.Telemetry.UsageCoverage.costUnit)
                (TelemetryUsageJson.groups (byExecution ()))

    // ------------------------------------------------------------------
    // Effects: run the CLI, serve HTTP
    // ------------------------------------------------------------------

    let private timestamp () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    /// Writes uploads to a private temporary directory for the duration of
    /// `action`, as `(path, display name)`; the directory is always removed.
    let withTempUploads (prefix: string) (uploads: Upload list) (action: (string * string) list -> 'T) : 'T =
        let directory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore

        try
            let files =
                uploads
                |> List.mapi (fun index upload ->
                    let path = Path.Combine(directory, $"upload-{index}")
                    File.WriteAllBytes(path, upload.Data)
                    path, upload.Name)

            action files
        finally
            try
                Directory.Delete(directory, true)
            with _ ->
                ()

    let private runCli (root: string) (arguments: string list) : Result<string, string> =
        match CliProcess.runSelf root arguments with
        | Error message -> Error message
        | Ok result when result.Exit = 0 -> Ok result.Out
        | Ok result -> Error(CliProcess.failureMessage result)

    let private idOf (json: string) =
        try
            match JsonNode.Parse json with
            | :? JsonObject as node -> field node "id"
            | _ -> None
        with _ ->
            None

    /// Executes one operation through the CLI and returns the JSON the API
    /// answers with: the read's own output, or the affected item's `work show`.
    let execute (root: string) (operation: WorkOperation) : Result<string, string> =
        let now = timestamp ()
        let show id = runCli root (commandLine now [] (WorkOperation.Show id))

        match operation with
        | WorkOperation.List _
        | WorkOperation.Show _ -> runCli root (commandLine now [] operation)
        | WorkOperation.Validate
        | WorkOperation.Status ->
            // Both print their JSON report even when they exit non-zero.
            match CliProcess.runSelf root (commandLine now [] operation) with
            | Error message -> Error message
            | Ok result ->
                match (try JsonNode.Parse result.Out |> Option.ofObj with _ -> None) with
                | Some _ -> Ok result.Out
                | None -> Error(CliProcess.failureMessage result)
        | WorkOperation.Capture(_, uploads) ->
            match runCli root (commandLine now [] operation) with
            | Error message -> Error message
            | Ok output ->
                match idOf output with
                | None -> Error "capture did not report the new work item's id"
                | Some id when uploads.IsEmpty -> show id
                | Some id ->
                    withTempUploads "ros-web-upload" uploads (fun files ->
                        runCli root (commandLine now files (WorkOperation.Attach(id, uploads))))
                    |> Result.bind (fun _ -> show id)
        | WorkOperation.Attach(_, []) -> Error "attachments requires at least one uploaded file"
        | WorkOperation.Attach(id, uploads) ->
            withTempUploads "ros-web-upload" uploads (fun files -> runCli root (commandLine now files operation))
            |> Result.bind (fun _ -> show id)
        | _ ->
            match subjectId operation with
            | None -> runCli root (commandLine now [] operation)
            | Some id -> runCli root (commandLine now [] operation) |> Result.bind (fun _ -> show id)

    /// The item's typed state as recorded now.
    let private currentItem (root: string) (id: string) : Result<WorkItemState option, string> =
        FileWorkListRepository.readStateSources root
        |> Result.map (fun (configuration, queueItems, contextItems) ->
            WorkStateView.project configuration queueItems contextItems |> List.tryFind (fun item -> item.Id = id))

    /// Runs a transition request through the CLI command for it and answers
    /// with the item's resulting typed state, or a structured refusal whose
    /// message is the CLI's own. The request's headers and peer never reach
    /// the command: identity is the server environment's, as for the CLI.
    let transition (root: string) (intent: TransitionIntent) : int * JsonNode =
        let action = transitionCode intent.Action

        let refuse category message =
            refusalResponse { Category = category; Message = message; RequestedAction = Some action; WorkItemId = intent.Id }

        match currentItem root intent.Id with
        | Error message -> refuse RefusalCategory.StateUnreadable message
        | Ok None -> refuse RefusalCategory.WorkItemNotFound $"work item '{intent.Id}' was not found"
        | Ok(Some before) ->
            match CliProcess.runSelf root (commandLine (timestamp ()) [] (transitionOperation before.GovernedBy intent)) with
            | Error message -> refuse RefusalCategory.ExecutionFailed message
            | Ok result when result.Exit <> 0 -> refuse (refusalCategory before intent result.Exit) (CliProcess.failureMessage result)
            | Ok _ ->
                match currentItem root intent.Id with
                | Ok(Some after) -> 200, WorkStateJson.transitionDocument action after (FileWorkListRepository.readDetail root intent.Id)
                | Ok None -> refuse RefusalCategory.StateUnreadable $"work item '{intent.Id}' is not in the recorded state after '{action}'"
                | Error message -> refuse RefusalCategory.StateUnreadable message

    /// The stylesheet: the repository's own `web/styles.css` when present
    /// (so a project can restyle it), else the copy compiled into this CLI.
    let stylesheet (root: string) (relative: string) =
        let local = Path.Combine(root, relative)

        if File.Exists local then
            Some(File.ReadAllText local)
        else
            Ros.Infrastructure.Lifecycle.Payload.embeddedText relative

    let private download (root: string) (id: string) (attachmentId: string) =
        match FileWorkListRepository.readAttachment root id attachmentId with
        | Error message -> HttpMessages.jsonError 404 message
        | Ok attachment ->
            let safeName = attachment.Name.Replace('\r', '_').Replace('\n', '_').Replace('"', '_')

            { Status = 200
              ContentType = attachment.ContentType |> Option.defaultValue "application/octet-stream"
              Headers =
                [ "Content-Disposition", $"attachment; filename=\"{safeName}\"; filename*=UTF-8''{Uri.EscapeDataString attachment.Name}" ]
              Body = File.ReadAllBytes attachment.FilePath }

    let private statusSummary root =
        execute root WorkOperation.Status |> Result.toOption |> Option.bind parseStatus

    let private withQuery (path: string) (pairs: (string * string) list) = path + Html.queryString pairs

    /// The HTTP adapter: route, run, render.
    let handle (root: string) (request: HttpRequestData) : HttpResponseData =
        match route request with
        | WebRoute.State query ->
            let status, body = stateResponse (FileWorkListRepository.readStateSources root) (FileWorkListRepository.readDetail root) query
            HttpMessages.jsonNode status body
        | WebRoute.Control query ->
            let known id = isKnown (FileWorkListRepository.readStateSources root) id

            let status, body =
                match query with
                | ControlQuery.Executions workItem ->
                    executionsResponse (workItem |> Option.map (ExecutionReads.qualifyWorkItem root)) (ExecutionReads.envelopes root)
                | ControlQuery.Execution id -> executionResponse (ExecutionReads.tryLoad root id)
                | ControlQuery.Evidence(id, offline) ->
                    evidenceResponse id (known id) (fun () -> FileWorkContextRepository.readContextView root (Some id)) (fun () -> CheckpointShowReads.read root offline id)
                | ControlQuery.Telemetry id ->
                    telemetryResponse
                        id
                        (known id)
                        (fun () -> FileMetricRegistryRepository.read root)
                        (fun () -> FileTelemetryUsageRepository.aggregate root (Some id) Ros.Domain.Telemetry.UsageDimension.WorkItem)
                        (fun () -> FileTelemetryUsageRepository.aggregate root (Some id) Ros.Domain.Telemetry.UsageDimension.Execution |> snd)

            HttpMessages.jsonNode status body
        | WebRoute.Transition request ->
            let status, body =
                match request with
                | Ok intent -> transition root intent
                | Error refusal -> refusalResponse refusal

            HttpMessages.jsonNode status body
        | WebRoute.Api(WorkOperation.Show id) when isKnown (FileWorkListRepository.readStateSources root) id = Some false ->
            HttpMessages.jsonNode 404 (WorkStateJson.workItemNotFound id)
        | WebRoute.Api operation ->
            match execute root operation with
            | Ok json -> HttpMessages.json 200 json
            | Error message -> HttpMessages.jsonError 400 message
        | WebRoute.ApiDownload(id, attachmentId) -> download root id attachmentId
        | WebRoute.ApiError(status, message) -> HttpMessages.jsonError status message
        | WebRoute.Home query ->
            let rows =
                execute root (WorkOperation.List(queryTags query, queryStatus query)) |> Result.bind parseRows

            HttpMessages.html 200 (renderHome (statusSummary root) rows query)
        | WebRoute.Detail(id, query) ->
            match execute root (WorkOperation.Show id) with
            | Error message -> HttpMessages.html 404 (renderMissing (statusSummary root) message)
            | Ok json ->
                match (try JsonNode.Parse json with _ -> null) with
                | :? JsonObject as node ->
                    HttpMessages.html 200 (renderDetail (statusSummary root) (parseRow node) (field node "detail") json query)
                | _ -> HttpMessages.html 500 (renderMissing None "work show returned an unreadable record")
        | WebRoute.ValidationPage ->
            HttpMessages.html 200 (renderValidation (statusSummary root) (execute root WorkOperation.Validate))
        | WebRoute.Stylesheet ->
            match stylesheet root "web/styles.css" with
            | Some css -> HttpMessages.css css
            | None -> HttpMessages.text 404 "not found"
        | WebRoute.FormPost(operation, returnTo) ->
            match execute root operation with
            | Ok json ->
                let id = subjectId operation |> Option.orElse (idOf json) |> Option.defaultValue ""
                HttpMessages.redirect (withQuery (detailPath id) [ "notice", notice operation id ])
            | Error message -> HttpMessages.redirect (withQuery returnTo [ "error", message ])
        | WebRoute.FormError(returnTo, message) -> HttpMessages.redirect (withQuery returnTo [ "error", message ])
        | WebRoute.NotFound -> HttpMessages.text 404 "not found"
        | WebRoute.MethodNotAllowed -> HttpMessages.text 405 "method not allowed"

    /// Parsed `web serve` options.
    let parseServeOptions (defaultPortValue: int) (arguments: string list) : Result<string * int, string> =
        let rec loop remaining (host: string) (port: int) =
            match remaining with
            | [] -> Ok(host, port)
            | "--host" :: value :: rest when not (value.StartsWith "--") -> loop rest value port
            | "--port" :: value :: rest ->
                match Int32.TryParse value with
                | true, parsed when parsed > 0 && parsed < 65536 -> loop rest host parsed
                | _ -> Error $"--port requires a port number (1-65535), got '{value}'"
            | flag :: _ -> Error $"unknown or incomplete option '{flag}'"

        loop arguments defaultHost defaultPortValue

    let serve (root: string) (arguments: string list) : int =
        match parseServeOptions defaultPort arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok(host, port) ->
            HttpHost.serve
                host
                port
                [ $"Praxis web interface: http://{host}:{port} (repository root: {root})"
                  "Bound to localhost by default; this server has no authentication -- do not expose it beyond your own machine without adding one." ]
                (handle root)
