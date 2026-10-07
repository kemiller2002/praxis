namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Identity
open Praxis.Domain.Identity
open Praxis.Infrastructure.Identity

/// Repository, work-item and Praxis instance identity (PRAXIS-ID-01, 04,
/// 05, 06): the PRX-REMOTE-050 validation matrix, the instance identity
/// lifecycle of requirements/PRAXIS-DUAL-ENTRY-RECONCILIATION.md (DER-16..26)
/// and the DER-01 branch policy.
[<RequireQualifiedAccess>]
module IdentityTests =
    let private t name run = { Name = $"identity: {name}"; Run = run }

    let private textAt (path: string list) (node: JsonNode) =
        (path |> List.fold (fun (current: JsonNode) (key: string) -> current[key]) node).GetValue<string>()

    let private locator value = (RepositoryLocator.tryCreate value).Value
    let private local value = (LocalWorkItemId.tryCreate value).Value

    let private repo (id: string option) (name: string option) =
        RepositoryIdentity.create RepositoryProvider.github id (name |> Option.map locator) |> Result.defaultWith failwith

    let private praxis = repo (Some "100") (Some "echelon-foundry/praxis")
    let private vigila = repo (Some "200") (Some "echelon-foundry/vigila")
    let private item repository id = WorkItemIdentity.create repository (local id)

    /// Only the variables a test names exist: the test process may itself be
    /// running in GitHub Actions.
    let private environment (values: (string * string) list) = fun name -> values |> List.tryFind (fst >> (=) name) |> Option.map snd
    let private localEnvironment = environment []

    let private githubActions id name =
        environment [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", id; "GITHUB_REPOSITORY", name ]

    /// CLI runs see neither the test runner's GitHub variables nor its
    /// identity: `CliHarness` removes GITHUB_ACTIONS.
    let private ros root arguments = CliHarness.rosWith root [] arguments

    let private withRepository (remote: string option) (run: string -> unit) =
        CliPort.withDirectory "praxis-identity" (fun root ->
            CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            CliHarness.git root [ "config"; "user.name"; "Praxis Test" ] |> ignore
            CliHarness.git root [ "config"; "user.email"; "praxis@example.invalid" ] |> ignore
            remote |> Option.iter (fun url -> CliHarness.git root [ "remote"; "add"; "origin"; url ] |> ignore)
            File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":{"id":"legacy-name","type":"tooling"}}""" + "\n")
            run root)

    /// An initialized, committed installation (so `validate` has nothing
    /// else to report), with an optional `origin`.
    let private withInstallation (remote: string option) (run: string -> unit) =
        CliPort.withDirectory "praxis-identity-installed" (fun root ->
            CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
            CliHarness.git root [ "config"; "user.name"; "Praxis Test" ] |> ignore
            CliHarness.git root [ "config"; "user.email"; "praxis@example.invalid" ] |> ignore
            remote |> Option.iter (fun url -> CliHarness.git root [ "remote"; "add"; "origin"; url ] |> ignore)
            CliPort.exitCode 0 (ros root [ "init"; "--project"; "identity-fixture" ])
            CliHarness.optOutOfDurableCheckpoints root
            CliHarness.commitAll root "baseline"
            run root)

    let private setBranchPolicy root (policy: string) =
        let path = Path.Combine(root, "ros.json")
        let config = JsonNode.Parse(File.ReadAllText path)
        config["workProtocol"]["branchPolicy"] <- JsonValue.Create policy
        File.WriteAllText(path, config.ToJsonString())

    let private repositoryMatrix =
        [ t "the same local ID in two repositories is two canonical identities (PRX-REMOTE-050)" (fun () ->
              let left = item praxis "WI-0042"
              let right = item vigila "WI-0042"
              Assert.isTrue (left <> right) "the same local ID must not collide across repositories"
              Assert.equal (Some "github:100/WI-0042") (WorkItemIdentity.key left)
              Assert.equal (Some "github:200/WI-0042") (WorkItemIdentity.key right)
              Assert.empty (WorkItemIdentity.conflicts [ left; right ]))

          t "two repositories with the same name under different owners are different" (fun () ->
              let mine = repo (Some "1") (Some "alice/tools")
              let theirs = repo (Some "2") (Some "bob/tools")
              Assert.equal RepositoryMatch.Different (RepositoryIdentity.compare mine theirs)
              Assert.isTrue (WorkItemIdentity.key (item mine "T-1") <> WorkItemIdentity.key (item theirs "T-1")) "keys must differ")

          t "a rename keeps the identity; only the display locator changes" (fun () ->
              let renamed = RepositoryIdentity.withLocator (locator "echelon-foundry/praxis-core") praxis
              Assert.equal RepositoryMatch.Same (RepositoryIdentity.compare praxis renamed)
              Assert.equal (WorkItemIdentity.key (item praxis "WI-1")) (WorkItemIdentity.key (item renamed "WI-1"))
              Assert.equal "echelon-foundry/praxis-core:WI-1" (WorkItemIdentity.display (item renamed "WI-1")))

          t "a transfer to another owner keeps the identity" (fun () ->
              let transferred = repo (Some "100") (Some "new-owner/praxis")
              Assert.equal RepositoryMatch.Same (RepositoryIdentity.compare praxis transferred)

              match WorkItemReference.resolveIn transferred (WorkItemReference.Qualified(item praxis "WI-9")) with
              | Ok resolved -> Assert.equal "new-owner/praxis:WI-9" (WorkItemIdentity.display resolved)
              | Error failure -> failwith (IdentityFailure.message failure))

          t "a stable ID with a changed locator is verified against the observation" (fun () ->
              let observation =
                  { Provider = Some RepositoryProvider.github
                    ProviderId = Some "100"
                    Locator = Some(locator "someone-else/renamed")
                    Source = "github-actions" }

              match RepositoryIdentityStatus.assess (Some praxis) observation with
              | RepositoryIdentityStatus.Verified identity ->
                  Assert.equal (Some(locator "someone-else/renamed")) identity.Locator
                  Assert.equal (Some "github:100") (RepositoryIdentity.repositoryId identity)
              | other -> failwith $"expected verified, got {other}")

          t "a copied configuration is contradicted by the observed stable ID" (fun () ->
              let observation =
                  { Provider = Some RepositoryProvider.github
                    ProviderId = Some "999"
                    Locator = Some(locator "someone/template-copy")
                    Source = "github-actions" }

              let status = RepositoryIdentityStatus.assess (Some praxis) observation
              Assert.equal "contradicted" (RepositoryIdentityStatus.code status)
              Assert.equal None (RepositoryIdentityStatus.current status)
              Assert.isTrue (RepositoryIdentityStatus.findings status |> List.exists fst) "a contradicted identity is an error")

          t "a cross-repository reference resolves to its own repository" (fun () ->
              let catalog = [ praxis, [ local "WI-1" ]; vigila, [ local "VIG-15" ] ]

              match WorkItemReference.lookup catalog (WorkItemReference.Qualified(item (repo (Some "200") None) "VIG-15")) with
              | Ok resolved -> Assert.equal "echelon-foundry/vigila:VIG-15" (WorkItemIdentity.display resolved)
              | Error failure -> failwith (IdentityFailure.message failure))

          t "an unqualified ID resolves inside its repository context" (fun () ->
              match WorkItemReference.resolveIn praxis (WorkItemReference.Unqualified(local "WI-7")) with
              | Ok resolved -> Assert.equal (Some "github:100/WI-7") (WorkItemIdentity.key resolved)
              | Error failure -> failwith (IdentityFailure.message failure))

          t "an ambiguous unqualified lookup fails closed" (fun () ->
              let catalog = [ praxis, [ local "WI-1" ]; vigila, [ local "WI-1" ] ]

              match WorkItemReference.lookup catalog (WorkItemReference.Unqualified(local "WI-1")) with
              | Error(IdentityFailure.Ambiguous(id, repositories)) ->
                  Assert.equal "WI-1" id
                  Assert.equal 2 repositories.Length
              | other -> failwith $"expected ambiguity, got {other}")

          t "a reference to another repository fails closed in a single-repository context" (fun () ->
              match WorkItemReference.resolveIn praxis (WorkItemReference.Qualified(item vigila "VIG-1")) with
              | Error(IdentityFailure.ForeignRepository _) -> ()
              | other -> failwith $"expected a foreign-repository failure, got {other}")

          t "duplicate and conflicting canonical identities fail closed" (fun () ->
              Assert.equal 1 (WorkItemIdentity.conflicts [ item praxis "WI-1"; item praxis "WI-1" ]).Length
              let sameIdTwoLocators = repo (Some "100") (Some "other/name")
              Assert.isTrue (not (RepositoryIdentity.conflicts [ praxis; sameIdTwoLocators ]).IsEmpty) "one stable ID with two locators"
              let sameLocatorTwoIds = repo (Some "300") (Some "echelon-foundry/praxis")
              Assert.isTrue (not (RepositoryIdentity.conflicts [ praxis; sameLocatorTwoIds ]).IsEmpty) "one locator claimed twice"

              match WorkItemReference.lookup [ praxis, [ local "WI-1" ]; sameLocatorTwoIds, [] ] (WorkItemReference.Unqualified(local "WI-1")) with
              | Error(IdentityFailure.Conflicting _) -> ()
              | other -> failwith $"expected a conflict, got {other}")

          t "legacy identities are explicit, never guessed, and upgrade only with matching evidence (PRX-REMOTE-048)" (fun () ->
              let legacy = RepositoryIdentity.legacy RepositoryProvider.github (locator "echelon-foundry/praxis")
              Assert.isTrue (RepositoryIdentity.isLegacy legacy) "a locator-only identity is legacy"
              Assert.equal None (WorkItemIdentity.key (item legacy "WI-1"))
              Assert.equal (Ok praxis) (RepositoryIdentity.upgrade legacy praxis)
              Assert.isTrue (Result.isError (RepositoryIdentity.upgrade legacy vigila)) "a different repository is not an upgrade"
              let renamedLegacy = RepositoryIdentity.legacy RepositoryProvider.github (locator "old/name")
              Assert.isTrue (Result.isError (RepositoryIdentity.upgrade renamedLegacy praxis)) "a rename cannot be told from a copy without a stable ID"

              match WorkItemReference.resolveIn praxis (WorkItemReference.Qualified(item renamedLegacy "WI-1")) with
              | Error(IdentityFailure.UnverifiedRepository _) -> ()
              | other -> failwith $"a legacy reference to another locator must not be guessed: {other}")

          t "structured references round-trip without parsing the display string" (fun () ->
              let identity = item praxis "WI-0042"
              let node = IdentityJson.renderWorkItem identity
              Assert.equal "github:100" (node["repositoryId"].GetValue<string>())
              Assert.equal "echelon-foundry/praxis" (node["repository"].GetValue<string>())
              Assert.equal "WI-0042" (node["localId"].GetValue<string>())

              match IdentityJson.parseWorkItemReference None (JsonNode.Parse(node.ToJsonString())) with
              | Ok(WorkItemReference.Qualified parsed) -> Assert.equal identity parsed
              | other -> failwith $"round trip failed: {other}"

              match IdentityJson.parseWorkItemReference None (JsonValue.Create "echelon-foundry/praxis:WI-0042") with
              | Error message -> Assert.isTrue (message.Contains "structured" || message.Contains "object") message
              | other -> failwith $"a display string must not be parsed into identity: {other}"

              match IdentityJson.parseWorkItemReference None (JsonValue.Create "WI-0042") with
              | Ok(WorkItemReference.Unqualified id) -> Assert.equal "WI-0042" (LocalWorkItemId.value id)
              | other -> failwith $"a bare ID is unqualified: {other}"

              let legacy = IdentityJson.renderLocalWorkItem None "WI-1"
              Assert.isTrue (isNull legacy["repositoryId"]) "a reference without an established repository is explicitly legacy")

          t "Target.repositoryFromRemote reuses the identity module's URL grammar" (fun () ->
              for url in [ "https://github.com/echelon-foundry/praxis.git"; "git@github.com:echelon-foundry/praxis.git"; "http://proxy:1/git/echelon-foundry/praxis" ] do
                  Assert.equal (Some "echelon-foundry/praxis") (Praxis.Domain.Installation.Target.repositoryFromRemote url)
                  Assert.equal (Some(locator "echelon-foundry/praxis")) (RepositoryLocator.ofRemoteUrl url)) ]

    let private record id repository : InstanceRecord =
        { InstanceId = (InstanceId.tryCreate id).Value
          CreatedAt = Some(DateTimeOffset.Parse "2026-10-06T00:00:00Z")
          CreatedWith = "3.7.2"
          Repository = repository
          Predecessors = [] }

    let private instanceDomain =
        [ t "instance binding: a clone, rename or transfer is bound; a template or fork is foreign (DER-24)" (fun () ->
              let created = record "pxi-1" (Some praxis)
              Assert.equal InstanceBinding.Bound (InstanceBinding.assess created (Some praxis))
              Assert.equal InstanceBinding.Bound (InstanceBinding.assess created (Some(repo (Some "100") (Some "new-owner/renamed"))))

              match InstanceBinding.assess created (Some vigila) with
              | InstanceBinding.Foreign _ -> ()
              | other -> failwith $"a copy into another repository must be foreign, got {other}"

              Assert.equal None (LocalInstance.authoritativeId (LocalInstance.Present(created, InstanceBinding.Foreign(praxis, vigila))))
              Assert.isTrue (LocalInstance.findings (LocalInstance.Present(created, InstanceBinding.Foreign(praxis, vigila))) |> List.exists fst) "foreign is an error")

          t "instance init creates once, keeps on repeat, and reinitializes only with a reason and lineage" (fun () ->
              let now = DateTimeOffset.Parse "2026-10-06T12:00:00Z"
              let newId = InstanceId.ofGuid (Guid.Parse "00000000-0000-0000-0000-000000000001")

              match InstanceInit.decide LocalInstance.Missing newId now "3.7.2" (Some praxis) None with
              | InstanceInitDecision.Create created ->
                  Assert.equal "pxi-00000000000000000000000000000001" (InstanceId.value created.InstanceId)
                  Assert.equal (Some praxis) created.Repository
              | other -> failwith $"expected create, got {other}"

              let existing = LocalInstance.Present(record "pxi-old" (Some praxis), InstanceBinding.Bound)

              match InstanceInit.decide existing newId now "9.9.9" (Some praxis) None with
              | InstanceInitDecision.Keep kept -> Assert.equal "pxi-old" (InstanceId.value kept.InstanceId)
              | other -> failwith $"an existing identity must be kept, got {other}"

              match InstanceInit.decide existing newId now "3.7.2" (Some vigila) (Some "  ") with
              | InstanceInitDecision.Refuse _ -> ()
              | other -> failwith $"reinitialization needs a reason, got {other}"

              match InstanceInit.decide existing newId now "3.7.2" (Some vigila) (Some "created from a template") with
              | InstanceInitDecision.Create replaced ->
                  Assert.equal "pxi-old" (InstanceId.value replaced.Predecessors.Head.InstanceId)
                  Assert.equal "created from a template" replaced.Predecessors.Head.Reason
              | other -> failwith $"expected a reinitialization, got {other}"

              match InstanceInit.decide (LocalInstance.Unreadable "bad") newId now "3.7.2" None None with
              | InstanceInitDecision.Refuse _ -> ()
              | other -> failwith $"an unreadable record is never overwritten, got {other}")

          t "the instance record round-trips and a malformed one is a typed failure" (fun () ->
              let original = { record "pxi-roundtrip" (Some praxis) with Predecessors = [ { InstanceId = (InstanceId.tryCreate "pxi-before").Value; Reason = "fork"; ReplacedAt = DateTimeOffset.Parse "2026-10-05T00:00:00Z" } ] }
              Assert.equal (Ok original) (IdentityJson.parseInstance (IdentityJson.renderInstance original))
              Assert.isTrue (Result.isError (IdentityJson.parseInstance "{not json")) "malformed JSON"
              Assert.isTrue (Result.isError (IdentityJson.parseInstance """{"schema":"praxis.instance/9","instanceId":"pxi-1"}""")) "unknown schema"

              match IdentityJson.parseInstance """{"instanceId":"pxi-legacy"}""" with
              | Ok legacy -> Assert.equal None legacy.CreatedAt
              | Error message -> failwith message)

          t "branch policy: meaningful work must run on a branch named for a work item (DER-01)" (fun () ->
              let paths = [ "src/a.fs" ]
              Assert.equal None (BranchPolicy.check BranchPolicy.Unrestricted (Some "feature") paths [ "WI-1" ])
              Assert.equal None (BranchPolicy.check BranchPolicy.WorkItemId (Some "WI-1") paths [ "WI-1" ])
              Assert.equal None (BranchPolicy.check BranchPolicy.WorkItemId (Some "feature") [] [ "WI-1" ])
              Assert.isTrue (BranchPolicy.check BranchPolicy.WorkItemId (Some "feature") paths [ "WI-1" ]).IsSome "a non-work-item branch is rejected"
              Assert.isTrue (BranchPolicy.check BranchPolicy.WorkItemId None paths [ "WI-1" ]).IsSome "an unknown branch is not a pass"
              Assert.equal (Some BranchPolicy.WorkItemId) (BranchPolicy.tryParse "work-item-id")
              Assert.equal None (BranchPolicy.tryParse "loose")) ]

    let private store =
        [ t "the store creates the identity before registration, binds it, and keeps it on repeat" (fun () ->
              withRepository (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  let env = githubActions "100" "echelon-foundry/praxis"
                  let now = DateTimeOffset.Parse "2026-10-06T12:00:00Z"
                  let first = FileInstanceIdentityStore.ensureWith env root (InstanceId.ofGuid (Guid.NewGuid())) now "3.7.2" None

                  match first, FileInstanceIdentityStore.localWith env root with
                  | Ok(InstanceInitDecision.Create created), LocalInstance.Present(stored, InstanceBinding.Bound) ->
                      Assert.equal created stored
                      Assert.equal (Some "github:100") (stored.Repository |> Option.bind RepositoryIdentity.repositoryId)
                  | other -> failwith $"expected a bound new identity, got {other}"

                  let before = File.ReadAllText(Path.Combine(root, ".praxis", "instance.json"))
                  let second = FileInstanceIdentityStore.ensureWith env root (InstanceId.ofGuid (Guid.NewGuid())) now "9.9.9" None
                  Assert.isTrue (match second with Ok(InstanceInitDecision.Keep _) -> true | _ -> false) "a repeat keeps the identity"
                  Assert.equal before (File.ReadAllText(Path.Combine(root, ".praxis", "instance.json")))

                  // A template or fork: the same files, another repository.
                  match FileInstanceIdentityStore.localWith (githubActions "555" "someone/copy") root with
                  | LocalInstance.Present(_, InstanceBinding.Foreign _) -> ()
                  | other -> failwith $"a copy must be foreign, got {other}"))

          t "the store reports a malformed record as unreadable and never overwrites it" (fun () ->
              withRepository None (fun root ->
                  Directory.CreateDirectory(Path.Combine(root, ".praxis")) |> ignore
                  File.WriteAllText(Path.Combine(root, ".praxis", "instance.json"), "{broken")

                  match FileInstanceIdentityStore.localWith localEnvironment root with
                  | LocalInstance.Unreadable _ -> ()
                  | other -> failwith $"expected unreadable, got {other}"

                  Assert.isTrue (Result.isError (FileInstanceIdentityStore.ensureWith localEnvironment root (InstanceId.ofGuid (Guid.NewGuid())) DateTimeOffset.UtcNow "3.7.2" None)) "refused"
                  Assert.equal "{broken" (File.ReadAllText(Path.Combine(root, ".praxis", "instance.json")))))

          t "repository identity is recorded beside the legacy name and verified from the environment" (fun () ->
              withRepository (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  match FileRepositoryIdentityRepository.status localEnvironment root with
                  | Ok(RepositoryIdentityStatus.NotEstablished(Some legacy)) -> Assert.isTrue (RepositoryIdentity.isLegacy legacy) "derived identity is legacy"
                  | other -> failwith $"expected not established, got {other}"

                  FileRepositoryIdentityRepository.writeConfigured root praxis |> Result.defaultWith failwith
                  Assert.equal (Some "legacy-name") (FileRepositoryIdentityRepository.readLegacyName root)
                  Assert.equal (Ok(Some praxis)) (FileRepositoryIdentityRepository.readConfigured root)
                  Assert.equal "unverified" (FileRepositoryIdentityRepository.status localEnvironment root |> Result.map RepositoryIdentityStatus.code |> Result.defaultValue "")
                  Assert.equal "verified" (FileRepositoryIdentityRepository.status (githubActions "100" "echelon-foundry/praxis") root |> Result.map RepositoryIdentityStatus.code |> Result.defaultValue "")
                  Assert.equal "contradicted" (FileRepositoryIdentityRepository.status (githubActions "7" "someone/fork") root |> Result.map RepositoryIdentityStatus.code |> Result.defaultValue "")))

          t "the projection is allow-listed: no credentials, environment or local paths (DER-25)" (fun () ->
              withRepository (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  FileRepositoryIdentityRepository.writeConfigured root praxis |> Result.defaultWith failwith
                  FileInstanceIdentityStore.ensureWith localEnvironment root (InstanceId.ofGuid (Guid.NewGuid())) DateTimeOffset.UtcNow "3.7.2" None |> ignore
                  Directory.CreateDirectory(Path.Combine(root, ".echelon")) |> ignore
                  File.WriteAllText(Path.Combine(root, ".echelon", "administration.json"), """{"schema":"echelon.administration/v1","provider":"x","token":"ghp_""" + String('s', 36) + "\"}")
                  Environment.SetEnvironmentVariable("PRAXIS_IDENTITY_TEST_SECRET", "sk-" + String('z', 30))

                  try
                      match FileInstanceProjection.buildWith localEnvironment root "3.7.2" with
                      | Error message -> failwith message
                      | Ok projection ->
                          let text = (IdentityJson.renderProjection projection).ToJsonString()
                          for forbidden in [ "ghp_"; "sk-"; root; "token"; "PRAXIS_IDENTITY_TEST_SECRET" ] do
                              Assert.isTrue (not (forbidden.Length > 0 && text.Contains forbidden)) $"projection leaked '{forbidden}'"
                          let node = JsonNode.Parse text
                          Assert.equal "praxis.instance-projection/1" (node["schema"].GetValue<string>())
                          Assert.equal "1.0" (textAt [ "protocols"; "reconciliation" ] node)
                          Assert.equal "unknown" (textAt [ "integrations"; "vigila" ] node)
                          Assert.equal (InstanceProjection.operationId projection) (InstanceProjection.operationId projection)
                  finally
                      Environment.SetEnvironmentVariable("PRAXIS_IDENTITY_TEST_SECRET", null))) ]

    let private cli =
        [ t "cli: init creates the instance identity and upgrade preserves it (DER-16, 17, 24)" (fun () ->
              CliPort.withDirectory "praxis-identity-init" (fun root ->
                  CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                  CliPort.exitCode 0 (ros root [ "init" ])
                  let path = Path.Combine(root, ".praxis", "instance.json")
                  Assert.isTrue (File.Exists path) "init must create .praxis/instance.json"
                  let created = File.ReadAllText path
                  CliPort.exitCode 0 (ros root [ "upgrade" ])
                  CliPort.exitCode 0 (ros root [ "init" ])
                  Assert.equal created (File.ReadAllText path)
                  let shown = ros root [ "instance"; "--json" ]
                  CliPort.exitCode 0 shown
                  Assert.equal ((IdentityJson.parseInstance created |> Result.defaultWith failwith).InstanceId |> InstanceId.value) (textAt [ "instanceId" ] (CliPort.parse shown.Out))))

          t "cli: upgrade gives an installation that predates instance identity one" (fun () ->
              CliPort.withDirectory "praxis-identity-legacy" (fun root ->
                  CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                  CliPort.exitCode 0 (ros root [ "init" ])
                  File.Delete(Path.Combine(root, ".praxis", "instance.json"))
                  let validated = ros root [ "validate" ]
                  CliPort.contains "instance identity" validated.Err
                  CliPort.exitCode 0 (ros root [ "upgrade" ])
                  Assert.isTrue (File.Exists(Path.Combine(root, ".praxis", "instance.json"))) "upgrade must create the missing identity"))

          t "cli: a foreign instance is refused by validate until an explicit reinitialization" (fun () ->
              withInstallation (Some "https://github.com/someone/copy.git") (fun root ->
                  File.WriteAllText(Path.Combine(root, ".praxis", "instance.json"), IdentityJson.renderInstance (record "pxi-original" (Some praxis)))
                  CliHarness.commitAll root "copied from a template"
                  let env = [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", "555"; "GITHUB_REPOSITORY", "someone/copy" ]
                  let validated = CliHarness.rosWith root env [ "validate" ]
                  CliPort.exitCode 1 validated
                  CliPort.contains "DER-24" validated.Err
                  CliPort.exitCode 2 (CliHarness.rosWith root env [ "instance"; "init"; "--reinitialize" ])
                  CliPort.exitCode 0 (CliHarness.rosWith root env [ "instance"; "init"; "--reinitialize"; "--reason"; "created from a template" ])
                  let replaced = IdentityJson.parseInstance (File.ReadAllText(Path.Combine(root, ".praxis", "instance.json"))) |> Result.defaultWith failwith
                  Assert.equal "pxi-original" (InstanceId.value replaced.Predecessors.Head.InstanceId)
                  Assert.equal (Some "github:555") (replaced.Repository |> Option.bind RepositoryIdentity.repositoryId)
                  CliHarness.commitAll root "reinitialize the instance identity"
                  CliPort.exitCode 0 (CliHarness.rosWith root env [ "validate" ])))

          t "cli: repository identity set records the identity and validate rejects a copied one" (fun () ->
              withInstallation (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  CliPort.exitCode 0 (ros root [ "repository"; "identity"; "set"; "--provider"; "github"; "--provider-id"; "100" ])
                  let shown = ros root [ "repository"; "identity"; "--json" ]
                  CliPort.exitCode 0 shown
                  let node = CliPort.parse shown.Out
                  Assert.equal "github:100" (textAt [ "identity"; "repositoryId" ] node)
                  Assert.equal "echelon-foundry/praxis" (textAt [ "identity"; "repository" ] node)
                  let env = [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", "999"; "GITHUB_REPOSITORY", "someone/fork" ]
                  let validated = CliHarness.rosWith root env [ "validate" ]
                  CliPort.exitCode 1 validated
                  CliPort.contains "copied from another repository" validated.Err
                  CliPort.exitCode 0 (CliHarness.rosWith root [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", "100"; "GITHUB_REPOSITORY", "echelon-foundry/praxis" ] [ "validate" ])))

          t "cli: instance register is optional, idempotent and changes nothing local when unavailable (DER-18, 19)" (fun () ->
              withRepository (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  CliPort.exitCode 0 (ros root [ "instance"; "init" ])
                  let before = File.ReadAllText(Path.Combine(root, ".praxis", "instance.json"))
                  let first = ros root [ "instance"; "register"; "--json"; "--actor-kind"; "agent"; "--agent"; "example/agent" ]
                  let second = ros root [ "instance"; "register"; "--json"; "--actor-kind"; "agent"; "--agent"; "example/agent" ]
                  CliPort.exitCode 0 first
                  CliPort.exitCode 0 second
                  CliPort.contains "unavailable" first.Out
                  let operation (run: CliHarness.Run) = textAt [ "request"; "operationId" ] (CliPort.parse run.Out)
                  Assert.equal (operation first) (operation second)
                  Assert.isTrue ((operation first).StartsWith "praxis-instance-") "the operation ID is the projection digest"
                  Assert.equal before (File.ReadAllText(Path.Combine(root, ".praxis", "instance.json")))
                  let projection = ros root [ "instance"; "projection"; "--json" ]
                  CliPort.exitCode 0 projection
                  Assert.equal "praxis.instance-projection/1" (textAt [ "schema" ] (CliPort.parse projection.Out))))

          t "cli: native executions carry the local instance identity, never a foreign one (DER-22, 24)" (fun () ->
              withInstallation (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  // Created where the stable repository ID is observable, so a
                  // copy can be told from a rename.
                  File.Delete(Path.Combine(root, ".praxis", "instance.json"))
                  CliPort.exitCode 0 (CliHarness.rosWith root [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", "100"; "GITHUB_REPOSITORY", "echelon-foundry/praxis" ] [ "instance"; "init" ])
                  CliHarness.commitAll root "instance identity created in CI"
                  let localId = (FileInstanceIdentityStore.readRecord root |> Result.defaultWith failwith).Value.InstanceId |> InstanceId.value
                  let identity = [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "example/agent" ]

                  let start id (extra: (string * string) list) =
                      CliPort.exitCode 0 (ros root [ "work"; "capture"; "--id"; id; "--title"; id; "--occurred-at"; "2026-10-06T12:00:00.000Z" ])
                      CliPort.exitCode 0 (ros root [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; "2026-10-06T12:00:01.000Z" ])
                      CliPort.exitCode 0 (CliHarness.rosWith root (identity @ extra) [ "work"; "start"; "--id"; id; "--occurred-at"; "2026-10-06T12:00:02.000Z" ])

                      Directory.GetFiles(Path.Combine(root, ".ros", "telemetry", "executions"), "*.json")
                      |> Array.map (File.ReadAllText >> JsonNode.Parse)
                      |> Array.find (fun record -> record["workItemId"].GetValue<string>() = id)

                  let native = start "WI-0101" []
                  Assert.equal localId (native["instanceId"].GetValue<string>())
                  // The canonical work item, structurally (PRX-REMOTE-047): the
                  // repository identity is not established here, so it is legacy.
                  Assert.equal "echelon-foundry/praxis" (textAt [ "workItem"; "repository" ] native)
                  Assert.equal "WI-0101" (textAt [ "workItem"; "localId" ] native)
                  Assert.isTrue (isNull (native["workItem"].AsObject()["repositoryId"])) "a legacy reference has no repositoryId"
                  let copied = start "WI-0102" [ "GITHUB_ACTIONS", "true"; "GITHUB_REPOSITORY_ID", "555"; "GITHUB_REPOSITORY", "someone/copy" ]
                  Assert.isTrue (isNull copied["instanceId"]) "a foreign instance identity must not be stamped"))

          t "cli: validate enforces workProtocol.branchPolicy on meaningful work (DER-01)" (fun () ->
              withInstallation None (fun root ->
                  setBranchPolicy root "work-item-id"
                  CliHarness.commitAll root "adopt the branch policy"
                  let clean = ros root [ "validate" ]
                  Assert.isTrue (not (clean.Err.Contains "DER-01")) $"no meaningful change, no finding: {clean.Err}"
                  File.WriteAllText(Path.Combine(root, "change.txt"), "work\n")
                  let rejected = ros root [ "validate" ]
                  CliPort.exitCode 1 rejected
                  CliPort.contains "DER-01" rejected.Err
                  CliHarness.git root [ "checkout"; "-q"; "-b"; "WI-0001" ] |> ignore
                  CliPort.exitCode 0 (ros root [ "work"; "capture"; "--id"; "WI-0001"; "--title"; "Branch policy"; "--occurred-at"; "2026-10-06T12:00:00.000Z" ])
                  CliPort.exitCode 0 (ros root [ "work"; "backlog-transition"; "--id"; "WI-0001"; "--action"; "ready"; "--occurred-at"; "2026-10-06T12:00:01.000Z" ])
                  CliPort.exitCode 0 (CliHarness.rosWith root [ "PRAXIS_ACTOR_KIND", "agent"; "PRAXIS_ACTOR", "example/agent" ] [ "work"; "start"; "--id"; "WI-0001"; "--occurred-at"; "2026-10-06T12:00:02.000Z" ])
                  let accepted = ros root [ "validate" ]
                  Assert.isTrue (not (accepted.Err.Contains "DER-01")) $"the work-item branch must pass the branch rule: {accepted.Err}"
                  let pullRequest = CliHarness.rosWith root [ "GITHUB_ACTIONS", "true"; "GITHUB_HEAD_REF", "feature/elsewhere" ] [ "validate" ]
                  CliPort.contains "feature/elsewhere" pullRequest.Err
                  setBranchPolicy root "loose"
                  let invalid = ros root [ "validate" ]
                  CliPort.exitCode 1 invalid
                  CliPort.contains "branchPolicy" invalid.Err

                  setBranchPolicy root "none"
                  Assert.isTrue (not ((ros root [ "validate" ]).Err.Contains "DER-01")) "the policy off produces no finding")) ]

    let private envelope instanceClaim =
        let claim =
            match instanceClaim with
            | Some id -> $"\"praxisInstanceId\":\"{id}\","
            | None -> ""

        fun (baseCommit: string) (transaction: string) ->
            $"""{{"schemaVersion":"1.0","transactionId":"{transaction}","workItem":"WI-0064","branch":"WI-0064","baseCommit":"{baseCommit}",{claim}"agent":{{"kind":"agent","id":"openai/codex","provider":"openai","model":"unknown","runtime":"codex"}},"timeline":[{{"sequence":1,"timestamp":"2026-09-26T10:00:00Z","action":"start"}}],"requests":[{{"type":"work.start","occurredAt":"2026-09-26T10:00:00Z"}}]}}"""

    let private reconciliation =
        [ t "reconciliation rejects a claimed instance that differs from the generated local one, end to end (DER-23)" (fun () ->
              CliPort.withDirectory "praxis-identity-envelope" (fun root ->
                  CliHarness.git root [ "init"; "-q"; "-b"; "WI-0064" ] |> ignore
                  CliHarness.git root [ "config"; "user.name"; "Praxis Test" ] |> ignore
                  CliHarness.git root [ "config"; "user.email"; "praxis@example.invalid" ] |> ignore
                  File.WriteAllText(Path.Combine(root, "ros.json"), """{"repository":"test-repository","telemetry":{"enabled":true},"workProtocol":{"completionEvidence":{"default":[],"research":[]}}}""")
                  CliPort.exitCode 0 (ros root [ "instance"; "init" ])
                  CliHarness.git root [ "add"; "-A" ] |> ignore
                  CliHarness.git root [ "commit"; "-qm"; "baseline with instance identity" ] |> ignore
                  let baseCommit = CliHarness.git root [ "rev-parse"; "HEAD" ]
                  let localId = (FileInstanceIdentityStore.readRecord root |> Result.defaultWith failwith).Value.InstanceId |> InstanceId.value

                  let inbox = Path.Combine(root, ".praxis", "inbox", "documents")
                  Directory.CreateDirectory inbox |> ignore
                  let spoofed = Path.Combine(inbox, "tx-spoof.json")
                  File.WriteAllText(spoofed, envelope (Some "pxi-spoofed") baseCommit "tx-spoof")
                  let rejected = ros root [ "reconcile"; "--envelope"; spoofed ]
                  CliPort.exitCode 2 rejected
                  CliPort.contains "instance-identity-mismatch" rejected.Err

                  let matching = Path.Combine(inbox, "tx-match.json")
                  File.WriteAllText(matching, envelope (Some localId) baseCommit "tx-match")
                  let applied = ros root [ "reconcile"; "--envelope"; matching ]
                  CliPort.exitCode 0 applied
                  CliPort.contains "APPLIED tx-match" applied.Out)) ]


    // ---- remote protocol 1.4 (PRAXIS-ID-02, PRX-REMOTE-047) ----

    let private sha = "59b4e032818a4c765886e48c117595dc58019d43"

    let private remoteRequest (version: string) (operation: string) (arguments: string) =
        $"""{{"protocol":"praxis.remote","protocolVersion":"{version}","requestId":"req-identity-0001","operation":"{operation}","repository":{{"ref":"refs/heads/main","expectedSha":"{sha}"}},"actor":{{"kind":"agent","id":"example/agent"}},"arguments":{arguments}}}"""

    let private parseRemote text =
        Praxis.Contracts.Remote.RemoteJson.parseRequest Praxis.Domain.Remote.ProtocolVersion.current text

    let private qualified = """{"repositoryId":"github:100","repository":"echelon-foundry/praxis","localId":"WI-0100"}"""

    let private trusted repository : Praxis.Domain.Remote.TrustedContext =
        { Principal = "principal:test"
          Grants = set Praxis.Domain.Remote.Capability.all
          ObservedRef = Some "refs/heads/main"
          ObservedSha = Some sha
          Repository = repository }

    let private remote =
        [ t "planner documents carry the repository their work-item IDs belong to (PRX-PLAN-182)" (fun () ->
              withInstallation (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  CliPort.exitCode 0 (ros root [ "repository"; "identity"; "set"; "--provider"; "github"; "--provider-id"; "100" ])
                  let analysis = ros root [ "plan"; "analyze"; "--json" ]
                  CliPort.exitCode 0 analysis
                  let node = CliPort.parse analysis.Out
                  Assert.equal "github:100" (textAt [ "repository"; "repositoryId" ] node)
                  Assert.equal "echelon-foundry/praxis" (textAt [ "repository"; "repository" ] node)))

          t "remote 1.4: praxis.describe advertises the governed repository and the reference shape" (fun () ->
              withInstallation (Some "https://github.com/echelon-foundry/praxis.git") (fun root ->
                  CliPort.exitCode 0 (ros root [ "repository"; "identity"; "set"; "--provider"; "github"; "--provider-id"; "100" ])
                  let described = ros root [ "remote"; "describe" ]
                  CliPort.exitCode 0 described
                  let node = CliPort.parse described.Out
                  Assert.equal "github:100" (textAt [ "repositoryIdentity"; "repositoryId" ] node)
                  Assert.equal "1.4" (textAt [ "workItemReference"; "introducedIn" ] node)))

          t "remote 1.4: a structured reference parses to its local ID and records its repository" (fun () ->
              match parseRemote (remoteRequest "1.4" "work.start" $"""{{"workItemIds":[{qualified},"WI-0101"]}}""") with
              | Ok request ->
                  match request.Arguments with
                  | Praxis.Domain.Remote.Arguments.WorkStart start -> Assert.equal [ "WI-0100"; "WI-0101" ] start.WorkItemIds
                  | other -> failwith $"unexpected {other}"

                  let scope = Assert.single request.WorkItemScopes
                  Assert.equal "arguments.workItemIds[0]" scope.Field
                  Assert.equal (Some "github:100") (RepositoryIdentity.repositoryId scope.Repository)
              | Error failure -> failwith $"{failure.Failure.Problems}")

          t "remote 1.4: structured references inside a batch are normalised too" (fun () ->
              let batch = $"""{{"requests":[{{"requestId":"req-identity-0002","operation":"work.resume","arguments":{{"workItemIds":[{qualified}]}}}}]}}"""

              match parseRemote (remoteRequest "1.4" "batch" batch) with
              | Ok request -> Assert.equal "arguments.requests[0].arguments.workItemIds[0]" (Assert.single request.WorkItemScopes).Field
              | Error failure -> failwith $"{failure.Failure.Problems}")

          t "remote 1.4: a 1.3 request may not use a structured reference" (fun () ->
              match parseRemote (remoteRequest "1.3" "work.start" $"""{{"workItemIds":[{qualified}]}}""") with
              | Error failure ->
                  Assert.equal Praxis.Domain.Remote.FailureCode.InvalidRequest failure.Failure.Code
                  Assert.isTrue (failure.Failure.Problems |> List.exists (fun problem -> problem.Message.Contains "1.4")) "names the version"
              | Ok _ -> failwith "a 1.3 request with a structured reference must be refused")

          t "remote 1.4: a display string is not accepted as a qualified reference" (fun () ->
              match parseRemote (remoteRequest "1.4" "work.context" "{\"workItemId\":\"echelon-foundry/praxis:WI-1\"}") with
              | Error failure -> Assert.equal Praxis.Domain.Remote.FailureCode.InvalidRequest failure.Failure.Code
              | Ok request ->
                  Assert.empty request.WorkItemScopes

                  match Praxis.Domain.Remote.RequestDecision.decide (trusted (Some praxis)) Praxis.Domain.Remote.JournalLookup.NotRecorded request with
                  | Praxis.Domain.Remote.Decision.Reject failure -> Assert.equal Praxis.Domain.Remote.FailureCode.InvalidRequest failure.Code
                  | other -> failwith $"a display string must not be parsed into identity: {other}")

          t "remote 1.4: a reference to another or unverifiable repository is refused before anything runs" (fun () ->
              let request =
                  match parseRemote (remoteRequest "1.4" "work.start" $"""{{"workItemIds":[{qualified}]}}""") with
                  | Ok request -> request
                  | Error failure -> failwith $"{failure.Failure.Problems}"

              let decide repository = Praxis.Domain.Remote.RequestDecision.decide (trusted repository) Praxis.Domain.Remote.JournalLookup.NotRecorded request

              Assert.equal Praxis.Domain.Remote.Decision.Execute (decide (Some praxis))
              Assert.equal Praxis.Domain.Remote.Decision.Execute (decide (Some(RepositoryIdentity.withLocator (locator "renamed/praxis") praxis)))

              for repository in [ Some vigila; None; Some(RepositoryIdentity.legacy RepositoryProvider.github (locator "other/repo")) ] do
                  match decide repository with
                  | Praxis.Domain.Remote.Decision.Reject failure -> Assert.equal Praxis.Domain.Remote.FailureCode.InvalidRequest failure.Code
                  | other -> failwith $"expected a refusal for {repository}, got {other}")

          t "remote 1.4: bare-ID requests fingerprint exactly as before; structured ones add their repository" (fun () ->
              let parsedOk text =
                  match parseRemote text with
                  | Ok request -> request
                  | Error failure -> failwith $"{failure.Failure.Problems}"

              let old = parsedOk (remoteRequest "1.3" "work.start" "{\"workItemIds\":[\"WI-0100\"]}")
              let bare = parsedOk (remoteRequest "1.4" "work.start" "{\"workItemIds\":[\"WI-0100\"]}")
              let structured = parsedOk (remoteRequest "1.4" "work.start" $"""{{"workItemIds":[{qualified}]}}""")
              Assert.empty old.WorkItemScopes
              Assert.isTrue (not ((Praxis.Domain.Remote.RequestFingerprint.canonical old).Contains "scope")) "a 1.3 encoding is unchanged"
              Assert.equal (Praxis.Domain.Remote.RequestFingerprint.compute old) (Praxis.Domain.Remote.RequestFingerprint.compute bare)
              Assert.isTrue (Praxis.Domain.Remote.RequestFingerprint.compute bare <> Praxis.Domain.Remote.RequestFingerprint.compute structured) "the repository is part of the intent")

          t "remote 1.4: responses carry the governed repository and the canonical work items; 1.3 responses keep their shape" (fun () ->
              let request version =
                  match parseRemote (remoteRequest version "work.start" "{\"workItemIds\":[\"WI-0100\"]}") with
                  | Ok request -> request
                  | Error failure -> failwith $"{failure.Failure.Problems}"

              let render version =
                  let response = { Praxis.Domain.Remote.Response.succeeded "3.7.2" (request version) (Some sha) None with RepositoryIdentity = Some praxis }
                  JsonNode.Parse(Praxis.Contracts.Remote.RemoteJson.renderResponse response)

              let current = render "1.4"
              Assert.equal "github:100" (textAt [ "repository"; "identity"; "repositoryId" ] current)
              Assert.equal "github:100" (textAt [ "repositoryId" ] (current["workItems"].AsArray()[0]))
              Assert.equal "WI-0100" (textAt [ "localId" ] (current["workItems"].AsArray()[0]))
              let earlier = render "1.3"
              Assert.isTrue (isNull earlier["workItems"]) "a 1.3 response has no workItems"
              Assert.isTrue (isNull (earlier["repository"].AsObject()["identity"])) "a 1.3 response has no repository identity") ]

    let tests = repositoryMatrix @ instanceDomain @ store @ cli @ reconciliation @ remote
