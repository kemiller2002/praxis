namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Work
open PlanningFixtures

/// `work group create` (PRAXIS-GROUP-01; PRX-GRP-073 phase two): the pure
/// declaration decision, the stored contract, the planner merge, and the
/// command through the real binary.
module WorkGroupTests =
    let private t name run = { Name = $"work group: {name}"; Run = run }

    let private request id members =
        { Id = id
          Members = members
          Kind = None
          ExecutionRepository = None
          CrossRepository = false
          SharedContext = []
          ArchitectureNotes = []
          DeclaredAt = "2026-10-07T00:00:00.000Z"
          DeclaredBy = "tester" }

    let private statuses =
        GroupDeclaration.memberStatuses
            [ queued "A-1" "ready" "2026-09-01T00:00:00Z"
              queued "A-2" "captured" "2026-09-01T00:00:00Z"
              queued "A-3" "blocked" "2026-09-01T00:00:00Z"
              queued "GONE-1" "abandoned" "2026-09-01T00:00:00Z"
              queued "LIVE-1" "ready" "2026-09-01T00:00:00Z"
              queued "DONE-1" "ready" "2026-09-01T00:00:00Z" ]
            [ live "LIVE-1" LiveWorkState.Active; live "DONE-1" LiveWorkState.Complete ]

    let private created result =
        match result with
        | Ok stored -> stored
        | Error rejections -> failwith $"expected a declaration, got {rejections}"

    let private rejected result =
        match result with
        | Ok stored -> failwith $"expected a refusal, got {stored}"
        | Error rejections -> rejections |> List.map GroupRejection.code |> List.sort

    let private domain =
        [ t "an open-member declaration is human-declared and keeps every field" (fun () ->
              let stored =
                  GroupDeclaration.create
                      []
                      statuses
                      { request "GROUP-PRAXIS-PLANNING-001" [ "A-1"; "A-2"; "LIVE-1" ] with
                          Kind = Some "shared-api-surface"
                          ExecutionRepository = Some "praxis"
                          CrossRepository = true
                          SharedContext = [ "src/Ros.Cli" ]
                          ArchitectureNotes = [ "one parser" ] }
                  |> created

              Assert.equal GroupOrigin.HumanDeclared stored.Group.Origin
              Assert.equal (Some GroupKind.SharedApiSurface) stored.Group.Kind
              Assert.equal [ "A-1"; "A-2"; "LIVE-1" ] stored.Group.Members
              Assert.equal (Some "praxis") stored.Group.ExecutionRepository
              Assert.isTrue stored.Group.CrossRepository "cross-repository flag kept"
              Assert.equal [ "src/Ros.Cli" ] stored.Group.SharedContext
              Assert.equal "tester" stored.DeclaredBy)

          t "unknown and terminal members are refused" (fun () ->
              Assert.equal [ "unknown-member" ] (GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "NOPE-9" ]) |> rejected)
              Assert.equal [ "terminal-member" ] (GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "GONE-1" ]) |> rejected)
              Assert.equal [ "terminal-member" ] (GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "DONE-1" ]) |> rejected))

          t "a duplicate group ID is refused" (fun () ->
              let first = GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "A-2" ]) |> created
              Assert.equal [ "duplicate-group-id" ] (GroupDeclaration.create [ first ] statuses (request "GROUP-A-1" [ "A-3"; "LIVE-1" ]) |> rejected))

          t "every reason is reported at once" (fun () ->
              let outcome = GroupDeclaration.create [] statuses { request "group-1" [ "A-1"; "A-1" ] with Kind = Some "nonsense" } |> rejected
              Assert.equal [ "duplicate-member"; "invalid-group-id"; "too-few-members"; "unknown-kind" ] outcome)

          t "a live record outranks the backlog entry it was promoted from" (fun () ->
              Assert.equal (Some(GroupMemberStatus.Open "active")) (statuses.TryFind "LIVE-1")
              Assert.equal (Some(GroupMemberStatus.Terminal "complete")) (statuses.TryFind "DONE-1")
              Assert.equal (Some(GroupMemberStatus.Terminal "abandoned")) (statuses.TryFind "GONE-1"))

          t "validate: later completion is partial completion, not a finding" (fun () ->
              let stored = GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "LIVE-1" ]) |> created
              let laterStatuses = statuses |> Map.add "LIVE-1" (GroupMemberStatus.Terminal "complete")
              Assert.empty (GroupDeclaration.findings [ stored ] laterStatuses))

          t "validate: duplicate IDs, unknown members and bad shape are findings" (fun () ->
              let stored = GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "A-2" ]) |> created
              let broken = { stored with Group = { stored.Group with Id = "bad"; Members = [ "A-1"; "NOPE-9" ] } }
              let findings = GroupDeclaration.findings [ stored; stored; broken ] statuses
              Assert.isTrue (findings |> List.exists (fun (id, message) -> id = "GROUP-A-1" && message.Contains "already declared")) "duplicate ID"
              Assert.isTrue (findings |> List.exists (fun (id, message) -> id = "bad" && message.Contains "NOPE-9")) "unknown member"
              Assert.isTrue (findings |> List.exists (fun (id, message) -> id = "bad" && message.Contains "GROUP-<AREA>")) "invalid ID")

          t "the store round-trips through its JSON contract" (fun () ->
              let stored =
                  GroupDeclaration.create [] statuses { request "GROUP-A-2" [ "A-1"; "A-2" ] with Kind = Some "custom:ledger"; SharedContext = [ "ledger" ] }
                  |> created

              let other = GroupDeclaration.create [ stored ] statuses (request "GROUP-A-1" [ "A-3"; "LIVE-1" ]) |> created

              match PlanningJson.parseGroupStore (PlanningJson.renderGroupStore [ stored; other ]) with
              | Ok parsed -> Assert.equal [ other; stored ] parsed
              | Error message -> failwith message)

          t "a stored declaration reaches the planner exactly as grouping.groups" (fun () ->
              let stored = GroupDeclaration.create [] statuses { request "GROUP-A-1" [ "A-1"; "A-3" ] with Kind = Some "shared-area" } |> created
              let viaStore = GroupDeclaration.mergeInto [ stored ] PlannerConfiguration.defaults

              let viaConfiguration =
                  { PlannerConfiguration.defaults with
                      Grouping = { PlannerConfiguration.defaults.Grouping with Groups = [ stored.Group ] } }

              Assert.equal viaConfiguration viaStore
              let queue = [ queued "A-1" "ready" "2026-09-01T00:00:00Z"; queued "A-3" "ready" "2026-09-01T00:00:00Z" ]
              let recommend configuration =
                  let planningInput = input queue [] history [] configuration
                  Grouping.recommend planningInput (Planner.analyze planningInput)

              let report = recommend viaStore
              Assert.equal (recommend viaConfiguration) report
              let group = report.Groups |> List.find (fun group -> WorkGroupId.value group.Id = "GROUP-A-1")
              Assert.equal GroupOrigin.HumanDeclared group.Origin)

          t "an explicit configuration keeps its own definition of the same ID" (fun () ->
              let stored = GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "A-3" ]) |> created
              let configured = { stored.Group with Members = [ "A-1"; "A-2" ] }

              let configuration =
                  { PlannerConfiguration.defaults with
                      Grouping = { PlannerConfiguration.defaults.Grouping with Groups = [ configured ] } }

              Assert.equal [ configured ] (GroupDeclaration.mergeInto [ stored ] configuration).Grouping.Groups) ]

    // ---- the command through the real binary -----------------------------------

    let private git (root: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("git")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- root
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        child.StandardOutput.ReadToEnd() |> ignore
        child.StandardError.ReadToEnd() |> ignore
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"git {String.Join(' ', arguments)} failed"

    let private write (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private queueDocument =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","nextSeq":1,"items":[
  {"id":"TASK-A","title":"Task A","description":"First.","tags":["alpha"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-B","title":"Task B","description":"Second.","tags":["beta"],"priority":"high","status":"captured","createdAt":"2026-09-02T00:00:00.000Z","updatedAt":"2026-09-02T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-C","title":"Task C","tags":[],"priority":"low","status":"abandoned","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-D","title":"Task D","tags":[],"priority":"low","status":"ready","createdAt":"2026-09-04T00:00:00.000Z","updatedAt":"2026-09-04T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}
]}"""

    let private contextDocument =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[
  {"id":"TASK-D","type":"task","state":"complete","semanticState":"complete","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[]}
]}"""

    let private fixture () =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-group-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        git root [ "init"; "--quiet"; "--initial-branch=main" ]
        git root [ "config"; "user.email"; "fixture@example.invalid" ]
        git root [ "config"; "user.name"; "Fixture" ]
        git root [ "config"; "commit.gpgsign"; "false" ]
        write root ".ros/work/queue.json" queueDocument
        write root ".ros/context/current.json" contextDocument
        git root [ "add"; "-A" ]
        git root [ "commit"; "--quiet"; "-m"; "Praxis state" ]
        root

    let private storePath root = Path.Combine(root, ".ros", "work", "groups.json")

    let private lifecycleBytes (root: string) =
        [ ".ros/work/queue.json"; ".ros/context/current.json" ]
        |> List.map (fun relative -> File.ReadAllText(Path.Combine(root, relative)))

    let private declare root extra =
        PraxisCli.run root None ([ "work"; "group"; "create"; "--occurred-at"; PraxisCli.now () ] @ extra)

    let private validateFindings root =
        let result = PraxisCli.run root None [ "validate"; "--json" ]
        let document = JsonNode.Parse result.Output :?> JsonObject

        document["findings"].AsArray()
        |> Seq.map (fun finding -> finding.AsObject())
        |> Seq.filter (fun finding -> PraxisCli.text (finding["path"]) = ".ros/work/groups.json")
        |> Seq.toList

    let private cli =
        [ t "create stores the declaration and changes no member's lifecycle" (fun () ->
              let root = fixture ()
              let before = lifecycleBytes root

              let result =
                  declare
                      root
                      [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--kind"; "shared-area"
                        "--execution-repository"; "group-fixture"; "--shared-context"; "src/alpha"; "--json" ]
                  |> PraxisCli.ok

              let document = result.Json
              Assert.equal "work-group-create" (PraxisCli.text (document["kind"]))
              Assert.isTrue (not (document["dryRun"].GetValue<bool>())) "not a dry run"
              Assert.equal "human-declared" (PraxisCli.text (document["group"]["origin"]))
              Assert.equal before (lifecycleBytes root)

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok [ stored ] ->
                  Assert.equal "GROUP-FIXTURE-001" stored.Group.Id
                  Assert.equal [ "TASK-A"; "TASK-B" ] stored.Group.Members
                  Assert.equal (Some "group-fixture") stored.Group.ExecutionRepository
              | other -> failwith $"unexpected store: {other}")

          t "--dry-run decides the same way and writes nothing" (fun () ->
              let root = fixture ()
              let result = declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dry run reported"
              Assert.isTrue (not (File.Exists(storePath root))) "dry run wrote the store"
              let refused = declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-C"; "--dry-run"; "--json" ]
              Assert.equal 1 refused.ExitCode
              Assert.isTrue (not (File.Exists(storePath root))) "refused dry run wrote the store")

          t "duplicate IDs, unknown and terminal members are refused with exit 1" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)

              let codes extra =
                  let result = declare root (extra @ [ "--json" ])
                  Assert.equal 1 result.ExitCode
                  result.Json["rejections"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["code"])) |> Seq.toList

              Assert.equal [ "duplicate-group-id" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ])
              Assert.equal [ "unknown-member" ] (codes [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--member"; "TASK-Z" ])
              Assert.equal [ "terminal-member" ] (codes [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--member"; "TASK-C" ])
              Assert.equal [ "terminal-member" ] (codes [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--member"; "TASK-D" ])
              Assert.equal stored (File.ReadAllText(storePath root)))

          t "invalid arguments exit 2" (fun () ->
              let root = fixture ()
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (declare root [ "--member"; "TASK-A"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal 2 (declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--bogus" ]).ExitCode
              Assert.isTrue (not (File.Exists(storePath root))) "nothing written")

          t "plan groups reads the stored declaration" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> PraxisCli.ok

              let group =
                  groups.Json["groups"].AsArray()
                  |> Seq.map (fun entry -> entry.AsObject())
                  |> Seq.find (fun entry -> PraxisCli.text (entry["id"]) = "GROUP-FIXTURE-001")

              Assert.equal "human-declared" (PraxisCli.text (group["origin"])))

          t "validate checks the stored groups" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              Assert.empty (validateFindings root)
              File.WriteAllText(storePath root, File.ReadAllText(storePath root).Replace("\"TASK-B\"", "\"TASK-Z\""))
              let findings = validateFindings root
              Assert.isTrue (findings |> List.exists (fun finding -> (PraxisCli.text (finding["message"])).Contains "TASK-Z")) "unknown member flagged"
              File.WriteAllText(storePath root, "{ not json")
              Assert.isTrue (not (validateFindings root).IsEmpty) "malformed store flagged") ]

    let tests = domain @ cli
