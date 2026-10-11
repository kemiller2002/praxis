namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Domain.Execution
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalWorkerDelegationTests =
    let private now = DateTimeOffset.Parse("2026-10-10T12:00:00Z")
    let private root = Path.GetTempPath()
    let private t name run = { Name = "local delegation: " + name; Run = run }
    let private refused result = Assert.isTrue (Result.isError result) "unauthorized delegation accepted"
    let private withFixture run =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let original = LocalAgentHandoffTests.packet
        let scope : EcirApprovalScope =
            { GroupId = original.GroupId; CohortId = original.CohortId; SourceCommit = original.SourceCommit
              ManifestDigest = original.SourceManifestDigest; BlueprintDigest = original.BlueprintDigest
              RequirementKeys = original.Members |> List.collect _.RequirementKeys; DecisionIds = original.DecisionIds }
        let unsigned : EcirSignedApproval =
            { SchemaVersion = EcirApprovals.SchemaVersion; KeyId = "key-1"; Approver = "operator-1"
              IssuedAt = "2026-10-10T11:00:00Z"; ExpiresAt = "2026-10-10T13:00:00Z"
              Scope = scope; Signature = "" }
        let receipt = JsonSerializer.Serialize
                        {| schemaVersion = unsigned.SchemaVersion; keyId = unsigned.KeyId; approver = unsigned.Approver
                           issuedAt = unsigned.IssuedAt; expiresAt = unsigned.ExpiresAt
                           signature = Convert.ToBase64String(key.SignData(EcirApprovals.signingPayload unsigned, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                           scope = {| groupId = scope.GroupId; cohortId = scope.CohortId; sourceCommit = scope.SourceCommit
                                      manifestDigest = scope.ManifestDigest; blueprintDigest = scope.BlueprintDigest
                                      requirementKeys = scope.RequirementKeys; decisionIds = scope.DecisionIds |} |}
        let packet = { original with ReceiptDigest = "sha256:" + EcirDispatchTransaction.hashText receipt }
        let policy : EcirHostPolicy =
            { Revision = packet.AuthorityRevision; RepositoryRoot = root; QualifiedRelease = true
              TrustedSigners = Map.ofList [ unsigned.KeyId, { Approver = unsigned.Approver; PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() } ]
              Grants = [ { GroupId = packet.GroupId; CohortId = packet.CohortId
                           Source = { Commit = packet.SourceCommit; ManifestPath = "manifest.json"; BlueprintPath = "blueprint.json"; SourceManifestDigest = packet.SourceManifestDigest }
                           BlueprintDigest = packet.BlueprintDigest; Ordo = { Executable = "/host/ordo"; Sha256 = packet.BlueprintDigest }
                           MemberRequirements = packet.Members |> List.map (fun m -> m.WorkItemId, m.RequirementKeys.Head) |> Map.ofList
                           ReceiptJson = receipt } ] }
        let sign packet =
            let envelope = { SchemaVersion = LocalWorkerDelegation.SchemaVersion; KeyId = unsigned.KeyId
                             Approver = unsigned.Approver; PacketDigest = LocalAgentHandoff.packetDigest packet; Signature = "" }
            { envelope with Signature = Convert.ToBase64String(key.SignData(LocalWorkerDelegation.signingPayload envelope, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) }
        run policy packet (sign packet) sign key

    let private verify policy packet envelope = LocalWorkerDelegation.verify root packet.RepositoryIdentity policy now packet envelope
    let tests =
        [ t "signed exact packet creates only ephemeral authority and round trips strictly" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  Assert.equal (Ok envelope) (LocalWorkerDelegation.render envelope |> LocalWorkerDelegation.read)
                  Assert.equal (Ok { Enabled = true; Revision = policy.Revision; ExpectedPacket = LocalAgentHandoff.canonicalPacket packet }) (verify policy packet envelope)
                  LocalWorkerDelegation.verify root "different-repository" policy now packet envelope |> refused
                  let reordered = { packet with Members = List.rev packet.Members; AllowedPaths = List.rev packet.AllowedPaths; DecisionIds = List.rev packet.DecisionIds }
                  Assert.equal (verify policy packet envelope) (verify policy reordered envelope)))
          t "all worker identity, resource and budget changes invalidate the signed packet" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  for changed in [ { packet with WorkerId = "other" }; { packet with AttemptId = "other" }
                                   { packet with ParentExecutionId = "other" }; { packet with ChildExecutionId = "other" }
                                   { packet with DispatchId = "other" }; { packet with RepositoryIdentity = "other" }
                                   { packet with AllowedPaths = [ "src/C.fs" ] }; { packet with TimeoutSeconds = 601 }
                                   { packet with MaxOutputBytes = 100 }; { packet with ExpiresAt = packet.ExpiresAt.AddSeconds(-1.) }
                                   { packet with Members = [ { packet.Members.Head with ExecutionId = "EXE-other" }; packet.Members[1] ] }
                                   { packet with Acceptance = [ { packet.Acceptance.Head with ValidatorId = "self" }; packet.Acceptance[1] ] }
                                   { packet with Prerequisites = [] } ] do verify policy changed envelope |> refused))
          t "ECIR approval alone cannot act as a worker delegation signature" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  let ecir = EcirApprovals.read policy.Grants.Head.ReceiptJson |> Result.toOption |> Option.get
                  verify policy packet { envelope with Signature = ecir.Signature } |> refused))
          t "revocation, revision and qualified release changes invalidate delegation" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  for changed in [ { policy with TrustedSigners = Map.empty }; { policy with Revision = "rev-2" }
                                   { policy with QualifiedRelease = false }; { policy with Grants = [] }
                                   { policy with RepositoryRoot = Path.Combine(root, "other") } ] do verify changed packet envelope |> refused))
          t "a signed packet cannot invent grant source, coverage or decisions" (fun () ->
              withFixture (fun policy packet _ sign _ ->
                  for changed in [ { packet with SourceCommit = String('f', 40) }; { packet with CohortId = "other" }
                                   { packet with BlueprintDigest = "sha256:" + String('f', 64) }
                                   { packet with Members = [ packet.Members.Head ] }; { packet with DecisionIds = [ "other" ] }
                                   { packet with ReceiptDigest = "sha256:" + String('f', 64) } ] do verify policy changed (sign changed) |> refused))
          t "delegation window must be contained in the ECIR window" (fun () ->
              withFixture (fun policy packet envelope sign _ ->
                  for at in [ packet.IssuedAt.AddTicks(-1L); packet.ExpiresAt; now.ToOffset(TimeSpan.FromHours 1.) ] do
                      LocalWorkerDelegation.verify root packet.RepositoryIdentity policy at packet envelope |> refused
                  for changed in [ { packet with IssuedAt = now.AddHours(-2.) }; { packet with ExpiresAt = now.AddHours(2.) } ] do
                      verify policy changed (sign changed) |> refused))
          t "current receipt bytes and both signatures remain mandatory" (fun () ->
              withFixture (fun policy packet envelope sign _ ->
                  let changed = { policy with Grants = [ { policy.Grants.Head with ReceiptJson = "{}" } ] }
                  verify changed packet envelope |> refused
                  let changedPacket = { packet with ReceiptDigest = "sha256:" + EcirDispatchTransaction.hashText "{}" }
                  verify changed changedPacket (sign changedPacket) |> refused
                  let old = policy.Grants.Head.ReceiptJson
                  let forgedNode = System.Text.Json.Nodes.JsonNode.Parse old
                  forgedNode["signature"] <- System.Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String(Array.zeroCreate<byte> 64))
                  let forged = forgedNode.ToJsonString()
                  Assert.isTrue (forged <> old) "fixture did not alter the receipt"
                  let forgedPolicy = { policy with Grants = [ { policy.Grants.Head with ReceiptJson = forged } ] }
                  let forgedPacket = { packet with ReceiptDigest = "sha256:" + EcirDispatchTransaction.hashText forged }
                  verify forgedPolicy forgedPacket (sign forgedPacket) |> refused
                  for signature in [ "invalid"; Convert.ToBase64String(Array.zeroCreate<byte> 64) ] do
                      verify policy packet { envelope with Signature = signature } |> refused))
          t "signer ownership, canonical public key and P256 are mandatory" (fun () ->
              withFixture (fun policy packet envelope _ key ->
                  use other = ECDsa.Create(ECCurve.NamedCurves.nistP384)
                  for signer in [ { Approver = "other"; PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() }
                                  { Approver = envelope.Approver; PublicKeyPem = key.ExportPkcs8PrivateKeyPem() }
                                  { Approver = envelope.Approver; PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() } ] do
                      verify { policy with TrustedSigners = Map.ofList [ envelope.KeyId, signer ] } packet envelope |> refused
                  for changed in [ { envelope with Approver = "other" }; { envelope with KeyId = "other" }
                                   { envelope with SchemaVersion = "other/1" } ] do verify policy packet changed |> refused))
          t "strict bounded envelope rejects missing, duplicate, unknown and nonstring fields" (fun () ->
              withFixture (fun _ _ envelope _ _ ->
                  let json = LocalWorkerDelegation.render envelope
                  for changed in [ null; "[]"; "{}"; json.Replace("\"keyId\":", "\"keyId\":\"duplicate\",\"keyId\":")
                                   json.Insert(1, "\"unknown\":true,"); json.Replace("\"key-1\"", "12")
                                   String('x', 8193); json.Replace("key-1", " key-1") ] do
                      LocalWorkerDelegation.read changed |> refused))
          t "authority callback reloads policy, envelope and clock at every boundary" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  let mutable current = policy
                  let mutable json = LocalWorkerDelegation.render envelope
                  let mutable at = now
                  let authority = LocalWorkerDelegation.authority root packet.RepositoryIdentity (fun () -> Ok current) (fun () -> Ok json) (fun () -> at) packet
                  Assert.isTrue (authority() |> Result.isOk) "valid authority refused"
                  current <- { policy with TrustedSigners = Map.empty }; authority() |> refused
                  current <- policy; json <- "{}"; authority() |> refused
                  json <- LocalWorkerDelegation.render envelope; at <- packet.ExpiresAt; authority() |> refused))
          t "signed authority composes with a real immutable journal without permitting relaunch" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  let directory = CliHarness.temporaryDirectory "praxis-signed-journal"
                  let repo, host = Path.Combine(directory, "repo"), Path.Combine(directory, "host")
                  Directory.CreateDirectory repo |> ignore
                  Directory.CreateDirectory host |> ignore
                  let ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
                  try
                      let store = FileLocalDispatchJournal.create repo host |> ok
                      let mutable launches = 0
                      let mutable checks = 0
                      let controller : LocalDispatchControllerPorts =
                          { Store = store
                            Authority = LocalWorkerDelegation.authority root packet.RepositoryIdentity (fun () -> checks <- checks + 1; Ok policy) (fun () -> Ok(LocalWorkerDelegation.render envelope)) (fun () -> now) packet
                            Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
                            NativeRecovery = fun () -> Ok()
                            ControllerSessionId = fun () -> Ok "fixture-controller"
                            Now = fun () -> now }
                      let ports : LocalJournaledDispatchPorts =
                          { Controller = controller
                            Worker = { Run = fun _ started _ -> task {
                                launches <- launches + 1
                                started "fixture-process" |> ok
                                return { ProcessIdentity = Some "fixture-process"; ProcessStarted = Some true; RootExitObserved = true
                                         Outcome = LocalWorkerProcessOutcome.Exited 0; StandardOutput = ""; StandardError = ""; CapturedBytes = 0 } } }
                            SelectSpec = fun _ ->
                                Ok { Executable = "/fixture/worker"; ExecutableDigest = "sha256:" + String('a', 64)
                                     WorkingDirectory = repo; Arguments = []; Environment = Map.empty
                                     Input = LocalAgentHandoffJson.renderPacket packet; TimeoutMilliseconds = 1000; MaxOutputBytes = 1024 } }
                      let report = LocalJournaledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> ok
                      Assert.equal false report.ReconciliationRequired
                      Assert.equal (Some(LocalDispatchPhase.Exited "fixture-process")) report.JournalPhase
                      Assert.equal 3 checks
                      LocalJournaledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> refused
                      Assert.equal 1 launches
                  finally Directory.Delete(directory, true)))
          t "signed authority is freshly checked before reservation and persisted launch intent" (fun () ->
              withFixture (fun policy packet envelope _ _ ->
                  let directory = CliHarness.temporaryDirectory "praxis-signed-delegation"
                  let repo, host = Path.Combine(directory, "repo"), Path.Combine(directory, "host")
                  Directory.CreateDirectory repo |> ignore
                  Directory.CreateDirectory host |> ignore
                  let ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
                  try
                      let store = FileLocalDispatchJournal.create repo host |> ok
                      let mutable current = policy
                      let mutable selections = 0
                      let controller : LocalDispatchControllerPorts =
                          { Store = store
                            Authority = LocalWorkerDelegation.authority root packet.RepositoryIdentity (fun () -> Ok current) (fun () -> Ok(LocalWorkerDelegation.render envelope)) (fun () -> now) packet
                            Prerequisites = fun () -> Ok(packet.Prerequisites |> List.map (fun p -> p.WorkItemId, p.EvidenceDigest) |> Map.ofList)
                            NativeRecovery = fun () -> Ok()
                            ControllerSessionId = fun () -> Ok "fixture-controller"
                            Now = fun () -> now }
                      let ports : LocalJournaledDispatchPorts =
                          { Controller = controller
                            Worker = { Run = fun _ _ _ -> failwith "revoked delegation reached worker" }
                            SelectSpec = fun _ ->
                                selections <- selections + 1
                                current <- { policy with TrustedSigners = Map.empty }
                                Ok { Executable = "/fixture/worker"; ExecutableDigest = "sha256:" + String('a', 64)
                                     WorkingDirectory = repo; Arguments = []; Environment = Map.empty
                                     Input = LocalAgentHandoffJson.renderPacket packet; TimeoutMilliseconds = 1000; MaxOutputBytes = 1024 } }
                      LocalJournaledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> refused
                      Assert.equal 1 selections
                      store.Load packet.RepositoryIdentity packet.DispatchId |> refused
                      current <- { policy with Revision = "revoked" }
                      LocalJournaledDispatch.run ports packet CancellationToken.None |> fun task -> task.GetAwaiter().GetResult() |> refused
                      Assert.equal 1 selections
                  finally Directory.Delete(directory, true)))
          t "unavailable host observations refuse without producing authority" (fun () ->
              withFixture (fun _ packet envelope _ _ ->
                  LocalWorkerDelegation.authority root packet.RepositoryIdentity (fun () -> Error "unavailable") (fun () -> Ok(LocalWorkerDelegation.render envelope)) (fun () -> now) packet () |> refused
                  LocalWorkerDelegation.authority root packet.RepositoryIdentity (fun () -> failwith "unavailable") (fun () -> failwith "must not read") (fun () -> now) packet () |> refused)) ]
