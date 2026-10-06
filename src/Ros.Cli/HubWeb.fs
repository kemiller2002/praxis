namespace Ros.Cli

open System
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// The hub's HTTP adapter: server-rendered pages with plain form posts, plus
/// the JSON API, both calling the same `Hub` functions as `praxis hub`.
[<RequireQualifiedAccess>]
type HubRoute =
    | ListRepos
    | RegisterRepo of path: string option * name: string option
    | UnregisterRepo of id: string
    | ListWork of repo: string option * tags: string list * status: string option
    | CreateWork of repoId: string * RequestBody

[<RequireQualifiedAccess>]
type HubWebRoute =
    | Api of HubRoute
    | ApiError of status: int * message: string
    | Home of query: (string * string) list
    | Stylesheet
    | FormPost of HubRoute
    | FormError of message: string
    | NotFound
    | MethodNotAllowed

[<RequireQualifiedAccess>]
module HubWeb =
    let private queryTags (query: (string * string) list) =
        query |> List.filter (fst >> (=) "tag") |> List.collect (snd >> HttpMessages.splitTags)

    let private filterFrom (query: (string * string) list) =
        HttpMessages.field "repo" query |> HttpMessages.nonBlank,
        queryTags query,
        HttpMessages.field "status" query |> HttpMessages.nonBlank

    let route (request: HttpRequestData) : HubWebRoute =
        let body () = HttpMessages.parseBody request

        let register (parsed: RequestBody) =
            HubRoute.RegisterRepo(HttpMessages.jsonString "path" parsed |> HttpMessages.nonBlank, HttpMessages.jsonString "name" parsed)

        match request.Method, request.Segments with
        | "GET", [ "api"; "repos" ] -> HubWebRoute.Api HubRoute.ListRepos
        | "POST", [ "api"; "repos" ] ->
            match body () with
            | Error message -> HubWebRoute.ApiError(400, message)
            | Ok parsed -> HubWebRoute.Api(register parsed)
        | "DELETE", [ "api"; "repos"; id ] -> HubWebRoute.Api(HubRoute.UnregisterRepo id)
        | "GET", [ "api"; "work" ] ->
            let repo, tags, status = filterFrom request.Query
            HubWebRoute.Api(HubRoute.ListWork(repo, tags, status))
        | "POST", [ "api"; "repos"; id; "work" ] ->
            match body () with
            | Error message -> HubWebRoute.ApiError(400, message)
            | Ok parsed -> HubWebRoute.Api(HubRoute.CreateWork(id, parsed))
        | methodName, "api" :: rest -> HubWebRoute.ApiError(404, $"""no route for {methodName} /api/{String.Join("/", rest)}""")
        | "GET", []
        | "GET", [ "index.html" ] -> HubWebRoute.Home request.Query
        | "GET", [ "styles.css" ] -> HubWebRoute.Stylesheet
        | "POST", [ "repos" ] ->
            match body () with
            | Error message -> HubWebRoute.FormError message
            | Ok parsed -> HubWebRoute.FormPost(register parsed)
        | "POST", [ "repos"; id; "unregister" ] -> HubWebRoute.FormPost(HubRoute.UnregisterRepo id)
        | "POST", [ "work" ] ->
            match body () with
            | Error message -> HubWebRoute.FormError message
            | Ok parsed ->
                match HttpMessages.jsonString "repo" parsed |> HttpMessages.nonBlank with
                | Some repoId -> HubWebRoute.FormPost(HubRoute.CreateWork(repoId, parsed))
                | None -> HubWebRoute.FormError "choose a repository to create the work item in"
        | "GET", _ -> HubWebRoute.NotFound
        | _ -> HubWebRoute.MethodNotAllowed

    /// The create input a request body describes, with uploads already
    /// written to disk as `files`.
    let createInput (body: RequestBody) (files: HubFile list) : HubCreateInput =
        let text name = HttpMessages.jsonString name body |> HttpMessages.nonBlank

        { Title = HttpMessages.jsonString "title" body |> Option.map (fun value -> value.Trim()) |> Option.defaultValue ""
          Tags = HttpMessages.stringList "tags" body |> Option.defaultValue []
          Priority = text "priority"
          Description = text "description"
          Id = text "id"
          Actor = text "actor"
          Files = files }

    /// The `praxis hub` command line that changes the hub's own registry
    /// for a registry operation (`None` for every other operation). `resolve`
    /// makes a registered path absolute against the server's directory, as
    /// the in-process registration always did.
    let registryCommand (resolve: string -> string) (operation: HubRoute) : string list option =
        match operation with
        | HubRoute.RegisterRepo(Some path, name) ->
            Some([ "hub"; "register"; resolve path ] @ (name |> Option.map (fun value -> [ "--name"; value ]) |> Option.defaultValue []))
        | HubRoute.UnregisterRepo id -> Some [ "hub"; "unregister"; id ]
        | _ -> None

    /// Runs this CLI's own `praxis hub register|unregister` against the hub
    /// root: the host never writes the registry itself.
    let private runRegistryCommand (root: string) (arguments: string list) : Result<JsonNode, string> =
        match CliProcess.runSelf root arguments with
        | Error message -> Error message
        | Ok result when result.Exit <> 0 -> Error(CliProcess.failureMessage result)
        | Ok result ->
            try
                match JsonNode.Parse result.Out with
                | null -> Error "praxis hub returned no output"
                | node -> Ok node
            with error ->
                Error $"unreadable output from praxis hub: {error.Message}"

    /// Executes one hub operation, returning the JSON the API answers with.
    let execute (root: string) (operation: HubRoute) : Result<JsonNode, string> =
        match operation, registryCommand IO.Path.GetFullPath operation with
        | _, Some arguments -> runRegistryCommand root arguments
        | HubRoute.ListRepos, _ -> Hub.listRepos root |> Result.map (fun repos -> HubRegistry.reposNode repos :> JsonNode)
        | HubRoute.RegisterRepo(None, _), _ -> Error "register requires a path"
        | HubRoute.RegisterRepo(Some _, _), None
        | HubRoute.UnregisterRepo _, None -> Error "no registry command for this operation"
        | HubRoute.ListWork(repo, tags, status), _ ->
            Hub.listWork root repo tags status
            |> Result.map (fun rows ->
                let array = JsonArray()
                rows |> List.iter (fun row -> array.Add(row: JsonNode))
                array :> JsonNode)
        | HubRoute.CreateWork(repoId, body), _ ->
            // Uploaded bytes touch disk only briefly, as named temp files the
            // spoke's own `work attach` copies into its attachments directory.
            TempUploads.withFiles "ros-hub-upload" (HttpMessages.uploads body) (fun written ->
                let files = written |> List.map (fun (path, name) -> { SourcePath = path; Name = Some name })
                Hub.createWork root repoId (createInput body files) |> Result.map (fun item -> item :> JsonNode))

    // ------------------------------------------------------------------
    // Pure: HTML rendering
    // ------------------------------------------------------------------

    let private e = Html.escape

    let private stringField (row: JsonObject) (name: string) =
        match row[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> value.GetValue<string>()
        | _ -> ""

    let private tagsOf (row: JsonObject) =
        match row["tags"] with
        | :? JsonArray as tags -> tags |> Seq.choose (fun tag -> match tag with :? JsonValue as value -> Some(value.ToString()) | _ -> None) |> Seq.toList
        | _ -> []

    let private workRow (row: JsonObject) =
        let repoLabel = e (stringField row "repoName")

        match row["error"] with
        | :? JsonValue as error ->
            $"<tr class=\"error-row\"><td>{repoLabel}</td><td colspan=\"5\">{e (error.ToString())}</td></tr>"
        | _ ->
            let status = stringField row "status"

            String.concat
                ""
                [ "<tr>"
                  $"<td>{repoLabel}</td>"
                  $"""<td>{e (stringField row "id")}</td>"""
                  $"""<td>{e (stringField row "title")}</td>"""
                  $"<td>{WebInterface.statusPill status}</td>"
                  $"<td>{WebInterface.tagList (tagsOf row)}</td>"
                  $"""<td>{e (stringField row "priority")}</td>"""
                  "</tr>" ]

    let private fileRows =
        [ 1..3 ]
        |> List.map (fun _ ->
            "<div class=\"file-row\"><input type=\"file\" name=\"file\" /><input type=\"text\" name=\"name\" placeholder=\"name (optional)\" /></div>")
        |> String.concat "\n"

    let renderHome (repos: Result<HubRepo list, string>) (work: Result<JsonObject list, string>) (query: (string * string) list) : string =
        let repoFilter = HttpMessages.field "repo" query |> Option.defaultValue ""
        let tagFilter = HttpMessages.field "tag" query |> Option.defaultValue ""
        let statusFilter = HttpMessages.field "status" query |> Option.defaultValue ""
        let registered = repos |> Result.defaultValue []

        let reposTable =
            match repos with
            | Error message -> $"<p class=\"error\" role=\"alert\">{e message}</p>"
            | Ok [] -> "<p class=\"muted\">No repositories registered yet.</p>"
            | Ok entries ->
                entries
                |> List.map (fun repo ->
                    $"<tr><td>{e repo.Id}</td><td>{e repo.Name}</td><td><code>{e repo.Path}</code></td><td><form method=\"post\" action=\"/repos/{Html.segment repo.Id}/unregister\"><button type=\"submit\">Unregister</button></form></td></tr>")
                |> String.concat "\n"
                |> sprintf "<table id=\"repos-table\"><thead><tr><th>ID</th><th>Name</th><th>Path</th><th></th></tr></thead><tbody>\n%s\n</tbody></table>"

        let repoOptions (selected: string) =
            registered
            |> List.map (fun repo ->
                let mark = if repo.Id = selected then " selected" else ""
                $"<option value=\"{e repo.Id}\"{mark}>{e repo.Name} ({e repo.Id})</option>")
            |> String.concat ""

        let workTable =
            match work with
            | Error message -> $"<p class=\"error\" role=\"alert\">{e message}</p>"
            | Ok [] -> "<p class=\"muted\">No work items match.</p>"
            | Ok rows ->
                rows
                |> List.map workRow
                |> String.concat "\n"
                |> sprintf "<table id=\"work-table\"><thead><tr><th>Repo</th><th>ID</th><th>Work</th><th>Status</th><th>Tags</th><th>Priority</th></tr></thead><tbody>\n%s\n</tbody></table>"

        Html.page
            "Praxis Project Administration Hub"
            (String.concat
                "\n"
                [ "<header>"
                  "<h1>Project Administration Hub</h1>"
                  "<p class=\"muted\">Creates work in other Praxis repositories by running their own <code>./praxis</code> -- it never edits a repository's files directly.</p>"
                  "</header>"
                  "<main>"
                  Html.flash query
                  "<section id=\"repos\" aria-label=\"Registered repositories\">"
                  "<h2>Registered repositories</h2>"
                  "<form id=\"register-form\" method=\"post\" action=\"/repos\">"
                  "<input id=\"register-path\" name=\"path\" type=\"text\" placeholder=\"/absolute/path/to/repo\" required />"
                  "<input name=\"name\" type=\"text\" placeholder=\"display name (optional)\" />"
                  "<button type=\"submit\" class=\"primary\">Register</button>"
                  "</form>"
                  reposTable
                  "</section>"
                  "<section id=\"create\" aria-label=\"Create work item in a repository\">"
                  "<h2>Create work item</h2>"
                  "<form id=\"create-form\" method=\"post\" action=\"/work\" enctype=\"multipart/form-data\">"
                  $"<select name=\"repo\" required>{repoOptions repoFilter}</select>"
                  "<input id=\"create-title\" name=\"title\" type=\"text\" placeholder=\"Describe the work\" required />"
                  "<input name=\"tags\" type=\"text\" placeholder=\"tags, comma, separated\" />"
                  $"""<select name="priority">{Html.options "medium" WebInterface.priorities}</select>"""
                  "<textarea name=\"description\" placeholder=\"Optional longer description\"></textarea>"
                  "<div class=\"file-rows\">"
                  fileRows
                  "</div>"
                  "<button type=\"submit\" class=\"primary\">Create</button>"
                  "</form>"
                  "</section>"
                  "<section id=\"filters\" aria-label=\"Filter aggregated work\">"
                  "<h2>Filter</h2>"
                  "<form method=\"get\" action=\"/\">"
                  $"<label>Repo <select name=\"repo\"><option value=\"\">all</option>{repoOptions repoFilter}</select></label>"
                  $"<label>Tag <input name=\"tag\" type=\"text\" placeholder=\"e.g. wasm\" value=\"{e tagFilter}\" /></label>"
                  $"""<label>Status <select name="status"><option value="">any</option>{Html.options statusFilter WebInterface.statuses}</select></label>"""
                  "<button type=\"submit\">Filter</button> <a href=\"/\">Clear</a>"
                  "</form>"
                  "</section>"
                  "<section id=\"queue\" aria-label=\"Aggregated work across repositories\">"
                  "<h2>Aggregated queue</h2>"
                  workTable
                  "</section>"
                  "</main>" ])

    let notice (operation: HubRoute) (result: JsonNode) =
        let field name =
            match result with
            | :? JsonObject as item -> stringField item name
            | _ -> ""

        match operation with
        | HubRoute.RegisterRepo _ -> $"""Registered {field "id"}."""
        | HubRoute.UnregisterRepo id -> $"Unregistered {id}."
        | HubRoute.CreateWork(repoId, _) -> $"""Created {field "id"} in {repoId}."""
        | _ -> "Done."

    // ------------------------------------------------------------------
    // Effects
    // ------------------------------------------------------------------

    let handle (root: string) (request: HttpRequestData) : HttpResponseData =
        match route request with
        | HubWebRoute.Api operation ->
            match execute root operation with
            | Ok node -> HttpMessages.jsonNode 200 node
            | Error message -> HttpMessages.jsonError 400 message
        | HubWebRoute.ApiError(status, message) -> HttpMessages.jsonError status message
        | HubWebRoute.Home query ->
            let repo, tags, status = filterFrom query
            HttpMessages.html 200 (renderHome (Hub.listRepos root) (Hub.listWork root repo tags status) query)
        | HubWebRoute.Stylesheet ->
            match WebInterface.stylesheet root "web-hub/styles.css" with
            | Some css -> HttpMessages.css css
            | None -> HttpMessages.text 404 "not found"
        | HubWebRoute.FormPost operation ->
            match execute root operation with
            | Ok node -> HttpMessages.redirect ("/" + Html.queryString [ "notice", notice operation node ])
            | Error message -> HttpMessages.redirect ("/" + Html.queryString [ "error", message ])
        | HubWebRoute.FormError message -> HttpMessages.redirect ("/" + Html.queryString [ "error", message ])
        | HubWebRoute.NotFound -> HttpMessages.text 404 "not found"
        | HubWebRoute.MethodNotAllowed -> HttpMessages.text 405 "method not allowed"

    let serve (root: string) (arguments: string list) : int =
        match WebInterface.parseServeOptions Hub.defaultPort arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            2
        | Ok(host, port) ->
            HttpHost.serve
                host
                port
                [ $"Praxis hub: http://{host}:{port} (hub root: {root})"
                  "Bound to localhost by default; this server has no authentication and can create work items and run commands in every registered repository -- do not expose it beyond your own machine without adding one." ]
                (ControlPlaneSource.serve (fun () -> FileStateIdentityRepository.read root) (handle root))
