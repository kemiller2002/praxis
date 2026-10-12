namespace Praxis.Tests

open System
open System.IO
open System.Threading
open Praxis.Application.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Artifacts
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalWorkerHostTests =
    let private t name run = { Name = "portability host: " + name; Run = run }
    let private ok = function Ok v -> v | Error e -> failwith (sprintf "%A" e)
    let private refused r = Assert.isTrue (Result.isError r) "unsafe host dispatch accepted"
    let private run host bindings packet token = FileLocalWorkerHost.run host bindings packet token |> fun task -> task.GetAwaiter().GetResult()
    let private fixture test =
        let directory = CliHarness.temporaryDirectory "praxis-portable-host"
        let repo = Path.Combine(directory, "repo")
        Directory.CreateDirectory repo |> ignore
        CliHarness.rosOk repo [ "init" ] |> ignore
        try
            CliHarness.git repo [ "init" ] |> ignore
            CliHarness.git repo [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "--allow-empty"; "-m"; "fixture source" ] |> ignore
            let packet = { LocalAgentHandoffTests.packet with SourceCommit = CliHarness.git repo [ "rev-parse"; "HEAD" ] }
            LocalWorkerDelegationTests.withFixtureForPacket packet (fun policy packet envelope _ _ ->
                LocalWorkerAssignmentTests.provision repo packet
                let provision name = let path = Path.Combine(directory, name) in Directory.CreateDirectory path |> ignore; path
                let settings = { RepositoryRoot = repo; HostRoot = provision "policy"; LockTimeout = TimeSpan.FromMilliseconds 100. }
                let at = ref(packet.IssuedAt.AddMinutes 1.)
                let host = FileLocalEcirAuthority.createPortableDevelopment settings (fun () -> at.Value)
                host.Initialize { policy with RepositoryRoot = repo } |> ok
                let mutable launches = 0
                let mutable selections = 0
                let adapter = LocalWorkerAdapters.command "fixture-bridge" packet.WorkerId
                                  (Set.ofList [ LocalWorkerOperatingSystem.Linux; LocalWorkerOperatingSystem.MacOS; LocalWorkerOperatingSystem.Windows ])
                                  (Set.singleton packet.Role) (LocalWorkerProcessTests.spec "result" [])
                let adapter = { adapter with Prepare = fun admitted -> selections <- selections + 1; adapter.Prepare admitted }
                let bindings : LocalWorkerHostBindings =
                    { RepositoryIdentity = packet.RepositoryIdentity
                      JournalRoot = provision "journal"; SpoolRoot = provision "spool"; SubmissionRoot = provision "submissions"
                      Catalog = LocalWorkerAdapters.catalog [ adapter ] |> ok
                      Platform = { LocalWorkerProcessPlatform.dotNet with ObserveIdentity = fun child -> launches <- launches + 1; Ok(sprintf "fixture-incarnation:%d" child.Id) }
                      LoadDelegation = fun () -> Ok(LocalWorkerDelegation.render envelope)
                      ValidateNative = fun _ -> Ok() // Additional host check; built-in native checks always run.
                      Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
                      ControllerSessionId = fun () -> Ok "fixture-controller" }
                test settings host bindings packet at (fun () -> launches, selections))
        finally CliHarness.removeTemporaryGitDirectory directory

    let tests =
        [ t "real subprocess seals output, records exit and archives once without granting acceptance" (fun () -> fixture (fun settings host bindings packet _ counts ->
              let report = run host bindings packet CancellationToken.None |> ok
              Assert.equal false report.Dispatch.ReconciliationRequired
              Assert.isTrue (Result.isOk report.Archive) (sprintf "complete output not archived: %A" report)
              let submissions = FileLocalWorkerSubmissions.create settings.RepositoryRoot bindings.SubmissionRoot |> ok
              let captured = submissions.Load packet.RepositoryIdentity packet.DispatchId |> ok
              Assert.equal 0 captured.ExitCode
              Assert.equal (Praxis.Contracts.Work.LocalAgentHandoffJson.renderResult LocalAgentHandoffTests.result) captured.Payload
              let spool = FileLocalWorkerOutputSpool.create settings.RepositoryRoot bindings.SpoolRoot |> ok
              spool.Load packet.RepositoryIdentity packet.DispatchId |> ok |> ignore
              run host bindings packet CancellationToken.None |> refused
              Assert.equal 1 (counts() |> fst)
              host.Load() |> ok |> ignore
              for resource in [ "work-protocol"; "work-groups" ] do Assert.isTrue (not (File.Exists(RegistryLock.lockPath settings.RepositoryRoot resource))) "native lease leaked"))
          t "native and policy leases span adapter preparation and actual process observation" (fun () -> fixture (fun settings host bindings packet _ _ ->
              let check () =
                  for resource in [ "work-protocol"; "work-groups" ] do Assert.isTrue (File.Exists(RegistryLock.lockPath settings.RepositoryRoot resource)) "native lease missing"
                  host.Load() |> refused
              let original = bindings.Platform.ObserveIdentity
              let binding = { bindings with ValidateNative = fun _ -> check(); Ok()
                                            Platform = { bindings.Platform with ObserveIdentity = fun child -> check(); original child } }
              run host binding packet CancellationToken.None |> ok |> ignore
              host.Load() |> ok |> ignore))
          t "invalid delegation and native observation refuse before worker preparation" (fun () ->
              for fault in [ "delegation"; "native"; "repository" ] do fixture (fun settings host bindings packet _ counts ->
                  let binding = match fault with
                                | "delegation" -> { bindings with LoadDelegation = fun () -> Ok "{}" }
                                | "native" -> { bindings with ValidateNative = fun _ -> Error "member execution differs" }
                                | _ -> { bindings with RepositoryIdentity = "different" }
                  run host binding packet CancellationToken.None |> refused
                  Assert.equal (0, 0) (counts())
                  let journal = FileLocalDispatchJournal.create settings.RepositoryRoot bindings.JournalRoot |> ok
                  journal.Load packet.RepositoryIdentity packet.DispatchId |> refused))
          t "expiry at intent retains reservation and prevents launch" (fun () -> fixture (fun settings host bindings packet at counts ->
              let mutable reads = 0
              let binding =
                  { bindings with
                      LoadDelegation = fun () ->
                        reads <- reads + 1
                        if reads = 3 then at.Value <- packet.ExpiresAt
                        bindings.LoadDelegation() }
              run host binding packet CancellationToken.None |> refused
              Assert.equal (0, 1) (counts())
              let journal = FileLocalDispatchJournal.create settings.RepositoryRoot bindings.JournalRoot |> ok
              let saved = journal.Load packet.RepositoryIdentity packet.DispatchId |> ok
              Assert.equal (Ok LocalDispatchPhase.Reserved) (LocalDispatchJournal.phase saved)))
          t "cancelled dispatch and invalid host storage never launch" (fun () ->
              for invalidStorage in [ false; true ] do fixture (fun settings host bindings packet _ counts ->
                  use cancellation = new CancellationTokenSource()
                  let binding = if invalidStorage then { bindings with JournalRoot = settings.RepositoryRoot } else bindings
                  if not invalidStorage then cancellation.Cancel()
                  run host binding packet cancellation.Token |> refused
                  Assert.equal (0, 0) (counts())
                  host.Load() |> ok |> ignore))
          t "unavailable incarnation retains uncertainty and does not archive output" (fun () -> fixture (fun settings host bindings packet _ _ ->
              let binding = { bindings with Platform = { bindings.Platform with ObserveIdentity = fun _ -> Error "incarnation unavailable" } }
              let report = run host binding packet CancellationToken.None |> ok
              Assert.equal true report.Dispatch.ReconciliationRequired
              report.Archive |> refused
              let submissions = FileLocalWorkerSubmissions.create settings.RepositoryRoot bindings.SubmissionRoot |> ok
              submissions.Load packet.RepositoryIdentity packet.DispatchId |> refused))
          t "throwing host observation releases all leases without enabling a retry" (fun () -> fixture (fun settings host bindings packet _ counts ->
              run host { bindings with ValidateNative = fun _ -> failwith "observer failed" } packet CancellationToken.None |> refused
              Assert.equal (0, 0) (counts())
              host.Load() |> ok |> ignore
              for resource in [ "work-protocol"; "work-groups" ] do Assert.isTrue (not (File.Exists(RegistryLock.lockPath settings.RepositoryRoot resource))) "native lease leaked"))
          t "pending policy or malformed native state refuses before selection" (fun () ->
              for fault in [ "policy"; "native"; "clock" ] do fixture (fun settings host bindings packet at counts ->
                  match fault with
                  | "policy" -> File.WriteAllText(Path.Combine(settings.HostRoot, "policy.pending"), "interrupted")
                  | "native" -> CliHarness.write settings.RepositoryRoot ".ros/context/current.json" "{}"
                  | _ -> at.Value <- at.Value.ToOffset(TimeSpan.FromHours 1.)
                  run host bindings packet CancellationToken.None |> refused
                  Assert.equal (0, 0) (counts())))
          t "native member replacement after adapter selection refuses before reservation" (fun () -> fixture (fun settings host bindings packet _ counts ->
              let adapter = LocalWorkerAdapters.command "changed-member-fixture" packet.WorkerId
                                (Set.ofList [ LocalWorkerOperatingSystem.Linux; LocalWorkerOperatingSystem.MacOS; LocalWorkerOperatingSystem.Windows ])
                                (Set.singleton packet.Role) (LocalWorkerProcessTests.spec "result" [])
              let changed =
                  { adapter with
                      Prepare = fun admitted ->
                          let execution = Praxis.Infrastructure.Execution.ExecutionStore.loadEnvelope settings.RepositoryRoot packet.Members.Head.ExecutionId |> ok
                          Praxis.Infrastructure.Execution.ExecutionStore.saveEnvelope settings.RepositoryRoot { execution with State = Praxis.Domain.Execution.ExecutionState.Blocked "fixture member changed" }
                          adapter.Prepare admitted }
              run host { bindings with Catalog = LocalWorkerAdapters.catalog [ changed ] |> ok } packet CancellationToken.None |> refused
              Assert.equal 0 (counts() |> fst)
              let journal = FileLocalDispatchJournal.create settings.RepositoryRoot bindings.JournalRoot |> ok
              journal.Load packet.RepositoryIdentity packet.DispatchId |> refused)) ]
