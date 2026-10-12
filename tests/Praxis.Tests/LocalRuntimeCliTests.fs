namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Work

/// `praxis local` through the real CLI: installed repository, native work
/// items/groups/executions, signed host policy and delegations, Git staging,
/// validator subprocesses, the worker bridge and native completion. The
/// worker's "agent command" is a deterministic fixture process and the
/// operator signature comes from an in-test key standing in for the
/// operator's own signing tool. This is not a live model-worker pilot.
[<RequireQualifiedAccess>]
module LocalRuntimeCliTests =
    open PraxisCli

    let private t name run = { Name = "local runtime cli: " + name; Run = run }
    let private coordinator = agent "example/coordinator" "example" "coordinator" "coordinator-session"
    let private target = "refs/heads/praxis-integration"
    let private groupNumber (id: string) = match id with "A" -> "001" | "B" -> "002" | _ -> "003"

    let private onPath (name: string) =
        let file = if OperatingSystem.IsWindows() then name + ".exe" else name
        let selected = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
                       |> Array.map (fun path -> Path.Combine(path, file)) |> Array.find File.Exists |> Path.GetFullPath
        let info = FileInfo selected
        if info.LinkTarget <> null then info.ResolveLinkTarget(true).FullName else selected

    let private digest path = GitWorkspace.fileDigest path |> function Ok value -> value | Error error -> failwith error

    let private texts (values: string list) = JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)

    /// A fixture process from this test assembly, pinned like any host tool.
    let private fixture mode (arguments: string list) =
        let spec = LocalWorkerProcessTests.spec mode arguments
        let command = JsonObject()
        command["executable"] <- JsonValue.Create spec.Executable
        command["sha256"] <- JsonValue.Create spec.ExecutableDigest
        command["arguments"] <- texts spec.Arguments
        command["timeoutMilliseconds"] <- JsonValue.Create 60000
        command["maxOutputBytes"] <- JsonValue.Create 65536
        let tests = Path.Combine(AppContext.BaseDirectory, "Praxis.Tests.dll")
        let dependencies = JsonObject()
        dependencies[tests] <- JsonValue.Create(digest tests)
        command["dependencies"] <- dependencies
        command

    let private validator id tests command =
        let node = JsonObject()
        node["id"] <- JsonValue.Create(id: string)
        node["tests"] <- JsonValue.Create(tests: bool)
        node["command"] <- command
        node

    /// The worker runs the real `praxis local worker-bridge`; only the agent
    /// command it drives is a fixture.
    let private worker (git: string) workerId (file: string) (content: string) =
        let dotnet = onPath "dotnet"
        let praxis = Path.Combine(AppContext.BaseDirectory, "praxis.dll")
        let agentCommand = LocalWorkerProcessTests.spec "agent-write" [ file; content ]
        let command = JsonObject()
        command["executable"] <- JsonValue.Create dotnet
        command["sha256"] <- JsonValue.Create(digest dotnet)
        command["arguments"] <-
            texts ([ praxis; "local"; "worker-bridge"; "--git"; git; "--agent-command"; agentCommand.Executable ]
                   @ (agentCommand.Arguments |> List.collect (fun value -> [ "--agent-arg"; value ])))
        command["timeoutMilliseconds"] <- JsonValue.Create 120000
        command["maxOutputBytes"] <- JsonValue.Create 65536
        let dependencies = JsonObject()
        dependencies[praxis] <- JsonValue.Create(digest praxis)
        command["dependencies"] <- dependencies
        let node = JsonObject()
        node["adapterId"] <- JsonValue.Create("bridge-" + workerId)
        node["workerId"] <- JsonValue.Create(workerId: string)
        node["roles"] <- texts [ "implementation" ]
        node["operatingSystems"] <- texts [ "linux"; "macos"; "windows" ]
        node["command"] <- command
        node

    type private Pilot =
        { Parent: string; Clone: string; Config: string; Key: ECDsa; Source: string; Repository: string }

    let private run (pilot: Pilot) arguments = PraxisCli.run pilot.Clone (Some coordinator) arguments

    let private sign (pilot: Pilot) (payload: string) (signature: string) =
        // Stand-in for the operator's own tool (for example openssl), which
        // emits DER; Praxis converts and verifies it against the policy.
        let bytes = pilot.Key.SignData(File.ReadAllBytes payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
        File.WriteAllBytes(signature, bytes)

    let private envelopes (pilot: Pilot) =
        ExecutionStore.list pilot.Clone |> List.choose (fun id -> ExecutionStore.loadEnvelope pilot.Clone id |> Result.toOption)

    let private execution pilot workItem =
        match envelopes pilot |> List.filter (fun envelope -> (envelope.WorkItem = workItem || envelope.WorkItem.EndsWith(":" + workItem)) && envelope.Parent.IsNone) with
        | [ envelope ] -> envelope.ExecutionId
        | other -> failwithf "expected one execution for %s, found %A among %A" workItem other (envelopes pilot |> List.map (fun e -> e.WorkItem, e.Actor.Id))

    let private state pilot (workItem: string) =
        let view = PraxisCli.run pilot.Clone None [ "work"; "context"; workItem ] |> ok
        text view.Json["workItems"].[0].["semanticState"]

    let private withPilot test =
        let parent = CliHarness.temporaryDirectory "local-runtime"
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        try
            let _, clone = installedRepository parent "repo"
            let repository = FileWorkConfigRepository.readRepositoryId clone
            let rosJson = Path.Combine(clone, "ros.json")
            let settings = JsonNode.Parse(File.ReadAllText rosJson)
            settings["workProtocol"].["workerChecklists"] <- JsonNode.Parse """{"delegatedTypes":["delegated-task"]}"""
            File.WriteAllText(rosJson, settings.ToJsonString())
            CliHarness.write clone "docs/spec.md" "R-A: src/a.txt contains alpha\nR-B: src/b.txt contains beta\nR-C: src/c.txt contains gamma\n"
            CliHarness.write clone "docs/manifest.json" "{\"source\":\"local runtime fixture\"}"
            CliHarness.write clone "docs/blueprint.json" "{\"cohorts\":[\"A\",\"B\",\"C\"]}"
            for id in [ "A"; "B"; "C" ] do
                PraxisCli.run clone (Some coordinator) [ "add"; "Deliver " + id; "--id"; "WI-" + id ] |> ok |> ignore
                PraxisCli.run clone (Some coordinator) [ "work"; "backlog-transition"; "--id"; "WI-" + id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
                PraxisCli.run clone (Some coordinator) [ "work"; "group"; "create"; "--group"; "GROUP-LOCAL-" + groupNumber id; "--member"; "WI-" + id; "--occurred-at"; now () ] |> ok |> ignore
            PraxisCli.run clone (Some coordinator) [ "work"; "begin"; "--id"; "WI-COORD"; "--occurred-at"; now () ] |> ok |> ignore
            let source = pushAll clone "local runtime source"
            let git = onPath "git"
            let host = Path.Combine(parent, "host")
            let roots = JsonObject()
            for name in [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence"; "checkouts" ] do
                let path = Path.Combine(host, name)
                Directory.CreateDirectory path |> ignore
                roots[name] <- JsonValue.Create path
            let configuration = JsonObject()
            configuration["schemaVersion"] <- JsonValue.Create "praxis.local-host/1"
            configuration["repositoryRoot"] <- JsonValue.Create(Path.GetFullPath clone)
            configuration["repositoryIdentity"] <- JsonValue.Create repository
            configuration["fileAccess"] <- JsonValue.Create "portable-development"
            configuration["roots"] <- roots
            configuration["git"] <- JsonNode.Parse(JsonSerializer.Serialize {| executable = git; sha256 = digest git |})
            configuration["committer"] <- JsonNode.Parse """{"name":"Praxis controller","email":"controller@example.invalid"}"""
            configuration["validatorPolicyRevision"] <- JsonValue.Create "validators-1"
            let system = JsonObject()
            system["build"] <- validator "host-build" false (fixture "echo" [ "{candidate}" ])
            system["tests"] <- validator "host-tests" true (fixture "validator-trx" [ "{trx}"; "pass" ])
            system["architecture"] <- validator "host-architecture" false (fixture "echo" [ "architecture" ])
            system["scope"] <- validator "host-scope" false (fixture "echo" [ "scope" ])
            configuration["systemValidators"] <- system
            configuration["acceptanceValidators"] <-
                JsonArray([| validator "accept-a" false (fixture "require-file" [ "src/a.txt"; "alpha" ]) :> JsonNode
                             validator "accept-b" false (fixture "require-file" [ "src/b.txt"; "beta" ])
                             validator "accept-c" false (fixture "require-file" [ "src/c.txt"; "gamma" ]) |])
            configuration["workers"] <-
                JsonArray([| worker git "worker-a" "src/a.txt" "alpha" :> JsonNode
                             worker git "worker-b" "src/b.txt" "beta"
                             // Exits zero with content the acceptance validator rejects.
                             worker git "worker-c" "src/c.txt" "not the requirement" |])
            configuration["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes","tests":"acceptance-artifacts"}"""
            let config = Path.Combine(host, "config.json")
            File.WriteAllText(config, configuration.ToJsonString())
            let publicKey = Path.Combine(parent, "operator-public.pem")
            File.WriteAllText(publicKey, key.ExportSubjectPublicKeyInfoPem())
            let pilot = { Parent = parent; Clone = clone; Config = config; Key = key; Source = source; Repository = repository }
            run pilot [ "local"; "operator"; "init"; "--host-config"; config; "--public-key"; publicKey; "--key-id"; "op-1"
                        "--approver"; "operator"; "--revision"; "rev-1"; "--declare-qualified-release" ] |> ok |> ignore
            for id, revision in [ "A", "rev-2"; "B", "rev-3"; "C", "rev-4" ] do
                let draft, payload, signature = Path.Combine(parent, id + "-grant.json"), Path.Combine(parent, id + "-grant.bin"), Path.Combine(parent, id + "-grant.sig")
                run pilot [ "local"; "operator"; "grant-draft"; "--host-config"; config; "--key-id"; "op-1"; "--approver"; "operator"
                            "--group"; "GROUP-LOCAL-" + groupNumber id; "--cohort"; "C-" + id; "--source"; source; "--manifest"; "docs/manifest.json"
                            "--blueprint"; "docs/blueprint.json"; "--member"; "WI-" + id + "=docs/spec.md#R-" + id
                            "--decision"; "DEC-" + id; "--draft"; draft; "--payload"; payload ] |> ok |> ignore
                sign pilot payload signature
                run pilot [ "local"; "operator"; "grant"; "--host-config"; config; "--draft"; draft; "--signature"; signature; "--revision"; revision ] |> ok |> ignore
                PraxisCli.run clone (Some coordinator) [ "plan"; "execute-group"; "GROUP-LOCAL-" + groupNumber id; "--occurred-at"; now (); "--member"; "WI-" + id; "--type"; "delegated-task" ] |> ok |> ignore
                // Every member has begun; this closes the coordinator's group execution.
                PraxisCli.run clone (Some coordinator) [ "plan"; "execute-group"; "GROUP-LOCAL-" + groupNumber id; "--occurred-at"; now () ] |> ok |> ignore
            for id in [ "a"; "b"; "c" ] do
                PraxisCli.run clone (Some(agent ("worker-" + id) "example" "worker" ("worker-" + id)))
                    [ "execution"; "start"; "--work-item"; "WI-COORD"; "--role"; "implementation"; "--baseline"; source
                      "--parent"; execution pilot "WI-COORD"; "--json" ] |> ok |> ignore
            GitFixture.git clone [ "update-ref"; target; source ] |> ignore
            test pilot
        finally
            GitFixture.cleanup parent

    let private child pilot workerId =
        match envelopes pilot |> List.filter (fun envelope -> envelope.Actor.Id = workerId && envelope.Parent.IsSome) with
        | [ envelope ] -> envelope.ExecutionId
        | other -> failwithf "expected one child execution for %s, found %A" workerId other

    /// Coordinator proposal, operator review/signature and delegation.
    let private delegated (pilot: Pilot) (id: string) (prerequisites: string list) =
        let lower = id.ToLowerInvariant()
        let packet, payload, signature, delegation =
            Path.Combine(pilot.Parent, id + ".packet.json"), Path.Combine(pilot.Parent, id + ".delegation.bin"),
            Path.Combine(pilot.Parent, id + ".delegation.sig"), Path.Combine(pilot.Parent, id + ".delegation.json")
        run pilot ([ "local"; "packet"; "propose"; "--host-config"; pilot.Config; "--group"; "GROUP-LOCAL-" + groupNumber id; "--worker"; "worker-" + lower
                     "--role"; "implementation"; "--dispatch"; "D-" + id; "--attempt"; "1"; "--parent"; execution pilot "WI-COORD"
                     "--child"; child pilot ("worker-" + lower); "--member"; "WI-" + id + "=" + execution pilot ("WI-" + id)
                     "--decision"; "DEC-" + id; "--allow"; "src/" + lower + ".txt"; "--acceptance"; "OBL-" + id + "=WI-" + id + ":accept-" + lower
                     "--timeout-seconds"; "300"; "--out"; packet ] @ (prerequisites |> List.collect (fun item -> [ "--prerequisite"; item ]))) |> ok |> ignore
        run pilot [ "local"; "operator"; "review"; "--host-config"; pilot.Config; "--packet"; packet; "--key-id"; "op-1"; "--approver"; "operator"; "--payload"; payload ] |> ok |> ignore
        sign pilot payload signature
        run pilot [ "local"; "operator"; "delegate"; "--host-config"; pilot.Config; "--packet"; packet; "--key-id"; "op-1"; "--approver"; "operator"
                    "--signature"; signature; "--out"; delegation ] |> ok |> ignore
        packet, delegation

    let private dispatch pilot (packet: string, delegation: string) =
        run pilot [ "local"; "run"; "--host-config"; pilot.Config; "--dispatch"; packet + "=" + delegation; "--target"; target; "--json" ]

    let private tip pilot = GitFixture.git pilot.Clone [ "rev-parse"; target ]

    let tests =
        [ t "coordinator delegates two workers whose independently checked candidates integrate and complete natively" (fun () ->
              withPilot (fun pilot ->
                  let first = dispatch pilot (delegated pilot "A" []) |> ok
                  let reported = (JsonNode.Parse first.Output).AsArray()[0]
                  Assert.equal "dispatched" (text reported["stage"])
                  let afterA = tip pilot
                  Assert.isTrue (afterA <> pilot.Source) "first candidate was not integrated"
                  Assert.equal "alpha" (GitFixture.git pilot.Clone [ "show"; afterA + ":src/a.txt" ])
                  Assert.equal "complete" (state pilot "WI-A")
                  Assert.equal Praxis.Domain.Execution.ExecutionState.Completed
                      (envelopes pilot |> List.find (fun e -> e.WorkItem.EndsWith "WI-A")).State
                  // The second worker started from the same source; its candidate
                  // is staged as a merge onto the advanced target and rechecked.
                  dispatch pilot (delegated pilot "B" [ "WI-A" ]) |> ok |> ignore
                  let afterB = tip pilot
                  Assert.equal [ afterA ] (GitFixture.git pilot.Clone [ "rev-list"; "--parents"; "-n"; "1"; afterB ] |> fun line -> line.Split(' ') |> Array.toList |> List.skip 1 |> List.take 1)
                  Assert.equal "alpha" (GitFixture.git pilot.Clone [ "show"; afterB + ":src/a.txt" ])
                  Assert.equal "beta" (GitFixture.git pilot.Clone [ "show"; afterB + ":src/b.txt" ])
                  Assert.equal "complete" (state pilot "WI-B")
                  // Each worker's child execution closes with the integrated candidate.
                  for workerId, candidate in [ "worker-a", afterA; "worker-b", afterB ] do
                      let closed = envelopes pilot |> List.find (fun e -> e.Actor.Id = workerId && e.Parent.IsSome)
                      Assert.equal Praxis.Domain.Execution.ExecutionState.Completed closed.State
                      Assert.equal (Some candidate) closed.CandidateRevision
                  // The controller's own working tree was never used for staging.
                  Assert.isTrue (not (File.Exists(Path.Combine(pilot.Clone, "src", "b.txt")))) "staging touched the controller checkout"))
          t "completion interrupted after integration and telemetry finalization resumes without a second update" (fun () ->
              withPilot (fun pilot ->
                  let edit (change: JsonNode -> unit) =
                      let node = JsonNode.Parse(File.ReadAllText pilot.Config)
                      change node
                      File.WriteAllText(pilot.Config, node.ToJsonString())
                  // Native completion requires implementation and tests evidence;
                  // this host maps only one, so completion refuses after integration.
                  edit (fun node -> node["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes"}""")
                  let delegation = delegated pilot "A" []
                  let refused = dispatch pilot delegation
                  Assert.equal 3 refused.ExitCode
                  let integrated = tip pilot
                  Assert.isTrue (integrated <> pilot.Source) "integration was not applied before the completion refusal"
                  Assert.equal "active" (state pilot "WI-A")
                  // The state a crash between telemetry finalization and native
                  // persistence leaves, produced by the same native writer.
                  FileTelemetryFinalizationRepository.finalizeWorkExecutions pilot.Clone "WI-A" |> function Ok() -> () | Error error -> failwith error
                  edit (fun node -> node["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes","tests":"acceptance-artifacts"}""")
                  let resumed = dispatch pilot delegation |> ok
                  Assert.equal "resumed" (text ((JsonNode.Parse resumed.Output).AsArray().[0].["stage"]))
                  Assert.equal integrated (tip pilot)
                  Assert.equal "complete" (state pilot "WI-A")
                  Assert.equal 1 (GitFixture.git pilot.Clone [ "reflog"; "show"; "--format=%H"; target ] |> fun log -> log.Split('\n') |> Array.filter ((=) integrated) |> Array.length)))
          t "a refusal after the worker exits resumes at acceptance without relaunching it" (fun () ->
              withPilot (fun pilot ->
                  let delegation = delegated pilot "A" []
                  GitFixture.git pilot.Clone [ "update-ref"; "-d"; target ] |> ignore
                  let refused = dispatch pilot delegation
                  Assert.equal 3 refused.ExitCode
                  let checkouts = Path.Combine(Path.GetDirectoryName pilot.Config, "checkouts")
                  let workers () = Directory.GetDirectories checkouts |> Array.filter (fun path -> Path.GetFileName(path).StartsWith "worker-") |> Array.length
                  Assert.equal 1 (workers ())
                  GitFixture.git pilot.Clone [ "update-ref"; target; pilot.Source ] |> ignore
                  let resumed = dispatch pilot delegation |> ok
                  Assert.equal "resumed" (text ((JsonNode.Parse resumed.Output).AsArray().[0].["stage"]))
                  Assert.equal 1 (workers ())
                  Assert.equal "complete" (state pilot "WI-A")))
          t "failed acceptance, forged delegation and replay refuse integration and completion" (fun () ->
              withPilot (fun pilot ->
                  let packet, delegation = delegated pilot "C" []
                  // A delegation signed by a key outside the protected policy.
                  use other = ECDsa.Create(ECCurve.NamedCurves.nistP256)
                  let forged = Path.Combine(pilot.Parent, "forged.delegation.json")
                  let original = JsonNode.Parse(File.ReadAllText delegation)
                  let payload = Path.Combine(pilot.Parent, "C.delegation.bin")
                  original["signature"] <- JsonValue.Create(Convert.ToBase64String(other.SignData(File.ReadAllBytes payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))
                  File.WriteAllText(forged, original.ToJsonString())
                  let refusedForged = dispatch pilot (packet, forged)
                  Assert.equal 3 refusedForged.ExitCode
                  let status = run pilot [ "local"; "status"; "--host-config"; pilot.Config; "--dispatch"; "D-C" ]
                  Assert.equal 3 status.ExitCode
                  // The worker exits zero, but its content fails independent acceptance.
                  let failed = dispatch pilot (packet, delegation)
                  Assert.equal 3 failed.ExitCode
                  Assert.isTrue (failed.Error.Contains "acceptance") failed.Error
                  Assert.equal pilot.Source (tip pilot)
                  Assert.equal "active" (state pilot "WI-C")
                  // Delegated work cannot bypass its checklist through standalone completion.
                  let standalone = run pilot [ "work"; "complete"; "--id"; "WI-C"; "--occurred-at"; now (); "--evidence"; "implementation=docs/spec.md"; "--evidence"; "tests=docs/spec.md" ]
                  Assert.isTrue (standalone.ExitCode <> 0 && standalone.Error.Contains "praxis local run") standalone.Error
                  Assert.equal "active" (state pilot "WI-C")
                  // Re-running resumes acceptance of the archived exit; it never relaunches.
                  let again = dispatch pilot (packet, delegation)
                  Assert.equal 3 again.ExitCode
                  Assert.equal pilot.Source (tip pilot)
                  let journal = run pilot [ "local"; "status"; "--host-config"; pilot.Config; "--dispatch"; "D-C" ] |> ok
                  Assert.isTrue (journal.Output.Contains "Exited") journal.Output
                  Assert.isTrue (journal.Output.Contains "integration: not prepared") journal.Output)) ]
