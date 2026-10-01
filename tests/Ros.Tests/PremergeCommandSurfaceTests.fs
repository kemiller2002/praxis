namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// Command groups main added after PR #92 diverged (merge base 9ace9d9) must
/// stay reachable through the real entry point after the reconciliation,
/// whichever side's `Program.fs`, `Ros.Cli.fsproj` compile list or usage
/// string wins a conflict (PRAXIS-PR92-PREMERGE-REGRESSION-FENCE). Every
/// assertion is about behavior a user sees, never the CLI's executable name,
/// so it holds before and after the rename.
[<RequireQualifiedAccess>]
module PremergeCommandSurfaceTests =
    open PremergeFence

    let private agentEnvironment =
        [ "ROS_ACTOR_KIND", "agent"
          "ROS_ACTOR", "example/fence-agent"
          "ROS_TELEMETRY_PROVIDER", "example"
          "ROS_TELEMETRY_RUNTIME", "fence"
          "ROS_TELEMETRY_SESSION_ID", "fence-session" ]

    let private installed label =
        let root = temporaryDirectory label
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity root
        cli root [ "init"; "--project"; "Fence" ] |> ok |> ignore
        root

    let private combined (result: Result) = result.Output + result.Error

    /// What an unknown command prints: the global lifecycle help.
    let private fallback root =
        cli root [ "definitely-not-a-praxis-command" ] |> combined

    /// Each newer command group, an invocation that must reach it, and text
    /// only that group's own handler prints.
    let private reachable =
        [ [ "plan" ], "plan analyze|simulate"
          [ "plan"; "analyze"; "--json" ], "\"praxis.plan/"
          [ "execution" ], "execution start --work-item"
          [ "execution"; "list"; "--json" ], "["
          [ "installation" ], "installation register --system"
          [ "installation"; "list"; "--json" ], "installation query"
          [ "work"; "checkpoint"; "show" ], "work checkpoint show requires"
          [ "work"; "checkpoint" ], "work checkpoint"
          [ "work"; "continue" ], "work continue requires"
          [ "work"; "abandon" ], "abandon requires"
          [ "remote"; "describe" ], "\"praxis.describe\""
          [ "status"; "--offline"; "--json" ], "\"continuity\""
          [ "work"; "context"; "--offline" ], "\"continuity\"" ]

    let tests =
        [ { Name = "fence: every command group main added after the merge base is dispatched, not the unknown-command fallback"
            Run =
              fun () ->
                  let root = installed "surface"
                  let unknown = fallback root

                  reachable
                  |> List.iter (fun (arguments, marker) ->
                      let output = cli root arguments |> combined
                      let label = String.concat " " arguments
                      Assert.isTrue (output <> unknown) $"'{label}' fell through to the unknown-command help"
                      contains marker output $"'{label}' did not reach its own handler") }

          { Name = "fence: the global help still teaches the newer command groups and their flags"
            Run =
              fun () ->
                  let help = cli (temporaryDirectory "help") [ "--help" ] |> combined

                  [ "execution start --work-item"; "execution list"; "installation register"; "installation list|status"
                    "installation history"; "plan analyze|simulate"; "explain-group"; "work checkpoint --id"
                    "work checkpoint show"; "work continue --id"; "work abandon --id"; "--unrecoverable-reason"
                    "work context [ID] [--text] [--offline]"; "status [--json] [--verbose] [--offline]"
                    "remote execute --request"; "remote describe"; "telemetry step start|complete|fail"
                    "telemetry usage" ]
                  |> List.iter (fun grammar -> contains grammar help "global help") }

          { Name = "fence: plan, execution and installation keep their structured JSON contracts"
            Run =
              fun () ->
                  let root = installed "contracts"
                  let plan = cli root [ "plan"; "analyze"; "--json" ] |> ok
                  Assert.isTrue ((text plan.Json.["schema"]).StartsWith("praxis.plan/", StringComparison.Ordinal)) "plan schema"
                  Assert.equal "analysis" (text plan.Json.["kind"])
                  let executions = cli root [ "execution"; "list"; "--json" ] |> ok
                  Assert.isTrue (JsonNode.Parse(executions.Output) :? JsonArray) "execution list --json is an array"
                  let query = cli root [ "installation"; "list"; "--json" ]
                  Assert.equal 0 query.ExitCode
                  contains "unavailable" (combined query) "installation list without an administration integration" }

          { Name = "fence: the reported version is the repository's declared release version, never older than main's 3.6.0"
            Run =
              fun () ->
                  // Main declares its version in package.json; PR #92 moves it to
                  // release.json. Whichever survives must be what --version prints,
                  // and a merge must not resurrect PR #92's older 3.4.0.
                  let declared =
                      [ "package.json"; "release.json" ]
                      |> List.map repositoryFile
                      |> List.filter File.Exists
                      |> List.map (fun path -> text (readJson path).["version"])
                      |> List.distinct
                      |> Assert.single

                  let printed =
                      (cli (temporaryDirectory "version") [ "--version" ] |> ok).Output.Trim().Split(' ')
                      |> Array.last

                  Assert.equal declared printed
                  Assert.isTrue (Version(printed) >= Version("3.6.0")) $"version {printed} regressed below main's released 3.6.0" }

          { Name = "fence: init self-registers the installation when a Project Administration integration is configured"
            Run =
              fun () ->
                  let root = temporaryDirectory "self-register"
                  git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                  git root [ "remote"; "add"; "origin"; "git@github.com:echelon-foundry/example.git" ] |> ignore
                  let store = Path.Combine(root, "admin-store")
                  Directory.CreateDirectory store |> ignore
                  let stub = Path.Combine(root, "stub-admin.sh")

                  File.WriteAllText(
                      stub,
                      "#!/bin/sh\nstore=\"$4\"\ncat > \"$store/request-$2.json\"\n"
                      + "printf '%s' '{\"schema\":\"echelon.installation.result/v1\",\"status\":\"recorded\",\"eventId\":\"IE-1\",\"operation\":\"installed\"}'\n"
                  )

                  write
                      root
                      ".echelon/administration.json"
                      $$"""{ "schema": "echelon.administration/v1", "provider": "project-administration", "required": false,
                             "environment": { "id": "ws-primary" },
                             "transport": { "kind": "local", "command": ["/bin/sh", "{{stub}}"], "store": "../admin-store" } }"""

                  cliWith root agentEnvironment [ "init"; "--project"; "Fence" ] |> ok |> ignore
                  let version = (cli root [ "--version" ] |> ok).Output.Trim().Split(' ') |> Array.last
                  let request = readJson (Path.Combine(store, "request-register.json"))
                  let serialized = request.ToJsonString()
                  contains "\"praxis\"" serialized "the registered system"
                  contains $"\"{version}\"" serialized "the registered version is the CLI's own" }

          { Name = "fence: work complete keeps an explicit --conclusion for a non-research item and records none when omitted (PRAXIS-REMOTE-16)"
            Run =
              fun () ->
                  let root = installed "conclusion"
                  commitAll root "install" |> ignore

                  [ "WI-MECH-EXPLICIT"; "WI-MECH-NONE" ]
                  |> List.iter (fun id -> cliWith root agentEnvironment [ "work"; "start"; "--id"; id; "--type"; "mechanical"; "--occurred-at"; now () ] |> ok |> ignore)

                  cliWith root agentEnvironment [ "work"; "complete"; "--id"; "WI-MECH-EXPLICIT"; "--occurred-at"; now (); "--conclusion"; "handed over and completed by a successor" ]
                  |> ok
                  |> ignore

                  cliWith root agentEnvironment [ "work"; "complete"; "--id"; "WI-MECH-NONE"; "--occurred-at"; now () ] |> ok |> ignore
                  let explicit = workItem root "WI-MECH-EXPLICIT"
                  let none = workItem root "WI-MECH-NONE"
                  Assert.equal "complete" (text explicit.["state"])
                  Assert.equal "handed over and completed by a successor" (text explicit.["conclusion"])
                  Assert.equal "complete" (text none.["state"])
                  Assert.isTrue (not ((none :?> JsonObject).ContainsKey "conclusion")) "no conclusion is invented" }

          { Name = "fence: a new installation, through the real entry point, enforces durable checkpoints"
            Run =
              fun () ->
                  let root = installed "continuity-default"
                  let config = readJson (Path.Combine(root, "ros.json"))
                  Assert.equal true (boolean config.["workProtocol"].["continuity"].["requireDurableCheckpoint"]) } ]
