namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Domain.Work

/// Starts a real `praxis ... serve` process on a free loopback port and
/// drives it over HTTP; `Dispose` always stops the process. `environment`
/// entries are applied after the identity variables are removed.
type ServedProcess(root: string, command: string list, ?environment: (string * string) list) =
    let freePort () =
        let probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let chosen = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        chosen

    let launch (port: int) =
        let errors = StringBuilder()
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        [ CliHarness.cli; "--root"; root ] @ command @ [ "--port"; string port ] |> List.iter startInfo.ArgumentList.Add
        CliHarness.identityVariables |> List.iter (startInfo.Environment.Remove >> ignore)
        defaultArg environment [] |> List.iter (fun (name, value) -> startInfo.Environment[name] <- value)
        let started = new Process(StartInfo = startInfo)
        started.ErrorDataReceived.Add(fun line -> if not (isNull line.Data) then lock errors (fun () -> errors.AppendLine line.Data |> ignore))
        started.Start() |> ignore
        started.BeginOutputReadLine()
        started.BeginErrorReadLine()
        started, errors

    let clientFor (port: int) =
        let handler = new HttpClientHandler(AllowAutoRedirect = false)
        new HttpClient(handler, BaseAddress = Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes 2.0)

    /// Ready, or exited before listening (with its stderr). A port taken
    /// between probing and binding is the one early exit worth retrying.
    let waitUntilListening (child: Process) (errors: StringBuilder) (client: HttpClient) =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let rec poll () =
            if child.HasExited then
                child.WaitForExit()
                Error(child.ExitCode, lock errors (fun () -> errors.ToString()))
            elif DateTime.UtcNow > deadline then
                child.Kill true
                failwith "server did not start listening within 60 seconds"
            else
                try
                    use _ = client.GetAsync("/styles.css").Result
                    Ok()
                with _ ->
                    Threading.Thread.Sleep 100
                    poll ()

        poll ()

    let child, client =
        let rec attempt remaining =
            let port = freePort ()
            let started, errors = launch port
            let candidate = clientFor port

            match waitUntilListening started errors candidate with
            | Ok() -> started, candidate
            | Error(_, stderr) when remaining > 1 && stderr.Contains "cannot listen" ->
                candidate.Dispose()
                attempt (remaining - 1)
            | Error(status, stderr) -> failwith $"server exited early with status {status}: {stderr}"

        attempt 5

    member _.Client = client

    member _.Get(path: string) = client.GetAsync(path).Result

    member _.PostJson(path: string, json: string) =
        use content = new StringContent(json, Encoding.UTF8, "application/json")
        client.PostAsync(path, content).Result

    /// A JSON post carrying extra request headers.
    member _.PostJsonWithHeaders(path: string, json: string, headers: (string * string) list) =
        use message = new HttpRequestMessage(HttpMethod.Post, path)
        message.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        headers |> List.iter (fun (name, value) -> message.Headers.TryAddWithoutValidation(name, value) |> ignore)
        client.SendAsync(message).Result

    member _.PostForm(path: string, fields: (string * string) list) =
        use content = new FormUrlEncodedContent(fields |> List.map (fun (key, value) -> Collections.Generic.KeyValuePair(key, value)))
        client.PostAsync(path, content).Result

    /// A browser-style multipart post: text fields, then `file` parts as
    /// `(filename, content)`.
    member _.PostMultipart(path: string, fields: (string * string) list, files: (string * string) list) =
        use content = new MultipartFormDataContent()

        for name, value in fields do
            content.Add(new StringContent(value), name)

        for fileName, text in files do
            content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes text), "file", fileName)

        client.PostAsync(path, content).Result

    member _.Delete(path: string) = client.DeleteAsync(path).Result

    interface IDisposable with
        member _.Dispose() =
            try
                if not child.HasExited then
                    child.Kill true
                    child.WaitForExit 10000 |> ignore
            finally
                client.Dispose()
                child.Dispose()

[<RequireQualifiedAccess>]
module Http =
    let body (response: HttpResponseMessage) = response.Content.ReadAsStringAsync().Result

    let json (response: HttpResponseMessage) = JsonNode.Parse(body response)

    let status (response: HttpResponseMessage) = int response.StatusCode

    let location (response: HttpResponseMessage) =
        response.Headers.Location |> Option.ofObj |> Option.map string |> Option.defaultValue ""

    let text (node: JsonNode) (name: string) = node[name].GetValue<string>()

    let number (node: JsonNode) (name: string) = node[name].GetValue<int>()

    let strings (node: JsonNode) (name: string) =
        node[name].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> Seq.toList

    let contains (fragment: string) (text: string) =
        Assert.isTrue (text.Contains fragment) $"expected to find '{fragment}' in:\n{text}"

[<RequireQualifiedAccess>]
module WebInterfaceTests =
    let private request methodName (path: string) (query: (string * string) list) contentType (body: byte array) =
        { Method = methodName
          Segments = HttpMessages.pathSegments path
          Query = query
          ContentType = contentType
          Body = body }

    let private jsonRequest methodName path (json: string) =
        request methodName path [] (Some "application/json") (Encoding.UTF8.GetBytes json)

    let private multipartBody (boundary: string) (parts: string list) =
        let body = parts |> List.map (fun part -> $"--{boundary}\r\n{part}\r\n") |> String.concat ""
        Encoding.UTF8.GetBytes(body + $"--{boundary}--\r\n")

    let private row id status backlogActions live : WorkListRow =
        { Id = id
          Title = id
          Description = None
          Tags = []
          Priority = None
          Status = status
          BlockedReason = None
          BacklogActions = backlogActions
          Attachments = []
          LiveWorkItem = live }

    /// A served web interface over a fresh, committed greenfield repository.
    let private withServer (test: string -> ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-web" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ])
            test root server
        finally
            CliHarness.removeDirectory root

    let private unitTests =
        [ { Name = "web: html escaping covers element and attribute metacharacters"
            Run =
              fun () ->
                  Assert.equal "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &#39;q&#39;" (Html.escape "<script>alert(\"x\")</script> & 'q'") }

          { Name = "web: urlencoded bodies decode plus, percent escapes and repeated keys in order"
            Run =
              fun () ->
                  Assert.equal
                      [ "tag", "a b"; "tag", "c&d"; "empty", ""; "flag", "" ]
                      (HttpMessages.parseUrlEncoded "?tag=a+b&tag=c%26d&empty=&flag") }

          { Name = "web: multipart parsing reads fields, quoted and extended filenames, and binary content"
            Run =
              fun () ->
                  let body =
                      multipartBody
                          "XyZ"
                          [ "Content-Disposition: form-data; name=\"title\"\r\n\r\nShip \"it\""
                            "Content-Disposition: form-data; name=\"file\"; filename=\"notes.md\"\r\nContent-Type: text/markdown\r\n\r\nline1\r\nline2"
                            "Content-Disposition: form-data; name=file; filename=plain.txt; filename*=utf-8''r%C3%A9sum%C3%A9.txt\r\n\r\n--not-a-boundary" ]

                  match HttpMessages.parseMultipart "XyZ" body with
                  | Error message -> failwith message
                  | Ok parts ->
                      Assert.equal 3 parts.Length
                      Assert.equal ("title", None) (parts[0].Name, parts[0].FileName)
                      Assert.equal "Ship \"it\"" (Encoding.UTF8.GetString parts[0].Data)
                      Assert.equal (Some "notes.md") parts[1].FileName
                      Assert.equal (Some "text/markdown") parts[1].ContentType
                      Assert.equal "line1\r\nline2" (Encoding.UTF8.GetString parts[1].Data)
                      Assert.equal (Some "résumé.txt") parts[2].FileName
                      Assert.equal "--not-a-boundary" (Encoding.UTF8.GetString parts[2].Data) }

          { Name = "web: a malformed multipart body is rejected rather than half-read"
            Run =
              fun () ->
                  match HttpMessages.parseMultipart "B" (Encoding.UTF8.GetBytes "--B\r\nContent-Disposition: form-data; name=\"x\"\r\n\r\nunterminated") with
                  | Ok _ -> failwith "expected a rejection"
                  | Error message -> Http.contains "closing boundary" message }

          { Name = "web: uploads pair each file with its positional name override and drop empty pickers"
            Run =
              fun () ->
                  let part name fileName (data: string) =
                      { Name = name
                        FileName = fileName
                        ContentType = None
                        Data = Encoding.UTF8.GetBytes data }

                  let uploads =
                      HttpMessages.uploadsFrom
                          [ part "file" (Some "a.txt") "A"
                            part "name" None "  "
                            part "file" (Some "dir/b.txt") "B"
                            part "name" None "renamed.txt"
                            part "file" (Some "") ""
                            part "name" None "ignored" ]

                  Assert.equal [ "a.txt", "A"; "renamed.txt", "B" ] (uploads |> List.map (fun upload -> upload.Name, Encoding.UTF8.GetString upload.Data)) }

          { Name = "web: routes map API, pages and form posts to typed operations"
            Run =
              fun () ->
                  Assert.equal
                      (WebRoute.Api(WorkOperation.List([ "a"; "b"; "c" ], Some "ready")))
                      (WebInterface.route (request "GET" "/api/work" [ "tag", "a,b"; "tag", "c"; "status", "ready" ] None [||]))

                  Assert.equal
                      (WebRoute.Api(WorkOperation.List([], Some "ready")))
                      (WebInterface.route (request "GET" "/api/work/ready" [] None [||]))

                  Assert.equal (WebRoute.Api(WorkOperation.Show "WI 1")) (WebInterface.route (request "GET" "/api/work/WI%201" [] None [||]))
                  Assert.equal (WebRoute.ApiDownload("WI-1", "ATT-2")) (WebInterface.route (request "GET" "/api/work/WI-1/attachments/ATT-2" [] None [||]))
                  Assert.equal (WebRoute.ApiError(404, "no route for GET /api/nope")) (WebInterface.route (request "GET" "/api/nope" [] None [||]))
                  Assert.equal (WebRoute.Home []) (WebInterface.route (request "GET" "/" [] None [||]))
                  Assert.equal WebRoute.MethodNotAllowed (WebInterface.route (request "PUT" "/" [] None [||]))

                  match WebInterface.route (jsonRequest "POST" "/api/work/WI-1/complete" """{"evidence":[{"type":"tests","path":"t.fs"},{"type":"","path":"x"}],"conclusion":" done "}""") with
                  | WebRoute.Api(WorkOperation.Complete("WI-1", evidence, conclusion, None)) ->
                      Assert.equal [ { EvidenceInput.Type = "tests"; Path = "t.fs" } ] evidence
                      Assert.equal (Some "done") conclusion
                  | other -> failwith $"unexpected route {other}"

                  let form = "evidence-type=implementation&evidence-path=src%2Fa.fs&evidence-type=&evidence-path="

                  match WebInterface.route (request "POST" "/work/WI-1/complete" [] (Some "application/x-www-form-urlencoded") (Encoding.UTF8.GetBytes form)) with
                  | WebRoute.FormPost(WorkOperation.Complete("WI-1", evidence, None, None), "/work/WI-1") ->
                      Assert.equal [ { EvidenceInput.Type = "implementation"; Path = "src/a.fs" } ] evidence
                  | other -> failwith $"unexpected route {other}" }

          { Name = "web: every operation is exactly one CLI command line"
            Run =
              fun () ->
                  let now = "2026-01-01T00:00:00.000Z"

                  Assert.equal
                      [ "work"; "capture"; "--title"; "T"; "--occurred-at"; now; "--priority"; "high"; "--tag"; "a"; "--actor"; "me" ]
                      (WebInterface.commandLine
                          now
                          []
                          (WorkOperation.Capture(
                              { Title = "T"
                                Tags = [ "a" ]
                                Priority = Some "high"
                                Description = None
                                Id = None
                                Actor = Some "me"
                                Source = None
                                SourceReference = None },
                              []
                          )))

                  Assert.equal
                      [ "work"; "backlog-transition"; "--id"; "W"; "--action"; "abandon"; "--occurred-at"; now; "--reason"; "r" ]
                      (WebInterface.commandLine now [] (WorkOperation.Abandon("W", Some "r")))

                  Assert.equal [ "work"; "block"; "--id"; "W"; "--occurred-at"; now ] (WebInterface.commandLine now [] (WorkOperation.Block("W", None)))

                  // An emptied tag field clears the tags: a bare trailing --tag.
                  Assert.equal
                      [ "work"; "update"; "--id"; "W"; "--occurred-at"; now; "--description"; ""; "--tag" ]
                      (WebInterface.commandLine
                          now
                          []
                          (WorkOperation.Update(
                              "W",
                              { Title = None
                                Description = Some ""
                                Tags = Some []
                                Priority = None }
                          )))

                  Assert.equal
                      [ "work"; "attach"; "--id"; "W"; "--occurred-at"; now; "--file"; "/tmp/u0=a b.txt" ]
                      (WebInterface.commandLine now [ "/tmp/u0", "a b.txt" ] (WorkOperation.Attach("W", [])))

                  Assert.equal
                      [ "work"; "complete"; "--id"; "W"; "--occurred-at"; now; "--evidence"; "tests=t.fs"; "--conclusion"; "ok" ]
                      (WebInterface.commandLine now [] (WorkOperation.Complete("W", [ { EvidenceInput.Type = "tests"; Path = "t.fs" } ], Some "ok", None)))

                  Assert.equal [ "validate"; "--json" ] (WebInterface.commandLine now [] WorkOperation.Validate) }

          { Name = "web: row actions project the kernel's live or backlog actions"
            Run =
              fun () ->
                  Assert.equal
                      [ RowAction.Abandon; RowAction.Ready ]
                      (WebInterface.availableActions (row "A" "captured" [ "abandon"; "ready"; "unknown" ] None))

                  Assert.equal
                      [ RowAction.Block; RowAction.Complete ]
                      (WebInterface.availableActions (
                          row "B" "active" [ "ready" ] (Some { State = "active"; SemanticState = "active"; AllowedActions = [ "block"; "complete" ] })
                      )) }

          { Name = "web: pages escape every repository-provided value"
            Run =
              fun () ->
                  let hostile = "<img src=x onerror=alert(1)>"

                  let item =
                      { row "WI-<1>" "captured" [ "ready" ] None with
                          Title = hostile
                          Tags = [ "\"tag\"" ]
                          Description = Some hostile }

                  let home = WebInterface.renderHome None (Ok [ item ]) [ "error", hostile; "tag", "\"><x" ]
                  let detail = WebInterface.renderDetail None item (Some hostile) "{}" []

                  for page in [ home; detail ] do
                      Assert.isTrue (not (page.Contains "<img")) "raw markup leaked into the page"
                      Http.contains "&lt;img src=x onerror=alert(1)&gt;" page

                  Http.contains "/work/WI-%3C1%3E" home
                  Assert.isTrue (not (home.Contains "<script")) "the page must not carry any script" }

          { Name = "web: re-running the CLI uses the dotnet host plus the entry assembly only when hosted"
            Run =
              fun () ->
                  Assert.equal ("/usr/bin/dotnet", [ "/app/praxis.dll" ]) (CliProcess.selfCommand "/usr/bin/dotnet" "/app/praxis.dll")
                  Assert.equal ("C:\\dotnet\\dotnet.exe", [ "praxis.dll" ]) (CliProcess.selfCommand "C:\\dotnet\\dotnet.exe" "praxis.dll")
                  Assert.equal ("/opt/praxis/praxis", []) (CliProcess.selfCommand "/opt/praxis/praxis" "") }

          { Name = "web: serve options default to loopback and validate the port"
            Run =
              fun () ->
                  Assert.equal (Ok("127.0.0.1", 4310)) (WebInterface.parseServeOptions 4310 [])
                  Assert.equal (Ok("0.0.0.0", 9000)) (WebInterface.parseServeOptions 4310 [ "--host"; "0.0.0.0"; "--port"; "9000" ])
                  Assert.isTrue (Result.isError (WebInterface.parseServeOptions 4310 [ "--port"; "nope" ])) "a non-numeric port must be rejected"
                  Assert.isTrue (Result.isError (WebInterface.parseServeOptions 4310 [ "--bogus" ])) "an unknown option must be rejected" } ]

    let private apiTests =
        [ { Name = "web serve: GET /api/work lists the unified queue, including items with no backlog entry"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      let response = server.Get "/api/work"
                      Assert.equal 200 (Http.status response)
                      let rows = (Http.json response).AsArray()
                      Assert.isTrue (rows |> Seq.exists (fun item -> (Http.text item "id").StartsWith "ROS-INSTALL")) "install item missing") }

          { Name = "web serve: POST /api/work captures a backlog item with an auto-generated ID"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      let response = server.PostJson("/api/work", """{"title":"Investigate payload growth","tags":["wasm","state"],"priority":"high"}""")
                      Assert.equal 200 (Http.status response)
                      let item = Http.json response
                      Assert.equal "WI-0001" (Http.text item "id")
                      Assert.equal [ "wasm"; "state" ] (Http.strings item "tags")
                      Assert.equal "captured" (Http.text item "status")) }

          { Name = "web serve: captured -> ready -> start requires the ready gate, same as the CLI"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Too soon"}""") |> ignore
                      let tooSoon = server.PostJson("/api/work/WI-0001/start", """{"type":"feature"}""")
                      Assert.equal 400 (Http.status tooSoon)
                      Http.contains "mark it ready first" (Http.text (Http.json tooSoon) "error")
                      Assert.equal 200 (Http.status (server.PostJson("/api/work/WI-0001/ready", "")))
                      let started = server.PostJson("/api/work/WI-0001/start", """{"type":"feature"}""")
                      Assert.equal 200 (Http.status started)
                      let body = Http.json started
                      Assert.equal "active" (Http.text body "status")
                      Assert.equal "active" (Http.text body["liveWorkItem"] "semanticState")) }

          { Name = "web serve: the full lifecycle over HTTP applies the CLI's evidence rules"
            Run =
              fun () ->
                  withServer (fun root server ->
                      server.PostJson("/api/work", """{"title":"Ship it","id":"WI-SHIP"}""") |> ignore
                      server.PostJson("/api/work/WI-SHIP/ready", "") |> ignore
                      server.PostJson("/api/work/WI-SHIP/start", """{"type":"feature"}""") |> ignore
                      let missing = server.PostJson("/api/work/WI-SHIP/complete", """{"evidence":[]}""")
                      Assert.equal 400 (Http.status missing)
                      Http.contains "completion evidence missing" (Http.text (Http.json missing) "error")
                      CliHarness.write root "src/Ship.fs" "module Ship\n"
                      CliHarness.write root "tests/ShipTests.fs" "// passed by fixture\n"

                      let completed =
                          server.PostJson(
                              "/api/work/WI-SHIP/complete",
                              """{"evidence":[{"type":"implementation","path":"src/Ship.fs"},{"type":"tests","path":"tests/ShipTests.fs"}]}"""
                          )

                      Assert.equal 200 (Http.status completed)
                      Assert.equal "complete" (Http.text (Http.json completed) "status")) }

          { Name = "web serve: block dispatches per ID to the backlog or the in-flight item, same as the CLI"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Backlog item","id":"WI-BACKLOG"}""") |> ignore
                      server.PostJson("/api/work/WI-BACKLOG/ready", "") |> ignore
                      server.PostJson("/api/work", """{"title":"In-flight item","id":"WI-INFLIGHT"}""") |> ignore
                      server.PostJson("/api/work/WI-INFLIGHT/ready", "") |> ignore
                      server.PostJson("/api/work/WI-INFLIGHT/start", """{"type":"feature"}""") |> ignore
                      let backlog = server.PostJson("/api/work/WI-BACKLOG/block", """{"reason":"waiting on benchmark"}""")
                      Assert.equal 200 (Http.status backlog)
                      Assert.equal "blocked" (Http.text (Http.json backlog) "status")
                      let inFlight = server.PostJson("/api/work/WI-INFLIGHT/block", """{"reason":"waiting on benchmark"}""")
                      Assert.equal 200 (Http.status inFlight)
                      Assert.equal "blocked" (Http.text (Http.json inFlight).["liveWorkItem"] "semanticState")
                      let resumed = server.PostJson("/api/work/WI-INFLIGHT/resume", "")
                      Assert.equal 200 (Http.status resumed)
                      Assert.equal "active" (Http.text (Http.json resumed).["liveWorkItem"] "semanticState")) }

          { Name = "web serve: abandonment is terminal over HTTP the same as over the CLI"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Dead idea","id":"WI-DEAD"}""") |> ignore
                      server.PostJson("/api/work/WI-DEAD/ready", "") |> ignore
                      Assert.equal 200 (Http.status (server.PostJson("/api/work/WI-DEAD/abandon", """{"reason":"no longer relevant"}""")))
                      let after = server.PostJson("/api/work/WI-DEAD/start", """{"type":"feature"}""")
                      Assert.equal 400 (Http.status after)
                      Http.contains "abandoned" (Http.text (Http.json after) "error")) }

          { Name = "web serve: an unknown item is a clean 404 and title text is stored, never executed"
            Run =
              fun () ->
                  withServer (fun root server ->
                      let missing = server.Get "/api/work/NOPE-0001"
                      Assert.equal 404 (Http.status missing)
                      Http.contains "not found" (Http.text (Http.json missing) "error")
                      let marker = Path.Combine(root, "pwned")
                      let dangerous = $"; touch {marker} #`touch {marker}` $(touch {marker})"
                      let payload = JsonObject()
                      payload["title"] <- JsonValue.Create dangerous
                      payload["id"] <- JsonValue.Create "WI-SAFE"
                      server.PostJson("/api/work", payload.ToJsonString()) |> ignore
                      Assert.equal dangerous (Http.text (Http.json (server.Get "/api/work/WI-SAFE")) "title")
                      Assert.isTrue (not (File.Exists marker)) "title text was executed") }

          { Name = "web serve: GET /api/validate and /api/status reflect repository state"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      let response = server.Get "/api/validate"
                      Assert.equal 200 (Http.status response)
                      let body = Http.json response
                      Assert.equal true (body["valid"].GetValue<bool>())
                      Assert.equal 0 (body["findings"].AsArray().Count)
                      let status = server.Get "/api/status"
                      Assert.equal 200 (Http.status status)
                      Assert.equal "passed" (Http.text (Http.json status) "validation")) }

          { Name = "web serve: unknown routes 404 and the page is server-rendered HTML with no script"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      Assert.equal 404 (Http.status (server.Get "/api/not-a-route"))
                      Assert.equal 404 (Http.status (server.Get "/nothing-here"))
                      let page = server.Get "/"
                      Assert.equal 200 (Http.status page)
                      Http.contains "text/html" (string page.Content.Headers.ContentType)
                      let html = Http.body page
                      Http.contains "Work Backlog" html
                      Http.contains "ROS-INSTALL" html
                      Assert.isTrue (not (html.Contains "<script")) "the page must not load JavaScript"
                      let css = server.Get "/styles.css"
                      Assert.equal 200 (Http.status css)
                      Http.contains "text/css" (string css.Content.Headers.ContentType)) }

          { Name = "web serve: capture accepts a description, and update changes title, description, tags and priority"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Investigate payload growth","description":"Grows superlinearly."}""") |> ignore
                      Assert.equal "Grows superlinearly." (Http.text (Http.json (server.Get "/api/work/WI-0001")) "description")

                      let updated =
                          server.PostJson(
                              "/api/work/WI-0001/update",
                              """{"title":"Investigate payload growth (root cause)","description":"Narrowed to serialization layer.","tags":["wasm","perf"],"priority":"high"}"""
                          )

                      Assert.equal 200 (Http.status updated)
                      let body = Http.json updated
                      Assert.equal "Investigate payload growth (root cause)" (Http.text body "title")
                      Assert.equal "Narrowed to serialization layer." (Http.text body "description")
                      Assert.equal [ "wasm"; "perf" ] (Http.strings body "tags")
                      Assert.equal "high" (Http.text body "priority")) }

          { Name = "web serve: multipart attachments upload several files with custom names and download byte for byte"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Ship it","id":"WI-SHIP"}""") |> ignore
                      let uploaded = server.PostMultipart("/api/work/WI-SHIP/attachments", [], [ "notes.md", "first content"; "renamed.txt", "second content" ])
                      let text = Http.body uploaded
                      Assert.equal 200 (Http.status uploaded)
                      let attachments = (JsonNode.Parse text).["attachments"].AsArray()
                      Assert.equal 2 attachments.Count
                      Assert.equal "notes.md" (Http.text attachments[0] "name")
                      Assert.equal "renamed.txt" (Http.text attachments[1] "name")
                      Assert.equal ("first content".Length) (Http.number attachments[0] "size")
                      Assert.isTrue (not (attachments[0].AsObject().ContainsKey "file")) "the internal storage name must not leak"
                      let download = server.Get $"""/api/work/WI-SHIP/attachments/{Http.text attachments[1] "id"}"""
                      Assert.equal 200 (Http.status download)
                      Http.contains "renamed.txt" (string download.Content.Headers.ContentDisposition)
                      Assert.equal "second content" (Http.body download)) }

          { Name = "web serve: attaching the same display name twice keeps both files distinct"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Dup names","id":"WI-DUP"}""") |> ignore

                      for content in [ "version one"; "version two" ] do
                          Assert.equal 200 (Http.status (server.PostMultipart("/api/work/WI-DUP/attachments", [], [ "same-name.txt", content ])))

                      let attachments = (Http.json (server.Get "/api/work/WI-DUP")).["attachments"].AsArray() |> Seq.toList
                      Assert.equal [ "same-name.txt"; "same-name.txt" ] (attachments |> List.map (fun item -> Http.text item "name"))

                      let contents =
                          attachments
                          |> List.map (fun item -> Http.body (server.Get $"""/api/work/WI-DUP/attachments/{Http.text item "id"}"""))
                          |> List.sort

                      Assert.equal [ "version one"; "version two" ] contents) }

          { Name = "web serve: downloading an unknown attachment 404s"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"No attachments","id":"WI-EMPTY"}""") |> ignore
                      Assert.equal 404 (Http.status (server.Get "/api/work/WI-EMPTY/attachments/ATT-1"))
                      Assert.equal 400 (Http.status (server.PostMultipart("/api/work/WI-EMPTY/attachments", [ "title", "x" ], [])))) } ]

    let private formTests =
        [ { Name = "web serve: the capture form posts, attaches files, and redirects to the new item"
            Run =
              fun () ->
                  withServer (fun root server ->
                      let uploadDirectories () =
                          Directory.GetDirectories(Path.GetTempPath(), "ros-web-upload-*") |> Set.ofArray

                      let before = uploadDirectories ()

                      let created =
                          server.PostMultipart(
                              "/work",
                              [ "title", "<b>Bold</b> plan"; "tags", "ui, forms"; "priority", "low"; "description", "Needs a form."; "name", "spec.md" ],
                              [ "original.md", "# Spec" ]
                          )

                      Assert.equal 303 (Http.status created)
                      Http.contains "/work/WI-0001?notice=" (Http.location created)
                      let item = Http.json (server.Get "/api/work/WI-0001")
                      Assert.equal "<b>Bold</b> plan" (Http.text item "title")
                      Assert.equal [ "ui"; "forms" ] (Http.strings item "tags")
                      Assert.equal "low" (Http.text item "priority")
                      Assert.equal "spec.md" (Http.text (item["attachments"].AsArray()[0]) "name")
                      let page = Http.body (server.Get(Http.location created))
                      Http.contains "&lt;b&gt;Bold&lt;/b&gt; plan" page
                      Http.contains "Captured WI-0001." page
                      Http.contains "/api/work/WI-0001/attachments/ATT-1" page
                      Assert.isTrue (not (page.Contains "<b>Bold")) "the title must be escaped"
                      Assert.empty (Set.difference (uploadDirectories ()) before |> Set.toList)
                      Assert.isTrue (File.Exists(Path.Combine(root, ".ros", "work", "queue.json"))) "queue written") }

          { Name = "web serve: form actions redirect with the kernel's own error and apply legal transitions"
            Run =
              fun () ->
                  withServer (fun root server ->
                      server.PostJson("/api/work", """{"title":"Form driven","id":"WI-FORM","tags":["keep"]}""") |> ignore
                      let blocked = server.PostForm("/work/WI-FORM/block", [ "reason", "" ])
                      Assert.equal 303 (Http.status blocked)
                      Http.contains "/work/WI-FORM?error=" (Http.location blocked)
                      Http.contains "reason" (Http.body (server.Get(Http.location blocked)))
                      Assert.equal 303 (Http.status (server.PostForm("/work/WI-FORM/ready", [])))

                      let updated =
                          server.PostForm("/work/WI-FORM/update", [ "title", "Form driven"; "description", ""; "tags", ""; "priority", "high" ])

                      Http.contains "notice=" (Http.location updated)
                      let item = Http.json (server.Get "/api/work/WI-FORM")
                      Assert.equal ([]: string list) (Http.strings item "tags")
                      Assert.equal "high" (Http.text item "priority")
                      Assert.equal 303 (Http.status (server.PostForm("/work/WI-FORM/start", [ "type", "feature" ])))
                      CliHarness.write root "src/Form.fs" "module Form\n"
                      CliHarness.write root "tests/FormTests.fs" "// ok\n"

                      let completed =
                          server.PostForm(
                              "/work/WI-FORM/complete",
                              [ "evidence-type", "implementation"
                                "evidence-path", "src/Form.fs"
                                "evidence-type", "tests"
                                "evidence-path", "tests/FormTests.fs"
                                "evidence-type", ""
                                "evidence-path", ""
                                "conclusion", "" ]
                          )

                      Http.contains "notice=Completed" (Http.location completed)
                      Assert.equal "complete" (Http.text (Http.json (server.Get "/api/work/WI-FORM")) "status")
                      let validation = Http.body (server.Get "/validate")
                      Http.contains "Validation" validation) }

          { Name = "web serve: the queue page filters by tag and status and offers only allowed actions"
            Run =
              fun () ->
                  withServer (fun _ server ->
                      server.PostJson("/api/work", """{"title":"Tagged","id":"WI-TAG","tags":["wasm"]}""") |> ignore
                      server.PostJson("/api/work", """{"title":"Other","id":"WI-OTHER"}""") |> ignore
                      let filtered = Http.body (server.Get "/?tag=wasm&status=captured")
                      Http.contains "WI-TAG" filtered
                      Assert.isTrue (not (filtered.Contains "WI-OTHER")) "the tag filter must exclude untagged items"
                      Http.contains "action=\"/work/WI-TAG/ready\"" filtered
                      Assert.isTrue (not (filtered.Contains "/work/WI-TAG/resume")) "resume is not allowed for a captured item"
                      let ready = Http.json (server.Get "/api/work/ready")
                      Assert.equal 0 (ready.AsArray().Count)
                      let missing = server.Get "/work/NOPE"
                      Assert.equal 404 (Http.status missing)
                      Http.contains "not found" (Http.body missing)) } ]

    let tests = unitTests @ apiTests @ formTests
