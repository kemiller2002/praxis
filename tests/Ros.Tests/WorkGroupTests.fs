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

/// `work group create`: durable human-declared execution groups
/// (PRAXIS-GROUP-01; requirements/PLANNING-WORK-GROUPS.md PRX-GRP-073 phase
/// two).
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

    let private groupsPath (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

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
              Assert.isTrue (broken.Output.Contains "GONE-1") "an unknown stored member is a finding") ]
