namespace Praxis.Tests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Execution
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Execution
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
module LocalWorkerAssignmentTests =
    let private ok = function Ok v -> v | Error e -> failwith (sprintf "%A" e)
    let private t name run = { Name = "portability assignment: " + name; Run = run }
    let private refused r = Assert.isTrue (Result.isError r) "changed native assignment accepted"
    let internal provision root (packet: LocalWorkerPacket) =
        // Group homes and envelope work items are qualified from the origin
        // remote, as the real group and execution commands record them.
        let home = "example/" + packet.RepositoryIdentity
        let remote = "https://example.invalid/" + home + ".git"
        let remotes = (CliHarness.git root [ "remote" ]).Split('\n')
        let verb = if Array.contains "origin" remotes then "set-url" else "add"
        CliHarness.git root [ "remote"; verb; "origin"; remote ] |> ignore
        let configuration = CliHarness.read root "ros.json" |> JsonNode.Parse
        configuration["repository"] <- JsonSerializer.SerializeToNode {| id = packet.RepositoryIdentity |}
        CliHarness.write root "ros.json" (configuration.ToJsonString())
        let items = packet.Members |> List.map (fun m ->
            {| id = m.WorkItemId; ``type`` = "task"; state = "active"; semanticState = "active"; telemetryExecutionIds = [ m.ExecutionId ] |})
        CliHarness.write root ".ros/context/current.json" (JsonSerializer.Serialize {| workItems = items |})
        let actor : Actor = { Kind = ActorKind.Human; Id = "fixture-owner"; Provider = None; Model = None; Runtime = None }
        let execution : GroupExecutionRecord =
            { Id = "GEX-fixture"; GroupId = packet.GroupId; Actor = actor; StartedAt = packet.IssuedAt.ToString("O")
              Repository = packet.RepositoryIdentity; Order = packet.Members |> List.map _.WorkItemId
              Mode = ExecutionMode.Grouped; Basis = []; OptOuts = []
              Members = packet.Members |> List.map (fun m -> { WorkItemId = m.WorkItemId; ExecutionId = m.ExecutionId; BegunAt = packet.IssuedAt.ToString("O"); Mode = ExecutionMode.Grouped })
              Fallback = None; EndedAt = Some(packet.IssuedAt.ToString("O")); Successors = []; Telemetry = []; Prediction = None; Outcome = None }
        let group : StoredWorkGroup =
            { Declaration = { Id = packet.GroupId; Members = packet.Members |> List.map _.WorkItemId; Kind = None; Origin = GroupOrigin.HumanDeclared
                              SharedContext = []; ExecutionRepository = Some home; CrossRepository = false; ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] }
              HomeRepository = Some home; Dependencies = []; Verifications = []; CreatedAt = packet.IssuedAt.ToString("O"); CreatedBy = actor
              History = []; Checkpoints = []; Executions = [ execution ] }
        FileWorkGroupRepository.writeStore root { Groups = [ group ]; References = [] } |> ok
        let envelope id work actorId =
            ExecutionEnvelope.create id (home + ":" + work)
                { Id = actorId; Kind = "agent"; Provider = None; Model = None; Runtime = None }
                (RoleAuthority.defaultFor packet.Role) packet.SourceCommit MutationBoundary.empty None packet.IssuedAt |> ok
        for m in packet.Members do
            ExecutionStore.saveEnvelope root (envelope m.ExecutionId m.WorkItemId "fixture-member")
            CliHarness.write root (".ros/telemetry/executions/" + m.ExecutionId + ".json")
                (JsonSerializer.Serialize {| executionId = m.ExecutionId; workItemId = m.WorkItemId; status = "active" |})
        ExecutionStore.saveEnvelope root (envelope packet.ParentExecutionId "COORDINATOR" "fixture-controller")
        ExecutionStore.saveEnvelope root { envelope packet.ChildExecutionId "COORDINATOR" packet.WorkerId with Parent = Some packet.ParentExecutionId }
    let private fixture test =
        let directory = CliHarness.temporaryDirectory "praxis-native-assignment"
        try
            CliHarness.rosOk directory [ "init" ] |> ignore
            CliHarness.git directory [ "init" ] |> ignore
            CliHarness.git directory [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "--allow-empty"; "-m"; "fixture source" ] |> ignore
            let packet = { LocalAgentHandoffTests.packet with SourceCommit = CliHarness.git directory [ "rev-parse"; "HEAD" ] }
            provision directory packet
            test directory packet
        finally CliHarness.removeTemporaryGitDirectory directory
    let private validate root packet = FileLocalWorkerAssignment.validate root LocalAgentHandoffTests.packet.RepositoryIdentity packet
    let private change root path edit =
        let node = CliHarness.read root path |> JsonNode.Parse
        edit node
        CliHarness.write root path (node.ToJsonString())

    let tests =
        [ t "native membership, execution envelopes and a real Git source are freshly observed without writes" (fun () -> fixture (fun root packet ->
              let before = CliHarness.read root ".ros/context/current.json"
              Assert.equal (Ok()) (validate root packet)
              Assert.equal (Ok()) (validate root { packet with Members = List.rev packet.Members })
              Assert.equal before (CliHarness.read root ".ros/context/current.json")))
          t "changed group membership, home and native execution mapping refuse" (fun () ->
              for edit in [ (fun (g: StoredWorkGroup) -> { g with HomeRepository = Some "other" })
                            (fun g -> { g with Declaration = { g.Declaration with Members = g.Declaration.Members.Tail } })
                            (fun g -> { g with Executions = [] })
                            (fun g -> { g with Executions = g.Executions @ g.Executions })
                            (fun g -> { g with Executions = g.Executions |> List.map (fun e -> { e with Members = e.Members.Tail }) }) ] do
                  fixture (fun root packet ->
                      let store = FileWorkGroupRepository.readStore root |> ok
                      FileWorkGroupRepository.writeStore root { store with Groups = store.Groups |> List.map edit } |> ok
                      validate root packet |> refused))
          t "inactive members, replaced links and extra active telemetry refuse" (fun () ->
              for fault in [ "state"; "link"; "finalized"; "ambiguous" ] do fixture (fun root packet ->
                  match fault with
                  | "state" -> change root ".ros/context/current.json" (fun n -> n["workItems"].[0].["semanticState"] <- JsonValue.Create "blocked")
                  | "link" -> change root ".ros/context/current.json" (fun n -> n["workItems"].[0].["telemetryExecutionIds"] <- JsonSerializer.SerializeToNode [ "EXE-other" ])
                  | "finalized" -> change root (".ros/telemetry/executions/" + packet.Members.Head.ExecutionId + ".json") (fun n -> n["status"] <- JsonValue.Create "finalized")
                  | _ -> CliHarness.write root ".ros/telemetry/executions/EXE-extra.json" (JsonSerializer.Serialize {| executionId = "EXE-extra"; workItemId = packet.Members.Head.WorkItemId; status = "active" |})
                  validate root packet |> refused))
          t "member envelope state, work identity and source baseline cannot be substituted" (fun () ->
              for fault in [ "state"; "workItem"; "baselineRevision" ] do fixture (fun root packet ->
                  change root (".ros/executions/" + packet.Members.Head.ExecutionId + "/envelope.json") (fun n -> n[fault] <- JsonValue.Create(if fault = "state" then "completed" elif fault = "workItem" then "other:WI-1" else String('f', 40)))
                  validate root packet |> refused))
          t "worker parent, role and actor must match the delegated lineage" (fun () ->
              for fault in [ "parentExecution"; "role"; "actor" ] do fixture (fun root packet ->
                  change root (".ros/executions/" + packet.ChildExecutionId + "/envelope.json") (fun n ->
                      if fault = "actor" then n["actor"]["id"] <- JsonValue.Create "different-worker"
                      else n[fault] <- JsonValue.Create(if fault = "role" then "review" else "EXE-other"))
                  validate root packet |> refused))
          t "malformed, duplicate and filename-crosswired native records refuse" (fun () ->
              for fault in [ "malformed"; "duplicate"; "filename" ] do fixture (fun root packet ->
                  let path = ".ros/telemetry/executions/" + packet.Members.Head.ExecutionId + ".json"
                  match fault with
                  | "malformed" -> CliHarness.write root path "{"
                  | "duplicate" -> CliHarness.write root path (CliHarness.read root path |> _.Replace("{", "{\"status\":\"active\","))
                  | _ -> change root path (fun n -> n["executionId"] <- JsonValue.Create "EXE-other")
                  validate root packet |> refused))
          t "unavailable source, foreign repository and path-shaped execution IDs refuse" (fun () -> fixture (fun root packet ->
              FileLocalWorkerAssignment.validate root "different" packet |> refused
              validate root { packet with ChildExecutionId = "../escape" } |> refused
              let missing = { packet with SourceCommit = String('f', 40) }
              provision root missing
              validate root missing |> refused))
          t "an existing source commit outside current history refuses" (fun () -> fixture (fun root packet ->
              CliHarness.git root [ "checkout"; "--orphan"; "unrelated-fixture" ] |> ignore
              CliHarness.git root [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid"; "commit"; "--allow-empty"; "-m"; "unrelated source fixture" ] |> ignore
              let unrelated = { packet with SourceCommit = CliHarness.git root [ "rev-parse"; "HEAD" ] }
              CliHarness.git root [ "checkout"; "--detach"; packet.SourceCommit ] |> ignore
              provision root unrelated
              validate root unrelated |> refused)) ]
