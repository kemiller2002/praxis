namespace Praxis.Cli

open Praxis.Infrastructure.Foundations
open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

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

[<RequireQualifiedAccess>]
type WebRoute =
    | Api of WorkOperation
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

    let private detailPath (id: string) = $"/work/{Html.segment id}"

    /// Pure routing: every request maps to exactly one route value.
    let route (request: HttpRequestData) : WebRoute =
        let body () = HttpMessages.parseBody request

        match request.Method, request.Segments with
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
        | WorkOperation.Abandon _ -> $"Abandoned {id}."
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
    /// Forma status lozenge: the state word is always visible text, so state never relies on color (SAF-FORMA-5).
    let statusPill (status: string) = $"<span class=\"ef-status-lozenge\" data-state=\"{FormaMarkup.lozengeState status}\">{e status}</span>"

    let tagList (tags: string list) =
        tags |> List.map (fun tag -> $"<span class=\"ef-badge\">{e tag}</span>") |> String.concat ""

    let private header (status: StatusSummary option) =
        let label =
            match status with
            | Some summary -> $"{e summary.Repository} · protocol {e summary.ProtocolVersion} · validation {e summary.Validation}"
            | None -> ""

        String.concat
            "\n"
            [ "<header class=\"ef-section\">"
              "<h1>Work Backlog</h1>"
              $"<p id=\"repository-label\" class=\"ef-eyebrow\">{label}</p>"
              "<nav class=\"ef-cluster\" aria-label=\"Primary\"><a href=\"/\">Queue</a> <a href=\"/validate\">Validate</a></nav>"
              "</header>" ]

    let private fileRows (count: int) =
        [ 1..count ]
        |> List.map (fun _ ->
            "<div class=\"ef-cluster\"><input type=\"file\" name=\"file\" aria-label=\"File\" /><input type=\"text\" name=\"name\" aria-label=\"File name (optional)\" placeholder=\"name (optional)\" /></div>")
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
              $"<td data-label=\"Actions\"><div class=\"ef-cluster\">{rowActionCell row}</div></td>"
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
            | Error message -> FormaMarkup.alert message
            | Ok [] -> "<section class=\"ef-empty-state\"><h3>No work items match.</h3></section>"
            | Ok items ->
                String.concat
                    "\n"
                    [ "<div class=\"ef-data-grid\" role=\"region\" tabindex=\"0\" aria-label=\"Work queue\"><table id=\"work-table\">"
                      "<thead><tr><th>ID</th><th>Work</th><th>Status</th><th>Tags</th><th>Priority</th><th>Actions</th></tr></thead>"
                      "<tbody>"
                      items |> List.map queueRow |> String.concat "\n"
                      "</tbody>"
                      "</table></div>" ]

        Html.page
            "Praxis Work Backlog"
            (String.concat
                "\n"
                [ header status
                  "<main id=\"main\" tabindex=\"-1\">"
                  Html.flash query
                  "<section id=\"capture\" class=\"ef-section\" aria-label=\"Capture new work\">"
                  "<h2>Capture</h2>"
                  "<form id=\"add-form\" method=\"post\" action=\"/work\" enctype=\"multipart/form-data\">"
                  "<div class=\"ef-field\"><label class=\"ef-field__label\" for=\"add-title\">Title</label><input id=\"add-title\" name=\"title\" type=\"text\" placeholder=\"Describe the work\" required /></div>"
                  "<div class=\"ef-field\"><label class=\"ef-field__label\" for=\"add-tags\">Tags</label><input id=\"add-tags\" name=\"tags\" type=\"text\" placeholder=\"tags, comma, separated\" /></div>"
                  $"""<div class="ef-field"><label class="ef-field__label" for="add-priority">Priority</label><select id="add-priority" name="priority">{Html.options "medium" priorities}</select></div>"""
                  "<div class=\"ef-field\"><label class=\"ef-field__label\" for=\"add-description\">Description</label><textarea id=\"add-description\" name=\"description\" placeholder=\"Optional longer description\"></textarea></div>"
                  "<div class=\"ef-stack\">"
                  fileRows 3
                  "</div>"
                  "<div class=\"ef-actions\"><button type=\"submit\" data-ef-variant=\"primary\">Add</button></div>"
                  "</form>"
                  "</section>"
                  "<section id=\"filters\" class=\"ef-section\" aria-label=\"Filter work\">"
                  "<h2>Filter</h2>"
                  "<form method=\"get\" action=\"/\" data-refine>"
                  $"<label>Tag <input name=\"tag\" type=\"text\" placeholder=\"e.g. wasm\" value=\"{e tagFilter}\" /></label>"
                  $"""<label>Status <select name="status"><option value="">any</option>{Html.options statusFilter statuses}</select></label>"""
                  "<div class=\"ef-actions\"><button type=\"submit\">Filter</button> <a class=\"ef-button\" href=\"/\">Clear</a></div>"
                  "</form>"
                  "</section>"
                  "<section id=\"queue\" class=\"ef-section\" aria-label=\"Work queue\">"
                  "<h2>Queue</h2>"
                  table
                  "</section>"
                  "</main>" ])

    let private attachmentList (row: WorkListRow) =
        match row.Attachments with
        | [] -> "<p>No attachments.</p>"
        | attachments ->
            attachments
            |> List.map (fun attachment ->
                let href = $"/api/work/{Html.segment row.Id}/attachments/{Html.segment attachment.Id}"
                $"<li><a href=\"{e href}\" download>{e attachment.Name}</a> ({Html.formatSize (int64 attachment.Size)})</li>")
            |> String.concat "\n"
            |> sprintf "<ul>\n%s\n</ul>"

    let private actionForm (row: WorkListRow) (action: RowAction) =
        let target name = $"/work/{Html.segment row.Id}/{name}"

        match action with
        | RowAction.Ready -> $"""<section id="ready"><h3>Mark ready</h3>{buttonForm row.Id "ready" "Mark ready"}</section>"""
        | RowAction.Resume -> $"""<section id="resume"><h3>Resume</h3>{buttonForm row.Id "resume" "Resume"}</section>"""
        | RowAction.Block ->
            $"""<section id="block"><h3>Block</h3><form method="post" action="{target "block"}"><textarea name="reason" aria-label="Reason for blocking" placeholder="Reason for blocking"></textarea><div class="ef-actions"><button type="submit">Block</button></div></form></section>"""
        | RowAction.Abandon ->
            $"""<section id="abandon"><h3>Abandon</h3><form method="post" action="{target "abandon"}"><textarea name="reason" aria-label="Reason for abandoning" placeholder="Reason for abandoning"></textarea><button type="submit">Abandon</button></form></section>"""
        | RowAction.Start ->
            $"""<section id="start"><h3>Start</h3><form method="post" action="{target "start"}"><label>Type <select name="type">{Html.options "feature" workTypes}</select></label><button type="submit">Start</button></form></section>"""
        | RowAction.Complete ->
            let evidenceRows =
                [ "implementation"; "tests"; ""; "" ]
                |> List.map (fun placeholder ->
                    let hint = if placeholder = "" then "type" else placeholder
                    $"<div class=\"ef-cluster\"><input name=\"evidence-type\" aria-label=\"Evidence type\" placeholder=\"{hint}\" /><input name=\"evidence-path\" aria-label=\"Evidence path\" placeholder=\"path\" /></div>")
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
            | Some item -> $"<p>Live work state: {e item.SemanticState}</p>"
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
                  "<main id=\"main\" tabindex=\"-1\">"
                  Html.flash query
                  "<p><a href=\"/\">&larr; Queue</a></p>"
                  "<section id=\"detail\" class=\"ef-section\" aria-label=\"Selected item detail\">"
                  $"<h2>{e row.Id}: {e row.Title}</h2>"
                  $"""<p class="ef-cluster">{statusPill row.Status} {tagList row.Tags} <span>{e (row.Priority |> Option.defaultValue "")}</span></p>"""
                  live
                  blocked
                  $"""<p>{e (row.Description |> Option.defaultValue "No description.")}</p>"""
                  attachmentList row
                  "</section>"
                  "<section id=\"actions\" class=\"ef-section\" aria-label=\"Allowed actions\">"
                  availableActions row |> List.map (actionForm row) |> String.concat "\n"
                  "</section>"
                  "<section id=\"edit\" class=\"ef-section\" aria-label=\"Edit work item\">"
                  "<h3>Edit</h3>"
                  $"""<form method="post" action="{target "update"}">"""
                  $"<label>Title <input name=\"title\" type=\"text\" value=\"{e row.Title}\" /></label>"
                  $"""<label>Description <textarea name="description">{e (row.Description |> Option.defaultValue "")}</textarea></label>"""
                  $"""<label>Tags <input name="tags" type="text" value="{e (String.Join(", ", row.Tags))}" /></label>"""
                  $"""<label>Priority <select name="priority">{Html.options (row.Priority |> Option.defaultValue "medium") priorities}</select></label>"""
                  "<button type=\"submit\">Save</button>"
                  "</form>"
                  "</section>"
                  "<section id=\"attach\" class=\"ef-section\" aria-label=\"Attach files\">"
                  "<h3>Attach files</h3>"
                  $"""<form method="post" action="{target "attachments"}" enctype="multipart/form-data">"""
                  "<div class=\"ef-stack\">"
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
            (String.concat "\n" [ header status; "<main id=\"main\" tabindex=\"-1\">"; FormaMarkup.alert message; "<p><a href=\"/\">&larr; Queue</a></p>"; "</main>" ])

    /// The `validate --json` result as a findings table.
    let renderValidation (status: StatusSummary option) (result: Result<string, string>) =
        let body =
            match result with
            | Error message -> FormaMarkup.alert message
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
                        if valid then "<p><span class=\"ef-status-lozenge\" data-state=\"ok\">Validation passed.</span></p>"
                        else "<p role=\"alert\"><span class=\"ef-status-lozenge\" data-state=\"blocked\">Validation failed.</span></p>"

                    if findings.IsEmpty then
                        summary
                    else
                        $"{summary}\n<div class=\"ef-data-grid\" role=\"region\" tabindex=\"0\" aria-label=\"Validation findings\"><table><thead><tr><th>Severity</th><th>Path</th><th>Field</th><th>Message</th><th>Repair</th></tr></thead><tbody>\n{rows}\n</tbody></table></div>"
                | _ -> $"<pre>{e json}</pre>"

        Html.page
            "Validation · Praxis Work Backlog"
            (String.concat "\n" [ header status; "<main id=\"main\" tabindex=\"-1\">"; "<section id=\"validation\" class=\"ef-section\"><h2>Validation</h2>"; body; "</section>"; "</main>" ])

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

    /// The stylesheet: the repository's own `web/styles.css` when present
    /// (so a project can restyle it), else the copy compiled into this CLI.
    let stylesheet (root: string) (relative: string) =
        let local = Path.Combine(root, relative)

        if File.Exists local then
            Some(File.ReadAllText local)
        else
            Praxis.Infrastructure.Lifecycle.Payload.embeddedText relative

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
                    HttpMessages.html 200 ((renderDetail (statusSummary root) (parseRow node) (field node "detail") json query).Replace("</main>", WebExecutions.workSection (CliProcess.runSelf root) id + "\n</main>"))
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
                  "Bound to localhost by default; this server has no authentication -- do not expose it beyond your own machine without adding one. Listen scope: GET /api/control-plane." ]
                (UrlGate.serve Praxis.Application.Web.UrlState.web (fun request -> WebExecutions.tryHandle { Host = host; Port = port } (CliProcess.runSelf root) request |> Option.defaultWith (fun () -> handle root request)))
