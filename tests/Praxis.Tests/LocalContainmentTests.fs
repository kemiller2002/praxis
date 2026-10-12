namespace Praxis.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Work
open Praxis.Contracts.Execution
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// Host containment qualification (#231/#235 phase 6). Pure tests run on
/// every OS. Real tests launch actual probes and adversarial workers through
/// the confined adapter where the mechanism is expected to work, and assert
/// refusal (unqualified) everywhere else; they never pass by skipping.
/// `PRAXIS_TEST_CONTAINMENT=required|unavailable` pins the expectation (CI
/// pins macOS/Windows to unavailable); otherwise (`auto`) Linux decides with an
/// independent `unshare` precheck that does not use the adapter under test.
[<RequireQualifiedAccess>]
module LocalContainmentTests =
    let private t name run = { Name = "local containment: " + name; Run = run }

    type private Expectation = Required | Unavailable

    let private expectation =
        lazy (
            let decided, reason =
                match Environment.GetEnvironmentVariable "PRAXIS_TEST_CONTAINMENT" with
                | "required" -> Required, "PRAXIS_TEST_CONTAINMENT=required"
                | "unavailable" -> Unavailable, "PRAXIS_TEST_CONTAINMENT=unavailable"
                | _ when not (OperatingSystem.IsLinux()) -> Unavailable, "no containment mechanism on this OS"
                | _ ->
                    let probe =
                        try CliHarness.run "unshare" [ "--user"; "--map-root-user"; "--mount"; "--net"; "--pid"; "--fork"; "--"; "/bin/true" ] []
                        with e -> { Exit = -1; Out = ""; Err = e.Message }
                    if probe.Exit = 0 then Required, "unshare precheck succeeded"
                    else Unavailable, "unshare precheck failed: " + probe.Err.Trim()
            printfn "  containment expectation on this host: %A (%s)" decided reason
            decided)

    let private digest (path: string) =
        use input = File.OpenRead path
        "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    let private settings : LinuxContainmentSettings =
        { ReadOnlyPaths = LocalHostConfiguration.defaultReadOnlyPaths
          EnvironmentAllowlist = Set.ofList [ "PATH"; "LANG"; "HOME"; "TMPDIR" ]
          MaxProcesses = 256; MaxOpenFiles = 1024; MaxFileBytes = 1073741824L; ScratchMegabytes = 64 }

    /// Disposable host roots: repository, checkouts and the named host roots.
    let private host () =
        let root = CliHarness.temporaryDirectory "praxis-containment"
        let make name = let path = Path.Combine(root, name) in Directory.CreateDirectory path |> ignore; path
        let paths : ContainmentHostPaths =
            { RepositoryRoot = make "repository"; CheckoutRoot = make "checkouts"
              HostRoots = [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence" ] |> List.map (fun n -> n, make n)
              Extra = [ make "config" ] }
        root, paths

    /// Synthetic POSIX host paths for the pure adapter checks on every OS.
    let private posix : ContainmentHostPaths =
        { RepositoryRoot = "/srv/praxis/repository"; CheckoutRoot = "/srv/praxis/checkouts"
          HostRoots = [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence" ] |> List.map (fun n -> n, "/srv/praxis/" + n)
          Extra = [ "/srv/praxis/config" ] }

    let private executable = Environment.ProcessPath

    let private tool name = { Name = name; Path = "/usr/bin/" + name; Digest = "sha256:" + String('a', 64) }
    let private tools : LinuxContainmentTools =
        { Unshare = tool "unshare"; Shell = tool "sh"; Prlimit = tool "prlimit"; Setpriv = tool "setpriv"
          Support = LinuxContainment.supportTools |> List.map tool }

    let private request (paths: ContainmentHostPaths) : ContainedLaunchRequest =
        { Stage = paths.CheckoutRoot + "/.praxis-stage-x"; WorkerRoot = paths.CheckoutRoot
          Sealed = [ paths.RepositoryRoot; paths.CheckoutRoot ] @ (paths.HostRoots |> List.map snd) @ paths.Extra @ LinuxContainment.systemSealed @ [ "/root/.ssh" ]
          Protected = LinuxContainment.systemProtected }

    let private spec work environment : LocalWorkerProcessSpec =
        { Executable = "/usr/bin/bash"; ExecutableDigest = "sha256:" + String('b', 64); Arguments = [ "-c"; "true" ]
          WorkingDirectory = work; Environment = environment; Input = ""; TimeoutMilliseconds = 10000; MaxOutputBytes = 4096 }

    let private probe id outcome = { Id = id; Dimension = (if id.StartsWith "net." then "network" else "filesystem"); Expected = "denied"; Observed = "x"; Outcome = outcome; Detail = "" }

    let private qualification probes =
        { Host = LinuxContainment.Host; Profile = LinuxContainment.Profile; ProfileDigest = "sha256:" + String('c', 64)
          Mechanism = LinuxContainment.Mechanism; OperatingSystem = "linux"; Kernel = "6.0"; Architecture = "x64"
          Tools = LinuxContainment.allTools tools; Probes = probes; Unsupported = [ "seccomp" ]; QualifiedAt = DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero) }

    let private passing =
        ContainmentProfile.dimensions |> List.map (fun d -> { probe ("p." + d) ContainmentProbeOutcome.Pass with Dimension = d }) |> qualification

    let private facts (q: ContainmentQualification) : ContainmentHostFacts =
        { OperatingSystem = "linux"; Kernel = q.Kernel; Architecture = q.Architecture; ProfileDigest = q.ProfileDigest; Tools = q.Tools }

    let private status dimension q = LocalContainment.restrictions q |> List.find (fun r -> r.Dimension = dimension) |> _.Status

    let private observations : ContainmentProbeObservations =
        { Reports = Map.empty; Completed = false; Output = ""; ChangedTargets = Set.empty; InsideWritten = false; LeakedSecrets = Set.empty
          LoopbackAccepted = false; SentinelAlive = true; NonAllowlistedRefused = true; AllowedEnvironment = settings.EnvironmentAllowlist; Limits = 256, 1024 }

    let private sentinel name secret =
        let info = ProcessStartInfo("/bin/bash", [ "-c"; "exec -a \"$0\" sleep 300"; name ])
        info.UseShellExecute <- false
        info.Environment["PRAXIS_SENTINEL_SECRET"] <- secret
        Process.Start info

    /// An adversarial worker: tampers with the policy, reads a planted
    /// credential directly and through a link, opens a socket to a host
    /// listener, kills a host process and escapes through a symlink to a
    /// directory outside. It also writes its own output as a positive control.
    let private adversary =
        String.concat
            "\n"
            [ "policy=$1; cred=$2; port=$3; spid=$4; outside=$5"
              "printf 'tamper\\n' >> \"$policy\" 2>/dev/null && echo POLICY-WRITTEN"
              "cat \"$cred\" 2>/dev/null"
              "(exec 3<>\"/dev/tcp/127.0.0.1/$port\") 2>/dev/null && echo SOCKET-OPEN"
              "kill -KILL \"$spid\" 2>/dev/null && echo KILLED"
              "ln -s \"$outside\" escape && printf x > escape/escaped 2>/dev/null && echo ESCAPED"
              "ln -s \"$cred\" cred-link && cat cred-link 2>/dev/null"
              "printf done > worker-output" ]

    let tests =
        [ t "probe judgement keeps unknown distinct from failure" (fun () ->
              let unreported = LocalContainmentQualification.judge observations
              let outcome id (results: ContainmentProbeResult list) = results |> List.find (fun p -> p.Id = id) |> _.Outcome
              Assert.equal ContainmentProbeOutcome.Unknown (outcome "fs.write-policy" unreported)
              Assert.equal ContainmentProbeOutcome.Unknown (outcome "net.loopback-host" unreported)
              Assert.isTrue (unreported |> List.forall (fun p -> p.Outcome <> ContainmentProbeOutcome.Pass || p.Id = "env.non-allowlisted-refused")) "an unreported probe passed"
              let violated =
                  LocalContainmentQualification.judge
                      { observations with ChangedTargets = Set.ofList [ "fs.write-policy" ]; LeakedSecrets = Set.ofList [ "cred.read-signer" ]
                                          LoopbackAccepted = true; SentinelAlive = false }
              for id in [ "fs.write-policy"; "cred.read-signer"; "net.loopback-host"; "proc.signal-host" ] do
                  Assert.equal ContainmentProbeOutcome.Fail (outcome id violated)
              let claimed =
                  LocalContainmentQualification.judge
                      { observations with Completed = true; Reports = Map.ofList [ "fs.write-inside", "allowed"; "cred.read-policy", "denied" ]; InsideWritten = false }
              Assert.equal ContainmentProbeOutcome.Fail (outcome "fs.write-inside" claimed)
              Assert.equal ContainmentProbeOutcome.Pass (outcome "cred.read-policy" claimed))

          t "restrictions derive enforced, unrestricted, unknown and unavailable from probes" (fun () ->
              Assert.isTrue (LocalContainment.isQualified passing) "all dimensions should be enforced"
              let mixed =
                  qualification
                      [ probe "fs.a" ContainmentProbeOutcome.Pass; probe "fs.b" ContainmentProbeOutcome.Unknown
                        probe "net.a" ContainmentProbeOutcome.Pass; probe "net.b" ContainmentProbeOutcome.Fail ]
              Assert.equal RestrictionStatus.Unknown (status "filesystem" mixed)
              Assert.equal RestrictionStatus.Unrestricted (status "network" mixed)
              Assert.equal RestrictionStatus.Unknown (status "process" mixed)
              Assert.isTrue (not (LocalContainment.isQualified mixed)) "mixed qualification must not qualify"
              let none = LocalContainment.unsupported LinuxContainment.Host "macos" "k" "arm64" [ "no mechanism" ] DateTimeOffset.UtcNow
              Assert.isTrue (ContainmentProfile.dimensions |> List.forall (fun d -> status d none = RestrictionStatus.Unavailable)) "unsupported must be unavailable"
              Assert.isTrue (not (LocalContainment.isQualified none)) "unsupported must not qualify")

          t "evidence round-trips and satisfies the existing praxis.containment-evidence/1 reader" (fun () ->
              let json = LocalContainmentJson.render passing
              match LocalContainmentJson.read json with
              | Ok read -> Assert.equal passing read
              | Error error -> failwith error
              match ExecutionJson.readContainmentEvidence (JsonNode.Parse json) with
              | Ok profile ->
                  Assert.equal (Some LinuxContainment.Host) profile.Source
                  Assert.equal ContainmentProfile.dimensions (ContainmentProfile.enforced profile)
              | Error error -> failwith error
              // A probe flipped to fail without changing the stated restrictions is refused.
              let tampered = json.Replace("\"result\": \"pass\"", "\"result\": \"fail\"")
              Assert.isTrue (Result.isError (LocalContainmentJson.read tampered)) "tampered evidence was accepted")

          t "launch gate refuses drift, staleness, shortfall and unsupported platforms" (fun () ->
              let now = passing.QualifiedAt.AddHours 1.0
              let day = TimeSpan.FromDays 1.0
              Assert.equal (Ok()) (LinuxContainment.admit (facts passing) now day passing)
              let refused f (q: ContainmentQualification) at expected =
                  match LinuxContainment.admit f at day q with
                  | Error reason when reason.Contains(expected: string) -> ()
                  | other -> failwithf "expected refusal containing '%s', got %A" expected other
              refused { facts passing with Kernel = "7.0" } passing now "kernel changed"
              refused { facts passing with ProfileDigest = "sha256:" + String('d', 64) } passing now "profile"
              refused { facts passing with Tools = [ tool "unshare" ] } passing now "tool closure"
              refused { facts passing with OperatingSystem = "macos" } passing now "unsupported and unqualified on macos"
              refused { facts passing with OperatingSystem = "windows" } passing now "unsupported and unqualified on windows"
              refused (facts passing) passing (now.AddDays 2.0) "stale"
              let shortfall = { passing with Probes = passing.Probes |> List.map (fun p -> if p.Dimension = "network" then { p with Outcome = ContainmentProbeOutcome.Unknown } else p) }
              refused (facts shortfall) shortfall now "network is unknown")

          t "adapter refuses unsafe binds, environment, executables and worker paths" (fun () ->
              let paths = posix
              do
                  let work = paths.CheckoutRoot + "/worker"
                  let ok = LinuxContainment.wrap settings tools (request paths) (spec work (Map.ofList [ "PATH", "/usr/bin" ]))
                  match ok with
                  | Ok confined ->
                      Assert.equal tools.Unshare.Path confined.Executable
                      Assert.equal tools.Unshare.Digest confined.ExecutableDigest
                      for flag in [ "--user"; "--net"; "--mount"; "--pid"; "--ipc"; "--uts"; "--kill-child"; "--no-new-privs"; "--bounding-set=-all"; "--core=0"; LinuxContainment.setupScript ] do
                          Assert.isTrue (List.contains flag confined.Arguments) ("missing confined argument " + flag)
                      Assert.equal [ "/usr/bin/bash"; "-c"; "true" ] (confined.Arguments |> List.skip (confined.Arguments.Length - 3))
                  | Error error -> failwith error
                  let refuses (s: LinuxContainmentSettings) (launch: LocalWorkerProcessSpec) expected =
                      match LinuxContainment.wrap s tools (request paths) launch with
                      | Error reason when reason.Contains(expected: string) -> ()
                      | other -> failwithf "expected refusal containing '%s', got %A" expected other
                  let policy = paths.HostRoots |> List.find (fun (n, _) -> n = "policy") |> snd
                  refuses { settings with ReadOnlyPaths = [ "/" ] } (spec work Map.empty) "read-only path / overlaps sealed host path"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/etc" ] } (spec work Map.empty) "read-only path /etc overlaps sealed host path /etc/shadow"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/root" ] } (spec work Map.empty) "overlaps sealed host path /root/.ssh"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/home" ] } (spec work Map.empty) "exposes protected path /home"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/etc/shadow" ] } (spec work Map.empty) "sealed host path /etc/shadow"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/root/.ssh/id_ed25519" ] } (spec work Map.empty) "sealed host path /root/.ssh"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; policy ] } (spec work Map.empty) "sealed host path"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; paths.RepositoryRoot + "/src" ] } (spec work Map.empty) "sealed host path"
                  refuses { settings with ReadOnlyPaths = [ "/usr"; "/usr/../root" ] } (spec work Map.empty) "not canonical"
                  refuses settings (spec work (Map.ofList [ "GITHUB_TOKEN", "secret" ])) "outside the containment allowlist"
                  refuses settings { spec work Map.empty with Executable = paths.CheckoutRoot + "/worker/tool" } "worker executable must be inside a read-only path"
                  refuses settings (spec policy Map.empty) "inside the host checkout root"
                  refuses settings (spec paths.CheckoutRoot Map.empty) "inside the host checkout root"
                  refuses settings (spec "/usr/lib/worker" Map.empty) "inside the host checkout root")

          t "host configuration requires a containment profile for a contained worker" (fun () ->
              let root, paths = host ()
              try
                  let q (value: string) = Text.Json.JsonSerializer.Serialize value
                  let bytes (containment: string) (workerContainment: string) =
                      let roots = paths.HostRoots |> List.map (fun (n, p) -> q n + ":" + q p) |> String.concat ","
                      let pin arguments = "{\"executable\":" + q executable + ",\"sha256\":\"sha256:" + String('a', 64) + "\",\"arguments\":" + arguments + ",\"timeoutMilliseconds\":1000,\"maxOutputBytes\":1000}"
                      let command = pin "[\"{candidate}\"]"
                      let tests = pin "[\"{candidate}\",\"{trx}\"]"
                      let worker = pin "[\"{worktree}\"]"
                      ("{\"schemaVersion\":\"praxis.local-host/1\",\"repositoryRoot\":" + q paths.RepositoryRoot + ",\"repositoryIdentity\":\"r\",\"fileAccess\":\"portable-development\","
                       + "\"roots\":{" + roots + ",\"checkouts\":" + q paths.CheckoutRoot + "},\"git\":{\"executable\":" + q executable + ",\"sha256\":\"sha256:" + String('a', 64) + "\"},"
                       + "\"committer\":{\"name\":\"n\",\"email\":\"e@x\"},\"validatorPolicyRevision\":\"v\",\"systemValidators\":{\"build\":{\"id\":\"b\",\"command\":" + command + "},"
                       + "\"tests\":{\"id\":\"t\",\"tests\":true,\"command\":" + tests + "},\"architecture\":{\"id\":\"a\",\"command\":" + command + "},\"scope\":{\"id\":\"s\",\"command\":" + command + "}},"
                       + "\"acceptanceValidators\":[],\"workers\":[{\"adapterId\":\"a\",\"workerId\":\"w\",\"roles\":[\"implementation\"],\"operatingSystems\":[\"linux\",\"macos\",\"windows\"]"
                       + workerContainment + ",\"command\":" + worker + "}],\"completionEvidence\":{\"implementation\":\"member-changes\"}" + containment + "}")
                      |> Text.Encoding.UTF8.GetBytes
                  let evidence = Path.Combine(root, "config", "containment.json")
                  let profile = ",\"containment\":{\"evidence\":" + q evidence + ",\"maxProcesses\":128}"
                  match LocalHostConfiguration.parse (Path.Combine(root, "config", "host.json")) (bytes "" ",\"containment\":\"required\"") with
                  | Error reason -> Assert.isTrue (reason.Contains "no containment profile") reason
                  | Ok _ -> failwith "a contained worker without a profile was accepted"
                  Assert.isTrue (Result.isError (LocalHostConfiguration.parse "h.json" (bytes profile ",\"containment\":\"maybe\""))) "unknown requirement accepted"
                  match LocalHostConfiguration.parse (Path.Combine(root, "config", "host.json")) (bytes profile ",\"containment\":\"required\"") with
                  | Ok configuration ->
                      let worker = Assert.single configuration.Workers
                      Assert.isTrue worker.RequiresContainment "worker should require containment"
                      let containment = configuration.Containment.Value
                      Assert.equal 128 containment.Settings.MaxProcesses
                      Assert.equal LocalHostConfiguration.defaultReadOnlyPaths containment.Settings.ReadOnlyPaths
                      // No evidence yet: the launch gate refuses on every OS.
                      match LocalHostContainment.admit configuration worker DateTimeOffset.UtcNow with
                      | Error reason -> Assert.isTrue (reason.Contains "requires containment; launch refused") reason
                      | Ok _ -> failwith "an unqualified contained worker was admitted"
                      Assert.equal (Ok None) (LocalHostContainment.admit configuration { worker with RequiresContainment = false } DateTimeOffset.UtcNow |> Result.map (Option.map ignore))
                  | Error reason -> failwith reason
              finally Directory.Delete(root, true))

          t "real probes qualify where the mechanism works and refuse everywhere else" (fun () ->
              let root, paths = host ()
              try
                  let record, failure = LocalContainmentQualification.qualify settings paths (Path.Combine(root, "config")) (fun () -> DateTimeOffset.UtcNow)
                  let evidence = Path.Combine(root, "config", "containment.json")
                  match LinuxContainmentHost.writeEvidence evidence record with
                  | Error error -> failwith error
                  | Ok _ -> ()
                  let admission = LocalContainmentQualification.admit settings evidence (TimeSpan.FromMinutes 10.0) DateTimeOffset.UtcNow
                  match expectation.Value with
                  | Required ->
                      let failed = record.Probes |> List.filter (fun p -> p.Outcome <> ContainmentProbeOutcome.Pass)
                      Assert.isTrue failed.IsEmpty (sprintf "probes did not pass (%A): %A" failure failed)
                      Assert.equal LocalContainmentQualification.probeIds (record.Probes |> List.map _.Id)
                      Assert.isTrue (LocalContainment.isQualified record) "qualification should be complete"
                      Assert.isTrue (Result.isOk admission) (sprintf "qualified host was refused: %A" admission)
                      // The probes left nothing behind in the host roots.
                      for _, directory in paths.HostRoots do Assert.empty (Directory.GetFileSystemEntries directory |> Array.toList)
                      Assert.empty (Directory.GetFileSystemEntries paths.CheckoutRoot |> Array.toList)
                  | Unavailable ->
                      Assert.isTrue (not (LocalContainment.isQualified record)) "an unavailable host must not qualify"
                      Assert.isTrue (record.Probes |> List.forall (fun p -> p.Outcome <> ContainmentProbeOutcome.Pass || p.Id = "env.non-allowlisted-refused")) "a probe passed on an unavailable host"
                      match admission with
                      | Error reason -> Assert.isTrue (reason.Length > 0) "refusal needs a reason"
                      | Ok _ -> failwith "an unavailable host admitted a contained launch"
                      if not (OperatingSystem.IsLinux()) then
                          Assert.isTrue (ContainmentProfile.dimensions |> List.forall (fun d -> status d record = RestrictionStatus.Unavailable)) "non-Linux must report unavailable"
              finally Directory.Delete(root, true))

          t "adversarial worker cannot write policy, read credentials, open sockets, kill host processes or escape by symlink" (fun () ->
              let root, paths = host ()
              let secret = "praxis-planted-" + Guid.NewGuid().ToString("N")
              let policyRoot = paths.HostRoots |> List.find (fun (n, _) -> n = "policy") |> snd
              let policy = Path.Combine(policyRoot, "policy.json")
              let credential = Path.Combine(paths.HostRoots |> List.find (fun (n, _) -> n = "evidence") |> snd, "signer.key")
              let outside = Path.Combine(root, "config")
              let work = Path.Combine(paths.CheckoutRoot, "worker")
              File.WriteAllText(policy, "{\"policy\":1}")
              File.WriteAllText(credential, secret)
              Directory.CreateDirectory work |> ignore
              let listener = new TcpListener(IPAddress.Loopback, 0)
              listener.Start()
              let accept = listener.AcceptTcpClientAsync()
              let name = "praxis-adversary-sentinel-" + Guid.NewGuid().ToString("N").Substring(0, 8)
              let target = if OperatingSystem.IsWindows() then None else Some(sentinel name secret)
              try
                  let launch : LocalWorkerProcessSpec =
                      { Executable = "/usr/bin/bash"; ExecutableDigest = (if File.Exists "/usr/bin/bash" then digest "/usr/bin/bash" else "")
                        Arguments = [ "-c"; adversary; "adversary"; policy; credential; string (listener.LocalEndpoint :?> IPEndPoint).Port
                                      string (target |> Option.map _.Id |> Option.defaultValue 0); outside ]
                        WorkingDirectory = work; Environment = Map.ofList [ "PATH", "/usr/bin:/bin" ]; Input = ""
                        TimeoutMilliseconds = 30000; MaxOutputBytes = 65536 }
                  let gate =
                      LinuxContainmentHost.resolveTools ()
                      |> Result.bind (fun resolved ->
                          LinuxContainmentHost.createStage paths.CheckoutRoot
                          |> Result.bind (fun stage -> LinuxContainmentHost.confine settings resolved paths stage launch))
                  match expectation.Value, gate with
                  | Required, Error error -> failwith ("confinement refused where it is required: " + error)
                  | Unavailable, Error _ -> () // unsupported platform or missing tools: refused before launch
                  | Unavailable, Ok confined ->
                      // Tools exist but the mechanism does not work: the launch must not run the worker.
                      let observed = (LocalWorkerProcess.run confined (fun _ -> Ok()) CancellationToken.None).GetAwaiter().GetResult()
                      Assert.isTrue (observed.Outcome <> LocalWorkerProcessOutcome.Exited 0 || not (File.Exists(Path.Combine(work, "worker-output")))) "worker ran unconfined"
                  | Required, Ok confined ->
                      let observed = (LocalWorkerProcess.run confined (fun _ -> Ok()) CancellationToken.None).GetAwaiter().GetResult()
                      let output = observed.StandardOutput + observed.StandardError
                      Assert.equal (LocalWorkerProcessOutcome.Exited 0) observed.Outcome
                      Assert.equal "done" (File.ReadAllText(Path.Combine(work, "worker-output")))
                      Assert.equal "{\"policy\":1}" (File.ReadAllText policy)
                      Assert.isTrue (not (output.Contains secret)) "planted credential leaked"
                      Assert.isTrue (not (accept.Wait 300)) "host listener accepted a connection"
                      Assert.isTrue (target |> Option.forall (fun p -> not p.HasExited)) "host process was killed"
                      Assert.isTrue (not (File.Exists(Path.Combine(outside, "escaped")))) "symlink escape wrote outside"
                      for marker in [ "POLICY-WRITTEN"; "SOCKET-OPEN"; "KILLED"; "ESCAPED" ] do
                          Assert.isTrue (not (output.Contains marker)) ("adversary reported " + marker)
              finally
                  listener.Stop()
                  target |> Option.iter (fun p -> (try p.Kill true with _ -> ()); p.Dispose())
                  Directory.Delete(root, true))

          t "cli qualify and status follow the probes" (fun () ->
              let root, paths = host ()
              try
                  let evidence = Path.Combine(root, "config", "containment.json")
                  let gitPath = executable
                  let configuration = JsonObject()
                  let pinned (args: string list) =
                      let command = JsonObject()
                      command["executable"] <- JsonValue.Create gitPath
                      command["sha256"] <- JsonValue.Create("sha256:" + String('a', 64))
                      command["arguments"] <- JsonArray(args |> List.map (fun a -> JsonValue.Create a :> JsonNode) |> List.toArray)
                      command["timeoutMilliseconds"] <- JsonValue.Create 1000
                      command["maxOutputBytes"] <- JsonValue.Create 1000
                      command
                  let validator id tests =
                      let node = JsonObject()
                      node["id"] <- JsonValue.Create(id: string)
                      if tests then node["tests"] <- JsonValue.Create true
                      node["command"] <- pinned (if tests then [ "{candidate}"; "{trx}" ] else [ "{candidate}" ])
                      node
                  configuration["schemaVersion"] <- JsonValue.Create "praxis.local-host/1"
                  configuration["repositoryRoot"] <- JsonValue.Create paths.RepositoryRoot
                  configuration["repositoryIdentity"] <- JsonValue.Create "r"
                  configuration["fileAccess"] <- JsonValue.Create "portable-development"
                  let roots = JsonObject()
                  for name, path in paths.HostRoots do roots[name] <- JsonValue.Create path
                  roots["checkouts"] <- JsonValue.Create paths.CheckoutRoot
                  configuration["roots"] <- roots
                  configuration["git"] <- JsonObject(dict [ "executable", JsonValue.Create gitPath :> JsonNode; "sha256", JsonValue.Create("sha256:" + String('a', 64)) ])
                  configuration["committer"] <- JsonObject(dict [ "name", JsonValue.Create "n" :> JsonNode; "email", JsonValue.Create "e@x" ])
                  configuration["validatorPolicyRevision"] <- JsonValue.Create "v"
                  configuration["systemValidators"] <-
                      JsonObject(dict [ "build", validator "b" false :> JsonNode; "tests", validator "t" true; "architecture", validator "a" false; "scope", validator "s" false ])
                  configuration["acceptanceValidators"] <- JsonArray()
                  let worker = JsonObject()
                  worker["adapterId"] <- JsonValue.Create "a"
                  worker["workerId"] <- JsonValue.Create "w"
                  worker["roles"] <- JsonArray(JsonValue.Create "implementation")
                  worker["operatingSystems"] <- JsonArray(JsonValue.Create "linux")
                  worker["containment"] <- JsonValue.Create "required"
                  worker["command"] <- pinned [ "{worktree}" ]
                  configuration["workers"] <- JsonArray(worker)
                  configuration["completionEvidence"] <- JsonObject(dict [ "implementation", JsonValue.Create "member-changes" :> JsonNode ])
                  configuration["containment"] <- JsonObject(dict [ "evidence", JsonValue.Create evidence :> JsonNode ])
                  let file = Path.Combine(root, "config", "host.json")
                  File.WriteAllText(file, configuration.ToJsonString())
                  let praxis args = CliHarness.rosWith paths.RepositoryRoot [] ([ "local"; "containment" ] @ args @ [ "--host-config"; file ])
                  let before = praxis [ "status"; "--json" ]
                  Assert.equal 3 before.Exit
                  let qualified = praxis [ "qualify"; "--json" ]
                  let record = JsonNode.Parse qualified.Out
                  Assert.equal "praxis.containment-evidence/1" (record["schema"].GetValue<string>())
                  Assert.isTrue (File.Exists evidence) "qualify must write the evidence file"
                  let after = praxis [ "status"; "--json" ]
                  match expectation.Value with
                  | Required ->
                      Assert.equal 0 qualified.Exit
                      let flag = record["qualified"].GetValue<bool>()
                      Assert.isTrue flag "qualified flag"
                      Assert.equal 0 after.Exit
                      let status = JsonNode.Parse after.Out
                      let admitted = status["admitted"].GetValue<bool>()
                      Assert.isTrue admitted "status should admit"
                  | Unavailable ->
                      Assert.equal 3 qualified.Exit
                      let flag = record["qualified"].GetValue<bool>()
                      Assert.isTrue (not flag) "unavailable host reported qualified"
                      Assert.equal 3 after.Exit
                      let status = JsonNode.Parse after.Out
                      let admitted = status["admitted"].GetValue<bool>()
                      Assert.isTrue (not admitted) "status admitted on an unavailable host"
              finally Directory.Delete(root, true)) ]
