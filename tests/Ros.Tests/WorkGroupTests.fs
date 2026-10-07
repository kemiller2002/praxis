namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Work
open PlanningFixtures

/// `work group create` (PRAXIS-GROUP-01), `work group show`
/// (PRAXIS-GROUP-02), `work group add` (PRAXIS-GROUP-03) and `work group remove` (PRAXIS-GROUP-04; PRX-GRP-073 phase two): the pure declaration decision and
/// view, the stored contract, the planner merge, and the commands through the
/// real binary.
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


    // ---- add: the pure decision -------------------------------------------------

    let private addRequest group item =
        { GroupId = group
          WorkItem = item
          AddedAt = "2026-10-08T00:00:00.000Z"
          AddedBy = "adder" }

    let private here (_: string) = "praxis"

    let private located (repositories: Map<string, string>) (id: string) =
        repositories.TryFind id |> Option.defaultValue "praxis"

    let private baseGroup =
        GroupDeclaration.create [] statuses (request "GROUP-A-1" [ "A-1"; "A-2" ]) |> created

    let private addTests =
        [ t "add: an open item joins, and who added it is recorded" (fun () ->
              let updated = GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "LIVE-1") |> created
              Assert.equal [ "A-1"; "A-2"; "LIVE-1" ] updated.Group.Members
              Assert.equal [ { WorkItem = "LIVE-1"; AddedAt = "2026-10-08T00:00:00.000Z"; AddedBy = "adder" } ] updated.Additions
              Assert.equal "tester" updated.DeclaredBy
              Assert.equal { baseGroup.Group with Members = updated.Group.Members } updated.Group)

          t "add: an undeclared group is refused" (fun () ->
              Assert.equal [ "unknown-group" ] (GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-9" "A-3") |> rejected))

          t "add: unknown and terminal items are refused" (fun () ->
              Assert.equal [ "unknown-member" ] (GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "NOPE-9") |> rejected)
              Assert.equal [ "terminal-member" ] (GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "GONE-1") |> rejected)
              Assert.equal [ "terminal-member" ] (GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "DONE-1") |> rejected))

          t "add: an item already present is refused" (fun () ->
              Assert.equal [ "already-member" ] (GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "A-2") |> rejected))

          t "add: a different execution repository is refused unless the group is cross-repository" (fun () ->
              let elsewhere = located (Map.ofList [ "A-3", "other-repo" ])
              Assert.equal [ "repository-mismatch" ] (GroupDeclaration.add [ baseGroup ] statuses elsewhere (addRequest "GROUP-A-1" "A-3") |> rejected)
              let declared = { baseGroup with Group = { baseGroup.Group with ExecutionRepository = Some "praxis" } }
              Assert.equal [ "repository-mismatch" ] (GroupDeclaration.add [ declared ] statuses elsewhere (addRequest "GROUP-A-1" "A-3") |> rejected)
              let cross = { baseGroup with Group = { baseGroup.Group with CrossRepository = true } }
              let updated = GroupDeclaration.add [ cross ] statuses elsewhere (addRequest "GROUP-A-1" "A-3") |> created
              Assert.equal [ "A-1"; "A-2"; "A-3" ] updated.Group.Members)

          t "add: a declared execution repository outranks the members' own" (fun () ->
              let declared = { baseGroup with Group = { baseGroup.Group with ExecutionRepository = Some "other-repo" } }
              let elsewhere = located (Map.ofList [ "A-3", "other-repo" ])
              Assert.equal [ "A-1"; "A-2"; "A-3" ] (GroupDeclaration.add [ declared ] statuses elsewhere (addRequest "GROUP-A-1" "A-3") |> created).Group.Members
              Assert.equal [ "repository-mismatch" ] (GroupDeclaration.add [ declared ] statuses here (addRequest "GROUP-A-1" "A-3") |> rejected))

          t "add: the mismatch message names both repositories" (fun () ->
              let message =
                  GroupRejection.message (GroupRejection.RepositoryMismatch("A-3", "other-repo", [ "praxis" ]))

              Assert.isTrue (message.Contains "other-repo" && message.Contains "praxis" && message.Contains "cross-repository") message)

          t "add: the location is the planner's own (explicit, inferred external, or current)" (fun () ->
              let queue =
                  [ queued "L-1" "ready" "2026-09-01T00:00:00Z"
                    { queued "L-2" "ready" "2026-09-01T00:00:00Z" with Description = Some "Lives in an external repository." }
                    queued "L-3" "ready" "2026-09-01T00:00:00Z" ]

              let grouping = { PlannerConfiguration.defaults.Grouping with ExecutionRepositories = [ "L-3", "elsewhere" ] }
              let where id = Grouping.locate grouping "praxis" queue id |> fst |> ExecutionLocation.describe
              Assert.equal [ "praxis"; "unknown external repository"; "elsewhere" ] ([ "L-1"; "L-2"; "L-3" ] |> List.map where))

          t "add: additions round-trip, and a store without them still parses" (fun () ->
              let updated = GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "LIVE-1") |> created

              match PlanningJson.parseGroupStore (PlanningJson.renderGroupStore [ updated ]) with
              | Ok parsed -> Assert.equal [ updated ] parsed
              | Error message -> failwith message

              let legacy =
                  """{"schemaVersion":"1.0.0","groups":[{"id":"GROUP-A-1","members":["A-1","A-2"],"kind":null,"origin":"human-declared","sharedContext":[],"executionRepository":null,"crossRepository":false,"architectureNotes":[],"declaredAt":"2026-10-07T00:00:00.000Z","declaredBy":"tester"}]}"""

              match PlanningJson.parseGroupStore legacy with
              | Ok [ parsed ] -> Assert.empty parsed.Additions
              | other -> failwith $"unexpected parse: {other}") ]

    // ---- remove: the pure decision ----------------------------------------------

    let private removeRequest group item =
        { GroupId = group
          WorkItem = item
          RemovedAt = "2026-10-09T00:00:00.000Z"
          RemovedBy = "remover"
          Reason = Some "split out" }

    let private threeMembers =
        GroupDeclaration.add [ baseGroup ] statuses here (addRequest "GROUP-A-1" "LIVE-1") |> created

    let private removeTests =
        [ t "remove: a member leaves, and who removed it is recorded" (fun () ->
              let updated = GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-1" "A-2") |> created
              Assert.equal [ "A-1"; "LIVE-1" ] updated.Group.Members
              Assert.equal [ { WorkItem = "A-2"; RemovedAt = "2026-10-09T00:00:00.000Z"; RemovedBy = "remover"; Reason = Some "split out" } ] updated.Removals
              Assert.equal threeMembers.Additions updated.Additions
              Assert.equal "tester" updated.DeclaredBy
              Assert.equal { threeMembers.Group with Members = updated.Group.Members } updated.Group)

          t "remove: an added member's addition stays recorded after it leaves" (fun () ->
              let updated = GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-1" "LIVE-1") |> created
              Assert.equal [ "A-1"; "A-2" ] updated.Group.Members
              Assert.equal [ "LIVE-1" ] (updated.Additions |> List.map (fun addition -> addition.WorkItem))
              Assert.equal [ "LIVE-1" ] (updated.Removals |> List.map (fun removal -> removal.WorkItem)))

          t "remove: non-members and undeclared groups are refused" (fun () ->
              Assert.equal [ "not-member" ] (GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-1" "A-3") |> rejected)
              Assert.equal [ "not-member" ] (GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-1" "NOPE-9") |> rejected)
              Assert.equal [ "unknown-group" ] (GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-9" "A-1") |> rejected))

          t "remove: the last members cannot be removed" (fun () ->
              Assert.equal [ "last-members" ] (GroupDeclaration.remove [ baseGroup ] (removeRequest "GROUP-A-1" "A-1") |> rejected)
              let message = GroupRejection.message (GroupRejection.LastMembers("A-1", "GROUP-A-1", 1))
              Assert.isTrue (message.Contains "at least two" && message.Contains "GROUP-A-1") message)

          t "remove: a terminal member may leave; its state is not consulted" (fun () ->
              let partial = { threeMembers with Group = { threeMembers.Group with Members = [ "A-1"; "A-2"; "DONE-1" ] } }
              Assert.equal [ "A-1"; "A-2" ] (GroupDeclaration.remove [ partial ] (removeRequest "GROUP-A-1" "DONE-1") |> created).Group.Members)

          t "remove: the result passes the store's own validation" (fun () ->
              let updated = GroupDeclaration.remove [ threeMembers ] (removeRequest "GROUP-A-1" "A-2") |> created
              Assert.empty (GroupDeclaration.findings [ updated ] statuses))

          t "remove: removals round-trip, a missing reason is null, and a store without them still parses" (fun () ->
              let updated =
                  GroupDeclaration.remove [ threeMembers ] { removeRequest "GROUP-A-1" "A-2" with Reason = None } |> created

              match PlanningJson.parseGroupStore (PlanningJson.renderGroupStore [ updated ]) with
              | Ok parsed -> Assert.equal [ updated ] parsed
              | Error message -> failwith message

              Assert.isTrue ((PlanningJson.renderGroupStore [ updated ]).Contains "\"reason\": null") "reason rendered as null"

              let legacy =
                  """{"schemaVersion":"1.0.0","groups":[{"id":"GROUP-A-1","members":["A-1","A-2"],"kind":null,"origin":"human-declared","sharedContext":[],"executionRepository":null,"crossRepository":false,"architectureNotes":[],"declaredAt":"2026-10-07T00:00:00.000Z","declaredBy":"tester","additions":[]}]}"""

              match PlanningJson.parseGroupStore legacy with
              | Ok [ parsed ] -> Assert.empty parsed.Removals
              | other -> failwith $"unexpected parse: {other}") ]

    // ---- show: the pure view ---------------------------------------------------

    let private viewQueue =
        [ queued "V-1" "ready" "2026-09-01T00:00:00Z"
          { queued "V-2" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "V-3" ] }
          queued "V-3" "blocked" "2026-09-03T00:00:00Z"
          queued "V-4" "ready" "2026-09-04T00:00:00Z" ]

    let private viewLive = [ live "V-4" LiveWorkState.Complete ]

    let private viewStatuses = GroupDeclaration.memberStatuses viewQueue viewLive

    let private viewStored =
        { Group =
            { Id = "GROUP-VIEW-001"
              Members = [ "V-1"; "V-2"; "V-3"; "V-4" ]
              Kind = Some GroupKind.SharedArea
              Origin = GroupOrigin.HumanDeclared
              SharedContext = [ "src/view" ]
              ExecutionRepository = None
              CrossRepository = false
              ArchitectureNotes = [ "one renderer" ] }
          DeclaredAt = "2026-10-07T00:00:00.000Z"
          DeclaredBy = "tester"
          Additions = []
          Removals = [] }

    let private plannedView (stored: StoredGroup) =
        let configuration = GroupDeclaration.mergeInto [ stored ] PlannerConfiguration.defaults
        let planningInput = input viewQueue viewLive history [] configuration
        GroupView.planned (Grouping.recommend planningInput (Planner.analyze planningInput)) stored.Group.Id

    let private memberOf (view: GroupView) id = view.Members |> List.find (fun entry -> entry.WorkItemId = id)

    let private viewTests =
        [ t "show: each member reports its own recorded and planning state" (fun () ->
              let view = GroupView.build viewStored viewStatuses (plannedView viewStored)
              Assert.equal [ "V-1"; "V-2"; "V-3"; "V-4" ] (view.Members |> List.map (fun entry -> entry.WorkItemId))
              Assert.equal (Some "ready") (memberOf view "V-1").RecordedState
              Assert.equal (Some MemberStatus.Runnable) (memberOf view "V-1").Status
              Assert.equal (Some "blocked") (memberOf view "V-3").RecordedState
              Assert.equal (Some PlanningWorkState.Blocked) (memberOf view "V-3").PlanningState
              Assert.equal (Some "complete") (memberOf view "V-4").RecordedState
              Assert.equal (Some MemberStatus.Complete) (memberOf view "V-4").Status
              Assert.empty view.Unavailable)

          t "show: partial-completion progress counts every member" (fun () ->
              let progress = (GroupView.build viewStored viewStatuses (plannedView viewStored)).Progress
              Assert.equal 4 progress.Total
              Assert.equal 1 progress.Complete
              Assert.equal 1 progress.Blocked
              Assert.equal 0 progress.Unknown
              Assert.isTrue (progress.Statement.StartsWith "1 of 4 complete; blocked: V-3") progress.Statement)

          t "show: a blocked member names the members it gates" (fun () ->
              let view = GroupView.build viewStored viewStatuses (plannedView viewStored)
              Assert.equal [ ({ WorkItemId = "V-3"; Gates = [ "V-2" ] }: BlockedMember) ] view.Blocked
              Assert.equal [ "V-3" ] (memberOf view "V-2").GatedBy
              Assert.empty (memberOf view "V-1").GatedBy)

          t "show: the execution repository is declared or derived, and says which" (fun () ->
              let derived = GroupView.build viewStored viewStatuses (plannedView viewStored)
              Assert.equal RepositoryBasis.Derived derived.RepositoryBasis
              Assert.isTrue derived.ExecutionRepository.IsSome "derived repository named"
              let declaredStored = { viewStored with Group = { viewStored.Group with ExecutionRepository = Some "praxis" } }
              let declared = GroupView.build declaredStored viewStatuses (plannedView declaredStored)
              Assert.equal (Some "praxis") declared.ExecutionRepository
              Assert.equal RepositoryBasis.Declared declared.RepositoryBasis)

          t "show: without the planner, planning state is unavailable, not guessed" (fun () ->
              let view = GroupView.build viewStored viewStatuses (Error "no analysis")
              Assert.isTrue (view.Members |> List.forall (fun entry -> entry.PlanningState.IsNone && entry.Status.IsNone)) "no planning state"
              Assert.equal (Some "blocked") (memberOf view "V-3").RecordedState
              Assert.equal 4 view.Progress.Unknown
              Assert.equal RepositoryBasis.Unknown view.RepositoryBasis
              Assert.isTrue (view.Unavailable |> List.exists (fun line -> line.Contains "no analysis")) "reason reported")

          t "show: a member that is no longer recorded is reported unknown" (fun () ->
              let stored = { viewStored with Group = { viewStored.Group with Members = [ "V-1"; "GONE-9" ] } }
              let view = GroupView.build stored viewStatuses (plannedView stored)
              Assert.equal None (memberOf view "GONE-9").RecordedState
              Assert.equal None (memberOf view "GONE-9").Status
              Assert.isTrue (view.Unavailable |> List.exists (fun line -> line.Contains "GONE-9")) "unknown member reported")

          t "show: an undeclared ID is not found" (fun () ->
              Assert.equal None (GroupView.tryFind [ viewStored ] "GROUP-VIEW-002")
              Assert.equal (Some viewStored) (GroupView.tryFind [ viewStored ] "GROUP-VIEW-001")) ]

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
              Assert.isTrue (not (validateFindings root).IsEmpty) "malformed store flagged")

          t "show: text and JSON views of a declared group" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--architecture-note"; "one parser" ] |> PraxisCli.ok |> ignore
              let text = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ]
              Assert.equal 0 text.ExitCode
              Assert.isTrue (text.Output.Contains "GROUP-FIXTURE-001 (human-declared") text.Output
              Assert.isTrue (text.Output.Contains "TASK-A: recorded ready; planning ready (runnable)") text.Output
              Assert.isTrue (text.Output.Contains "Progress: 0 of 2 complete") text.Output
              Assert.isTrue (text.Output.Contains "one parser") text.Output
              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-show" (PraxisCli.text (shown.Json["kind"]))
              let view = shown.Json["view"]
              Assert.equal "GROUP-FIXTURE-001" (PraxisCli.text (view["group"]["id"]))
              let total = (view["progress"]["total"]).GetValue<int>()
              Assert.equal 2 total
              let members = view["members"].AsArray() |> Seq.map (fun entry -> (PraxisCli.text (entry["workItem"]), PraxisCli.text (entry["recordedState"]))) |> Seq.toList
              Assert.equal [ "TASK-A", "ready"; "TASK-B", "captured" ] members
              Assert.equal "derived" (PraxisCli.text (view["executionRepository"]["basis"])))

          t "show: blocked members and who they gate" (fun () ->
              let root = fixture ()
              write root ".ros/work/queue.json" (queueDocument.Replace("\"description\":\"Second.\"", "\"description\":\"Second.\",\"dependsOn\":[\"TASK-E\"]").Replace("]}", ",\n  {\"id\":\"TASK-E\",\"title\":\"Task E\",\"tags\":[],\"priority\":\"low\",\"status\":\"blocked\",\"createdAt\":\"2026-09-05T00:00:00.000Z\",\"updatedAt\":\"2026-09-05T00:00:00.000Z\",\"createdBy\":\"unknown\",\"source\":\"manual\",\"sourceReference\":null}\n]}"))
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--member"; "TASK-E" ] |> PraxisCli.ok |> ignore
              let view = (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> PraxisCli.ok).Json["view"]
              let blocked = view["blocked"].AsArray() |> Seq.map (fun entry -> (PraxisCli.text (entry["workItem"]), entry["gates"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList)) |> Seq.toList
              Assert.equal [ "TASK-E", [ "TASK-B" ] ] blocked)

          t "show: an unknown group exits 1 and usage errors exit 2" (fun () ->
              let root = fixture ()
              let missing = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-404"; "--json" ]
              Assert.equal 1 missing.ExitCode
              Assert.isTrue (not (missing.Json["ok"].GetValue<bool>())) "ok is false"
              Assert.equal 1 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-404" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "GROUP-FIXTURE-002" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show"; "--bogus" ]).ExitCode)

          t "show never writes" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let snapshot () =
                  Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                  |> Array.filter (fun path -> not (path.Contains(Path.DirectorySeparatorChar.ToString() + ".git" + Path.DirectorySeparatorChar.ToString())))
                  |> Array.sort
                  |> Array.map (fun path -> path, File.ReadAllText path, File.GetLastWriteTimeUtc path)
                  |> Array.toList

              let before = snapshot ()
              PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ignore
              PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ignore
              PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-404" ] |> ignore
              Assert.equal before (snapshot ())) ]

    let private addMember root extra =
        PraxisCli.run root None ([ "work"; "group"; "add"; "--occurred-at"; PraxisCli.now () ] @ extra)

    let private addCli =
        [ t "add stores the member with its adder and changes no lifecycle" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let before = lifecycleBytes root
              let result = addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-E"; "--json" ]
              Assert.equal 1 result.ExitCode
              write root ".ros/work/queue.json" (queueDocument.Replace("]}", ",\n  {\"id\":\"TASK-E\",\"title\":\"Task E\",\"tags\":[],\"priority\":\"low\",\"status\":\"ready\",\"createdAt\":\"2026-09-05T00:00:00.000Z\",\"updatedAt\":\"2026-09-05T00:00:00.000Z\",\"createdBy\":\"unknown\",\"source\":\"manual\",\"sourceReference\":null}\n]}"))
              let lifecycle = lifecycleBytes root
              let added = addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-E"; "--actor"; "group-adder"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-add" (PraxisCli.text (added.Json["kind"]))
              Assert.equal lifecycle (lifecycleBytes root)
              Assert.isTrue (before <> lifecycle) "fixture queue changed only by the test"

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok [ stored ] ->
                  Assert.equal [ "TASK-A"; "TASK-B"; "TASK-E" ] stored.Group.Members
                  Assert.equal [ "TASK-E" ] (stored.Additions |> List.map (fun addition -> addition.WorkItem))
                  Assert.isTrue ((List.head stored.Additions).AddedBy.Contains "group-adder") (List.head stored.Additions).AddedBy
              | other -> failwith $"unexpected store: {other}"

              Assert.empty (validateFindings root)
              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ]
              Assert.isTrue (shown.Output.Contains "TASK-E: recorded ready") shown.Output)

          t "add refuses unknown groups, unknown, terminal and present items with exit 1" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)

              let codes extra =
                  let result = addMember root (extra @ [ "--json" ])
                  Assert.equal 1 result.ExitCode
                  result.Json["rejections"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["code"])) |> Seq.toList

              Assert.equal [ "unknown-group" ] (codes [ "--id"; "GROUP-FIXTURE-404"; "--member"; "TASK-A" ])
              Assert.equal [ "unknown-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-Z" ])
              Assert.equal [ "terminal-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-C" ])
              Assert.equal [ "terminal-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-D" ])
              Assert.equal [ "already-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B" ])
              Assert.equal stored (File.ReadAllText(storePath root)))

          t "add refuses an item in another execution repository unless the group is cross-repository" (fun () ->
              let root = fixture ()
              write root ".ros/work/queue.json" (queueDocument.Replace("]}", ",\n  {\"id\":\"TASK-X\",\"title\":\"Task X\",\"description\":\"Implemented in an external repository.\",\"tags\":[],\"priority\":\"low\",\"status\":\"ready\",\"createdAt\":\"2026-09-05T00:00:00.000Z\",\"updatedAt\":\"2026-09-05T00:00:00.000Z\",\"createdBy\":\"unknown\",\"source\":\"manual\",\"sourceReference\":null}\n]}"))
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              declare root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--cross-repository" ] |> PraxisCli.ok |> ignore
              let refused = addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-X" ]
              Assert.equal 1 refused.ExitCode
              Assert.isTrue (refused.Error.Contains "unknown external repository") refused.Error
              addMember root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-X" ] |> PraxisCli.ok |> ignore)

          t "add --dry-run decides the same way and writes nothing" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              write root ".ros/work/queue.json" (queueDocument.Replace("\"status\":\"abandoned\"", "\"status\":\"ready\""))
              let stored = File.ReadAllText(storePath root)
              let result = addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-C"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dry run reported"
              Assert.equal stored (File.ReadAllText(storePath root)))

          t "add usage errors exit 2" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)
              Assert.equal 2 (addMember root [ "--member"; "TASK-D" ]).ExitCode
              Assert.equal 2 (addMember root [ "--id"; "GROUP-FIXTURE-001" ]).ExitCode
              Assert.equal 2 (addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--bogus" ]).ExitCode
              Assert.equal stored (File.ReadAllText(storePath root))) ]

    let private removeMember root extra =
        PraxisCli.run root None ([ "work"; "group"; "remove"; "--occurred-at"; PraxisCli.now () ] @ extra)

    /// Every file outside `.git` and the group store, with its content.
    let private everythingButTheStore (root: string) =
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun path ->
            not (path.Contains(Path.DirectorySeparatorChar.ToString() + ".git" + Path.DirectorySeparatorChar.ToString()))
            && path <> storePath root)
        |> Array.sort
        |> Array.map (fun path -> path, File.ReadAllText path)
        |> Array.toList

    let private removeCli =
        [ t "remove stores the removal with its remover and changes nothing else" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              write root ".ros/work/queue.json" (queueDocument.Replace("]}", ",\n  {\"id\":\"TASK-E\",\"title\":\"Task E\",\"tags\":[],\"priority\":\"low\",\"status\":\"ready\",\"createdAt\":\"2026-09-05T00:00:00.000Z\",\"updatedAt\":\"2026-09-05T00:00:00.000Z\",\"createdBy\":\"unknown\",\"source\":\"manual\",\"sourceReference\":null}\n]}"))
              addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-E" ] |> PraxisCli.ok |> ignore
              let before = everythingButTheStore root

              let removed =
                  removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--reason"; "moved to its own group"; "--actor"; "group-remover"; "--json" ]
                  |> PraxisCli.ok

              Assert.equal "work-group-remove" (PraxisCli.text (removed.Json["kind"]))
              Assert.isTrue (removed.Json["ok"].GetValue<bool>()) "ok"
              Assert.equal before (everythingButTheStore root)

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok [ stored ] ->
                  Assert.equal [ "TASK-A"; "TASK-E" ] stored.Group.Members
                  Assert.equal [ "TASK-E" ] (stored.Additions |> List.map (fun addition -> addition.WorkItem))

                  match stored.Removals with
                  | [ removal ] ->
                      Assert.equal "TASK-B" removal.WorkItem
                      Assert.isTrue (removal.RemovedBy.Contains "group-remover") removal.RemovedBy
                      Assert.equal (Some "moved to its own group") removal.Reason
                  | other -> failwith $"unexpected removals: {other}"
              | other -> failwith $"unexpected store: {other}"

              Assert.empty (validateFindings root)
              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ]
              Assert.isTrue (not (shown.Output.Contains "TASK-B")) shown.Output
              let item = PraxisCli.run root None [ "work"; "show"; "TASK-B" ]
              Assert.equal 0 item.ExitCode)

          t "remove leaves a terminal member's state and evidence untouched" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore

              // TASK-D completed after it joined (partial completion, PRX-GRP-042).
              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok [ stored ] ->
                  let partial = { stored with Group = { stored.Group with Members = stored.Group.Members @ [ "TASK-D" ] } }
                  File.WriteAllText(storePath root, PlanningJson.renderGroupStore [ partial ])
              | other -> failwith $"unexpected store: {other}"

              write root ".ros/context/current.json" (contextDocument.Replace("\"evidence\":[]", "\"evidence\":[{\"type\":\"test\",\"path\":\"evidence.txt\"}]"))
              let before = everythingButTheStore root
              removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-D" ] |> PraxisCli.ok |> ignore
              Assert.equal before (everythingButTheStore root)
              Assert.isTrue ((File.ReadAllText(Path.Combine(root, ".ros", "context", "current.json"))).Contains "evidence.txt") "evidence kept")

          t "remove refuses non-members, undeclared groups and the last members with exit 1" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)

              let codes extra =
                  let result = removeMember root (extra @ [ "--json" ])
                  Assert.equal 1 result.ExitCode
                  Assert.isTrue (not (result.Json["ok"].GetValue<bool>())) "ok is false"
                  result.Json["rejections"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["code"])) |> Seq.toList

              Assert.equal [ "not-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-D" ])
              Assert.equal [ "not-member" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-Z" ])
              Assert.equal [ "unknown-group" ] (codes [ "--id"; "GROUP-FIXTURE-404"; "--member"; "TASK-A" ])
              Assert.equal [ "last-members" ] (codes [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ])
              let text = removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B" ]
              Assert.equal 1 text.ExitCode
              Assert.isTrue (text.Error.Contains "at least two") text.Error
              Assert.equal stored (File.ReadAllText(storePath root)))

          t "remove --dry-run decides the same way and writes nothing" (fun () ->
              let root = fixture ()
              write root ".ros/work/queue.json" (queueDocument.Replace("\"status\":\"abandoned\"", "\"status\":\"ready\""))
              declare root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--member"; "TASK-C" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)
              let result = removeMember root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-C"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dry run reported"
              let group = result.Json["group"]
              let members = group["members"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList
              Assert.equal [ "TASK-A"; "TASK-B" ] members
              Assert.equal stored (File.ReadAllText(storePath root))
              let text = removeMember root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-C"; "--dry-run" ] |> PraxisCli.ok
              Assert.isTrue (text.Output.Contains "would remove TASK-C from GROUP-FIXTURE-002") text.Output
              Assert.equal 1 (removeMember root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-D"; "--dry-run" ]).ExitCode
              Assert.equal stored (File.ReadAllText(storePath root)))

          t "remove usage errors exit 2" (fun () ->
              let root = fixture ()
              declare root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let stored = File.ReadAllText(storePath root)
              Assert.equal 2 (removeMember root [ "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (removeMember root [ "--id"; "GROUP-FIXTURE-001" ]).ExitCode
              Assert.equal 2 (removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "remove"; "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--reason"; " " ]).ExitCode
              Assert.equal 2 (removeMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--bogus" ]).ExitCode
              Assert.equal 2 (addMember root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--reason"; "not an add flag" ]).ExitCode
              Assert.equal stored (File.ReadAllText(storePath root))) ]

    let tests = domain @ addTests @ removeTests @ viewTests @ cli @ addCli @ removeCli
