namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Ros.Domain.Naming

/// Cross-branch requirements that only exist once PR #92 (F#-only, Praxis
/// rename) and main (continuity, remote 1.3, planner, execution,
/// installation) are combined: P1-P7 of the reconciliation manifest in
/// EV-ROS-2026-A060 (PRAXIS-PR92-RECONCILE, PRAXIS-PR92-POSTMERGE-FENCE).
[<RequireQualifiedAccess>]
module PostmergeReconciliationTests =
    open PremergeFence

    let private canonical (who: Actor) =
        [ "PRAXIS_ACTOR_KIND", who.Kind
          "PRAXIS_ACTOR", who.Id
          "PRAXIS_TELEMETRY_PROVIDER", who.Provider
          "PRAXIS_TELEMETRY_RUNTIME", who.Runtime
          "PRAXIS_TELEMETRY_SESSION_ID", who.Session ]

    let private legacy (who: Actor) =
        canonical who |> List.map (fun (name, value) -> name.Replace("PRAXIS_", "ROS_"), value)

    let private installed label =
        let root = temporaryDirectory label
        git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity root
        cli root [ "init"; "--project"; "Postmerge" ] |> ok |> ignore
        root

    /// A Praxis installation with a bare remote, on a pushed feature branch.
    let private pushedInstallation label (change: JsonObject -> unit) =
        let parent = temporaryDirectory label
        let bare = Path.Combine(parent, "remote.git")
        let clone = Path.Combine(parent, "clone")
        git parent [ "init"; "-q"; "--bare"; "-b"; "main"; bare ] |> ignore
        Directory.CreateDirectory clone |> ignore
        git clone [ "init"; "-q"; "-b"; "main" ] |> ignore
        configureGitIdentity clone
        git clone [ "remote"; "add"; "origin"; bare ] |> ignore
        cli clone [ "init"; "--project"; "Postmerge" ] |> ok |> ignore
        editConfig clone change
        commitAll clone "install praxis" |> ignore
        git clone [ "push"; "-q"; "-u"; "origin"; "main" ] |> ignore
        git clone [ "switch"; "-q"; "-c"; "feature/x" ] |> ignore
        git clone [ "push"; "-q"; "-u"; "origin"; "feature/x" ] |> ignore
        clone

    let private pushAll clone message =
        let sha = commitAll clone message
        git clone [ "push"; "-q" ] |> ignore
        sha

    let private execution root (id: string) =
        readJson (Path.Combine(root, ".ros", "telemetry", "executions", $"{id}.json"))

    /// Scaffolded instructions that run a command through `./ros` rather than
    /// the canonical `./praxis`.
    let private legacyInstruction =
        Regex(@"\./ros (work|init|status|verify|upgrade|doctor|validate|registry|telemetry|provenance|remote|plan|add|execution|installation)\b")

    let private agentA = actor "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = actor "other/agent-b" "other" "agent-b" "session-b"

    let tests =
        [ { Name = "P1 postmerge: a new installation's instructions teach ./praxis, including main's continuity handoff; ./ros only aliases it"
            Run =
              fun () ->
                  let root = installed "p1-scaffold"

                  let teaching =
                      Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
                      |> Array.filter (fun path -> not (path.Contains $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                      |> Array.collect (fun path -> File.ReadAllLines path |> Array.map (fun line -> Path.GetRelativePath(root, path), line))
                      |> Array.filter (fun (_, line) -> legacyInstruction.IsMatch line)

                  Assert.equal [||] teaching
                  let handoff = File.ReadAllText(Path.Combine(root, "HANDOFF.md"))
                  contains "./praxis work checkpoint" handoff "continuity handoff"
                  contains "./praxis work continue" handoff "continuity handoff"
                  let alias = File.ReadAllText(Path.Combine(root, "ros"))
                  contains "Compatibility alias" alias "the scaffolded ros launcher"
                  contains "/praxis\" \"$@\"" alias "ros execs the praxis launcher" }

          { Name = "P1 postmerge: no user-facing string in the CLI source teaches a ./ros command"
            Run =
              fun () ->
                  // Guidance merged from main (repair hints, planner advice) must
                  // name the canonical command; ./ros stays only an alias.
                  let offenders =
                      Directory.GetFiles(repositoryFile "src", "*.fs", SearchOption.AllDirectories)
                      |> Array.collect (fun path ->
                          File.ReadAllLines path
                          |> Array.mapi (fun index line -> $"{Path.GetRelativePath(repositoryRoot.Force(), path)}:{index + 1}", line))
                      |> Array.filter (fun (_, line) -> legacyInstruction.IsMatch line)
                      |> Array.map fst

                  Assert.equal [||] offenders }

          { Name = "P2 postmerge: work checkpointed under ROS_* identity in a pre-continuity installation is continued under PRAXIS_* identity"
            Run =
              fun () ->
                  // A ROS-era ros.json never had continuity enforcement; the upgrade never imposes it.
                  let clone =
                      pushedInstallation "p2-continue" (fun config -> (config.["workProtocol"] :?> JsonObject).Remove "continuity" |> ignore)

                  cliWith clone (legacy agentA) [ "work"; "start"; "--id"; "FEAT-9"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  let predecessor = (workItem clone "FEAT-9").["telemetryExecutionIds"] |> strings |> List.head
                  write clone "src/slice.txt" "slice\n"
                  let sha = pushAll clone "slice under ROS_ identity"

                  cliWith clone (legacy agentA) [ "work"; "checkpoint"; "--id"; "FEAT-9"; "--occurred-at"; now (); "--summary"; "Slice"; "--next-action"; "Finish" ]
                  |> ok
                  |> ignore

                  pushAll clone "praxis: checkpoint under ROS_ identity" |> ignore
                  cli clone [ "upgrade" ] |> ok |> ignore
                  let protocol = (readJson (Path.Combine(clone, "ros.json"))).["workProtocol"] :?> JsonObject
                  Assert.isTrue (not (protocol.ContainsKey "continuity")) "upgrade imposed continuity"

                  let continued = cliWith clone (canonical agentB) [ "work"; "continue"; "--id"; "FEAT-9"; "--occurred-at"; now (); "--json" ] |> ok
                  let successor = text continued.Json.["executionId"]
                  Assert.isTrue (successor <> predecessor) "the successor reused the predecessor's execution"
                  Assert.equal predecessor (text continued.Json.["predecessor"].["executionId"])
                  Assert.equal "interrupted" (text continued.Json.["predecessor"].["disposition"])
                  Assert.equal sha (text continued.Json.["continuity"].["checkpoint"].["commit"])
                  let record = execution clone successor
                  Assert.equal predecessor (text record.["identity"].["parentExecutionId"])
                  Assert.equal "other" (text record.["identity"].["provider"])
                  Assert.equal "session-b" (text record.["identity"].["sessionId"])
                  Assert.equal "session-a" (text (execution clone predecessor).["identity"].["sessionId"])
                  let continuedEvent = events clone |> List.findBack (fun event -> text event.["type"] = "work.continued")
                  Assert.equal agentB.Id (text continuedEvent.["actor"].["id"])
                  cli clone [ "validate" ] |> ok |> ignore }

          { Name = "P3 postmerge: main's newer command groups run identically through ./praxis and the ./ros alias in this checkout"
            Run =
              fun () ->
                  if not (OperatingSystem.IsWindows()) then
                      let root = repositoryRoot.Force()
                      let through launcher arguments = exec "sh" root [] (Path.Combine(root, launcher) :: arguments)
                      let unknown = (through "praxis" [ "definitely-not-a-praxis-command" ]).Error

                      [ [ "plan"; "analyze"; "--json"; "--as-of"; "2026-09-30T00:00:00.000Z" ], "\"praxis.plan/"
                        [ "execution"; "list"; "--json" ], "["
                        [ "installation"; "list"; "--json" ], "installation query"
                        [ "remote"; "describe" ], "\"1.3\""
                        [ "work"; "checkpoint"; "show" ], "work checkpoint show requires"
                        [ "work"; "continue" ], "work continue requires"
                        [ "work"; "abandon" ], "abandon requires" ]
                      |> List.iter (fun (arguments, marker) ->
                          let label = String.concat " " arguments
                          let canonicalRun = through "praxis" arguments
                          let aliasRun = through "ros" arguments
                          Assert.equal canonicalRun.ExitCode aliasRun.ExitCode
                          Assert.equal canonicalRun.Output aliasRun.Output
                          Assert.equal canonicalRun.Error aliasRun.Error
                          Assert.isTrue (canonicalRun.Error <> unknown) $"'{label}' fell through to the unknown-command help"
                          contains marker (canonicalRun.Output + canonicalRun.Error) $"'{label}' did not reach its handler") }

          { Name = "P4 postmerge: telemetry recorded under PRAXIS_* identity keeps provider, runtime, session, step, tokens and cost"
            Run =
              fun () ->
                  let root = installed "p4-telemetry"
                  commitAll root "install" |> ignore
                  let env = canonical agentA
                  cliWith root env [ "work"; "start"; "--id"; "FEAT-4"; "--type"; "mechanical"; "--occurred-at"; now () ] |> ok |> ignore
                  let id = (workItem root "FEAT-4").["telemetryExecutionIds"] |> strings |> List.head
                  cliWith root env [ "telemetry"; "step"; "start"; "FEAT-4"; "--step"; "impl"; "--occurred-at"; now () ] |> ok |> ignore

                  cliWith root env [ "telemetry"; "record"; "FEAT-4"; "--metric"; "tokens.input"; "--value"; "1200"; "--unit"; "tokens"; "--source-type"; "runtime-api"; "--step"; "impl"; "--quiet" ]
                  |> ok
                  |> ignore

                  cliWith root env [ "telemetry"; "record"; "FEAT-4"; "--metric"; "cost.session_cumulative"; "--value"; "0.42"; "--unit"; "currency"; "--currency"; "USD"; "--source-type"; "runtime-api"; "--step"; "impl"; "--quiet" ]
                  |> ok
                  |> ignore

                  cliWith root env [ "telemetry"; "step"; "complete"; "FEAT-4"; "--step"; "impl"; "--occurred-at"; now () ] |> ok |> ignore
                  let identity = (execution root id).["identity"]
                  Assert.equal "example" (text identity.["provider"])
                  Assert.equal "agent-a" (text identity.["runtime"])
                  Assert.equal "session-a" (text identity.["sessionId"])
                  let usage = (cli root [ "telemetry"; "usage"; "FEAT-4"; "--by"; "step" ] |> ok).Json.["groups"] |> array

                  let total metric =
                      usage
                      |> List.find (fun group -> text group.["metric"] = metric && text group.["key"] = "impl")
                      |> fun group -> group.["total"].GetValue<float>()

                  Assert.equal 1200.0 (total "tokens.input")

                  // Cost is not a token metric, so `telemetry usage` omits it; the
                  // execution record keeps it with its currency, step and evidence.
                  let cost =
                      (execution root id).["metrics"]
                      |> array
                      |> List.find (fun metric -> text metric.["id"] = "cost.session_cumulative")

                  Assert.equal 0.42 (cost.["value"].GetValue<float>())
                  Assert.equal "USD" (text cost.["currency"])
                  Assert.equal "impl" (text cost.["dimensions"].["step"])
                  Assert.equal "runtime-api" (text cost.["source"].["type"])
                  cliWith root env [ "work"; "complete"; "--id"; "FEAT-4"; "--occurred-at"; now () ] |> ok |> ignore
                  Assert.equal "finalized" (text (execution root id).["status"])

                  let started = events root |> List.find (fun event -> text event.["type"] = "work.started")
                  Assert.equal agentA.Id (text started.["actor"].["id"])
                  cli root [ "validate" ] |> ok |> ignore }

          { Name = "P5 postmerge: the release builds and smoke-tests praxis at the declared version, and legacy ros-fs assets come from the praxis binary"
            Run =
              fun () ->
                  let native = readRepositoryFile ".github/workflows/native-release.yml"
                  contains "= \"praxis ${VERSION}\"" native "the .NET tool smoke test"
                  excludes "ros-fs ${VERSION}" native "the .NET tool smoke test"
                  let assets = readRepositoryFile ".github/workflows/ros-fs-assets.yml"
                  contains "binary=praxis" assets "legacy assets are built from the praxis binary"
                  contains "release.json" assets "new tags declare their version in release.json"

                  [ ".github/workflows/native-release.yml"; ".github/workflows/ros-fs-assets.yml"; ".github/workflows/release.yml"; "scripts/praxis-release-bump.sh"; "scripts/praxis-remote-enable.sh" ]
                  |> List.iter (fun file ->
                      let text = readRepositoryFile file
                      Assert.isTrue (not (Regex.IsMatch(text, @"(?m)^[^#\n]*\b(setup-node|npm (version|run|install|ci)|node (-e|-p))\b"))) $"{file} still runs Node or npm")

                  let declared = text (readJson (repositoryFile "release.json")).["version"]
                  Assert.equal $"praxis {declared}" ((cli (temporaryDirectory "p5-version") [ "--version" ] |> ok).Output.Trim()) }

          { Name = "P6 postmerge: PRAXIS_GIT_REMOTE_TIMEOUT_SECONDS reaches the ROS_ reader main's durability checks use"
            Run =
              fun () ->
                  let lookup name =
                      if name = "PRAXIS_GIT_REMOTE_TIMEOUT_SECONDS" then Some "7" else None

                  Assert.equal [ "ROS_GIT_REMOTE_TIMEOUT_SECONDS", "7" ] (EnvironmentAliases.legacyAssignments lookup) }

          { Name = "P7 postmerge: every decision and requirement ID names exactly one record, with the renumbered PR #92 records distinct from main's"
            Run =
              fun () ->
                  let records directory =
                      Directory.GetFiles(repositoryFile directory, "*.md")
                      |> Array.map Path.GetFileName
                      |> Array.choose (fun name ->
                          let m = Regex.Match(name, @"^((DF|RQ)-[A-Z]+-\d{4}-[A-Z]\d+)--(.+)\.md$")
                          if m.Success then Some(m.Groups[1].Value, m.Groups[3].Value) else None)
                      |> List.ofArray

                  let all = records "research/decisions" @ records "research/requirements"

                  let duplicated =
                      all |> List.countBy fst |> List.filter (fun (_, count) -> count > 1) |> List.map fst

                  Assert.empty duplicated

                  [ "DF-ROS-2026-A042", "durable-work-checkpoints-and-executor-continuation"
                    "DF-ROS-2026-A043", "effective-current-step-telemetry-segmentation"
                    "DF-ROS-2026-A049", "fsharp-dotnet-only-repository"
                    "DF-ROS-2026-A050", "praxis-canonical-name-ros-compatibility"
                    "RQ-ROS-2026-A022", "durable-work-checkpoints-and-agent-continuity"
                    "RQ-ROS-2026-A023", "remote-requests-without-actions-dispatch"
                    "RQ-ROS-2026-A024", "fsharp-dotnet-only-repository"
                    "RQ-ROS-2026-A025", "praxis-canonical-name" ]
                  |> List.iter (fun (id, slug) -> Assert.equal (Some slug) (all |> List.tryFind (fst >> (=) id) |> Option.map snd)) } ]
