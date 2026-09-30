namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Infrastructure.Planning

/// `work group create`: durable human-declared execution groups
/// (PRAXIS-GROUP-01; requirements/PLANNING-WORK-GROUPS.md PRX-GRP-073).
module WorkGroupTests =
    let private t name run = { Name = $"work group: {name}"; Run = run }

    // ---- Domain ------------------------------------------------------------------

    let private lifecycle =
        Map.ofList [ "A-1", "ready"; "A-2", "active"; "A-3", "blocked"; "A-4", "captured"; "D-1", "complete"; "D-2", "abandoned" ]

    let private request members =
        { Id = "GROUP-PRAXIS-TEST-001"
          Members = members
          Kind = Some GroupKind.SharedApiSurface
          SharedContext = [ "one command family" ]
          ExecutionRepository = Some "praxis"
          CrossRepository = false
          OccurredAt = "2026-09-30T12:00:00.000Z"
          CreatedBy = "agent:test" }

    let private rejections result =
        match result with
        | Error found -> found
        | Ok _ -> failwith "expected a rejection"

    let private storedGroup crossRepository executionRepository =
        match GroupDeclaration.decide Set.empty lifecycle { request [ "A-1"; "A-2" ] with CrossRepository = crossRepository; ExecutionRepository = executionRepository } with
        | Ok group -> group
        | Error found -> failwith $"%A{found}"

    /// A-3 executes in "other"; X-EXT in an unknown external repository;
    /// everything else in "praxis".
    let private locate id =
        match id with
        | "A-3" -> ExecutionLocation.Repository "other"
        | "X-EXT" -> ExecutionLocation.UnknownExternal
        | _ -> ExecutionLocation.Repository "praxis"

    let private addition groupId memberId =
        { GroupId = groupId
          Member = memberId
          OccurredAt = "2026-09-30T13:00:00.000Z"
          AddedBy = "agent:adder" }

    let private addTo stored memberId =
        GroupDeclaration.decideAddition stored (lifecycle.Add("X-EXT", "ready")) locate "praxis" (addition "GROUP-PRAXIS-TEST-001" memberId)

    // ---- CLI fixture -------------------------------------------------------------

    let private git (root: string) (arguments: string list) =
        let startInfo = ProcessStartInfo("git")
        startInfo.UseShellExecute <- false
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.WorkingDirectory <- root
        arguments |> List.iter startInfo.ArgumentList.Add
        use child = Process.Start startInfo
        let output = child.StandardOutput.ReadToEnd()
        child.StandardError.ReadToEnd() |> ignore
        child.WaitForExit()
        if child.ExitCode <> 0 then failwith $"git {String.Join(' ', arguments)} failed"
        output.Trim()

    let private write (root: string) (relative: string) (content: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private queue =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","nextSeq":1,"items":[
  {"id":"TASK-A","title":"Task A","description":"First.","tags":["cli"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-B","title":"Task B","description":"Second.","tags":["cli"],"priority":"high","status":"ready","createdAt":"2026-09-02T00:00:00.000Z","updatedAt":"2026-09-02T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-C","title":"Task C","description":"Third.","tags":["docs"],"priority":"low","status":"captured","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-D","title":"Task D","description":"Done.","tags":["cli"],"priority":"low","status":"complete","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-X","title":"Task X","description":"Implemented in an external repository.","tags":["cli"],"priority":"low","status":"ready","createdAt":"2026-09-04T00:00:00.000Z","updatedAt":"2026-09-04T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}
]}"""

    let private context =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[
  {"id":"TASK-B","type":"task","state":"active","semanticState":"active","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[]},
  {"id":"LIVE-E","type":"task","state":"abandoned","semanticState":"abandoned","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[]}
]}"""

    let private fixture () =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-group-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        git root [ "init"; "--quiet"; "--initial-branch=main" ] |> ignore
        git root [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
        git root [ "config"; "user.name"; "Fixture" ] |> ignore
        git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
        write root ".ros/work/queue.json" queue
        write root ".ros/context/current.json" context
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "Praxis state" ] |> ignore
        root

    let private hashes (root: string) =
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun path -> not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
        |> Array.sort
        |> Array.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> Map.ofArray

    let private groupsFile (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    let private create (root: string) (extra: string list) =
        PraxisCli.run
            root
            (Some(PraxisCli.agent "agent:fixture" "provider-a" "runtime-a" "session-1"))
            ([ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CLI-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--kind"; "shared-api-surface"
               "--execution-repository"; "group-fixture"; "--shared-context"; "one CLI surface"; "--occurred-at"; PraxisCli.now () ]
             @ extra)

    let private text (node: JsonNode) = node.GetValue<string>()

    let private addMember (root: string) (memberId: string) (extra: string list) =
        PraxisCli.run
            root
            (Some(PraxisCli.agent "agent:adder" "provider-b" "runtime-b" "session-2"))
            ([ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CLI-001"; "--member"; memberId; "--occurred-at"; PraxisCli.now () ] @ extra)

    let private declaredIn (groups: JsonObject) (id: string) =
        groups["groups"].AsArray() |> Seq.map (fun group -> group.AsObject()) |> Seq.tryFind (fun group -> text group["id"] = id)

    let tests =
        [ t "a valid declaration is recorded as human-declared with distinct members" (fun () ->
              match GroupDeclaration.decide Set.empty lifecycle (request [ "A-1"; "A-2"; "A-3"; "A-4" ]) with
              | Error found -> failwith $"%A{found}"
              | Ok group ->
                  Assert.equal GroupOrigin.HumanDeclared group.Declaration.Origin
                  Assert.equal [ "A-1"; "A-2"; "A-3"; "A-4" ] group.Declaration.Members
                  Assert.equal (Some "praxis") group.Declaration.ExecutionRepository
                  Assert.equal "agent:test" group.CreatedBy)

          t "unknown and terminal members are refused, each named" (fun () ->
              let found = GroupDeclaration.decide Set.empty lifecycle (request [ "A-1"; "X-9"; "D-1"; "D-2" ]) |> rejections

              Assert.equal
                  [ GroupCreationRejection.UnknownMember "X-9"
                    GroupCreationRejection.TerminalMember("D-1", "complete")
                    GroupCreationRejection.TerminalMember("D-2", "abandoned") ]
                  found)

          t "a duplicate group ID is refused" (fun () ->
              let found = GroupDeclaration.decide (set [ "GROUP-PRAXIS-TEST-001" ]) lifecycle (request [ "A-1"; "A-2" ]) |> rejections
              Assert.equal [ GroupCreationRejection.DuplicateId "GROUP-PRAXIS-TEST-001" ] found)

          t "malformed IDs, repeated members, single members and bad timestamps are refused" (fun () ->
              let found =
                  GroupDeclaration.decide Set.empty lifecycle { request [ "A-1"; "A-1" ] with Id = "group-1"; OccurredAt = "yesterday" } |> rejections

              Assert.equal
                  [ GroupCreationRejection.InvalidId "group-1"
                    GroupCreationRejection.TooFewMembers 1
                    GroupCreationRejection.DuplicateMember "A-1"
                    GroupCreationRejection.InvalidTimestamp "yesterday" ]
                  found)

          t "stored groups round-trip through the JSON contract" (fun () ->
              let group =
                  match GroupDeclaration.decide Set.empty lifecycle (request [ "A-2"; "A-1" ]) with
                  | Ok group -> group
                  | Error found -> failwith $"%A{found}"

              let rendered = PlanningJson.renderStoredGroups [ group ]
              Assert.equal (Ok [ group ]) (PlanningJson.parseStoredGroups rendered)
              Assert.equal rendered (PlanningJson.parseStoredGroups rendered |> Result.map PlanningJson.renderStoredGroups |> Result.defaultValue ""))

          t "validate findings name structural defects but accept members that later completed" (fun () ->
              let stored =
                  [ { Declaration =
                        { Id = "GROUP-PRAXIS-TEST-001"
                          Members = [ "A-1"; "D-1" ]
                          Kind = None
                          Origin = GroupOrigin.HumanDeclared
                          SharedContext = []
                          ExecutionRepository = None
                          CrossRepository = false
                          ArchitectureNotes = [] }
                      CreatedAt = "2026-09-30T12:00:00.000Z"
                      CreatedBy = "agent:test"
                      Additions = [] } ]

              Assert.empty (GroupDeclaration.findings stored (set [ "A-1"; "D-1" ]))

              let broken = stored @ [ { stored.Head with Declaration = { stored.Head.Declaration with Members = [ "Z-1"; "Z-1" ] } } ]
              let found = GroupDeclaration.findings broken (set [ "A-1"; "D-1" ]) |> List.map (fun finding -> finding.Field, finding.Message)

              Assert.isTrue (found |> List.filter (fst >> (=) "id") |> List.length = 2) "both copies of a stored ID are reported"
              Assert.isTrue (found |> List.contains ("members", "member Z-1 is not a known work item (backlog queue or live context)")) "unknown member reported"
              Assert.isTrue (found |> List.contains ("members", "member Z-1 is listed more than once")) "repeated member reported")

          t "the planner merges stored declarations and refuses an ID also configured" (fun () ->
              let stored =
                  match GroupDeclaration.decide Set.empty lifecycle (request [ "A-1"; "A-2" ]) with
                  | Ok group -> group
                  | Error found -> failwith $"%A{found}"

              match GroupDeclaration.mergeInto PlannerConfiguration.defaults [ stored ] with
              | Ok merged -> Assert.equal [ stored.Declaration ] merged.Grouping.Groups
              | Error message -> failwith message

              let clashing =
                  { PlannerConfiguration.defaults with
                      Grouping = { GroupingConfiguration.defaults with Groups = [ stored.Declaration ] } }

              Assert.isTrue (GroupDeclaration.mergeInto clashing [ stored ] |> Result.isError) "a doubly declared ID is refused")

          t "cli create records the group and never changes a member's lifecycle state" (fun () ->
              let root = fixture ()
              let before = hashes root
              let result = create root [ "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-created" (text result.Json["kind"])
              Assert.equal "agent:fixture" (text (result.Json["group"].["createdBy"]))
              let after = hashes root

              let changed =
                  (Set.ofSeq (Map.keys before) + Set.ofSeq (Map.keys after))
                  |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)
                  |> Set.toList

              Assert.equal [ ".ros/work/groups.json" ] changed

              match FileWorkGroupStore.read root with
              | Ok [ group ] ->
                  Assert.equal "GROUP-FIXTURE-CLI-001" group.Declaration.Id
                  Assert.equal [ "TASK-A"; "TASK-B" ] group.Declaration.Members
                  Assert.equal (Some GroupKind.SharedApiSurface) group.Declaration.Kind
                  Assert.equal [ "one CLI surface" ] group.Declaration.SharedContext
              | other -> failwith $"%A{other}")

          t "cli dry run reports the group and writes nothing" (fun () ->
              let root = fixture ()
              let before = hashes root
              let result = create root [ "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-planned" (text result.Json["kind"])
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dryRun is reported"
              Assert.equal before (hashes root)
              Assert.isTrue (not (File.Exists(groupsFile root))) "no groups file")

          t "cli refuses a duplicate ID and unknown or terminal members without writing" (fun () ->
              let root = fixture ()
              create root [] |> PraxisCli.ok |> ignore
              let before = hashes root
              let duplicate = create root [ "--json" ]
              Assert.equal 1 duplicate.ExitCode
              Assert.equal "work-group-rejected" (text duplicate.Json["kind"])

              let members =
                  PraxisCli.run
                      root
                      None
                      [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CLI-002"; "--member"; "TASK-A"; "--member"; "TASK-D"; "--member"; "LIVE-E"
                        "--member"; "NOT-THERE"; "--occurred-at"; PraxisCli.now () ]

              Assert.equal 1 members.ExitCode
              Assert.isTrue (members.Error.Contains "member TASK-D is complete") members.Error
              Assert.isTrue (members.Error.Contains "member LIVE-E is abandoned") members.Error
              Assert.isTrue (members.Error.Contains "member NOT-THERE is not a known work item") members.Error
              Assert.equal before (hashes root))

          t "cli rejects malformed arguments with exit 2" (fun () ->
              let root = fixture ()
              let result = PraxisCli.run root None [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CLI-003"; "--member"; "TASK-A"; "--kind"; "bogus" ]
              Assert.equal 2 result.ExitCode
              Assert.isTrue (result.Error.Contains "--occurred-at") result.Error
              Assert.isTrue (result.Error.Contains "--kind 'bogus'") result.Error)

          t "the planner reads a stored group exactly as a configured grouping.groups entry" (fun () ->
              let stored = fixture ()
              create stored [] |> PraxisCli.ok |> ignore
              let fromState = PraxisCli.run stored None [ "plan"; "groups"; "--json"; "--as-of"; "2026-09-30T00:00:00Z" ] |> PraxisCli.ok

              let configured = fixture ()

              let declaration =
                  match FileWorkGroupStore.read stored with
                  | Ok [ group ] -> group.Declaration
                  | other -> failwith $"%A{other}"

              let configuration =
                  """{"grouping":{"groups":[{"id":"__ID__","members":["TASK-A","TASK-B"],"kind":"shared-api-surface","origin":"human-declared","sharedContext":["one CLI surface"],"executionRepository":"group-fixture","crossRepository":false}]}}"""
                      .Replace("__ID__", declaration.Id)

              let configPath = Path.Combine(Path.GetTempPath(), $"praxis-group-config-{Guid.NewGuid():N}.json")
              File.WriteAllText(configPath, configuration)

              let fromConfig =
                  PraxisCli.run configured None [ "plan"; "groups"; "--json"; "--as-of"; "2026-09-30T00:00:00Z"; "--config"; configPath ] |> PraxisCli.ok

              let groupsOf (result: PraxisCli.Result) = result.Json["groups"].ToJsonString()
              Assert.equal (groupsOf fromConfig) (groupsOf fromState)

              match declaredIn fromState.Json "GROUP-FIXTURE-CLI-001" with
              | Some group -> Assert.equal "human-declared" (text group["origin"])
              | None -> failwith "the stored group is not reported by plan groups"

              let clash = PraxisCli.run stored None [ "plan"; "groups"; "--json"; "--config"; configPath ]
              Assert.equal 1 clash.ExitCode
              Assert.isTrue (clash.Error.Contains "declared both in Praxis state") clash.Error)

          t "validate checks stored groups" (fun () ->
              let root = fixture ()
              Assert.empty (FileWorkGroupRepository.validationFindings root)
              create root [] |> PraxisCli.ok |> ignore
              Assert.empty (FileWorkGroupRepository.validationFindings root)

              let file = groupsFile root
              File.WriteAllText(file, File.ReadAllText(file).Replace("\"TASK-B\"", "\"GONE-1\""))

              Assert.equal
                  [ ".ros/work/groups.json", "groups[GROUP-FIXTURE-CLI-001].members", "member GONE-1 is not a known work item (backlog queue or live context)" ]
                  (FileWorkGroupRepository.validationFindings root)

              File.WriteAllText(file, "{ not json")

              match FileWorkGroupRepository.validationFindings root with
              | [ path, "groups", message ] ->
                  Assert.equal ".ros/work/groups.json" path
                  Assert.isTrue (message.Contains "malformed work groups") message
              | other -> failwith $"%A{other}")

          // ---- work group add (PRAXIS-GROUP-03) -----------------------------------------

          t "add appends a member and records who added it and when" (fun () ->
              match addTo [ storedGroup false (Some "praxis") ] "A-4" with
              | Error found -> failwith $"%A{found}"
              | Ok group ->
                  Assert.equal [ "A-1"; "A-2"; "A-4" ] group.Declaration.Members
                  Assert.equal "agent:test" group.CreatedBy

                  Assert.equal
                      [ { Member = "A-4"
                          AddedAt = "2026-09-30T13:00:00.000Z"
                          AddedBy = "agent:adder" } ]
                      group.Additions)

          t "add refuses an unknown group, a present member and unknown or terminal items" (fun () ->
              let stored = [ storedGroup false (Some "praxis") ]

              Assert.equal
                  [ MemberAdditionRejection.UnknownGroup "GROUP-PRAXIS-TEST-404" ]
                  (GroupDeclaration.decideAddition stored lifecycle locate "praxis" (addition "GROUP-PRAXIS-TEST-404" "A-4") |> rejections)

              Assert.equal [ MemberAdditionRejection.AlreadyMember("GROUP-PRAXIS-TEST-001", "A-2") ] (addTo stored "A-2" |> rejections)
              Assert.equal [ MemberAdditionRejection.UnknownMember "Z-9" ] (addTo stored "Z-9" |> rejections)
              Assert.equal [ MemberAdditionRejection.TerminalMember("D-1", "complete") ] (addTo stored "D-1" |> rejections)
              Assert.equal [ MemberAdditionRejection.TerminalMember("D-2", "abandoned") ] (addTo stored "D-2" |> rejections)

              Assert.equal
                  [ MemberAdditionRejection.InvalidTimestamp "later" ]
                  (GroupDeclaration.decideAddition stored lifecycle locate "praxis" { addition "GROUP-PRAXIS-TEST-001" "A-4" with OccurredAt = "later" }
                   |> rejections))

          t "add refuses an item executing elsewhere unless the group is cross-repository" (fun () ->
              let single = [ storedGroup false (Some "praxis") ]
              Assert.equal [ MemberAdditionRejection.RepositoryMismatch("A-3", "other", "praxis") ] (addTo single "A-3" |> rejections)

              Assert.equal
                  [ MemberAdditionRejection.RepositoryMismatch("X-EXT", "unknown external repository", "praxis") ]
                  (addTo single "X-EXT" |> rejections)

              let implicit = [ storedGroup false None ]
              Assert.isTrue (addTo implicit "A-4" |> Result.isOk) "a group without a declared repository executes in the planned one"
              Assert.isTrue (addTo implicit "A-3" |> Result.isError) "and still refuses another repository"

              let cross = [ storedGroup true (Some "praxis") ]
              Assert.isTrue (addTo cross "A-3" |> Result.isOk) "a cross-repository group admits another repository"
              Assert.isTrue (addTo cross "X-EXT" |> Result.isOk) "and an external one")

          t "additions round-trip, and a stored group without additions still reads" (fun () ->
              let group =
                  match addTo [ storedGroup false (Some "praxis") ] "A-4" with
                  | Ok group -> group
                  | Error found -> failwith $"%A{found}"

              let rendered = PlanningJson.renderStoredGroups [ group ]
              Assert.equal (Ok [ group ]) (PlanningJson.parseStoredGroups rendered)

              let legacy = JsonNode.Parse(rendered).AsObject()
              legacy["groups"].[0].AsObject().Remove "additions" |> ignore

              Assert.equal
                  (Ok [ { group with Additions = [] } ])
                  (PlanningJson.parseStoredGroups (legacy.ToJsonString()))

              Assert.empty (GroupDeclaration.findings [ group ] (set [ "A-1"; "A-2"; "A-4" ]))

              let orphan = { group with Additions = group.Additions @ [ { Member = "A-9"; AddedAt = "soon"; AddedBy = "" } ] }

              Assert.equal
                  [ "added member A-9 is not a member"
                    "'soon' (addition of A-9) is not a timestamp"
                    "the actor who added A-9 is missing" ]
                  (GroupDeclaration.findings [ orphan ] (set [ "A-1"; "A-2"; "A-4" ]) |> List.map (fun finding -> finding.Message)))

          t "cli add records the member and its adder and changes only the groups file" (fun () ->
              let root = fixture ()
              create root [] |> PraxisCli.ok |> ignore
              let before = hashes root
              let result = addMember root "TASK-C" [ "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-member-added" (text result.Json["kind"])
              Assert.equal "agent:adder" (text (result.Json["addition"].["addedBy"]))
              let after = hashes root

              let changed =
                  (Set.ofSeq (Map.keys before) + Set.ofSeq (Map.keys after))
                  |> Set.filter (fun path -> before.TryFind path <> after.TryFind path)
                  |> Set.toList

              Assert.equal [ ".ros/work/groups.json" ] changed

              match FileWorkGroupStore.read root with
              | Ok [ group ] ->
                  Assert.equal [ "TASK-A"; "TASK-B"; "TASK-C" ] group.Declaration.Members
                  Assert.equal "agent:fixture" group.CreatedBy
                  Assert.equal [ "TASK-C" ] (group.Additions |> List.map (fun addition -> addition.Member))
                  Assert.equal [ "agent:adder" ] (group.Additions |> List.map (fun addition -> addition.AddedBy))
              | other -> failwith $"%A{other}"

              Assert.empty (FileWorkGroupRepository.validationFindings root)

              let planned = PraxisCli.run root None [ "plan"; "groups"; "--json"; "--as-of"; "2026-09-30T00:00:00Z" ] |> PraxisCli.ok

              match declaredIn planned.Json "GROUP-FIXTURE-CLI-001" with
              | Some group -> Assert.isTrue (group["members"].ToJsonString().Contains "TASK-C") (group.ToJsonString())
              | None -> failwith "the stored group is not reported by plan groups")

          t "cli add dry run reports the addition and writes nothing" (fun () ->
              let root = fixture ()
              create root [] |> PraxisCli.ok |> ignore
              let before = hashes root
              let result = addMember root "TASK-C" [ "--dry-run"; "--json" ] |> PraxisCli.ok
              Assert.equal "work-group-member-planned" (text result.Json["kind"])
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dryRun is reported"
              Assert.equal before (hashes root))

          t "cli add refuses present, unknown, terminal and external items without writing" (fun () ->
              let root = fixture ()
              create root [] |> PraxisCli.ok |> ignore
              let before = hashes root

              let refused memberId (expected: string) =
                  let result = addMember root memberId []
                  Assert.equal 1 result.ExitCode
                  Assert.isTrue (result.Error.Contains expected) result.Error

              refused "TASK-A" "TASK-A is already a member of GROUP-FIXTURE-CLI-001"
              refused "NOT-THERE" "NOT-THERE is not a known work item"
              refused "TASK-D" "TASK-D is complete"
              refused "LIVE-E" "LIVE-E is abandoned"
              refused "TASK-X" "TASK-X executes in unknown external repository but the group executes in group-fixture"

              let unknown = PraxisCli.run root None [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CLI-404"; "--member"; "TASK-C"; "--occurred-at"; PraxisCli.now (); "--json" ]
              Assert.equal 1 unknown.ExitCode
              Assert.equal "work-group-rejected" (text unknown.Json["kind"])
              Assert.equal before (hashes root))

          t "cli add honours explicit execution repositories and cross-repository groups" (fun () ->
              let root = fixture ()
              create root [] |> PraxisCli.ok |> ignore

              let configPath = Path.Combine(Path.GetTempPath(), $"praxis-group-config-{Guid.NewGuid():N}.json")
              File.WriteAllText(configPath, """{"grouping":{"executionRepositories":{"TASK-C":"elsewhere"}}}""")

              let mismatch = addMember root "TASK-C" [ "--config"; configPath ]
              Assert.equal 1 mismatch.ExitCode
              Assert.isTrue (mismatch.Error.Contains "TASK-C executes in elsewhere but the group executes in group-fixture") mismatch.Error

              PraxisCli.run
                  root
                  None
                  [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CROSS-001"; "--member"; "TASK-A"; "--member"; "TASK-B"; "--cross-repository"
                    "--occurred-at"; PraxisCli.now () ]
              |> PraxisCli.ok
              |> ignore

              let cross =
                  PraxisCli.run root None [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CROSS-001"; "--member"; "TASK-X"; "--occurred-at"; PraxisCli.now () ]
                  |> PraxisCli.ok

              Assert.isTrue (cross.Output.Contains "added TASK-X to GROUP-FIXTURE-CROSS-001") cross.Output)

          t "cli add rejects malformed arguments with exit 2" (fun () ->
              let root = fixture ()

              let result =
                  PraxisCli.run root None [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CLI-001"; "--member"; "TASK-A"; "--member"; "TASK-C"; "--kind"; "shared-files" ]

              Assert.equal 2 result.ExitCode
              Assert.isTrue (result.Error.Contains "pass --member once") result.Error
              Assert.isTrue (result.Error.Contains "--occurred-at") result.Error
              Assert.isTrue (result.Error.Contains "--kind is a work group create option") result.Error) ]
