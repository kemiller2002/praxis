namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module EcirHostTests =
    let private t name run = { Name = "ECIR host: " + name; Run = run }
    let private now = DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero)
    let private sha c = "sha256:" + String(c, 64)
    let private source =
        { Commit = String('a', 40); ManifestPath = "manifest.json"; BlueprintPath = "blueprint.json"; SourceManifestDigest = sha 'b' }
    let private grant =
        { GroupId = "GROUP-1"; CohortId = "COHORT-1"; Source = source
          BlueprintDigest = sha 'c'; Ordo = { Executable = "/host/ordo"; Sha256 = sha 'd' }
          MemberRequirements = Map.ofList [ "WI-1", "R-1" ]; ReceiptJson = "" }
    let private policy root =
        { Revision = "rev-1"; RepositoryRoot = root; QualifiedRelease = true
          TrustedSigners = Map.empty; Grants = [ grant ] }
    let private docs =
        { Commit = source.Commit; Manifest = "{}"; SourceManifestDigest = source.SourceManifestDigest
          Blueprint = """{"nodes":[{"id":"COHORT-1","kind":"cohort","requirementKeys":["R-1"]},{"id":"DEC-1","kind":"decision","requirementKeys":["R-1"]}],"requirements":[{"source":{"key":"R-1"},"disposition":{"kind":"modeled"}}]}""" }
    let private validator =
        { BlueprintDigest = grant.BlueprintDigest; SourceRequirements = 1; RepresentedRequirements = 1
          ModeledRequirements = 1; UnresolvedRequirements = 0; DeferredRequirements = 0
          ValidatedByExecutableSha256 = grant.Ordo.Sha256 }

    let private withPolicy run =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        let scope =
            { GroupId = grant.GroupId; CohortId = grant.CohortId; SourceCommit = source.Commit
              ManifestDigest = source.SourceManifestDigest; BlueprintDigest = grant.BlueprintDigest
              RequirementKeys = [ "R-1" ]; DecisionIds = [ "DEC-1" ] }
        let receipt =
            { SchemaVersion = EcirApprovals.SchemaVersion; KeyId = "owner-1"; Approver = "owner"
              IssuedAt = "2026-10-10T08:00:00Z"; ExpiresAt = "2026-10-10T10:00:00Z"
              Scope = scope; Signature = "" }
        let signature = key.SignData(EcirApprovals.signingPayload receipt, HashAlgorithmName.SHA256) |> Convert.ToBase64String
        let json =
            JsonSerializer.Serialize
                {| schemaVersion = receipt.SchemaVersion; keyId = receipt.KeyId; approver = receipt.Approver
                   issuedAt = receipt.IssuedAt; expiresAt = receipt.ExpiresAt; signature = signature
                   scope = {| groupId = scope.GroupId; cohortId = scope.CohortId; sourceCommit = scope.SourceCommit
                              manifestDigest = scope.ManifestDigest; blueprintDigest = scope.BlueprintDigest
                              requirementKeys = scope.RequirementKeys; decisionIds = scope.DecisionIds |} |}
        let p =
            { policy (Path.GetTempPath()) with
                TrustedSigners = Map.ofList [ receipt.KeyId, { Approver = receipt.Approver; PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() } ]
                Grants = [ { grant with ReceiptJson = json } ] }
        run p key

    let private withStore run =
        let directory = Path.Combine(Path.GetTempPath(), "praxis-ecir-store-" + Guid.NewGuid().ToString("N"))
        let root = Path.Combine(directory, "repo")
        Directory.CreateDirectory root |> ignore
        let journal = Path.Combine(directory, "host", "pending.json")
        let writes =
            [ ".ros/context/current.json"; ".ros/events/events.jsonl"; ".ros/work/groups.json"; ".ros/work/ecir-dispatches/receipt-1.json" ]
            |> List.map (fun path -> { Path = path; BeforeSha256 = None; Content = "accepted:" + path })
        try run root journal writes
        finally Directory.Delete(directory, true)

    let tests =
        [ t "host grant refuses different repository, duplicate grants and changed membership" (fun () ->
              let root = Path.GetTempPath()
              let p = policy root
              Assert.isTrue (EcirHostPolicy.resolve root "GROUP-1" [ "WI-1" ] p |> Result.isOk) "exact host grant rejected"
              for candidate in [ { p with QualifiedRelease = false }; { p with Revision = "" }; { p with Grants = [ grant; grant ] };
                                 { p with RepositoryRoot = Path.Combine(root, "other") } ] do
                  Assert.isTrue (EcirHostPolicy.resolve root "GROUP-1" [ "WI-1" ] candidate |> Result.isError) "invalid host policy accepted"
              for members in [ []; [ "WI-1"; "WI-2" ]; [ "WI-1"; "WI-1" ] ] do
                  Assert.isTrue (EcirHostPolicy.resolve root "GROUP-1" members p |> Result.isError) "changed members accepted")

          t "source and validator release pins remain mandatory after validation" (fun () ->
              withPolicy (fun p _ ->
                  Assert.isTrue (EcirHostPolicy.verifyAfterValidation p p.Grants.Head [ "R-1" ] docs validator now |> Result.isOk) "valid fixture rejected"
                  for changed in [ { validator with BlueprintDigest = sha 'e' }; { validator with ValidatedByExecutableSha256 = sha 'e' } ] do
                      Assert.isTrue (EcirHostPolicy.verifyAfterValidation p p.Grants.Head [ "R-1" ] docs changed now |> Result.isError) "changed validator pin accepted"
                  Assert.isTrue (EcirHostPolicy.verifyAfterValidation p p.Grants.Head [ "R-1" ] { docs with Commit = String('f', 40) } validator now |> Result.isError) "changed source accepted"))

          t "dispatch rechecks policy, membership, expiry and host CAS before any commit" (fun () ->
              withPolicy (fun p _ ->
                  for failure in [ "none"; "revoked"; "membership"; "expired"; "cas"; "stage" ] do
                      let mutable staged = false
                      let mutable committed = false
                      let mutable loads = 0
                      let mutable times = 0
                      let authority =
                          { Load = fun () ->
                                loads <- loads + 1
                                Ok(if staged && failure = "revoked" then { p with TrustedSigners = Map.empty } else p)
                            UtcNow = fun () ->
                                times <- times + 1
                                if staged && failure = "expired" then now.AddHours 1. else now
                            Commit = fun revision expires commit ->
                                Assert.equal p.Revision revision
                                Assert.isTrue (expires > now) "expiry omitted from host commit"
                                if failure = "cas" then Error "host revision changed" else commit() }
                      let result =
                          EcirHostPolicy.dispatch authority p.RepositoryRoot "GROUP-1"
                              (fun () -> Ok(if staged && failure = "membership" then [ "WI-2" ] else [ "WI-1" ]))
                              (fun _ -> Ok(docs, validator))
                              (fun _ ->
                                  staged <- true
                                  if failure = "stage" then Error "stage refused"
                                  else Ok(fun () -> committed <- true; Ok()))
                      Assert.equal (failure = "none") (Result.isOk result)
                      Assert.equal (failure = "none") committed
                      if failure = "none" then
                          Assert.equal 2 loads
                          Assert.equal 2 times))

          t "approval expiring during validation never reaches staging" (fun () ->
              withPolicy (fun p _ ->
                  let mutable time = now
                  let mutable staged = false
                  let authority =
                      { Load = fun () -> Ok p
                        UtcNow = fun () -> time
                        Commit = fun _ _ commit -> commit() }
                  let result =
                      EcirHostPolicy.dispatch authority p.RepositoryRoot "GROUP-1" (fun () -> Ok [ "WI-1" ])
                          (fun _ -> time <- now.AddHours 1.; Ok(docs, validator))
                          (fun _ -> staged <- true; Ok(fun () -> Ok()))
                  Assert.isTrue (Result.isError result && not staged) "expired authorization reached stage"))

          t "crash at every state-write boundary recovers without repeating member start" (fun () ->
              for boundary in 0 .. 3 do
                  withStore (fun root journal writes ->
                      Assert.equal (Ok()) (EcirDispatchTransaction.prepare root journal writes)
                      let interrupted = EcirDispatchTransaction.recover root journal (fun index -> if index = boundary then failwith "simulated crash")
                      Assert.isTrue (Result.isError interrupted && File.Exists journal) "crash did not retain journal"
                      Assert.equal (Ok()) (EcirDispatchTransaction.recover root journal ignore)
                      Assert.equal (Ok()) (EcirDispatchTransaction.recover root journal ignore)
                      for write in writes do Assert.equal write.Content (File.ReadAllText(Path.Combine(root, write.Path)))))

          t "late target conflict is detected before any earlier write" (fun () ->
              withStore (fun root journal writes ->
                  Assert.equal (Ok()) (EcirDispatchTransaction.prepare root journal writes)
                  let last = Path.Combine(root, writes[3].Path)
                  Directory.CreateDirectory(Path.GetDirectoryName last) |> ignore
                  File.WriteAllText(last, "someone else's change")
                  Assert.isTrue (EcirDispatchTransaction.recover root journal ignore |> Result.isError) "conflict was overwritten"
                  Assert.isTrue (not (File.Exists(Path.Combine(root, writes.Head.Path)))) "earlier state was partially written"))

          t "incomplete, duplicate, traversing and repo-owned journal writes are refused" (fun () ->
              withStore (fun root journal writes ->
                  let invalid =
                      [ writes.Tail; writes @ [ writes.Head ]
                        writes |> List.map (fun w -> if w.Path = ".ros/work/groups.json" then { w with Path = "../escape.json" } else w)
                        writes |> List.map (fun w -> if w.Path = ".ros/work/groups.json" then { w with Path = ".ros/work/../work/groups.json" } else w) ]
                  for candidate in invalid do
                      Assert.isTrue (EcirDispatchTransaction.prepare root journal candidate |> Result.isError) "unsafe writes accepted"
                  Assert.isTrue (EcirDispatchTransaction.prepare root (Path.Combine(root, "pending.json")) writes |> Result.isError) "repo-owned host journal accepted"))

          t "changed content and wrong repository invalidate recovery" (fun () ->
              withStore (fun root journal writes ->
                  Assert.equal (Ok()) (EcirDispatchTransaction.prepare root journal writes)
                  let original = File.ReadAllText journal
                  File.WriteAllText(journal, original.Replace("accepted:", "tampered:"))
                  Assert.isTrue (EcirDispatchTransaction.recover root journal ignore |> Result.isError) "tampered journal replayed"
                  File.WriteAllText(journal, original)
                  Assert.isTrue (EcirDispatchTransaction.recover (Path.GetDirectoryName root) journal ignore |> Result.isError) "journal replayed into different repository"))

          t "concurrent preparation never replaces the accepted host journal" (fun () ->
              withStore (fun root journal writes ->
                  Directory.CreateDirectory(Path.GetDirectoryName journal) |> ignore
                  use barrier = new Barrier(2)
                  let attempt candidate = Task.Run(fun () ->
                      barrier.SignalAndWait(TimeSpan.FromSeconds 5.) |> ignore
                      EcirDispatchTransaction.prepare root journal candidate)
                  let alternate = writes |> List.map (fun w -> { w with Content = "alternate:" + w.Path })
                  let first = attempt writes
                  let second = attempt alternate
                  Task.WaitAll [| first :> Task; second :> Task |]
                  Assert.equal 1 ([ first.Result; second.Result ] |> List.filter Result.isOk |> List.length)
                  Assert.equal (Ok()) (EcirDispatchTransaction.recover root journal ignore)
                  let accepted = if Result.isOk first.Result then writes else alternate
                  for write in accepted do Assert.equal write.Content (File.ReadAllText(Path.Combine(root, write.Path)))))

          t "dangling links and pre-existing target changes are refused" (fun () ->
              withStore (fun root journal writes ->
                  let context = Path.Combine(root, writes.Head.Path)
                  Directory.CreateDirectory(Path.GetDirectoryName context) |> ignore
                  File.WriteAllText(context, "new state")
                  Assert.isTrue (EcirDispatchTransaction.prepare root journal writes |> Result.isError) "changed target accepted"
                  File.Delete context
                  if not (OperatingSystem.IsWindows()) then
                      File.CreateSymbolicLink(context, Path.Combine(root, "missing.json")) |> ignore
                      Assert.isTrue (EcirDispatchTransaction.prepare root journal writes |> Result.isError) "dangling symlink accepted"))

          t "issuance and revocation commit exact receipt and audit through host CAS" (fun () ->
              withPolicy (fun initial key ->
                  let mutable current = initial
                  let mutable audits: EcirHostAudit list = []
                  let mutable authorized = true
                  let host =
                      { Load = fun () -> Ok current
                        AuthenticatedApprover = fun () -> Ok "owner"
                        UtcNow = fun () -> now
                        Sign = fun _ payload -> Ok(key.SignData(payload, HashAlgorithmName.SHA256))
                        CanRevoke = fun _ _ -> authorized
                        Apply = fun expected updated record ->
                            if current.Revision <> expected then Error "CAS refused"
                            else
                                Assert.equal expected record.PreviousRevision
                                Assert.equal updated.Revision record.NewRevision
                                current <- updated
                                audits <- audits @ [ record ]
                                Ok() }
                  let issued = EcirHostAdministration.issue host current.RepositoryRoot "GROUP-1" [ "WI-1" ] "owner-1" (TimeSpan.FromHours 2.) (fun _ -> Ok(docs, validator))
                  Assert.isTrue (Result.isOk issued) "host issuance failed"
                  Assert.equal "issued" audits.Head.Action
                  Assert.isTrue (audits.Head.ReceiptSha256.IsSome) "issued audit omitted receipt identity"
                  Assert.isTrue (EcirHostPolicy.verifyAfterValidation current current.Grants.Head [ "R-1" ] docs validator now |> Result.isOk) "issued receipt did not verify"
                  authorized <- false
                  Assert.isTrue (EcirHostAdministration.revoke host "owner-1" |> Result.isError) "unauthorized revocation succeeded"
                  Assert.equal 1 audits.Length
                  authorized <- true
                  Assert.equal (Ok()) (EcirHostAdministration.revoke host "owner-1")
                  Assert.equal "revoked" audits[1].Action
                  Assert.isTrue (EcirHostPolicy.verifyAfterValidation current current.Grants.Head [ "R-1" ] docs validator now |> Result.isError) "revoked key still authorized"
                  Assert.isTrue (EcirHostAdministration.revoke host "owner-1" |> Result.isError) "duplicate revocation mutated host state"
                  Assert.equal 2 audits.Length))

          t "stages real native member and group effects, then recovers their exact telemetry" (fun () ->
              withPolicy (fun p _ ->
                  let parent = GitFixture.temporaryDirectory "ecir-native-stage"
                  try
                      let _, clone = PraxisCli.installedRepository parent "repo"
                      let executor = PraxisCli.agent "example/ecir-stage" "example" "stage" "stage-session"
                      let actor =
                          { Kind = ActorKind.Agent; Id = executor.Id; Provider = Some executor.Provider
                            Model = Some "unknown"; Runtime = Some executor.Runtime }
                      let cli root args = PraxisCli.run root (Some executor) args |> PraxisCli.ok |> ignore
                      cli clone [ "add"; "Native fixture"; "--id"; "WI-1" ]
                      cli clone [ "work"; "backlog-transition"; "--id"; "WI-1"; "--action"; "ready"; "--occurred-at"; PraxisCli.now() ]
                      cli clone [ "work"; "group"; "create"; "--group"; "GROUP-1"; "--member"; "WI-1"; "--occurred-at"; PraxisCli.now() ]
                      PraxisCli.pushAll clone "native ECIR staging fixture" |> ignore
                      let facts, _, repository = FileGroupExecution.facts clone None "GROUP-1" actor |> Result.defaultWith failwith
                      let occurredAt = PraxisCli.now()
                      let request =
                          { GroupId = "GROUP-1"; OccurredAt = occurredAt; Actor = actor; Member = Some "WI-1"
                            Mode = Some(ExecutionMode.Independent, "native staging fixture")
                            IndependentMembers = []; NewExecutionId = FileGroupExecution.newIdentifier "GROUP-1" occurredAt actor
                            Repository = repository }
                      let plan = GroupExecutions.decide request facts |> Result.defaultWith (fun errors -> failwithf "%A" errors)
                      let approval = EcirHostPolicy.verifyAfterValidation p p.Grants.Head [ "R-1" ] docs validator now |> Result.defaultWith failwith
                      let evidence = { PolicyRevision = p.Revision; VerifiedAt = now; Approval = approval; Validator = validator }
                      let begun stage =
                          let result = PraxisCli.run stage (Some executor) [ "work"; "begin"; "--id"; "WI-1"; "--occurred-at"; occurredAt ]
                          if result.ExitCode = 0 then Ok() else Error result.Error
                      let record stage executionId =
                          FileWorkGroupRepository.readStore stage
                          |> Result.bind (fun store ->
                              let group = WorkGroups.tryFind store.Groups "GROUP-1" |> Option.get
                              let updated = GroupExecutions.record request group plan (Some executionId)
                              FileWorkGroupRepository.writeStore stage { store with Groups = WorkGroups.upsert store.Groups updated })
                      use staged = EcirNativeStage.prepare clone "WI-1" evidence begun record |> Result.defaultWith failwith
                      Assert.equal None (FileGroupExecution.memberExecution clone "WI-1")
                      Assert.equal "" (GitFixture.git clone [ "status"; "--porcelain" ])
                      Assert.isTrue (staged.Writes |> List.exists (fun write -> write.Path.StartsWith(".ros/telemetry/executions/"))) "native member telemetry omitted"
                      Assert.isTrue (staged.Writes |> List.exists (fun write -> write.Path.StartsWith(".ros/executions/"))) "native execution envelope omitted"
                      let journal = Path.Combine(parent, "host", "native.json")
                      Assert.equal (Ok()) (EcirDispatchTransaction.prepare clone journal staged.Writes)
                      Assert.equal (Ok()) (EcirDispatchTransaction.recover clone journal ignore)
                      let executionId = FileGroupExecution.memberExecution clone "WI-1" |> Option.get
                      let group = FileWorkGroupRepository.read clone |> Result.defaultWith failwith |> fun groups -> WorkGroups.tryFind groups "GROUP-1" |> Option.get
                      Assert.equal executionId group.Executions.Head.Members.Head.ExecutionId
                      Assert.equal "feature/x" (GitFixture.git clone [ "branch"; "--show-current" ])
                      Assert.equal (Ok()) (EcirDispatchTransaction.recover clone journal ignore)
                  finally GitFixture.cleanup parent))

          t "failed audit persistence and wrong authenticated issuer never publish approval" (fun () ->
              withPolicy (fun initial key ->
                  let mutable writes = 0
                  let baseHost =
                      { Load = fun () -> Ok initial
                        AuthenticatedApprover = fun () -> Ok "owner"
                        UtcNow = fun () -> now
                        Sign = fun _ payload -> Ok(key.SignData(payload, HashAlgorithmName.SHA256))
                        CanRevoke = fun _ _ -> true
                        Apply = fun _ _ _ -> writes <- writes + 1; Error "audit persistence failed" }
                  let issue host = EcirHostAdministration.issue host initial.RepositoryRoot "GROUP-1" [ "WI-1" ] "owner-1" (TimeSpan.FromHours 2.) (fun _ -> Ok(docs, validator))
                  Assert.isTrue (issue baseHost |> Result.isError) "receipt returned despite missing audit"
                  Assert.equal 1 writes
                  Assert.isTrue (issue { baseHost with AuthenticatedApprover = fun () -> Ok "agent" } |> Result.isError) "wrong issuer accepted"
                  Assert.isTrue (issue { baseHost with Sign = fun _ _ -> Ok(Array.zeroCreate<byte> 64) } |> Result.isError) "invalid signer output accepted"
                  Assert.equal 1 writes)) ]
