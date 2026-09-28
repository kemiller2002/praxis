namespace Ros.Tests

open System.IO

/// End-to-end provenance tests through the real CLI.
///
/// Actor resolution (RQ-ROS-2026-A001) used to exist twice -- the F#
/// `Ros.Domain.Provenance.ActorResolution` and Node's `resolveActor` in
/// tools/ros_telemetry.mjs -- and tests/provenance-actor-fsharp-differential.test.mjs
/// compared them live. The Node side was run one last time on 2026-09-28 over
/// every case below (with every identity variable cleared first) and its exact
/// output, key order included, is frozen here: the actor is hashed into every
/// event id, so the serialized form is the contract.
///
/// The rest is ported from tests/provenance-followups.test.mjs.
[<RequireQualifiedAccess>]
module ProvenanceCliTests =
    let private unknownActor =
        """{"kind":"unknown","id":"unknown","provider":"unknown","model":"unknown","runtime":"unknown"}"""

    /// (case, environment, identity flags, Node's resolveActor output).
    let private actorCases =
        [ "nothing known", [], [], unknownActor
          "Claude Code runtime",
          [ "CLAUDE_CODE_SESSION_ID", "s-1" ],
          [],
          """{"kind":"agent","id":"anthropic/claude-code","provider":"anthropic","model":"unknown","runtime":"claude-code"}"""
          "Codex runtime with model",
          [ "CODEX_SESSION_ID", "c-1"; "ROS_TELEMETRY_MODEL", "gpt-5-codex" ],
          [],
          """{"kind":"agent","id":"openai/codex","provider":"openai","model":"gpt-5-codex","runtime":"codex"}"""
          "Gemini CLI runtime",
          [ "GEMINI_SESSION_ID", "g-1" ],
          [],
          """{"kind":"agent","id":"google/gemini-cli","provider":"google","model":"unknown","runtime":"gemini-cli"}"""
          "GitHub Actions automation",
          [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9" ],
          [],
          """{"kind":"automation","id":"github/github-actions","provider":"github","model":"unknown","runtime":"github-actions"}"""
          "local model server implies no kind",
          [ "OLLAMA_HOST", "http://localhost:11434" ],
          [],
          """{"kind":"unknown","id":"local/ollama","provider":"local","model":"unknown","runtime":"ollama"}"""
          "declared human via environment", [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ], [], """{"kind":"human","id":"kevin"}"""
          "future provider declared via environment",
          [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "future-ai"; "ROS_TELEMETRY_RUNTIME", "future-cli" ],
          [],
          """{"kind":"agent","id":"future-ai/future-cli","provider":"future-ai","model":"unknown","runtime":"future-cli"}"""
          "an empty provider variable counts as unset",
          [ "ROS_TELEMETRY_PROVIDER", ""; "CLAUDE_CODE_SESSION_ID", "s-1" ],
          [],
          """{"kind":"agent","id":"anthropic/claude-code","provider":"anthropic","model":"unknown","runtime":"claude-code"}"""
          "an explicit unknown provider suppresses runtime detection",
          [ "ROS_TELEMETRY_PROVIDER", "unknown"; "CLAUDE_CODE_SESSION_ID", "s-1" ],
          [],
          unknownActor
          "an explicit unknown runtime suppresses runtime detection", [ "ROS_TELEMETRY_RUNTIME", "unknown"; "CODEX_SESSION_ID", "c-1" ], [], unknownActor
          "an explicit unknown provider suppresses CI detection", [ "ROS_TELEMETRY_PROVIDER", "unknown"; "GITHUB_ACTIONS", "true" ], [], unknownActor
          "a whitespace provider is not a known provider",
          [ "ROS_TELEMETRY_PROVIDER", " "; "ROS_TELEMETRY_RUNTIME", "x" ],
          [],
          """{"kind":"unknown","id":"unknown","provider":"unknown","model":"unknown","runtime":"x"}"""
          "explicit flags override the runtime",
          [ "CLAUDE_CODE_SESSION_ID", "s-1" ],
          [ "--actor-kind"; "agent"; "--agent"; "reviewer-bot"; "--model"; "m-2" ],
          """{"kind":"agent","id":"reviewer-bot","provider":"anthropic","model":"m-2","runtime":"claude-code"}"""
          "a declared human creator", [], [ "--actor-kind"; "human"; "--actor"; "kevin" ], """{"kind":"human","id":"kevin"}""" ]

    let private identity root environment flags =
        CliHarness.rosWith root environment ([ "provenance"; "identity"; "--json" ] @ flags)

    let private actorCaseTest (name: string, environment, flags, expected: string) =
        { Name = $"provenance cli: identity resolves the frozen actor: {name}"
          Run = fun () ->
              CliPort.withRepository "Provenance Actor" (fun root ->
                  let result = identity root environment flags
                  CliPort.exitCode 0 result
                  // Byte-for-byte, key order included.
                  Assert.equal expected (CliPort.compact ((CliPort.parse result.Out)["actor"]))) }

    let private actorTests =
        List.map actorCaseTest actorCases
        @ [ { Name = "provenance cli: identity rejects an invalid explicit actor kind"
              Run = fun () ->
                  CliPort.withRepository "Provenance Actor" (fun root ->
                      let result = identity root [ "ROS_ACTOR_KIND", "robot" ] []
                      CliPort.exitCode 2 result
                      CliPort.contains "unknown actor kind 'robot'" result.Err) } ]

    let private events root =
        File.ReadAllLines(Path.Combine(root, ".ros", "events", "events.jsonl"))
        |> Array.filter (fun line -> line <> "")
        |> Array.map CliPort.parse
        |> Array.toList

    let private claude = [ "CLAUDE_CODE_SESSION_ID", "claude-session-1" ]

    let private rosOkWith root environment arguments =
        let result = CliHarness.rosWith root environment arguments
        CliPort.exitCode 0 result
        result

    let private followupTests =
        [ { Name = "provenance cli: the installer's bookkeeping event carries the automation actor, so the audit sees every event attributed"
            Run = fun () ->
                // F# `init` records the same canonical installer actor the legacy
                // Node bootstrap did, so this assertion (from the Node fixture)
                // also holds for installs made by the retired bootstrap.
                CliPort.withRepository "Provenance Followups" (fun root ->
                    let install = events root |> List.head
                    Assert.equal """{"kind":"automation","id":"ros-bootstrap","runtime":"ros-bootstrap"}""" (CliPort.compact (install["actor"]))
                    let audit = CliPort.parse (CliHarness.rosOk root [ "provenance"; "audit"; "--json" ]).Out
                    Assert.equal (CliPort.number (audit["summary"]["events"])) (CliPort.number (audit["summary"]["eventsWithActor"]))) }
          { Name = "provenance cli: ordo handoff names the producing agent and its bound execution"
            Run = fun () ->
                CliPort.withRepository "Provenance Followups" (fun root ->
                    rosOkWith root claude [ "add"; "Handoff work"; "--id"; "WI-H" ] |> ignore
                    rosOkWith root claude [ "work"; "backlog-transition"; "--action"; "ready"; "--id"; "WI-H"; "--occurred-at"; CliHarness.now () ] |> ignore
                    rosOkWith root claude [ "work"; "start"; "--id"; "WI-H"; "--occurred-at"; CliHarness.now () ] |> ignore

                    let execution =
                        Directory.GetFiles(Path.Combine(root, ".ros", "telemetry", "executions"), "*.json")
                        |> Array.exactlyOne
                        |> Path.GetFileNameWithoutExtension

                    let handoff = CliPort.parse (rosOkWith root claude [ "ordo"; "handoff"; "--revision"; "abc123"; "--source"; "praxis" ]).Out
                    Assert.equal "agent" (CliPort.text (CliPort.at [ "producedBy"; "actor"; "kind" ] handoff))
                    Assert.equal "anthropic/claude-code" (CliPort.text (CliPort.at [ "producedBy"; "actor"; "id" ] handoff))
                    Assert.equal execution (CliPort.text (handoff["producedBy"]["execution"]))

                    let other =
                        CliPort.parse (rosOkWith root [ "CLAUDE_CODE_SESSION_ID", "claude-session-2" ] [ "ordo"; "handoff"; "--revision"; "abc123"; "--source"; "praxis" ]).Out

                    Assert.isTrue (isNull (other["producedBy"]["execution"])) "another session must not claim the bound execution") }
          { Name = "provenance cli: audit reports empty collaboration aggregates for a fresh install"
            Run = fun () ->
                CliPort.withRepository "Provenance Followups" (fun root ->
                    let audit = CliPort.parse (CliHarness.rosOk root [ "provenance"; "audit"; "--json" ]).Out

                    CliPort.deepEqual
                        """{"agentToAgentRevisions":[],"humanCorrectionsOfAgentWork":[],"humanApprovedAgentWork":[],"hotspots":[]}"""
                        (audit["collaboration"])) } ]

    let tests = actorTests @ followupTests
