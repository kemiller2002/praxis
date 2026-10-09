namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Cli

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
                        Body = [||]
                        Headers = [] }

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
                      Assert.equal "/" (Http.location registered)
                      Http.contains "notice:Registered" (Http.flash registered)
                      let repoId = spokeId spokes[0]
                      let rejected = server.PostForm("/repos", [ "path", spokes[0]; "name", "again" ])
                      Http.contains "already registered" (Http.flash rejected)

                      let created =
                          server.PostMultipart("/work", [ "repo", repoId; "title", "From the page"; "tags", "hub"; "priority", "low"; "name", "brief.md" ], [ "x.md", "brief" ])

                      Assert.equal 303 (Http.status created)
                      Http.contains "notice:Created WI-0001" (Http.flash created)
                      let spokeItem = JsonNode.Parse((CliHarness.rosOk spokes[0] [ "work"; "show"; "WI-0001" ]).Out)
                      Assert.equal "brief.md" (Http.text (spokeItem["attachments"].AsArray()[0]) "name")
                      Assert.equal [ "hub" ] (Http.strings spokeItem "tags")
                      let page = Http.body (server.Get "/?status=captured")
                      Http.contains "From the page" page
                      Http.contains "&lt;Alpha&gt;" page
                      Assert.isTrue (not (page.Contains "<script")) "the hub page carries no script"
                      Http.contains "text/css" (string (server.Get "/styles.css").Content.Headers.ContentType)
                      let unregistered = server.PostForm($"/repos/{repoId}/unregister", [])
                      Http.contains "notice:Unregistered" (Http.flash unregistered)
                      Assert.equal 0 ((hubOk hubRoot [ "repos" ]).AsArray().Count)) }

          { Name = "hub serve: filters have one canonical URL, an unknown page is a typed not-found, and the page links to itself (SAF-URL)"
            Run =
              fun () ->
                  withRepositories 1 (fun hubRoot _ ->
                      use server = new ServedProcess(hubRoot, [ "hub"; "serve" ])
                      let empty = server.Get "/?repo=&tag=&status="
                      Assert.equal 303 (Http.status empty)
                      Assert.equal "/" (Http.location empty)
                      Assert.equal "/?tag=a,b" (Http.location (server.Get "/?tag=b&tag=a"))
                      let missing = server.Get "/somewhere"
                      Assert.equal 404 (Http.status missing)
                      Http.contains "Back to the start" (Http.body missing)
                      Http.contains "Link to this view" (Http.body (server.Get "/?status=ready"))) } ]

    let tests = unitTests @ cliTests @ scaffoldTests @ serverTests
