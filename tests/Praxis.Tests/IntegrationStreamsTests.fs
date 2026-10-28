namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Work

/// `praxis streams check` (EII-B-001): each rule of the integration contract,
/// its negative controls, the JSON contract and the CLI over a real Git
/// repository.
[<RequireQualifiedAccess>]
module IntegrationStreamsTests =
    let private stream id workItem owns dependsOn : StreamContract =
        { Id = id
          WorkItem = workItem
          Ref = $"r/{id}"
          Owns = OwnershipSource.Declared owns
          DependsOn = dependsOn
          RequiredTests = []
          Readiness = ReadinessCondition.all
          Artifacts = [] }

    /// core owns src/engine, shell owns src/app; src/app/Port.fs is a shell
    /// interface persist may change with a recorded approval.
    let private contract: IntegrationContract =
        { Target = None
          ManagedPaths = [ ".ros/**" ]
          CommonPaths = [ "docs/**" ]
          Streams =
            [ stream "core" "WI-1" [ "src/engine/**" ] []
              stream "shell" "WI-2" [ "src/app/**" ] [ "core" ]
              stream "persist" "WI-3" [ "src/persistence/**" ] [ "core" ] ]
          Interfaces =
            [ { Id = "shell-port"
                Owner = "shell"
                Paths = [ "src/app/Port.fs"; "src/app/View.fs" ]
                Approvals =
                  [ { Stream = "persist"
                      Paths = [ "src/app/Port.fs" ]
                      Authority = ApprovalAuthority.Recorded "coordinator"
                      Reason = Some "wire the persistence port" } ] } ] }

    let private verifiedAtTip = CompletionEvidence.Verified("c0ffee", [ "sha256:receipt" ], [ ".ros/events/events.jsonl" ])

    let private facts (id: string) (pending: string list) : StreamFacts =
        { Stream = id
          Tip = Some $"tip-{id}"
          MergeBase = Some "base"
          PendingPaths = pending
          TargetIncomingPaths = []
          Integrated = false
          PresentArtifacts = []
          Completion = verifiedAtTip }

    let private integratedFacts id = { facts id [] with Integrated = true }

    let private run (contract: IntegrationContract) (streams: StreamFacts list) (pairs: PairFacts list) =
        let resolved = IntegrationStreams.resolve [] [] contract

        IntegrationStreams.check
            contract
            resolved
            None
            { Target = "main"
              TargetCommit = Some "target"
              Streams = streams
              Pairs = pairs
              KnownDecisions = [ "DF-ROS-2026-A001" ] }

    let private streamOf (report: IntegrationReport) id = report.Streams |> List.find (fun stream -> stream.Stream = id)

    let private condition (report: StreamReport) kind =
        (report.Conditions |> List.find (fun result -> result.Condition = kind)).Met

    let private codes (report: StreamReport) = report.Findings |> List.map PathFinding.code

    let private allPresent = [ integratedFacts "core"; facts "shell" []; facts "persist" [] ]

    let private withShell (pending: string list) =
        [ integratedFacts "core"; facts "shell" pending; facts "persist" [] ]

    let private withPersist (pending: string list) =
        [ integratedFacts "core"; facts "shell" []; facts "persist" pending ]

    let private sampleJson =
        """{ "planner": { "areas": { "WI-2": ["src/app"] } },
             "integration": {
               "schema": "praxis.integration-contract/1",
               "target": "main",
               "managedPaths": [".ros/**"],
               "streams": [
                 { "id": "core", "workItem": "WI-1", "ref": "r/core", "owns": ["src/engine/**"], "requiredTests": ["tests/Engine"] },
                 { "id": "shell", "workItem": "WI-2", "ref": "r/shell", "ownsPlannerArea": true, "dependsOn": ["core"], "readiness": ["ownership", "overlap"], "artifacts": ["dist/**"] } ],
               "interfaces": [
                 { "id": "port", "owner": "shell", "paths": ["src/app/Port.fs"],
                   "approvals": [ { "stream": "core", "decision": "DF-ROS-2026-A001", "reason": "r" } ] } ] } }"""

    let private parseError (json: string) =
        match IntegrationContractJson.parse json with
        | Ok _ -> failwith "expected the contract to be refused"
        | Error message -> message

    let private unitTests =
        [ { Name = "streams check: a stream changing only its owned, common and managed paths is clean"
            Run =
              fun () ->
                  let report = run contract (withShell [ "src/app/Shell.fs"; "docs/guide.md"; ".ros/events/events.jsonl" ]) []
                  let shell = streamOf report "shell"
                  Assert.empty shell.Findings
                  Assert.equal [ ".ros/events/events.jsonl" ] shell.ManagedPaths
                  Assert.equal [ "common"; "owned" ] (shell.Covered |> List.map _.Basis)
                  Assert.equal (Some true) (condition shell ReadinessCondition.Ownership)
                  Assert.equal Readiness.Ready shell.Readiness }

          { Name = "streams check: negative control - a path outside the stream's ownership is flagged with its owner"
            Run =
              fun () ->
                  let report = run contract (withShell [ "src/app/Shell.fs"; "src/engine/Kernel.fs"; "README.md" ]) []
                  let shell = streamOf report "shell"

                  Assert.equal
                      [ PathFinding.OutsideOwnership("README.md", [])
                        PathFinding.OutsideOwnership("src/engine/Kernel.fs", [ "core" ]) ]
                      shell.Findings

                  Assert.equal (Some false) (condition shell ReadinessCondition.Ownership)
                  Assert.equal Readiness.Blocked shell.Readiness
                  Assert.equal false (IntegrationReport.passes report) }

          { Name = "streams check: negative control - an unapproved shared-interface edit is flagged even inside another stream's area"
            Run =
              fun () ->
                  let report = run contract (withPersist [ "src/persistence/Store.fs"; "src/app/View.fs" ]) []
                  let persist = streamOf report "persist"
                  Assert.equal [ PathFinding.InterfaceUnapproved("src/app/View.fs", "shell-port", "shell") ] persist.Findings
                  Assert.equal (Some false) (condition persist ReadinessCondition.Interfaces)
                  Assert.equal (Some true) (condition persist ReadinessCondition.Ownership)
                  Assert.equal Readiness.Blocked persist.Readiness }

          { Name = "streams check: a recorded approval covers exactly its paths; the interface owner needs none"
            Run =
              fun () ->
                  let report = run contract [ integratedFacts "core"; facts "shell" [ "src/app/View.fs" ]; facts "persist" [ "src/app/Port.fs" ] ] []
                  let persist, shell = streamOf report "persist", streamOf report "shell"
                  Assert.empty persist.Findings
                  Assert.equal [ "approved:shell-port:recorded:coordinator" ] (persist.Covered |> List.map _.Basis)
                  Assert.equal [ "interface-owner:shell-port" ] (shell.Covered |> List.map _.Basis) }

          { Name = "streams check: an approval citing a Decision Record counts only when the record exists at the target"
            Run =
              fun () ->
                  let cites decision =
                      { contract with
                          Interfaces =
                            [ { contract.Interfaces.Head with
                                  Approvals = [ { Stream = "persist"; Paths = []; Authority = ApprovalAuthority.Decision decision; Reason = None } ] } ] }

                  let present = streamOf (run (cites "DF-ROS-2026-A001") (withPersist [ "src/app/View.fs" ]) []) "persist"
                  let absent = streamOf (run (cites "DF-ROS-2026-A999") (withPersist [ "src/app/View.fs" ]) []) "persist"
                  Assert.empty present.Findings
                  Assert.equal [ "approved:shell-port:decision:DF-ROS-2026-A001" ] (present.Covered |> List.map _.Basis)
                  Assert.equal [ PathFinding.DecisionNotFound("src/app/View.fs", "shell-port", "DF-ROS-2026-A999") ] absent.Findings
                  Assert.equal (Some false) (condition absent ReadinessCondition.Interfaces) }

          { Name = "streams check: negative control - two sibling streams changing the same path overlap before merge"
            Run =
              fun () ->
                  let pair =
                      { Left = "persist"
                        Right = "shell"
                        LeftPaths = [ "src/app/Port.fs"; ".ros/events/events.jsonl"; "src/persistence/Store.fs" ]
                        RightPaths = [ "src/app/Port.fs"; ".ros/events/events.jsonl"; "src/app/Shell.fs" ] }

                  let report = run contract [ integratedFacts "core"; facts "shell" [ "src/app/Port.fs" ]; facts "persist" [ "src/app/Port.fs" ] ] [ pair ]
                  let overlap = Assert.single report.SiblingOverlaps
                  Assert.equal [ "src/app/Port.fs" ] overlap.Paths
                  Assert.equal [ ".ros/events/events.jsonl" ] overlap.ManagedPaths

                  for id in [ "shell"; "persist" ] do
                      let stream = streamOf report id
                      Assert.equal (Some false) (condition stream ReadinessCondition.Overlap)
                      Assert.equal Readiness.Blocked stream.Readiness }

          { Name = "streams check: overlap with the target is sync check's overlap, applied to the stream's pending changes"
            Run =
              fun () ->
                  let shell = { facts "shell" [ "src/app/Shell.fs"; "docs/a.md"; ".ros/x.json" ] with TargetIncomingPaths = [ ".ros/x.json"; "docs/a.md"; "src/engine/K.fs" ] }
                  let report = run contract [ integratedFacts "core"; shell; facts "persist" [] ] []
                  let result = streamOf report "shell"
                  Assert.equal [ "docs/a.md" ] result.TargetOverlap
                  Assert.equal (Praxis.Domain.Git.UpstreamSync.overlap [ "docs/a.md"; "src/app/Shell.fs" ] [ "docs/a.md"; "src/engine/K.fs" ]) result.TargetOverlap
                  Assert.equal (Some false) (condition result ReadinessCondition.Overlap) }

          { Name = "streams check: negative control - a dependency cycle is an error and blocks every stream in it"
            Run =
              fun () ->
                  let cyclic =
                      { contract with
                          Streams =
                            [ stream "core" "WI-1" [ "src/engine/**" ] [ "persist" ]
                              stream "shell" "WI-2" [ "src/app/**" ] [ "core" ]
                              stream "persist" "WI-3" [ "src/persistence/**" ] [ "shell" ] ] }

                  let report = run cyclic allPresent []
                  Assert.equal [ [ "core"; "persist"; "shell" ] ] report.Cycles
                  Assert.equal false (IntegrationReport.passes report)

                  for id in [ "core"; "shell"; "persist" ] do
                      Assert.equal (Some false) (condition (streamOf report id) ReadinessCondition.Dependencies) }

          { Name = "streams check: the integration sequence is topological, adds hard planner dependencies, and waits for unintegrated producers"
            Run =
              fun () ->
                  let planner: DeclaredDependency list =
                      [ { From = "WI-3"; To = "WI-2"; Kind = DependencyKind.Hard }
                        { From = "WI-2"; To = "WI-9"; Kind = DependencyKind.Hard }
                        { From = "WI-1"; To = "WI-3"; Kind = DependencyKind.Soft } ]

                  let resolved = IntegrationStreams.resolve [] planner contract
                  Assert.equal [ "core", "contract"; "shell", "planner" ] (resolved |> List.find (fun item -> item.Contract.Id = "persist")).Dependencies

                  let report =
                      IntegrationStreams.check contract resolved None
                          { Target = "main"; TargetCommit = None; Streams = [ facts "core" []; facts "shell" []; facts "persist" [] ]; Pairs = []; KnownDecisions = [] }

                  Assert.equal [ "core"; "shell"; "persist" ] report.Sequence
                  Assert.empty report.Cycles
                  Assert.equal (Some false) (condition (streamOf report "shell") ReadinessCondition.Dependencies)
                  Assert.equal (Some true) (condition (streamOf report "core") ReadinessCondition.Dependencies) }

          { Name = "streams check: negative control - missing required-test evidence is unverified, never ready"
            Run =
              fun () ->
                  let withTests = { contract with Streams = contract.Streams |> List.map (fun item -> { item with RequiredTests = [ "tests/Shell" ] }) }
                  let missing = { facts "shell" [ "src/app/Shell.fs" ] with Completion = CompletionEvidence.Missing }
                  let stale = { facts "shell" [ "src/app/Shell.fs" ] with Completion = CompletionEvidence.Verified("c1", [], [ "src/app/Shell.fs" ]) }
                  let current = facts "shell" [ "src/app/Shell.fs" ]

                  let shellWith shellFacts = streamOf (run withTests [ integratedFacts "core"; shellFacts; facts "persist" [] ] []) "shell"
                  let missing, stale, current = shellWith missing, shellWith stale, shellWith current

                  for report in [ missing; stale ] do
                      Assert.equal [ "unverified" ] (report.RequiredTests |> List.map _.Status)
                      Assert.equal None (condition report ReadinessCondition.VerifiedCompletion)
                      Assert.equal Readiness.Unverified report.Readiness

                  Assert.equal [ "covered-by-verified-completion" ] (current.RequiredTests |> List.map _.Status)
                  Assert.equal Readiness.Ready current.Readiness }

          { Name = "streams check: ownership can come from planner.areas without re-declaring paths"
            Run =
              fun () ->
                  let byArea = { contract with Streams = [ { (stream "shell" "WI-2" [] []) with Owns = OwnershipSource.PlannerArea } ]; Interfaces = [] }
                  let resolved = IntegrationStreams.resolve [ "WI-2", [ "src/app" ] ] [] byArea
                  Assert.equal "planner.areas[WI-2]" resolved.Head.OwnershipOrigin
                  let check path = IntegrationStreams.classify byArea resolved Set.empty "shell" path
                  Assert.equal (Ok "owned") (check "src/app/View.fs")
                  Assert.equal (Ok "owned") (check "src/app")
                  Assert.equal (Error(PathFinding.OutsideOwnership("src/apparent.fs", []))) (check "src/apparent.fs")
                  let unresolved = IntegrationStreams.resolve [] [] byArea
                  Assert.equal [ "stream 'shell' owns no paths (planner.areas[WI-2] is empty)" ] (IntegrationStreams.contractFindings byArea unresolved) }

          { Name = "streams check: missing expected artifacts and an unresolved ref are reported, not assumed"
            Run =
              fun () ->
                  let withArtifact = { contract with Streams = contract.Streams |> List.map (fun item -> if item.Id = "shell" then { item with Artifacts = [ "dist/**" ] } else item) }
                  let report = run withArtifact [ integratedFacts "core"; facts "shell" []; { facts "persist" [] with Tip = None } ] []
                  Assert.equal (Some false) (condition (streamOf report "shell") ReadinessCondition.Artifacts)
                  let persist = streamOf report "persist"
                  Assert.equal Readiness.Unavailable persist.Readiness
                  Assert.isTrue (persist.Conditions |> List.forall (fun result -> result.Met.IsNone)) "an unresolved ref decides nothing" }

          { Name = "streams check: contract findings name unknown streams"
            Run =
              fun () ->
                  let broken = { contract with Streams = [ stream "core" "WI-1" [ "src/**" ] [ "ghost" ] ]; Interfaces = [ { contract.Interfaces.Head with Owner = "nobody" } ] }
                  let findings = IntegrationStreams.contractFindings broken (IntegrationStreams.resolve [] [] broken)

                  Assert.equal
                      [ "stream 'core' depends on unknown stream 'ghost'"
                        "interface 'shell-port' names unknown owner 'nobody'"
                        "interface 'shell-port' approves unknown stream 'persist'" ]
                      findings }

          { Name = "streams check: Grouping.topologicalOrder places prerequisites first and reports cycles"
            Run =
              fun () ->
                  let edges = Map.ofList [ "a", [ "b" ]; "b", []; "c", [ "d" ]; "d", [ "c" ] ]
                  let order, cycles = Grouping.topologicalOrder [ "d"; "c"; "b"; "a" ] (fun node -> edges[node])
                  Assert.equal [ "b"; "a"; "c"; "d" ] order
                  Assert.equal [ [ "c"; "d" ] ] cycles }

          { Name = "streams contract: the JSON contract parses from ros.json, including planner-area ownership and decision approvals"
            Run =
              fun () ->
                  match IntegrationContractJson.parse sampleJson with
                  | Error message -> failwith message
                  | Ok parsed ->
                      Assert.equal (Some "main") parsed.Target
                      Assert.equal [ "core"; "shell" ] (parsed.Streams |> List.map _.Id)
                      Assert.equal OwnershipSource.PlannerArea parsed.Streams[1].Owns
                      Assert.equal [ ReadinessCondition.Ownership; ReadinessCondition.Overlap ] parsed.Streams[1].Readiness
                      Assert.equal ReadinessCondition.all parsed.Streams[0].Readiness
                      Assert.equal (ApprovalAuthority.Decision "DF-ROS-2026-A001") parsed.Interfaces.Head.Approvals.Head.Authority }

          { Name = "streams contract: malformed contracts are refused with the offending field"
            Run =
              fun () ->
                  let replace (oldValue: string) (newValue: string) = sampleJson.Replace(oldValue, newValue)
                  Assert.isTrue ((parseError (replace "praxis.integration-contract/1" "praxis.integration-contract/9")).Contains "expected 'praxis.integration-contract/1'") "schema"
                  Assert.isTrue ((parseError (replace "\"ownsPlannerArea\": true" "\"ownsPlannerArea\": true, \"owns\": [\"x\"]")).Contains "both owns and ownsPlannerArea") "ownership"
                  Assert.isTrue ((parseError (replace "\"decision\": \"DF-ROS-2026-A001\", " "")).Contains "needs a decision") "approval"
                  Assert.isTrue ((parseError (replace "\"ownership\", \"overlap\"" "\"ownership\", \"merged\"")).Contains "unknown condition 'merged'") "readiness" }

          { Name = "streams contract: the published schema names the contract version and its readiness conditions"
            Run =
              fun () ->
                  let schema = JsonNode.Parse(File.ReadAllText(Path.Combine(CliPort.repositoryRoot.Value, "schemas/praxis-integration-contract.schema.json")))
                  Assert.equal IntegrationContract.Schema (schema["title"].GetValue<string>())
                  Assert.equal IntegrationContract.Schema (schema["properties"].["schema"].["const"].GetValue<string>())
                  let conditions = schema["$defs"].["stream"].["properties"].["readiness"].["items"].["enum"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList
                  Assert.equal (ReadinessCondition.all |> List.map ReadinessCondition.code) conditions } ]

    let private commitOn root (branch: string) (path: string) (content: string) =
        CliHarness.git root [ "checkout"; "-q"; branch ] |> ignore
        CliHarness.write root path content
        CliHarness.commitAll root $"{branch}: {path}"

    let private cliContract =
        """{ "integration": {
               "schema": "praxis.integration-contract/1",
               "target": "main",
               "managedPaths": [".ros/**"],
               "streams": [
                 { "id": "core", "workItem": "WI-1", "ref": "core", "owns": ["engine/**"], "readiness": ["ownership", "interfaces", "overlap", "dependencies"] },
                 { "id": "shell", "workItem": "WI-2", "ref": "shell", "owns": ["app/**"], "dependsOn": ["core"], "requiredTests": ["tests/app"] } ],
               "interfaces": [ { "id": "port", "owner": "shell", "paths": ["app/Port.fs"] } ] } }"""

    let private cliTests =
        [ { Name = "streams check CLI: flags ownership, interface and sibling overlap from Git facts before merging"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-streams"

                  try
                      CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      CliHarness.write root "ros.json" cliContract
                      CliHarness.write root "engine/Kernel.fs" "kernel\n"
                      CliHarness.write root "app/Port.fs" "port\n"
                      CliHarness.commitAll root "baseline"
                      CliHarness.git root [ "branch"; "core" ] |> ignore
                      CliHarness.git root [ "branch"; "shell" ] |> ignore
                      commitOn root "core" "engine/Kernel.fs" "kernel v2\n"
                      commitOn root "core" "app/Port.fs" "port from core\n"
                      commitOn root "shell" "app/Port.fs" "port from shell\n"
                      commitOn root "shell" "app/View.fs" "view\n"
                      CliHarness.git root [ "checkout"; "-q"; "main" ] |> ignore
                      let headBefore = CliHarness.git root [ "rev-parse"; "HEAD" ]

                      let result = CliHarness.ros root [ "streams"; "check"; "--json" ]
                      Assert.equal 3 result.Exit
                      let report = JsonNode.Parse result.Out
                      Assert.equal "praxis.streams-check/1" (report["schema"].GetValue<string>())
                      Assert.equal [ "core"; "shell" ] (report["sequence"].AsArray() |> Seq.map _.GetValue<string>() |> Seq.toList)
                      let streams = report["streams"].AsArray()
                      let core = streams |> Seq.find (fun node -> node["id"].GetValue<string>() = "core")
                      let shell = streams |> Seq.find (fun node -> node["id"].GetValue<string>() = "shell")
                      let finding = core["findings"].AsArray() |> Seq.exactlyOne
                      Assert.equal "interface-unapproved" (finding["code"].GetValue<string>())
                      Assert.equal "app/Port.fs" (finding["path"].GetValue<string>())
                      Assert.equal "blocked" (core["readiness"].GetValue<string>())
                      Assert.equal 0 (shell["findings"].AsArray().Count)
                      Assert.equal "unverified" (shell["requiredTests"].[0].["status"].GetValue<string>())
                      let overlap = report["siblingOverlaps"].AsArray() |> Seq.exactlyOne
                      Assert.equal "app/Port.fs" (overlap["paths"].[0].GetValue<string>())
                      Assert.equal headBefore (CliHarness.git root [ "rev-parse"; "HEAD" ])

                      let only = CliHarness.ros root [ "streams"; "check"; "--stream"; "core" ]
                      Assert.equal 3 only.Exit
                      Assert.isTrue (only.Out.Contains "interface-unapproved app/Port.fs (interface port, owner shell)") only.Out
                      Assert.isTrue (not (only.Out.Contains "stream shell (")) "--stream limits the reported streams"

                      let unknown = CliHarness.ros root [ "streams"; "check"; "--stream"; "ghost" ]
                      Assert.equal 2 unknown.Exit
                  finally
                      try Directory.Delete(root, true) with _ -> () }

          { Name = "streams check CLI: a stream with clean ownership and integrated dependencies passes against --against"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-streams-pass"

                  try
                      CliHarness.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
                      CliHarness.write root "contract.json" (cliContract.Replace("\"requiredTests\": [\"tests/app\"]", "\"readiness\": [\"ownership\", \"interfaces\", \"overlap\", \"dependencies\"]"))
                      CliHarness.write root "engine/Kernel.fs" "kernel\n"
                      CliHarness.commitAll root "baseline"
                      CliHarness.git root [ "branch"; "core" ] |> ignore
                      CliHarness.git root [ "branch"; "shell" ] |> ignore
                      commitOn root "shell" "app/View.fs" "view\n"
                      CliHarness.git root [ "checkout"; "-q"; "main" ] |> ignore

                      let result = CliHarness.ros root [ "streams"; "check"; "--contract"; "contract.json"; "--against"; "main"; "--stream"; "shell"; "--json" ]
                      Assert.equal 0 result.Exit
                      let report = JsonNode.Parse result.Out
                      Assert.equal true (report["passed"].GetValue<bool>())
                      let shell = report["streams"].[0]
                      Assert.equal "ready" (shell["readiness"].GetValue<string>())
                      Assert.equal "owned" (shell["covered"].[0].["basis"].GetValue<string>())
                  finally
                      try Directory.Delete(root, true) with _ -> () } ]

    let tests = unitTests @ cliTests
