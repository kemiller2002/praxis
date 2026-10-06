namespace Ros.Tests

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Cli
open Ros.Contracts.Work
open Ros.Domain.Work

/// The control-plane hosts (`praxis web serve`, `praxis hub serve`) hold no
/// canonical state, attribute every response to the durable state it was
/// derived from, reconstruct it on restart, and write repository files only
/// through the CLI (PRX-CTL-004, PRX-CTL-008, PRX-UI-009).
[<RequireQualifiedAccess>]
module ControlPlaneHostTests =
    // ------------------------------------------------------------------
    // The host source check
    // ------------------------------------------------------------------

    /// The files that make up the two HTTP hosts. `TempUploads.fs` is not one
    /// of them: it is the single, separately reviewed place uploaded bytes
    /// are written, under the system temporary directory.
    let hostFiles = [ "WebHttp.fs"; "WebInterface.fs"; "HubWeb.fs"; "ControlPlaneSource.fs" ]

    /// Direct file-system writes.
    let private writeApis =
        Regex(
            @"\b(File\.(Write|Append|Create|Copy|Move|Delete|Open|Replace|Set)\w*|Directory\.(CreateDirectory|Delete|Move)|FileStream|StreamWriter|FileInfo|DirectoryInfo)\b",
            RegexOptions.CultureInvariant
        )

    /// State retained between requests: module-level mutables and caches.
    let private retainedState =
        Regex(@"^    let\s+mutable\b|\b(ConcurrentDictionary|Dictionary|MemoryCache|ConditionalWeakTable|Lazy)\b|\blazy\s*\(", RegexOptions.CultureInvariant ||| RegexOptions.Multiline)

    /// Calls into persistence: functions (lower-case; type names such as
    /// `FileTelemetryUsageRepository.Scope` are not calls) of anything named
    /// like a repository, store, transaction or read module, the hub's
    /// registry effects, or the process runners.
    let private persistenceCall =
        Regex(@"\b(File\w+Repository|\w+Store|\w+Transaction|\w+Reads|Hub|HubRegistry|CliProcess|TempUploads|Payload)\.([a-z]\w*)", RegexOptions.CultureInvariant)

    /// The persistence functions a host may call: reads, the CLI child
    /// process (this CLI or a spoke's own `./praxis`), and the temporary
    /// upload directory. Every repository write happens inside the CLI.
    let allowedCalls =
        Set.ofList
            [ "FileWorkListRepository.readStateSources"
              "FileWorkListRepository.readDetail"
              "FileWorkListRepository.readAttachment"
              "FileWorkContextRepository.readContextView"
              "FileMetricRegistryRepository.read"
              "FileTelemetryUsageRepository.aggregate"
              "FileStateIdentityRepository.read"
              "ExecutionReads.envelopes"
              "ExecutionReads.tryLoad"
              "ExecutionReads.selectReadable"
              "ExecutionReads.qualifyWorkItem"
              "CheckpointShowReads.read"
              "CheckpointShowReads.history"
              "CheckpointShowReads.continuity"
              "Hub.listRepos"
              "Hub.listWork"
              "Hub.createWork"
              "Hub.repositoryStates"
              "Hub.repositoryWork"
              "Hub.repositoryWorkItem"
              "Hub.workState"
              "Hub.requestTransition"
              "Hub.defaultPort"
              "HubRegistry.reposNode"
              "HubRegistry.repoNode"
              "CliProcess.runSelf"
              "CliProcess.failureMessage"
              "TempUploads.withFiles"
              "Payload.embeddedText" ]

    let private withoutComments (text: string) =
        text.Split('\n') |> Array.map (fun line -> match line.IndexOf "//" with | -1 -> line | index -> line.Substring(0, index)) |> String.concat "\n"

    /// Every way `text` (the source of host file `name`) could write a
    /// repository file or keep state itself, as `name: finding` lines.
    let findings (name: string) (text: string) : string list =
        let code = withoutComments text

        let matches (pattern: Regex) = pattern.Matches code |> Seq.map _.Value |> Seq.distinct |> Seq.toList

        [ yield! matches writeApis |> List.map (fun api -> $"{name}: writes the file system directly ({api})")
          yield! matches retainedState |> List.map (fun found -> $"{name}: keeps state between requests ({found.Trim()})")
          yield!
              persistenceCall.Matches code
              |> Seq.map _.Value
              |> Seq.distinct
              |> Seq.filter (fun call -> not (allowedCalls.Contains call))
              |> Seq.map (fun call -> $"{name}: calls {call}, which is not a read or the CLI transition path")
              |> Seq.toList ]

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, "src", "Ros.Cli")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let hostSourceFindings () =
        let root = repositoryRoot (DirectoryInfo(AppContext.BaseDirectory))
        hostFiles |> List.collect (fun file -> findings file (File.ReadAllText(Path.Combine(root, "src", "Ros.Cli", file))))

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    let private identity fingerprint =
        { Repository = "repo"
          Commit = Some "abc"
          Branch = Some "main"
          Fingerprint = fingerprint }

    let private record path content =
        { Path = path; ContentHash = StateIdentity.contentHash (Encoding.UTF8.GetBytes(content: string)) }

    let private response contentType (body: string) : HttpResponseData =
        { Status = 200
          ContentType = contentType
          Headers = []
          Body = Encoding.UTF8.GetBytes body }

    /// Every file under `root` except Git's own, as relative path -> SHA-256.
    let private snapshot (root: string) : Map<string, string> =
        let gitDirectory = Path.Combine(root, ".git") + string Path.DirectorySeparatorChar

        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        |> Seq.filter (fun path -> not (path.StartsWith(gitDirectory, StringComparison.Ordinal)))
        |> Seq.map (fun path -> Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofSeq

    let private header (response: HttpResponseMessage) (name: string) =
        match response.Headers.TryGetValues name with
        | true, values -> values |> Seq.tryHead
        | _ -> None

    let private sourceHeaders = [ "Praxis-State-Fingerprint"; "Praxis-Repository"; "Praxis-Commit"; "Praxis-State-Stable" ]

    /// Status, the `Praxis-*` headers and the body of a GET.
    let private observe (server: ServedProcess) (path: string) =
        let answer = server.Get path
        Http.status answer, sourceHeaders |> List.map (fun name -> name, header answer name), Http.body answer

    let private stateIdentity (root: string) =
        CliHarness.rosOk root [ "state"; "identity"; "--json" ] |> _.Out |> JsonNode.Parse

    let private withRepository (prefix: string) (test: string -> unit) =
        let root = CliHarness.initializedRepository prefix None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "pre-continuity completion semantics"

        try
            test root
        finally
            CliHarness.removeDirectory root

    let private webRoutes id =
        [ "/api/v1/work"
          $"/api/v1/work/{id}"
          $"/api/v1/work/{id}/evidence?offline=true"
          $"/api/v1/work/{id}/telemetry"
          "/api/v1/executions"
          $"/api/v1/executions?workItem={id}"
          "/api/work"
          $"/api/work/{id}"
          "/"
          $"/work/{id}" ]

    /// Captures and starts `id` through the CLI.
    let private startedThroughCli (root: string) (id: string) =
        CliHarness.rosOk root [ "work"; "capture"; "--title"; $"Item {id}"; "--id"; id; "--occurred-at"; CliHarness.now () ] |> ignore
        CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; CliHarness.now () ] |> ignore
        CliHarness.rosOk root [ "work"; "start"; "--id"; id; "--occurred-at"; CliHarness.now () ] |> ignore

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    let private unitTests =
        [ { Name = "state identity: the fingerprint depends on record paths and bytes, never on enumeration order"
            Run =
              fun () ->
                  let a = record ".ros/work/queue.json" "{}"
                  let b = record "ros.json" "{\"name\":\"x\"}"
                  Assert.equal (StateIdentity.fingerprint [ a; b ]) (StateIdentity.fingerprint [ b; a ])
                  Assert.isTrue ((StateIdentity.fingerprint [ a; b ]).StartsWith "sha256:") "fingerprints are labelled with their algorithm"
                  Assert.isTrue (StateIdentity.fingerprint [ a; b ] <> StateIdentity.fingerprint [ a; record "ros.json" "{\"name\":\"y\"}" ]) "content changes the fingerprint"
                  Assert.isTrue (StateIdentity.fingerprint [ a ] <> StateIdentity.fingerprint [ { a with Path = ".ros/work/other.json" } ]) "the path changes the fingerprint"
                  Assert.isTrue (StateIdentity.fingerprint [] <> StateIdentity.fingerprint [ a ]) "an added record changes the fingerprint" }

          { Name = "state identity: every .ros file is an input except transient locks"
            Run =
              fun () ->
                  Assert.isTrue (StateIdentity.coversStatePath ".ros/context/current.json") "context"
                  Assert.isTrue (StateIdentity.coversStatePath ".ros\\telemetry\\executions\\EXE-1.json") "Windows separators"
                  Assert.isTrue (not (StateIdentity.coversStatePath ".ros/locks/work-protocol.lock")) "locks are transient"
                  Assert.isTrue (not (StateIdentity.coversStatePath "src/feature.fs")) "product files are not Praxis state" }

          { Name = "state identity: a read is attributed only when the identity around it agrees, and re-read while it changes"
            Run =
              fun () ->
                  let settled = StateIdentity.stable (fun () -> identity (Ok "sha256:1")) (fun () -> "value") 3
                  Assert.equal (true, "value", Ok "sha256:1") (settled.Stable, settled.Value, settled.Identity.Fingerprint)

                  // The state moves once, mid-read, then settles: re-read once.
                  let observations = Collections.Generic.Queue([ "sha256:1"; "sha256:2"; "sha256:2"; "sha256:2" ])
                  let reads = ref 0
                  let moved = StateIdentity.stable (fun () -> identity (Ok(observations.Dequeue()))) (fun () -> reads.Value <- reads.Value + 1; reads.Value) 3
                  Assert.equal (true, 2, Ok "sha256:2") (moved.Stable, moved.Value, moved.Identity.Fingerprint)

                  // It never settles: give up after the attempts, unstable, attributed to the last identity seen.
                  let tick = ref 0
                  let next () = tick.Value <- tick.Value + 1; identity (Ok $"sha256:{tick.Value}")
                  let restless = StateIdentity.stable next (fun () -> tick.Value) 3
                  Assert.equal (false, Ok "sha256:6") (restless.Stable, restless.Identity.Fingerprint)

                  let unreadable = StateIdentity.stable (fun () -> identity (Error "denied")) (fun () -> 1) 3
                  Assert.isTrue (not unreadable.Stable) "an unreadable state is never reported as stable" }

          { Name = "control-plane source: versioned documents gain source, every response gains the Praxis headers, legacy bodies are unchanged"
            Run =
              fun () ->
                  let attributed value = ControlPlaneSource.attribute { Identity = identity (Ok "sha256:f"); Stable = true; Value = value }
                  let versioned = attributed (response "application/json; charset=utf-8" """{"contract":"praxis.work-state","version":1,"kind":"work-list","items":[]}""")
                  let source = (JsonNode.Parse(Encoding.UTF8.GetString versioned.Body))["source"]
                  Assert.equal ("repo", "abc", "main", "sha256:f", true) (Http.text source "repository", Http.text source "commit", Http.text source "branch", Http.text source "stateFingerprint", source["stable"].GetValue<bool>())

                  let headers =
                      [ "Praxis-State-Fingerprint", "sha256:f"
                        "Praxis-Repository", "repo"
                        "Praxis-Commit", "abc"
                        "Praxis-State-Stable", "true" ]

                  Assert.equal headers versioned.Headers

                  for legacy in [ response "application/json; charset=utf-8" """[{"id":"WI-1"}]"""
                                  response "application/json; charset=utf-8" """{"error":"nope"}"""
                                  response "text/html; charset=utf-8" "<p>page</p>" ] do
                      let decorated = attributed legacy
                      Assert.equal legacy.Body decorated.Body
                      Assert.equal headers decorated.Headers

                  Assert.equal "repo" (ControlPlaneSource.headerValue "repo")
                  Assert.equal "r%C3%A9po" (ControlPlaneSource.headerValue "répo")
                  let unknown = ControlPlaneSource.attribute { Identity = { identity (Error "denied") with Commit = None }; Stable = false; Value = response "text/plain" "" }

                  Assert.equal
                      [ "Praxis-State-Fingerprint", "unavailable"
                        "Praxis-Repository", "repo"
                        "Praxis-Commit", "unknown"
                        "Praxis-State-Stable", "false"
                        "Praxis-State-Error", "denied" ]
                      unknown.Headers }

          { Name = "control-plane source: a GET is re-read while the state changes, any other request runs exactly once"
            Run =
              fun () ->
                  let request methodName = { Method = methodName; Segments = []; Query = []; ContentType = None; Body = [||] }
                  let tick = ref 0
                  let moving () = tick.Value <- tick.Value + 1; identity (Ok $"sha256:{tick.Value}")
                  let handled = ref 0
                  let handler _ = handled.Value <- handled.Value + 1; response "text/plain" ""
                  ControlPlaneSource.serve moving handler (request "GET") |> ignore
                  Assert.equal ControlPlaneSource.readAttempts handled.Value
                  handled.Value <- 0
                  ControlPlaneSource.serve moving handler (request "POST") |> ignore
                  Assert.equal 1 handled.Value }

          { Name = "state identity: the praxis.state-identity document round-trips"
            Run =
              fun () ->
                  let original = identity (Ok "sha256:abc")
                  Assert.equal (Ok original) (StateIdentityJson.parse (StateIdentityJson.document original))
                  let failed = { identity (Error "denied") with Commit = None; Branch = None }
                  Assert.equal (Ok failed) (StateIdentityJson.parse (StateIdentityJson.document failed))
                  Assert.isTrue (StateIdentityJson.parse (JsonNode.Parse """{"contract":"praxis.work-state"}""") |> Result.isError) "other contracts are refused" }

          { Name = "hub serve: registry changes are the praxis hub command lines"
            Run =
              fun () ->
                  let resolve (path: string) = "/abs/" + path
                  Assert.equal (Some [ "hub"; "register"; "/abs/spoke"; "--name"; "Spoke" ]) (HubWeb.registryCommand resolve (HubRoute.RegisterRepo(Some "spoke", Some "Spoke")))
                  Assert.equal (Some [ "hub"; "register"; "/abs/spoke" ]) (HubWeb.registryCommand resolve (HubRoute.RegisterRepo(Some "spoke", None)))
                  Assert.equal (Some [ "hub"; "unregister"; "alpha" ]) (HubWeb.registryCommand resolve (HubRoute.UnregisterRepo "alpha"))
                  Assert.equal None (HubWeb.registryCommand resolve HubRoute.ListRepos) }

          { Name = "host source check: the web and hub hosts write repository files only through the CLI and keep no state"
            Run = fun () -> hostSourceFindings () |> Assert.empty }

          { Name = "host source check: a direct write, a retained cache or a non-read persistence call is reported"
            Run =
              fun () ->
                  let seeded =
                      String.concat
                          "\n"
                          [ "module Host ="
                            "    let mutable cache = None"
                            "    let private seen = System.Collections.Concurrent.ConcurrentDictionary<string, string>()"
                            "    let save root = File.WriteAllText(Path.Combine(root, \".ros/x\"), \"\")"
                            "    let register root = Hub.registerRepo root \"p\" None"
                            "    let record root = FileWorkCaptureRepository.capture root"
                            "    let allowed root = FileWorkListRepository.readStateSources root"
                            "    // File.Delete in a comment is not code" ]

                  let found = findings "Host.fs" seeded
                  Assert.equal 5 found.Length
                  Http.contains "File.WriteAllText" (String.concat "\n" found)
                  Http.contains "Hub.registerRepo" (String.concat "\n" found)
                  Http.contains "FileWorkCaptureRepository.capture" (String.concat "\n" found)
                  Http.contains "ConcurrentDictionary" (String.concat "\n" found)
                  Http.contains "let mutable" (String.concat "\n" found) } ]

    let private webTests =
        [ { Name = "web serve: every response carries the repository, commit and state fingerprint it was derived from"
            Run =
              fun () ->
                  withRepository "ros-host" (fun root ->
                      startedThroughCli root "WI-1" |> ignore
                      use server = new ServedProcess(root, [ "web"; "serve" ])
                      let expected = stateIdentity root

                      for path in [ "/api/v1/work"; "/api/work"; "/"; "/styles.css"; "/api/v1/work/NOPE" ] do
                          let status, headers, _ = observe server path
                          Assert.isTrue (status = 200 || status = 404) $"{path} answered {status}"

                          Assert.equal
                              [ "Praxis-State-Fingerprint", Some(Http.text expected "stateFingerprint")
                                "Praxis-Repository", Some(Http.text expected "repository")
                                "Praxis-Commit", Some(Http.text expected "commit")
                                "Praxis-State-Stable", Some "true" ]
                              headers

                      let _, _, body = observe server "/api/v1/work"
                      let source = (JsonNode.Parse body)["source"]
                      Assert.equal (Http.text expected "stateFingerprint") (Http.text source "stateFingerprint")
                      Assert.equal (Http.text expected "commit") (Http.text source "commit")
                      Assert.equal (Http.text expected "repository") (Http.text source "repository")

                      let accepted = server.PostJson("/api/v1/work/WI-1/transitions", """{"action":"block","reason":"waiting"}""")
                      Assert.equal 200 (Http.status accepted)
                      let after = stateIdentity root
                      Assert.equal (Some(Http.text after "stateFingerprint")) (header accepted "Praxis-State-Fingerprint")
                      Assert.isTrue (Http.text after "stateFingerprint" <> Http.text expected "stateFingerprint") "a transition changes the fingerprint"
                      Assert.equal (Http.text after "stateFingerprint") (Http.text ((Http.json accepted).["source"]) "stateFingerprint")) }

          { Name = "web serve: a restarted host on the same repository answers every route identically"
            Run =
              fun () ->
                  withRepository "ros-host" (fun root ->
                      startedThroughCli root "WI-1"
                      CliHarness.rosOk root [ "work"; "capture"; "--title"; "Second"; "--id"; "WI-2"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let routes = webRoutes "WI-1"

                      let answers () =
                          use server = new ServedProcess(root, [ "web"; "serve" ])
                          routes |> List.map (fun path -> path, observe server path)

                      let first = answers ()
                      let second = answers ()

                      for (path, (status, _, _)) in first do
                          Assert.isTrue (status = 200) $"{path} answered {status}"

                      for ((path, before), (_, after)) in List.zip first second do
                          Assert.isTrue (before = after) $"{path} differs after a restart:\n{before}\n---\n{after}") }

          { Name = "web serve: a change made through the CLI while the host runs is served without a restart"
            Run =
              fun () ->
                  withRepository "ros-host" (fun root ->
                      use server = new ServedProcess(root, [ "web"; "serve" ])
                      let before = server.Get "/api/v1/work"
                      let ids document = (Http.json document).["items"].AsArray() |> Seq.map (fun item -> Http.text item "id") |> Seq.toList
                      Assert.isTrue (not (List.contains "WI-9" (ids before))) "WI-9 does not exist yet"

                      CliHarness.rosOk root [ "work"; "capture"; "--title"; "Added outside the host"; "--id"; "WI-9"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let captured = server.Get "/api/v1/work"
                      Assert.equal (ids before @ [ "WI-9" ] |> List.sort) (ids captured |> List.sort)
                      Assert.isTrue (header before "Praxis-State-Fingerprint" <> header captured "Praxis-State-Fingerprint") "the capture changes the fingerprint"

                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--id"; "WI-9"; "--action"; "ready"; "--occurred-at"; CliHarness.now () ] |> ignore
                      CliHarness.rosOk root [ "work"; "start"; "--id"; "WI-9"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let started = Http.json (server.Get "/api/v1/work/WI-9")
                      Assert.equal "active" (Http.text started["item"] "semanticState")
                      Assert.equal (Http.text (stateIdentity root) "stateFingerprint") (Http.text started["source"] "stateFingerprint")) }

          { Name = "web serve: answering every read route leaves every repository file byte-for-byte unchanged"
            Run =
              fun () ->
                  withRepository "ros-host" (fun root ->
                      startedThroughCli root "WI-1"
                      use server = new ServedProcess(root, [ "web"; "serve" ])
                      let before = snapshot root

                      for path in webRoutes "WI-1" @ [ "/api/v1/work/WI-1/evidence"; "/api/status"; "/api/validate"; "/validate"; "/styles.css" ] do
                          server.Get path |> ignore

                      Assert.isTrue (snapshot root = before) "a read route changed a repository file") } ]

    let private spoke (prefix: string) =
        let root = CliHarness.initializedRepository prefix None
        let launcher = Path.Combine(root, "praxis")
        File.WriteAllText(launcher, $"#!/bin/sh\nexec dotnet \"{CliHarness.cli}\" \"$@\"\n")

        if not (OperatingSystem.IsWindows()) then
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

        CliHarness.commitAll root "launcher"
        root

    let private withHub (test: string -> string -> unit) =
        let hub = CliHarness.initializedRepository "ros-hub" (Some "project-administration")
        let spokeRoot = spoke "ros-spoke"

        try
            CliHarness.rosOk hub [ "hub"; "register"; spokeRoot; "--name"; "Spoke" ] |> ignore
            test hub spokeRoot
        finally
            [ hub; spokeRoot ] |> List.iter CliHarness.removeDirectory

    let private hubTests =
        [ { Name = "hub serve: a restarted hub answers identically, and work rows carry their spoke's state identity"
            Run =
              fun () ->
                  withHub (fun hub spokeRoot ->
                      CliHarness.rosOk spokeRoot [ "work"; "capture"; "--title"; "Spoke item"; "--id"; "WI-1"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let routes = [ "/api/repos"; "/api/work"; "/" ]

                      let answers () =
                          use server = new ServedProcess(hub, [ "hub"; "serve" ])
                          routes |> List.map (fun path -> path, observe server path)

                      let first = answers ()
                      let second = answers ()

                      for ((path, before), (_, after)) in List.zip first second do
                          Assert.isTrue (before = after) $"{path} differs after a restart:\n{before}\n---\n{after}"

                      let hubIdentity = stateIdentity hub
                      let _, headers, body = first |> List.find (fst >> (=) "/api/work") |> snd
                      Assert.equal (Some(Http.text hubIdentity "stateFingerprint")) (headers |> List.find (fst >> (=) "Praxis-State-Fingerprint") |> snd)

                      let row = (JsonNode.Parse body).AsArray()[0]
                      let spokeIdentity = stateIdentity spokeRoot
                      Assert.equal (Http.text spokeIdentity "stateFingerprint") (Http.text row["repoSource"] "stateFingerprint")
                      Assert.equal (Http.text spokeIdentity "commit") (Http.text row["repoSource"] "commit")
                      Assert.equal (Http.text spokeIdentity "repository") (Http.text row["repoSource"] "repository")) }

          { Name = "hub serve: changes made through a spoke's CLI and the hub's CLI while the hub runs are served without a restart"
            Run =
              fun () ->
                  withHub (fun hub spokeRoot ->
                      use server = new ServedProcess(hub, [ "hub"; "serve" ])
                      let rows () = (Http.json (server.Get "/api/work")).AsArray() |> Seq.toList
                      let idsOf (listed: JsonNode list) = listed |> List.map (fun row -> Http.text row "id")
                      let before = rows ()
                      Assert.isTrue (not (List.contains "WI-7" (idsOf before))) "WI-7 does not exist yet"

                      CliHarness.rosOk spokeRoot [ "work"; "capture"; "--title"; "Spoke item"; "--id"; "WI-7"; "--occurred-at"; CliHarness.now () ] |> ignore
                      let listed = rows ()
                      Assert.equal (idsOf before @ [ "WI-7" ] |> List.sort) (idsOf listed |> List.sort)
                      let spokeFingerprint = Http.text (stateIdentity spokeRoot) "stateFingerprint"

                      for row in listed do
                          Assert.equal spokeFingerprint (Http.text row.["repoSource"] "stateFingerprint")

                      let reposBefore = server.Get "/api/repos"
                      let second = spoke "ros-spoke-b"

                      try
                          CliHarness.rosOk hub [ "hub"; "register"; second; "--name"; "Second" ] |> ignore
                          let reposAfter = server.Get "/api/repos"
                          Assert.equal 2 ((Http.json reposAfter).AsArray().Count)
                          Assert.isTrue (header reposBefore "Praxis-State-Fingerprint" <> header reposAfter "Praxis-State-Fingerprint") "registration changes the hub's fingerprint"
                      finally
                          CliHarness.removeDirectory second) }

          { Name = "hub serve: registering over HTTP is done by the praxis hub command, and reads change no file"
            Run =
              fun () ->
                  withHub (fun hub spokeRoot ->
                      use server = new ServedProcess(hub, [ "hub"; "serve" ])
                      let before = snapshot hub
                      let spokeBefore = snapshot spokeRoot

                      for path in [ "/api/repos"; "/api/work"; "/"; "/styles.css" ] do
                          server.Get path |> ignore

                      Assert.isTrue (snapshot hub = before) "a hub read changed a hub file"
                      Assert.isTrue (snapshot spokeRoot = spokeBefore) "a hub read changed a spoke file"

                      let second = spoke "ros-spoke-b"

                      try
                          let registered = server.PostJson("/api/repos", JsonObject([ Collections.Generic.KeyValuePair("path", JsonValue.Create second :> JsonNode) ]).ToJsonString())
                          Assert.equal 200 (Http.status registered)
                          let entry = Http.json registered
                          Assert.equal second (Http.text entry "path")
                          let registry = JsonNode.Parse(CliHarness.read hub ".ros/hub/registry.json")
                          Assert.equal 2 (registry["repos"].AsArray().Count)
                          Http.contains (Http.text entry "id") (CliHarness.read hub ".ros/hub/registry.md")
                          Assert.equal (Some(Http.text (stateIdentity hub) "stateFingerprint")) (header registered "Praxis-State-Fingerprint")
                          Assert.equal 200 (Http.status (server.Delete $"""/api/repos/{Http.text entry "id"}"""))
                          Assert.equal 1 ((JsonNode.Parse(CliHarness.read hub ".ros/hub/registry.json")).["repos"].AsArray().Count)
                      finally
                          CliHarness.removeDirectory second) } ]

    let private cliTests =
        [ { Name = "state identity: the CLI reports the repository, HEAD and the fingerprint of the Praxis records, and reads only"
            Run =
              fun () ->
                  withRepository "ros-identity" (fun root ->
                      let before = snapshot root
                      let document = stateIdentity root
                      Assert.equal "praxis.state-identity" (Http.text document "contract")
                      Assert.equal (CliHarness.git root [ "rev-parse"; "HEAD" ] |> _.Trim()) (Http.text document "commit")
                      Assert.equal "main" (Http.text document "branch")
                      Assert.isTrue (snapshot root = before) "state identity changed a file"
                      Assert.equal (document.ToJsonString()) ((stateIdentity root).ToJsonString())

                      File.WriteAllText(Path.Combine(root, "README-extra.md"), "not Praxis state\n")
                      Assert.equal (Http.text document "stateFingerprint") (Http.text (stateIdentity root) "stateFingerprint")

                      CliHarness.rosOk root [ "work"; "capture"; "--title"; "Changes state"; "--occurred-at"; CliHarness.now () ] |> ignore
                      Assert.isTrue (Http.text document "stateFingerprint" <> Http.text (stateIdentity root) "stateFingerprint") "a capture changes the fingerprint"

                      let text = CliHarness.rosOk root [ "state"; "identity" ] |> _.Out
                      Http.contains "state fingerprint: sha256:" text) } ]

    let tests = unitTests @ cliTests @ webTests @ hubTests
