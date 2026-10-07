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
                DeclaredBy = Some actor
                Membership = []
                Checkpoints = [] } ] }

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

    // ---- work group add (PRAXIS-GROUP-03) ---------------------------------------

    /// Everything executes in "praxis" except E-1 (echelon) and X-1 (an
    /// undeclared external repository).
    let private locate id =
        match id with
        | "E-1" -> ExecutionLocation.Repository "echelon"
        | "X-1" -> ExecutionLocation.UnknownExternal
        | _ -> ExecutionLocation.Repository "praxis"

    let private addStates = GroupDeclaration.memberStates (queue @ [ queued "E-1" "ready" "2026-09-01T00:00:00Z"; queued "X-1" "ready" "2026-09-01T00:00:00Z" ]) liveItems

    let private addTo store groupId memberId =
        GroupDeclaration.addMember store addStates locate "2026-10-07T01:00:00.000Z" (Some actor) groupId memberId

    let private baseStore = recorded GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1" ])

    let private addRejected store groupId memberId =
        match addTo store groupId memberId with
        | Ok _ -> failwith "expected a rejection"
        | Error rejections -> rejections

    let private addTests =
        [ t "add: appends the member and records who added it, leaving the declaration otherwise unchanged" (fun () ->
              match addTo baseStore "GROUP-PRAXIS-A-001" "L-1" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, change) ->
                  Assert.equal (declared "GROUP-PRAXIS-A-001" [ "A-1"; "L-1" ]) group.Declaration
                  Assert.equal
                      { Member = "L-1"
                        Operation = MembershipOperation.Added
                        OccurredAt = "2026-10-07T01:00:00.000Z"
                        Actor = Some actor }
                      change
                  Assert.equal [ change ] group.Membership
                  Assert.equal "2026-10-07T00:00:00.000Z" group.DeclaredAt
                  Assert.equal (Some group) (GroupStore.tryFind "GROUP-PRAXIS-A-001" store)
                  Assert.equal 1 store.Groups.Length)

          t "add: refuses unknown and terminal items" (fun () ->
              Assert.equal [ GroupRejection.UnknownMember "NOPE-1" ] (addRejected baseStore "GROUP-PRAXIS-A-001" "NOPE-1")
              Assert.equal [ GroupRejection.TerminalMember("A-3", "abandoned") ] (addRejected baseStore "GROUP-PRAXIS-A-001" "A-3")
              Assert.equal [ GroupRejection.TerminalMember("A-4", "complete") ] (addRejected baseStore "GROUP-PRAXIS-A-001" "A-4"))

          t "add: refuses an item already present and an unknown group" (fun () ->
              Assert.equal [ GroupRejection.AlreadyMember("A-1", "GROUP-PRAXIS-A-001") ] (addRejected baseStore "GROUP-PRAXIS-A-001" "A-1")
              Assert.equal [ GroupRejection.UnknownGroup "GROUP-PRAXIS-NONE-001" ] (addRejected baseStore "GROUP-PRAXIS-NONE-001" "A-2"))

          t "add: refuses another repository's item unless the group is cross-repository" (fun () ->
              Assert.equal [ GroupRejection.RepositoryMismatch("E-1", "echelon", "praxis") ] (addRejected baseStore "GROUP-PRAXIS-A-001" "E-1")
              Assert.equal [ GroupRejection.RepositoryMismatch("X-1", "unknown external repository", "praxis") ] (addRejected baseStore "GROUP-PRAXIS-A-001" "X-1")
              // Undeclared group repository: the members' own repository is the group's.
              let undeclared = recorded GroupStore.empty { declared "GROUP-PRAXIS-B-001" [ "A-1" ] with ExecutionRepository = None }
              Assert.equal [ GroupRejection.RepositoryMismatch("E-1", "echelon", "praxis") ] (addRejected undeclared "GROUP-PRAXIS-B-001" "E-1")
              Assert.isTrue (addTo undeclared "GROUP-PRAXIS-B-001" "A-2" |> Result.isOk) "same repository"
              let cross = recorded GroupStore.empty { declared "GROUP-ECHELON-A-001" [ "A-1" ] with CrossRepository = true; ExecutionRepository = Some "echelon" }
              Assert.isTrue (addTo cross "GROUP-ECHELON-A-001" "E-1" |> Result.isOk) "cross-repository admits echelon"
              Assert.isTrue (addTo cross "GROUP-ECHELON-A-001" "X-1" |> Result.isOk) "cross-repository admits external")

          t "add: the membership history round-trips and the planner still reads the plain declaration" (fun () ->
              match addTo baseStore "GROUP-PRAXIS-A-001" "A-2" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, _, _) ->
                  let json = PlanningJson.renderGroupStore store
                  Assert.equal (Ok store) (PlanningJson.parseGroupStore json)
                  Assert.equal [ declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2" ] ] (GroupStore.declarations store)
                  // A store written before membership history existed still parses.
                  let legacy = json.Replace("\"membership\"", "\"ignoredMembership\"")
                  Assert.equal (Ok []) (PlanningJson.parseGroupStore legacy |> Result.map (fun parsed -> (Assert.single parsed.Groups).Membership))
                  Assert.empty (GroupDeclaration.findings store addStates))

          t "add: validate reports membership history that contradicts the members" (fun () ->
              match addTo baseStore "GROUP-PRAXIS-A-001" "A-2" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, _) ->
                  let broken = GroupStore.replace { group with Declaration = { group.Declaration with Members = [ "A-1" ] } } store
                  let finding = Assert.single (GroupDeclaration.findings broken addStates)
                  Assert.equal "groups[GROUP-PRAXIS-A-001].membership" finding.Field)

          t "add operation: repositories come from the planner configuration and only the store is written" (fun () ->
              let store = ref baseStore
              let writes = ref 0
              let addQueue = queue @ [ queued "E-1" "ready" "2026-09-01T00:00:00Z" ]

              let groups: WorkGroupPort =
                  { ReadStore = fun () -> Ok store.Value
                    Queue = fun () -> Ok addQueue
                    Live = fun () -> Ok liveItems
                    WriteStore =
                      fun updated ->
                          writes.Value <- writes.Value + 1
                          store.Value <- updated
                          Ok() }

              let planning: PlanningReadPort =
                  { Repository = fun () -> { Name = "praxis"; Commit = Some "1111111111111111111111111111111111111111"; Branch = Some "main" }
                    Queue = fun () -> Ok addQueue
                    Live = fun () -> Ok liveItems
                    Executions = fun () -> history
                    RepositoryObservations = fun _ -> []
                    SuppliedObservations = fun () -> Ok []
                    Configuration = fun () -> Ok { PlannerConfiguration.defaults with Grouping = { GroupingConfiguration.defaults with ExecutionRepositories = [ "E-1", "echelon" ] } }
                    DeclaredGroups = fun () -> Ok(GroupStore.declarations store.Value) }

              let run memberId dryRun =
                  let request: GroupAddRequest =
                      { GroupId = "GROUP-PRAXIS-A-001"
                        Member = memberId
                        OccurredAt = "2026-10-07T01:00:00.000Z"
                        Actor = Some actor
                        DryRun = dryRun }

                  match WorkGroupOperations.add groups planning "2026-10-07T01:00:00.000Z" "test" request with
                  | Ok outcome -> outcome
                  | Error message -> failwith message

              match run "E-1" false with
              | GroupAddOutcome.Rejected [ GroupRejection.RepositoryMismatch("E-1", "echelon", "praxis") ] -> ()
              | outcome -> failwith $"unexpected outcome {outcome}"

              match run "A-2" true with
              | GroupAddOutcome.Planned(_, added) -> Assert.equal ("captured", "praxis") (added.State, added.ExecutionRepository)
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 0 writes.Value

              match run "A-2" false with
              | GroupAddOutcome.Recorded(group, _) -> Assert.equal [ "A-1"; "A-2" ] group.Declaration.Members
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 1 writes.Value
              Assert.equal [ "A-1"; "A-2" ] (GroupStore.tryFind "GROUP-PRAXIS-A-001" store.Value).Value.Declaration.Members)

          t "cli: add records the member with provenance and changes no member's state" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [])).ExitCode
              let before = memberFiles root
              let addArguments = [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--occurred-at"; PraxisCli.now (); "--json" ]
              let result = PraxisCli.run root executor addArguments
              Assert.equal 0 result.ExitCode
              Assert.equal "member-added" (text (result.Json["kind"]))
              Assert.isTrue (result.Json["recorded"].GetValue<bool>()) "recorded"
              Assert.equal "group-fixture" (text (result.Json.["member"].["executionRepository"]))
              Assert.equal "ready" (text (result.Json.["member"].["state"]))
              Assert.equal "test/agent" (text (result.Json.["change"].["actor"].["id"]))
              Assert.equal before (memberFiles root)

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok store ->
                  let group = Assert.single store.Groups
                  Assert.equal [ "TASK-A"; "TASK-B" ] group.Declaration.Members
                  let change = Assert.single group.Membership
                  Assert.equal ("TASK-B", MembershipOperation.Added) (change.Member, change.Operation)
                  Assert.equal (Some "test/agent") (change.Actor |> Option.map (fun recordedActor -> recordedActor.Id))
              | Error message -> failwith message

              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-CORE-001"; "--json" ]
              Assert.equal [ "TASK-A"; "TASK-B" ] (shown.Json["members"].AsArray() |> Seq.map (fun entry -> text (entry["id"])) |> List.ofSeq)
              Assert.isTrue (not ((PraxisCli.run root None [ "validate"; "--json" ]).Output.Contains ".ros/work/groups.json")) "no group finding")

          t "cli: add refusals and --dry-run write nothing; argument errors exit 2" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A" ] [])).ExitCode
              let configuration = Path.Combine(Path.GetTempPath(), $"praxis-group-config-{Guid.NewGuid():N}.json")
              File.WriteAllText(configuration, """{"grouping":{"executionRepositories":{"TASK-B":"echelon"}}}""")
              let before = fingerprint root
              let add (extra: string list) = PraxisCli.run root executor ([ "work"; "group"; "add"; "--occurred-at"; PraxisCli.now () ] @ extra)

              for groupId, memberId in
                  [ "GROUP-FIXTURE-CORE-001", "TASK-A"
                    "GROUP-FIXTURE-CORE-001", "NOPE-9"
                    "GROUP-FIXTURE-CORE-001", "TASK-X"
                    "GROUP-FIXTURE-CORE-001", "TASK-DONE"
                    "GROUP-FIXTURE-NONE-001", "TASK-B" ] do
                  let result = add [ "--id"; groupId; "--member"; memberId; "--json" ]
                  Assert.equal 1 result.ExitCode
                  Assert.equal "member-rejected" (text (result.Json["kind"]))

              let mismatch = add [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--config"; configuration; "--json" ]
              Assert.equal 1 mismatch.ExitCode
              Assert.isTrue (mismatch.Output.Contains "echelon") mismatch.Output

              let dryRun = add [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--dry-run"; "--json" ]
              Assert.equal 0 dryRun.ExitCode
              Assert.isTrue (not (dryRun.Json["recorded"].GetValue<bool>())) "not recorded"
              Assert.equal 2 (add [ "--id"; "GROUP-FIXTURE-CORE-001" ]).ExitCode
              Assert.equal 2 (add [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (add [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--cross-repository" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root executor [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal before (fingerprint root)) ]

    // ---- work group remove (PRAXIS-GROUP-04) ------------------------------------

    let private removeFrom store groupId memberId =
        GroupDeclaration.removeMember store "2026-10-07T02:00:00.000Z" (Some actor) groupId memberId

    let private twoMemberStore = recorded GroupStore.empty (declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-2"; "L-1" ])

    let private removeRejected store groupId memberId =
        match removeFrom store groupId memberId with
        | Ok _ -> failwith "expected a rejection"
        | Error rejections -> rejections

    let private removeTests =
        [ t "remove: drops the member, keeps the others in order and records who removed it" (fun () ->
              match removeFrom twoMemberStore "GROUP-PRAXIS-A-001" "A-2" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, change) ->
                  Assert.equal (declared "GROUP-PRAXIS-A-001" [ "A-1"; "L-1" ]) group.Declaration
                  Assert.equal
                      { Member = "A-2"
                        Operation = MembershipOperation.Removed
                        OccurredAt = "2026-10-07T02:00:00.000Z"
                        Actor = Some actor }
                      change
                  Assert.equal [ change ] group.Membership
                  Assert.equal "2026-10-07T00:00:00.000Z" group.DeclaredAt
                  Assert.equal (Some actor) group.DeclaredBy
                  Assert.equal (Some group) (GroupStore.tryFind "GROUP-PRAXIS-A-001" store))

          t "remove: refuses non-members and unknown groups" (fun () ->
              Assert.equal [ GroupRejection.NotMember("A-4", "GROUP-PRAXIS-A-001") ] (removeRejected twoMemberStore "GROUP-PRAXIS-A-001" "A-4")
              Assert.equal [ GroupRejection.NotMember("NOPE-1", "GROUP-PRAXIS-A-001") ] (removeRejected twoMemberStore "GROUP-PRAXIS-A-001" "NOPE-1")
              Assert.equal [ GroupRejection.UnknownGroup "GROUP-PRAXIS-NONE-001" ] (removeRejected twoMemberStore "GROUP-PRAXIS-NONE-001" "A-1"))

          t "remove: refuses the last member" (fun () ->
              Assert.equal [ GroupRejection.LastMember("A-1", "GROUP-PRAXIS-A-001") ] (removeRejected baseStore "GROUP-PRAXIS-A-001" "A-1")
              Assert.isTrue ((GroupRejection.message (GroupRejection.LastMember("A-1", "GROUP-PRAXIS-A-001"))).Contains "last member") "message names the rule")

          t "remove: terminal and unknown members may leave; member state is not an input" (fun () ->
              let stored =
                  { Declaration = declared "GROUP-PRAXIS-A-001" [ "A-1"; "A-4"; "GONE-1" ]
                    DeclaredAt = "2026-10-07T00:00:00.000Z"
                    DeclaredBy = None
                    Membership = []
                    Checkpoints = [] }

              let store = { Groups = [ stored ] }
              Assert.isTrue (removeFrom store "GROUP-PRAXIS-A-001" "A-4" |> Result.isOk) "a member that became complete"
              Assert.isTrue (removeFrom store "GROUP-PRAXIS-A-001" "GONE-1" |> Result.isOk) "a member unknown to this repository")

          t "remove: history round-trips, a re-added member is valid and validate reports a contradiction" (fun () ->
              match removeFrom twoMemberStore "GROUP-PRAXIS-A-001" "A-2" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, _) ->
                  Assert.equal (Ok store) (PlanningJson.parseGroupStore (PlanningJson.renderGroupStore store))
                  Assert.equal [ declared "GROUP-PRAXIS-A-001" [ "A-1"; "L-1" ] ] (GroupStore.declarations store)
                  Assert.empty (GroupDeclaration.findings store addStates)

                  match addTo store "GROUP-PRAXIS-A-001" "A-2" with
                  | Error rejections -> failwith $"unexpected rejection: {rejections}"
                  | Ok(readded, regrouped, _) ->
                      Assert.equal [ MembershipOperation.Removed; MembershipOperation.Added ] (regrouped.Membership |> List.map (fun change -> change.Operation))
                      Assert.empty (GroupDeclaration.findings readded addStates)

                  let broken = GroupStore.replace { group with Declaration = { group.Declaration with Members = [ "A-1"; "L-1"; "A-2" ] } } store
                  let finding = Assert.single (GroupDeclaration.findings broken addStates)
                  Assert.equal "groups[GROUP-PRAXIS-A-001].membership" finding.Field
                  Assert.isTrue (finding.Message.Contains "removed") finding.Message)

          t "remove operation: reports the item's own state and writes only the store, never on dry run" (fun () ->
              let store = ref twoMemberStore
              let writes = ref 0

              let groups: WorkGroupPort =
                  { ReadStore = fun () -> Ok store.Value
                    Queue = fun () -> Ok queue
                    Live = fun () -> Ok liveItems
                    WriteStore =
                      fun updated ->
                          writes.Value <- writes.Value + 1
                          store.Value <- updated
                          Ok() }

              let run memberId dryRun =
                  let request: GroupRemoveRequest =
                      { GroupId = "GROUP-PRAXIS-A-001"
                        Member = memberId
                        OccurredAt = "2026-10-07T02:00:00.000Z"
                        Actor = Some actor
                        DryRun = dryRun }

                  match WorkGroupOperations.remove groups request with
                  | Ok outcome -> outcome
                  | Error message -> failwith message

              match run "A-4" false with
              | GroupRemoveOutcome.Rejected [ GroupRejection.NotMember("A-4", "GROUP-PRAXIS-A-001") ] -> ()
              | outcome -> failwith $"unexpected outcome {outcome}"

              match run "L-1" true with
              | GroupRemoveOutcome.Planned(group, removed) ->
                  Assert.equal "active" removed.State
                  Assert.equal [ "A-1"; "A-2" ] group.Declaration.Members
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 0 writes.Value

              match run "A-2" false with
              | GroupRemoveOutcome.Recorded(group, removed) ->
                  Assert.equal "captured" removed.State
                  Assert.equal [ "A-1"; "L-1" ] group.Declaration.Members
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 1 writes.Value
              Assert.equal [ "A-1"; "L-1" ] (GroupStore.tryFind "GROUP-PRAXIS-A-001" store.Value).Value.Declaration.Members)

          t "cli: remove records provenance and changes no member's state" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [])).ExitCode
              let before = memberFiles root
              let removeArguments = [ "work"; "group"; "remove"; "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--occurred-at"; PraxisCli.now (); "--json" ]
              let result = PraxisCli.run root executor removeArguments
              Assert.equal 0 result.ExitCode
              Assert.equal "member-removed" (text (result.Json["kind"]))
              Assert.isTrue (result.Json["recorded"].GetValue<bool>()) "recorded"
              Assert.equal "ready" (text (result.Json.["member"].["state"]))
              Assert.equal "removed" (text (result.Json.["change"].["operation"]))
              Assert.equal "test/agent" (text (result.Json.["change"].["actor"].["id"]))
              Assert.equal before (memberFiles root)

              match PlanningJson.parseGroupStore (File.ReadAllText(storePath root)) with
              | Ok store ->
                  let group = Assert.single store.Groups
                  Assert.equal [ "TASK-A" ] group.Declaration.Members
                  let change = Assert.single group.Membership
                  Assert.equal ("TASK-B", MembershipOperation.Removed) (change.Member, change.Operation)
                  Assert.equal (Some "test/agent") (change.Actor |> Option.map (fun recordedActor -> recordedActor.Id))
              | Error message -> failwith message

              let shown = PraxisCli.run root None [ "work"; "group"; "show"; "GROUP-FIXTURE-CORE-001"; "--json" ]
              Assert.equal [ "TASK-A" ] (shown.Json["members"].AsArray() |> Seq.map (fun entry -> text (entry["id"])) |> List.ofSeq)
              Assert.isTrue (not ((PraxisCli.run root None [ "validate"; "--json" ]).Output.Contains ".ros/work/groups.json")) "no group finding")

          t "cli: remove refusals and --dry-run write nothing; argument errors exit 2" (fun () ->
              let root = fixture ()
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-CORE-001" [ "TASK-A"; "TASK-B" ] [])).ExitCode
              Assert.equal 0 (PraxisCli.run root executor (createArguments "GROUP-FIXTURE-SOLO-001" [ "TASK-A" ] [])).ExitCode
              let before = fingerprint root
              let remove (extra: string list) = PraxisCli.run root executor ([ "work"; "group"; "remove"; "--occurred-at"; PraxisCli.now () ] @ extra)

              for groupId, memberId, kind in
                  [ "GROUP-FIXTURE-CORE-001", "TASK-X", "member-removal-rejected"
                    "GROUP-FIXTURE-CORE-001", "NOPE-9", "member-removal-rejected"
                    "GROUP-FIXTURE-NONE-001", "TASK-A", "member-removal-rejected"
                    "GROUP-FIXTURE-SOLO-001", "TASK-A", "member-removal-rejected" ] do
                  let result = remove [ "--id"; groupId; "--member"; memberId; "--json" ]
                  Assert.equal 1 result.ExitCode
                  Assert.equal kind (text (result.Json["kind"]))

              let last = remove [ "--id"; "GROUP-FIXTURE-SOLO-001"; "--member"; "TASK-A" ]
              Assert.isTrue (last.Error.Contains "last member") last.Error

              let dryRun = remove [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--dry-run"; "--json" ]
              Assert.equal 0 dryRun.ExitCode
              Assert.isTrue (not (dryRun.Json["recorded"].GetValue<bool>())) "not recorded"
              Assert.equal [ "TASK-A" ] (dryRun.Json.["group"].["members"].AsArray() |> Seq.map text |> List.ofSeq)
              Assert.equal 2 (remove [ "--id"; "GROUP-FIXTURE-CORE-001" ]).ExitCode
              Assert.equal 2 (remove [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--member"; "TASK-A" ]).ExitCode
              Assert.equal 2 (remove [ "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B"; "--config"; "x.json" ]).ExitCode
              Assert.equal 2 (PraxisCli.run root executor [ "work"; "group"; "remove"; "--id"; "GROUP-FIXTURE-CORE-001"; "--member"; "TASK-B" ]).ExitCode
              Assert.equal before (fingerprint root)) ]

    // ---- work group checkpoint (PRAXIS-GROUP-05) -------------------------------

    let private sha (digit: char) =
        Ros.Domain.Git.CommitId.tryParse (String(digit, 40)) |> Option.get

    let private verifiedAt (commit: char) : GitDurableLocation =
        { Repository = "praxis"
          Branch = "feature/x"
          LocalCommit = sha commit
          Remote = { Name = "origin"; Url = Some "https://example.test/praxis.git" }
          RemoteBranch = "feature/x"
          RemoteCommit = sha commit }

    /// A-1 ready, A-2 captured, L-1 active with its own checkpoint, A-4
    /// complete, A-3 abandoned, GONE-1 unknown.
    let private checkpointStore =
        { Groups =
            [ { Declaration = declared "GROUP-PRAXIS-CP-001" [ "L-1"; "A-1"; "A-4"; "A-3"; "GONE-1" ]
                DeclaredAt = "2026-10-07T00:00:00.000Z"
                DeclaredBy = Some actor
                Membership = []
                Checkpoints = [] } ] }

    let private checkpointLive =
        [ live "A-4" LiveWorkState.Complete
          { live "L-1" LiveWorkState.Active with Checkpoint = Some(checkpoint "feature/x" (String('c', 40)) "write the next slice") } ]

    let private ownCheckpoint: MemberCheckpointReference =
        { CheckpointId = "cp-cccccc"
          ExecutionId = "EXE-1"
          Commit = String('c', 40)
          RecordedAt = "2026-09-20T00:00:00.000Z" }

    let private recordGroupCheckpoint store groupId =
        GroupDeclaration.recordCheckpoint
            store
            (GroupDeclaration.memberStates queue checkpointLive)
            (Map.ofList [ "L-1", ownCheckpoint ])
            "2026-10-07T03:00:00.000Z"
            (Some actor)
            "  shared store contract settled  "
            "implement A-1"
            [ "one group store for all group commands" ]
            (verifiedAt 'd')
            groupId

    let private checkpointPorts (store: GroupStore) =
        let current = ref store
        let writes = ref 0

        let port: WorkGroupPort =
            { ReadStore = fun () -> Ok current.Value
              Queue = fun () -> Ok queue
              Live = fun () -> Ok checkpointLive
              WriteStore =
                fun updated ->
                    writes.Value <- writes.Value + 1
                    current.Value <- updated
                    Ok() }

        port, current, writes

    let private checkpointRequest groupId dryRun : GroupCheckpointRequest =
        { GroupId = groupId
          Repository = "praxis"
          Summary = "shared store contract settled"
          NextAction = "implement A-1"
          Decisions = [ "one group store for all group commands" ]
          OccurredAt = "2026-10-07T03:00:00.000Z"
          Actor = Some actor
          DryRun = dryRun }

    let private checkpointTests =
        [ t "checkpoint: partitions members, references their own checkpoints and appends to the group" (fun () ->
              match recordGroupCheckpoint checkpointStore "GROUP-PRAXIS-CP-001" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, recorded) ->
                  Assert.equal [ recorded ] group.Checkpoints
                  Assert.equal (Some group) (GroupStore.tryFind "GROUP-PRAXIS-CP-001" store)
                  Assert.equal "shared store contract settled" recorded.Summary
                  Assert.equal (verifiedAt 'd') recorded.Location
                  Assert.equal [ "L-1" ] (GroupDeclaration.partitionOf GroupMemberPartition.Active recorded)
                  Assert.equal [ "A-4" ] (GroupDeclaration.partitionOf GroupMemberPartition.Completed recorded)
                  Assert.equal [ "A-3" ] (GroupDeclaration.partitionOf GroupMemberPartition.Abandoned recorded)
                  Assert.equal [ "A-1"; "GONE-1" ] (GroupDeclaration.partitionOf GroupMemberPartition.Remaining recorded)
                  Assert.equal [ "active"; "ready"; "complete"; "abandoned"; "unknown" ] (recorded.Members |> List.map (fun entry -> entry.State))
                  Assert.equal [ Some ownCheckpoint; None; None; None; None ] (recorded.Members |> List.map (fun entry -> entry.Checkpoint))
                  // The declaration (what the planner reads) is untouched.
                  Assert.equal (GroupStore.declarations checkpointStore) (GroupStore.declarations store))

          t "checkpoint: an unknown group is refused; earlier group checkpoints are never rewritten" (fun () ->
              match recordGroupCheckpoint checkpointStore "GROUP-PRAXIS-NONE-001" with
              | Error rejections -> Assert.equal [ GroupRejection.UnknownGroup "GROUP-PRAXIS-NONE-001" ] rejections
              | Ok _ -> failwith "expected a rejection"

              match recordGroupCheckpoint checkpointStore "GROUP-PRAXIS-CP-001" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, _, first) ->
                  match recordGroupCheckpoint store "GROUP-PRAXIS-CP-001" with
                  | Error rejections -> failwith $"unexpected rejection: {rejections}"
                  | Ok(_, group, second) -> Assert.equal [ first; second ] group.Checkpoints)

          t "checkpoint: the store round-trips with group checkpoints and a store without them still parses" (fun () ->
              match recordGroupCheckpoint checkpointStore "GROUP-PRAXIS-CP-001" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, _, _) ->
                  let json = PlanningJson.renderGroupStore store
                  Assert.equal (Ok store) (PlanningJson.parseGroupStore json)
                  Assert.isTrue (json.Contains "\"remaining\"" && json.Contains "\"git-remote-observation\"") "derived partitions and verification are rendered"
                  let withoutCheckpoints = (JsonNode.Parse json).AsObject()
                  (withoutCheckpoints["groups"].[0].AsObject()).Remove "checkpoints" |> ignore
                  Assert.equal (Ok checkpointStore) (PlanningJson.parseGroupStore (withoutCheckpoints.ToJsonString())))

          t "checkpoint: validate accepts a recorded group checkpoint and reports a tampered one" (fun () ->
              let states = GroupDeclaration.memberStates queue checkpointLive

              match recordGroupCheckpoint checkpointStore "GROUP-PRAXIS-CP-001" with
              | Error rejections -> failwith $"unexpected rejection: {rejections}"
              | Ok(store, group, recorded) ->
                  let known = GroupDeclaration.findings store states |> List.filter (fun finding -> finding.Field.EndsWith "checkpoints")
                  Assert.empty known

                  let tampered =
                      { recorded with
                          Summary = " "
                          Location = { recorded.Location with RemoteCommit = sha 'e' } }

                  let broken = GroupStore.replace { group with Checkpoints = [ tampered ] } store
                  let findings = GroupDeclaration.findings broken states |> List.filter (fun finding -> finding.Field = "groups[GROUP-PRAXIS-CP-001].checkpoints")
                  Assert.equal 2 findings.Length
                  Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "blank summary")) "blank summary"
                  Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "remoteCommit")) "commit mismatch")

          t "checkpoint: durability uses work checkpoint's own Git rules without a work item or execution" (fun () ->
              let candidate: CheckpointCandidate =
                  { WorkItemId = "GROUP-PRAXIS-CP-001"
                    Repository = "praxis"
                    Summary = "settled"
                    NextAction = "next"
                    StepId = None
                    OccurredAt = "2026-10-07T03:00:00.000Z" }

              let head = sha 'd'
              let origin: Ros.Domain.Git.RemoteIdentity = { Name = "origin"; Url = None }

              let observed: CheckpointObservations =
                  { WorkItemState = None
                    Execution = ExecutionObservation.NoneActive
                    Head = Ros.Domain.Git.GitRead.Observed(Ros.Domain.Git.HeadState.OnBranch("feature/x", head))
                    Upstream = Some(Ros.Domain.Git.GitRead.Observed(Ros.Domain.Git.UpstreamState.Tracking(origin, "feature/x")))
                    RemoteBranch = Some(Ros.Domain.Git.RemoteBranchObservation.At head)
                    LocalToRemote = None
                    WorkingTree = Ros.Domain.Git.GitStatusObservation.Clean
                    PathFilter = PathFilterConfig.defaultConfig
                    BaselineDirtyPaths = [] }

              match CheckpointVerification.durableLocation candidate observed with
              | Ok location -> Assert.equal head location.RemoteCommit
              | Error rejections -> failwith $"unexpected rejection: {rejections}"

              let codes observations candidate =
                  match CheckpointVerification.durableLocation candidate observations with
                  | Ok _ -> failwith "expected a rejection"
                  | Error rejections -> rejections |> List.map CheckpointRejection.code

              let ahead =
                  { observed with
                      RemoteBranch = Some(Ros.Domain.Git.RemoteBranchObservation.At(sha 'e'))
                      LocalToRemote = Some(Ros.Domain.Git.CommitRelationObservation.Related(Ros.Domain.Git.CommitRelation.Ahead 1)) }

              Assert.equal [ "local-ahead" ] (codes ahead candidate)
              Assert.equal [ "blank-summary"; "blank-next-action" ] (codes observed { candidate with Summary = " "; NextAction = "" })
              // work checkpoint itself would refuse the same Git state identically.
              match CheckpointVerification.verify candidate { ahead with WorkItemState = Some LiveWorkState.Active; Execution = ExecutionObservation.Resolved("EXE-1", []) } with
              | Error rejections -> Assert.equal [ "local-ahead" ] (rejections |> List.map CheckpointRejection.code)
              | Ok _ -> failwith "expected a rejection")

          t "checkpoint operation: unknown groups skip Git, refusals and dry runs write nothing" (fun () ->
              let port, current, writes = checkpointPorts checkpointStore
              let verified = ref 0

              let verify (_: CheckpointCandidate) =
                  verified.Value <- verified.Value + 1
                  Ok(verifiedAt 'd')

              let refuse (_: CheckpointCandidate) = Error [ CheckpointRejection.UncommittedChanges [ "src/x.fs" ] ]

              let run verify request =
                  match WorkGroupOperations.checkpoint port verify request with
                  | Ok outcome -> outcome
                  | Error message -> failwith message

              match run verify (checkpointRequest "GROUP-PRAXIS-NONE-001" false) with
              | GroupCheckpointOutcome.Rejected [ GroupRejection.UnknownGroup "GROUP-PRAXIS-NONE-001" ] -> ()
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 0 verified.Value

              match run refuse (checkpointRequest "GROUP-PRAXIS-CP-001" false) with
              | GroupCheckpointOutcome.NotDurable [ CheckpointRejection.UncommittedChanges _ ] -> ()
              | outcome -> failwith $"unexpected outcome {outcome}"

              match run verify (checkpointRequest "GROUP-PRAXIS-CP-001" true) with
              | GroupCheckpointOutcome.Planned(group, _) -> Assert.equal 1 group.Checkpoints.Length
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 0 writes.Value
              Assert.equal checkpointStore current.Value

              match run verify (checkpointRequest "GROUP-PRAXIS-CP-001" false) with
              | GroupCheckpointOutcome.Recorded(group, recorded) ->
                  Assert.equal [ recorded ] group.Checkpoints
                  Assert.equal (Some ownCheckpoint) (recorded.Members |> List.find (fun entry -> entry.WorkItemId = "L-1")).Checkpoint
              | outcome -> failwith $"unexpected outcome {outcome}"

              Assert.equal 1 writes.Value
              Assert.equal 2 verified.Value) ]

    let private start clone (id: string) =
        PraxisCli.run clone executor [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; PraxisCli.now () ] |> PraxisCli.ok |> ignore

    let private groupCheckpoint clone (extra: string list) =
        PraxisCli.run
            clone
            executor
            ([ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CP-001"; "--occurred-at"; PraxisCli.now (); "--summary"; "store settled"; "--next-action"; "implement FEAT-2" ]
             @ extra)

    let private withPushedRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "group-checkpoint"

        try
            let _, clone = PraxisCli.installedRepository parent "clone"
            test clone
        finally
            GitFixture.cleanup parent

    let private rejectionCodes (result: PraxisCli.Result) =
        result.Json["rejections"].AsArray() |> Seq.map (fun node -> text node["code"]) |> List.ofSeq

    let private checkpointCliTests =
        [ t "cli: checkpoint records a verified group checkpoint that references members' own checkpoints" (fun () ->
              withPushedRepository (fun clone ->
                  start clone "FEAT-1"
                  start clone "FEAT-2"
                  GitFixture.write clone "src/one.txt" "one\n"
                  PraxisCli.pushAll clone "FEAT-1 work" |> ignore

                  let own =
                      PraxisCli.run clone executor [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; PraxisCli.now (); "--summary"; "slice"; "--next-action"; "next"; "--json" ]
                      |> PraxisCli.ok

                  let ownId = text own.Json["checkpoint"].["id"]
                  PraxisCli.run clone executor (createArguments "GROUP-FIXTURE-CP-001" [ "FEAT-1"; "FEAT-2" ] [ "--architecture-note"; "one store" ]) |> PraxisCli.ok |> ignore
                  let head = PraxisCli.pushAll clone "Praxis state"
                  let memberState = memberFiles clone
                  let events = File.ReadAllBytes(Path.Combine(clone, ".ros", "events", "events.jsonl"))

                  let result = groupCheckpoint clone [ "--decision"; "members keep their own checkpoints"; "--json" ] |> PraxisCli.ok
                  Assert.equal "group-checkpointed" (text result.Json["kind"])
                  Assert.isTrue (result.Json["recorded"].GetValue<bool>()) "recorded"
                  let recorded = result.Json["checkpoint"]
                  Assert.equal head (text recorded["commit"])
                  Assert.equal head (text recorded["remoteCommit"])
                  Assert.equal "feature/x" (text recorded["branch"])
                  Assert.equal "verified" (text recorded["verification"].["status"])
                  Assert.equal "test/agent" (text recorded["actor"].["id"])
                  Assert.equal [ "members keep their own checkpoints" ] (recorded["decisions"].AsArray() |> Seq.map text |> List.ofSeq)
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] (recorded["active"].AsArray() |> Seq.map text |> List.ofSeq)
                  Assert.equal ownId (text recorded["members"].[0].["checkpoint"].["checkpointId"])
                  Assert.isTrue (isNull recorded["members"].[1].["checkpoint"]) "FEAT-2 has no checkpoint of its own"
                  Assert.isTrue (isNull recorded["paths"]) "a group checkpoint claims no paths"

                  // Members' own state, checkpoints and events are untouched.
                  Assert.equal memberState (memberFiles clone)
                  Assert.equal events (File.ReadAllBytes(Path.Combine(clone, ".ros", "events", "events.jsonl")))
                  let context = PraxisCli.run clone None [ "work"; "context"; "FEAT-1" ] |> PraxisCli.ok
                  Assert.equal ownId (text context.Json["continuity"].[0].["checkpoint"].["id"])

                  let shown = PraxisCli.run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-CP-001" ] |> PraxisCli.ok
                  Assert.isTrue (shown.Output.Contains "store settled" && shown.Output.Contains "decision: members keep their own checkpoints") shown.Output
                  PraxisCli.run clone None [ "validate" ] |> PraxisCli.ok |> ignore))

          t "cli: checkpoint refuses unknown groups, unpushed and dirty work; argument errors exit 2" (fun () ->
              withPushedRepository (fun clone ->
                  start clone "FEAT-1"
                  PraxisCli.run clone executor (createArguments "GROUP-FIXTURE-CP-001" [ "FEAT-1" ] []) |> PraxisCli.ok |> ignore
                  PraxisCli.pushAll clone "Praxis state" |> ignore
                  let unknown = PraxisCli.run clone executor [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-NONE-001"; "--occurred-at"; PraxisCli.now (); "--summary"; "s"; "--next-action"; "n"; "--json" ]
                  Assert.equal 1 unknown.ExitCode
                  Assert.equal "group-checkpoint-rejected" (text unknown.Json["kind"])
                  GitFixture.write clone "src/one.txt" "one\n"
                  GitFixture.commitAll clone "not pushed" |> ignore
                  let ahead = groupCheckpoint clone [ "--json" ]
                  Assert.equal 1 ahead.ExitCode
                  Assert.equal [ "local-ahead" ] (rejectionCodes ahead)
                  GitFixture.git clone [ "push"; "-q" ] |> ignore
                  GitFixture.write clone "src/one.txt" "two\n"
                  Assert.equal [ "uncommitted-changes" ] (rejectionCodes (groupCheckpoint clone [ "--json" ]))
                  GitFixture.git clone [ "checkout"; "--"; "src/one.txt" ] |> ignore
                  let before = File.ReadAllText(storePath clone)

                  let blank =
                      PraxisCli.run clone executor [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CP-001"; "--occurred-at"; PraxisCli.now (); "--summary"; "  "; "--next-action"; "n"; "--json" ]

                  Assert.equal 2 blank.ExitCode
                  Assert.equal [ "blank-summary" ] (rejectionCodes blank)
                  Assert.equal 2 (PraxisCli.run clone executor [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CP-001"; "--occurred-at"; PraxisCli.now (); "--summary"; "s" ]).ExitCode
                  Assert.equal 2 (groupCheckpoint clone [ "--decision"; " " ]).ExitCode
                  Assert.equal 2 (groupCheckpoint clone [ "stray" ]).ExitCode
                  let dryRun = groupCheckpoint clone [ "--dry-run"; "--json" ] |> PraxisCli.ok
                  Assert.isTrue (not (dryRun.Json["recorded"].GetValue<bool>())) "not recorded"
                  Assert.equal before (File.ReadAllText(storePath clone)))) ]

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
                    DeclaredBy = None
                    Membership = []
                    Checkpoints = [] }

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
        @ addTests
        @ removeTests
        @ checkpointTests
        @ checkpointCliTests
