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
    let private coordinator = agent "example/coordinator" "example" "coordinator" "coordinator-session"
    let private target = "refs/heads/construction-target"
    let private group = "GROUP-CONSTRUCT-001"
    let private fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "construction", "time-entry-timer")
    let private fixture name = File.ReadAllText(Path.Combine(fixtures, name))
    let private requirement = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L116-L129"

    let private onPath (name: string) =
        let file = if OperatingSystem.IsWindows() then name + ".exe" else name
        let selected = Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)
                       |> Array.map (fun path -> Path.Combine(path, file)) |> Array.find File.Exists |> Path.GetFullPath
        let info = FileInfo selected
        if info.LinkTarget <> null then info.ResolveLinkTarget(true).FullName else selected

    let private digest path = GitWorkspace.fileDigest path |> function Ok value -> value | Error error -> failwith error

    type private Host = { Parent: string; Clone: string; Config: string; Plan: string; Journal: string }

    let private echo () =
        let spec = LocalWorkerProcessTests.spec "echo" [ "unused" ]
        JsonNode.Parse(JsonSerializer.Serialize {| executable = spec.Executable; sha256 = spec.ExecutableDigest; arguments = spec.Arguments
                                                   timeoutMilliseconds = 60000; maxOutputBytes = 65536 |})

    let private configurationAt (mount: string) clone host git (construction: bool) =
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
        node["git"] <- JsonNode.Parse(JsonSerializer.Serialize {| executable = git; sha256 = digest git |})
        node["committer"] <- JsonNode.Parse """{"name":"Praxis controller","email":"controller@example.invalid"}"""
        node["validatorPolicyRevision"] <- JsonValue.Create "validators-1"
        node["systemValidators"] <- system
        node["acceptanceValidators"] <- JsonArray()
        node["workers"] <- JsonArray(workerNode)
        node["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes"}"""
        node.ToJsonString()

    let private configuration = configurationAt "src/Domain"

    let private withHost (construction: bool) (prepare: string -> unit) test =
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
            run [ "local"; "operator"; "grant-draft"; "--host-config"; config; "--key-id"; "op-1"; "--approver"; "operator"
                  "--group"; group; "--cohort"; "COH-TIMER"; "--source"; source; "--manifest"; "ecir/timer/source-manifest.json"
                  "--blueprint"; "ecir/timer/blueprint.json"; "--member"; "WI-TIMER=" + requirement
                  "--decision"; "DEC-TIMER-STATES"; "--draft"; draft; "--payload"; payload ] |> ok |> ignore
            File.WriteAllBytes(signature, key.SignData(File.ReadAllBytes payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            run [ "local"; "operator"; "grant"; "--host-config"; config; "--draft"; draft; "--signature"; signature; "--revision"; "rev-2" ] |> ok |> ignore
            let plan = Path.Combine(parent, "plan.json")
            File.WriteAllText(plan, fixture "plan.json")
            test { Parent = parent; Clone = clone; Config = config; Plan = plan; Journal = Path.Combine(host, "construction") }
        finally
            GitFixture.cleanup parent

    let private construct (host: Host) verb plan =
        PraxisCli.run host.Clone (Some coordinator) [ "construct"; verb; "--host-config"; host.Config; "--group"; group; "--plan"; plan; "--target"; target ]

    let private tip (host: Host) = GitFixture.git host.Clone [ "rev-parse"; target ]

    let private refused (result: PraxisCli.Result) (fragment: string) =
        Assert.equal 3 result.ExitCode
        Assert.isTrue (result.Error.Contains fragment) result.Error

    let private writes (document: JsonNode) =
        document["writes"] :?> JsonArray |> Seq.map (fun write -> text write["kind"], text write["path"]) |> Seq.toList

    let tests =
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
                  let entry = Directory.GetDirectories(host.Journal) |> Array.filter (fun path -> Path.GetFileName path <> "locks") |> Array.exactlyOne
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
