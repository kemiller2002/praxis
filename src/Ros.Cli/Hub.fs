namespace Ros.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Work

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

    /// A spoke's launcher: the canonical `praxis`, or the `ros` compatibility
    /// launcher of a repository installed before the Praxis rename.
    let launcherNames = [ "praxis"; "ros" ]

    let resolveLauncher (exists: string -> bool) (repositoryPath: string) : string option =
        launcherNames
        |> List.map (fun name -> System.IO.Path.Combine(repositoryPath, name))
        |> List.tryFind exists

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

    /// `./praxis add` in the spoke. Files are attached with a separate `work
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

    /// A row widened by `repoSource`, the spoke state it was listed from.
    let withSource (source: JsonObject) (row: JsonObject) : JsonObject =
        let copy = row.DeepClone().AsObject()
        copy["repoSource"] <- source.DeepClone()
        copy

    let unavailableSource (reason: string) : JsonObject =
        let node = JsonObject()
        node["unavailable"] <- JsonValue.Create reason
        node

    let errorRow (repo: HubRepo) (message: string) : JsonObject =
        let node = JsonObject()
        node["repoId"] <- JsonValue.Create repo.Id
        node["repoName"] <- JsonValue.Create repo.Name
        node["error"] <- JsonValue.Create message
        node

    // ------------------------------------------------------------------
    // `praxis hub` argument parsing (pure)
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
/// own `./praxis`, the only way the hub ever touches another repository.
[<RequireQualifiedAccess>]
module Hub =
    let defaultPort = 4320

    let usage =
        "Usage: praxis [--root PATH] hub register PATH [--name NAME] | hub unregister ID | hub repos | hub create REPO-ID \"title\" [--tag T] [--priority P] [--description D] [--id ID] [--actor NAME] [--file PATH[=NAME]] | hub work [--repo ID] [--tag T] [--status S] | hub serve [--port N] [--host H]"

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
            Error $"not a Praxis repository (no ros.json found): {resolved}"
        elif (HubRegistry.resolveLauncher File.Exists resolved).IsNone then
            Error $"no './praxis' (or legacy './ros') launcher found in: {resolved}"
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

    /// Runs the spoke's own `./praxis` (or, for a spoke installed before the
    /// rename, its `./ros`) from its own directory and parses its JSON.
    let runSpoke (repo: HubRepo) (arguments: string list) : Result<JsonNode, string> =
        match Directory.Exists repo.Path, HubRegistry.resolveLauncher File.Exists repo.Path with
        | false, _ -> Error $"registered path for '{repo.Id}' no longer exists: {repo.Path}"
        | true, None -> Error $"'{repo.Id}' no longer has a './praxis' (or legacy './ros') launcher at {repo.Path}"
        | true, Some executable ->
            match CliProcess.run repo.Path executable arguments with
            | Error message -> Error $"{repo.Id}: {message}"
            | Ok result when result.Exit <> 0 -> Error $"{repo.Id}: {CliProcess.failureMessage result}"
            | Ok result ->
                try
                    match JsonNode.Parse result.Out with
                    | null -> Error $"{repo.Id}: empty output from ./praxis"
                    | node -> Ok node
                with error ->
                    Error $"{repo.Id}: unreadable output from ./praxis: {error.Message}"

    let private asObject (repo: HubRepo) (node: JsonNode) =
        match node with
        | :? JsonObject as item -> Ok item
        | _ -> Error $"{repo.Id}: ./praxis did not return a work item"

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
                    | _ -> Error $"{repo.Id}: ./praxis add did not report an id"))

    /// The spoke's own `./praxis state identity --json`: the hub reads a
    /// spoke's state identity the same way it reads its work, never from its files.
    let spokeIdentity (repo: HubRepo) : Result<StateIdentity, string> =
        runSpoke repo [ "state"; "identity"; "--json" ] |> Result.bind StateIdentityJson.parse

    /// One spoke's rows, each carrying `repoSource`: the spoke state identity
    /// observed before and after its `work list` (re-read while they differ),
    /// or `{"unavailable": reason}` when the spoke cannot report one.
    let private listWorkIn (repo: HubRepo) (tags: string list) (status: string option) : JsonObject list =
        let list () = runSpoke repo (HubRegistry.listArguments tags status)

        let listed, source =
            match spokeIdentity repo with
            | Error reason -> list (), HubRegistry.unavailableSource reason
            | Ok _ ->
                let identify () =
                    spokeIdentity repo
                    |> Result.defaultWith (fun reason -> { Repository = repo.Id; Commit = None; Branch = None; Fingerprint = Error reason })

                let attributed = StateIdentity.stable identify list ControlPlaneSource.readAttempts
                attributed.Value, StateIdentityJson.sourceNode attributed.Identity attributed.Stable

        match listed with
        | Ok(:? JsonArray as rows) ->
            rows
            |> Seq.choose (fun row ->
                match row with
                | :? JsonObject as item -> Some(HubRegistry.annotate repo item |> HubRegistry.withSource source)
                | _ -> None)
            |> Seq.toList
        | Ok _ -> [ HubRegistry.errorRow repo $"{repo.Id}: ./praxis work list did not return an array" ]
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

    /// `praxis hub ...` (except `serve`, which `HubWeb` owns).
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
            eprintfn "ERROR create requires a repository ID and title, e.g. praxis hub create REPO-ID \"Title\""
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

