namespace Ros.Tests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open PlanningFixtures

/// `work group create` (PRAXIS-GROUP-01, PRX-GRP-073 phase two): declared
/// execution groups recorded in `.ros/work/groups.json`.
module WorkGroupTests =
    let private t name run = { Name = $"work group: {name}"; Run = run }

    let private declared (id: string) (members: string list) : DeclaredGroup =
        { Id = id
          Members = members
          Kind = Some GroupKind.SharedApiSurface
          Origin = GroupOrigin.HumanDeclared
          SharedContext = [ "one typed group store" ]
          ExecutionRepository = Some "praxis"
          CrossRepository = false
          ArchitectureNotes = [ "members keep their own attribution" ] }

    let private actor =
        { Kind = ActorKind.Agent
          Id = "test/agent"
          Provider = Some "test"
          Model = Some "unknown"
          Runtime = Some "test-runtime" }

    let private queue =
        [ queued "A-1" "ready" "2026-09-01T00:00:00Z"
          queued "A-2" "captured" "2026-09-01T00:00:00Z"
          queued "A-3" "abandoned" "2026-09-01T00:00:00Z"
          queued "A-4" "ready" "2026-09-01T00:00:00Z" ]

    let private liveItems = [ live "A-4" LiveWorkState.Complete; live "L-1" LiveWorkState.Active ]

    let private states = GroupDeclaration.memberStates queue liveItems

    let private create store declaration =
        GroupDeclaration.create store states "2026-10-07T00:00:00.000Z" (Some actor) declaration

    let private recorded store declaration =
        match create store declaration with
        | Ok(updated, _) -> updated
        | Error rejections -> failwith $"unexpected rejection: {rejections}"

    let private rejectionsOf store declaration =
        match create store declaration with
        | Ok _ -> failwith "expected a rejection"
        | Error rejections -> rejections

    // ---- CLI fixture ---------------------------------------------------------

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

    let private fixtureQueue =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","nextSeq":1,"items":[
  {"id":"TASK-A","title":"Task A","tags":["alpha"],"priority":"high","status":"ready","createdAt":"2026-09-01T00:00:00.000Z","updatedAt":"2026-09-01T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-B","title":"Task B","tags":["beta"],"priority":"high","status":"ready","createdAt":"2026-09-02T00:00:00.000Z","updatedAt":"2026-09-02T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null},
  {"id":"TASK-X","title":"Task X","tags":[],"priority":"low","status":"abandoned","createdAt":"2026-09-03T00:00:00.000Z","updatedAt":"2026-09-03T00:00:00.000Z","createdBy":"unknown","source":"manual","sourceReference":null}
]}"""

    let private fixtureContext =
        """{"schemaVersion":"1.0.0","repository":"group-fixture","protocolVersion":"1.0.0","actor":"test","updatedAt":"2026-09-10T00:00:00.000Z","workItems":[
  {"id":"TASK-DONE","type":"task","state":"complete","semanticState":"complete","evidence":[],"updatedAt":"2026-09-10T00:00:00.000Z","telemetryExecutionIds":[]}
]}"""

    let private fixture () =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-group-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore
        git root [ "init"; "--quiet"; "--initial-branch=main" ] |> ignore
        git root [ "config"; "user.email"; "fixture@example.invalid" ] |> ignore
        git root [ "config"; "user.name"; "Fixture" ] |> ignore
        git root [ "config"; "commit.gpgsign"; "false" ] |> ignore
        write root ".ros/work/queue.json" fixtureQueue
        write root ".ros/context/current.json" fixtureContext
        git root [ "add"; "-A" ] |> ignore
        git root [ "commit"; "--quiet"; "-m"; "Praxis state" ] |> ignore
        root

    let private storePath (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    /// Every file outside .git, hashed.
    let private fingerprint (root: string) =
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        |> Array.filter (fun path -> not (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
        |> Array.sort
        |> Array.map (fun path -> Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))
        |> fun lines -> String.Join("\n", lines)

    let private memberFiles (root: string) =
        [ ".ros/work/queue.json"; ".ros/context/current.json" ]
        |> List.map (fun relative -> Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative)))))

    let private executor = Some(PraxisCli.agent "test/agent" "test" "test-runtime" "session-1")

    let private createArguments (id: string) (members: string list) (extra: string list) =
        [ "work"; "group"; "create"; "--id"; id ]
        @ (members |> List.collect (fun memberId -> [ "--member"; memberId ]))
        @ [ "--occurred-at"; PraxisCli.now () ]
        @ extra

    let private text (node: JsonNode) = node.GetValue<string>()

    // ---- work group show (PRAXIS-GROUP-02) -------------------------------------

    let private showQueue =
        [ queued "S-1" "ready" "2026-09-01T00:00:00Z"
          { queued "S-2" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "S-1" ] }
          { queued "S-3" "ready" "2026-09-03T00:00:00Z" with DependsOn = [ "S-2" ] }
          queued "S-4" "ready" "2026-09-04T00:00:00Z"
          queued "S-5" "abandoned" "2026-09-05T00:00:00Z" ]

    let private showLive =
        [ { live "S-1" LiveWorkState.Blocked with BlockReason = Some "waiting on a design decision" }
          live "S-4" LiveWorkState.Complete ]

    let private showStore =
        { Groups =
            [ { Declaration = declared "GROUP-PRAXIS-SHOW-001" [ "S-1"; "S-2"; "S-3"; "S-4"; "S-5"; "GONE-1" ]
                DeclaredAt = "2026-10-07T00:00:00.000Z"
                DeclaredBy = Some actor } ] }

    let private showPorts () =
        let writes = ref 0

        let groups: WorkGroupPort =
            { ReadStore = fun () -> Ok showStore
              Queue = fun () -> Ok showQueue
              Live = fun () -> Ok showLive
              WriteStore = fun _ -> writes.Value <- writes.Value + 1; Error "show must not write" }

        let planning: PlanningReadPort =
            { Repository = fun () -> { Name = "praxis"; Commit = Some "1111111111111111111111111111111111111111"; Branch = Some "main" }
              Queue = fun () -> Ok showQueue
              Live = fun () -> Ok showLive
              Executions = fun () -> history
              RepositoryObservations = fun _ -> []
              SuppliedObservations = fun () -> Ok []
              Configuration = fun () -> Ok PlannerConfiguration.defaults
              DeclaredGroups = fun () -> Ok(GroupStore.declarations showStore) }

        groups, planning, writes

    let private shown id =
        let groups, planning, writes = showPorts ()

        match WorkGroupOperations.show groups planning "2026-10-07T00:00:00.000Z" "test" id with
        | Ok outcome -> outcome, writes.Value
        | Error message -> failwith message

    let private showView () =
        match shown "GROUP-PRAXIS-SHOW-001" with
        | GroupShowOutcome.Shown(view, _), 0 -> view
        | outcome -> failwith $"unexpected outcome {outcome}"

    let private memberView (view: GroupView) id = view.Members |> List.find (fun entry -> entry.WorkItemId = id)

    let private showTests =
        [ t "show: members carry their own recorded state beside the planner's" (fun () ->
              let view = showView ()
              Assert.equal [ "S-1"; "S-2"; "S-3"; "S-4"; "S-5"; "GONE-1" ] (view.Members |> List.map (fun entry -> entry.WorkItemId))
              Assert.equal (Some(MemberState.Open "blocked")) (memberView view "S-1").Recorded
              Assert.equal (Some(MemberState.Terminal "complete")) (memberView view "S-4").Recorded
              Assert.equal (Some(MemberState.Terminal "abandoned")) (memberView view "S-5").Recorded
              Assert.equal None (memberView view "GONE-1").Recorded
              Assert.equal None (memberView view "GONE-1").Planning
              Assert.equal (Some PlanningWorkState.Blocked) ((memberView view "S-1").Planning |> Option.map (fun planned -> planned.PlanningState))
              Assert.equal [ "blocked"; "runnable"; "runnable"; "complete"; "abandoned"; "unknown" ] (view.Members |> List.map GroupView.statusCode))

          t "show: partial-completion progress counts every declared member" (fun () ->
              let progress = (showView ()).Progress
              Assert.equal 6 progress.Total
              Assert.equal 1 progress.Complete
              Assert.equal 1 progress.Abandoned
              Assert.equal 1 progress.Blocked
              Assert.equal 2 progress.Runnable
              Assert.equal 1 progress.Unknown
              Assert.isTrue (progress.Statement.StartsWith "1 of 6 complete") progress.Statement
              Assert.isTrue (progress.Statement.Contains "unknown: GONE-1") progress.Statement)

          t "show: a blocked member lists the members it gates" (fun () ->
              let view = showView ()
              Assert.equal [ { WorkItemId = "S-1"; Gates = [ "S-2"; "S-3" ] } ] view.Blocked
              Assert.equal [ "S-1" ] ((memberView view "S-3").Planning.Value.GatedBy)
              Assert.empty (memberView view "S-3").Gates)

          t "show: declaration, repository and planner notes are reported; nothing is written" (fun () ->
              let view = showView ()
              Assert.equal (declared "GROUP-PRAXIS-SHOW-001" [ "S-1"; "S-2"; "S-3"; "S-4"; "S-5"; "GONE-1" ]) view.Group.Declaration
              Assert.equal (Some "praxis") view.PlannerExecutionRepository
              Assert.isTrue (view.Notes |> List.exists (fun note -> note.Code = GroupNoteCode.UnknownMember)) "the planner's unknown-member note")

          t "show: an unknown group is NotFound and nothing is written" (fun () ->
              match shown "GROUP-PRAXIS-NONE-001" with
              | GroupShowOutcome.NotFound "GROUP-PRAXIS-NONE-001", 0 -> ()
              | outcome -> failwith $"unexpected outcome {outcome}")

          t "cli: show renders text and --json and never writes" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [ "--architecture-note"; "one store" ])).ExitCode
              let before = fingerprint root
              let textResult = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-CORE-001" ]
              Assert.equal 0 textResult.ExitCode
              Assert.isTrue (textResult.Output.Contains "Group GROUP-FIXTURE-CORE-001" && textResult.Output.Contains "TASK-A" && textResult.Output.Contains "Architecture note: one store") textResult.Output
              let jsonResult = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-CORE-001"; "--json" ]
              Assert.equal 0 jsonResult.ExitCode
              Assert.equal "group-shown" (text (jsonResult.Json["kind"]))
              Assert.equal "GROUP-FIXTURE-CORE-001" (text (jsonResult.Json.["group"].["id"]))
              Assert.equal [ "TASK-A"; "TASK-B" ] (jsonResult.Json["members"].AsArray() |> Seq.map (fun entry -> text (entry["id"])) |> List.ofSeq)
              Assert.equal 2 (jsonResult.Json.["progress"].["total"].GetValue<int>())
              Assert.equal before (fingerprint root))

          t "cli: show reports partial completion from members' own states" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [])).ExitCode
              let completed =
                  fixtureContext.Replace(
                      "\n]}",
                      ",\n  {\"id\":\"TASK-B\",\"type\":\"task\",\"state\":\"complete\",\"semanticState\":\"complete\",\"evidence\":[],\"updatedAt\":\"2026-09-11T00:00:00.000Z\",\"telemetryExecutionIds\":[]}\n]}"
                  )

              write root ".ros/context/current.json" completed
              let result = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-CORE-001"; "--json" ]
              Assert.equal 0 result.ExitCode
              let taskB = result.Json["members"].AsArray() |> Seq.find (fun entry -> text (entry["id"]) = "TASK-B")
              Assert.equal "complete" (text (taskB["recordedState"]))
              Assert.isTrue (taskB["recordedTerminal"].GetValue<bool>()) "terminal"
              Assert.equal 1 (result.Json.["progress"].["complete"].GetValue<int>())
              // A member completing after declaration is not a validate finding (PRX-GRP-042).
              Assert.isTrue (not ((PraxisCli.run root None [ "validate"; "--json" ]).Output.Contains ".ros/work/groups.json")) "no group finding")

          t "cli: show of an unknown group exits 1; a missing ID exits 2" (fun () ->
              let root = fixture ()
              let before = fingerprint root
              let missing = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-NONE-001"; "--json" ]
              Assert.equal 1 missing.ExitCode
              Assert.equal "group-not-found" (text (missing.Json["kind"]))
              Assert.equal 1 (PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-NONE-001" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root None [ "work"; "group"; "show"; "A"; "B" ]).ExitCode
              Assert.equal before (fingerprint root)) ]

    let tests =
        [ t "create records a valid declaration and sorts the store by ID" (fun () ->
              let store = recorded (recorded GroupStore.empty (declared "GROUP-PRAXIS-B-001" [ "A-1" ])) (declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2"; "L-1" ])
              Assert.equal [ "GROUP-PRAXIS-A-001"; "GROUP-PRAXIS-B-001" ] (store.Groups |> List.map (fun group -> group.Declaration.Id))
              let stored = (GroupStore.tryFind "GROUP-PRAXIS-A-001" store).Value
              Assert.equal (declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2"; "L-1" ]) stored.Declaration
              Assert.equal (Some actor) stored.DeclaredBy
              Assert.equal "2026-10-07T00:00:00.000Z" stored.DeclaredAt)

          t "create refuses unknown and terminal members" (fun () ->
              Assert.equal
                  [ GroupRejection.UnknownMember "NOPE-1"
                    GroupRejection.TerminalMember("A-3", "abandoned")
                    GroupRejection.TerminalMember("A-4", "complete") ]
                  (rejectionsOf GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1"; "NOPE-1"; "A-3"; "A-4" ])))

          t "live context outranks the backlog status" (fun () ->
              Assert.equal (Some(MemberState.Terminal "complete")) (states.TryFind "A-4")
              Assert.equal (Some(MemberState.Open "active")) (states.TryFind "L-1")
              Assert.equal (Some(MemberState.Open "captured")) (states.TryFind "A-2"))

          t "create refuses a duplicate group ID" (fun () ->
              let store = recorded GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1" ])
              Assert.equal [ GroupRejection.DuplicateGroupId "GROUP-PRAXIS-A-001" ] (rejectionsOf store (declared "GROUP-PRAXIS-A-001" [ "A-2" ])))

          t "create refuses malformed declarations" (fun () ->
              Assert.equal [ GroupRejection.InvalidGroupId "group-1"; GroupRejection.NoMembers ] (rejectionsOf GroupStore.empty (declared "group-1" []))

              Assert.equal [ GroupRejection.DuplicateMember "A-1" ] (rejectionsOf GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-1" ]))

              Assert.equal
                  [ GroupRejection.CrossRepositoryWithoutRepository ]
                  (rejectionsOf GroupStore.empty { declared "GROUP-ECHELON-A-001" [ "A-1" ] with CrossRepository = true; ExecutionRepository = None }))

          t "the store round-trips and each entry parses as a grouping.groups entry" (fun () ->
              let store = recorded GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2" ])
              let json = PlanningJson.renderGroupStore store
              Assert.equal (Ok store) (PlanningJson.parseGroupStore json)
              Assert.equal json (PlanningJson.renderGroupStore store)
              let entry = JsonObject()
              PlanningJson.declaredGroupFields (Assert.single store.Groups).Declaration |> List.iter (fun (name, value) -> entry[name] <- value)
              let configuration = $"""{{"grouping":{{"groups":[{entry.ToJsonString()}]}}}}"""

              match PlanningJson.parseConfiguration configuration with
              | Ok parsed -> Assert.equal (GroupStore.declarations store) parsed.Grouping.Groups
              | Error message -> failwith message)

          t "a store with another schema is refused" (fun () ->
              Assert.isTrue (PlanningJson.parseGroupStore """{"schema":"other/1","groups":[]}""" |> Result.isError) "unknown schema"
              Assert.isTrue (PlanningJson.parseGroupStore """{"groups":[]}""" |> Result.isError) "missing schema")

          t "the planner reads a stored declaration exactly as a configured one" (fun () ->
              let group = declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2" ]

              let port configuration stored : PlanningReadPort =
                  { Repository = fun () -> { Name = "fixture"; Commit = Some "1111111111111111111111111111111111111111"; Branch = Some "main" }
                    Queue = fun () -> Ok queue
                    Live = fun () -> Ok liveItems
                    Executions = fun () -> history
                    RepositoryObservations = fun _ -> []
                    SuppliedObservations = fun () -> Ok []
                    Configuration = fun () -> Ok configuration
                    DeclaredGroups = fun () -> Ok stored }

              let configured =
                  { stateSafe with Grouping = { stateSafe.Grouping with Groups = [ group ] } }

              let report port =
                  match PlanningOperations.analyze port "2026-10-07T00:00:00.000Z" "test" with
                  | Ok(planningInput, analysis) -> planningInput.Configuration, Grouping.recommend planningInput analysis
                  | Error message -> failwith message

              Assert.equal (report (port configured [])) (report (port stateSafe [ group ])))

          t "a configured group outranks a stored group with the same ID" (fun () ->
              let configured = declared "GROUP-PRAXIS-A-001" [ "A-1" ]
              let stored = [ declared "GROUP-PRAXIS-A-001" [ "A-2" ]; declared "GROUP-PRAXIS-B-001" [ "A-2" ] ]
              let merged = GroupDeclaration.mergeInto { GroupingConfiguration.defaults with Groups = [ configured ] } stored
              Assert.equal [ configured; List.last stored ] merged.Groups)

          t "validate findings: unknown members and duplicate IDs, not partial completion" (fun () ->
              let stored id members =
                  { Declaration = declared id members
                    DeclaredAt = "2026-10-07T00:00:00.000Z"
                    DeclaredBy = None }

              let store = { Groups = [ stored "GROUP-PRAXIS-A-001" [ "A-1"; "A-4" ]; stored "GROUP-PRAXIS-A-001" [ "GONE-1" ] ] }
              let findings = GroupDeclaration.findings store states
              Assert.equal 2 findings.Length
              Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "declared more than once")) "duplicate ID"
              Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "GONE-1")) "unknown member"
              Assert.empty (GroupDeclaration.findings { Groups = [ stored "GROUP-PRAXIS-A-001" [ "A-1"; "A-4" ] ] } states))

          t "cli: create records the group and changes no member's state" (fun () ->
              let root = fixture ()
              let before = memberFiles root
              let result = PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [ "--kind"; "dependency-chain"; "--json" ])
              Assert.equal 0 result.ExitCode
              Assert.equal "group-created" (text (result.Json["kind"]))
              Assert.isTrue (result.Json["recorded"].GetValue<bool>()) "recorded"
              Assert.equal "test/agent" (text (result.Json.["group"].["declaredBy"].["id"]))
              Assert.equal before (memberFiles root)

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok store ->
                  let group = (Assert.single store.Groups).Declaration
                  Assert.equal [ "TASK-A"; "TASK-B" ] group.Members
                  Assert.equal (Some GroupKind.DependencyChain) group.Kind
                  Assert.equal GroupOrigin.HumanDeclared group.Origin
              | Error message -> failwith message)

          t "cli: --dry-run writes nothing" (fun () ->
              let root = fixture ()
              let before = fingerprint root
              let result = PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [ "--dry-run"; "--json" ])
              Assert.equal 0 result.ExitCode
              Assert.isTrue (result.Json["dryRun"].GetValue<bool>()) "dry run"
              Assert.isTrue (not (result.Json["recorded"].GetValue<bool>())) "not recorded"
              Assert.equal before (fingerprint root))

          t "cli: refusals exit 1 and leave the store unchanged" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [])).ExitCode
              let before = fingerprint root

              for members, id in
                  [ [ "TASK-B" ], "GROUP-FIXTURE-CORE-001"
                    [ "NOPE-9" ], "GROUP-FIXTURE-CORE-002"
                    [ "TASK-X" ], "GROUP-FIXTURE-CORE-003"
                    [ "TASK-DONE" ], "GROUP-FIXTURE-CORE-004" ] do
                  let result = PraxisCli.run root executor (createArguments id members [ "--json" ])
                  Assert.equal 1 result.ExitCode
                  Assert.equal "group-rejected" (text (result.Json["kind"]))

              Assert.equal before (fingerprint root))

          t "cli: invalid arguments exit 2" (fun () ->
              let root = fixture ()
              Assert.equal 2 (PraxisCli.run root executor [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [ "--kind"; "nonsense" ])).ExitCode
              Assert.equal 2 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [ "stray" ])).ExitCode
              Assert.isTrue (not (File.Exists(storePath root))) "nothing recorded")

          t "cli: plan groups reports the stored declaration" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [])).ExitCode
              let groups = PraxisCli.run root None [ "plan"; "groups"; "--json" ]
              Assert.equal 0 groups.ExitCode
              let group = groups.Json["groups"].AsArray() |> Seq.find (fun group -> text (group["id"]) = "GROUP-FIXTURE-CORE-001")
              Assert.equal "human-declared" (text (group["origin"])))

          t "cli: validate reports a stored group with an unknown member" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [])).ExitCode
              let clean = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.isTrue (not (clean.Output.Contains ".ros/work/groups.json")) "a valid store has no findings"
              File.WriteAllText(storePath root, File.ReadAllText(storePath root).Replace("\"TASK-A\"", "\"GONE-1\""))
              let result = PraxisCli.run root None [ "validate"; "--json" ]
              Assert.equal 1 result.ExitCode
              Assert.isTrue (result.Output.Contains ".ros/work/groups.json" && result.Output.Contains "GONE-1") "the unknown member is reported") ]
        @ showTests
