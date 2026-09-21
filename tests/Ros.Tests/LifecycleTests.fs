namespace Ros.Tests

open Ros.Domain.Lifecycle

/// Pure lifecycle tests. Everything here runs against `ObservedRepository`
/// values rather than a filesystem, which is the point of keeping planning
/// pure: the ownership rules, the migration chain and the state machine are
/// all decidable without touching a disk.
[<RequireQualifiedAccess>]
module LifecycleTests =
    let private entry path ownership sha =
        { Path = path
          Ownership = ownership
          Sha256 = sha
          Executable = false
          Integration = None }

    let private observed files recorded =
        { ObservedRepository.empty with
            Files = Map.ofList files
            Directories = Set.ofList [ ".echelon" ]
            ConfigurationPresent = true
            Manifest =
                match recorded with
                | None -> None
                | Some artifacts ->
                    Some
                        { SchemaVersion = 1
                          Tool = "ros"
                          Package = "pkg"
                          InstalledVersion = "1.0.0"
                          ConfigurationVersion = Migration.CurrentConfigurationVersion
                          Profile = "greenfield"
                          ManagedArtifacts = artifacts } }

    let private recordedArtifact path ownership sha =
        { Path = path
          Ownership = ownership
          Sha256 = sha }

    let private planFor payload state =
        (Planning.initialize "greenfield" "pkg" "1.0.0" payload state).Plan

    let private changesFor payload state =
        planFor payload state
        |> fun plan -> plan.Changes |> List.filter (fun change -> (PlannedChange.kind change).EndsWith "file")

    let tests =
        [ { Name = "every ownership round-trips through its wire form"
            Run =
              fun () ->
                  for ownership in [ Ownership.ToolOwned; Ownership.Generated; Ownership.UserOwned; Ownership.Shared ] do
                      Assert.equal (Some ownership) (Ownership.parse (Ownership.toString ownership))

                  Assert.equal None (Ownership.parse "something-else") }

          { Name = "only tool-owned files have their integrity asserted"
            Run =
              fun () ->
                  Assert.isTrue (Ownership.integrityChecked Ownership.ToolOwned) "tool-owned must be integrity checked"

                  for ownership in [ Ownership.Generated; Ownership.UserOwned; Ownership.Shared ] do
                      Assert.isTrue
                          (not (Ownership.integrityChecked ownership))
                          $"{Ownership.toString ownership} must not be integrity checked" }

          { Name = "an absent file is created whatever its ownership"
            Run =
              fun () ->
                  for ownership in [ Ownership.ToolOwned; Ownership.Generated; Ownership.UserOwned; Ownership.Shared ] do
                      let payload = [ entry "a.md" ownership "aaa" ]

                      match changesFor payload (observed [] None) |> Assert.single with
                      | PlannedChange.CreateFile("a.md", created, "aaa") -> Assert.equal ownership created
                      | other -> failwith $"Expected a create for {Ownership.toString ownership}, got {other}" }

          { Name = "a file already byte-identical to the payload plans no change"
            Run =
              fun () ->
                  let payload = [ entry "a.md" Ownership.ToolOwned "aaa" ]
                  let state = observed [ "a.md", "aaa" ] (Some [ recordedArtifact "a.md" Ownership.ToolOwned "aaa" ])
                  let plan = planFor payload state
                  Assert.empty (changesFor payload state)
                  Assert.equal [ "a.md" ] plan.Preserved }

          { Name = "user-owned, shared and generated files that differ are preserved, never rewritten"
            Run =
              fun () ->
                  for ownership in [ Ownership.UserOwned; Ownership.Shared; Ownership.Generated ] do
                      let payload = [ entry "a.md" ownership "new" ]
                      let state = observed [ "a.md", "localedit" ] (Some [ recordedArtifact "a.md" ownership "seed" ])
                      let plan = planFor payload state
                      Assert.empty (changesFor payload state)
                      Assert.empty plan.Conflicts
                      Assert.equal [ "a.md" ] plan.Preserved }

          { Name = "a tool-owned file untouched since installation is replaced"
            Run =
              fun () ->
                  let payload = [ entry "a.md" Ownership.ToolOwned "new" ]
                  let state = observed [ "a.md", "old" ] (Some [ recordedArtifact "a.md" Ownership.ToolOwned "old" ])

                  match changesFor payload state |> Assert.single with
                  | PlannedChange.UpdateManagedFile("a.md", "old", "new") -> ()
                  | other -> failwith $"Expected an update, got {other}" }

          { Name = "a locally modified tool-owned file blocks instead of being overwritten"
            Run =
              fun () ->
                  let payload = [ entry "a.md" Ownership.ToolOwned "new" ]
                  let state = observed [ "a.md", "mine" ] (Some [ recordedArtifact "a.md" Ownership.ToolOwned "old" ])
                  let plan = planFor payload state
                  Assert.empty (changesFor payload state)
                  Assert.equal (Conflict.LocallyModifiedToolFile "a.md") (Assert.single plan.Conflicts)

                  match Plan.validate plan with
                  | Ok _ -> failwith "a blocking conflict must fail validation"
                  | Error conflicts -> Assert.equal 1 conflicts.Length }

          { Name = "a legacy recorded tool-owned file can upgrade when it still matches its old snapshot"
            Run =
              fun () ->
                  let payload = [ entry "tool.md" Ownership.ToolOwned "current" ]
                  let legacy =
                      { SchemaVersion = 1
                        Tool = "ros"
                        Package = "pkg"
                        InstalledVersion = "2.0.0"
                        ConfigurationVersion = Migration.LegacyConfigurationVersion
                        Profile = "greenfield"
                        ManagedArtifacts = [ recordedArtifact "tool.md" Ownership.ToolOwned "old" ] }

                  let state =
                      { observed [ "tool.md", "old" ] None with
                          LegacyManifest = Some legacy
                          LegacyManifestPresent = true }

                  let steps = Migration.path Migration.LegacyConfigurationVersion |> Result.defaultValue []
                  let plan = (Planning.upgrade "greenfield" "pkg" "3.1.2" payload state steps).Plan

                  match plan.Changes |> List.tryFind (function | PlannedChange.UpdateManagedFile("tool.md", _, _) -> true | _ -> false) with
                  | Some(PlannedChange.UpdateManagedFile("tool.md", "old", "current")) -> ()
                  | other -> failwith $"Expected legacy-owned update, got {other}"

                  Assert.empty (Plan.blockingConflicts plan) }

          { Name = "a legacy tool-owned file edited after installation still blocks upgrade"
            Run =
              fun () ->
                  let payload = [ entry "tool.md" Ownership.ToolOwned "current" ]
                  let legacy =
                      { SchemaVersion = 1
                        Tool = "ros"
                        Package = "pkg"
                        InstalledVersion = "2.0.0"
                        ConfigurationVersion = Migration.LegacyConfigurationVersion
                        Profile = "greenfield"
                        ManagedArtifacts = [ recordedArtifact "tool.md" Ownership.ToolOwned "old" ] }

                  let state =
                      { observed [ "tool.md", "local-edit" ] None with
                          LegacyManifest = Some legacy
                          LegacyManifestPresent = true }

                  let steps = Migration.path Migration.LegacyConfigurationVersion |> Result.defaultValue []
                  let plan = (Planning.upgrade "greenfield" "pkg" "3.1.2" payload state steps).Plan
                  Assert.equal (Conflict.LocallyModifiedToolFile "tool.md") (Assert.single (Plan.blockingConflicts plan)) }

          { Name = "an unrecorded file in the way of a tool-owned file blocks installation"
            Run =
              fun () ->
                  let payload = [ entry "a.md" Ownership.ToolOwned "new" ]
                  let plan = planFor payload (observed [ "a.md", "theirs" ] None)
                  Assert.equal (Conflict.UnmanagedFileInTheWay "a.md") (Assert.single plan.Conflicts) }

          { Name = "planning is idempotent: re-planning against a plan's own result asks for nothing"
            Run =
              fun () ->
                  let payload =
                      [ entry "tool.md" Ownership.ToolOwned "t1"
                        entry "user.md" Ownership.UserOwned "u1"
                        entry "shared.md" Ownership.Shared "s1"
                        entry "gen.json" Ownership.Generated "g1" ]

                  let first = Planning.initialize "greenfield" "pkg" "1.0.0" payload (observed [] None)
                  Assert.equal 4 (changesFor payload (observed [] None)).Length

                  // The repository as it stands once that plan has run.
                  let after =
                      { observed (payload |> List.map (fun e -> e.Path, e.Sha256)) None with
                          Manifest = Some first.Manifest }

                  let second = Planning.initialize "greenfield" "pkg" "1.0.0" payload after
                  Assert.isTrue (Plan.isNoOp second.Plan) $"second plan should be empty but had {second.Plan.Changes}"
                  Assert.equal first.Manifest second.Manifest }

          { Name = "the manifest records a preserved file at the hash it actually has"
            Run =
              fun () ->
                  let payload = [ entry "user.md" Ownership.UserOwned "packaged" ]
                  let installation = Planning.initialize "greenfield" "pkg" "1.0.0" payload (observed [ "user.md", "theirs" ] None)
                  let artifact = Assert.single installation.Manifest.ManagedArtifacts
                  Assert.equal "theirs" artifact.Sha256
                  Assert.equal Ownership.UserOwned artifact.Ownership }

          { Name = "shared ownership can adopt a previously tool-owned file without overwriting sibling capability regions"
            Run =
              fun () ->
                  let payload = [ entry "AGENTS.md" Ownership.Shared "ros-seed" ]
                  let state =
                      observed
                          [ "AGENTS.md", "ros-seed-plus-sibling-managed-regions" ]
                          (Some [ recordedArtifact "AGENTS.md" Ownership.ToolOwned "ros-seed" ])

                  let installation = Planning.initialize "greenfield" "pkg" "3.1.1" payload state
                  Assert.empty (Plan.blockingConflicts installation.Plan)
                  Assert.equal [ "AGENTS.md" ] installation.Plan.Preserved

                  let artifact = Assert.single installation.Manifest.ManagedArtifacts
                  Assert.equal Ownership.Shared artifact.Ownership
                  Assert.equal "ros-seed-plus-sibling-managed-regions" artifact.Sha256 }

          { Name = "an installation already at the current configuration version needs no migration"
            Run = fun () -> Assert.empty (Migration.path Migration.CurrentConfigurationVersion |> Result.defaultValue [ Unchecked.defaultof<_> ]) }

          { Name = "a legacy installation migrates through one declared step"
            Run =
              fun () ->
                  match Migration.path Migration.LegacyConfigurationVersion with
                  | Error message -> failwith message
                  | Ok steps ->
                      let step = Assert.single steps
                      Assert.equal Migration.LegacyConfigurationVersion step.FromVersion
                      Assert.equal Migration.CurrentConfigurationVersion step.ToVersion }

          { Name = "a configuration version newer than this CLI is an error, not a silent no-op"
            Run =
              fun () ->
                  match Migration.path (Migration.CurrentConfigurationVersion + 1) with
                  | Ok steps -> failwith $"Expected an error but got {steps.Length} step(s)"
                  | Error message -> Assert.isTrue (message.Contains "newer than this CLI supports") message }

          { Name = "an empty repository is not installed rather than invalid"
            Run =
              fun () ->
                  let state = { ObservedRepository.empty with ConfigurationProblem = Some(InstallationProblem.ConfigurationMissing "ros.json") }
                  Assert.equal InstallationState.NotInstalled (Planning.installationState "2.0.0" state) }

          { Name = "a legacy installation reports as one upgrade behind"
            Run =
              fun () ->
                  let state = { ObservedRepository.empty with LegacyManifestPresent = true; ConfigurationPresent = true }

                  match Planning.installationState "2.0.0" state with
                  | InstallationState.UpgradeRequired(InstalledVersion "legacy", AvailableVersion "2.0.0") -> ()
                  | other -> failwith $"Expected upgrade-required, got {other}" }

          { Name = "a current installation reports as installed, an older one as upgradable"
            Run =
              fun () ->
                  let state = observed [ "a.md", "aaa" ] (Some [ recordedArtifact "a.md" Ownership.ToolOwned "aaa" ])
                  Assert.equal (InstallationState.Installed(InstalledVersion "1.0.0")) (Planning.installationState "1.0.0" state)

                  match Planning.installationState "2.0.0" state with
                  | InstallationState.UpgradeRequired(InstalledVersion "1.0.0", AvailableVersion "2.0.0") -> ()
                  | other -> failwith $"Expected upgrade-required, got {other}" }

          { Name = "a missing or modified tool-owned artifact makes the installation invalid"
            Run =
              fun () ->
                  let recorded = Some [ recordedArtifact "a.md" Ownership.ToolOwned "aaa" ]

                  match Planning.installationState "1.0.0" (observed [] recorded) with
                  | InstallationState.Invalid [ InstallationProblem.ManagedArtifactMissing "a.md" ] -> ()
                  | other -> failwith $"Expected a missing-artifact problem, got {other}"

                  match Planning.installationState "1.0.0" (observed [ "a.md", "changed" ] recorded) with
                  | InstallationState.Invalid [ InstallationProblem.ManagedArtifactModified "a.md" ] -> ()
                  | other -> failwith $"Expected a modified-artifact problem, got {other}" }

          { Name = "a drifted user-owned artifact never makes the installation invalid"
            Run =
              fun () ->
                  let recorded = Some [ recordedArtifact "user.md" Ownership.UserOwned "seed" ]
                  let state = observed [ "user.md", "theirs" ] recorded
                  Assert.equal (InstallationState.Installed(InstalledVersion "1.0.0")) (Planning.installationState "1.0.0" state) }

          { Name = "doctor separates errors, warnings and information"
            Run =
              fun () ->
                  let recorded =
                      Some
                          [ recordedArtifact "tool.md" Ownership.ToolOwned "t1"
                            recordedArtifact "shared.md" Ownership.Shared "s1" ]

                  // tool.md is gone (error); shared.md was edited (information).
                  // No upgrade warning: an installation this CLI has found to
                  // be invalid needs repairing before its version matters, so
                  // the state machine reports the problem rather than also
                  // suggesting an upgrade over the top of it.
                  let state = observed [ "shared.md", "edited" ] recorded
                  let diagnoses = Diagnostics.inspect "2.0.0" None state

                  Assert.equal 1 (Diagnostics.errors diagnoses).Length
                  Assert.empty (Diagnostics.warnings diagnoses)

                  Assert.isTrue
                      (diagnoses |> List.exists (fun item -> item.Code = "seeded-file-modified" && item.Severity = Severity.Information))
                      "an edited shared file is information, not a problem"

                  // Every error and warning must say how to fix itself.
                  for item in diagnoses |> List.filter (fun item -> item.Severity <> Severity.Information) do
                      Assert.isTrue item.Remedy.IsSome $"{item.Code} must carry a remedy" }

          { Name = "strict verification fails on warnings; plain verification does not"
            Run =
              fun () ->
                  let recorded = Some [ recordedArtifact "tool.md" Ownership.ToolOwned "t1" ]
                  let state = observed [ "tool.md", "t1" ] recorded
                  let diagnoses = Diagnostics.inspect "2.0.0" None state

                  Assert.empty (Diagnostics.verificationFailures false diagnoses)
                  Assert.equal 1 (Diagnostics.verificationFailures true diagnoses).Length

                  // A strict pass always implies a plain pass.
                  let current = Diagnostics.inspect "1.0.0" None state
                  Assert.empty (Diagnostics.verificationFailures true current)
                  Assert.empty (Diagnostics.verificationFailures false current) }

          { Name = "an upgrade from a legacy installation runs its migration and preserves every file"
            Run =
              fun () ->
                  let payload =
                      [ entry "tool.md" Ownership.ToolOwned "t1"
                        entry "user.md" Ownership.UserOwned "u1" ]

                  let state =
                      { observed [ "tool.md", "t1"; "user.md", "edited" ] None with
                          LegacyManifestPresent = true }

                  let steps = Migration.path Migration.LegacyConfigurationVersion |> Result.defaultValue []
                  let installation = Planning.upgrade "greenfield" "pkg" "2.0.0" payload state steps

                  Assert.empty (Plan.blockingConflicts installation.Plan)

                  Assert.isTrue
                      (installation.Plan.Changes
                       |> List.exists (function
                           | PlannedChange.RunMigration(0, 1, _) -> true
                           | _ -> false))
                      "the plan must name the migration it runs"

                  Assert.isTrue (installation.Plan.Preserved |> List.contains "user.md") "the user's edit must be preserved"
                  Assert.equal "2.0.0" installation.Manifest.InstalledVersion }

          { Name = "a migration with an unmet precondition blocks before anything is planned"
            Run =
              fun () ->
                  let steps = [ { FromVersion = 0; ToVersion = 1; Description = "adopt" } ]
                  // No legacy manifest and no current manifest: nothing to migrate.
                  let installation = Planning.upgrade "greenfield" "pkg" "2.0.0" [] ObservedRepository.empty steps

                  match Assert.single (Plan.blockingConflicts installation.Plan) with
                  | Conflict.MigrationPreconditionFailed(0, 1, _) -> ()
                  | other -> failwith $"Expected a precondition failure, got {other}" }

          // The scaffold is compiled into the assembly so a released binary can
          // install and upgrade a repository on its own. If the embedded set
          // and the manifests ever drift, that binary ships unable to do the
          // thing it exists for -- so the two must match exactly, in both
          // directions: a missing file breaks `init`, an extra one is dead
          // weight in every published binary.
          { Name = "the embedded scaffold is exactly what the starter manifests reference"
            Run =
              fun () ->
                  let embedded = Ros.Infrastructure.Lifecycle.Payload.embeddedPaths ()

                  Assert.isTrue (not embedded.IsEmpty) "no scaffold is embedded; the build is not shipping a usable binary"

                  // MSBuild names a resource with the building platform's
                  // separator, so a Windows build would otherwise index the
                  // scaffold under backslashes and match nothing the manifests
                  // ask for. The index normalises; this asserts it stays so.
                  Assert.empty (embedded |> Set.filter (fun path -> path.Contains '\\') |> Set.toList)

                  let declared =
                      [ "greenfield"; "project-administration" ]
                      |> List.collect (fun profile ->
                          let manifest = $"starter/{profile}/manifest.json"

                          match Ros.Infrastructure.Lifecycle.Payload.embeddedText manifest with
                          | None -> failwith $"{manifest} is not embedded"
                          | Some text ->
                              use document = System.Text.Json.JsonDocument.Parse text

                              document.RootElement.GetProperty("files").EnumerateArray()
                              |> Seq.map (fun entry -> entry.GetProperty("source").GetString())
                              |> List.ofSeq
                              |> fun sources -> manifest :: sources)
                      // package.json is read for the installed name and version.
                      |> fun sources -> "package.json" :: sources
                      |> Set.ofList

                  let missing = Set.difference declared embedded |> Set.toList
                  let extra = Set.difference embedded declared |> Set.toList

                  Assert.empty missing
                  Assert.empty extra }

          { Name = "every exit code has exactly one documented meaning"
            Run =
              fun () ->
                  let codes = ExitCode.describe |> List.map fst
                  Assert.equal codes.Length (codes |> List.distinct |> List.length)
                  Assert.equal 0 ExitCode.Success
                  Assert.equal 8 ExitCode.describe.Length } ]
