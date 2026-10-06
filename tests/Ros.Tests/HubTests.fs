namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Contracts.Work
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module HubTests =
    /// A spoke: a real initialized repository whose own `./praxis` runs this
    /// build of the CLI, exactly as a released spoke's launcher would.
    let private spoke (prefix: string) =
        let root = CliHarness.initializedRepository prefix None
        let launcher = Path.Combine(root, "praxis")
        File.WriteAllText(launcher, $"#!/bin/sh\nexec dotnet \"{CliHarness.cli}\" \"$@\"\n")

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(
                launcher,
                UnixFileMode.UserRead
                ||| UnixFileMode.UserWrite
                ||| UnixFileMode.UserExecute
                ||| UnixFileMode.GroupRead
                ||| UnixFileMode.GroupExecute
            )

        root

    let private spokeId (root: string) =
        let config = JsonNode.Parse(CliHarness.read root "ros.json")
        Http.text config["repository"] "id"

    /// Runs `test hub spokes`, removing every directory afterwards.
    let private withRepositories (spokeCount: int) (test: string -> string list -> unit) =
        let hub = CliHarness.initializedRepository "ros-hub" (Some "project-administration")
        let spokes = [ 1..spokeCount ] |> List.map (fun index -> spoke $"ros-spoke{index}")

        try
            test hub spokes
        finally
            hub :: spokes |> List.iter CliHarness.removeDirectory

    let private hub (root: string) (arguments: string list) = CliHarness.ros root ("hub" :: arguments)

    let private hubOk (root: string) (arguments: string list) =
        let result = hub root arguments

        if result.Exit <> 0 then
            failwith $"hub {String.Join(' ', arguments)} exited {result.Exit}: {result.Err}"

        JsonNode.Parse result.Out

    let private hubError (root: string) (arguments: string list) =
        let result = hub root arguments
        Assert.equal 1 result.Exit
        result.Err

    let private registryFixture =
        "{\n  \"schemaVersion\": \"1.0.0\",\n  \"repos\": [\n    {\n      \"id\": \"alpha\",\n      \"name\": \"Alpha Repo\",\n      \"path\": \"/srv/alpha\",\n      \"registeredAt\": \"2026-01-02T03:04:05.678Z\"\n    }\n  ]\n}\n"

    let private repo id path =
        { Id = id
          Name = id
          Path = path
          RegisteredAt = Some "2026-01-01T00:00:00.000Z" }

    let private unitTests =
        [ { Name = "hub: a registry written by the Node hub parses and re-renders byte for byte"
            Run =
              fun () ->
                  match HubRegistry.parse registryFixture with
                  | Error message -> failwith message
                  | Ok registry ->
                      Assert.equal
                          [ { Id = "alpha"
                              Name = "Alpha Repo"
                              Path = "/srv/alpha"
                              RegisteredAt = Some "2026-01-02T03:04:05.678Z" } ]
                          registry.Repos

                      Assert.equal registryFixture (HubRegistry.render registry)

                      Assert.equal
                          "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n| alpha | Alpha Repo | /srv/alpha |\n"
                          (HubRegistry.renderMarkdown registry.Repos)

                      Assert.equal "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n" (HubRegistry.renderMarkdown [])
                      Assert.equal "{\n  \"schemaVersion\": \"1.0.0\",\n  \"repos\": []\n}\n" (HubRegistry.render HubRegistry.empty) }

          { Name = "hub: registration rejects a duplicate path or id and defaults the name to the id"
            Run =
              fun () ->
                  let registry = { HubRegistry.empty with Repos = [ repo "alpha" "/srv/alpha" ] }

                  match HubRegistry.register registry "/srv/beta" "beta" (Some "  ") "2026-02-02T00:00:00.000Z" with
                  | Error message -> failwith message
                  | Ok(updated, entry) ->
                      Assert.equal "beta" entry.Name
                      Assert.equal [ "alpha"; "beta" ] (updated.Repos |> List.map (fun item -> item.Id))

                  match HubRegistry.register registry "/srv/alpha" "other" None "t" with
                  | Ok _ -> failwith "a duplicate path must be rejected"
                  | Error message -> Http.contains "already registered" message

                  match HubRegistry.register registry "/srv/elsewhere" "alpha" None "t" with
                  | Ok _ -> failwith "a duplicate id must be rejected"
                  | Error message -> Http.contains "already registered" message

                  match HubRegistry.unregister registry "nope" with
                  | Ok _ -> failwith "an unknown id must be rejected"
                  | Error message -> Http.contains "no registered repository" message }

          { Name = "hub: a spoke is addressed by repository.id, then name, then directory name"
            Run =
              fun () ->
                  Assert.equal "repo-a" (HubRegistry.spokeRepositoryId (Some """{"name":"n","repository":{"id":"repo-a"}}""") "/x/dir")
                  Assert.equal "n" (HubRegistry.spokeRepositoryId (Some """{"name":"n"}""") "/x/dir")
                  Assert.equal "dir" (HubRegistry.spokeRepositoryId (Some "not json") "/x/dir/") }

          { Name = "hub: create and list build the spoke's own command lines"
            Run =
              fun () ->
                  let input =
                      { Title = "Fix it"
                        Tags = [ "a"; "b" ]
                        Priority = Some "high"
                        Description = Some "why"
                        Id = None
                        Actor = None
                        Files = [] }

                  Assert.equal
                      [ "add"; "Fix it"; "--tag"; "a"; "--tag"; "b"; "--priority"; "high"; "--description"; "why" ]
                      (HubRegistry.addArguments input)

                  Assert.equal
                      [ "work"; "attach"; "--id"; "WI-1"; "--occurred-at"; "t"; "--file"; "/tmp/x=notes.md"; "--file"; "/tmp/y" ]
                      (HubRegistry.attachArguments "WI-1" "t" [ { SourcePath = "/tmp/x"; Name = Some "notes.md" }; { SourcePath = "/tmp/y"; Name = None } ])

                  Assert.equal [ "work"; "list"; "--tag"; "a"; "--status"; "ready" ] (HubRegistry.listArguments [ "a" ] (Some "ready"))
                  Assert.equal (Ok [ "a"; "b"; "c" ]) (HubRegistry.tagOptions [ "--tag"; "a,b"; "-t"; "c"; "--tag"; "a" ])
                  Assert.isTrue (Result.isError (HubRegistry.tagOptions [ "--tag" ])) "--tag needs a value"

                  Assert.equal
                      (Ok [ { SourcePath = "p"; Name = Some "n=m" }; { SourcePath = "q"; Name = None } ])
                      (HubRegistry.fileOptions [ "--file"; "p=n=m"; "--file"; "q" ]) }

          { Name = "hub: a created item merges the spoke's added and shown rows, then its repository"
            Run =
              fun () ->
                  let added = JsonNode.Parse("""{"id":"WI-1","title":"T","status":"captured"}""").AsObject()
                  let shown = JsonNode.Parse("""{"id":"WI-1","title":"T","attachments":[{"name":"a"}]}""").AsObject()
                  let merged = HubRegistry.mergeCreated (repo "alpha" "/a") added (Some shown)

                  Assert.equal
                      [ "id"; "title"; "status"; "attachments"; "repoId"; "repoName" ]
                      (merged |> Seq.map (fun property -> property.Key) |> Seq.toList)

                  Assert.equal "alpha" (merged["repoId"].GetValue<string>())
                  Assert.isTrue (not (added.ContainsKey "repoId")) "the spoke's row must not be mutated" }

          { Name = "hub: routes map the JSON API and the form posts"
            Run =
              fun () ->
                  let request methodName path query =
                      { Method = methodName
                        Segments = HttpMessages.pathSegments path
                        Query = query
                        ContentType = None
                        Body = [||] }

                  Assert.equal (HubWebRoute.Api HubRoute.ListRepos) (HubWeb.route (request "GET" "/api/repos" []))
                  Assert.equal (HubWebRoute.Api(HubRoute.UnregisterRepo "a b")) (HubWeb.route (request "DELETE" "/api/repos/a%20b" []))

                  Assert.equal
                      (HubWebRoute.Api(HubRoute.ListWork(Some "alpha", [ "x"; "y" ], Some "ready")))
                      (HubWeb.route (request "GET" "/api/work" [ "repo", "alpha"; "tag", "x,y"; "status", "ready" ]))

                  Assert.equal (HubWebRoute.FormPost(HubRoute.UnregisterRepo "alpha")) (HubWeb.route (request "POST" "/repos/alpha/unregister" []))
                  Assert.equal (HubWebRoute.FormError "choose a repository to create the work item in") (HubWeb.route (request "POST" "/work" []))

                  let page =
                      HubWeb.renderHome
                          (Ok [ repo "<a>" "/p\"q" ])
                          (Ok [ JsonNode.Parse("""{"repoId":"x","repoName":"<i>n</i>","error":"<b>boom</b>"}""").AsObject() ])
                          []

                  Http.contains "&lt;b&gt;boom&lt;/b&gt;" page
                  Http.contains "/p&quot;q" page
                  Assert.isTrue (not (page.Contains "<i>n</i>") && not (page.Contains "<script")) "hub page must escape and carry no script" } ]

    let private cliTests =
        [ { Name = "hub register reads repository.id and rejects duplicates by path or id"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let repoA = List.head spokes
                      let entry = hubOk hubRoot [ "register"; repoA; "--name"; "Alpha" ]
                      Assert.equal "Alpha" (Http.text entry "name")
                      Assert.equal (Path.GetFullPath repoA) (Http.text entry "path")
                      Assert.equal (spokeId repoA) (Http.text entry "id")
                      Http.contains "already registered" (hubError hubRoot [ "register"; repoA ])
                      let repos = (hubOk hubRoot [ "repos" ]).AsArray()
                      Assert.equal [ Path.GetFullPath repoA ] (repos |> Seq.map (fun item -> Http.text item "path") |> Seq.toList)
                      let markdown = CliHarness.read hubRoot ".ros/hub/registry.md"
                      Http.contains $"| {spokeId repoA} | Alpha | {Path.GetFullPath repoA} |" markdown) }

          { Name = "hub register rejects a second repository whose repository.id collides"
            Run =
              fun () ->
                  withRepositories 2 (fun hubRoot spokes ->
                      let repoA, repoB = spokes[0], spokes[1]
                      let config = JsonNode.Parse(CliHarness.read repoB "ros.json")
                      config["repository"]["id"] <- JsonValue.Create(spokeId repoA)
                      CliHarness.write repoB "ros.json" (config.ToJsonString())
                      hubOk hubRoot [ "register"; repoA ] |> ignore
                      Http.contains "already registered" (hubError hubRoot [ "register"; repoB ])) }

          { Name = "hub create reaches a spoke installed before the rename through its legacy ./ros launcher"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let legacy = spokes[0]
                      File.Move(Path.Combine(legacy, "praxis"), Path.Combine(legacy, "ros"), true)
                      let id = spokeId legacy
                      hubOk hubRoot [ "register"; legacy ] |> ignore
                      let created = hubOk hubRoot [ "create"; id; "Legacy spoke item" ]
                      Assert.equal "Legacy spoke item" (Http.text created "title")) }

          { Name = "hub register rejects a non-Praxis directory and a directory without a ./praxis or legacy ./ros launcher"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let plain = CliHarness.temporaryDirectory "not-ros"

                      try
                          Http.contains "not a Praxis repository" (hubError hubRoot [ "register"; plain ])
                          File.Delete(Path.Combine(spokes[0], "praxis"))
                          File.Delete(Path.Combine(spokes[0], "ros"))
                          Http.contains "no './praxis' (or legacy './ros') launcher" (hubError hubRoot [ "register"; spokes[0] ])
                          Http.contains "not a directory" (hubError hubRoot [ "register"; Path.Combine(plain, "missing") ])
                      finally
                          CliHarness.removeDirectory plain) }

          { Name = "hub create shells out to the spoke's own ./praxis and the item lands in its real queue"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let repoA = List.head spokes
                      let id = Http.text (hubOk hubRoot [ "register"; repoA ]) "id"
                      let design = Path.Combine(repoA, "design-note.md")
                      File.WriteAllText(design, "hello from the hub\n")

                      let item =
                          hubOk
                              hubRoot
                              [ "create"; id; "Investigate payload growth"; "--tag"; "wasm"; "--tag"; "perf"; "--priority"; "high"
                                "--description"; "Grows superlinearly."; "--file"; $"{design}=notes.md" ]

                      Assert.equal "WI-0001" (Http.text item "id")
                      Assert.equal id (Http.text item "repoId")
                      Assert.equal "notes.md" (Http.text (item["attachments"].AsArray()[0]) "name")
                      let queue = JsonNode.Parse(CliHarness.read repoA ".ros/work/queue.json")
                      let created = queue["items"].AsArray() |> Seq.find (fun entry -> Http.text entry "id" = "WI-0001")
                      Assert.equal "Investigate payload growth" (Http.text created "title")
                      Assert.equal "high" (Http.text created "priority")
                      Assert.equal [ "wasm"; "perf" ] (Http.strings created "tags")
                      Assert.equal "Grows superlinearly." (Http.text created "description")) }

          { Name = "hub create against an unknown repository id fails clearly"
            Run =
              fun () ->
                  withRepositories 0 (fun hubRoot _ ->
                      Http.contains "no registered repository" (hubError hubRoot [ "create"; "nope"; "x" ])
                      Assert.equal 2 (hub hubRoot [ "bogus" ]).Exit) }

          { Name = "hub work merges rows across repositories and isolates one broken repository's error"
            Run =
              fun () ->
                  withRepositories 2 (fun hubRoot spokes ->
                      let idA = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      let idB = Http.text (hubOk hubRoot [ "register"; spokes[1] ]) "id"
                      hubOk hubRoot [ "create"; idA; "A item" ] |> ignore
                      hubOk hubRoot [ "create"; idB; "B item" ] |> ignore
                      let combined = (hubOk hubRoot [ "work"; "--status"; "captured" ]).AsArray() |> Seq.toList
                      let titleIn repoId = combined |> List.find (fun row -> Http.text row "repoId" = repoId) |> fun row -> Http.text row "title"
                      Assert.equal "A item" (titleIn idA)
                      Assert.equal "B item" (titleIn idB)
                      let onlyA = (hubOk hubRoot [ "work"; "--repo"; idA ]).AsArray() |> Seq.toList
                      Assert.isTrue (onlyA |> List.forall (fun row -> Http.text row "repoId" = idA)) "--repo filters to one repository"
                      let moved = spokes[1] + "-moved"
                      Directory.Move(spokes[1], moved)

                      try
                          let afterMove = (hubOk hubRoot [ "work" ]).AsArray() |> Seq.toList
                          Assert.isTrue (afterMove |> List.exists (fun row -> Http.text row "repoId" = idA && isNull row["error"])) "the healthy repository is unaffected"
                          let broken = afterMove |> List.find (fun row -> Http.text row "repoId" = idB)
                          Http.contains "no longer exists" (Http.text broken "error")
                      finally
                          CliHarness.removeDirectory moved) }

          { Name = "hub unregister removes a repository and create against it fails afterwards"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let id = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      Assert.equal id (Http.text (hubOk hubRoot [ "unregister"; id ]) "id")
                      Assert.equal 0 ((hubOk hubRoot [ "repos" ]).AsArray().Count)
                      Http.contains "no registered repository" (hubError hubRoot [ "create"; id; "x" ])
                      Assert.equal "# Registered Repositories\n\n| ID | Name | Path |\n|---|---|---|\n" (CliHarness.read hubRoot ".ros/hub/registry.md")) }

          { Name = "hub keeps an existing registry.json compatible: entries survive a later registration unchanged"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      CliHarness.write hubRoot ".ros/hub/registry.json" registryFixture
                      let repos = (hubOk hubRoot [ "repos" ]).AsArray()
                      Assert.equal "Alpha Repo" (Http.text repos[0] "name")
                      let id = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      let written = CliHarness.read hubRoot ".ros/hub/registry.json"
                      Assert.isTrue (written.StartsWith(registryFixture.Substring(0, registryFixture.Length - "\n  ]\n}\n".Length))) "the existing entry must be preserved byte for byte"
                      Assert.isTrue (written.EndsWith "\n") "registry.json ends with a newline"
                      let registry = JsonNode.Parse written
                      Assert.equal [ "alpha"; id ] (registry["repos"].AsArray() |> Seq.map (fun item -> Http.text item "id") |> Seq.toList)
                      // A repository the hub cannot reach is one error row, not a failure.
                      let rows = (hubOk hubRoot [ "work" ]).AsArray() |> Seq.toList
                      Http.contains "no longer exists" (Http.text (rows |> List.find (fun row -> Http.text row "repoId" = "alpha")) "error")) } ]

    let private scaffoldTests =
        [ { Name = "hub scaffold: the project-administration profile ships no Node toolchain; ./praxis-hub (and the ros-hub alias) run ./praxis hub"
            Run =
              fun () ->
                  withRepositories 0 (fun hubRoot _ ->
                      let nodeArtifacts =
                          Directory.EnumerateFiles(hubRoot, "*", SearchOption.AllDirectories)
                          |> Seq.map (fun path -> Path.GetRelativePath(hubRoot, path).Replace('\\', '/'))
                          |> Seq.filter (fun path -> not (path.StartsWith ".git/"))
                          |> Seq.filter (fun path ->
                              [ ".js"; ".mjs"; ".ts" ] |> List.contains (Path.GetExtension path)
                              || [ "package.json"; "tsconfig.json" ] |> List.contains (Path.GetFileName path))
                          |> Seq.toList

                      Assert.empty nodeArtifacts
                      Assert.isTrue (File.Exists(Path.Combine(hubRoot, "web-hub", "styles.css"))) "the hub stylesheet is scaffolded"
                      let launcher = Path.Combine(hubRoot, "praxis")
                      File.WriteAllText(launcher, $"#!/bin/sh\nexec dotnet \"{CliHarness.cli}\" \"$@\"\n")

                      if not (OperatingSystem.IsWindows()) then
                          File.SetUnixFileMode(launcher, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
                          for hubLauncher in [ "praxis-hub"; "ros-hub" ] do
                              let result = CliHarness.runIn (Some(Path.GetTempPath())) (Path.Combine(hubRoot, hubLauncher)) [ "repos" ] []
                              Assert.equal 0 result.Exit
                              Assert.equal "[]" (result.Out.Trim())) } ]

    let private serverTests =
        [ { Name = "hub serve: register, create (JSON and multipart with a file), aggregate, unregister"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let path = JsonObject()
                      path["path"] <- JsonValue.Create spokes[0]
                      path["name"] <- JsonValue.Create "Alpha"
                      let registered = server.PostJson("/api/repos", path.ToJsonString())
                      let repoText = Http.body registered
                      Assert.equal 200 (Http.status registered)
                      let repoId = Http.text (JsonNode.Parse repoText) "id"
                      let created = server.PostJson($"/api/repos/{repoId}/work", """{"title":"Investigate growth","tags":["wasm"],"priority":"high"}""")
                      Assert.equal 200 (Http.status created)
                      Assert.equal "WI-0001" (Http.text (Http.json created) "id")

                      let uploadFiles () =
                          Directory.GetDirectories(Path.GetTempPath(), "ros-hub-upload-*") |> Set.ofArray

                      let before = uploadFiles ()

                      let withFile =
                          server.PostMultipart($"/api/repos/{repoId}/work", [ "title", "With an attachment"; "tags", "cleanup,wasm" ], [ "spec.md", "hub upload content" ])

                      let withFileText = Http.body withFile
                      Assert.equal 200 (Http.status withFile)
                      let withFileBody = JsonNode.Parse withFileText
                      Assert.equal "spec.md" (Http.text (withFileBody["attachments"].AsArray()[0]) "name")
                      Assert.equal [ "cleanup"; "wasm" ] (Http.strings withFileBody "tags")
                      Assert.empty (Set.difference (uploadFiles ()) before |> Set.toList)
                      let attachmentId = Http.text (withFileBody["attachments"].AsArray()[0]) "id"
                      let stored = Directory.GetFiles(Path.Combine(spokes[0], ".ros", "work", "attachments", "WI-0002"))
                      Assert.equal [ "hub upload content" ] (stored |> Array.map File.ReadAllText |> Array.toList)
                      Assert.isTrue (attachmentId <> "") "attachment id"
                      let aggregated = server.Get "/api/work"
                      Assert.equal 200 (Http.status aggregated)
                      let rows = (Http.json aggregated).AsArray()
                      Assert.equal 2 (rows |> Seq.filter (fun row -> (Http.text row "id").StartsWith "WI-") |> Seq.length)
                      Assert.equal 200 (Http.status (server.Delete $"/api/repos/{repoId}"))
                      Assert.equal 0 ((Http.json (server.Get "/api/repos")).AsArray().Count)
                      Assert.equal 400 (Http.status (server.Delete $"/api/repos/{repoId}"))
                      Assert.equal 404 (Http.status (server.Get "/api/nope"))) }

          { Name = "hub serve: the page registers, creates with an attachment, lists and unregisters through form posts"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let registered = server.PostForm("/repos", [ "path", spokes[0]; "name", "<Alpha>" ])
                      Assert.equal 303 (Http.status registered)
                      Http.contains "notice=Registered" (Http.location registered)
                      let repoId = spokeId spokes[0]
                      let rejected = server.PostForm("/repos", [ "path", spokes[0]; "name", "again" ])
                      Http.contains "error=already%20registered" (Http.location rejected)

                      let created =
                          server.PostMultipart("/work", [ "repo", repoId; "title", "From the page"; "tags", "hub"; "priority", "low"; "name", "brief.md" ], [ "x.md", "brief" ])

                      Assert.equal 303 (Http.status created)
                      Http.contains "notice=Created%20WI-0001" (Http.location created)
                      let spokeItem = JsonNode.Parse((CliHarness.rosOk spokes[0] [ "work"; "show"; "WI-0001" ]).Out)
                      Assert.equal "brief.md" (Http.text (spokeItem["attachments"].AsArray()[0]) "name")
                      Assert.equal [ "hub" ] (Http.strings spokeItem "tags")
                      let page = Http.body (server.Get "/?status=captured")
                      Http.contains "From the page" page
                      Http.contains "&lt;Alpha&gt;" page
                      Assert.isTrue (not (page.Contains "<script")) "the hub page carries no script"
                      Http.contains "text/css" (string (server.Get "/styles.css").Content.Headers.ContentType)
                      let unregistered = server.PostForm($"/repos/{repoId}/unregister", [])
                      Http.contains "notice=Unregistered" (Http.location unregistered)
                      Assert.equal 0 ((hubOk hubRoot [ "repos" ]).AsArray().Count)) } ]

    // ------------------------------------------------------------------
    // Versioned multi-repository aggregation (PRX-CTL-007)
    // ------------------------------------------------------------------

    let private document (text: string) = JsonNode.Parse(text).AsObject()

    let private workState kind = $"""{{"contract":"praxis.work-state","version":1,"kind":"{kind}"}}"""

    /// Every file under `root` except Git's own, as `path:sha256` lines.
    let private snapshot (root: string) =
        let gitDirectory = Path.Combine(root, ".git") + string Path.DirectorySeparatorChar

        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> not (path.StartsWith(gitDirectory, StringComparison.Ordinal)))
        |> Seq.map (fun path ->
            let hash = Convert.ToHexString(Security.Cryptography.SHA256.HashData(File.ReadAllBytes path))
            $"{Path.GetRelativePath(root, path)}:{hash}")
        |> Seq.sort
        |> Seq.toList

    let private capture (root: string) (id: string) (title: string) =
        CliHarness.rosOk root [ "work"; "capture"; "--title"; title; "--id"; id; "--occurred-at"; CliHarness.now () ] |> ignore

    let private stateOf (root: string) (arguments: string list) = JsonNode.Parse((CliHarness.rosOk root ("work" :: "state" :: arguments)).Out)

    /// A spoke whose launcher behaves like a Praxis that predates `work state`.
    let private makeOlderPraxis (root: string) =
        File.WriteAllText(Path.Combine(root, "praxis"), "#!/bin/sh\necho 'Usage: praxis [--root PATH] version | work list'\nexit 2\n")

    let private availability (repository: JsonNode) = Http.text repository["availability"] "status"

    /// The node at `names` under `node` (null when absent).
    let private nodeAt (names: string list) (node: JsonNode) =
        names |> List.fold (fun (current: JsonNode) (name: string) -> if isNull current then null else current[name]) node

    let private repositoriesOf (node: JsonNode) = node["repositories"].AsArray() |> Seq.toList

    /// The work rows of an aggregated entry's own work-list document.
    let private itemsOf (entry: JsonNode) =
        let document = entry["document"]
        document["items"].AsArray() |> Seq.toList

    let private aggregationUnitTests =
        [ { Name = "hub state: a repository's answer is classified by contract, never altered"
            Run =
              fun () ->
                  let listed = HubStateJson.classify "work state" [ "work-list" ] 0 (workState "work-list") ""
                  Assert.equal SpokeAvailability.Available listed.Availability
                  Assert.equal (workState "work-list") (listed.Document.Value.ToJsonString())

                  let notFound =
                      HubStateJson.classify "work state" [ "work-item" ] 1 """{"contract":"praxis.error","version":1,"code":"work-item-not-found","error":"x"}""" ""

                  Assert.equal SpokeAvailability.Available notFound.Availability
                  Assert.isTrue notFound.Document.IsSome "the repository's own error document is relayed"

                  let unreadable =
                      HubStateJson.classify "work state" [ "work-list" ] 1 """{"contract":"praxis.error","version":1,"code":"work-state-unreadable","error":"queue.json is broken"}""" ""

                  Assert.equal (SpokeAvailability.Unreadable "queue.json is broken") unreadable.Availability

                  match (HubStateJson.classify "work state" [ "work-list" ] 0 """{"contract":"praxis.work-state","version":2,"kind":"work-list"}""" "").Availability with
                  | SpokeAvailability.Incompatible reason -> Http.contains "version 2; this hub reads version 1" reason
                  | other -> failwith $"expected incompatible, got {other}"

                  match (HubStateJson.classify "work state" [ "work-list" ] 2 "Usage: praxis version | work list" "Usage: praxis").Availability with
                  | SpokeAvailability.Incompatible reason -> Http.contains "does not provide `praxis work state`" reason
                  | other -> failwith $"expected incompatible, got {other}"

                  match (HubStateJson.classify "work state" [ "work-list" ] 0 (workState "work-item") "").Availability with
                  | SpokeAvailability.Incompatible _ -> ()
                  | other -> failwith $"a document of the wrong kind is incompatible, got {other}" }

          { Name = "hub state: a relayed error keeps the single-repository API's status"
            Run =
              fun () ->
                  let error code = Some(document $"""{{"contract":"praxis.error","version":1,"code":"{code}"}}""")
                  Assert.equal 200 (HubRegistry.relayedStatus (Some(document (workState "work-item"))))
                  Assert.equal 404 (HubRegistry.relayedStatus (error "work-item-not-found"))
                  Assert.equal 409 (HubRegistry.relayedStatus (error "illegal-transition"))
                  Assert.equal 422 (HubRegistry.relayedStatus (error "reason-required"))
                  Assert.equal 502 (HubRegistry.relayedStatus (error "something-new"))

                  let unreachable =
                      HubRegistry.repositoryAnswer
                          HubStateJson.repositoryWorkListKind
                          (JsonObject())
                          { Availability = SpokeAvailability.Unreachable "gone"
                            Document = None }

                  Assert.equal 502 unreachable.Status
                  Assert.equal "repository-unavailable" (Http.text unreachable.Document "code") }

          { Name = "hub state: the v1 routes and the spoke command lines they delegate to"
            Run =
              fun () ->
                  let request methodName path query body =
                      { Method = methodName
                        Segments = HttpMessages.pathSegments path
                        Query = query
                        ContentType = Some "application/json"
                        Body = Text.Encoding.UTF8.GetBytes(body: string) }

                  Assert.equal (HubWebRoute.State HubStateRoute.Repositories) (HubWeb.route (request "GET" "/api/v1/repos" [] ""))

                  Assert.equal
                      (HubWebRoute.State(HubStateRoute.RepositoryWork("alpha", [ "x" ], Some "ready")))
                      (HubWeb.route (request "GET" "/api/v1/repos/alpha/work" [ "tag", "x"; "status", "ready" ] ""))

                  Assert.equal
                      (HubWebRoute.State(HubStateRoute.RepositoryWorkItem("alpha", "WI-1")))
                      (HubWeb.route (request "GET" "/api/v1/repos/alpha/work/WI-1" [] ""))

                  Assert.equal (HubWebRoute.State(HubStateRoute.Work(Some "alpha", [], None))) (HubWeb.route (request "GET" "/api/v1/work" [ "repo", "alpha" ] ""))

                  match HubWeb.route (request "POST" "/api/v1/repos/alpha/work/WI-1/transitions" [] """{"action":"ready"}""") with
                  | HubWebRoute.State(HubStateRoute.Transition("alpha", "WI-1", Ok body)) -> Assert.equal "ready" (Http.text body "action")
                  | other -> failwith $"unexpected route {other}"

                  Assert.equal [ "work"; "state"; "WI-1" ] (HubRegistry.stateArguments (Some "WI-1") [] None)
                  Assert.equal [ "work"; "state"; "--tag"; "x"; "--status"; "ready" ] (HubRegistry.stateArguments None [ "x" ] (Some "ready"))

                  Assert.equal
                      [ "work"; "transition"; "--id"; "WI-1"; "--request"; """{"action":"ready"}""" ]
                      (HubRegistry.transitionArguments "WI-1" (document """{"action":"ready"}"""))

                  Assert.equal (Ok(StateQuery.List([ "a"; "b" ], Some "ready"))) (WorkStateCommands.stateQuery [ "--tag"; "a,b"; "--status"; "ready"; "--json" ])
                  Assert.equal (Ok(StateQuery.Item "WI-1")) (WorkStateCommands.stateQuery [ "WI-1" ])
                  Assert.isTrue (WorkStateCommands.stateQuery [ "WI-1"; "--status"; "ready" ] |> Result.isError) "an item read takes no list filter"
                  Assert.equal (Some RefusalCategory.IllegalTransition) (WebInterface.refusalCategoryOf "illegal-transition") } ]

    let private aggregationCliTests =
        [ { Name = "work state prints the same praxis.work-state documents web serve answers; work transition runs the API's transition path"
            Run =
              fun () ->
                  withRepositories 1 (fun _ spokes ->
                      let root = spokes[0]
                      capture root "WI-S1" "State contract item"

                      using (new ServedProcess(root, [ "web"; "serve" ])) (fun server ->
                          let served (path: string) =
                              let body = (Http.json (server.Get path)).AsObject()
                              body.Remove "source" |> ignore
                              body.ToJsonString()

                          Assert.equal (served "/api/v1/work") ((stateOf root []).ToJsonString())
                          Assert.equal (served "/api/v1/work/WI-S1") ((stateOf root [ "WI-S1" ]).ToJsonString()))

                      let missing = CliHarness.ros root [ "work"; "state"; "WI-NOPE" ]
                      Assert.equal 1 missing.Exit
                      Assert.equal "work-item-not-found" (Http.text (JsonNode.Parse missing.Out) "code")

                      let before = snapshot root
                      let refused = CliHarness.ros root [ "work"; "transition"; "--id"; "WI-S1"; "--request"; """{"action":"resume"}""" ]
                      Assert.equal 1 refused.Exit
                      Assert.equal "illegal-transition" (Http.text (JsonNode.Parse refused.Out) "code")
                      Assert.equal before (snapshot root)

                      let accepted = CliHarness.ros root [ "work"; "transition"; "--id"; "WI-S1"; "--request"; """{"action":"ready"}""" ]
                      Assert.equal 0 accepted.Exit
                      let transitioned = JsonNode.Parse accepted.Out
                      Assert.equal "work-transition" (Http.text transitioned "kind")
                      Assert.equal "ready" (transitioned |> nodeAt [ "item"; "backlog" ] |> fun backlog -> Http.text backlog "status")

                      let malformed = CliHarness.ros root [ "work"; "transition"; "--id"; "WI-S1"; "--request"; "{" ]
                      Assert.equal 2 malformed.Exit
                      Assert.equal "invalid-request" (Http.text (JsonNode.Parse malformed.Out) "code")
                      Assert.equal 2 (CliHarness.ros root [ "work"; "transition"; "--request"; "{}" ]).Exit) }

          { Name = "hub state prints a repository's own documents and reports an unregistered repository"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let id = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      capture spokes[0] "WI-C1" "Read through the hub CLI"
                      let item = hubOk hubRoot [ "state"; "--repo"; id; "--item"; "WI-C1" ]
                      Assert.equal "praxis.hub-state" (Http.text item "contract")
                      Assert.equal "repository-work-item" (Http.text item "kind")
                      Assert.equal ((stateOf (List.item 0 spokes) [ "WI-C1" ]).ToJsonString()) (item["document"].ToJsonString())
                      Assert.equal "work-list" (Http.text (hubOk hubRoot [ "state" ]) "kind")
                      let unknown = hub hubRoot [ "state"; "--repo"; "nope" ]
                      Assert.equal 1 unknown.Exit
                      Assert.equal "repository-not-registered" (Http.text (JsonNode.Parse unknown.Out) "code")) } ]

    let private aggregationServerTests =
        [ { Name = "hub serve v1: repositories with availability, and each repository's own work list and item, by delegation"
            Run =
              fun () ->
                  withRepositories 2 (fun hubRoot spokes ->
                      let idA = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      let idB = Http.text (hubOk hubRoot [ "register"; spokes[1] ]) "id"
                      capture spokes[0] "WI-A1" "Alpha item"
                      capture spokes[1] "WI-B1" "Beta item"
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])

                      let repositories = Http.json (server.Get "/api/v1/repos")
                      Assert.equal "repository-list" (Http.text repositories "kind")
                      let listed = repositories["repositories"].AsArray() |> Seq.toList
                      Assert.equal [ idA; idB ] (listed |> List.map (fun repository -> Http.text repository "id"))
                      Assert.isTrue (listed |> List.forall (fun repository -> availability repository = "available")) "both repositories answer"
                      Assert.equal idA (List.head listed |> nodeAt [ "repositorySource" ] |> fun source -> Http.text source "repository")

                      let work = server.Get $"/api/v1/repos/{idA}/work"
                      Assert.equal 200 (Http.status work)
                      let workBody = Http.json work
                      Assert.equal "repository-work-list" (Http.text workBody "kind")
                      Assert.equal ((stateOf (List.item 0 spokes) []).ToJsonString()) (workBody["document"].ToJsonString())

                      let item = Http.json (server.Get $"/api/v1/repos/{idB}/work/WI-B1")
                      Assert.equal ((stateOf (List.item 1 spokes) [ "WI-B1" ]).ToJsonString()) (item["document"].ToJsonString())

                      let missing = server.Get $"/api/v1/repos/{idB}/work/WI-NOPE"
                      Assert.equal 404 (Http.status missing)
                      Assert.equal "work-item-not-found" (Http.json missing |> nodeAt [ "document" ] |> fun found -> Http.text found "code")

                      let unregistered = server.Get "/api/v1/repos/nope/work"
                      Assert.equal 404 (Http.status unregistered)
                      Assert.equal "repository-not-registered" (Http.text (Http.json unregistered) "code")

                      let aggregated = Http.json (server.Get "/api/v1/work")
                      let entries = aggregated["repositories"].AsArray() |> Seq.toList
                      let titlesIn (entry: JsonNode) = itemsOf entry |> List.map (fun row -> Http.text row "title")
                      let titles = entries |> List.collect titlesIn
                      Assert.isTrue (List.contains "Alpha item" titles && List.contains "Beta item" titles) $"both repositories' own items are listed: {titles}") }

          { Name = "hub serve v1: the hub stores only its registry, and a repository's own change is served without the hub"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      CliHarness.optOutOfDurableCheckpoints spokes[0]
                      CliHarness.commitAll spokes[0] "pre-continuity completion semantics"
                      let id = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      let title = "Uniquely titled spoke item 7f3e"
                      capture spokes[0] "WI-D1" title
                      CliHarness.rosOk spokes[0] [ "work"; "backlog-transition"; "--id"; "WI-D1"; "--action"; "ready"; "--occurred-at"; CliHarness.now () ] |> ignore
                      CliHarness.commitAll hubRoot "registered"
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let hubBefore = snapshot hubRoot
                      let semanticState () = Http.json (server.Get $"/api/v1/repos/{id}/work/WI-D1") |> nodeAt [ "document"; "item" ] |> fun item -> Http.text item "semanticState"
                      let before = semanticState ()

                      // Changed in the repository itself; the hub is not involved.
                      CliHarness.rosOk spokes[0] [ "work"; "start"; "--id"; "WI-D1"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let after = semanticState ()
                      Assert.isTrue (before <> after) $"the repository's change is served: {before} -> {after}"
                      Assert.equal (stateOf (List.item 0 spokes) [ "WI-D1" ] |> nodeAt [ "item" ] |> fun item -> Http.text item "semanticState") after
                      server.Get "/api/v1/work" |> ignore
                      server.Get "/api/v1/repos" |> ignore

                      Assert.equal hubBefore (snapshot hubRoot)
                      Assert.equal [ "registry.json"; "registry.md" ] (Directory.GetFiles(Path.Combine(hubRoot, ".ros", "hub")) |> Array.map Path.GetFileName |> Array.sort |> Array.toList)

                      let copies =
                          Directory.EnumerateFiles(hubRoot, "*", SearchOption.AllDirectories)
                          |> Seq.filter (fun path -> not (path.Contains(string Path.DirectorySeparatorChar + ".git" + string Path.DirectorySeparatorChar)))
                          |> Seq.filter (fun path -> (File.ReadAllText path).Contains title)
                          |> Seq.toList

                      Assert.empty copies) }

          { Name = "hub serve v1: an unreachable or incompatible repository is reported for itself and the response still succeeds"
            Run =
              fun () ->
                  withRepositories 3 (fun hubRoot spokes ->
                      let ids = spokes |> List.map (fun spokeRoot -> Http.text (hubOk hubRoot [ "register"; spokeRoot ]) "id")
                      capture spokes[0] "WI-H1" "Healthy item"
                      makeOlderPraxis spokes[2]
                      let moved = spokes[1] + "-moved"
                      Directory.Move(spokes[1], moved)

                      try
                          use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                          let aggregated = server.Get "/api/v1/work"
                          Assert.equal 200 (Http.status aggregated)
                          let entries = Http.json aggregated |> repositoriesOf
                          let entry id = entries |> List.find (fun item -> Http.text item["repository"] "id" = id)
                          Assert.equal "available" (entry ids[0] |> nodeAt [ "repository" ] |> availability)
                          Assert.isTrue (itemsOf (entry ids[0]) |> List.exists (fun row -> Http.text row "title" = "Healthy item")) "the healthy repository's work is listed"
                          Assert.equal "unreachable" (entry ids[1] |> nodeAt [ "repository" ] |> availability)
                          Http.contains "no longer exists" (entry ids[1] |> nodeAt [ "repository"; "availability" ] |> fun found -> Http.text found "reason")
                          Assert.isTrue (entry ids[1] |> nodeAt [ "document" ] |> isNull) "an unreachable repository has no document"
                          Assert.equal "incompatible" (entry ids[2] |> nodeAt [ "repository" ] |> availability)
                          Http.contains "does not provide `praxis work state`" (entry ids[2] |> nodeAt [ "repository"; "availability" ] |> fun found -> Http.text found "reason")

                          let repositories = Http.json (server.Get "/api/v1/repos") |> repositoriesOf |> List.map availability
                          Assert.equal [ "available"; "unreachable"; "incompatible" ] repositories

                          let single = server.Get $"/api/v1/repos/{ids[2]}/work"
                          Assert.equal 502 (Http.status single)
                          Assert.equal "repository-unavailable" (Http.text (Http.json single) "code")
                      finally
                          CliHarness.removeDirectory moved) }

          { Name = "hub serve v1: a transition is run by the owning repository's own transition path"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot spokes ->
                      let id = Http.text (hubOk hubRoot [ "register"; spokes[0] ]) "id"
                      capture spokes[0] "WI-T1" "Transitioned through the hub"
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let path = $"/api/v1/repos/{id}/work/WI-T1/transitions"
                      let spokeBefore = snapshot spokes[0]
                      let registryBefore = CliHarness.read hubRoot ".ros/hub/registry.json"

                      let refused = server.PostJson(path, """{"action":"resume"}""")
                      Assert.equal 409 (Http.status refused)
                      let refusal = (Http.json refused)["document"]
                      Assert.equal "illegal-transition" (Http.text refusal "code")
                      Assert.equal "resume" (Http.text refusal "requestedAction")
                      Assert.equal spokeBefore (snapshot spokes[0])

                      let accepted = server.PostJson(path, """{"action":"ready"}""")
                      Assert.equal 200 (Http.status accepted)
                      let body = Http.json accepted
                      Assert.equal "repository-work-transition" (Http.text body "kind")
                      Assert.equal "work-transition" (Http.text body["document"] "kind")
                      Assert.equal "ready" (stateOf (List.item 0 spokes) [ "WI-T1" ] |> nodeAt [ "item"; "backlog" ] |> fun backlog -> Http.text backlog "status")
                      Assert.equal registryBefore (CliHarness.read hubRoot ".ros/hub/registry.json")

                      Assert.equal 400 (Http.status (server.PostJson(path, "[1]")))
                      Assert.equal 404 (Http.status (server.PostJson("/api/v1/repos/nope/work/WI-T1/transitions", """{"action":"ready"}""")))) } ]

    let tests =
        unitTests
        @ cliTests
        @ scaffoldTests
        @ serverTests
        @ aggregationUnitTests
        @ aggregationCliTests
        @ aggregationServerTests
