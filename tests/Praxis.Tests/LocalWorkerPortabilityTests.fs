namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalWorkerPortabilityTests =
    let private t name run = { Name = "portability: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refused result = Assert.isTrue (Result.isError result) "unsupported adapter accepted"
    let private packet = LocalAgentHandoffTests.packet
    let private admitted =
        LocalAgentHandoff.checkPacket { Enabled = true; Revision = packet.AuthorityRevision; ExpectedPacket = packet }
            (packet.IssuedAt.AddMinutes 1.) (packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList) packet |> ok
    let private allSystems = Set.ofList [ LocalWorkerOperatingSystem.Linux; LocalWorkerOperatingSystem.MacOS; LocalWorkerOperatingSystem.Windows ]
    let private adapter workerId prepare : LocalWorkerAdapter =
        { Id = "bridge-" + workerId; WorkerId = workerId; OperatingSystems = allSystems
          Roles = Set.singleton ExecutionRole.Implementation; Prepare = prepare }
    let private fixture test =
        let directory = CliHarness.temporaryDirectory "praxis-portability"
        let repo, host = Path.Combine(directory, "repo"), Path.Combine(directory, "host")
        Directory.CreateDirectory repo |> ignore
        Directory.CreateDirectory host |> ignore
        let now = DateTimeOffset.UtcNow
        let settings = { RepositoryRoot = repo; HostRoot = host; LockTimeout = TimeSpan.FromMilliseconds 100. }
        let store = FileLocalEcirAuthority.createPortableDevelopment settings (fun () -> now)
        try test settings store now
        finally Directory.Delete(directory, true)
    let private policy root : EcirHostPolicy =
        { Revision = "portable-1"; RepositoryRoot = root; QualifiedRelease = false; TrustedSigners = Map.empty; Grants = [] }

    let tests =
        [ t "two worker bridges share the packet protocol on all declared OSes" (fun () ->
              let mutable first = 0
              let mutable second = 0
              let template = LocalWorkerProcessTests.spec "echo" []
              let a = adapter packet.WorkerId (fun _ -> first <- first + 1; Ok template)
              let b = adapter "another-worker" (fun _ -> second <- second + 1; Ok template)
              let catalog = LocalWorkerAdapters.catalog [ a; b ] |> ok
              for os in allSystems do LocalWorkerAdapters.select os catalog admitted |> ok |> ignore
              Assert.equal 3 first
              Assert.equal 0 second
              let other = { packet with WorkerId = "another-worker" }
              let checkedOther = LocalAgentHandoff.checkPacket { Enabled = true; Revision = other.AuthorityRevision; ExpectedPacket = other }
                                     (other.IssuedAt.AddMinutes 1.) (other.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList) other |> ok
              LocalWorkerAdapters.select LocalWorkerOperatingSystem.Windows catalog checkedOther |> ok |> ignore
              Assert.equal 1 second)
          t "ambiguous identities and invalid capability declarations refuse" (fun () ->
              let a = adapter packet.WorkerId (fun _ -> failwith "must not prepare")
              for candidates in [ []; [ a; a ]; [ a; { a with Id = "different" } ]; [ a; { a with WorkerId = "different" } ]
                                  [ { a with OperatingSystems = Set.empty } ]; [ { a with Roles = Set.empty } ]
                                  [ { a with WorkerId = " worker" } ]; [ { a with Id = "" } ] ] do
                  LocalWorkerAdapters.catalog candidates |> refused)
          t "unknown worker, unsupported OS and role refuse before preparation" (fun () ->
              let mutable calls = 0
              let a = adapter packet.WorkerId (fun _ -> calls <- calls + 1; Error "must not prepare")
              for candidate in [ { a with WorkerId = "different" }
                                 { a with OperatingSystems = Set.singleton LocalWorkerOperatingSystem.Linux }
                                 { a with Roles = Set.singleton ExecutionRole.Review } ] do
                  let catalog = LocalWorkerAdapters.catalog [ candidate ] |> ok
                  LocalWorkerAdapters.select LocalWorkerOperatingSystem.Windows catalog admitted |> refused
              Assert.equal 0 calls)
          t "command bridges preserve canonical stdin and tighten delegated budgets" (fun () ->
              let template = { LocalWorkerProcessTests.spec "echo" [] with Input = "discarded template prose"; TimeoutMilliseconds = 86400000; MaxOutputBytes = 67108864 }
              let a = LocalWorkerAdapters.command "fixture" packet.WorkerId allSystems (Set.singleton packet.Role) template
              let selected = LocalWorkerAdapters.select LocalWorkerOperatingSystem.MacOS (LocalWorkerAdapters.catalog [ a ] |> ok) admitted |> ok
              Assert.equal (LocalAgentHandoffJson.renderPacket packet) selected.Input
              Assert.equal (packet.TimeoutSeconds * 1000) selected.TimeoutMilliseconds
              Assert.equal packet.MaxOutputBytes selected.MaxOutputBytes
              Assert.equal template.Arguments selected.Arguments
              Assert.equal template.Environment selected.Environment)
          t "adapter exceptions become refusals rather than fallback to another worker" (fun () ->
              let catalog = LocalWorkerAdapters.catalog [ adapter packet.WorkerId (fun _ -> failwith "adapter failure") ] |> ok
              LocalWorkerAdapters.select LocalWorkerOperatingSystem.Linux catalog admitted |> refused)
          t "current OS selection uses the runtime instead of model-provided OS claims" (fun () ->
              let expected = if OperatingSystem.IsWindows() then LocalWorkerOperatingSystem.Windows elif OperatingSystem.IsMacOS() then LocalWorkerOperatingSystem.MacOS else LocalWorkerOperatingSystem.Linux
              Assert.equal (Ok expected) (LocalWorkerProcessPlatform.operatingSystem())
              let catalog = LocalWorkerAdapters.catalog [ adapter packet.WorkerId (fun _ -> Ok(LocalWorkerProcessTests.spec "echo" [])) ] |> ok
              LocalWorkerProcessPlatform.selectCurrent catalog admitted |> ok |> ignore)
          t "portable host policy round trips and remains explicitly unqualified" (fun () -> fixture (fun settings store _ ->
              Assert.equal "portable-development-unqualified" store.FileAccessProfile
              let expected = policy settings.RepositoryRoot
              store.Initialize expected |> ok
              Assert.equal (Ok expected) (store.Load())
              let reopened = FileLocalEcirAuthority.createPortableDevelopment settings (fun () -> DateTimeOffset.UtcNow)
              Assert.equal (Ok expected) (reopened.Load())
              store.Initialize expected |> refused
              let mutable committed = false
              store.Authority.Commit expected.Revision (DateTimeOffset.UtcNow.AddHours 1.) (fun () -> committed <- true; Ok()) |> refused
              Assert.equal false committed))
          t "portable storage preserves audited CAS and pending-write refusal" (fun () -> fixture (fun settings store now ->
              use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
              let original = { policy settings.RepositoryRoot with TrustedSigners = Map.ofList [ "fixture-key", { Approver = "fixture-owner"; PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() } ] }
              store.Initialize original |> ok
              let updated = { original with Revision = "portable-2"; TrustedSigners = Map.empty }
              let audit : EcirHostAudit =
                  { Id = "portable-audit"; Action = "revoked"; Approver = "fixture-owner"; KeyId = "fixture-key"
                    GroupId = None; PreviousRevision = original.Revision; NewRevision = updated.Revision; OccurredAt = now; ReceiptSha256 = None }
              store.Apply(original.Revision, updated, audit) |> ok
              Assert.equal (Ok [ audit ]) (store.Audit())
              store.Apply(original.Revision, updated, audit) |> refused
              File.WriteAllText(Path.Combine(settings.HostRoot, "policy.pending"), "interrupted")
              store.Load() |> refused))
          t "injected file access refusal is respected by the shared store" (fun () -> fixture (fun settings _ now ->
              let access = { LocalHostFileAccess.portableDevelopment with CheckDirectory = fun _ -> Error "ACL adapter refused" }
              let store = FileLocalEcirAuthority.createWithFileAccess settings (fun () -> now) access
              store.Initialize(policy settings.RepositoryRoot) |> refused
              Assert.isTrue (not (File.Exists(Path.Combine(settings.HostRoot, "policy.json")))) "refused storage was written"))
          t "portable profile still rejects a host root inside the repository" (fun () -> fixture (fun settings _ now ->
              let nested = Path.Combine(settings.RepositoryRoot, "host")
              Directory.CreateDirectory nested |> ignore
              let mutable refusedRoot = false
              try FileLocalEcirAuthority.createPortableDevelopment { settings with HostRoot = nested } (fun () -> now) |> ignore
              with :? InvalidOperationException -> refusedRoot <- true
              Assert.isTrue refusedRoot "repository-local authority was accepted"))
          t "injected platform preserves literal argv, stdin and bounded output on a real process" (fun () ->
              let mutable observed = 0
              let mutable stopped = 0
              let platform =
                  { LocalWorkerProcessPlatform.dotNet with
                      ObserveIdentity = fun child -> observed <- observed + 1; Ok(sprintf "fixture-incarnation:%d" child.Id)
                      StopAndObserveRootExit = fun child cancellation -> stopped <- stopped + 1; LocalWorkerProcessPlatform.dotNet.StopAndObserveRootExit child cancellation }
              let port = LocalWorkerProcess.createWithPlatform platform
              let spec = LocalWorkerProcessTests.spec "echo" [ "literal space"; "$(not-a-command)" ]
              let result = port.Run spec (fun _ -> Ok()) CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
              Assert.equal (LocalWorkerProcessOutcome.Exited 0) result.Outcome
              Assert.isTrue result.RootExitObserved "root exit was not observed"
              use json = JsonDocument.Parse result.StandardOutput
              Assert.equal spec.Input (json.RootElement.GetProperty("input").GetString())
              Assert.equal [ "literal space"; "$(not-a-command)" ] (json.RootElement.GetProperty("arguments").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList)
              Assert.equal 1 observed
              Assert.equal 1 stopped)
          t "platform exit refusal stays uncertain even after fixture cleanup" (fun () ->
              let platform =
                  { LocalWorkerProcessPlatform.dotNet with
                      ObserveIdentity = fun child -> Ok(sprintf "fixture-incarnation:%d" child.Id)
                      StopAndObserveRootExit = fun child cancellation -> task {
                          let! _ = LocalWorkerProcessPlatform.dotNet.StopAndObserveRootExit child cancellation
                          return Error "fixture cannot confirm exit" } }
              let result = (LocalWorkerProcess.createWithPlatform platform).Run (LocalWorkerProcessTests.spec "echo" []) (fun _ -> Ok()) CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()
              Assert.equal false result.RootExitObserved
              match result.Outcome with LocalWorkerProcessOutcome.ObservationFailed _ -> () | _ -> failwith "platform refusal was promoted") ]
