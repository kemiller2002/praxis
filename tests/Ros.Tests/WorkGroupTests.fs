namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open PlanningFixtures

/// `work group create`, `work group show`, `work group add` and `work group
/// remove`: durable human-declared execution groups (PRAXIS-GROUP-01..04;
/// requirements/PLANNING-WORK-GROUPS.md PRX-GRP-073 phase two).
module WorkGroupTests =
    let private t name run = { Name = $"work group: {name}"; Run = run }

    let private declared id members =
        { Id = id
          Members = members
          Kind = Some GroupKind.SharedApiSurface
          Origin = GroupOrigin.HumanDeclared
          SharedContext = [ "one CLI surface" ]
          ExecutionRepository = None
          CrossRepository = false
          ArchitectureNotes = [] }

    let private actor =
        { Kind = ActorKind.Agent
          Id = "example/agent"
          Provider = Some "example"
          Model = Some "unknown"
          Runtime = Some "agent" }

    let private stored group =
        { Group = group
          DeclaredAt = "2026-09-30T00:00:00.000Z"
          DeclaredBy = actor }

    let private queue =
        [ queued "TASK-A" "ready" "2026-09-01T00:00:00Z"
          queued "TASK-B" "captured" "2026-09-02T00:00:00Z"
          queued "TASK-C" "abandoned" "2026-09-03T00:00:00Z"
          queued "TASK-D" "ready" "2026-09-04T00:00:00Z" ]

    let private liveItems = [ live "TASK-D" LiveWorkState.Complete; live "LIVE-E" LiveWorkState.Blocked ]
    let private standing = GroupDeclaration.standing queue liveItems

    let private rejections result =
        match result with
        | Ok _ -> []
        | Error found -> found

    // ---- CLI fixture ---------------------------------------------------------

    let private queueJson =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","nextSeq":1,"items":[
  {"id":"TASK-A","title":"Task A","tags":["cli"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-B","title":"Task B","tags":["docs"],"priority":"low","status":"ready","createdAt":"2026-09-02T00:00:00.000Z","updatedAt":"2026-09-02T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-C","title":"Task C","tags":[],"priority":"low","status":"abandoned","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}
]}"""

    let private contextJson =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[
  {"id":"TASK-D","type":"task","state":"complete","semanticState":"complete","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[]}
]}"""

    let private write (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private fixture () =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-group-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        GitFixture.git root [ "init"; "-q"; "-b"; "main" ] |> ignore
        GitFixture.configureIdentity root
        write root ".ros/work/queue.json" queueJson
        write root ".ros/context/current.json" contextJson
        GitFixture.commitAll root "fixture" |> ignore
        root

    let private hash (root: string) (relative: string) =
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))))

    let private executor = PraxisCli.agent "example/agent" "example" "agent" "session-g"

    let private create (root: string) (extra: string list) =
        PraxisCli.run root (Some executor) ([ "work"; "group"; "create"; "--occurred-at"; PraxisCli.now () ] @ extra)

    let private number (node: JsonNode) = node.GetValue<int>()
    let private flag (node: JsonNode) = node.GetValue<bool>()

    let private groupsPath (root: string) = Path.Combine(root, ".ros", "work", "groups.json")
    let private ledgerPath (root: string) = Path.Combine(root, ".ros", "work", "group-membership.json")

    // ---- work group add (PRAXIS-GROUP-03) -------------------------------------

    let private additionContext (grouping: GroupingConfiguration) (entries: StoredGroupDeclaration list) : MemberAdditionContext =
        { Stored = entries
          Standing = standing
          Location = Grouping.executionLocation grouping "this/repo" (queue @ [ { queued "TASK-X" "ready" "2026-09-05T00:00:00Z" with Description = Some "lands in an external repository" } ]) >> fst
          Repository = "this/repo" }

    let private addition group workItem : MemberAdditionRequest =
        { GroupId = group; WorkItem = workItem; Reason = None }

    let private additionStanding id =
        if id = "TASK-X" then MemberStanding.Open "ready" else standing id

    let private decideAddition grouping entries request =
        GroupMembership.decide { additionContext grouping entries with Standing = additionStanding } request

    let private add (root: string) (extra: string list) =
        PraxisCli.run root (Some executor) ([ "work"; "group"; "add"; "--occurred-at"; PraxisCli.now () ] @ extra)

    // ---- work group remove (PRAXIS-GROUP-04) ----------------------------------

    let private removal group workItem : MemberRemovalRequest =
        { GroupId = group; WorkItem = workItem; Reason = None }

    let private remove (root: string) (extra: string list) =
        PraxisCli.run root (Some executor) ([ "work"; "group"; "remove"; "--occurred-at"; PraxisCli.now () ] @ extra)

    /// Every file outside `.git` except the two group files, with its hash:
    /// what a removal must leave untouched (lifecycle, evidence, attribution).
    let private untouched (root: string) =
        let excluded = set [ groupsPath root; ledgerPath root ]

        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun file -> not (file.Contains $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && not (excluded.Contains file))
        |> Array.sort
        |> Array.map (fun file -> Path.GetRelativePath(root, file), hash root (Path.GetRelativePath(root, file)))
        |> Array.toList

    let private members (node: JsonNode) = node.AsArray() |> Seq.map PraxisCli.text |> Seq.toList

    let tests =
        [ t "standing prefers live state and classifies terminal states" (fun () ->
              Assert.equal (MemberStanding.Open "ready") (standing "TASK-A")
              Assert.equal (MemberStanding.Open "captured") (standing "TASK-B")
              Assert.equal (MemberStanding.Terminal "abandoned") (standing "TASK-C")
              Assert.equal (MemberStanding.Terminal "complete") (standing "TASK-D")
              Assert.equal (MemberStanding.Open "blocked") (standing "LIVE-E")
              Assert.equal MemberStanding.Unknown (standing "NOPE-1"))

          t "a valid declaration is accepted unchanged" (fun () ->
              let group = declared "GROUP-CLI-001" [ "TASK-A"; "TASK-B"; "LIVE-E" ]
              Assert.equal (Ok group) (GroupDeclaration.decide [] standing group))

          t "unknown and terminal members are refused" (fun () ->
              let found = GroupDeclaration.decide [] standing (declared "GROUP-CLI-001" [ "TASK-A"; "NOPE-1"; "TASK-C"; "TASK-D" ]) |> rejections

              Assert.equal
                  [ GroupDeclarationRejection.UnknownMember "NOPE-1"
                    GroupDeclarationRejection.TerminalMember("TASK-C", "abandoned")
                    GroupDeclarationRejection.TerminalMember("TASK-D", "complete") ]
                  found)

          t "a duplicate group ID is refused" (fun () ->
              let existing = [ declared "GROUP-CLI-001" [ "TASK-B" ] ]
              let found = GroupDeclaration.decide existing standing (declared "GROUP-CLI-001" [ "TASK-A" ]) |> rejections
              Assert.equal [ GroupDeclarationRejection.DuplicateGroupId "GROUP-CLI-001" ] found)

          t "shape problems are all reported at once" (fun () ->
              let malformed = { declared "group-1" [ "TASK-A"; "TASK-A"; "bad id" ] with SharedContext = [ " " ] }
              let found = GroupDeclaration.decide [] standing malformed |> rejections

              Assert.equal
                  [ GroupDeclarationRejection.InvalidGroupId "group-1"
                    GroupDeclarationRejection.InvalidMemberId "bad id"
                    GroupDeclarationRejection.DuplicateMember "TASK-A"
                    GroupDeclarationRejection.BlankEntry "--shared-context" ]
                  found

              Assert.equal [ GroupDeclarationRejection.NoMembers ] (GroupDeclaration.decide [] standing (declared "GROUP-CLI-002" []) |> rejections))

          t "validate findings: duplicates and unknown members, but a member completed later is partial completion" (fun () ->
              let entries =
                  [ stored (declared "GROUP-CLI-001" [ "TASK-A"; "TASK-D" ])
                    stored (declared "GROUP-CLI-001" [ "TASK-B" ])
                    stored (declared "GROUP-CLI-002" [ "GONE-1" ]) ]

              let found = GroupDeclaration.findings standing entries |> List.map snd
              Assert.equal 2 found.Length
              Assert.isTrue (found |> List.exists (fun message -> message.Contains "GROUP-CLI-001 is already declared")) "duplicate ID"
              Assert.isTrue (found |> List.exists (fun message -> message.Contains "GONE-1")) "unknown member"
              Assert.empty (GroupDeclaration.findings standing [ stored (declared "GROUP-CLI-003" [ "TASK-D" ]) ]))

          t "stored groups merge into grouping.groups, and a clash with configuration is refused" (fun () ->
              let configured = { PlannerConfiguration.defaults with Grouping = { GroupingConfiguration.defaults with Groups = [ declared "GROUP-CFG-001" [ "TASK-A" ] ] } }
              let entry = stored (declared "GROUP-CLI-001" [ "TASK-B" ])

              match GroupDeclaration.mergeInto configured [ entry ] with
              | Ok merged -> Assert.equal [ "GROUP-CFG-001"; "GROUP-CLI-001" ] (merged.Grouping.Groups |> List.map (fun group -> group.Id))
              | Error message -> failwith message

              match GroupDeclaration.mergeInto configured [ stored (declared "GROUP-CFG-001" [ "TASK-B" ]) ] with
              | Ok _ -> failwith "a clash must be refused"
              | Error message -> Assert.isTrue (message.Contains "GROUP-CFG-001") "the clash names the group")

          t "the store round-trips and its group part parses as a grouping.groups entry" (fun () ->
              let entry = stored { declared "GROUP-CLI-001" [ "TASK-A"; "TASK-B" ] with ExecutionRepository = Some "other/repo"; CrossRepository = true; ArchitectureNotes = [ "one parser" ] }
              let rendered = WorkGroupJson.renderStore [ entry ]
              Assert.equal (Ok [ entry ]) (WorkGroupJson.parseStore rendered)
              let document = JsonNode.Parse rendered
              let groupNode = document["groups"].AsArray().[0].DeepClone()
              let configuration = JsonObject()
              let grouping = JsonObject()
              grouping["groups"] <- JsonArray([| groupNode |])
              configuration["grouping"] <- grouping

              match PlanningJson.parseConfiguration (configuration.ToJsonString()) with
              | Ok parsed -> Assert.equal [ entry.Group ] parsed.Grouping.Groups
              | Error message -> failwith message)

          t "cli: create records the group, changes no lifecycle state, and plan groups reads it" (fun () ->
              let root = fixture ()
              let queueBefore = hash root ".ros/work/queue.json"
              let contextBefore = hash root ".ros/context/current.json"

              let result =
                  create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--kind"; "shared-api-surface"; "--shared-context"; "one CLI"; "--json" ]
                  |> PraxisCli.ok

              Assert.equal "created" (PraxisCli.text (result.Json["status"]))
              Assert.isTrue (File.Exists(groupsPath root)) "the store is written"
              Assert.equal queueBefore (hash root ".ros/work/queue.json")
              Assert.equal contextBefore (hash root ".ros/context/current.json")

              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> PraxisCli.ok
              let group = groups.Json["groups"].AsArray() |> Seq.find (fun entry -> PraxisCli.text (entry["id"]) = "GROUP-FIXTURE-001")
              Assert.equal "human-declared" (PraxisCli.text (group["origin"]))
              let members = group["members"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["workItem"])) |> Seq.toList
              Assert.equal [ "TASK-A"; "TASK-B" ] members)

          t "cli: dry run writes nothing; refusals write nothing" (fun () ->
              let root = fixture ()
              let planned = create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.equal "planned" (PraxisCli.text (planned.Json["status"]))
              Assert.isTrue (not (File.Exists(groupsPath root))) "a dry run records nothing"
              let refused = create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-C"; "--member"; "TASK-D"; "--member"; "NOPE-1"; "--json" ]
              Assert.equal 1 refused.ExitCode
              Assert.equal "rejected" (PraxisCli.text (refused.Json["status"]))
              Assert.equal 3 (refused.Json["rejections"].AsArray().Count)
              Assert.isTrue (not (File.Exists(groupsPath root))) "a refusal records nothing"
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let duplicate = create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B" ]
              Assert.equal 1 duplicate.ExitCode
              Assert.isTrue (duplicate.Error.Contains "already declared") "duplicate IDs are refused"
              Assert.equal 2 (create root [ "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (create root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A"; "--kind"; "nonsense" ]).ExitCode)

          t "cli: validate checks stored groups" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let clean = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.isTrue (not (clean.Output.Contains "groups.json")) "a valid store has no findings"
              write root ".ros/work/groups.json" ((File.ReadAllText(groupsPath root)).Replace("\"TASK-A\"", "\"GONE-1\""))
              let broken = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.equal 1 broken.ExitCode
              Assert.isTrue (broken.Output.Contains "GONE-1") "an unknown stored member is a finding")

          // ---- work group show (PRAXIS-GROUP-02) -------------------------------

          t "show: members keep their own recorded and planning states; progress is partial" (fun () ->
              let showQueue =
                  [ queued "TASK-A" "ready" "2026-09-01T00:00:00Z"
                    { queued "TASK-B" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "TASK-X" ] }
                    queued "TASK-C" "abandoned" "2026-09-03T00:00:00Z"
                    queued "TASK-X" "ready" "2026-09-04T00:00:00Z"
                    { queued "OUT-1" "ready" "2026-09-05T00:00:00Z" with DependsOn = [ "TASK-X" ] } ]

              let showLive =
                  [ live "TASK-D" LiveWorkState.Complete
                    { live "TASK-X" LiveWorkState.Blocked with BlockReason = Some "the store format is undecided" } ]

              let analysis = analyze showQueue showLive [] [] PlannerConfiguration.defaults
              let entry = stored (declared "GROUP-CLI-001" [ "TASK-X"; "TASK-B"; "TASK-A"; "TASK-D"; "TASK-C"; "GONE-1" ])
              let view = GroupView.project (GroupDeclaration.standing showQueue showLive) analysis entry
              let byId id = view.Members |> List.find (fun item -> item.WorkItem = id)

              Assert.equal [ "TASK-X"; "TASK-B"; "TASK-A"; "TASK-D"; "TASK-C"; "GONE-1" ] (view.Members |> List.map (fun item -> item.WorkItem))
              Assert.equal (Some "blocked") (byId "TASK-X").RecordedState
              Assert.equal (Some "ready") (byId "TASK-B").RecordedState
              Assert.equal (Some PlanningWorkState.Ready) (byId "TASK-B").PlanningState
              Assert.equal [ "TASK-X" ] (byId "TASK-B").WaitsOn
              Assert.equal (Some "complete") (byId "TASK-D").RecordedState
              Assert.equal (Some PlanningWorkState.Complete) (byId "TASK-D").PlanningState
              Assert.equal None (byId "GONE-1").RecordedState
              Assert.equal None (byId "GONE-1").PlanningState

              Assert.equal
                  { Total = 6; Complete = 1; Abandoned = 1; Open = 3; Blocked = 1; Unknown = 1 }
                  view.Progress

              Assert.isTrue ((GroupView.progressStatement view.Progress).StartsWith "1 of 6 complete, 1 abandoned") "abandoned is not counted as complete")

          t "show: a blocked member names its reasons and the members and other items it gates" (fun () ->
              let showQueue =
                  [ { queued "TASK-B" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "TASK-X" ] }
                    { queued "TASK-E" "ready" "2026-09-03T00:00:00Z" with DependsOn = [ "TASK-B" ] }
                    queued "TASK-X" "ready" "2026-09-04T00:00:00Z"
                    { queued "OUT-1" "ready" "2026-09-05T00:00:00Z" with DependsOn = [ "TASK-X" ] } ]

              let showLive = [ { live "TASK-X" LiveWorkState.Blocked with BlockReason = Some "the store format is undecided" } ]
              let analysis = analyze showQueue showLive [] [] PlannerConfiguration.defaults
              let view = GroupView.project (GroupDeclaration.standing showQueue showLive) analysis (stored (declared "GROUP-CLI-001" [ "TASK-B"; "TASK-E"; "TASK-X" ]))

              match view.Blocked with
              | [ blocked ] ->
                  Assert.equal "TASK-X" blocked.WorkItem
                  Assert.equal [ "TASK-B"; "TASK-E" ] blocked.GatesMembers
                  Assert.equal [ "OUT-1" ] blocked.GatesOthers
                  Assert.isTrue (blocked.Reasons |> List.contains "the store format is undecided") "the recorded block reason is shown"
              | other -> failwith $"expected exactly TASK-X blocked, found %A{other}")

          t "show: the view JSON carries states, progress and blocked members, and never a lifecycle change" (fun () ->
              let showQueue = [ queued "TASK-A" "ready" "2026-09-01T00:00:00Z" ]
              let analysis = analyze showQueue [ live "TASK-D" LiveWorkState.Complete ] [] [] PlannerConfiguration.defaults
              let view = GroupView.project (GroupDeclaration.standing showQueue [ live "TASK-D" LiveWorkState.Complete ]) analysis (stored (declared "GROUP-CLI-001" [ "TASK-A"; "TASK-D" ]))
              let document = JsonNode.Parse(WorkGroupJson.renderView analysis.Snapshot view)
              Assert.equal "work-group-show" (PraxisCli.text (document["kind"]))
              Assert.equal "GROUP-CLI-001" (PraxisCli.text (document["group"]["id"]))
              let members = document["members"].AsArray()
              Assert.equal "ready" (PraxisCli.text (members.[0].["recordedState"]))
              Assert.equal "complete" (PraxisCli.text (members.[1].["planningState"]))
              Assert.equal 1 (number (document["progress"]["complete"]))
              Assert.isTrue (not (flag (document["progress"]["allComplete"]))) "partial completion is not all complete"
              Assert.isTrue (not (flag (document["lifecycleChanged"]))) "show changes no lifecycle state")

          t "cli: show renders text and JSON, reflects a member completed later, and writes nothing" (fun () ->
              let root = fixture ()

              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--execution-repository"; "fixture/repo"; "--architecture-note"; "one store"; "--shared-context"; "one CLI" ]
              |> PraxisCli.ok
              |> ignore

              // TASK-B completes after the group was declared: partial completion.
              write root ".ros/context/current.json" (contextJson.Replace("\"TASK-D\"", "\"TASK-B\""))
              GitFixture.commitAll root "declare and complete" |> ignore
              let tracked = [ ".ros/work/queue.json"; ".ros/context/current.json"; ".ros/work/groups.json" ]
              let before = tracked |> List.map (hash root)

              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> PraxisCli.ok
              Assert.equal "found" (PraxisCli.text (shown.Json["status"]))
              Assert.equal "fixture/repo" (PraxisCli.text (shown.Json["group"]["executionRepository"]))
              let notes = (shown.Json["group"]["architectureNotes"]).AsArray()
              Assert.equal "one store" (PraxisCli.text notes.[0])
              let states = shown.Json["members"].AsArray() |> Seq.map (fun item -> PraxisCli.text (item["workItem"]), PraxisCli.text (item["recordedState"])) |> Seq.toList
              Assert.equal [ "TASK-A", "ready"; "TASK-B", "complete" ] states
              Assert.equal 1 (number (shown.Json["progress"]["complete"]))
              Assert.equal 1 (number (shown.Json["progress"]["open"]))

              let text = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> PraxisCli.ok
              Assert.isTrue (text.Output.Contains "1 of 2 complete") "text shows partial progress"
              Assert.isTrue (text.Output.Contains "fixture/repo (repository-local)") "text names the execution repository"
              Assert.isTrue (text.Output.Contains "note:       one store") "text shows architecture notes"

              Assert.equal before (tracked |> List.map (hash root))
              Assert.equal "" (GitFixture.git root [ "status"; "--porcelain" ]))

          t "cli: show exits 1 for an undeclared group and 2 for bad arguments, writing nothing" (fun () ->
              let root = fixture ()
              let missing = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-404"; "--json" ]
              Assert.equal 1 missing.ExitCode
              Assert.equal "not-found" (PraxisCli.text (missing.Json["status"]))
              Assert.isTrue (missing.Error.Contains "GROUP-FIXTURE-404 is not declared") "the error names the group"
              Assert.equal 1 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-404" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--bogus" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--as-of"; "yesterday" ]).ExitCode
              Assert.isTrue (not (File.Exists(groupsPath root))) "show never creates the store"
              Assert.equal "" (GitFixture.git root [ "status"; "--porcelain" ]))

          t "add: an open item is appended in declared order; nothing else in the declaration changes" (fun () ->
              let entry = stored (declared "GROUP-CLI-001" [ "TASK-B" ])

              match decideAddition GroupingConfiguration.defaults [ entry ] (addition "GROUP-CLI-001" "TASK-A") with
              | Ok updated ->
                  Assert.equal [ "TASK-B"; "TASK-A" ] updated.Group.Members
                  Assert.equal { entry.Group with Members = [ "TASK-B"; "TASK-A" ] } updated.Group
                  Assert.equal entry.DeclaredAt updated.DeclaredAt
                  Assert.equal entry.DeclaredBy updated.DeclaredBy
              | Error found -> failwith $"%A{found}"

              // A blocked item is open: it may join, and stays blocked.
              Assert.isTrue (decideAddition GroupingConfiguration.defaults [ entry ] (addition "GROUP-CLI-001" "LIVE-E") |> Result.isOk) "blocked items may join")

          t "add: unknown, terminal, invalid and already-present items and undeclared groups are refused" (fun () ->
              let entries = [ stored (declared "GROUP-CLI-001" [ "TASK-B" ]) ]
              let refused workItem = decideAddition GroupingConfiguration.defaults entries (addition "GROUP-CLI-001" workItem) |> rejections

              Assert.equal [ GroupAdditionRejection.UnknownMember "NOPE-1" ] (refused "NOPE-1")
              Assert.equal [ GroupAdditionRejection.TerminalMember("TASK-C", "abandoned") ] (refused "TASK-C")
              Assert.equal [ GroupAdditionRejection.TerminalMember("TASK-D", "complete") ] (refused "TASK-D")
              Assert.equal [ GroupAdditionRejection.InvalidMemberId "bad id" ] (refused "bad id")
              Assert.equal [ GroupAdditionRejection.AlreadyMember("TASK-B", "GROUP-CLI-001") ] (refused "TASK-B")

              Assert.equal
                  [ GroupAdditionRejection.UndeclaredGroup "GROUP-CLI-404"; GroupAdditionRejection.BlankReason ]
                  (decideAddition GroupingConfiguration.defaults entries { addition "GROUP-CLI-404" "TASK-A" with Reason = Some " " } |> rejections))

          t "add: an item executing in another repository is refused unless the group is cross-repository" (fun () ->
              let grouping = { GroupingConfiguration.defaults with ExecutionRepositories = [ "TASK-A", "other/repo" ] }
              let local = stored (declared "GROUP-CLI-001" [ "TASK-B" ])
              let refused entry workItem = decideAddition grouping [ entry ] (addition entry.Group.Id workItem) |> rejections

              Assert.equal [ GroupAdditionRejection.RepositoryMismatch("TASK-A", "other/repo", "this/repo") ] (refused local "TASK-A")
              Assert.equal [ GroupAdditionRejection.RepositoryMismatch("TASK-X", "unknown external repository", "this/repo") ] (refused local "TASK-X")

              // A group that executes elsewhere accepts that repository's items only.
              let remote = stored { declared "GROUP-CLI-002" [ "TASK-B" ] with ExecutionRepository = Some "other/repo" }
              Assert.empty (refused remote "TASK-A")
              Assert.equal [ GroupAdditionRejection.RepositoryMismatch("LIVE-E", "this/repo", "other/repo") ] (refused remote "LIVE-E")

              let cross = stored { declared "GROUP-ECHELON-CLI-001" [ "TASK-B" ] with CrossRepository = true }
              Assert.empty (refused cross "TASK-A")
              Assert.empty (refused cross "TASK-X"))

          t "add: the membership ledger round-trips and validate findings name inconsistent additions" (fun () ->
              let recorded: MemberAddition =
                  { GroupId = "GROUP-CLI-001"
                    WorkItem = "TASK-A"
                    AddedAt = "2026-09-30T01:00:00.000Z"
                    AddedBy = actor
                    Reason = Some "same parser" }

              let unattributed = { recorded with Reason = None }
              let changes = [ MembershipChange.Added recorded; MembershipChange.Added unattributed ]
              Assert.equal (Ok changes) (WorkGroupMembershipJson.parseLedger (WorkGroupMembershipJson.renderLedger changes))
              Assert.isTrue (WorkGroupMembershipJson.parseLedger """{"schemaVersion":"1.0.0","changes":[{"change":"merged"}]}""" |> Result.isError) "unknown changes are malformed"

              let entries = [ stored (declared "GROUP-CLI-001" [ "TASK-B"; "TASK-A" ]) ]
              Assert.empty (GroupMembership.findings entries [ MembershipChange.Added recorded ])

              let found =
                  GroupMembership.findings entries [ MembershipChange.Added { recorded with WorkItem = "TASK-E" }; MembershipChange.Added { recorded with GroupId = "GROUP-CLI-404" } ]
                  |> List.map snd

              Assert.equal 2 found.Length
              Assert.isTrue (found.[0].Contains "does not list it") "an addition the group lacks"
              Assert.isTrue (found.[1].Contains "not declared") "an addition to an undeclared group")

          t "cli: add appends the member, records who added it, changes no lifecycle state, and the planner reads it" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let queueBefore = hash root ".ros/work/queue.json"
              let contextBefore = hash root ".ros/context/current.json"

              let added = add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--reason"; "same CLI surface"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-add" (PraxisCli.text (added.Json["kind"]))
              Assert.equal "added" (PraxisCli.text (added.Json["status"]))
              Assert.equal [ "TASK-A"; "TASK-B" ] (added.Json["members"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList)
              Assert.equal "example/agent" (PraxisCli.text ((added.Json["addition"]).["addedBy"].["id"]))
              Assert.isTrue (not (flag (added.Json["lifecycleChanged"]))) "add changes no lifecycle state"
              Assert.equal queueBefore (hash root ".ros/work/queue.json")
              Assert.equal contextBefore (hash root ".ros/context/current.json")

              match WorkGroupMembershipJson.parseLedger (File.ReadAllText(ledgerPath root)) with
              | Ok [ MembershipChange.Added recorded ] ->
                  Assert.equal "GROUP-FIXTURE-001" recorded.GroupId
                  Assert.equal "TASK-B" recorded.WorkItem
                  Assert.equal "example/agent" recorded.AddedBy.Id
                  Assert.equal (Some "same CLI surface") recorded.Reason
              | other -> failwith $"expected one recorded addition, found %A{other}"

              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> PraxisCli.ok
              let group = groups.Json["groups"].AsArray() |> Seq.find (fun entry -> PraxisCli.text (entry["id"]) = "GROUP-FIXTURE-001")
              Assert.equal [ "TASK-A"; "TASK-B" ] (group["members"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["workItem"])) |> Seq.toList)

              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> PraxisCli.ok
              Assert.equal 2 (shown.Json["members"].AsArray().Count)

              let validated = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.isTrue (not (validated.Output.Contains "group-membership.json")) "a consistent ledger has no findings")

          t "cli: add dry run and refusals write nothing; bad arguments exit 2" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let groupsBefore = hash root ".ros/work/groups.json"

              let planned = add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.equal "planned" (PraxisCli.text (planned.Json["status"]))
              Assert.equal [ "TASK-A"; "TASK-B" ] (planned.Json["members"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList)

              [ "TASK-A", "already-member"; "TASK-C", "terminal-member"; "TASK-D", "terminal-member"; "NOPE-1", "unknown-member" ]
              |> List.iter (fun (workItem, code) ->
                  let refused = add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; workItem; "--json" ]
                  Assert.equal 1 refused.ExitCode
                  Assert.equal "rejected" (PraxisCli.text (refused.Json["status"]))
                  Assert.equal code (PraxisCli.text (refused.Json["rejections"].[0].["code"])))

              let undeclared = add root [ "--id"; "GROUP-FIXTURE-404"; "--member"; "TASK-B" ]
              Assert.equal 1 undeclared.ExitCode
              Assert.isTrue (undeclared.Error.Contains "GROUP-FIXTURE-404 is not declared") "the error names the group"

              Assert.equal groupsBefore (hash root ".ros/work/groups.json")
              Assert.isTrue (not (File.Exists(ledgerPath root))) "no refusal or dry run records an addition"

              Assert.equal 2 (add root [ "--id"; "GROUP-FIXTURE-001" ]).ExitCode
              Assert.equal 2 (add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--member"; "TASK-D" ]).ExitCode
              Assert.equal 2 (add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--kind"; "custom:x" ]).ExitCode
              Assert.equal 2 (create root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-B"; "--reason"; "x" ]).ExitCode)

          t "cli: add refuses another repository's item unless the group is cross-repository" (fun () ->
              let root = fixture ()
              write root "planner.json" """{"grouping":{"executionRepositories":{"TASK-B":"other/repo"}}}"""
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              create root [ "--id"; "GROUP-ECHELON-FIXTURE-001"; "--member"; "TASK-A"; "--cross-repository" ] |> PraxisCli.ok |> ignore
              let config = Path.Combine(root, "planner.json")

              let refused = add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--config"; config; "--json" ]
              Assert.equal 1 refused.ExitCode
              Assert.equal "repository-mismatch" (PraxisCli.text (refused.Json["rejections"].[0].["code"]))
              Assert.isTrue (refused.Error.Contains "other/repo") "the refusal names the item's repository"

              add root [ "--id"; "GROUP-ECHELON-FIXTURE-001"; "--member"; "TASK-B"; "--config"; config ] |> PraxisCli.ok |> ignore
              Assert.isTrue (File.Exists(ledgerPath root)) "the cross-repository addition is recorded")

          t "cli: validate reports a recorded addition the group does not list" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              match WorkGroupJson.parseStore (File.ReadAllText(groupsPath root)) with
              | Ok entries -> write root ".ros/work/groups.json" (WorkGroupJson.renderStore (entries |> List.map (fun entry -> { entry with Group = { entry.Group with Members = [ "TASK-A" ] } })))
              | Error message -> failwith message
              let broken = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.equal 1 broken.ExitCode
              Assert.isTrue (broken.Output.Contains "does not list it") "a ledger/group mismatch is a finding") 
          t "remove: a member leaves in declared order; non-members, the last member and blank reasons are refused" (fun () ->
              let entries = [ stored (declared "GROUP-CLI-001" [ "TASK-A"; "TASK-B"; "TASK-D" ]); stored (declared "GROUP-CLI-002" [ "TASK-A" ]) ]

              match GroupMembership.decideRemoval entries (removal "GROUP-CLI-001" "TASK-B") with
              | Ok updated ->
                  Assert.equal [ "TASK-A"; "TASK-D" ] updated.Group.Members
                  Assert.equal entries.[0].DeclaredAt updated.DeclaredAt
                  Assert.equal entries.[0].DeclaredBy updated.DeclaredBy
              | Error found -> failwith $"expected the removal to be accepted, found %A{found}"

              // A member that completed after declaration may leave; its state is never consulted.
              Assert.isTrue (GroupMembership.decideRemoval entries (removal "GROUP-CLI-001" "TASK-D") |> Result.isOk) "a terminal member may leave"

              let refused group workItem = GroupMembership.decideRemoval entries (removal group workItem) |> rejections
              Assert.equal [ GroupRemovalRejection.NotMember("TASK-C", "GROUP-CLI-001") ] (refused "GROUP-CLI-001" "TASK-C")
              Assert.equal [ GroupRemovalRejection.NotMember("NOPE-1", "GROUP-CLI-001") ] (refused "GROUP-CLI-001" "NOPE-1")
              Assert.equal [ GroupRemovalRejection.LastMember("TASK-A", "GROUP-CLI-002") ] (refused "GROUP-CLI-002" "TASK-A")

              Assert.equal
                  [ GroupRemovalRejection.UndeclaredGroup "GROUP-CLI-404"; GroupRemovalRejection.BlankReason ]
                  (GroupMembership.decideRemoval entries { removal "GROUP-CLI-404" "TASK-A" with Reason = Some " " } |> rejections)

              Assert.equal
                  [ GroupRemovalRejection.NotMember("TASK-C", "GROUP-CLI-001"); GroupRemovalRejection.BlankReason ]
                  (GroupMembership.decideRemoval entries { removal "GROUP-CLI-001" "TASK-C" with Reason = Some "" } |> rejections))

          t "remove: removals round-trip in the ledger and findings follow each member's latest change" (fun () ->
              let added: MemberAddition =
                  { GroupId = "GROUP-CLI-001"
                    WorkItem = "TASK-B"
                    AddedAt = "2026-09-30T01:00:00.000Z"
                    AddedBy = actor
                    Reason = None }

              let removed: MemberRemoval =
                  { GroupId = "GROUP-CLI-001"
                    WorkItem = "TASK-B"
                    RemovedAt = "2026-09-30T02:00:00.000Z"
                    RemovedBy = actor
                    Reason = Some "belongs to the docs group" }

              let ledger = [ MembershipChange.Added added; MembershipChange.Removed removed ]
              Assert.equal (Ok ledger) (WorkGroupMembershipJson.parseLedger (WorkGroupMembershipJson.renderLedger ledger))

              let without = [ stored (declared "GROUP-CLI-001" [ "TASK-A" ]) ]
              let listing = [ stored (declared "GROUP-CLI-001" [ "TASK-A"; "TASK-B" ]) ]

              // An addition later undone is history, not a finding.
              Assert.empty (GroupMembership.findings without ledger)
              Assert.empty (GroupMembership.findings listing (ledger @ [ MembershipChange.Added { added with AddedAt = "2026-09-30T03:00:00.000Z" } ]))

              let stillListed = GroupMembership.findings listing ledger
              Assert.equal [ "changes[1]", "records TASK-B removed from GROUP-CLI-001, but the group still lists it" ] stillListed

              let unattributed = GroupMembership.findings without [ MembershipChange.Removed { removed with RemovedAt = ""; RemovedBy = { actor with Id = "" } } ] |> List.map snd
              Assert.equal [ "removedAt is required"; "removedBy.id is required" ] unattributed)

          t "cli: remove drops the member, records who removed it, and changes nothing else" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let before = untouched root

              let declaredBefore =
                  match WorkGroupJson.parseStore (File.ReadAllText(groupsPath root)) with
                  | Ok [ entry ] -> entry
                  | other -> failwith $"expected one stored group, found %A{other}"

              let removed = remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--reason"; "belongs elsewhere"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-remove" (PraxisCli.text (removed.Json["kind"]))
              Assert.equal "removed" (PraxisCli.text (removed.Json["status"]))
              Assert.equal [ "TASK-B" ] (members (removed.Json["members"]))
              Assert.equal "example/agent" (PraxisCli.text ((removed.Json["removal"]).["removedBy"].["id"]))
              Assert.isTrue (not (flag (removed.Json["lifecycleChanged"]))) "remove changes no lifecycle state"
              Assert.equal before (untouched root)

              match WorkGroupJson.parseStore (File.ReadAllText(groupsPath root)) with
              | Ok [ entry ] ->
                  Assert.equal [ "TASK-B" ] entry.Group.Members
                  Assert.equal declaredBefore.DeclaredAt entry.DeclaredAt
                  Assert.equal declaredBefore.DeclaredBy entry.DeclaredBy
              | other -> failwith $"expected one stored group, found %A{other}"

              match WorkGroupMembershipJson.parseLedger (File.ReadAllText(ledgerPath root)) with
              | Ok [ MembershipChange.Removed recorded ] ->
                  Assert.equal "TASK-A" recorded.WorkItem
                  Assert.equal "example/agent" recorded.RemovedBy.Id
                  Assert.equal (Some "belongs elsewhere") recorded.Reason
              | other -> failwith $"expected one recorded removal, found %A{other}"

              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ] |> PraxisCli.ok
              let group = groups.Json["groups"].AsArray() |> Seq.find (fun entry -> PraxisCli.text (entry["id"]) = "GROUP-FIXTURE-001")
              Assert.equal [ "TASK-B" ] (group["members"].AsArray() |> Seq.map (fun entry -> PraxisCli.text (entry["workItem"])) |> Seq.toList)

              // Removed then re-added: the ledger stays consistent.
              add root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let validated = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.isTrue (not (validated.Output.Contains "group-membership.json")) "a consistent ledger has no findings")

          t "cli: remove dry run and refusals write nothing; the last member stays; bad arguments exit 2" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              create root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A" ] |> PraxisCli.ok |> ignore
              let groupsBefore = hash root ".ros/work/groups.json"

              let planned = remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B"; "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.equal "planned" (PraxisCli.text (planned.Json["status"]))
              Assert.equal [ "TASK-A" ] (members (planned.Json["members"]))

              [ "GROUP-FIXTURE-001", "TASK-C", "not-member"; "GROUP-FIXTURE-001", "NOPE-1", "not-member"; "GROUP-FIXTURE-002", "TASK-A", "last-member"; "GROUP-FIXTURE-404", "TASK-A", "undeclared-group" ]
              |> List.iter (fun (group, workItem, code) ->
                  let refused = remove root [ "--id"; group; "--member"; workItem; "--json" ]
                  Assert.equal 1 refused.ExitCode
                  Assert.equal "rejected" (PraxisCli.text (refused.Json["status"]))
                  Assert.equal code (PraxisCli.text (refused.Json["rejections"].[0].["code"])))

              let last = remove root [ "--id"; "GROUP-FIXTURE-002"; "--member"; "TASK-A" ]
              Assert.isTrue (last.Error.Contains "last member of GROUP-FIXTURE-002") "the refusal explains the last-member rule"

              Assert.equal groupsBefore (hash root ".ros/work/groups.json")
              Assert.isTrue (not (File.Exists(ledgerPath root))) "no refusal or dry run records a removal"

              Assert.equal 2 (remove root [ "--id"; "GROUP-FIXTURE-001" ]).ExitCode
              Assert.equal 2 (remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal 2 (remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--config"; "planner.json" ]).ExitCode
              Assert.equal 2 (remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--cross-repository" ]).ExitCode)

          t "cli: validate reports a recorded removal the group still lists" (fun () ->
              let root = fixture ()
              create root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-A"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              let original = File.ReadAllText(groupsPath root)
              remove root [ "--id"; "GROUP-FIXTURE-001"; "--member"; "TASK-B" ] |> PraxisCli.ok |> ignore
              write root ".ros/work/groups.json" original
              let broken = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.equal 1 broken.ExitCode
              Assert.isTrue (broken.Output.Contains "still lists it") "a ledger/group mismatch is a finding") ]
