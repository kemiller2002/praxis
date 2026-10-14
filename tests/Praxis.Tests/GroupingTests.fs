namespace Praxis.Tests

open System.Text.Json.Nodes
open Praxis.Contracts.Planning
open Praxis.Domain.Planning
open Praxis.Domain.Work
open PlanningFixtures

/// Work-group recommendations (requirements/PLANNING-WORK-GROUPS.md,
/// PRX-GRP-090 cases 1..20, plus group dependencies and merging).
module GroupingTests =
    let private t name run = { Name = $"grouping: {name}"; Run = run }

    let private tagged (id: string) (tags: string list) =
        { queued id "ready" "2026-09-01T00:00:00Z" with Tags = tags }

    let private withGrouping (configuration: PlannerConfiguration) (change: GroupingConfiguration -> GroupingConfiguration) =
        { configuration with Grouping = change configuration.Grouping }

    let private report queue liveItems observations configuration =
        let planningInput = input queue liveItems history observations configuration
        let analysis = Planner.analyze planningInput
        planningInput, analysis, Grouping.recommend planningInput analysis

    let private memberIds (group: WorkGroup) = group.Members |> List.map (fun entry -> entry.WorkItemId)

    let private groupOf (grouping: GroupingReport) (id: string) =
        grouping.Groups |> List.tryFind (fun group -> memberIds group |> List.contains id)

    let private persistence = [ "P-1"; "P-2"; "P-3" ] |> List.map (fun id -> tagged id [ "persistence" ])

    let private persistenceConfiguration =
        { stateSafe with Areas = [ "P-1", [ "src/Db" ]; "P-2", [ "src/Db/Schema" ]; "P-3", [ "src/Db/Migrations" ] ] }

    let tests =
        [ t "1 high-affinity items form a candidate group" (fun () ->
              let _, _, grouping = report persistence [] [] persistenceConfiguration
              let group = Assert.single grouping.Groups
              Assert.equal [ "P-1"; "P-2"; "P-3" ] (memberIds group)
              Assert.equal ContextAffinity.High group.Affinity
              Assert.equal GroupOrigin.PlannerRecommended group.Origin
              Assert.isTrue (group.Cohesion |> List.exists (fun line -> line.Statement.StartsWith "3/3 same area 'persistence'")) "cohesion must say 3/3 share the area")

          t "2 unrelated items are not grouped to reach a size" (fun () ->
              let unrelated = [ "U-1"; "U-2"; "U-3"; "U-4" ] |> List.map (fun id -> tagged id [ $"topic-{id.ToLowerInvariant()}" ])
              let _, _, grouping = report unrelated [] [] stateSafe
              Assert.empty grouping.Groups
              Assert.equal 4 grouping.Ungrouped.Length
              Assert.isTrue (grouping.Ungrouped |> List.forall (fun entry -> entry.Affinity = ContextAffinity.None)) "unrelated items have no affinity")

          t "3 explicit human grouping outranks inferred grouping" (fun () ->
              let configuration =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          Groups =
                            [ { Id = "GROUP-HUMAN-001"
                                Members = [ "P-1"; "D-1" ]
                                Kind = Some GroupKind.SharedMigration
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = [ "one typed migration model" ]
                                ExecutionRepository = None
                                CrossRepository = false
                                ArchitectureNotes = [ "no member may introduce direct SQL" ]; IndependentReason = None; IndependentMembers = [] } ] })

              let _, _, grouping = report (persistence @ [ tagged "D-1" [ "docs" ] ]) [] [] configuration
              let human = grouping.Groups |> List.find (fun group -> WorkGroupId.value group.Id = "GROUP-HUMAN-001")
              Assert.equal [ "D-1"; "P-1" ] (memberIds human)
              Assert.equal GroupOrigin.HumanDeclared human.Origin
              Assert.equal GroupKind.SharedMigration human.Kind
              let inferred = groupOf grouping "P-2" |> Option.get
              Assert.equal [ "P-2"; "P-3" ] (memberIds inferred)
              Assert.isTrue (grouping.Groups |> List.filter (fun group -> memberIds group |> List.contains "P-1") |> List.length = 1) "P-1 must belong only to the declared group")

          t "4 hard dependency order is kept inside a group" (fun () ->
              let queue =
                  [ { tagged "CHAIN-A" [ "api" ] with DependsOn = [ "CHAIN-B" ] }
                    { tagged "CHAIN-B" [ "api" ] with DependsOn = [ "CHAIN-C" ] }
                    tagged "CHAIN-C" [ "api" ] ]

              let _, _, grouping = report queue [] [] stateSafe
              let group = Assert.single grouping.Groups
              Assert.equal [ "CHAIN-C"; "CHAIN-B"; "CHAIN-A" ] group.RequiredSequence
              Assert.equal GroupExecution.OneSequentialAgent group.Execution
              let schedule = Grouping.schedule stateSafe (Planner.analyze (input queue [] history [] stateSafe)) grouping None
              let unit = schedule.Waves |> List.collect (fun wave -> wave.Units) |> Assert.single
              Assert.equal [ "CHAIN-C"; "CHAIN-B"; "CHAIN-A" ] unit.Members)

          t "5 external-repository work is not scheduled into the wrong checkout" (fun () ->
              let queue =
                  [ tagged "LOCAL-1" [ "remote" ]
                    tagged "LOCAL-2" [ "remote" ]
                    tagged "EXT-1" [ "remote" ]
                    tagged "EXT-2" [ "remote" ]
                    { tagged "EXT-3" [ "remote" ] with Description = Some "Install the surface. External repository." } ]

              let configuration =
                  withGrouping stateSafe (fun grouping -> { grouping with ExecutionRepositories = [ "EXT-1", "conditor"; "EXT-2", "conditor" ] })

              let _, _, grouping = report queue [] [] configuration
              Assert.equal 2 grouping.Groups.Length
              let external = groupOf grouping "EXT-1" |> Option.get
              Assert.equal [ "EXT-1"; "EXT-2" ] (memberIds external)
              Assert.equal "conditor" external.ExecutionRepository
              Assert.isTrue ((WorkGroupId.value external.Id).StartsWith "GROUP-CONDITOR-") "the group ID names its execution repository"
              let local = groupOf grouping "LOCAL-1" |> Option.get
              Assert.equal [ "LOCAL-1"; "LOCAL-2" ] (memberIds local)
              Assert.equal "fixture" local.ExecutionRepository
              Assert.equal None (groupOf grouping "EXT-3")
              let undeclared = grouping.Ungrouped |> List.find (fun entry -> entry.WorkItem = "EXT-3")
              Assert.isTrue (undeclared.Reason.Contains "external repository") "an undeclared external item says why it is not grouped")

          t "5b a declared group mixing repositories must be split or declared cross-repository" (fun () ->
              let declared cross =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          ExecutionRepositories = [ "EXT-1", "folio" ]
                          Groups =
                            [ { Id = "GROUP-ECHELON-PUBLISHING-001"
                                Members = [ "LOCAL-1"; "EXT-1" ]
                                Kind = None
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = []
                                ExecutionRepository = None
                                CrossRepository = cross
                                ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] } ] })

              let queue = [ tagged "LOCAL-1" [ "site" ]; tagged "EXT-1" [ "site" ] ]
              let _, _, mixed = report queue [] [] (declared false)
              let group = Assert.single mixed.Groups
              Assert.equal GroupExecution.SplitByRepository group.Execution
              Assert.isTrue (group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.MixedRepositories)) "mixed repositories must be reported"
              let _, _, cross = report queue [] [] (declared true)
              let group = Assert.single cross.Groups
              Assert.isTrue group.CrossRepository "declared cross-repository"
              Assert.isTrue (group.Execution <> GroupExecution.SplitByRepository) "an explicit cross-repository group is not split")

          t "6 blocked items remain blocked within a group" (fun () ->
              let queue = persistence @ [ { tagged "P-4" [ "persistence" ] with DependsOn = [ "P-2" ] } ]
              let blocked = { live "P-2" LiveWorkState.Blocked with BlockReason = Some "waiting on a schema review" }
              let planningInput, analysis, grouping = report queue [ blocked ] [] stateSafe
              let group = Assert.single grouping.Groups
              let status id = group.Members |> List.find (fun entry -> entry.WorkItemId = id)
              Assert.equal MemberStatus.Blocked (status "P-2").Status
              Assert.equal PlanningWorkState.Blocked (status "P-2").PlanningState
              Assert.equal [ "P-2" ] (status "P-4").GatedBy
              Assert.empty (status "P-1").GatedBy
              let schedule = Grouping.schedule planningInput.Configuration analysis grouping None
              let scheduled = schedule.Waves |> List.collect (fun wave -> wave.Units) |> List.collect (fun unit -> unit.Members)
              Assert.equal [ "P-1"; "P-3" ] scheduled
              Assert.isTrue (schedule.NotScheduled |> List.exists (fun (id, _) -> id = "P-4")) "a member waiting on a blocked member is not scheduled")

          t "7 completed items are excluded" (fun () ->
              let queue = persistence @ [ tagged "P-DONE" [ "persistence" ] ]
              let _, _, grouping = report queue [ live "P-DONE" LiveWorkState.Complete ] [] stateSafe
              Assert.equal [ "P-1"; "P-2"; "P-3" ] (grouping.Groups |> Assert.single |> memberIds)
              Assert.isTrue (grouping.Ungrouped |> List.forall (fun entry -> entry.WorkItem <> "P-DONE")) "terminal items are not listed as ungrouped work")

          t "8 group membership does not alter lifecycle state" (fun () ->
              let declared =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          Groups =
                            [ { Id = "GROUP-X-001"
                                Members = [ "P-1"; "P-2" ]
                                Kind = None
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = []
                                ExecutionRepository = None
                                CrossRepository = false
                                ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] } ] })

              let states (analysis: PlanningAnalysis) = analysis.Items |> List.map (fun entry -> entry.Id, entry.LifecycleState, entry.PlanningState)
              let _, plain, _ = report persistence [] [] stateSafe
              let _, grouped, grouping = report persistence [] [] declared
              Assert.equal (states plain) (states grouped)
              let group = grouping.Groups |> List.find (fun group -> WorkGroupId.value group.Id = "GROUP-X-001")
              Assert.isTrue (group.Members |> List.forall (fun entry -> entry.LifecycleState = "ready")) "members keep their recorded lifecycle")

          t "9 group membership does not merge attribution" (fun () ->
              let checkpointed = { live "P-1" LiveWorkState.Active with Checkpoint = Some(checkpoint "feature/p1" "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" "run the tests") }
              let _, analysis, grouping = report persistence [ checkpointed ] [] persistenceConfiguration
              let group = Assert.single grouping.Groups
              Assert.equal (memberIds group |> List.distinct) (memberIds group)
              Assert.equal None (item analysis "P-2").Checkpoint
              Assert.isTrue (item analysis "P-1").Checkpoint.IsSome "P-1 keeps its own checkpoint"
              let json = PlanningJson.groups analysis.Snapshot grouping
              let groupsArray = json["groups"].AsArray()
              let membersArray = groupsArray.[0].["members"].AsArray()
              let firstMember = membersArray[0].AsObject()
              let keys = firstMember |> Seq.map (fun property -> property.Key) |> Seq.toList
              Assert.equal [ "workItem"; "reason"; "confidence"; "recordedState"; "planningState"; "status"; "gatedBy" ] keys)

          t "10 partial completion is supported" (fun () ->
              let configuration =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          Groups =
                            [ { Id = "GROUP-SUMMA-DATABASE-004"
                                Members = [ "DB-21"; "DB-22"; "DB-23"; "DB-24" ]
                                Kind = None
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = []
                                ExecutionRepository = None
                                CrossRepository = false
                                ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] } ] })

              let queue = [ "DB-21"; "DB-22"; "DB-23"; "DB-24" ] |> List.map (fun id -> tagged id [ "database" ])
              let liveItems = [ live "DB-21" LiveWorkState.Complete; live "DB-22" LiveWorkState.Complete; { live "DB-23" LiveWorkState.Blocked with BlockReason = Some "needs a DBA" } ]
              let _, _, grouping = report queue liveItems [] configuration
              let group = Assert.single grouping.Groups
              Assert.equal 2 group.Progress.Complete
              Assert.equal 1 group.Progress.Blocked
              Assert.equal 1 group.Progress.Runnable
              Assert.isTrue (group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.PartialCompletion)) "partial completion is reported"
              Assert.empty (group.Members |> List.find (fun entry -> entry.WorkItemId = "DB-24")).GatedBy)

          t "11 unknown affinity remains unknown" (fun () ->
              let queue = [ { tagged "BARE-1" [] with Description = None }; tagged "SCOPED-1" [ "persistence" ] ]
              let planningInput, analysis, grouping = report queue [] [] stateSafe
              let pair = Grouping.affinity planningInput analysis "BARE-1" "SCOPED-1" |> Option.get
              Assert.equal ContextAffinity.Unknown pair.Level
              let bare = grouping.Ungrouped |> List.find (fun entry -> entry.WorkItem = "BARE-1")
              Assert.equal ContextAffinity.Unknown bare.Affinity)

          t "12 title similarity alone is insufficient" (fun () ->
              let queue =
                  [ { tagged "T-1" [] with Title = "Database timeout handling retries" }
                    { tagged "T-2" [] with Title = "Database timeout handling documentation" }
                    { tagged "T-3" [] with Title = "Database timeout handling metrics" } ]

              let planningInput, analysis, grouping = report queue [] [] stateSafe
              let pair = Grouping.affinity planningInput analysis "T-1" "T-2" |> Option.get
              Assert.equal ContextAffinity.Low pair.Level
              Assert.isTrue (pair.Signals |> List.forall (fun signal -> AffinitySignal.basis signal = SignalBasis.Inferred)) "title similarity is inferred"
              Assert.empty grouping.Groups)

          t "13 shared files raise affinity and collision risk separately" (fun () ->
              let planningInput, analysis, grouping = report persistence [] [] persistenceConfiguration
              let pair = Grouping.affinity planningInput analysis "P-1" "P-2" |> Option.get
              Assert.equal ContextAffinity.High pair.Level
              Assert.isTrue (pair.Signals |> List.contains (AffinitySignal.SharedDeclaredPath "src/Db")) "shared path is an affinity signal"
              let collision = analysis.Collisions |> List.find (fun collision -> collision.Left = "P-1" && collision.Right = "P-2")
              Assert.equal CollisionRisk.Conflict collision.Risk
              let group = Assert.single grouping.Groups
              Assert.equal ContextAffinity.High group.Affinity
              Assert.equal CollisionRisk.Conflict group.Collision)

          t "14 high affinity does not imply parallel safety" (fun () ->
              let _, _, grouping = report persistence [] [] persistenceConfiguration
              let group = Assert.single grouping.Groups
              Assert.isTrue (not group.ParallelSafe) "high-affinity members share files"
              Assert.equal GroupExecution.OneSequentialAgent group.Execution
              // Collision-safe members still get one owner, never one agent each.
              let declared =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          Groups =
                            [ { Id = "GROUP-SAFE-001"
                                Members = [ "S-1"; "S-2" ]
                                Kind = None
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = []
                                ExecutionRepository = None
                                CrossRepository = false
                                ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] } ] })

              let _, _, safe = report [ tagged "S-1" [ "alpha" ]; tagged "S-2" [ "beta" ] ] [] [] declared
              let group = Assert.single safe.Groups
              Assert.isTrue group.ParallelSafe "no collision signal between S-1 and S-2"
              Assert.equal GroupExecution.OneOwnerIndependentSubtasks group.Execution)

          t "15 deterministic input produces deterministic groups" (fun () ->
              let queue = persistence @ [ tagged "Q-1" [ "queue" ]; tagged "Q-2" [ "queue" ]; tagged "Q-3" [ "queue" ] ]
              let render queue =
                  let _, analysis, grouping = report queue [] [] persistenceConfiguration
                  PlanningJson.render (PlanningJson.logicalGroups analysis.Snapshot grouping)

              Assert.equal (render queue) (render queue)
              Assert.equal (render queue) (render (List.rev queue)))

          t "16 group recommendation JSON round-trips" (fun () ->
              let configuration =
                  withGrouping persistenceConfiguration (fun grouping ->
                      { grouping with
                          Architecture = [ { Decision = "DF-ROS-2026-A099"; Members = [ "P-3"; "Q-1" ]; Statement = "one migration model" } ] })

              let queue = persistence @ [ tagged "Q-1" [ "queue" ]; { tagged "Q-2" [ "queue" ] with DependsOn = [ "Q-1"; "P-1" ] } ]
              let _, analysis, grouping = report queue [ { live "Q-2" LiveWorkState.Blocked with BlockReason = Some "awaiting merge of PR #5" } ] [] configuration
              let json = PlanningJson.render (PlanningJson.groups analysis.Snapshot grouping)

              match PlanningJson.parseGroups json with
              | Error message -> failwith message
              | Ok(snapshot, parsed) ->
                  Assert.equal analysis.Snapshot snapshot
                  Assert.equal grouping parsed
                  Assert.equal json (PlanningJson.render (PlanningJson.groups snapshot parsed)))

          t "17 a group explanation names its evidence" (fun () ->
              let queue = persistence @ [ tagged "P-DONE" [ "persistence" ]; tagged "OTHER" [ "docs" ] ]
              let planningInput, analysis, grouping = report queue [ live "P-DONE" LiveWorkState.Complete ] [] persistenceConfiguration
              let id = grouping.Groups |> Assert.single |> fun group -> WorkGroupId.value group.Id

              match Grouping.explain planningInput analysis grouping id with
              | Error message -> failwith message
              | Ok explanation ->
                  Assert.isTrue (explanation.Evidence |> List.exists (fun (basis, statement) -> basis = SignalBasis.Explicit && statement.Contains "same area 'persistence'")) "explicit area evidence is named"
                  Assert.isTrue (explanation.Evidence |> List.exists (fun (_, statement) -> statement.Contains "src/Db")) "declared path evidence is named"
                  let excluded = explanation.Excluded |> List.find (fun entry -> entry.WorkItem = "P-DONE")
                  Assert.isTrue (excluded.Reason.StartsWith "complete") "a related completed item says why it is excluded"
                  Assert.isTrue (explanation.Excluded |> List.forall (fun entry -> entry.WorkItem <> "OTHER")) "unrelated items are not listed as exclusions"
                  Assert.isTrue (not explanation.Unknown.IsEmpty && not explanation.WouldChange.IsEmpty) "unknowns and what would change are stated"
                  Assert.isTrue (explanation.ExecutionRationale |> List.exists (fun line -> line.Contains "one-sequential-agent")) "one agent versus several is answered"

              match Grouping.explain planningInput analysis grouping "GROUP-NOPE-001" with
              | Ok _ -> failwith "an unknown group must be refused"
              | Error _ -> ())

          t "18 oversized groups produce warnings, automatic ones split" (fun () ->
              let ids = [ for index in 1..14 -> $"BIG-{index:D2}" ]
              let queue = ids |> List.map (fun id -> tagged id [ "big-area" ])

              let declared =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with
                          Groups =
                            [ { Id = "GROUP-BIG-001"
                                Members = ids
                                Kind = None
                                Origin = GroupOrigin.HumanDeclared
                                SharedContext = []
                                ExecutionRepository = None
                                CrossRepository = false
                                ArchitectureNotes = []; IndependentReason = None; IndependentMembers = [] } ] })

              let _, _, human = report queue [] [] declared
              let group = Assert.single human.Groups
              Assert.equal 14 group.Members.Length
              Assert.isTrue (group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.Oversized && entry.Severity = FindingSeverity.Warning)) "a large declared group warns"
              let _, _, automatic = report queue [] [] stateSafe
              Assert.equal [ 7; 7 ] (automatic.Groups |> List.map (fun group -> group.Members.Length))
              Assert.isTrue (automatic.Groups |> List.forall (fun group -> group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.SplitForSize))) "the split is explained")

          t "19 context-pressure evidence can trigger a split" (fun () ->
              let ids = [ for index in 1..6 -> $"CP-{index}" ]
              let queue = ids |> List.map (fun id -> tagged id [ "telemetry-core" ])
              let _, _, before = report queue [] [] stateSafe
              Assert.equal 6 (Assert.single before.Groups).Members.Length

              let pressure =
                  { Kind = ObservationKind.ContextPressure([ "CP-1"; "CP-2"; "CP-3"; "CP-4" ], [ "compactions", 2; "forgottenRequirements", 1 ])
                    Provenance = Provenance.create EvidenceSource.Telemetry "EXE-grouped-arm" }

              let _, _, after = report queue [] [ pressure ] stateSafe
              Assert.equal [ 3; 3 ] (after.Groups |> List.map (fun group -> group.Members.Length))
              let split = after.Groups |> List.head |> fun group -> group.Notes |> List.find (fun entry -> entry.Code = GroupNoteCode.SplitForContextPressure)
              Assert.isTrue (split.Message.Contains "compactions 2") "the split names its evidence")

          t "group dependencies derive from member dependencies and cycles stay detectable" (fun () ->
              let queue =
                  [ tagged "A-1" [ "alpha" ]
                    { tagged "A-2" [ "alpha" ] with DependsOn = [ "B-2" ] }
                    { tagged "B-1" [ "beta" ] with DependsOn = [ "A-1" ] }
                    tagged "B-2" [ "beta" ]
                    { tagged "U-1" [ "gamma" ] with DependsOn = [ "B-2" ] } ]

              let blocker = { live "A-1" LiveWorkState.Blocked with BlockReason = Some "awaiting merge of PR #5" }
              let _, _, grouping = report queue [ blocker ] [] stateSafe
              let alpha = groupOf grouping "A-1" |> Option.get |> fun group -> WorkGroupId.value group.Id
              let beta = groupOf grouping "B-1" |> Option.get |> fun group -> WorkGroupId.value group.Id
              let has source target = grouping.Dependencies |> List.exists (fun edge -> edge.From = source && edge.To = target)
              Assert.isTrue (has (GroupEndpoint.Group beta) (GroupEndpoint.Group alpha)) "group -> group"
              Assert.isTrue (has (GroupEndpoint.Group alpha) (GroupEndpoint.Group beta)) "the reverse edge from A-2"
              Assert.isTrue (has (GroupEndpoint.Item "U-1") (GroupEndpoint.Group beta)) "item -> group"
              Assert.isTrue (has (GroupEndpoint.Group alpha) (GroupEndpoint.External(DependencyTarget.PullRequest 5))) "group -> external prerequisite"
              let edge = grouping.Dependencies |> List.find (fun edge -> edge.From = GroupEndpoint.Group beta && edge.To = GroupEndpoint.Group alpha)
              Assert.isTrue (edge.Via |> List.exists (fun via -> via.StartsWith "B-1 -> A-1")) "the member dependency is named, not hidden"
              Assert.equal [ [ alpha; beta ] |> List.sort ] grouping.Cycles
              let relation = Assert.single grouping.Relations
              Assert.isTrue (relation.Dependent && not relation.MayRunConcurrently) "dependent groups do not run concurrently")

          t "a common architecture decision merges candidate groups, explained" (fun () ->
              let queue = [ tagged "M-1" [ "schema" ]; tagged "M-2" [ "schema" ]; tagged "N-1" [ "api" ]; tagged "N-2" [ "api" ] ]
              let _, _, separate = report queue [] [] stateSafe
              Assert.equal 2 separate.Groups.Length

              let configuration =
                  withGrouping stateSafe (fun grouping ->
                      { grouping with Architecture = [ { Decision = "DF-ROS-2026-A100"; Members = [ "M-1"; "M-2"; "N-1"; "N-2" ]; Statement = "one Result error boundary" } ] })

              let _, _, merged = report queue [] [] configuration
              let group = Assert.single merged.Groups
              Assert.equal 4 group.Members.Length
              Assert.equal GroupOrigin.ArchitectureDeclared group.Origin
              Assert.isTrue (group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.MergedByArchitecture && entry.Message.Contains "DF-ROS-2026-A100")) "the merge is explained")

          t "captured members make a planning group that must be triaged before execution" (fun () ->
              let queue = [ { tagged "C-1" [ "attribution" ] with Status = "captured" }; { tagged "C-2" [ "attribution" ] with Status = "captured" } ]
              let _, _, grouping = report queue [] [] stateSafe
              let group = Assert.single grouping.Groups
              Assert.isTrue (group.Members |> List.forall (fun entry -> entry.Status = MemberStatus.NotRunnable)) "captured members are not runnable"
              Assert.isTrue (group.ExecutionReasons |> List.exists (fun reason -> reason.StartsWith "triage")) "triage comes first"
              Assert.isTrue (group.Notes |> List.exists (fun entry -> entry.Code = GroupNoteCode.BelowPreferredSize)) "a cohesive pair is kept, and its size noted"
              let planningInput = input queue [] history [] stateSafe
              let analysis = Planner.analyze planningInput
              let speed = Scheduling.simulate analysis stateSafe OptimizationObjective.MinimumDuration None
              let tradeoff = Grouping.compare stateSafe analysis grouping speed None |> fun comparison -> Assert.single comparison.Tradeoffs
              Assert.equal [ "C-1"; "C-2" ] tradeoff.NotYetRunnable
              Assert.equal 2 tradeoff.Independent.Executions)

          t "compare --groups counts context acquisitions and never claims unmeasured savings" (fun () ->
              let planningInput, analysis, grouping = report persistence [] [] persistenceConfiguration
              let speed = Scheduling.simulate analysis planningInput.Configuration OptimizationObjective.MinimumDuration None
              let comparison = Grouping.compare planningInput.Configuration analysis grouping speed None
              let tradeoff = Assert.single comparison.Tradeoffs
              Assert.equal 3 tradeoff.Independent.ContextAcquisitions
              Assert.equal 1 tradeoff.Grouped.ContextAcquisitions
              Assert.isTrue (tradeoff.ContextSaving.Contains "unknown") "savings are unknown until measured"
              let group = Assert.single grouping.Groups
              Assert.equal EvidenceConfidence.Unknown group.ContextCost.ColdStart.Confidence
              let json = PlanningJson.render (PlanningJson.groups analysis.Snapshot grouping)
              let parsed = JsonNode.Parse json
              let groupsArray = parsed["groups"].AsArray()
              Assert.equal "unknown" (groupsArray.[0].["contextCost"].["estimatedReuse"].GetValue<string>())) ]
