namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

/// `praxis construct` through the real CLI (Praxis #242): an installed
/// repository with a native group, a signed operator grant in the protected
/// host policy, Ordo's real time-entry timer ECIR and construction plan, and
/// the pinned Ordo engine. The plan file is the only agent-supplied input.
[<RequireQualifiedAccess>]
module ConstructionCliTests =
    open PraxisCli

    let private t name run = { Name = "construction cli: " + name; Run = run }
    let internal coordinator = agent "example/coordinator" "example" "coordinator" "coordinator-session"
    let internal target = "refs/heads/construction-target"
    let internal group = "GROUP-CONSTRUCT-001"
    let internal fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "construction", "time-entry-timer")
    let private fixture name = File.ReadAllText(Path.Combine(fixtures, name))
    let internal requirement = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L116-L129"

    let internal onPath (name: string) =
        let file = if OperatingSystem.IsWindows() then name + ".exe" else name
        let selected = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
                       |> Array.map (fun path -> Path.Combine(path, file)) |> Array.find File.Exists |> Path.GetFullPath
        let info = FileInfo selected
        if info.LinkTarget <> null then info.ResolveLinkTarget(true).FullName else selected

    let private digest path = GitWorkspace.fileDigest path |> function Ok value -> value | Error error -> failwith error

    type internal Host = { Parent: string; Clone: string; Config: string; Plan: string; Journal: string }

    let private echo () =
        let spec = LocalWorkerProcessTests.spec "echo" [ "unused" ]
        JsonNode.Parse(JsonSerializer.Serialize {| executable = spec.Executable; sha256 = spec.ExecutableDigest; arguments = spec.Arguments
                                                   timeoutMilliseconds = 60000; maxOutputBytes = 65536 |})

    /// Host slot policy: one slot for both timer port contracts, and one
    /// declared slot no contract of this construction needs.
    let internal timerSlots =
        """[{"file":"Extensions.fs","contracts":["ITimerPorts.CurrentTime","ITimerPorts.Perform/Stop"],"maxBytes":4096},
            {"file":"Completion.fs","contracts":["ITimerRules.Guard/NotInThisPlan"],"maxBytes":4096}]"""

    let internal configurationWith (slots: string) (mount: string) clone host git (construction: bool) =
        let roots = JsonObject()
        let names = [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence"; "checkouts" ] @ (if construction then [ "construction" ] else [])
        for name in names do
            let path = Path.Combine(host, name)
            Directory.CreateDirectory path |> ignore
            roots[name] <- JsonValue.Create path
        let validator id tests = JsonNode.Parse(JsonSerializer.Serialize {| id = id; tests = tests |}) :?> JsonObject
        let system = JsonObject()
        for name in [ "build"; "tests"; "architecture"; "scope" ] do
            let node = validator ("host-" + name) (name = "tests")
            let command = echo ()
            if name = "tests" then command["arguments"] <- JsonNode.Parse """["{trx}"]"""
            node["command"] <- command
            system[name] <- node
        let workerNode = JsonNode.Parse """{"adapterId":"unused","workerId":"unused","roles":["implementation"],"operatingSystems":["linux","macos","windows"]}"""
        workerNode["command"] <- echo ()
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "praxis.local-host/1"
        node["repositoryRoot"] <- JsonValue.Create(Path.GetFullPath clone)
        node["repositoryIdentity"] <- JsonValue.Create(FileWorkConfigRepository.readRepositoryId clone)
        node["fileAccess"] <- JsonValue.Create "portable-development"
        node["roots"] <- roots
        node["constructionMount"] <- JsonValue.Create mount
        node["extensionSlots"] <- JsonNode.Parse slots
        node["git"] <- JsonNode.Parse(JsonSerializer.Serialize {| executable = git; sha256 = digest git |})
        node["committer"] <- JsonNode.Parse """{"name":"Praxis controller","email":"controller@example.invalid"}"""
        node["validatorPolicyRevision"] <- JsonValue.Create "validators-1"
        node["systemValidators"] <- system
        node["acceptanceValidators"] <- JsonArray()
        node["workers"] <- JsonArray(workerNode)
        node["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes"}"""
        node.ToJsonString()

    let private configurationAt = configurationWith timerSlots
    let private configuration = configurationAt "src/Domain"

    /// `grantExtra parent clone` adds grant-draft arguments (CONSTRUCT-008:
    /// `--decisions FILE`); it may write files under `parent` first.
    let internal withHostGranting (grantExtra: string -> string -> string list) (construction: bool) (prepare: string -> unit) test =
        let parent = CliHarness.temporaryDirectory "construction"
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        try
            let _, clone = installedRepository parent "repo"
            CliHarness.write clone "ecir/timer/source-manifest.json" (fixture "source-manifest.json")
            CliHarness.write clone "ecir/timer/blueprint.json" (fixture "blueprint.json")
            CliHarness.write clone "src/Domain/README.md" "Handwritten domain host.\n"
            prepare clone
            PraxisCli.run clone (Some coordinator) [ "add"; "Construct the timer workflow"; "--id"; "WI-TIMER" ] |> ok |> ignore
            PraxisCli.run clone (Some coordinator) [ "work"; "backlog-transition"; "--id"; "WI-TIMER"; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
            PraxisCli.run clone (Some coordinator) [ "work"; "group"; "create"; "--group"; group; "--member"; "WI-TIMER"; "--occurred-at"; now () ] |> ok |> ignore
            let source = pushAll clone "construction source"
            GitFixture.git clone [ "update-ref"; target; source ] |> ignore
            let host = Path.Combine(parent, "host")
            Directory.CreateDirectory host |> ignore
            let config = Path.Combine(host, "config.json")
            File.WriteAllText(config, configuration clone host (onPath "git") construction)
            let publicKey = Path.Combine(parent, "operator-public.pem")
            File.WriteAllText(publicKey, key.ExportSubjectPublicKeyInfoPem())
            let run arguments = PraxisCli.run clone (Some coordinator) arguments
            run [ "local"; "operator"; "init"; "--host-config"; config; "--public-key"; publicKey; "--key-id"; "op-1"
                  "--approver"; "operator"; "--revision"; "rev-1"; "--declare-qualified-release" ] |> ok |> ignore
            let draft, payload, signature = Path.Combine(parent, "grant.json"), Path.Combine(parent, "grant.bin"), Path.Combine(parent, "grant.sig")
            let drafted =
                run ([ "local"; "operator"; "grant-draft"; "--host-config"; config; "--key-id"; "op-1"; "--approver"; "operator"
                       "--group"; group; "--cohort"; "COH-TIMER"; "--source"; source; "--manifest"; "ecir/timer/source-manifest.json"
                       "--blueprint"; "ecir/timer/blueprint.json"; "--member"; "WI-TIMER=" + requirement
                       "--decision"; "DEC-TIMER-STATES"; "--draft"; draft; "--payload"; payload ] @ grantExtra parent clone)
            if drafted.ExitCode <> 0 then
                Assert.isTrue (not (File.Exists draft)) "refused grant-draft wrote an unsigned draft"
                Assert.isTrue (not (File.Exists payload)) "refused grant-draft wrote a signing payload"
            drafted |> ok |> ignore
            File.WriteAllBytes(signature, key.SignData(File.ReadAllBytes payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            run [ "local"; "operator"; "grant"; "--host-config"; config; "--draft"; draft; "--signature"; signature; "--revision"; "rev-2" ] |> ok |> ignore
            let plan = Path.Combine(parent, "plan.json")
            File.WriteAllText(plan, fixture "plan.json")
            test { Parent = parent; Clone = clone; Config = config; Plan = plan; Journal = Path.Combine(host, "construction") }
        finally
            GitFixture.cleanup parent

    let internal withHost (construction: bool) (prepare: string -> unit) test = withHostGranting (fun _ _ -> []) construction prepare test

    let internal construct (host: Host) verb plan =
        PraxisCli.run host.Clone (Some coordinator) [ "construct"; verb; "--host-config"; host.Config; "--group"; group; "--plan"; plan; "--target"; target ]

    let internal tip (host: Host) = GitFixture.git host.Clone [ "rev-parse"; target ]

    let private refused (result: PraxisCli.Result) (fragment: string) =
        Assert.equal 3 result.ExitCode
        Assert.isTrue (result.Error.Contains fragment) result.Error

    let private writes (document: JsonNode) =
        document["writes"] :?> JsonArray |> Seq.map (fun write -> text write["kind"], text write["path"]) |> Seq.toList

    // ---- Praxis #243: routing and host-declared handwritten extension slots ----

    let internal handwritten = File.ReadAllText(Path.Combine(fixtures, "handwritten", "Extensions.fs"))

    let private route (host: Host) =
        PraxisCli.run host.Clone (Some coordinator) [ "construct"; "route"; "--host-config"; host.Config; "--group"; group; "--plan"; host.Plan ]

    let internal extendWith (host: Host) (groupId: string) (plan: string) (files: string) (role: string) (extra: string list) =
        PraxisCli.run host.Clone (Some coordinator)
            ([ "construct"; "extend"; "--host-config"; host.Config; "--group"; groupId; "--plan"; plan; "--target"; target; "--files"; files; "--role"; role ] @ extra)

    let internal extend (host: Host) files =
        PraxisCli.run host.Clone (Some coordinator)
            [ "construct"; "extend"; "--host-config"; host.Config; "--group"; group; "--plan"; host.Plan; "--target"; target; "--files"; files ]

    /// A fresh outbox holding exactly these (name, content) regular files.
    let internal outbox (host: Host) (files: (string * string) list) =
        let directory = Path.Combine(host.Parent, "outbox-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory directory |> ignore
        for name, content in files do File.WriteAllText(Path.Combine(directory, name), content)
        directory

    let private lines (text: string) = text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim() |> Set.ofArray
    let private tree (host: Host) (commit: string) = GitFixture.git host.Clone [ "ls-tree"; "-r"; "--full-tree"; commit ] |> lines

    let internal reconfigure (host: Host) slots mount =
        File.WriteAllText(host.Config, configurationWith slots mount host.Clone (Path.GetDirectoryName host.Config) (onPath "git") true)

    /// Every refusal names each entry with its own reason, before any effect.
    let private refusedEach (host: Host) (result: PraxisCli.Result) (expected: (string * string) list) =
        Assert.equal 3 result.ExitCode
        for name, reason in expected do
            Assert.isTrue (result.Error.Contains(name + ": " + reason)) (sprintf "%s / %s: %s" name reason result.Error)

    let private routes (host: Host) =
        let directory = Path.Combine(host.Journal, "routes")
        if Directory.Exists directory then Directory.GetFiles directory |> Array.toList else []

    let private extensionTests =
        [ t "an Ordo vocabulary 2 plan whose Stop failure is unresolved surfaces it as a semantic decision, never custom code" (fun () ->
              withHost true ignore (fun host ->
                  let v2 = Path.Combine(host.Parent, "plan.v2-unresolved.json")
                  File.WriteAllText(v2, fixture "plan.v2-unresolved.json")
                  let preview = construct host "preview" v2 |> ok
                  Assert.equal "ordo.construction/2" (text preview.Json["binding"].["generatorVersion"])
                  let obligations = preview.Json["obligations"] :?> JsonArray |> Seq.toList
                  let failure = obligations |> List.filter (fun o -> text o["kind"] = "resolve-effect-failure")
                  Assert.equal 1 failure.Length
                  Assert.equal "Stop" (text failure.Head["effect"])
                  Assert.isTrue ((failure.Head["requirementKeys"] :?> JsonArray).Count > 0) "effect-failure obligation lost its requirement keys"
                  let routed = route { host with Plan = v2 } |> ok
                  let record = JsonNode.Parse(File.ReadAllText((routes host).Head))
                  Assert.equal (text routed.Json["routeId"]) (text record["routeId"])
                  let semantic = record["semantic"] :?> JsonArray |> Seq.toList
                  Assert.isTrue (semantic |> List.exists (fun o -> text o["effectFailure"] = "Stop" && text o["subject"] <> "")) (record.ToJsonString())
                  Assert.isTrue (record["custom"] :?> JsonArray |> Seq.forall (fun r -> text r["id"] <> text failure.Head["id"])) "effect failure routed to custom code"))
          t "route classifies deterministic, semantic and bounded custom work and persists one content-addressed record" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let first = route host |> ok
                  let second = route host |> ok
                  Assert.equal (text first.Json["routeId"]) (text second.Json["routeId"])
                  Assert.equal before (tip host)
                  Assert.equal 1 (routes host).Length
                  let record = JsonNode.Parse(File.ReadAllText((routes host).Head))
                  Assert.equal (text first.Json["routeId"]) (text record["routeId"])
                  Assert.equal "praxis.construction-routing/1" (text record["schemaVersion"])
                  let count (name: string) = (record[name] :?> JsonArray).Count
                  // (a) generated model, transition, orchestration and manifest
                  Assert.equal 4 (count "deterministic")
                  // (b) the two declared unresolved questions
                  Assert.equal 2 (count "semantic")
                  // (c) both port contracts, in the one declared slot
                  Assert.equal 2 (count "custom")
                  Assert.equal 1 (count "verification")
                  Assert.equal 0 (count "exceptions")
                  for routed in record["custom"] :?> JsonArray do
                      Assert.equal "src/Domain/Extensions.fs" (text routed["slot"])
                      Assert.isTrue ((routed["requirementKeys"] :?> JsonArray).Count > 0) "custom route lost its requirement keys"
                  let measured = record["measurement"]
                  Assert.equal 4 (measured["deterministicWrites"].GetValue<int>())
                  Assert.equal 1 (measured["boundedCustomSlots"].GetValue<int>())
                  Assert.equal 2 (measured["boundedCustomObligations"].GetValue<int>())
                  Assert.equal 2 (measured["semanticObligations"].GetValue<int>())
                  Assert.equal "unavailable" (text measured["reasoningTokens"])
                  Assert.equal "disabled" (text record["workerExecution"].["status"])))
          t "an implement obligation with no declared slot is a typed exception carrying its requirement keys" (fun () ->
              withHost true ignore (fun host ->
                  reconfigure host """[{"file":"Extensions.fs","contracts":["ITimerPorts.CurrentTime"],"maxBytes":4096}]""" "src/Domain"
                  let routed = route host |> ok
                  let exceptions = routed.Json["exceptions"] :?> JsonArray |> Seq.toList
                  Assert.equal 1 exceptions.Length
                  let missing = exceptions.Head
                  Assert.equal "unroutable-implementation" (text missing["kind"])
                  Assert.equal "IMPLEMENT:ITimerPorts.Perform/Stop" (text missing["obligationId"])
                  let preview = construct host "preview" host.Plan |> ok
                  let original = preview.Json["obligations"] :?> JsonArray |> Seq.find (fun o -> text o["id"] = "IMPLEMENT:ITimerPorts.Perform/Stop")
                  Assert.equal (original["requirementKeys"].ToJsonString()) (missing["requirementKeys"].ToJsonString())
                  Assert.isTrue ((missing["requirementKeys"] :?> JsonArray).Count > 0) "exception lost its requirement keys"))
          t "extend commits the pilot's handwritten file into its slot on the exact construction, with trailers, then is idempotent" (fun () ->
              withHost true ignore (fun host ->
                  let head = GitFixture.git host.Clone [ "rev-parse"; "HEAD" ]
                  let routed = route host |> ok
                  let applied = construct host "apply" host.Plan |> ok
                  let construction = tip host
                  let files = outbox host [ "Extensions.fs", handwritten ]
                  let extended = extend host files |> ok
                  Assert.equal "applied" (text extended.Json["outcome"])
                  let candidate = text extended.Json["candidateCommit"]
                  Assert.equal candidate (tip host)
                  Assert.equal construction (GitFixture.git host.Clone [ "rev-parse"; candidate + "^" ])
                  Assert.equal head (GitFixture.git host.Clone [ "rev-parse"; "HEAD" ])
                  // Exact tree: the construction's tree plus exactly the slot file.
                  let added = tree host candidate - tree host construction
                  Assert.equal 1 added.Count
                  Assert.isTrue (added.MinimumElement.StartsWith "100644 blob " && added.MinimumElement.EndsWith "\tsrc/Domain/Extensions.fs") (sprintf "%A" added)
                  Assert.isTrue (Set.isSubset (tree host construction) (tree host candidate)) "extension removed or changed construction files"
                  let info = Diagnostics.ProcessStartInfo("git", [ "cat-file"; "blob"; candidate + ":src/Domain/Extensions.fs" ])
                  info.WorkingDirectory <- host.Clone
                  info.RedirectStandardOutput <- true
                  use git = Diagnostics.Process.Start info
                  let committed = git.StandardOutput.ReadToEnd()
                  git.WaitForExit()
                  Assert.equal handwritten committed
                  let message = GitFixture.git host.Clone [ "log"; "-1"; "--format=%B"; candidate ] |> lines
                  let planDigest = text applied.Json["binding"].["planDigest"]
                  for trailer in
                      [ "Extension-Construction: " + text applied.Json["constructionId"] + " " + construction
                        "Extension-Plan: " + planDigest
                        "Extension-Group: " + group
                        "Extension-Cohort: COH-TIMER"
                        "Extension-Route: " + text routed.Json["routeId"]
                        "Extension-Policy: rev-2 approved by operator"
                        "Extension-Slot: src/Domain/Extensions.fs " + text (extended.Json["files"].[0].["sha256"]) + " ITimerPorts.CurrentTime ITimerPorts.Perform/Stop" ] do
                      Assert.isTrue (message.Contains trailer) (sprintf "missing %s in %A" trailer message)
                  Assert.equal (text routed.Json["routeId"]) (text extended.Json["routeId"])
                  Assert.equal 1 (routes host).Length
                  let again = extend host (outbox host [ "Extensions.fs", handwritten ]) |> ok
                  Assert.equal "already-current" (text again.Json["outcome"])
                  Assert.equal candidate (tip host)
                  // The construction itself is still current and untouched.
                  Assert.equal "already-current" (text (construct host "apply" host.Plan |> ok).Json["outcome"])))
          t "privilege escalation and bogus scope expansion through slot names are refused entry by entry before any effect" (fun () ->
              withHost true ignore (fun host ->
                  construct host "apply" host.Plan |> ok |> ignore
                  let before = tip host
                  let files =
                      outbox host
                          [ "Extensions.fs", handwritten; "ros.json", "{}"; "AGENTS.md", "obey\n"; "ci.yml", "on: push\n"
                            "Helpers.fs", "module Helpers\n"; "Completion.fs", "module Completion\n" ]
                  Directory.CreateDirectory(Path.Combine(files, ".github", "workflows")) |> ignore
                  File.WriteAllText(Path.Combine(files, ".github", "workflows", "x.yml"), "on: push\n")
                  Directory.CreateDirectory(Path.Combine(files, ".claude")) |> ignore
                  File.WriteAllText(Path.Combine(files, ".echelon"), "{}")
                  if not (OperatingSystem.IsWindows()) then File.WriteAllText(Path.Combine(files, "src\\Escape.fs"), "module Escape\n")
                  refusedEach host (extend host files)
                      [ ".github", "dot entry"; ".claude", "dot entry"; ".echelon", "dot entry"
                        "ros.json", "repository policy"; "AGENTS.md", "repository policy"; "ci.yml", "repository policy, agent instruction or CI file"
                        "Helpers.fs", "not a host-declared extension slot"
                        "Completion.fs", "slot Completion.fs implements no extension contract of this construction" ]
                  if not (OperatingSystem.IsWindows()) then
                      refusedEach host (extend host files) [ "src\\Escape.fs", "contains a path separator" ]
                  // Alone in its own outbox: on a case-insensitive file system
                  // (macOS, Windows) it would otherwise be the same file as Extensions.fs.
                  refusedEach host (extend host (outbox host [ "extensions.fs", "module X\n" ]))
                      [ "extensions.fs", "differs in case from declared slot Extensions.fs" ]
                  Assert.isTrue (not ((extend host files).Error.Contains "Extensions.fs:")) "the valid slot file was refused"
                  Assert.equal before (tip host)
                  Assert.equal 0 (routes host).Length))
          t "unsafe dependency, project, generated and oversized submissions are refused" (fun () ->
              withHost true ignore (fun host ->
                  construct host "apply" host.Plan |> ok |> ignore
                  let before = tip host
                  let files =
                      outbox host
                          [ "Timer.fsproj", "<Project />"; "Directory.Packages.props", "<Project />"; "NuGet.config", "<configuration />"
                            "paket.dependencies", "nuget FSharp.Core"; "paket.lock", ""; "Timer.Model.g.fs", "module Forged\n"; "blueprint.json", "{}"
                            "requirements.md", "changed\n" ]
                  Directory.CreateDirectory(Path.Combine(files, "generated", "Timer")) |> ignore
                  File.WriteAllText(Path.Combine(files, "generated", "Timer", "Timer.Model.g.fs"), "module Forged\n")
                  refusedEach host (extend host files)
                      [ "Timer.fsproj", "project or build file"; "Directory.Packages.props", "project or build file"
                        "NuGet.config", "package or dependency file"; "paket.dependencies", "package or dependency file"; "paket.lock", "package or dependency file"
                        "Timer.Model.g.fs", "generated-file name"; "generated", "the generated ownership directory"
                        "blueprint.json", "ECIR or construction source document"; "requirements.md", "requirement source document" ]
                  refusedEach host (extend host (outbox host [ "Extensions.fs", String('x', 4097) ])) [ "Extensions.fs", "4097 bytes exceeds the slot limit of 4096" ]
                  refusedEach host (extend host (outbox host [ "Extensions.fs", "// <auto-generated by ordo.construction 1>\nmodule Forged\n" ]))
                      [ "Extensions.fs", "carries the generated-file marker" ]
                  Assert.equal before (tip host)
                  // The host cannot declare an unsafe slot either.
                  for slot, reason in [ "Directory.Packages.props", "project or build file"; "src/Extensions.fs", "path separator"; ".github", "dot entry" ] do
                      reconfigure host (sprintf """[{"file":"%s","contracts":["ITimerPorts.CurrentTime"],"maxBytes":4096}]""" slot) "src/Domain"
                      refused (extend host (outbox host [ "Extensions.fs", handwritten ])) ("extension slot " + slot + " refused: ")
                      refused (route host) reason
                  reconfigure host timerSlots ".github/workflows"
                  refused (route host) "extension slots may not be placed under CI, agent or repository configuration"
                  Assert.equal before (tip host)))
          t "a symlinked slot file or files directory is refused, never followed" (fun () ->
              if not (OperatingSystem.IsWindows()) then
                  withHost true ignore (fun host ->
                      construct host "apply" host.Plan |> ok |> ignore
                      let before = tip host
                      let secret = Path.Combine(host.Parent, "secret.fs")
                      File.WriteAllText(secret, "module Secret\n")
                      let files = outbox host []
                      File.CreateSymbolicLink(Path.Combine(files, "Extensions.fs"), secret) |> ignore
                      refusedEach host (extend host files) [ "Extensions.fs", "symbolic link" ]
                      let real = outbox host [ "Extensions.fs", handwritten ]
                      let linked = Path.Combine(host.Parent, "linked-outbox")
                      Directory.CreateSymbolicLink(linked, real) |> ignore
                      refused (extend host linked) "symbolic link"
                      Assert.equal before (tip host)))
          t "a stale plan is refused: a target without the construction, or carrying another construction's trailers" (fun () ->
              withHost true ignore (fun host ->
                  let base' = tip host
                  let files = outbox host [ "Extensions.fs", handwritten ]
                  refused (extend host files) "target does not carry this exact construction"
                  Assert.equal base' (tip host)
                  // Same generated bytes, but no construction commit for this plan in history.
                  let applied = construct host "apply" host.Plan |> ok
                  let copied = GitFixture.git host.Clone [ "commit-tree"; text applied.Json["candidateCommit"] + "^{tree}"; "-p"; base'; "-m"; "copied without provenance" ]
                  GitFixture.git host.Clone [ "update-ref"; target; copied ] |> ignore
                  refused (extend host files) "no construction commit with this plan's digest, cohort and group trailers"
                  Assert.equal copied (tip host)
                  // A different admitted plan (one question reworded) has another digest:
                  // the target carries the original construction, not this one.
                  GitFixture.git host.Clone [ "update-ref"; target; text applied.Json["candidateCommit"] ] |> ignore
                  let node = JsonNode.Parse(File.ReadAllText host.Plan)
                  let unresolved = node["operations"] :?> JsonArray |> Seq.find (fun op -> text op["op"] = "declareUnresolved")
                  unresolved["question"] <- JsonValue.Create(text unresolved["question"] + " Reworded.")
                  let other = Path.Combine(host.Parent, "other-plan.json")
                  File.WriteAllText(other, node.ToJsonString())
                  let otherDigest = text (construct host "preview" other |> ok).Json["binding"].["planDigest"]
                  Assert.isTrue (otherDigest <> text applied.Json["binding"].["planDigest"]) "the reworded plan kept its digest"
                  refused (extendWith host group other files "implementation" []) "target does not carry this exact construction"
                  Assert.equal (text applied.Json["candidateCommit"]) (tip host)))
          t "a wrong role, a wrong group, a revoked signer and a worker-execution request are refused" (fun () ->
              withHost true ignore (fun host ->
                  construct host "apply" host.Plan |> ok |> ignore
                  let before = tip host
                  let files = outbox host [ "Extensions.fs", handwritten ]
                  refused (extendWith host group host.Plan files "verification" []) "role verification may not submit handwritten extensions"
                  refused (extendWith host group host.Plan files "review" []) "role review may not submit"
                  refused (extendWith host group host.Plan files "root" []) "unknown role: root"
                  refused (extendWith host "GROUP-OTHER" host.Plan files "implementation" []) "construction group is missing"
                  for flag in [ "--worker"; "--command"; "--run" ] do
                      refused (extendWith host group host.Plan files "implementation" [ flag; "anything" ]) "autonomous custom-code worker execution is disabled"
                  Assert.equal 2 (extendWith host group host.Plan files "implementation" [ "--allow-anything"; "x" ]).ExitCode
                  PraxisCli.run host.Clone (Some coordinator) [ "local"; "operator"; "revoke"; "--host-config"; host.Config; "--key-id"; "op-1"; "--revision"; "rev-3" ] |> ok |> ignore
                  Assert.equal 3 (extend host files).ExitCode
                  Assert.equal before (tip host)
                  Assert.equal 0 (routes host).Length))
          t "a slot that would land beside the ECIR sources is refused at placement" (fun () ->
              withHost true ignore (fun host ->
                  reconfigure host timerSlots "ecir/timer"
                  construct host "apply" host.Plan |> ok |> ignore
                  let before = tip host
                  refusedEach host (extend host (outbox host [ "Extensions.fs", handwritten ])) [ "ecir/timer/Extensions.fs", "ECIR source document or its directory" ]
                  Assert.equal before (tip host))) ]

    let private baseTests =
        [ t "preview is deterministic, read-only and names every generated file, contract and open obligation" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let status = GitFixture.git host.Clone [ "status"; "--porcelain" ]
                  let first = construct host "preview" host.Plan |> ok
                  let second = construct host "preview" host.Plan |> ok
                  Assert.equal (first.Json.ToJsonString()) (second.Json.ToJsonString())
                  Assert.equal before (tip host)
                  Assert.equal status (GitFixture.git host.Clone [ "status"; "--porcelain" ])
                  Assert.isTrue (Directory.GetFileSystemEntries(host.Journal).Length = 0) "preview wrote to the construction journal"
                  Assert.equal "src/Domain/generated/Timer/" (text first.Json["ownershipRoot"])
                  let planned = writes first.Json
                  Assert.isTrue (planned |> List.forall (fun (kind, path) -> kind = "create" && path.StartsWith "src/Domain/generated/Timer/")) (sprintf "%A" planned)
                  Assert.isTrue (planned |> List.exists (fun (_, path) -> path.EndsWith "construction-manifest.json")) "manifest missing"
                  let obligations = first.Json["obligations"] :?> JsonArray |> Seq.toList
                  Assert.isTrue (not obligations.IsEmpty && obligations |> List.forall (fun o -> text o["status"] = "open")) "obligations not open"
                  Assert.isTrue (obligations |> List.exists (fun o -> text o["kind"] = "resolve")) "the unresolved short-stop conflict is not surfaced"
                  Assert.equal "COH-TIMER" (text first.Json["binding"].["cohortId"])
                  Assert.equal "rev-2" (text first.Json["binding"].["policyRevision"])))
          t "apply moves the target by one commit changing only planned generated files, then is idempotent" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let head = GitFixture.git host.Clone [ "rev-parse"; "HEAD" ]
                  let applied = construct host "apply" host.Plan |> ok
                  Assert.equal "applied" (text applied.Json["outcome"])
                  let candidate = text applied.Json["candidateCommit"]
                  Assert.equal candidate (tip host)
                  Assert.equal before (GitFixture.git host.Clone [ "rev-parse"; candidate + "^" ])
                  Assert.equal head (GitFixture.git host.Clone [ "rev-parse"; "HEAD" ])
                  let changed = GitFixture.git host.Clone [ "diff"; "--name-only"; before; candidate ] |> fun text -> text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
                  Assert.equal (writes applied.Json |> List.map snd |> Set.ofList) changed
                  // Exact bytes: the committed blob hashes to Ordo's artifact digest.
                  for write in applied.Json["writes"] :?> JsonArray do
                      let info = Diagnostics.ProcessStartInfo("git", [ "cat-file"; "blob"; candidate + ":" + text write["path"] ])
                      info.WorkingDirectory <- host.Clone
                      info.RedirectStandardOutput <- true
                      use git = Diagnostics.Process.Start info
                      use bytes = new MemoryStream()
                      git.StandardOutput.BaseStream.CopyTo bytes
                      git.WaitForExit()
                      let actual = "sha256:" + (SHA256.HashData(bytes.ToArray()) |> Convert.ToHexString).ToLowerInvariant()
                      Assert.equal (text write["sha256"]) actual
                  let message = GitFixture.git host.Clone [ "log"; "-1"; "--format=%B"; candidate ]
                  Assert.isTrue (message.Contains("Construction-Plan: " + text applied.Json["binding"].["planDigest"])) message
                  Assert.isTrue (message.Contains "Construction-Cohort: COH-TIMER") message
                  let again = construct host "apply" host.Plan |> ok
                  Assert.equal "already-current" (text again.Json["outcome"])
                  Assert.equal candidate (tip host)
                  Assert.isTrue (writes again.Json |> List.forall (fun (kind, _) -> kind = "unchanged")) (sprintf "%A" (writes again.Json))))
          t "an edited plan, a plan for another cohort and an injected operation are refused before any effect" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let edit (change: JsonNode -> unit) =
                      let node = JsonNode.Parse(File.ReadAllText host.Plan)
                      change node
                      let path = Path.Combine(host.Parent, "edited-" + Guid.NewGuid().ToString("N") + ".json")
                      File.WriteAllText(path, node.ToJsonString())
                      path
                  refused (construct host "apply" (edit (fun plan -> plan["binding"].["cohortId"] <- JsonValue.Create "COH-OTHER"))) "construction plan refused"
                  refused (construct host "apply" (edit (fun plan -> plan["binding"].["decisionIds"] <- JsonArray()))) "construction plan refused"
                  refused (construct host "apply" (edit (fun plan -> (plan["operations"] :?> JsonArray).Add(JsonNode.Parse """{"op":"writeFile","path":"../../.github/workflows/x.yml"}""")))) "construction plan refused"
                  Assert.equal before (tip host)))
          t "an omitted binding is stamped by the host from the approved cohort and yields the same plan digest" (fun () ->
              withHost true ignore (fun host ->
                  let unbound =
                      let node = JsonNode.Parse(File.ReadAllText host.Plan) :?> JsonObject
                      node.Remove "binding" |> ignore
                      let path = Path.Combine(host.Parent, "unbound.json")
                      File.WriteAllText(path, node.ToJsonString())
                      path
                  let bound = construct host "preview" host.Plan |> ok
                  let stamped = construct host "preview" unbound |> ok
                  Assert.equal (text bound.Json["binding"].["planDigest"]) (text stamped.Json["binding"].["planDigest"])
                  Assert.equal (writes bound.Json) (writes stamped.Json)
                  // The output carries the exact admitted plan and its canonical
                  // binding, distinct from the committed files' byte digests.
                  let fixtureBinding = (JsonNode.Parse(File.ReadAllText host.Plan))["binding"]
                  let reported = stamped.Json["binding"]
                  Assert.equal (text fixtureBinding["sourceManifestDigest"]) (text reported["planSourceManifestDigest"])
                  Assert.equal (text fixtureBinding["blueprintDigest"]) (text reported["planBlueprintDigest"])
                  Assert.isTrue (text reported["planBlueprintDigest"] <> text reported["blueprintDigest"]) "canonical and byte blueprint digests were conflated"
                  Assert.equal (text fixtureBinding["sourceManifestDigest"]) (text stamped.Json["plan"].["binding"].["sourceManifestDigest"])
                  let reportedPlan = Path.Combine(host.Parent, "reported-plan.json")
                  File.WriteAllText(reportedPlan, stamped.Json["plan"].ToJsonString())
                  let replayed = construct host "preview" reportedPlan |> ok
                  Assert.equal (text stamped.Json["binding"].["planDigest"]) (text replayed.Json["binding"].["planDigest"])
                  Assert.equal (writes stamped.Json) (writes replayed.Json)
                  // Stamping supplies only the binding: an unbound plan with an
                  // operation outside the vocabulary is still refused by Ordo.
                  let injected =
                      let node = JsonNode.Parse(File.ReadAllText unbound)
                      (node["operations"] :?> JsonArray).Add(JsonNode.Parse """{"op":"addPackage","name":"x"}""")
                      let path = Path.Combine(host.Parent, "unbound-injected.json")
                      File.WriteAllText(path, node.ToJsonString())
                      path
                  refused (construct host "apply" injected) "construction plan refused"))
          t "a handwritten file inside the generated root refuses the whole construction" (fun () ->
              withHost true (fun clone -> CliHarness.write clone "src/Domain/generated/Timer/Rules.fs" "module Rules // handwritten\n") (fun host ->
                  let before = tip host
                  refused (construct host "preview" host.Plan) "handwritten file inside the generated root"
                  refused (construct host "apply" host.Plan) "handwritten file inside the generated root"
                  Assert.equal before (tip host)))
          t "a linked ancestor of the generated root is refused, never followed" (fun () ->
              if not (OperatingSystem.IsWindows()) then
                  withHost true (fun clone ->
                      Directory.CreateDirectory(Path.Combine(clone, "outside")) |> ignore
                      CliHarness.write clone "outside/keep.txt" "kept\n"
                      File.CreateSymbolicLink(Path.Combine(clone, "src", "Domain", "generated"), "../../outside") |> ignore) (fun host ->
                      let before = tip host
                      refused (construct host "apply" host.Plan) "is a blob, not a directory"
                      Assert.equal before (tip host)))
          t "an apply interrupted after the target moved is reconciled, and one interrupted before it resumes the same candidate" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let applied = construct host "apply" host.Plan |> ok
                  let candidate = text applied.Json["candidateCommit"]
                  let entry = Directory.GetDirectories(host.Journal) |> Array.filter (fun path -> not (List.contains (Path.GetFileName path) [ "locks"; "plans"; "routes" ])) |> Array.exactlyOne
                  let applied = Directory.GetFiles(entry, "applied.json", SearchOption.AllDirectories) |> Array.exactlyOne
                  // Crash after update-ref, before the applied record.
                  File.Delete applied
                  let resumed = construct host "apply" host.Plan |> ok
                  Assert.equal "already-current" (text resumed.Json["outcome"])
                  Assert.isTrue (Directory.GetFiles(entry, "applied.json", SearchOption.AllDirectories).Length = 1) "the moved target was not reconciled"
                  // Crash after the journal entry, before update-ref.
                  File.Delete(Directory.GetFiles(entry, "applied.json", SearchOption.AllDirectories) |> Array.exactlyOne)
                  GitFixture.git host.Clone [ "update-ref"; target; before; candidate ] |> ignore
                  let replayed = construct host "apply" host.Plan |> ok
                  Assert.equal "applied" (text replayed.Json["outcome"])
                  Assert.equal candidate (text replayed.Json["candidateCommit"])
                  Assert.equal candidate (tip host)))
          t "policy, authority, validator and CI locations cannot be construction targets" (fun () ->
              withHost true ignore (fun host ->
                  let before = tip host
                  let hostRoot = Path.GetDirectoryName host.Config
                  for mount in [ ".ros/generated"; ".git"; "ros.json" ] do
                      File.WriteAllText(host.Config, configurationAt mount host.Clone hostRoot (onPath "git") true)
                      refused (construct host "apply" host.Plan) "constructionMount"
                  for mount in [ ".github/workflows"; ".claude" ] do
                      File.WriteAllText(host.Config, configurationAt mount host.Clone hostRoot (onPath "git") true)
                      refused (construct host "apply" host.Plan) "CI, agent or repository configuration"
                  Assert.equal before (tip host)))
          t "a revoked signer and a disabled construction root are refused" (fun () ->
              withHost false ignore (fun host ->
                  refused (construct host "apply" host.Plan) "construction is disabled")
              withHost true ignore (fun host ->
                  let before = tip host
                  PraxisCli.run host.Clone (Some coordinator) [ "local"; "operator"; "revoke"; "--host-config"; host.Config; "--key-id"; "op-1"; "--revision"; "rev-3" ] |> ok |> ignore
                  Assert.equal 3 (construct host "apply" host.Plan).ExitCode
                  Assert.equal before (tip host))) ]

    let tests = baseTests @ extensionTests
