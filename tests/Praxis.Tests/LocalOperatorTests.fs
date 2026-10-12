namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// Host configuration, private-key-free operator tooling and grant auditing.
[<RequireQualifiedAccess>]
module LocalOperatorTests =
    let private t name run = { Name = "local operator: " + name; Run = run }
    let private ok = function Ok value -> value | Error error -> failwith (sprintf "%A" error)
    let private refused result = Assert.isTrue (Result.isError result) "unsafe operator input accepted"
    let private sha = "sha256:" + String('a', 64)

    let private command (arguments: string list) =
        let node = JsonObject()
        node["executable"] <- JsonValue.Create(Environment.ProcessPath)
        node["sha256"] <- JsonValue.Create sha
        node["arguments"] <- JsonArray(arguments |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)
        node["timeoutMilliseconds"] <- JsonValue.Create 1000
        node["maxOutputBytes"] <- JsonValue.Create 1000
        node

    let private validator id tests arguments =
        let node = JsonObject()
        node["id"] <- JsonValue.Create(id: string)
        node["tests"] <- JsonValue.Create(tests: bool)
        node["command"] <- command arguments
        node

    let private withConfiguration test =
        let root = CliHarness.temporaryDirectory "praxis-local-host-config"
        try
            let repository = Path.Combine(root, "repo")
            Directory.CreateDirectory repository |> ignore
            let roots = JsonObject()
            for name in [ "policy"; "journal"; "spool"; "submissions"; "integration"; "evidence"; "checkouts" ] do
                let path = Path.Combine(root, name)
                Directory.CreateDirectory path |> ignore
                roots[name] <- JsonValue.Create path
            let node = JsonObject()
            node["schemaVersion"] <- JsonValue.Create LocalHostConfiguration.Schema
            node["repositoryRoot"] <- JsonValue.Create repository
            node["repositoryIdentity"] <- JsonValue.Create "repo-42"
            node["fileAccess"] <- JsonValue.Create "portable-development"
            node["roots"] <- roots
            node["git"] <- JsonNode.Parse(sprintf """{"executable":%s,"sha256":"%s"}""" (Text.Json.JsonSerializer.Serialize Environment.ProcessPath) sha)
            node["committer"] <- JsonNode.Parse """{"name":"controller","email":"controller@example.invalid"}"""
            node["validatorPolicyRevision"] <- JsonValue.Create "validators-1"
            let system = JsonObject()
            system["build"] <- validator "build" false [ "{candidate}" ]
            system["tests"] <- validator "tests" true [ "{candidate}"; "{trx}" ]
            system["architecture"] <- validator "architecture" false []
            system["scope"] <- validator "scope" false []
            node["systemValidators"] <- system
            node["acceptanceValidators"] <- JsonArray([| validator "accept" false [ "{candidate}" ] :> JsonNode |])
            let worker = JsonObject()
            worker["adapterId"] <- JsonValue.Create "bridge"
            worker["workerId"] <- JsonValue.Create "worker-1"
            worker["roles"] <- JsonNode.Parse """["implementation"]"""
            worker["operatingSystems"] <- JsonNode.Parse """["linux","macos","windows"]"""
            worker["command"] <- command [ "{worktree}" ]
            node["workers"] <- JsonArray([| worker :> JsonNode |])
            node["completionEvidence"] <- JsonNode.Parse """{"implementation":"member-changes"}"""
            let parse (value: JsonNode) = LocalHostConfiguration.parse (Path.Combine(root, "config.json")) (Encoding.UTF8.GetBytes(value.ToJsonString()))
            test root (node :> JsonNode) parse
        finally CliHarness.removeTemporaryGitDirectory root

    let private changed (node: JsonNode) (edit: JsonNode -> unit) =
        let copy = node.DeepClone()
        edit copy
        copy

    let tests =
        [ t "host configuration is strict, outside the repository and token-safe" (fun () ->
              withConfiguration (fun root node parse ->
                  let parsed = parse node |> ok
                  Assert.equal 4 parsed.SystemValidators.Count
                  Assert.equal [ "worker-1" ] (parsed.Workers |> List.map _.WorkerId)
                  for edit in
                      [ (fun (copy: JsonNode) -> copy["unknown"] <- JsonValue.Create true)
                        (fun copy -> copy.AsObject().Remove "committer" |> ignore)
                        (fun copy -> copy["roots"].["evidence"] <- JsonValue.Create(Path.Combine(root, "repo")))
                        (fun copy -> copy["roots"].["spool"] <- JsonValue.Create(Path.Combine(root, "journal")))
                        (fun copy -> copy["fileAccess"] <- JsonValue.Create "trust-me")
                        (fun copy -> copy["systemValidators"].["build"] <- validator "build" false [ "{trx}" ])
                        (fun copy -> copy["systemValidators"].["tests"] <- validator "tests" true [ "{candidate}" ])
                        (fun copy -> copy["systemValidators"].["tests"] <- validator "tests" false [ "{candidate}" ])
                        (fun copy -> copy["acceptanceValidators"] <- JsonArray([| validator "accept" false [ "{secret}" ] :> JsonNode |]))
                        (fun copy -> copy["acceptanceValidators"] <- JsonArray([| validator "build" false [] :> JsonNode |]))
                        (fun copy -> copy["workers"].[0].["command"].["maxOutputBytes"] <- JsonValue.Create 70000)
                        (fun copy -> copy["workers"].[0].["command"].["sha256"] <- JsonValue.Create "unpinned")
                        (fun copy -> copy["completionEvidence"] <- JsonNode.Parse """{"tests":"worker-claims"}""") ] do
                      parse (changed node edit) |> refused))
          t "external DER and P1363 signatures normalize and only public keys provision" (fun () ->
              use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
              let payload = Encoding.UTF8.GetBytes "exact payload"
              let der = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
              let normalized = LocalOperator.normalizeSignature der |> ok
              Assert.equal 64 normalized.Length
              Assert.isTrue (key.VerifyData(payload, normalized, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) "normalized signature does not verify"
              Assert.equal normalized (LocalOperator.normalizeSignature normalized |> ok)
              LocalOperator.normalizeSignature [| 0x30uy; 0x02uy; 0x01uy |] |> refused
              LocalOperator.normalizeSignature (Array.append der [| 0uy |]) |> refused
              Assert.equal (key.ExportSubjectPublicKeyInfoPem()) (LocalOperator.canonicalPublicKey (key.ExportSubjectPublicKeyInfoPem()) |> ok)
              LocalOperator.canonicalPublicKey (key.ExportPkcs8PrivateKeyPem()) |> refused
              use other = ECDsa.Create(ECCurve.NamedCurves.nistP384)
              LocalOperator.canonicalPublicKey (other.ExportSubjectPublicKeyInfoPem()) |> refused)
          t "worker bridge prompt carries only the anchored requirement section and allowed files" (fun () ->
              let root = CliHarness.temporaryDirectory "praxis-bridge-prompt"
              try
                  CliHarness.write root "docs/req.md" "# Requirements\n\n## R-A\nslugify text\n\n## R-B\nword count\n"
                  let packet =
                      { LocalAgentHandoffTests.packet with
                          Members = [ { LocalAgentHandoffTests.packet.Members.Head with RequirementKeys = [ "docs/req.md#R-A" ] } ]
                          AllowedPaths = [ "src/text_tools.py" ] }
                  let text = LocalWorkerBridge.prompt { Git = "git"; Backend = LocalBridgeBackend.Command("agent", []); Worktree = root } packet true
                  Assert.isTrue (text.Contains "slugify text") text
                  Assert.isTrue (not (text.Contains "word count")) "an unrelated requirement leaked into the prompt"
                  Assert.isTrue (text.Contains "src/text_tools.py (does not exist yet)") text
                  Assert.isTrue (text.Contains "do not run praxis") text
              finally CliHarness.removeTemporaryGitDirectory root)
          t "grants are audited additions from trusted signers and never replace a group" (fun () ->
              LocalWorkerDelegationTests.withFixture (fun policy packet _ _ key ->
                  let root = CliHarness.temporaryDirectory "praxis-local-grant"
                  try
                      let repository = Path.Combine(root, "repo")
                      let host = Path.Combine(root, "policy")
                      Directory.CreateDirectory repository |> ignore
                      Directory.CreateDirectory host |> ignore
                      let store = FileLocalEcirAuthority.createPortableDevelopment { RepositoryRoot = repository; HostRoot = host; LockTimeout = TimeSpan.FromMilliseconds 100. } (fun () -> DateTimeOffset.UtcNow)
                      let initial = { policy with RepositoryRoot = repository; Grants = [] }
                      store.Initialize initial |> ok
                      let grant = { policy.Grants.Head with GroupId = "GROUP-NEW-001" }
                      let audit revision keyId : EcirHostAudit =
                          { Id = "AUD-" + revision; Action = "granted"; Approver = "operator-1"; KeyId = keyId; GroupId = Some grant.GroupId
                            PreviousRevision = initial.Revision; NewRevision = revision; OccurredAt = DateTimeOffset.UtcNow
                            ReceiptSha256 = Some(EcirDispatchTransaction.hashText grant.ReceiptJson) }
                      store.Apply(initial.Revision, { initial with Revision = "rev-x"; Grants = [ grant ] }, audit "rev-x" "unknown-key") |> refused
                      store.Apply(initial.Revision, { initial with Revision = "rev-y"; Grants = [ { grant with ReceiptJson = "{}" } ] }, audit "rev-y" "key-1") |> refused
                      store.Apply(initial.Revision, { initial with Revision = "rev-2"; Grants = [ grant ] }, audit "rev-2" "key-1") |> ok
                      let current = store.Load() |> ok
                      Assert.equal [ "GROUP-NEW-001" ] (current.Grants |> List.map _.GroupId)
                      let again = { audit "rev-3" "key-1" with PreviousRevision = "rev-2" }
                      store.Apply("rev-2", { current with Revision = "rev-3"; Grants = current.Grants @ [ grant ] }, again) |> refused
                      Assert.equal [ "granted" ] (store.Audit() |> ok |> List.map _.Action)
                  finally CliHarness.removeTemporaryGitDirectory root)) ]
