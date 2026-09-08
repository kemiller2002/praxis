namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Ros.Application.Artifacts
open Ros.Domain.Artifacts
open Ros.Infrastructure.Artifacts

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
                let failure =
                    { Operation = "acquire artifact registry lock"
                      Path = None
                      Message = "injected lease failure"
                      Outcome = DependencyOutcome.Failed }

                let repository =
                    { Load = fun () -> failwith "Load must not run after failed lease acquisition"
                      ReadRegistry = fun _ -> failwith "Read must not run after failed lease acquisition"
                      WriteRegistry = fun _ _ -> failwith "Write must not run after failed lease acquisition"
                      AcquireRegistryWriteLease = fun () -> Error failure }

                match ArtifactOperations.buildRegistries false repository with
                | RegistryBuildOutcome.DependencyFailure actual -> Assert.equal failure actual
                | outcome -> failwith $"Expected lease failure, received {outcome}" }
          { Name = "registry build does not load when pending transaction recovery fails"
            Run = fun () ->
                let failure =
                    { Operation = "recover artifact registry transaction"
                      Path = None
                      Message = "injected recovery failure"
                      Outcome = DependencyOutcome.Indeterminate }

                let repository =
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

                let releaseFailure =
                    { Operation = "release artifact registry lock"
                      Path = None
                      Message = "injected ownership change"
                      Outcome = DependencyOutcome.Indeterminate }

                let repository =
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
                | outcome -> failwith $"Expected indeterminate release outcome, received {outcome}" } ]
