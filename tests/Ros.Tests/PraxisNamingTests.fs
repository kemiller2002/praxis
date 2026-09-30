namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Cli
open Ros.Domain.Naming

/// The Praxis naming contract (DF-ROS-2026-A050, RQ-ROS-2026-A025): `praxis`
/// is the canonical CLI, every `ros` launcher is an alias of the same
/// implementation, PRAXIS_* variables are canonical, new installations use
/// Praxis launchers and workflow, and a ROS-era installation upgrades without
/// losing `.ros/` state, its aliases or local edits.
[<RequireQualifiedAccess>]
module PraxisNamingTests =
    let private version () = CliPort.releaseVersion.Value

    let private sha256 (path: string) =
        File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString |> _.ToLowerInvariant()

    /// Asserts the exit code and returns the run for further assertions.
    let private succeeded (result: CliHarness.Run) =
        CliPort.exitCode 0 result
        result

    let private posixOnly body =
        if not (OperatingSystem.IsWindows()) then body ()

    /// `sh LAUNCHER ARGS` from `directory`.
    let private launch (directory: string) (launcher: string) (arguments: string list) =
        CliHarness.runIn (Some directory) "sh" ([ Path.Combine(directory, launcher) ] @ arguments) []

    let private exists root relative = File.Exists(Path.Combine(root, relative))

    let private isAliasOf (target: string) (content: string) =
        content.Contains "Compatibility alias" && content.Contains $"/{target}\" \"$@\""

    /// Rewrite `.echelon/ros.json`'s managed-artifact list, as a ROS-era
    /// installation recorded it.
    let private rewriteManagedArtifacts (root: string) (update: JsonObject list -> JsonObject list) =
        let manifest = CliPort.readJson root ".echelon/ros.json"
        let artifacts = manifest["managedArtifacts"].AsArray() |> Seq.map (fun node -> node.DeepClone().AsObject()) |> List.ofSeq
        let updated = JsonArray()
        update artifacts |> List.iter (fun artifact -> updated.Add artifact)
        manifest["managedArtifacts"] <- updated
        CliPort.writeJson root ".echelon/ros.json" manifest

    let private artifactPath (artifact: JsonObject) = artifact["path"].GetValue<string>()

    /// Turn a fresh installation into the shape a pre-rename (ROS-era) CLI
    /// left behind: only `ros*` launchers, a `ros-validation.yml` workflow
    /// named "ROS validation", and a manifest recording exactly that.
    let private makeRosEra (root: string) (editWorkflow: bool) =
        let workflows = Path.Combine(root, ".github", "workflows")
        let current = Path.Combine(workflows, "praxis-validation.yml")
        let legacy = Path.Combine(workflows, "ros-validation.yml")
        let legacyContent = (File.ReadAllText current).Replace("name: Praxis validation", "name: ROS validation").Replace("./praxis", "./ros")
        File.Delete current
        File.WriteAllText(legacy, legacyContent)
        let recordedWorkflowSha = sha256 legacy

        if editWorkflow then
            File.AppendAllText(legacy, "# a local edit the upgrade must keep\n")

        for launcher in [ "praxis"; "praxis.cmd"; "praxis.ps1" ] do
            File.Delete(Path.Combine(root, launcher))

        // The pre-rename `ros` was the real launcher, not an alias.
        let legacyLauncher = Path.Combine(root, "ros")
        File.WriteAllText(legacyLauncher, "#!/usr/bin/env sh\n# pre-rename launcher\nexit 0\n")
        let legacyLauncherSha = sha256 legacyLauncher

        rewriteManagedArtifacts root (fun artifacts ->
            artifacts
            |> List.filter (fun artifact -> not (List.contains (artifactPath artifact) [ "praxis"; "praxis.cmd"; "praxis.ps1" ]))
            |> List.map (fun artifact ->
                match artifactPath artifact with
                | ".github/workflows/praxis-validation.yml" ->
                    artifact["path"] <- JsonValue.Create ".github/workflows/ros-validation.yml"
                    artifact["sha256"] <- JsonValue.Create recordedWorkflowSha
                    artifact
                | "ros" ->
                    artifact["sha256"] <- JsonValue.Create legacyLauncherSha
                    artifact
                | _ -> artifact))

        // A ROS-era ros.json predates durable-checkpoint enforcement, and an
        // upgrade never imposes it on existing configuration.
        let config = CliPort.readJson root "ros.json"
        config["workProtocol"].AsObject().Remove "continuity" |> ignore
        CliPort.writeJson root "ros.json" config
        CliHarness.write root ".ros/work/history-marker.txt" "ROS-era state that must survive\n"
        CliHarness.commitAll root "ROS-era installation"
        legacyContent

    let tests =
        [ { Name = "praxis naming: --version identifies the praxis CLI with the release version"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-naming-version"

                  try
                      let result = succeeded (CliHarness.ros root [ "--version" ])
                      Assert.equal $"praxis {version ()}" (result.Out.Trim())
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: help names praxis, documents the ros alias, and teaches praxis commands"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-naming-help"

                  try
                      let help = (succeeded (CliHarness.ros root [ "--help" ])).Out
                      Assert.isTrue (help.StartsWith "praxis -- Praxis") help
                      Assert.isTrue (help.Contains "The legacy `ros` command is supported as a compatibility alias") help

                      for expected in [ "praxis <command> [options]"; "Usage: praxis [--root PATH]"; "architecture check"; "web serve"; "hub serve" ] do
                          Assert.isTrue (help.Contains expected) $"help lacks '{expected}'"

                      for legacy in [ "Usage: ros"; "ros-fs"; "Repository Operating System lifecycle CLI"; "\n  ros " ] do
                          Assert.isTrue (not (help.Contains legacy)) $"help still says '{legacy}'"

                      let initHelp = (succeeded (CliHarness.ros root [ "init"; "--help" ])).Out
                      Assert.isTrue (initHelp.StartsWith "praxis init --") initHelp
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: repair hints teach praxis commands"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-hints" None

                  try
                      File.Delete(Path.Combine(root, "BOOTSTRAP.md"))
                      let doctor = CliHarness.ros root [ "doctor" ]
                      let output = doctor.Out + doctor.Err
                      Assert.isTrue (output.Contains "praxis init") output
                      Assert.isTrue (not (output.Contains "'ros ")) output
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: ./praxis and the ./ros alias in this checkout run the same Praxis CLI"
            Run =
              fun () ->
                  posixOnly (fun () ->
                      let root = CliPort.repositoryRoot.Value
                      let canonical = succeeded (launch root "praxis" [ "--version" ])
                      let alias = succeeded (launch root "ros" [ "--version" ])
                      Assert.equal $"praxis {version ()}" (canonical.Out.Trim())
                      Assert.equal canonical.Out alias.Out
                      Assert.isTrue (isAliasOf "praxis" (File.ReadAllText(Path.Combine(root, "ros")))) "./ros must only exec ./praxis"
                      let check = succeeded (launch root "praxis" [ "architecture"; "check" ])
                      Assert.isTrue (check.Out.Contains "architecture check passed") check.Out) }

          { Name = "praxis naming: canonical PRAXIS_* variables reach every reader and win over ROS_*"
            Run =
              fun () ->
                  let lookup values name = values |> List.tryFind (fst >> (=) name) |> Option.map snd

                  Assert.equal
                      [ "ROS_ACTOR", "praxis-agent"; "ROS_BASE_REF", "main" ]
                      (EnvironmentAliases.legacyAssignments (lookup [ "PRAXIS_ACTOR", "praxis-agent"; "PRAXIS_BASE_REF", "main"; "PRAXIS_TELEMETRY_MODEL", "" ]))

                  Assert.empty (EnvironmentAliases.legacyAssignments (lookup [ "ROS_ACTOR", "legacy" ]))
                  Assert.isTrue (EnvironmentAliases.canonicalNames |> List.forall (fun name -> name.StartsWith "PRAXIS_")) "canonical names use PRAXIS_"

                  let root = CliHarness.temporaryDirectory "praxis-naming-environment"

                  try
                      let actorOf environment =
                          let result = succeeded (CliHarness.rosWith root environment [ "provenance"; "identity"; "--json" ])
                          (CliHarness.json result.Out)["actor"]

                      let canonical = actorOf [ "PRAXIS_ACTOR_KIND", "human"; "PRAXIS_ACTOR", "canonical-person" ]
                      Assert.equal "human" (canonical["kind"].GetValue<string>())
                      Assert.equal "canonical-person" (canonical["id"].GetValue<string>())

                      let legacy = actorOf [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "legacy-person" ]
                      Assert.equal "legacy-person" (legacy["id"].GetValue<string>())

                      let both = actorOf [ "PRAXIS_ACTOR_KIND", "human"; "PRAXIS_ACTOR", "canonical-person"; "ROS_ACTOR", "legacy-person" ]
                      Assert.equal "canonical-person" (both["id"].GetValue<string>())
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: a fresh installation's primary launchers and workflow are Praxis; ros launchers are aliases"
            Run =
              fun () ->
                  for profile in [ "greenfield"; "project-administration" ] do
                      let root = CliHarness.initializedRepository "praxis-naming-scaffold" (Some profile)

                      try
                          for launcher in [ "praxis"; "praxis.cmd"; "praxis.ps1"; "ros"; "ros.cmd"; "ros.ps1" ] do
                              Assert.isTrue (exists root launcher) $"{profile} lacks {launcher}"

                          Assert.isTrue (isAliasOf "praxis" (CliHarness.read root "ros")) $"{profile}: ros must exec praxis"
                          Assert.isTrue ((CliHarness.read root "ros.cmd").Contains "praxis.cmd") $"{profile}: ros.cmd must call praxis.cmd"
                          Assert.isTrue ((CliHarness.read root "ros.ps1").Contains "praxis.ps1") $"{profile}: ros.ps1 must call praxis.ps1"
                          Assert.isTrue ((CliHarness.read root "praxis.cmd").Contains "praxis.ps1") $"{profile}: praxis.cmd must run praxis.ps1"

                          if not (OperatingSystem.IsWindows()) then
                              let mode = File.GetUnixFileMode(Path.Combine(root, "praxis"))
                              Assert.isTrue (mode.HasFlag UnixFileMode.UserExecute) "praxis is executable"

                          let workflow = CliHarness.read root ".github/workflows/praxis-validation.yml"
                          Assert.isTrue (workflow.Contains "name: Praxis validation") workflow
                          Assert.isTrue (workflow.Contains "run: ./praxis validate") workflow
                          Assert.isTrue (workflow.Contains "PRAXIS_BASE_REF") workflow
                          Assert.isTrue (not (exists root ".github/workflows/ros-validation.yml")) "no ROS-named workflow"

                          if profile = "project-administration" then
                              Assert.isTrue ((CliHarness.read root "praxis-hub").Contains "/praxis\" --root") "praxis-hub runs ./praxis hub"
                              Assert.isTrue (isAliasOf "praxis-hub" (CliHarness.read root "ros-hub")) "ros-hub must exec praxis-hub"

                          Assert.equal 0 (CliHarness.ros root [ "verify" ]).Exit
                      finally
                          CliHarness.removeDirectory root }

          { Name = "praxis naming: the scaffolded ./ros alias runs the project's ./praxis launcher"
            Run =
              fun () ->
                  posixOnly (fun () ->
                      let root = CliHarness.initializedRepository "praxis-naming-alias" None

                      try
                          // Stand in for the pinned release so no download is needed.
                          File.WriteAllText(Path.Combine(root, "praxis"), "#!/usr/bin/env sh\necho \"praxis-launcher $*\"\n")
                          let result = succeeded (launch root "ros" [ "validate"; "--json" ])
                          Assert.equal "praxis-launcher validate --json" (result.Out.Trim())
                      finally
                          CliHarness.removeDirectory root) }

          { Name = "praxis naming: upgrading a ROS-era installation adds Praxis launchers, moves its workflow and keeps .ros state"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-upgrade" None

                  try
                      makeRosEra root false |> ignore
                      Assert.equal 0 (CliHarness.ros root [ "upgrade" ]).Exit

                      for launcher in [ "praxis"; "praxis.cmd"; "praxis.ps1" ] do
                          Assert.isTrue (exists root launcher) $"upgrade did not add {launcher}"

                      Assert.isTrue (isAliasOf "praxis" (CliHarness.read root "ros")) "the unmodified ROS-era launcher becomes an alias"
                      Assert.isTrue (not (exists root ".github/workflows/ros-validation.yml")) "the ROS-era workflow moved"
                      let workflow = CliHarness.read root ".github/workflows/praxis-validation.yml"
                      Assert.isTrue (workflow.Contains "name: Praxis validation") "an unmodified workflow is brought up to date"
                      Assert.equal "ROS-era state that must survive\n" (CliHarness.read root ".ros/work/history-marker.txt")

                      let manifest = CliHarness.read root ".echelon/ros.json"
                      Assert.isTrue (manifest.Contains "\"tool\": \"ros\"") "the persisted manifest format is unchanged"
                      Assert.isTrue (not (manifest.Contains "ros-validation.yml")) manifest
                      Assert.equal 0 (CliHarness.ros root [ "verify" ]).Exit
                      Assert.equal 0 (CliHarness.ros root [ "init"; "--check" ]).Exit
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: upgrading moves a locally edited ROS-era workflow with its edits intact"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-upgrade-edited" None

                  try
                      let legacyContent = makeRosEra root true
                      Assert.equal 0 (CliHarness.ros root [ "upgrade" ]).Exit
                      Assert.isTrue (not (exists root ".github/workflows/ros-validation.yml")) "the edited workflow moved"

                      Assert.equal
                          (legacyContent + "# a local edit the upgrade must keep\n")
                          (CliHarness.read root ".github/workflows/praxis-validation.yml")

                      Assert.equal 0 (CliHarness.ros root [ "verify" ]).Exit
                      Assert.equal 0 (CliHarness.ros root [ "init"; "--check" ]).Exit
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: the hub runs a spoke's praxis launcher and falls back to a pre-rename ros launcher"
            Run =
              fun () ->
                  let present paths (path: string) = List.contains (path.Replace('\\', '/')) paths
                  Assert.equal (Some(Path.Combine("/spoke", "praxis"))) (HubRegistry.resolveLauncher (present [ "/spoke/praxis"; "/spoke/ros" ]) "/spoke")
                  Assert.equal (Some(Path.Combine("/spoke", "ros"))) (HubRegistry.resolveLauncher (present [ "/spoke/ros" ]) "/spoke")
                  Assert.equal None (HubRegistry.resolveLauncher (present []) "/spoke") }

          // ---- rename invariants hardened before main is reconciled into PR #92
          // (PRAXIS-PR92-PREMERGE-RENAME-FENCE). Each is behavior this branch
          // already has; a merge that lets main's side of a conflict win would
          // silently lose it.

          { Name = "praxis naming: telemetry identity honours PRAXIS_* only, ROS_* only, and PRAXIS_* over ROS_* when both are set"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-naming-telemetry-env"

                  try
                      let identity environment =
                          let result = succeeded (CliHarness.rosWith root (("PRAXIS_ACTOR_KIND", "agent") :: ("PRAXIS_ACTOR", "example/agent") :: environment) [ "provenance"; "identity"; "--json" ])
                          (CliHarness.json result.Out).["actor"]

                      let field (actor: JsonNode) (name: string) = CliPort.text actor.[name]
                      let canonical = identity [ "PRAXIS_TELEMETRY_PROVIDER", "praxis-provider"; "PRAXIS_TELEMETRY_RUNTIME", "praxis-runtime" ]
                      Assert.equal "praxis-provider" (field canonical "provider")
                      Assert.equal "praxis-runtime" (field canonical "runtime")
                      let legacy = identity [ "ROS_TELEMETRY_PROVIDER", "ros-provider"; "ROS_TELEMETRY_RUNTIME", "ros-runtime" ]
                      Assert.equal "ros-provider" (field legacy "provider")
                      Assert.equal "ros-runtime" (field legacy "runtime")

                      let both =
                          identity
                              [ "PRAXIS_TELEMETRY_PROVIDER", "praxis-provider"; "ROS_TELEMETRY_PROVIDER", "ros-provider"
                                "PRAXIS_TELEMETRY_RUNTIME", "praxis-runtime"; "ROS_TELEMETRY_RUNTIME", "ros-runtime" ]

                      Assert.equal "praxis-provider" (field both "provider")
                      Assert.equal "praxis-runtime" (field both "runtime")
                      // An empty canonical value is unset: it never blanks a legacy value.
                      let empty = identity [ "PRAXIS_TELEMETRY_PROVIDER", ""; "ROS_TELEMETRY_PROVIDER", "ros-provider" ]
                      Assert.equal "ros-provider" (field empty "provider")
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: every documented ROS_* variable has a PRAXIS_* alias; unrelated prefixes are untouched"
            Run =
              fun () ->
                  for suffix in [ "ACTOR"; "ACTOR_KIND"; "TELEMETRY_PROVIDER"; "TELEMETRY_RUNTIME"; "TELEMETRY_MODEL"; "TELEMETRY_SESSION_ID"; "TELEMETRY_RUN_ID"; "BASE_REF"; "GIT_REMOTE_TIMEOUT_SECONDS" ] do
                      Assert.isTrue (List.contains ("PRAXIS_" + suffix) EnvironmentAliases.canonicalNames) $"PRAXIS_{suffix} is not aliased"

                  let lookup name = if name = "PRAXIS_BASE_REF" then Some "abc" else None
                  Assert.equal [ "ROS_BASE_REF", "abc" ] (EnvironmentAliases.legacyAssignments lookup) }

          { Name = "praxis naming: a new installation keeps the intentionally retained ROS persisted names"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-retained" None

                  try
                      // DF-ROS-2026-A050 renames the product, not persisted state: these
                      // names are read by every earlier release and must not move.
                      Assert.isTrue (Directory.Exists(Path.Combine(root, ".ros"))) ".ros/ is the state directory"
                      Assert.isTrue (exists root "ros.json") "ros.json is the configuration file"
                      Assert.isTrue (exists root ".echelon/ros.json") ".echelon/ros.json is the installation manifest"
                      Assert.equal "ros" (CliPort.text (CliPort.readJson root ".echelon/ros.json").["tool"])
                      Assert.equal (version ()) (CliPort.text (CliPort.readJson root "ros.json").["rosVersion"])

                      for renamed in [ ".praxis"; "praxis.json"; ".echelon/praxis.json" ] do
                          Assert.isTrue (not (exists root renamed || Directory.Exists(Path.Combine(root, renamed)))) $"{renamed} must not appear"

                      let events = CliHarness.read root ".ros/events/events.jsonl"
                      Assert.isTrue (events.Contains("ROS-INSTALL-" + version().Replace(".", "-"))) "the install work item keeps its ROS-INSTALL-* id"
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: upgrading never discards a customized ROS-era ros launcher; it blocks and changes nothing"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-upgrade-custom-launcher" None

                  try
                      makeRosEra root false |> ignore
                      let customized = "#!/usr/bin/env sh\n# pre-rename launcher\n# a local edit\nexit 0\n"
                      File.WriteAllText(Path.Combine(root, "ros"), customized)
                      CliHarness.commitAll root "customize the ROS-era launcher"
                      let upgrade = CliHarness.ros root [ "upgrade" ]
                      Assert.equal 5 upgrade.Exit
                      Assert.isTrue ((upgrade.Out + upgrade.Err).Contains "ros: tool-owned file was modified locally") (upgrade.Out + upgrade.Err)
                      Assert.equal customized (CliHarness.read root "ros")
                      Assert.isTrue (exists root ".github/workflows/ros-validation.yml") "a blocked upgrade moves nothing"
                      Assert.isTrue (not (exists root "praxis")) "a blocked upgrade adds nothing"
                      Assert.equal "ROS-era state that must survive\n" (CliHarness.read root ".ros/work/history-marker.txt")
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: upgrading a ROS-era installation leaves its legacy Node launcher helper in place"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-upgrade-legacy-helper" None

                  try
                      makeRosEra root false |> ignore
                      CliHarness.write root "tools/ros_fs_launcher.mjs" "// ROS-era launcher helper\n"
                      CliHarness.commitAll root "ROS-era launcher helper"
                      Assert.equal 0 (CliHarness.ros root [ "upgrade" ]).Exit
                      // Upgrade never deletes a file it does not manage.
                      Assert.equal "// ROS-era launcher helper\n" (CliHarness.read root "tools/ros_fs_launcher.mjs")
                      Assert.isTrue (isAliasOf "praxis" (CliHarness.read root "ros")) "ros becomes the alias"
                      Assert.equal 0 (CliHarness.ros root [ "verify" ]).Exit
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: work begun under ROS-era identity variables is resumed and completed through praxis with PRAXIS_* identity"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "praxis-naming-legacy-work" None

                  try
                      let legacy = [ "ROS_ACTOR_KIND", "agent"; "ROS_ACTOR", "example/agent"; "ROS_TELEMETRY_PROVIDER", "example"; "ROS_TELEMETRY_RUNTIME", "runtime"; "ROS_TELEMETRY_SESSION_ID", "s-1" ]
                      let canonical = legacy |> List.map (fun (name, value) -> name.Replace("ROS_", "PRAXIS_"), value)
                      let occurredAt () = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                      CliHarness.rosOk root [ "add"; "Legacy item"; "--id"; "WI-LEGACY-1" ] |> ignore
                      CliHarness.rosOk root [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-LEGACY-1"; "--occurred-at"; occurredAt () ] |> ignore
                      succeeded (CliHarness.rosWith root legacy [ "work"; "start"; "--id"; "WI-LEGACY-1"; "--type"; "mechanical"; "--occurred-at"; occurredAt () ]) |> ignore
                      makeRosEra root false |> ignore
                      Assert.equal 0 (CliHarness.ros root [ "upgrade" ]).Exit

                      let context = succeeded (CliHarness.rosWith root canonical [ "work"; "context"; "WI-LEGACY-1" ])
                      Assert.isTrue (context.Out.Contains "WI-LEGACY-1") context.Out
                      succeeded (CliHarness.rosWith root canonical [ "work"; "block"; "--id"; "WI-LEGACY-1"; "--reason"; "handoff"; "--occurred-at"; occurredAt () ]) |> ignore
                      succeeded (CliHarness.rosWith root canonical [ "work"; "resume"; "--id"; "WI-LEGACY-1"; "--occurred-at"; occurredAt () ]) |> ignore
                      succeeded (CliHarness.rosWith root canonical [ "work"; "complete"; "--id"; "WI-LEGACY-1"; "--occurred-at"; occurredAt () ]) |> ignore

                      let item =
                          CliPort.items (CliPort.readJson root ".ros/context/current.json").["workItems"]
                          |> Seq.find (fun node -> CliPort.text node.["id"] = "WI-LEGACY-1")

                      Assert.equal "complete" (CliPort.text item.["state"])
                      let blocked = CliHarness.read root ".ros/events/events.jsonl"
                      Assert.isTrue (blocked.Contains "\"example/agent\"") "the same actor is recorded under either prefix"
                  finally
                      CliHarness.removeDirectory root }

          { Name = "praxis naming: ./ros in this checkout is the same program as ./praxis for help, not only --version"
            Run =
              fun () ->
                  posixOnly (fun () ->
                      let root = CliPort.repositoryRoot.Value
                      let canonical = succeeded (launch root "praxis" [ "--help" ])
                      let alias = succeeded (launch root "ros" [ "--help" ])
                      Assert.equal canonical.Out alias.Out) } ]
