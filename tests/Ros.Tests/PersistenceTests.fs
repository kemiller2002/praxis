namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Ros.Application.Artifacts
open Ros.Application.Work
open Ros.Domain.Artifacts
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Work

[<RequireQualifiedAccess>]
module PersistenceTests =
    let private withTemporaryRoot operation =
        let root = Path.Combine(Path.GetTempPath(), $"ros-persistence-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            operation root
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    let private metadata processId ownerToken resource =
        $"{{\"pid\":{processId},\"ownerToken\":\"{ownerToken}\",\"resource\":\"{resource}\",\"acquiredAt\":\"2026-09-08T00:00:00.0000000Z\"}}\n"

    let private validDocument =
        { RelativePath = "research/evidence/EV-TEST-2026-A001--example.md"
          FileName = "EV-TEST-2026-A001--example.md"
          Metadata =
            Map.ofList
                [ "id", ArtifactValue.Text "EV-TEST-2026-A001"
                  "title", ArtifactValue.Text "Example"
                  "status", ArtifactValue.Text "accepted"
                  "evidence_type", ArtifactValue.Text "primary" ] }

    let private noWaitSettings =
        { RegistryLock.defaultSettings with
            RetryDelay = TimeSpan.Zero
            Timeout = TimeSpan.Zero }

    let private workStateWrites eventContent contextContent =
        [ { Path = ".ros/events/events.jsonl"; Content = eventContent }
          { Path = ".ros/context/current.json"; Content = contextContent } ]

    let private sha256Text (content: string) =
        content
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let tests =
        [ { Name = "registry lock path matches the Node SHA-256 lease layout"
            Run = fun () ->
                let resource = "artifact-registries"
                let bytes = Encoding.UTF8.GetBytes resource
                let expectedHash = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                let actual = RegistryLock.lockPath "/repository" resource
                Assert.equal $"/repository/.ros/locks/{expectedHash}.lock" (actual.Replace('\\', '/')) }
          { Name = "registry lock reclaims an expired dead-owner lease"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    let resource = "artifact-registries"
                    let file = RegistryLock.lockPath root resource
                    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                    File.WriteAllText(file, metadata 999999 "node-or-fsharp-owner" resource)
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow - RegistryLock.defaultSettings.StaleAfter - TimeSpan.FromSeconds 1.0)

                    match RegistryLock.acquire root resource noWaitSettings with
                    | Error failure -> failwith $"Expected stale recovery: {failure.Message}"
                    | Ok lease ->
                        Assert.isTrue (File.Exists file) "expected acquired lease file"

                        match lease.Release() with
                        | Error failure -> failwith $"Expected release: {failure.Message}"
                        | Ok() -> Assert.isTrue (not (File.Exists file)) "expected released lease file") }
          { Name = "registry lock rejects a current-process owner without deleting it"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    let resource = "artifact-registries"
                    let file = RegistryLock.lockPath root resource
                    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                    File.WriteAllText(file, metadata Environment.ProcessId "live-owner" resource)

                    match RegistryLock.acquire root resource noWaitSettings with
                    | Ok lease ->
                        lease.Release() |> ignore
                        failwith "Expected live lock rejection"
                    | Error failure ->
                        Assert.equal DependencyOutcome.Failed failure.Outcome
                        Assert.isTrue (File.Exists file) "a live lock must not be deleted") }
          { Name = "registry lock reports ownership change on release"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    let resource = "artifact-registries"

                    match RegistryLock.acquire root resource noWaitSettings with
                    | Error failure -> failwith $"Expected acquisition: {failure.Message}"
                    | Ok lease ->
                        let file = RegistryLock.lockPath root resource
                        File.WriteAllText(file, metadata Environment.ProcessId "different-owner" resource)

                        match lease.Release() with
                        | Ok() -> failwith "Expected ownership-change failure"
                        | Error failure ->
                            Assert.equal DependencyOutcome.Indeterminate failure.Outcome
                            File.Delete file) }
          { Name = "registry transaction replays a pending generated-registry write set"
            Run = fun () ->
                withTemporaryRoot (fun root ->
                    let registryPath = "registries/evidence.json"
                    let expected = "[\n  {\n    \"id\": \"EV-TEST-2026-A001\"\n  }\n]\n"
                    let file = Path.Combine(root, registryPath)

                    match RegistryTransaction.prepare root [ { Path = registryPath; Content = expected } ] with
                    | Error failure -> failwith $"Expected preparation: {failure.Message}"
                    | Ok() ->
                        Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                        File.WriteAllText(file, "[]\n")
                        Assert.isTrue (File.Exists(RegistryTransaction.transactionPath root)) "expected pending transaction"

                        match RegistryTransaction.recover root with
                        | Error failure -> failwith $"Expected recovery: {failure.Message}"
                        | Ok() ->
                            Assert.equal expected (File.ReadAllText file)
                            Assert.isTrue (not (File.Exists(RegistryTransaction.transactionPath root))) "expected completed transaction") }
          { Name = "registry build does not load or write when lease acquisition fails"
            Run = fun () ->
                let failure: DependencyFailure =
                    { Operation = "acquire artifact registry lock"
                      Path = None
                      Message = "injected lease failure"
                      Outcome = DependencyOutcome.Failed }

                let repository: ArtifactRepository =
                    { Load = fun () -> failwith "Load must not run after failed lease acquisition"
                      ReadRegistry = fun _ -> failwith "Read must not run after failed lease acquisition"
                      WriteRegistry = fun _ _ -> failwith "Write must not run after failed lease acquisition"
                      AcquireRegistryWriteLease = fun () -> Error failure }

                match ArtifactOperations.buildRegistries false repository with
                | RegistryBuildOutcome.DependencyFailure actual -> Assert.equal failure actual
                | outcome -> failwith $"Expected lease failure, received {outcome}" }
          { Name = "registry build does not load when pending transaction recovery fails"
            Run = fun () ->
                let failure: DependencyFailure =
                    { Operation = "recover artifact registry transaction"
                      Path = None
                      Message = "injected recovery failure"
                      Outcome = DependencyOutcome.Indeterminate }

                let repository: ArtifactRepository =
                    { Load = fun () -> failwith "Load must not run after failed transaction recovery"
                      ReadRegistry = fun _ -> failwith "Read must not run after failed transaction recovery"
                      WriteRegistry = fun _ _ -> failwith "Write must not run after failed transaction recovery"
                      AcquireRegistryWriteLease =
                        fun () ->
                            Ok
                                { Recover = fun () -> Error failure
                                  Prepare = fun _ -> Ok()
                                  Complete = fun () -> Ok()
                                  Release = fun () -> Ok() } }

                match ArtifactOperations.buildRegistries false repository with
                | RegistryBuildOutcome.DependencyFailure actual -> Assert.equal failure actual
                | outcome -> failwith $"Expected recovery failure, received {outcome}" }
          { Name = "registry build exposes an indeterminate release after successful writes"
            Run = fun () ->
                let writes = ResizeArray<string>()

                let releaseFailure: DependencyFailure =
                    { Operation = "release artifact registry lock"
                      Path = None
                      Message = "injected ownership change"
                      Outcome = DependencyOutcome.Indeterminate }

                let repository: ArtifactRepository =
                    { Load = fun () -> Ok { Documents = [ validDocument ]; ParseFindings = [] }
                      ReadRegistry = fun _ -> Ok None
                      WriteRegistry = fun path _ -> writes.Add path; Ok()
                      AcquireRegistryWriteLease =
                        fun () ->
                            Ok
                                { Recover = fun () -> Ok()
                                  Prepare = fun _ -> Ok()
                                  Complete = fun () -> Ok()
                                  Release = fun () -> Error releaseFailure } }

                match ArtifactOperations.buildRegistries false repository with
                | RegistryBuildOutcome.Incomplete(written, pending, failure) ->
                    Assert.equal 8 written.Length
                    Assert.equal 0 pending.Length
                    Assert.equal releaseFailure failure
                | outcome -> failwith $"Expected indeterminate release outcome, received {outcome}" }
          { Name = "work-state recovery completes an interrupted event-then-context write"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let eventFile = Path.Combine(root, ".ros/events/events.jsonl")
                      let contextFile = Path.Combine(root, ".ros/context/current.json")
                      Directory.CreateDirectory(Path.GetDirectoryName eventFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName contextFile) |> ignore
                      File.WriteAllText(eventFile, "before-event\n")
                      File.WriteAllText(contextFile, "before-context\n")
                      let writes = workStateWrites "after-event\n" "after-context\n"

                      match WorkStateTransaction.prepare root writes with
                      | Error failure -> failwith failure.Message
                      | Ok() ->
                          File.WriteAllText(eventFile, "after-event\n")

                          match WorkStateTransaction.recover root with
                          | Error failure -> failwith failure.Message
                          | Ok() ->
                              Assert.equal "after-event\n" (File.ReadAllText eventFile)
                              Assert.equal "after-context\n" (File.ReadAllText contextFile)
                              Assert.isTrue (not (File.Exists(WorkStateTransaction.transactionPath root))) "expected completed recovery") }
          { Name = "work-state recovery preflights every target before replay"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let eventFile = Path.Combine(root, ".ros/events/events.jsonl")
                      let contextFile = Path.Combine(root, ".ros/context/current.json")
                      Directory.CreateDirectory(Path.GetDirectoryName eventFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName contextFile) |> ignore
                      File.WriteAllText(eventFile, "before-event\n")
                      File.WriteAllText(contextFile, "before-context\n")

                      match WorkStateTransaction.prepare root (workStateWrites "after-event\n" "after-context\n") with
                      | Error failure -> failwith failure.Message
                      | Ok() ->
                          File.WriteAllText(contextFile, "third-party-context\n")

                          match WorkStateTransaction.recover root with
                          | Ok() -> failwith "Expected divergence to prevent recovery"
                          | Error failure ->
                              Assert.equal WorkPersistenceOutcome.Indeterminate failure.Outcome
                              Assert.equal (Some ".ros/context/current.json") failure.Path
                              Assert.equal "before-event\n" (File.ReadAllText eventFile)
                              Assert.isTrue (File.Exists(WorkStateTransaction.transactionPath root)) "conflicted transaction must remain") }
          { Name = "work-state transaction rejects incomplete or reordered targets"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let reversed = workStateWrites "event\n" "context\n" |> List.rev

                      match WorkStateTransaction.prepare root reversed with
                      | Ok() -> failwith "Expected reordered write rejection"
                      | Error failure -> Assert.equal WorkPersistenceOutcome.Failed failure.Outcome

                      match WorkStateTransaction.prepare root [ { Path = ".ros/context/current.json"; Content = "context\n" } ] with
                      | Ok() -> failwith "Expected incomplete write-set rejection"
                      | Error failure -> Assert.equal WorkPersistenceOutcome.Failed failure.Outcome) }
          { Name = "work-state transaction never overwrites an unrecovered journal"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let writes = workStateWrites "event\n" "context\n"

                      match WorkStateTransaction.prepare root writes with
                      | Error failure -> failwith failure.Message
                      | Ok() ->
                          let original = File.ReadAllText(WorkStateTransaction.transactionPath root)

                          match WorkStateTransaction.prepare root writes with
                          | Ok() -> failwith "Expected pending transaction rejection"
                          | Error failure ->
                              Assert.equal WorkPersistenceOutcome.Failed failure.Outcome
                              Assert.equal original (File.ReadAllText(WorkStateTransaction.transactionPath root))) }
          { Name = "work-state recovery rejects a corrupt journal without touching targets"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let eventFile = Path.Combine(root, ".ros/events/events.jsonl")
                      let contextFile = Path.Combine(root, ".ros/context/current.json")
                      let transaction = WorkStateTransaction.transactionPath root
                      Directory.CreateDirectory(Path.GetDirectoryName eventFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName contextFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName transaction) |> ignore
                      File.WriteAllText(eventFile, "before-event\n")
                      File.WriteAllText(contextFile, "before-context\n")
                      File.WriteAllText(transaction, "{\"schemaVersion\":\"1.0.0\",\"resource\":\"work-state\",\"writes\":[]}")

                      match WorkStateTransaction.recover root with
                      | Ok() -> failwith "Expected corrupt transaction rejection"
                      | Error failure ->
                          Assert.equal WorkPersistenceOutcome.Indeterminate failure.Outcome
                          Assert.equal "before-event\n" (File.ReadAllText eventFile)
                          Assert.equal "before-context\n" (File.ReadAllText contextFile)
                          Assert.isTrue (File.Exists transaction) "corrupt transaction must remain for inspection") }
          { Name = "F# recovers a Node-shaped work-state journal"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let eventFile = Path.Combine(root, ".ros/events/events.jsonl")
                      let contextFile = Path.Combine(root, ".ros/context/current.json")
                      let transaction = WorkStateTransaction.transactionPath root
                      let beforeEvent = "before-event\n"
                      let beforeContext = "before-context\n"
                      let afterEvent = "after-event\n"
                      let afterContext = "after-context\n"
                      Directory.CreateDirectory(Path.GetDirectoryName eventFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName contextFile) |> ignore
                      Directory.CreateDirectory(Path.GetDirectoryName transaction) |> ignore
                      File.WriteAllText(eventFile, beforeEvent)
                      File.WriteAllText(contextFile, beforeContext)

                      let record =
                          {| schemaVersion = "1.0.0"
                             resource = "work-state"
                             writes =
                              [ {| path = ".ros/events/events.jsonl"
                                   beforeSha256 = sha256Text beforeEvent
                                   afterSha256 = sha256Text afterEvent
                                   content = afterEvent |}
                                {| path = ".ros/context/current.json"
                                   beforeSha256 = sha256Text beforeContext
                                   afterSha256 = sha256Text afterContext
                                   content = afterContext |} ] |}

                      File.WriteAllText(transaction, JsonSerializer.Serialize record + "\n")

                      match WorkStateTransaction.recover root with
                      | Error failure -> failwith failure.Message
                      | Ok() ->
                          Assert.equal afterEvent (File.ReadAllText eventFile)
                          Assert.equal afterContext (File.ReadAllText contextFile)
                          Assert.isTrue (not (File.Exists transaction)) "expected Node-shaped journal completion") } ]
