namespace Ros.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

/// One registered repository, in `.ros/hub/registry.json`'s own key order.
type HubRepo =
    { Id: string
      Name: string
      Path: string
      RegisteredAt: string option }

type HubRegistry =
    { SchemaVersion: string
      Repos: HubRepo list }

type HubFile = { SourcePath: string; Name: string option }

type HubCreateInput =
    { Title: string
      Tags: string list
      Priority: string option
      Description: string option
      Id: string option
      Actor: string option
      Files: HubFile list }

/// The project-administration hub's registry: pure model, JSON/Markdown
/// projections and registration decisions. The registry format is the one
/// `.ros/hub/registry.json` has always had, so existing hubs keep working.
[<RequireQualifiedAccess>]
module HubRegistry =
    let empty = { SchemaVersion = "1.0.0"; Repos = [] }

    let private stringOf (node: JsonNode) =
        match node with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private field (item: JsonObject) (name: string) = item[name] |> Option.ofObj |> Option.bind stringOf

    let parse (text: string) : Result<HubRegistry, string> =
        try
            match JsonNode.Parse text with
            | :? JsonObject as root ->
                let repos =
                    match root["repos"] with
                    | :? JsonArray as entries ->
                        entries
                        |> Seq.choose (fun entry ->
                            match entry with
                            | :? JsonObject as repo ->
                                match field repo "id", field repo "path" with
                                | Some id, Some path ->
                                    Some
                                        { Id = id
                                          Name = field repo "name" |> Option.defaultValue id
                                          Path = path
                                          RegisteredAt = field repo "registeredAt" }
                                | _ -> None
                            | _ -> None)
                        |> Seq.toList
                    | _ -> []

                Ok
                    { SchemaVersion = field root "schemaVersion" |> Option.defaultValue empty.SchemaVersion
                      Repos = repos }
            | _ -> Error "registry.json must be a JSON object"
        with error ->
            Error $"registry.json is not valid JSON: {error.Message}"

    let repoNode (repo: HubRepo) : JsonObject =
        let node = JsonObject()
        node["id"] <- JsonValue.Create repo.Id
        node["name"] <- JsonValue.Create repo.Name
        node["path"] <- JsonValue.Create repo.Path
        repo.RegisteredAt |> Option.iter (fun value -> node["registeredAt"] <- JsonValue.Create value)
        node

    let reposNode (repos: HubRepo list) : JsonArray =
        let array = JsonArray()
        repos |> List.iter (fun repo -> array.Add(repoNode repo: JsonNode))
        array

    /// `JSON.stringify(registry, null, 2)` plus a trailing newline.
    let render (registry: HubRegistry) : string =
        let root = JsonObject()
        root["schemaVersion"] <- JsonValue.Create registry.SchemaVersion
        root["repos"] <- reposNode registry.Repos
        HttpMessages.renderJson root + "\n"

    let renderMarkdown (repos: HubRepo list) : string =
        let header = "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n"
        let body = repos |> List.map (fun repo -> $"| {repo.Id} | {repo.Name} | {repo.Path} |") |> String.concat "\n"
        header + body + (if body = "" then "" else "\n")

    /// A spoke is addressed by its own `ros.json` `repository.id`, else its
    /// `name`, else its directory name -- never an id the hub invents.
    let spokeRepositoryId (rosJson: string option) (repoPath: string) : string =
        let fromConfig =
            rosJson
            |> Option.bind (fun text ->
                try
                    match JsonNode.Parse text with
                    | :? JsonObject as config ->
                        let repositoryId =
                            match config["repository"] with
                            | :? JsonObject as repository -> field repository "id"
                            | _ -> None

                        repositoryId |> Option.orElse (field config "name")
                    | _ -> None
                with _ ->
                    None)

        fromConfig
        |> Option.defaultWith (fun () -> Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))

    /// Adds a repository unless its path or id is already registered.
    let register
        (registry: HubRegistry)
        (resolvedPath: string)
        (id: string)
        (name: string option)
        (registeredAt: string)
        : Result<HubRegistry * HubRepo, string> =
        if registry.Repos |> List.exists (fun repo -> repo.Path = resolvedPath) then
            Error $"already registered: {resolvedPath}"
        elif registry.Repos |> List.exists (fun repo -> repo.Id = id) then
            Error $"a repository with id '{id}' is already registered"
        else
            let entry =
                { Id = id
                  Name = name |> Option.map (fun value -> value.Trim()) |> Option.filter ((<>) "") |> Option.defaultValue id
                  Path = resolvedPath
                  RegisteredAt = Some registeredAt }

            Ok({ registry with Repos = registry.Repos @ [ entry ] }, entry)

    let find (registry: HubRegistry) (id: string) : Result<HubRepo, string> =
        match registry.Repos |> List.tryFind (fun repo -> repo.Id = id) with
        | Some repo -> Ok repo
        | None -> Error $"no registered repository with id '{id}'"

    let unregister (registry: HubRegistry) (id: string) : Result<HubRegistry * HubRepo, string> =
        find registry id
        |> Result.map (fun removed -> { registry with Repos = registry.Repos |> List.filter (fun repo -> repo.Id <> id) }, removed)

    // ------------------------------------------------------------------
    // Spoke command lines (pure)
    // ------------------------------------------------------------------

    let private tagFlags (tags: string list) = tags |> List.collect (fun tag -> [ "--tag"; tag ])

    /// `./ros add` in the spoke. Files are attached with a separate `work
    /// attach` (`attachArguments`): `add` itself takes no files.
    let addArguments (input: HubCreateInput) : string list =
        let flag name value =
            match value with
            | Some text -> [ name; text ]
            | None -> []

        [ "add"; input.Title ]
        @ tagFlags input.Tags
        @ flag "--priority" input.Priority
        @ flag "--description" input.Description
        @ flag "--id" input.Id
        @ flag "--actor" input.Actor

    let attachArguments (id: string) (occurredAt: string) (files: HubFile list) : string list =
        [ "work"; "attach"; "--id"; id; "--occurred-at"; occurredAt ]
        @ (files
           |> List.collect (fun file ->
               match file.Name with
               | Some name -> [ "--file"; $"{file.SourcePath}={name}" ]
               | None -> [ "--file"; file.SourcePath ]))

    let listArguments (tags: string list) (status: string option) : string list =
        [ "work"; "list" ]
        @ tagFlags tags
        @ (match status with
           | Some value -> [ "--status"; value ]
           | None -> [])

    /// A spoke row annotated with the repository it came from.
    let annotate (repo: HubRepo) (row: JsonObject) : JsonObject =
        let copy = row.DeepClone().AsObject()
        copy["repoId"] <- JsonValue.Create repo.Id
        copy["repoName"] <- JsonValue.Create repo.Name
        copy

    /// `{ ...added, ...shown, repoId, repoName }`: the created item, widened
    /// by `work show` (which carries attachments) when files were attached.
    let mergeCreated (repo: HubRepo) (added: JsonObject) (shown: JsonObject option) : JsonObject =
        let merged = added.DeepClone().AsObject()

        shown
        |> Option.iter (fun detail ->
            for property in detail do
                merged[property.Key] <-
                    match property.Value with
                    | null -> null
                    | value -> value.DeepClone())

        annotate repo merged

    let errorRow (repo: HubRepo) (message: string) : JsonObject =
        let node = JsonObject()
        node["repoId"] <- JsonValue.Create repo.Id
        node["repoName"] <- JsonValue.Create repo.Name
        node["error"] <- JsonValue.Create message
        node

    // ------------------------------------------------------------------
    // `ros hub` argument parsing (pure)
    // ------------------------------------------------------------------

    /// Every `--tag`/`-t` value, comma-split and de-duplicated in order.
    let tagOptions (arguments: string list) : Result<string list, string> =
        let rec loop remaining acc =
            match remaining with
            | ("--tag" | "-t") :: value :: rest when not (value.StartsWith "--") ->
                loop rest (acc @ (value.Split(',') |> Array.map (fun tag -> tag.Trim()) |> Array.filter ((<>) "") |> Array.toList))
            | ("--tag" | "-t" as flag) :: _ -> Error $"{flag} requires a value"
            | _ :: rest -> loop rest acc
            | [] -> Ok(List.distinct acc)

        loop arguments []

    let fileOptions (arguments: string list) : Result<HubFile list, string> =
        let rec loop remaining acc =
            match remaining with
            | "--file" :: value :: rest when not (value.StartsWith "--") ->
                let file =
                    match value.IndexOf '=' with
                    | separator when separator > 0 ->
                        { SourcePath = value.Substring(0, separator)
                          Name = Some(value.Substring(separator + 1)) }
                    | _ -> { SourcePath = value; Name = None }

                loop rest (file :: acc)
            | "--file" :: _ -> Error "--file requires PATH or PATH=NAME"
            | _ :: rest -> loop rest acc
            | [] -> Ok(List.rev acc)

        loop arguments []

    let option (name: string) (arguments: string list) : Result<string option, string> =
        match arguments |> List.tryFindIndex ((=) name) with
        | None -> Ok None
        | Some index ->
            match arguments |> List.tryItem (index + 1) with
            | Some value when not (value.StartsWith "--") -> Ok(Some value)
            | _ -> Error $"{name} requires a value"

/// The hub's effects: the registry files under `.ros/hub/`, and each spoke's
/// own `./ros`, the only way the hub ever touches another repository.
[<RequireQualifiedAccess>]
module Hub =
    let defaultPort = 4320

    let usage =
        "Usage: ros [--root PATH] hub register PATH [--name NAME] | hub unregister ID | hub repos | hub create REPO-ID \"title\" [--tag T] [--priority P] [--description D] [--id ID] [--actor NAME] [--file PATH[=NAME]] | hub work [--repo ID] [--tag T] [--status S] | hub serve [--port N] [--host H]"

    let private registryPath root = Path.Combine(root, ".ros", "hub", "registry.json")
    let private registryMarkdownPath root = Path.Combine(root, ".ros", "hub", "registry.md")

    let private timestamp () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

    let load (root: string) : Result<HubRegistry, string> =
        let path = registryPath root
        if File.Exists path then HubRegistry.parse (File.ReadAllText path) else Ok HubRegistry.empty

    let private save (root: string) (registry: HubRegistry) =
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath root)) |> ignore
        let utf8 = Text.UTF8Encoding(false)
        File.WriteAllText(registryPath root, HubRegistry.render registry, utf8)
        File.WriteAllText(registryMarkdownPath root, HubRegistry.renderMarkdown registry.Repos, utf8)

    let registerRepo (root: string) (repoPath: string) (name: string option) : Result<HubRepo, string> =
        let resolved = Path.GetFullPath repoPath

        if not (Directory.Exists resolved) then
            Error $"not a directory: {resolved}"
        elif not (File.Exists(Path.Combine(resolved, "ros.json"))) then
            Error $"not a ROS repository (no ros.json found): {resolved}"
        elif not (File.Exists(Path.Combine(resolved, "ros"))) then
            Error $"no './ros' executable found in: {resolved}"
        else
            load root
            |> Result.bind (fun registry ->
                let id = HubRegistry.spokeRepositoryId (Some(File.ReadAllText(Path.Combine(resolved, "ros.json")))) resolved
                HubRegistry.register registry resolved id name (timestamp ()))
            |> Result.map (fun (registry, entry) ->
                save root registry
                entry)

    let unregisterRepo (root: string) (id: string) : Result<HubRepo, string> =
        load root
        |> Result.bind (fun registry -> HubRegistry.unregister registry id)
        |> Result.map (fun (registry, removed) ->
            save root registry
            removed)

    let listRepos (root: string) = load root |> Result.map (fun registry -> registry.Repos)

    let private findRepo root id = load root |> Result.bind (fun registry -> HubRegistry.find registry id)

    /// Runs the spoke's own `./ros` from its own directory and parses its JSON.
    let runSpoke (repo: HubRepo) (arguments: string list) : Result<JsonNode, string> =
        let executable = Path.Combine(repo.Path, "ros")

        if not (Directory.Exists repo.Path) then
            Error $"registered path for '{repo.Id}' no longer exists: {repo.Path}"
        elif not (File.Exists executable) then
            Error $"'{repo.Id}' no longer has a './ros' executable at {repo.Path}"
        else
            match CliProcess.run repo.Path executable arguments with
            | Error message -> Error $"{repo.Id}: {message}"
            | Ok result when result.Exit <> 0 -> Error $"{repo.Id}: {CliProcess.failureMessage result}"
            | Ok result ->
                try
                    match JsonNode.Parse result.Out with
                    | null -> Error $"{repo.Id}: empty output from ./ros"
                    | node -> Ok node
                with error ->
                    Error $"{repo.Id}: unreadable output from ./ros: {error.Message}"

    let private asObject (repo: HubRepo) (node: JsonNode) =
        match node with
        | :? JsonObject as item -> Ok item
        | _ -> Error $"{repo.Id}: ./ros did not return a work item"

    let createWork (root: string) (repoId: string) (input: HubCreateInput) : Result<JsonObject, string> =
        if String.IsNullOrWhiteSpace input.Title then
            Error "create requires a non-empty title"
        else
            findRepo root repoId
            |> Result.bind (fun repo ->
                runSpoke repo (HubRegistry.addArguments input)
                |> Result.bind (asObject repo)
                |> Result.bind (fun added ->
                    match input.Files, added["id"] with
                    | [], _ -> Ok(HubRegistry.mergeCreated repo added None)
                    | files, (:? JsonValue as idValue) ->
                        let id = idValue.ToString()

                        runSpoke repo (HubRegistry.attachArguments id (timestamp ()) files)
                        |> Result.bind (fun _ -> runSpoke repo [ "work"; "show"; id ])
                        |> Result.bind (asObject repo)
                        |> Result.map (fun shown -> HubRegistry.mergeCreated repo added (Some shown))
                    | _ -> Error $"{repo.Id}: ./ros add did not report an id"))

    let private listWorkIn (repo: HubRepo) (tags: string list) (status: string option) : JsonObject list =
        match runSpoke repo (HubRegistry.listArguments tags status) with
        | Ok(:? JsonArray as rows) ->
            rows
            |> Seq.choose (fun row ->
                match row with
                | :? JsonObject as item -> Some(HubRegistry.annotate repo item)
                | _ -> None)
            |> Seq.toList
        | Ok _ -> [ HubRegistry.errorRow repo $"{repo.Id}: ./ros work list did not return an array" ]
        | Error message -> [ HubRegistry.errorRow repo message ]

    /// Best-effort per repository: one unreachable spoke becomes one error
    /// row rather than failing the whole view.
    let listWork (root: string) (repoId: string option) (tags: string list) (status: string option) : Result<JsonObject list, string> =
        let repos =
            match repoId with
            | Some id -> findRepo root id |> Result.map List.singleton
            | None -> listRepos root

        repos |> Result.map (List.collect (fun repo -> listWorkIn repo tags status))

    let private print (node: JsonNode) = printfn "%s" (HttpMessages.renderJson node)

    let private objectsNode (items: JsonObject list) =
        let array = JsonArray()
        items |> List.iter (fun item -> array.Add(item: JsonNode))
        array

    let private report (result: Result<JsonNode, string>) =
        match result with
        | Ok node ->
            print node
            0
        | Error message ->
            eprintfn "ERROR %s" message
            1

    type private ResultBuilder() =
        member _.Bind(result: Result<'T, string>, binder: 'T -> Result<'U, string>) = Result.bind binder result
        member _.Return(value: 'T) : Result<'T, string> = Ok value
        member _.ReturnFrom(result: Result<'T, string>) = result

    let private result = ResultBuilder()

    /// `ros hub ...` (except `serve`, which `HubWeb` owns).
    let run (root: string) (arguments: string list) : int =
        match arguments with
        | "register" :: path :: rest when not (path.StartsWith "--") ->
            report (
                result {
                    let! name = HubRegistry.option "--name" rest
                    let! entry = registerRepo root path name
                    return HubRegistry.repoNode entry :> JsonNode
                }
            )
        | [ "unregister"; id ] -> report (unregisterRepo root id |> Result.map (fun entry -> HubRegistry.repoNode entry :> JsonNode))
        | [ "repos" ] -> report (listRepos root |> Result.map (fun repos -> HubRegistry.reposNode repos :> JsonNode))
        | "create" :: repoId :: title :: rest when not (title.StartsWith "--") ->
            report (
                result {
                    let! tags = HubRegistry.tagOptions rest
                    let! priority = HubRegistry.option "--priority" rest
                    let! description = HubRegistry.option "--description" rest
                    let! id = HubRegistry.option "--id" rest
                    let! actor = HubRegistry.option "--actor" rest
                    let! files = HubRegistry.fileOptions rest

                    let! item =
                        createWork
                            root
                            repoId
                            { Title = title
                              Tags = tags
                              Priority = priority
                              Description = description
                              Id = id
                              Actor = actor
                              Files = files }

                    return item :> JsonNode
                }
            )
        | "create" :: _ ->
            eprintfn "ERROR create requires a repository ID and title, e.g. ros hub create REPO-ID \"Title\""
            1
        | "work" :: rest ->
            report (
                result {
                    let! repoId = HubRegistry.option "--repo" rest
                    let! status = HubRegistry.option "--status" rest
                    let! tags = HubRegistry.tagOptions rest
                    let! rows = listWork root repoId tags status
                    return objectsNode rows :> JsonNode
                }
            )
        | "register" :: _ ->
            eprintfn "ERROR register requires a path"
            1
        | [ "unregister" ] ->
            eprintfn "ERROR unregister requires a repository ID"
            1
        | _ ->
            eprintfn "%s" usage
            2

/// The hub's HTTP adapter: server-rendered pages with plain form posts, plus
/// the JSON API, both calling the same `Hub` functions as `ros hub`.
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

    /// Executes one hub operation, returning the JSON the API answers with.
    let execute (root: string) (operation: HubRoute) : Result<JsonNode, string> =
        match operation with
        | HubRoute.ListRepos -> Hub.listRepos root |> Result.map (fun repos -> HubRegistry.reposNode repos :> JsonNode)
        | HubRoute.RegisterRepo(None, _) -> Error "register requires a path"
        | HubRoute.RegisterRepo(Some path, name) ->
            Hub.registerRepo root path name |> Result.map (fun entry -> HubRegistry.repoNode entry :> JsonNode)
        | HubRoute.UnregisterRepo id -> Hub.unregisterRepo root id |> Result.map (fun entry -> HubRegistry.repoNode entry :> JsonNode)
        | HubRoute.ListWork(repo, tags, status) ->
            Hub.listWork root repo tags status
            |> Result.map (fun rows ->
                let array = JsonArray()
                rows |> List.iter (fun row -> array.Add(row: JsonNode))
                array :> JsonNode)
        | HubRoute.CreateWork(repoId, body) ->
            // Uploaded bytes touch disk only briefly, as named temp files the
            // spoke's own `work attach` copies into its attachments directory.
            WebInterface.withTempUploads "ros-hub-upload" (HttpMessages.uploads body) (fun written ->
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
            "ROS Project Administration Hub"
            (String.concat
                "\n"
                [ "<header>"
                  "<h1>Project Administration Hub</h1>"
                  "<p class=\"muted\">Creates work in other ROS repositories by running their own <code>./ros</code> -- it never edits a repository's files directly.</p>"
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
                [ $"ROS hub: http://{host}:{port} (hub root: {root})"
                  "Bound to localhost by default; this server has no authentication and can create work items and run commands in every registered repository -- do not expose it beyond your own machine without adding one." ]
                (handle root)
