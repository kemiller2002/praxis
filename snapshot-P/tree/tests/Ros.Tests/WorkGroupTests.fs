namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Git
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open PlanningFixtures

/// Pure fixtures for declared work groups (PRAXIS-GROUP-01..05).
module WorkGroupFixtures =
    let actorA =
        { Kind = ActorKind.Agent
          Id = "example/agent-a"
          Provider = Some "example"
          Model = Some "unknown"
          Runtime = Some "agent-a" }

    let actorB = { actorA with Id = "example/agent-b"; Runtime = Some "agent-b" }

    let at = "2026-10-01T00:00:00.000Z"

    let catalogItem id state =
        { Id = id
          State = state
          ExecutionRepository = "fixture"
          LatestCheckpointId = None }

    let catalog (items: CatalogItem list) =
        { Repository = "fixture"
          Items = items |> List.map (fun item -> item.Id, item) |> Map.ofList }

    let ready id = catalogItem id (RecordedWorkState.Backlog "ready")
    let active id = catalogItem id (RecordedWorkState.Live LiveWorkState.Active)
    let blocked id = catalogItem id (RecordedWorkState.Live LiveWorkState.Blocked)
    let complete id = catalogItem id (RecordedWorkState.Live LiveWorkState.Complete)

    let request id members =
        { Id = id
          Members = members
          Kind = Some GroupKind.SharedArea
          Origin = GroupOrigin.HumanDeclared
          ExecutionRepository = None
          CrossRepository = false
          SharedContext = [ "one typed group store" ]
          ArchitectureNotes = [ "every command writes only groups.json" ]
          OccurredAt = at
          Actor = actorA
          Reason = None }

    let created (known: WorkCatalog) id members =
        match WorkGroups.create known [] (request id members) with
        | Ok group -> group
        | Error rejections -> failwith $"%A{rejections}"

    let membership groupId workItemId =
        { GroupId = groupId
          WorkItemId = workItemId
          ExecutionRepository = None
          OccurredAt = "2026-10-02T00:00:00.000Z"
          Actor = actorB
          Reason = Some "shares the store" }

    let commit = (CommitId.tryParse "0123456789abcdef0123456789abcdef01234567").Value

    let verified: Result<GitDurableLocation, CheckpointRejection list> =
        Ok
            { Repository = "fixture"
              Branch = "feature/x"
              LocalCommit = commit
              Remote = { Name = "origin"; Url = None }
              RemoteBranch = "feature/x"
              RemoteCommit = commit }

    let checkpointRequest groupId =
        { GroupId = groupId
          Summary = "store and create done"
          NextAction = "implement show"
          SharedDecisions = [ "one groups.json store" ]
          OccurredAt = "2026-10-03T00:00:00.000Z"
          Actor = actorB }

    let identify (checkpoint: GroupCheckpoint) = $"cp-{checkpoint.Summary.Length}-{checkpoint.Active.Length}"

    let codes (result: Result<'a, GroupRejection list>) =
        match result with
        | Ok _ -> []
        | Error rejections -> rejections |> List.map GroupRejection.code

open WorkGroupFixtures

module WorkGroupTests =
    let private t name run = { Name = $"work group: {name}"; Run = run }

    let private standard = catalog [ ready "W-1"; active "W-2"; blocked "W-3"; complete "W-4"; catalogItem "W-5" (RecordedWorkState.Backlog "abandoned") ]

    let tests =
        [ t "create records the declaration, its members and who declared them" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2"; "W-3" ]
              Assert.equal [ "W-1"; "W-2"; "W-3" ] (WorkGroups.memberIds group)
              Assert.equal "fixture" group.ExecutionRepository
              Assert.equal actorA group.CreatedBy
              Assert.isTrue (group.Members |> List.forall (fun entry -> entry.AddedBy = actorA && entry.AddedAt = at)) "every member carries provenance"
              Assert.equal [ GroupChange.Created ] (group.History |> List.map (fun entry -> entry.Change))
              Assert.empty group.Checkpoints)

          t "create refuses unknown, terminal, repeated and invalid members, and invalid or duplicate IDs" (fun () ->
              Assert.equal [ "unknown-member" ] (codes (WorkGroups.create standard [] (request "GROUP-FIXTURE-001" [ "W-1"; "W-404" ])))
              Assert.equal [ "terminal-member"; "terminal-member" ] (codes (WorkGroups.create standard [] (request "GROUP-FIXTURE-001" [ "W-4"; "W-5" ])))
              Assert.equal [ "repeated-member" ] (codes (WorkGroups.create standard [] (request "GROUP-FIXTURE-001" [ "W-1"; "W-1" ])))
              Assert.equal [ "invalid-member-id" ] (codes (WorkGroups.create standard [] (request "GROUP-FIXTURE-001" [ "lower" ])))
              Assert.equal [ "invalid-group-id" ] (codes (WorkGroups.create standard [] (request "group-1" [ "W-1" ])))
              Assert.equal [ "duplicate-group" ] (codes (WorkGroups.create standard [ "GROUP-FIXTURE-001" ] (request "GROUP-FIXTURE-001" [ "W-1" ])))
              Assert.equal [ "no-members" ] (codes (WorkGroups.create standard [] (request "GROUP-FIXTURE-001" []))))

          t "create keeps groups repository-local unless declared cross-repository (PRX-GRP-051)" (fun () ->
              let elsewhere = { ready "X-1" with ExecutionRepository = "other-repo" }
              let known = catalog [ ready "W-1"; elsewhere ]
              Assert.equal [ "repository-mismatch" ] (codes (WorkGroups.create known [] (request "GROUP-FIXTURE-001" [ "W-1"; "X-1" ])))
              let crossing = WorkGroups.create known [] { request "GROUP-ECHELON-SHARED-001" [ "W-1"; "X-1" ] with CrossRepository = true }
              Assert.equal [] (codes crossing)

              match crossing with
              | Ok group -> Assert.equal [ "fixture"; "other-repo" ] (group.Members |> List.map (fun entry -> entry.ExecutionRepository))
              | Error _ -> ())

          t "the planner reads a stored group exactly as it reads grouping.groups (PRX-GRP-073)" (fun () ->
              let queue = [ "W-1"; "W-2"; "W-3" ] |> List.map (fun id -> queued id "ready" "2026-09-01T00:00:00Z")
              let known = catalog [ ready "W-1"; ready "W-2"; ready "W-3" ]
              let group = created known "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]

              let configured =
                  { stateSafe with Grouping = { stateSafe.Grouping with Groups = [ WorkGroups.declared group ] } }

              let stored = WorkGroups.declare "fixture" [ group ] stateSafe
              Assert.equal configured stored

              let recommend configuration =
                  let planningInput = input queue [] history [] configuration
                  Grouping.recommend planningInput (Planner.analyze planningInput)

              Assert.equal (recommend configured) (recommend stored)
              let declared = (recommend stored).Groups |> List.find (fun entry -> WorkGroupId.value entry.Id = "GROUP-FIXTURE-001")
              Assert.equal GroupOrigin.HumanDeclared declared.Origin
              Assert.equal [ "every command writes only groups.json" ] declared.ArchitectureNotes)

          t "a configured declaration with the same ID wins and an empty stored group declares nothing" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1" ]
              let configuredGroup = { WorkGroups.declared group with Members = [ "W-2" ] }
              let configuration = { stateSafe with Grouping = { stateSafe.Grouping with Groups = [ configuredGroup ] } }
              Assert.equal [ configuredGroup ] (WorkGroups.declare "fixture" [ group ] configuration).Grouping.Groups
              Assert.empty (WorkGroups.declare "fixture" [ { group with Members = [] } ] stateSafe).Grouping.Groups)

          t "a stored member executing elsewhere keeps that location in the planner" (fun () ->
              let elsewhere = { ready "X-1" with ExecutionRepository = "other-repo" }
              let known = catalog [ ready "W-1"; elsewhere ]

              let group =
                  match WorkGroups.create known [] { request "GROUP-ECHELON-SHARED-001" [ "W-1"; "X-1" ] with CrossRepository = true } with
                  | Ok value -> value
                  | Error rejections -> failwith $"%A{rejections}"

              Assert.equal [ "X-1", "other-repo" ] (WorkGroups.declare "fixture" [ group ] stateSafe).Grouping.ExecutionRepositories)

          t "validate reports unknown members, duplicate IDs and repository mixing; terminal members are fine" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]
              Assert.empty (WorkGroups.validate (catalog [ ready "W-1"; complete "W-2" ]) [ group ])
              let findings = WorkGroups.validate (catalog [ ready "W-1" ]) [ group; group ]
              Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "declared more than once")) "duplicate IDs"
              Assert.isTrue (findings |> List.exists (fun finding -> finding.Message.Contains "'W-2' is not a work item")) "unknown member"
              let mixed = { group with Members = group.Members |> List.map (fun entry -> { entry with ExecutionRepository = "other-repo" }) }
              Assert.isTrue (WorkGroups.validate standard [ mixed ] |> List.exists (fun finding -> finding.Message.Contains "repository-local")) "mixed repositories")

          t "show reports each member's own state, partial progress, and who a blocked member gates" (fun () ->
              let queue =
                  [ queued "W-1" "ready" "2026-09-01T00:00:00Z"
                    { queued "W-2" "ready" "2026-09-02T00:00:00Z" with DependsOn = [ "W-1" ] }
                    queued "W-3" "ready" "2026-09-03T00:00:00Z"
                    queued "W-4" "ready" "2026-09-04T00:00:00Z" ]

              let liveItems = [ { live "W-1" LiveWorkState.Blocked with BlockReason = Some "waiting on the API" }; live "W-3" LiveWorkState.Complete; live "W-4" LiveWorkState.Active ]
              let analysis = analyze queue liveItems history [] stateSafe
              let known = catalog [ blocked "W-1"; ready "W-2"; complete "W-3"; active "W-4" ]
              let group = { created (catalog [ ready "W-1"; ready "W-3"; ready "W-4" ]) "GROUP-FIXTURE-001" [ "W-1"; "W-3"; "W-4" ] with Checkpoints = [] }
              let view = GroupView.build known (Some analysis) group
              Assert.equal { Members = 3; Complete = 1; Abandoned = 0; Active = 1; Blocked = 1; Remaining = 2; Unknown = 0 } view.Progress
              Assert.equal "1 of 3 complete, 1 active, 1 blocked, 2 remaining" (GroupView.describeProgress view.Progress)
              let blockedMember = Assert.single (GroupView.blocked view)
              Assert.equal "W-1" blockedMember.Membership.WorkItemId
              Assert.equal [ "W-2" ] blockedMember.Gates
              Assert.equal (Some "waiting on the API") blockedMember.BlockReason
              Assert.equal (Some PlanningWorkState.Complete) (view.Members |> List.find (fun entry -> entry.Membership.WorkItemId = "W-3")).PlanningState
              let json = WorkGroupJson.render (WorkGroupJson.view view) |> JsonNode.Parse
              Assert.equal "W-2" (json["blocked"].[0].["gates"].[0].GetValue<string>())
              Assert.equal 1 (json["progress"].["complete"].GetValue<int>()))

          t "show without the planner keeps planning states unknown and counts untracked members" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]
              let view = GroupView.build (catalog [ ready "W-1" ]) None group
              Assert.isTrue (not view.PlanningAvailable) "the planner was not consulted"
              Assert.isTrue (view.Members |> List.forall (fun entry -> entry.PlanningState.IsNone)) "planning states are unknown, not guessed"
              Assert.equal 1 view.Progress.Unknown
              Assert.equal 2 view.Progress.Remaining)

          t "add appends one member with who added it, when and why" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1" ]

              match WorkGroups.add standard [ group ] (membership "GROUP-FIXTURE-001" "W-2") with
              | Error rejections -> failwith $"%A{rejections}"
              | Ok changed ->
                  Assert.equal [ "W-1"; "W-2" ] (WorkGroups.memberIds changed)
                  let added = changed.Members |> List.last
                  Assert.equal actorB added.AddedBy
                  Assert.equal "2026-10-02T00:00:00.000Z" added.AddedAt
                  Assert.equal (Some "shares the store") added.Reason
                  Assert.equal actorA (changed.Members |> List.head).AddedBy
                  Assert.equal [ GroupChange.Created; GroupChange.MemberAdded "W-2" ] (changed.History |> List.map (fun entry -> entry.Change))
                  Assert.equal actorB (changed.History |> List.last).Actor)

          t "add refuses unknown, terminal and present items, unknown groups, and another repository's item unless cross-repository" (fun () ->
              let elsewhere = { ready "X-1" with ExecutionRepository = "other-repo" }
              let known = catalog [ ready "W-1"; ready "W-2"; complete "W-4"; elsewhere ]
              let group = created known "GROUP-FIXTURE-001" [ "W-1" ]
              let add workItemId = WorkGroups.add known [ group ] (membership "GROUP-FIXTURE-001" workItemId)
              Assert.equal [ "unknown-member" ] (codes (add "W-404"))
              Assert.equal [ "terminal-member" ] (codes (add "W-4"))
              Assert.equal [ "already-member" ] (codes (add "W-1"))
              Assert.equal [ "repository-mismatch" ] (codes (add "X-1"))
              Assert.equal [ "repository-mismatch" ] (codes (WorkGroups.add known [ group ] { membership "GROUP-FIXTURE-001" "W-2" with ExecutionRepository = Some "third-repo" }))
              Assert.equal [ "group-not-found" ] (codes (WorkGroups.add known [ group ] (membership "GROUP-FIXTURE-404" "W-2")))
              Assert.equal [ "invalid-group-id" ] (codes (WorkGroups.add known [ group ] (membership "nope" "W-2")))
              let crossing = { group with CrossRepository = true }

              match WorkGroups.add known [ crossing ] (membership "GROUP-FIXTURE-001" "X-1") with
              | Ok changed -> Assert.equal "other-repo" (changed.Members |> List.last).ExecutionRepository
              | Error rejections -> failwith $"%A{rejections}")

          t "remove drops one member, records who removed it and why, and refuses non-members" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]
              Assert.equal [ "not-member" ] (codes (WorkGroups.remove [ group ] false (membership "GROUP-FIXTURE-001" "W-3")))
              Assert.equal [ "group-not-found" ] (codes (WorkGroups.remove [ group ] false (membership "GROUP-FIXTURE-404" "W-1")))

              match WorkGroups.remove [ group ] false (membership "GROUP-FIXTURE-001" "W-2") with
              | Error rejections -> failwith $"%A{rejections}"
              | Ok changed ->
                  Assert.equal [ "W-1" ] (WorkGroups.memberIds changed)
                  let entry = changed.History |> List.last
                  Assert.equal (GroupChange.MemberRemoved "W-2") entry.Change
                  Assert.equal actorB entry.Actor
                  Assert.equal (Some "shares the store") entry.Reason
                  Assert.equal group.Members.Head changed.Members.Head)

          t "removing the last member is refused unless explicit, and an emptied group keeps its history" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1" ]
              Assert.equal [ "last-member" ] (codes (WorkGroups.remove [ group ] false (membership "GROUP-FIXTURE-001" "W-1")))

              match WorkGroups.remove [ group ] true (membership "GROUP-FIXTURE-001" "W-1") with
              | Error rejections -> failwith $"%A{rejections}"
              | Ok emptied ->
                  Assert.empty emptied.Members
                  Assert.equal [ GroupChange.Created; GroupChange.MemberRemoved "W-1" ] (emptied.History |> List.map (fun entry -> entry.Change))
                  Assert.empty (WorkGroups.validate standard [ emptied ]))

          t "a member that is no longer tracked can still be removed" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]
              Assert.isTrue (not (WorkGroups.validate (catalog [ ready "W-1" ]) [ group ]).IsEmpty) "the untracked member is reported"

              match WorkGroups.remove [ group ] false (membership "GROUP-FIXTURE-001" "W-2") with
              | Ok changed -> Assert.empty (WorkGroups.validate (catalog [ ready "W-1" ]) [ changed ])
              | Error rejections -> failwith $"%A{rejections}")

          t "checkpoint partitions members by their own state and references, never replaces, their checkpoints" (fun () ->
              let known =
                  catalog
                      [ { active "W-1" with LatestCheckpointId = Some "member-cp-1" }
                        complete "W-2"
                        blocked "W-3"
                        catalogItem "W-5" (RecordedWorkState.Live LiveWorkState.Abandoned)
                        ready "W-6" ]

              let group = created (catalog [ ready "W-1"; ready "W-2"; ready "W-3"; ready "W-5"; ready "W-6" ]) "GROUP-FIXTURE-001" [ "W-1"; "W-2"; "W-3"; "W-5"; "W-6" ]

              match WorkGroups.checkpoint known [ group ] verified identify (checkpointRequest "GROUP-FIXTURE-001") with
              | Error rejections -> failwith $"%A{rejections}"
              | Ok(changed, recorded) ->
                  Assert.equal [ "W-1" ] recorded.Active
                  Assert.equal [ "W-2" ] recorded.Completed
                  Assert.equal [ "W-5" ] recorded.Abandoned
                  Assert.equal [ "W-1"; "W-3"; "W-6" ] recorded.Remaining
                  Assert.equal (Some "member-cp-1") (recorded.MemberCheckpoints |> List.find (fun reference -> reference.WorkItemId = "W-1")).CheckpointId
                  Assert.equal None (recorded.MemberCheckpoints |> List.find (fun reference -> reference.WorkItemId = "W-3")).CheckpointId
                  Assert.equal "cp-21-1" recorded.Id
                  Assert.equal [ "one groups.json store" ] recorded.SharedDecisions
                  Assert.equal actorB recorded.Actor
                  Assert.equal [ recorded ] changed.Checkpoints
                  Assert.equal (GroupChange.Checkpointed "cp-21-1") (changed.History |> List.last).Change
                  Assert.equal group.Members changed.Members
                  let json = WorkGroupJson.checkpoint recorded
                  Assert.equal 0 (json["paths"].AsArray().Count)
                  Assert.equal (Ok [ changed ]) (WorkGroupJson.parse (WorkGroupJson.render (WorkGroupJson.document [ changed ]))))

          t "checkpoint requires the same durable verification as work checkpoint, and real text" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1" ]
              let refused location = WorkGroups.checkpoint standard [ group ] location identify (checkpointRequest "GROUP-FIXTURE-001")
              let remote = { Name = "origin"; Url = None }
              Assert.equal [ "local-ahead" ] (codes (refused (Error [ CheckpointRejection.LocalAhead(1, remote, "feature/x") ])))
              Assert.equal [ "uncommitted-changes" ] (codes (refused (Error [ CheckpointRejection.UncommittedChanges [ "src/a.fs" ] ])))
              Assert.equal [ "no-upstream" ] (codes (refused (Error [ CheckpointRejection.NoUpstream "feature/x" ])))

              let blank = WorkGroups.checkpoint standard [ group ] verified identify { checkpointRequest "GROUP-FIXTURE-001" with Summary = " "; NextAction = "" }
              Assert.equal [ "blank-summary"; "blank-next-action" ] (codes blank)
              Assert.equal [ "group-not-found" ] (codes (WorkGroups.checkpoint standard [ group ] verified identify (checkpointRequest "GROUP-FIXTURE-404")))
              Assert.equal [ "empty-group" ] (codes (WorkGroups.checkpoint standard [ { group with Members = [] } ] verified identify (checkpointRequest "GROUP-FIXTURE-001"))))

          t "the durable-location rule a group checkpoint uses is work checkpoint's own" (fun () ->
              let remote = { Name = "origin"; Url = None }

              let observations head remoteHead tree =
                  { WorkItemState = None
                    Execution = ExecutionObservation.NoneActive
                    Head = GitRead.Observed(HeadState.OnBranch("feature/x", head))
                    Upstream = Some(GitRead.Observed(UpstreamState.Tracking(remote, "feature/x")))
                    RemoteBranch = Some(RemoteBranchObservation.At remoteHead)
                    LocalToRemote = if head = remoteHead then None else Some(CommitRelationObservation.Related(CommitRelation.Ahead 1))
                    WorkingTree =
                      match tree with
                      | [] -> GitStatusObservation.Clean
                      | paths ->
                          GitStatusObservation.Changed(
                              paths
                              |> List.map (fun path ->
                                  { Status = GitChangeStatus.Tracked(GitDelta.Unmodified, GitDelta.Modified)
                                    Path = path
                                    OriginalPath = None })
                          )
                    PathFilter = { PathFilterConfig.defaultConfig with IgnoredPatterns = [ ".ros/**" ] }
                    BaselineDirtyPaths = [] }

              let other = (CommitId.tryParse "fedcba9876543210fedcba9876543210fedcba98").Value

              match CheckpointVerification.verifyDurableLocation "fixture" (observations commit commit []) with
              | Ok location -> Assert.equal commit location.RemoteCommit
              | Error rejections -> failwith $"%A{rejections}"

              let codesOf result =
                  match result with
                  | Ok _ -> []
                  | Error rejections -> rejections |> List.map CheckpointRejection.code

              Assert.equal [ "local-ahead" ] (codesOf (CheckpointVerification.verifyDurableLocation "fixture" (observations commit other [])))
              Assert.equal [] (codesOf (CheckpointVerification.verifyDurableLocation "fixture" (observations commit commit [ ".ros/work/groups.json" ])))
              Assert.equal [ "uncommitted-changes" ] (codesOf (CheckpointVerification.verifyDurableLocation "fixture" (observations commit commit [ "src/a.fs" ]))))

          t "the stored document round-trips" (fun () ->
              let group = created standard "GROUP-FIXTURE-001" [ "W-1"; "W-2" ]
              let json = WorkGroupJson.render (WorkGroupJson.document [ group ])
              Assert.equal (Ok [ group ]) (WorkGroupJson.parse json)
              Assert.isTrue (match WorkGroupJson.parse """{"schemaVersion":"9.9.9","groups":[]}""" with Error _ -> true | Ok _ -> false) "an unknown schema is refused") ]

/// `work group ...` through the real binary against a fixture repository.
module WorkGroupCliTests =
    open PraxisCli

    let private t name run = { Name = $"work group cli: {name}"; Run = run }

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"

    let withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "work-group-cli"

        try
            let _, clone = installedRepository parent "clone"
            test clone
        finally
            GitFixture.cleanup parent

    let capture clone (id: string) =
        run clone (Some agentA) [ "work"; "capture"; "--id"; id; "--title"; $"Item {id}"; "--occurred-at"; now () ] |> ok |> ignore

    let start clone (id: string) =
        run clone (Some agentA) [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

    let createGroup clone (id: string) (members: string list) (extra: string list) =
        run clone (Some agentA) ([ "work"; "group"; "create"; "--id"; id; "--occurred-at"; now (); "--json" ] @ (members |> List.collect (fun entry -> [ "--member"; entry ])) @ extra)

    let rejectionCodes (result: Result) =
        match result.Json["rejections"] with
        | :? JsonArray as array -> array |> Seq.map (fun node -> text node["code"]) |> Seq.toList
        | _ -> []

    let workState clone =
        [ ".ros/work/queue.json"; ".ros/context/current.json"; ".ros/events/events.jsonl" ]
        |> List.map (fun relative ->
            let path = Path.Combine(clone, relative)
            if File.Exists path then File.ReadAllText path else "")

    let groupsFile clone = Path.Combine(clone, ".ros", "work", "groups.json")

    let repositoryName clone = Ros.Infrastructure.Planning.FileWorkGroupRepository.repositoryName clone

    let tests =
        [ t "create records the group, leaves members' records byte-identical, and the planner reads it" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  capture clone "FEAT-2"
                  start clone "FEAT-3"
                  let before = workState clone
                  let result = createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2"; "FEAT-3" ] [ "--kind"; "shared-area"; "--shared-context"; "one store" ] |> ok
                  Assert.equal "recorded" (text result.Json["status"])
                  Assert.equal 3 (result.Json["group"].["members"].AsArray().Count)
                  Assert.equal "example/agent-a" (text result.Json["group"].["createdBy"].["id"])
                  Assert.equal before (workState clone)
                  let groups = run clone None [ "plan"; "groups"; "--json" ] |> ok
                  let declared = groups.Json["groups"].AsArray() |> Seq.find (fun node -> text node["id"] = "GROUP-FIXTURE-001")
                  Assert.equal "human-declared" (text declared["origin"])
                  run clone None [ "validate" ] |> ok |> ignore))

          t "create --dry-run records nothing; refusals record nothing and name every reason" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  start clone "FEAT-2"
                  run clone (Some agentA) [ "work"; "abandon"; "--id"; "FEAT-2"; "--reason"; "cancelled"; "--occurred-at"; now () ] |> ok |> ignore
                  let dry = createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dry.Json["status"])
                  Assert.isTrue (not (File.Exists(groupsFile clone))) "a dry run wrote groups.json"
                  let refused = createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2"; "FEAT-404" ] []
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "terminal-member"; "unknown-member" ] (rejectionCodes refused |> List.sort)
                  Assert.isTrue (not (File.Exists(groupsFile clone))) "a refusal wrote groups.json"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                  let duplicate = createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] []
                  Assert.equal [ "duplicate-group" ] (rejectionCodes duplicate)
                  let invalid = createGroup clone "group-x" [ "FEAT-1" ] []
                  Assert.equal 2 invalid.ExitCode
                  Assert.equal [ "invalid-group-id" ] (rejectionCodes invalid)))

          t "create refuses an ID already declared in the named planner configuration" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  File.WriteAllText(Path.Combine(clone, "planner.json"), """{"grouping":{"groups":[{"id":"GROUP-FIXTURE-001","members":["FEAT-1"]}]}}""")
                  let refused = createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [ "--config"; "planner.json" ]
                  Assert.equal [ "duplicate-group" ] (rejectionCodes refused)))

          t "show prints members, progress and blocked members in text and JSON, exits 1 for an unknown group, and never writes" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  start clone "FEAT-2"
                  start clone "FEAT-3"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2"; "FEAT-3" ] [ "--architecture-note"; "one store" ] |> ok |> ignore
                  run clone (Some agentA) [ "work"; "block"; "--id"; "FEAT-3"; "--reason"; "waiting on review"; "--unrecoverable-reason"; "fixture"; "--occurred-at"; now () ] |> ok |> ignore
                  let before = workState clone @ [ File.ReadAllText(groupsFile clone) ]
                  let shown = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok
                  let document = shown.Json
                  Assert.equal "work group show" (text document["command"])
                  Assert.equal 3 (document["progress"].["members"].GetValue<int>())
                  Assert.equal 1 (document["progress"].["blocked"].GetValue<int>())
                  Assert.equal "FEAT-3" (text document["blocked"].[0].["workItemId"])
                  Assert.equal "captured" (text document["members"].[0].["recordedState"])
                  Assert.equal "active" (text document["members"].[1].["recordedState"])
                  Assert.equal "one store" (text document["architectureNotes"].[0])
                  Assert.equal (repositoryName clone) (text document["executionRepository"])
                  let textView = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ok
                  Assert.isTrue (textView.Output.Contains "Progress: 0 of 3 complete") textView.Output
                  Assert.isTrue (textView.Output.Contains "FEAT-3 (waiting on review)") textView.Output
                  let missing = run clone None [ "work"; "group"; "show"; "GROUP-MISSING-001" ]
                  Assert.equal 1 missing.ExitCode
                  Assert.isTrue (missing.Error.Contains "GROUP-MISSING-001") missing.Error
                  Assert.equal before (workState clone @ [ File.ReadAllText(groupsFile clone) ])))

          t "add records the member with its own provenance and leaves every member's records byte-identical" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  capture clone "FEAT-2"
                  start clone "FEAT-3"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                  let before = workState clone
                  let agentB = agent "example/agent-b" "example" "agent-b" "session-b"

                  let add who (id: string) (extra: string list) =
                      run clone (Some who) ([ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-001"; "--member"; id; "--occurred-at"; now (); "--json" ] @ extra)

                  let dry = add agentB "FEAT-2" [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dry.Json["status"])
                  Assert.equal 1 ((run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok).Json["members"].AsArray().Count)
                  let added = add agentB "FEAT-2" [ "--reason"; "same store" ] |> ok
                  Assert.equal "recorded" (text added.Json["status"])
                  let members = added.Json["group"].["members"].AsArray()
                  Assert.equal "example/agent-b" (text members[1].["addedBy"].["id"])
                  Assert.equal "example/agent-a" (text members[0].["addedBy"].["id"])
                  Assert.equal "same store" (text members[1].["reason"])
                  Assert.equal "member-added" (text (added.Json["group"].["history"].AsArray() |> Seq.last).["change"])
                  Assert.equal [ "already-member" ] (rejectionCodes (add agentA "FEAT-2" []))
                  Assert.equal [ "unknown-member" ] (rejectionCodes (add agentA "FEAT-404" []))
                  Assert.equal [ "repository-mismatch" ] (rejectionCodes (add agentA "FEAT-3" [ "--execution-repository"; "other-repo" ]))
                  Assert.equal before (workState clone)
                  run clone None [ "validate" ] |> ok |> ignore))

          t "remove never touches the member's records, refuses non-members, and empties a group only when told" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  start clone "FEAT-2"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] [] |> ok |> ignore
                  let before = workState clone

                  let remove (id: string) (extra: string list) =
                      run clone (Some agentA) ([ "work"; "group"; "remove"; "--id"; "GROUP-FIXTURE-001"; "--member"; id; "--occurred-at"; now (); "--json" ] @ extra)

                  Assert.equal [ "not-member" ] (rejectionCodes (remove "FEAT-404" []))
                  let dry = remove "FEAT-2" [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dry.Json["status"])
                  let removed = remove "FEAT-2" [ "--reason"; "split out" ] |> ok
                  Assert.equal "recorded" (text removed.Json["status"])
                  let last = (removed.Json["group"].["history"].AsArray() |> Seq.last)
                  Assert.equal "member-removed" (text last["change"])
                  Assert.equal "FEAT-2" (text last["subject"])
                  Assert.equal "example/agent-a" (text last["actor"].["id"])
                  Assert.equal "split out" (text last["reason"])
                  Assert.equal before (workState clone)
                  let refused = remove "FEAT-1" []
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "last-member" ] (rejectionCodes refused)
                  let emptied = remove "FEAT-1" [ "--allow-empty" ] |> ok
                  Assert.equal 0 (emptied.Json["group"].["members"].AsArray().Count)
                  Assert.equal before (workState clone)
                  run clone None [ "validate" ] |> ok |> ignore
                  let groups = run clone None [ "plan"; "groups"; "--json" ] |> ok
                  Assert.isTrue (groups.Json["groups"].AsArray() |> Seq.forall (fun node -> text node["id"] <> "GROUP-FIXTURE-001")) "an empty group declares nothing"))

          t "checkpoint verifies durability like work checkpoint, references members' own checkpoints and claims none of their changes" (fun () ->
              withRepository (fun clone ->
                  start clone "FEAT-1"
                  start clone "FEAT-2"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] [] |> ok |> ignore
                  GitFixture.write clone "src/a.txt" "a\n"
                  pushAll clone "FEAT-1 work" |> ignore
                  let own = run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "a done"; "--next-action"; "b"; "--json" ] |> ok
                  let ownId = text own.Json["checkpoint"].["id"]
                  GitFixture.write clone "src/b.txt" "b\n"
                  let head = pushAll clone "FEAT-2 work"

                  let groupCheckpoint (extra: string list) =
                      run clone (Some agentA) ([ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "both slices pushed"; "--next-action"; "finish FEAT-2"; "--decision"; "one store"; "--json" ] @ extra)

                  let before = workState clone
                  let recorded = groupCheckpoint [] |> ok
                  let document = recorded.Json["checkpoint"]
                  Assert.equal "recorded" (text recorded.Json["status"])
                  Assert.equal head (text document["location"].["commit"])
                  Assert.equal head (text document["location"].["remoteCommit"])
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] (document["members"].["active"].AsArray() |> Seq.map text |> Seq.toList)
                  Assert.equal ownId (text document["memberCheckpoints"].[0].["checkpointId"])
                  Assert.isTrue (isNull document["memberCheckpoints"].[1].["checkpointId"]) "FEAT-2 has no checkpoint of its own"
                  Assert.equal 0 (document["paths"].AsArray().Count)
                  Assert.equal before (workState clone)
                  let shown = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok
                  Assert.equal (text document["id"]) (text shown.Json["latestCheckpoint"].["id"])
                  run clone None [ "validate" ] |> ok |> ignore
                  // FEAT-2's own checkpoint still owns its change; FEAT-1's is not claimed by it.
                  let second = run clone (Some agentA) [ "work"; "checkpoint"; "--id"; "FEAT-2"; "--occurred-at"; now (); "--summary"; "b done"; "--next-action"; "complete"; "--json" ] |> ok
                  let paths = second.Json["paths"].AsArray() |> Seq.map text |> Seq.toList
                  Assert.isTrue (List.contains "src/b.txt" paths) $"%A{paths}"
                  Assert.isTrue (not (List.contains "src/a.txt" paths)) $"%A{paths}"
                  let history = run clone None [ "work"; "checkpoint"; "show"; "FEAT-1"; "--json"; "--offline" ] |> ok
                  Assert.equal 1 (history.Json["history"].AsArray().Count)))

          t "checkpoint refuses an unpushed or dirty state and records nothing; validate catches altered checkpoints" (fun () ->
              withRepository (fun clone ->
                  start clone "FEAT-1"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore

                  let groupCheckpoint (extra: string list) =
                      run clone (Some agentA) ([ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "slice"; "--next-action"; "next"; "--json" ] @ extra)

                  let stored = File.ReadAllText(groupsFile clone)
                  GitFixture.write clone "src/a.txt" "a\n"
                  GitFixture.commitAll clone "not pushed" |> ignore
                  Assert.equal [ "local-ahead" ] (rejectionCodes (groupCheckpoint []))
                  GitFixture.git clone [ "push"; "-q" ] |> ignore
                  GitFixture.write clone "src/a.txt" "changed\n"
                  Assert.equal [ "uncommitted-changes" ] (rejectionCodes (groupCheckpoint []))
                  Assert.equal stored (File.ReadAllText(groupsFile clone))
                  pushAll clone "pushed" |> ignore
                  let blank = run clone (Some agentA) [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; " "; "--next-action"; "x"; "--json" ]
                  Assert.equal 2 blank.ExitCode
                  Assert.equal [ "blank-summary" ] (rejectionCodes blank)
                  let dry = groupCheckpoint [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dry.Json["status"])
                  Assert.equal stored (File.ReadAllText(groupsFile clone))
                  groupCheckpoint [] |> ok |> ignore
                  run clone None [ "validate" ] |> ok |> ignore
                  let file = groupsFile clone
                  File.WriteAllText(file, (File.ReadAllText file).Replace("\"summary\": \"slice\"", "\"summary\": \"rewritten\""))
                  let failed = run clone None [ "validate" ]
                  Assert.equal 1 failed.ExitCode
                  Assert.isTrue (failed.Error.Contains "does not match its content") failed.Error))

          t "validate checks stored groups" (fun () ->
              withRepository (fun clone ->
                  capture clone "FEAT-1"
                  createGroup clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                  let file = groupsFile clone
                  File.WriteAllText(file, (File.ReadAllText file).Replace("\"workItemId\": \"FEAT-1\"", "\"workItemId\": \"FEAT-404\""))
                  let failed = run clone None [ "validate" ]
                  Assert.equal 1 failed.ExitCode
                  Assert.isTrue (failed.Error.Contains "FEAT-404") failed.Error
                  File.WriteAllText(file, "{ not json")
                  Assert.equal 1 (run clone None [ "validate" ]).ExitCode)) ]
