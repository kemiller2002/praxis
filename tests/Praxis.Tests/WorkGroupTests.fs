namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Git
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// `work group create|show|add|remove|checkpoint` (PRX-GRP-073): the stored
/// group model, its decisions, and the commands through the real binary.
[<RequireQualifiedAccess>]
module WorkGroupTests =
    open PraxisCli

    // ---- pure fixtures ----

    let private human: Actor =
        { Kind = ActorKind.Human
          Id = "owner"
          Provider = None
          Model = None
          Runtime = None }

    let private standings =
        Map.ofList
            [ "ITEM-1", MemberStanding.Open "ready"
              "ITEM-2", MemberStanding.Open "active"
              "ITEM-3", MemberStanding.Open "blocked"
              "ITEM-4", MemberStanding.Open "captured"
              "DONE-1", MemberStanding.Terminal "complete"
              "GONE-1", MemberStanding.Terminal "abandoned"
              "ELSE-1", MemberStanding.Open "ready"
              "AWAY-1", MemberStanding.Open "ready" ]

    /// Observations of other repositories: `owner/other` is readable,
    /// `owner/offline` is not.
    let private observation (repository: string) (id: string) (outcome: ObservedMember) (asOf: string) : MemberObservation =
        { Member = $"{repository}:{id}"
          Repository = repository
          WorkItemId = id
          Method = "git-ref"
          Ref = Some "origin/main"
          Commit = Some(String.replicate 40 "e")
          ObservedAt = asOf
          SourceAsOf = Some asOf
          Outcome = outcome
          LatestCheckpoint = None
          Linked = Some true }

    let private remoteStates =
        Map.ofList [ "OPEN-1", "ready"; "OPEN-2", "active"; "SHUT-1", "complete" ]

    let private observe (repository: string) (id: string) =
        match repository with
        | "owner/other" -> observation repository id (ObservedMember.Read(remoteStates |> Map.tryFind id)) "2026-10-06T12:00:00.000Z"
        | _ -> observation repository id (ObservedMember.Unobservable(Unobservable.Unreachable "no observation source")) "2026-10-06T12:00:00.000Z"

    let private context (groups: StoredWorkGroup list) : GroupContext =
        { Groups = groups
          Standing = fun id -> standings |> Map.tryFind id |> Option.defaultValue MemberStanding.Unknown
          RepositoryOf =
            fun id ->
                match id with
                | "ELSE-1" -> ExecutionLocation.Repository "other-repository"
                | "AWAY-1" -> ExecutionLocation.UnknownExternal
                | _ -> ExecutionLocation.Repository "this-repository"
          ThisRepository = Some "owner/this"
          Observe = observe }

    let private request (groupId: string) (members: string list) : GroupCreateRequest =
        { GroupId = groupId
          Members = members
          Kind = Some GroupKind.SharedApiSurface
          Origin = GroupOrigin.HumanDeclared
          SharedContext = [ "one store" ]
          ExecutionRepository = "this-repository"
          CrossRepository = false
          ArchitectureNotes = []
          OccurredAt = "2026-09-30T12:00:00.000Z"
          Actor = human
          Reason = Some "they share one API surface"
          ExecutionId = None
          HomeRepository = None
          Dependencies = []
          IndependentReason = None
          IndependentMembers = [] }

    let private created (groupId: string) (members: string list) =
        match WorkGroups.create (context []) (request groupId members) with
        | Ok change -> change.Group
        | Error rejections -> failwith $"unexpected rejection: {rejections}"

    let private codes (result: Result<'a, GroupRejection list>) =
        match result with
        | Ok _ -> []
        | Error rejections -> rejections |> List.map GroupRejection.code

    // ---- CLI fixture ----

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private cli clone arguments = run clone (Some agentA) arguments

    /// A Praxis repository with open items ITEM-1..3 (ready; ITEM-3 depends
    /// on ITEM-2), ITEM-4 (captured), a completed DONE-1 and an abandoned
    /// GONE-1, all pushed.
    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "work-group"

        try
            let _, clone = installedRepository parent "clone-a"

            for id in [ "ITEM-1"; "ITEM-2"; "ITEM-4"; "DONE-1"; "GONE-1" ] do
                cli clone [ "add"; $"Work {id}"; "--id"; id ] |> ok |> ignore

            cli clone [ "add"; "Work ITEM-3"; "--id"; "ITEM-3"; "--description"; "Follows the second item. Depends on: ITEM-2." ] |> ok |> ignore

            for id in [ "ITEM-1"; "ITEM-2"; "ITEM-3"; "DONE-1"; "GONE-1" ] do
                cli clone [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore

            cli clone [ "work"; "start"; "--id"; "DONE-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
            cli clone [ "work"; "complete"; "--id"; "DONE-1"; "--occurred-at"; now (); "--evidence"; "implementation=README.md"; "--evidence"; "tests=README.md" ] |> ok |> ignore
            cli clone [ "work"; "backlog-transition"; "--id"; "GONE-1"; "--action"; "abandon"; "--reason"; "not needed"; "--occurred-at"; now () ] |> ok |> ignore
            pushAll clone "praxis state" |> ignore
            test clone
        finally
            GitFixture.cleanup parent

    let private lifecycleFiles = [ ".ros/work/queue.json"; ".ros/context/current.json"; ".ros/events/events.jsonl" ]

    /// The members' lifecycle records, hashed: group commands never change them.
    let private lifecycle clone =
        lifecycleFiles
        |> List.map (fun relative ->
            let file = Path.Combine(clone, relative)
            relative, (if File.Exists file then Convert.ToHexString(SHA256.HashData(File.ReadAllBytes file)) else "absent"))

    let private groupsFile clone = Path.Combine(clone, ".ros", "work", "groups.json")

    let private createGroup clone (groupId: string) (members: string list) (extra: string list) =
        cli clone ([ "work"; "group"; "create"; "--group"; groupId; "--occurred-at"; now (); "--json" ] @ (members |> List.collect (fun id -> [ "--member"; id ])) @ extra)

    let private rejectionCodes (result: Result) =
        match result.Json["rejections"] with
        | :? JsonArray as array -> array |> Seq.map (fun node -> text node["code"]) |> Seq.toList
        | _ -> []

    let private t name run = { Name = $"work group: {name}"; Run = run }

    let tests =
        [ t "create refuses unknown, terminal and repeated members, invalid IDs, duplicates and foreign-repository items together" (fun () ->
              Assert.equal [] (codes (WorkGroups.create (context []) (request "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3"; "ITEM-4" ])))

              Assert.equal
                  [ "invalid-group-id"; "repeated-member"; "unknown-member"; "terminal-member"; "terminal-member"; "repository-mismatch" ]
                  (codes (WorkGroups.create (context []) (request "group-area" [ "ITEM-1"; "ITEM-1"; "NOPE-1"; "DONE-1"; "GONE-1"; "ELSE-1" ])))

              let existing = created "GROUP-AREA-001" [ "ITEM-1" ]
              Assert.equal [ "duplicate-group"; "no-members" ] (codes (WorkGroups.create (context [ existing ]) (request "GROUP-AREA-001" [])))
              Assert.equal [ "invalid-member-id" ] (codes (WorkGroups.create (context []) (request "GROUP-AREA-002" [ "not an id" ])))

              let crossRepository = { request "GROUP-ECHELON-AREA-003" [ "ITEM-1"; "ELSE-1" ] with CrossRepository = true }
              Assert.equal [] (codes (WorkGroups.create (context []) crossRepository)))

          t "a created group records its declaration and a creation entry, and nothing about any member's lifecycle" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-2"; "ITEM-1" ]
              Assert.equal [ "ITEM-2"; "ITEM-1" ] group.Declaration.Members
              Assert.equal (Some "this-repository") group.Declaration.ExecutionRepository
              Assert.equal GroupOrigin.HumanDeclared group.Declaration.Origin
              let entry = Assert.single group.History
              Assert.equal GroupOperation.Created entry.Operation
              Assert.equal human entry.Actor
              Assert.equal (Some "they share one API surface") entry.Reason)

          t "stored declarations join configured ones; a configured group with the same ID keeps its configured form" (fun () ->
              let stored = [ created "GROUP-AREA-001" [ "ITEM-1" ]; created "GROUP-AREA-002" [ "ITEM-2" ] ]
              let configured = { (created "GROUP-AREA-002" [ "ITEM-3" ]).Declaration with SharedContext = [ "configured" ] }
              let merged = WorkGroups.declarations [ configured ] stored
              Assert.equal [ "GROUP-AREA-002"; "GROUP-AREA-001" ] (merged |> List.map (fun group -> group.Id))
              Assert.equal [ "configured" ] merged.Head.SharedContext
              Assert.equal [ "GROUP-AREA-001"; "GROUP-AREA-002" ] (WorkGroups.declarations [] stored |> List.map (fun group -> group.Id)))

          t "validation accepts terminal members of a stored group but reports unknown members, empty groups and broken history" (fun () ->
              let joined = created "GROUP-AREA-001" [ "ITEM-1" ]
              // DONE-1 completed after it joined.
              let group = { joined with Declaration = { joined.Declaration with Members = [ "ITEM-1"; "DONE-1" ] } }
              Assert.empty (WorkGroups.findings (context [ group ]))

              let broken =
                  { group with
                      Declaration = { group.Declaration with Members = [] }
                      History = [] }

              let unknown = { group with Declaration = { group.Declaration with Id = "GROUP-AREA-002"; Members = [ "NOPE-1"; "ITEM-1"; "ITEM-1" ] } }
              let fields = WorkGroups.findings (context [ broken; unknown ]) |> List.map (fun (id, field, _) -> $"{id}.{field}")
              Assert.equal [ "GROUP-AREA-001.members"; "GROUP-AREA-001.history"; "GROUP-AREA-002.members"; "GROUP-AREA-002.members" ] fields)

          t "the store round-trips through JSON byte for byte, ordered by group ID" (fun () ->
              let groups = WorkGroups.upsert (WorkGroups.upsert [] (created "GROUP-B-001" [ "ITEM-1" ])) (created "GROUP-A-001" [ "ITEM-2"; "ITEM-3" ])
              Assert.equal [ "GROUP-A-001"; "GROUP-B-001" ] (groups |> List.map (fun group -> group.Declaration.Id))
              let rendered = WorkGroupJson.renderStore groups

              match WorkGroupJson.readStore rendered with
              | Ok read ->
                  Assert.equal groups read
                  Assert.equal rendered (WorkGroupJson.renderStore read)
              | Error message -> failwith message

              Assert.isTrue (WorkGroupJson.readStore """{"schemaVersion":1,"groups":[{"id":"GROUP-A-001"}]}""" |> Result.isError) "a malformed group was accepted")

          t "cli create records the group in groups.json only, with provenance, and leaves every member's lifecycle byte-identical" (fun () ->
              withRepository (fun clone ->
                  let before = lifecycle clone
                  let result = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2"; "ITEM-4" ] [ "--kind"; "shared-files"; "--shared-context"; "one module" ] |> ok
                  Assert.equal "recorded" (text result.Json["status"])
                  let group = result.Json["group"]
                  Assert.equal "GROUP-FIXTURE-001" (text group["id"])
                  Assert.equal "shared-files" (text group["kind"])
                  Assert.equal "example/agent-a" (text group["createdBy"].["id"])
                  Assert.equal "created" (text group["history"].[0].["operation"])
                  Assert.equal before (lifecycle clone)
                  Assert.isTrue (File.Exists(groupsFile clone)) "groups.json was not written"
                  run clone None [ "validate" ] |> ok |> ignore))

          t "cli create --dry-run and refusals write nothing; argument errors exit 2 and state refusals exit 1" (fun () ->
              withRepository (fun clone ->
                  let dryRun = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [ "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dryRun.Json["status"])
                  Assert.isTrue (not (File.Exists(groupsFile clone))) "a dry run wrote groups.json"

                  let refused = createGroup clone "GROUP-FIXTURE-001" [ "DONE-1"; "GONE-1"; "NOPE-1" ] []
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "terminal-member"; "terminal-member"; "unknown-member" ] (rejectionCodes refused)
                  Assert.isTrue (not (File.Exists(groupsFile clone))) "a refusal wrote groups.json"

                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  let duplicate = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-2" ] []
                  Assert.equal 1 duplicate.ExitCode
                  Assert.equal [ "duplicate-group" ] (rejectionCodes duplicate)

                  Assert.equal 2 (createGroup clone "lower-case" [ "ITEM-2" ] []).ExitCode
                  Assert.equal 2 (cli clone [ "work"; "group"; "create"; "--group"; "GROUP-FIXTURE-002"; "--occurred-at"; now () ]).ExitCode
                  Assert.equal 2 (createGroup clone "GROUP-FIXTURE-002" [ "ITEM-2" ] [ "--kind"; "nonsense" ]).ExitCode))

          t "cli the planner reads a stored group exactly as the same declaration in grouping.groups" (fun () ->
              withRepository (fun clone ->
                  let asOf = "2026-09-30T12:00:00.000Z"
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] [ "--kind"; "shared-files"; "--shared-context"; "one module" ] |> ok |> ignore
                  let stored = run clone None [ "plan"; "explain-group"; "GROUP-FIXTURE-001"; "--as-of"; asOf; "--json" ] |> ok
                  let group = (JsonNode.Parse(File.ReadAllText(groupsFile clone)).["groups"].[0]).DeepClone().AsObject()
                  let configuration = Path.Combine(clone, "..", "planner.json")
                  File.WriteAllText(configuration, $"""{{"grouping":{{"groups":[{group.ToJsonString()}]}}}}""")
                  File.Delete(groupsFile clone)
                  let configured = run clone None [ "plan"; "explain-group"; "GROUP-FIXTURE-001"; "--as-of"; asOf; "--config"; configuration; "--json" ] |> ok
                  Assert.equal configured.Output stored.Output))

          t "progress keeps every member's own standing, never implies the group succeeded, and names who a blocked member gates" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3"; "ITEM-4" ]

              let facts =
                  Map.ofList
                      [ "ITEM-1", { State = Some "complete"; PlanningState = Some "complete"; WaitsOn = []; WaitsOnBlocked = []; Observation = None; Stale = false }
                        "ITEM-2", { State = Some "blocked"; PlanningState = Some "blocked"; WaitsOn = []; WaitsOnBlocked = []; Observation = None; Stale = false }
                        "ITEM-3", { State = Some "ready"; PlanningState = Some "waiting-on-dependency"; WaitsOn = [ "ITEM-2" ]; WaitsOnBlocked = [ "ITEM-2" ]; Observation = None; Stale = false }
                        "ITEM-4", { State = None; PlanningState = None; WaitsOn = [ "ITEM-2" ]; WaitsOnBlocked = [ "ITEM-2" ]; Observation = None; Stale = false } ]

              let progress = WorkGroups.progress group (fun id -> facts[id])
              Assert.equal [ "ITEM-1" ] progress.Completed
              Assert.equal [ "ITEM-2" ] progress.Blocked
              Assert.equal [ "ITEM-3" ] progress.Remaining
              Assert.equal [ "ITEM-4" ] progress.Unknown
              let blocked = progress.Members |> List.find (fun row -> row.WorkItemId = "ITEM-2")
              Assert.equal [ "ITEM-3"; "ITEM-4" ] blocked.Gates
              Assert.equal "1 of 4 complete (0 active, 1 blocked, 1 remaining, 0 abandoned, 1 unknown)" (GroupProgress.summary progress)
              let node = WorkGroupJson.progressNode progress
              Assert.equal false (node["complete"].GetValue<bool>()))

          t "cli show reports members' own recorded and planning states, blocked members and who they gate, and writes nothing" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] [ "--architecture-note"; "one store" ] |> ok |> ignore
                  cli clone [ "work"; "start"; "--id"; "ITEM-2"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  cli clone [ "work"; "block"; "--id"; "ITEM-2"; "--reason"; "waiting on a decision"; "--occurred-at"; now () ] |> ok |> ignore
                  let before = GitFixture.git clone [ "status"; "--porcelain"; "--untracked-files=all" ], lifecycle clone, File.ReadAllText(groupsFile clone)

                  let shown = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok
                  Assert.equal "found" (text shown.Json["status"])
                  let progress = shown.Json["progress"]
                  Assert.equal 3 (progress["total"].GetValue<int>())
                  Assert.equal false (progress["complete"].GetValue<bool>())
                  Assert.equal "ITEM-2" (text progress["blocked"].[0])
                  let members = shown.Json["members"].AsArray() |> Seq.map (fun node -> text node["workItemId"], node) |> Map.ofSeq
                  Assert.equal "blocked" (text members["ITEM-2"].["state"])
                  Assert.equal "ITEM-3" (text members["ITEM-2"].["gates"].[0])
                  Assert.equal "ITEM-2" (text members["ITEM-3"].["waitsOn"].[0])
                  Assert.isTrue (members["ITEM-3"].["planningState"] <> null) "no planning state"
                  Assert.equal "one store" (text shown.Json["group"].["architectureNotes"].[0])

                  let textView = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ok
                  Assert.isTrue (textView.Output.Contains "ITEM-2: gates ITEM-3") textView.Output
                  Assert.isTrue (textView.Output.Contains "0 of 3 complete") textView.Output
                  Assert.equal before (GitFixture.git clone [ "status"; "--porcelain"; "--untracked-files=all" ], lifecycle clone, File.ReadAllText(groupsFile clone))))

          t "cli show of an unknown group exits 1 in text and JSON; a missing ID exits 2" (fun () ->
              withRepository (fun clone ->
                  let missing = run clone None [ "work"; "group"; "show"; "GROUP-NOPE-001" ]
                  Assert.equal 1 missing.ExitCode
                  Assert.isTrue (missing.Error.Contains "unknown-group") missing.Error
                  let json = run clone None [ "work"; "group"; "show"; "GROUP-NOPE-001"; "--json" ]
                  Assert.equal 1 json.ExitCode
                  Assert.equal "not-found" (text json.Json["status"])
                  Assert.equal 2 (run clone None [ "work"; "group"; "show" ]).ExitCode
                  Assert.isTrue (not (File.Exists(groupsFile clone))) "show created groups.json"))

          t "add joins by the creation rule, refuses current members and unknown groups, and records who added the member" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1" ]

              let change (id: string) (groupId: string) : GroupMemberRequest =
                  { GroupId = groupId
                    WorkItemId = id
                    OccurredAt = "2026-09-30T13:00:00.000Z"
                    Actor = human
                    Reason = Some "same module"
                    AllowEmpty = false
                    ExecutionId = Some "EXE-CALLER"
                    Dependencies = []
                    GroupIndependent = false
                    MemberIndependent = false }

              let add groups id groupId = WorkGroups.add (context groups) (change id groupId)
              Assert.equal [ "unknown-group" ] (codes (add [ group ] "ITEM-2" "GROUP-AREA-404"))
              Assert.equal [ "invalid-group-id" ] (codes (add [ group ] "ITEM-2" "nope"))
              // A current member is an end state that already holds (PRX-GRP-114).
              match add [ group ] "ITEM-1" "GROUP-AREA-001" with
              | Ok change -> Assert.equal (false, group) (change.Changed, change.Group)
              | Error rejections -> failwith $"{rejections}"
              Assert.equal [ "unknown-member" ] (codes (add [ group ] "NOPE-1" "GROUP-AREA-001"))
              Assert.equal [ "terminal-member" ] (codes (add [ group ] "DONE-1" "GROUP-AREA-001"))
              Assert.equal [ "repository-mismatch" ] (codes (add [ group ] "ELSE-1" "GROUP-AREA-001"))
              // The planner infers an external repository from the description;
              // the join rule applies the same inference (PRX-GRP-051).
              Assert.equal [ "repository-mismatch" ] (codes (add [ group ] "AWAY-1" "GROUP-AREA-001"))
              let crossRepository = { group with Declaration = { group.Declaration with Id = "GROUP-ECHELON-AREA-001"; CrossRepository = true } }
              Assert.equal [] (codes (add [ crossRepository ] "ELSE-1" "GROUP-ECHELON-AREA-001"))

              match add [ group ] "ITEM-2" "GROUP-AREA-001" with
              | Error rejections -> failwith $"{rejections}"
              | Ok change ->
                  let updated = change.Group
                  Assert.isTrue change.Changed "the add did not change the group"
                  Assert.equal [ "ITEM-1"; "ITEM-2" ] updated.Declaration.Members
                  let entry = List.last updated.History
                  Assert.equal GroupOperation.MemberAdded entry.Operation
                  Assert.equal (Some "ITEM-2") entry.Member
                  Assert.equal human entry.Actor
                  Assert.equal (Some "EXE-CALLER") entry.ExecutionId
                  Assert.equal (Some "active") entry.MemberState
                  Assert.equal group.History (updated.History |> List.truncate 1))

          t "cli add records the member and who added it, leaves lifecycle files untouched, and the planner sees the new member" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  let before = lifecycle clone
                  let add extra = cli clone ([ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--json" ] @ extra)

                  let dryRun = add [ "--member"; "ITEM-2"; "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dryRun.Json["status"])
                  Assert.isTrue (not ((File.ReadAllText(groupsFile clone)).Contains "ITEM-2")) "a dry run wrote the member"

                  let added = add [ "--member"; "ITEM-2"; "--reason"; "same module" ] |> ok
                  let history = added.Json["group"].["history"].AsArray()
                  Assert.equal "member-added" (text history[1].["operation"])
                  Assert.equal "ITEM-2" (text history[1].["member"])
                  Assert.equal "example/agent-a" (text history[1].["actor"].["id"])
                  Assert.equal "same module" (text history[1].["reason"])
                  Assert.equal before (lifecycle clone)

                  let explained = run clone None [ "plan"; "explain-group"; "GROUP-FIXTURE-001"; "--json" ] |> ok
                  Assert.isTrue (explained.Output.Contains "\"ITEM-2\"") "the planner does not see the added member"

                  for id, code in [ "DONE-1", "terminal-member"; "GONE-1", "terminal-member"; "NOPE-1", "unknown-member" ] do
                      let refused = add [ "--member"; id ]
                      Assert.equal 1 refused.ExitCode
                      Assert.equal [ code ] (rejectionCodes refused)

                  Assert.equal 2 (add [ "--member"; "ITEM-3"; "--member"; "ITEM-4" ]).ExitCode
                  Assert.equal 1 (cli clone [ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-404"; "--member"; "ITEM-3"; "--occurred-at"; now () ]).ExitCode
                  run clone None [ "validate" ] |> ok |> ignore))

          t "cli add refuses an item that executes in another repository unless the group is cross-repository" (fun () ->
              withRepository (fun clone ->
                  let configuration = Path.Combine(clone, "..", "planner.json")
                  File.WriteAllText(configuration, """{"grouping":{"executionRepositories":{"ITEM-3":"conditor"}}}""")
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  createGroup clone "GROUP-ECHELON-FIXTURE-002" [ "ITEM-2" ] [ "--cross-repository"; "--home-repository"; "owner/home" ] |> ok |> ignore
                  let add groupId = cli clone [ "work"; "group"; "add"; "--group"; groupId; "--member"; "ITEM-3"; "--config"; configuration; "--occurred-at"; now (); "--json" ]
                  let refused = add "GROUP-FIXTURE-001"
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "repository-mismatch" ] (rejectionCodes refused)
                  add "GROUP-ECHELON-FIXTURE-002" |> ok |> ignore))

          t "remove refuses non-members and the last member unless explicit; any member's state may leave; history records who removed it" (fun () ->
              let joined = created "GROUP-AREA-001" [ "ITEM-1" ]
              // DONE-1 completed after it joined.
              let group = { joined with Declaration = { joined.Declaration with Members = [ "ITEM-1"; "DONE-1" ] } }

              let change (id: string) (allowEmpty: bool) : GroupMemberRequest =
                  { GroupId = "GROUP-AREA-001"
                    WorkItemId = id
                    OccurredAt = "2026-09-30T13:00:00.000Z"
                    Actor = human
                    Reason = Some "not part of this design"
                    AllowEmpty = allowEmpty
                    ExecutionId = None
                    Dependencies = []
                    GroupIndependent = false
                    MemberIndependent = false }

              let remove (target: StoredWorkGroup) id allowEmpty = WorkGroups.remove (context [ target ]) (change id allowEmpty)
              Assert.equal [ "not-member" ] (codes (remove group "ITEM-2" false))
              Assert.equal [ "unknown-group" ] (codes (WorkGroups.remove (context []) (change "ITEM-1" false)))

              let withoutDone =
                  match remove group "DONE-1" false with
                  | Ok change -> change.Group
                  | Error rejections -> failwith $"{rejections}"

              Assert.equal [ "ITEM-1" ] withoutDone.Declaration.Members
              let entry = List.last withoutDone.History
              Assert.equal (GroupOperation.MemberRemoved, Some "DONE-1", human, false) (entry.Operation, entry.Member, entry.Actor, entry.ExplicitEmpty)
              Assert.equal [ "last-member" ] (codes (remove withoutDone "ITEM-1" false))

              match remove withoutDone "ITEM-1" true with
              | Error rejections -> failwith $"{rejections}"
              | Ok { Group = empty } ->
                  Assert.equal [] empty.Declaration.Members
                  Assert.isTrue (List.last empty.History).ExplicitEmpty "the empty removal is not explicit"
                  Assert.empty (WorkGroups.findings (context [ empty ])))

          t "cli remove leaves every member's lifecycle, evidence and attribution untouched and records who removed it" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2" ] [] |> ok |> ignore
                  let before = lifecycle clone
                  let remove extra = cli clone ([ "work"; "group"; "remove"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--json" ] @ extra)

                  let dryRun = remove [ "--member"; "ITEM-2"; "--dry-run" ] |> ok
                  Assert.equal "dry-run" (text dryRun.Json["status"])
                  Assert.isTrue ((File.ReadAllText(groupsFile clone)).Contains "ITEM-2") "a dry run removed the member"

                  let refused = remove [ "--member"; "ITEM-3" ]
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "not-member" ] (rejectionCodes refused)

                  let removed = remove [ "--member"; "ITEM-2"; "--reason"; "belongs elsewhere" ] |> ok
                  let entry = removed.Json["group"].["history"].AsArray() |> Seq.last
                  Assert.equal "member-removed" (text entry["operation"])
                  Assert.equal "ITEM-2" (text entry["member"])
                  Assert.equal "example/agent-a" (text entry["actor"].["id"])
                  Assert.equal before (lifecycle clone)

                  let last = remove [ "--member"; "ITEM-1" ]
                  Assert.equal 1 last.ExitCode
                  Assert.equal [ "last-member" ] (rejectionCodes last)
                  let emptied = remove [ "--member"; "ITEM-1"; "--allow-empty" ] |> ok
                  Assert.equal 0 (emptied.Json["group"].["members"].AsArray().Count)
                  Assert.equal true ((emptied.Json["group"].["history"].AsArray() |> Seq.last).["explicitEmpty"].GetValue<bool>())
                  Assert.equal before (lifecycle clone)
                  run clone None [ "validate" ] |> ok |> ignore
                  Assert.equal 2 (cli clone [ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-1"; "--allow-empty"; "--occurred-at"; now () ]).ExitCode))

          t "a group checkpoint needs a recorded group, text and the shared durability verdict; it references member checkpoints and changes no member" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3" ]
              let commit = CommitId.tryParse (String.replicate 40 "a") |> Option.get

              let location: GitDurableLocation =
                  { Repository = "this-repository"
                    Branch = "feature/x"
                    LocalCommit = commit
                    Remote = { Name = "origin"; Url = None }
                    RemoteBranch = "feature/x"
                    RemoteCommit = commit }

              let request: GroupCheckpointRequest =
                  { GroupId = "GROUP-AREA-001"
                    CheckpointId = "gcp-1"
                    Summary = "store and create done"
                    NextAction = "implement show"
                    Decisions = [ "one store file" ]
                    OccurredAt = "2026-09-30T14:00:00.000Z"
                    Actor = human }

              let facts id = MemberFacts.ofStanding (fun candidate -> standings |> Map.tryFind candidate |> Option.defaultValue MemberStanding.Unknown) id
              let reference id = if id = "ITEM-2" then Some { WorkItemId = id; CheckpointId = "cp-2"; Commit = String.replicate 40 "b" } else None
              let own id = if id = "ITEM-2" then ExecutionObservation.Resolved("EXE-1", []) else ExecutionObservation.NoneActive
              let decideAs execution groups request location = WorkGroups.checkpoint (context groups) request facts execution reference location
              let decide = decideAs own
              let codes result = match result with Ok _ -> [] | Error rejections -> rejections |> List.map GroupCheckpointRejection.code

              Assert.equal [ "unknown-group" ] (codes (decide [] request (Ok location)))
              // The ownership half of `work checkpoint`'s rule: an active member,
              // under the caller's own execution.
              let idle = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-3" ]
              Assert.equal [ "no-active-member" ] (codes (decide [ idle ] request (Ok location)))
              Assert.equal [ "no-own-execution" ] (codes (decideAs (fun _ -> ExecutionObservation.NoneActive) [ group ] request (Ok location)))
              Assert.equal [ "blank-summary"; "local-ahead" ] (codes (decide [ group ] { request with Summary = " " } (Error [ CheckpointRejection.LocalAhead(1, { Name = "origin"; Url = None }, "feature/x") ])))

              match decide [ group ] request (Ok location) with
              | Error rejections -> failwith $"{rejections}"
              | Ok(updated, recorded) ->
                  Assert.equal [ recorded ] updated.Checkpoints
                  Assert.equal (group.Declaration, group.History) (updated.Declaration, updated.History)
                  Assert.equal ([], [ "ITEM-2" ], [ "ITEM-3" ], [ "ITEM-1" ]) (recorded.Completed, recorded.Active, recorded.Blocked, recorded.Remaining)
                  Assert.equal [ "cp-2" ] (recorded.MemberCheckpoints |> List.map (fun item -> item.CheckpointId))
                  let rendered = WorkGroupJson.renderStore [ updated ]
                  Assert.equal (Ok [ updated ]) (WorkGroupJson.readStore rendered)

                  // `validate` re-checks what a stored checkpoint references.
                  let owners id = if id = "cp-2" then Some "ITEM-2" else None
                  Assert.equal [] (WorkGroups.checkpointFindings owners [ updated ])
                  let messages groups owners = WorkGroups.checkpointFindings owners groups |> List.map (fun (_, _, message) -> message)
                  Assert.isTrue ((messages [ updated ] (fun _ -> None)).Head.Contains "not a recorded work checkpoint") "an unknown member checkpoint was accepted"
                  Assert.isTrue ((messages [ updated ] (fun _ -> Some "ITEM-1")).Head.Contains "ITEM-1 recorded it") "another member's checkpoint was accepted"
                  let other = CommitId.tryParse (String.replicate 40 "c") |> Option.get
                  let undurable = { recorded with Location = { recorded.Location with RemoteCommit = other } }
                  let tampered = { updated with Checkpoints = [ undurable; undurable ] }
                  Assert.equal 3 (messages [ tampered ] owners).Length)

          t "cli group checkpoint is refused when no member is active, as work checkpoint is for an inactive item" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2" ] [] |> ok |> ignore
                  pushAll clone "declare the group" |> ignore
                  let before = File.ReadAllText(groupsFile clone)

                  let refused =
                      cli clone [ "work"; "group"; "checkpoint"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "nothing yet"; "--next-action"; "start"; "--json" ]

                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "no-active-member" ] (rejectionCodes refused)
                  Assert.equal before (File.ReadAllText(groupsFile clone))))

          t "cli group checkpoint is refused on unpushed or dirty state and records a verified checkpoint over members' own, changing none" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2"; "ITEM-3" ] [] |> ok |> ignore
                  cli clone [ "work"; "start"; "--id"; "ITEM-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: first slice" |> ignore
                  cli clone [ "work"; "checkpoint"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--summary"; "first slice"; "--next-action"; "second slice" ] |> ok |> ignore
                  // The member's own recorded checkpoint and history (not its
                  // live freshness, which later commits legitimately change).
                  let memberCheckpoint () =
                      let shown = (run clone None [ "work"; "checkpoint"; "show"; "ITEM-1"; "--json" ] |> ok).Json
                      text shown["continuity"].["checkpoint"].["id"], shown["history"].ToJsonString()
                  let memberBefore = memberCheckpoint ()
                  let eventsBefore = File.ReadAllText(Path.Combine(clone, ".ros", "events", "events.jsonl"))

                  let checkpoint extra =
                      cli clone ([ "work"; "group"; "checkpoint"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "store done"; "--next-action"; "show next"; "--decision"; "one store file"; "--json" ] @ extra)

                  GitFixture.write clone "src/two.txt" "two\n"
                  let dirty = checkpoint []
                  Assert.equal 1 dirty.ExitCode
                  Assert.isTrue (rejectionCodes dirty |> List.contains "uncommitted-changes") dirty.Output
                  GitFixture.commitAll clone "unpushed work" |> ignore
                  let unpushed = checkpoint []
                  Assert.equal 1 unpushed.ExitCode
                  Assert.equal [ "local-ahead" ] (rejectionCodes unpushed)
                  Assert.isTrue (not ((File.ReadAllText(groupsFile clone)).Contains "gcp-")) "a refused checkpoint was written"
                  GitFixture.git clone [ "push"; "-q" ] |> ignore
                  let head = GitFixture.git clone [ "rev-parse"; "HEAD" ]

                  let recorded = checkpoint [] |> ok
                  let node = recorded.Json["checkpoint"]
                  Assert.equal head (text node["location"].["commit"])
                  Assert.equal head (text node["location"].["remoteCommit"])
                  Assert.equal "verified" (text node["verification"].["status"])
                  Assert.equal "ITEM-1" (text node["members"].["active"].[0])
                  Assert.equal 2 (node["members"].["remaining"].AsArray().Count)
                  Assert.equal "ITEM-1" (text node["memberCheckpoints"].[0].["workItemId"])
                  Assert.equal "one store file" (text node["decisions"].[0])
                  Assert.equal "example/agent-a" (text node["actor"].["id"])
                  Assert.isTrue (isNull node["paths"] && isNull node["executionId"]) "a group checkpoint claimed paths or an execution"
                  Assert.equal memberBefore (memberCheckpoint ())
                  Assert.equal eventsBefore (File.ReadAllText(Path.Combine(clone, ".ros", "events", "events.jsonl")))

                  let shown = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ok
                  Assert.isTrue (shown.Output.Contains "LATEST GROUP CHECKPOINT" && shown.Output.Contains "decision: one store file") shown.Output
                  run clone None [ "validate" ] |> ok |> ignore
                  Assert.equal 2 (cli clone [ "work"; "group"; "checkpoint"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now () ]).ExitCode
                  Assert.equal 1 (cli clone [ "work"; "group"; "checkpoint"; "--group"; "GROUP-FIXTURE-404"; "--occurred-at"; now (); "--summary"; "s"; "--next-action"; "n" ]).ExitCode))

          // ---- PRX-GRP-110..116 (PRAXIS-GROUP-08; PRX-GRP-190 cases 27-32) ----

          t "derived status follows the PRX-GRP-103 precedence and no stored field sets it" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2" ]

              let statusOf (states: (string option * string list) list) =
                  let facts =
                      List.zip [ "ITEM-1"; "ITEM-2" ] states
                      |> List.map (fun (id, (state, waitsOnBlocked)) -> id, { State = state; PlanningState = None; WaitsOn = waitsOnBlocked; WaitsOnBlocked = waitsOnBlocked; Observation = None; Stale = false })
                      |> Map.ofList

                  GroupStatus.code (WorkGroups.summarize group (fun id -> facts[id])).Status

              Assert.equal "complete" (statusOf [ Some "complete", []; Some "complete", [] ])
              Assert.equal "unknown" (statusOf [ Some "complete", []; None, [] ])
              Assert.equal "partially-complete" (statusOf [ Some "complete", []; Some "blocked", [] ])
              Assert.equal "partially-complete" (statusOf [ Some "complete", []; Some "abandoned", [] ])
              Assert.equal "blocked" (statusOf [ Some "blocked", []; Some "ready", [ "OUTSIDE-1" ] ])
              Assert.equal "active" (statusOf [ Some "blocked", []; Some "active", [] ])
              Assert.equal "not-started" (statusOf [ Some "ready", []; Some "captured", [] ])
              let empty = { group with Declaration = { group.Declaration with Members = [] } }
              Assert.equal GroupStatus.NotStarted (WorkGroups.summarize empty (fun _ -> failwith "no member")).Status
              let stored = (WorkGroupJson.groupNode group).ToJsonString()
              Assert.isTrue (not (stored.Contains "groupStatus") && not (stored.Contains "\"status\"")) "a status was stored in the group record")

          t "removed-open members are reported with their removal reason; a member removed after completing is not" (fun () ->
              // DONE-1 completed after it joined.
              let joined = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2" ]
              let group = { joined with Declaration = { joined.Declaration with Members = [ "ITEM-1"; "ITEM-2"; "DONE-1" ] } }

              let change (id: string) : GroupMemberRequest =
                  { GroupId = "GROUP-AREA-001"
                    WorkItemId = id
                    OccurredAt = "2026-09-30T13:00:00.000Z"
                    Actor = human
                    Reason = Some $"{id} moved elsewhere"
                    AllowEmpty = false
                    ExecutionId = None
                    Dependencies = []
                    GroupIndependent = false
                    MemberIndependent = false }

              let removeFrom (target: StoredWorkGroup) id =
                  match WorkGroups.remove (context [ target ]) (change id) with
                  | Ok change -> change.Group
                  | Error rejections -> failwith $"{rejections}"

              let after = removeFrom (removeFrom group "ITEM-2") "DONE-1"
              let removed = WorkGroups.removedOpen after (fun id -> MemberStanding.state ((context []).Standing id))
              Assert.equal [ "ITEM-2" ] (removed |> List.map (fun row -> row.WorkItemId))
              Assert.equal (Some "ITEM-2 moved elsewhere") removed.Head.Reason
              Assert.equal (Some "active") removed.Head.StateAtRemoval
              let node = WorkGroupJson.summaryNode (WorkGroups.summarize after (MemberFacts.ofStanding (fun id -> (context []).Standing id)))
              Assert.equal "ITEM-2" (text node["removedOpen"].[0].["workItemId"]))

          t "repeated identical create, add and remove are unchanged and append nothing; conflicting requests are refused" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2" ]

              match WorkGroups.create (context [ group ]) (request "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2" ]) with
              | Ok change -> Assert.equal (false, group) (change.Changed, change.Group)
              | Error rejections -> failwith $"{rejections}"

              Assert.equal [ "duplicate-group" ] (codes (WorkGroups.create (context [ group ]) (request "GROUP-AREA-001" [ "ITEM-1" ])))
              Assert.equal [ "duplicate-group" ] (codes (WorkGroups.create (context [ group ]) { request "GROUP-AREA-001" [ "ITEM-1"; "ITEM-2" ] with Origin = GroupOrigin.ArchitectureDeclared }))

              let removal: GroupMemberRequest =
                  { GroupId = "GROUP-AREA-001"; WorkItemId = "ITEM-2"; OccurredAt = "2026-09-30T13:00:00.000Z"; Actor = human; Reason = None; AllowEmpty = false; ExecutionId = None; Dependencies = []; GroupIndependent = false; MemberIndependent = false }

              let once =
                  match WorkGroups.remove (context [ group ]) removal with
                  | Ok change -> change.Group
                  | Error rejections -> failwith $"{rejections}"

              match WorkGroups.remove (context [ once ]) removal with
              | Ok change -> Assert.equal (false, once.History.Length) (change.Changed, change.Group.History.Length)
              | Error rejections -> failwith $"{rejections}"

              Assert.equal [ "not-member" ] (codes (WorkGroups.remove (context [ once ]) { removal with WorkItemId = "ITEM-3" })))

          t "history is append-only: a truncated, reordered or rewritten history or a vanished group is reported" (fun () ->
              let group = created "GROUP-AREA-001" [ "ITEM-1" ]

              let added =
                  match WorkGroups.add (context [ group ]) { GroupId = "GROUP-AREA-001"; WorkItemId = "ITEM-2"; OccurredAt = "2026-09-30T13:00:00.000Z"; Actor = human; Reason = None; AllowEmpty = false; ExecutionId = None; Dependencies = []; GroupIndependent = false; MemberIndependent = false } with
                  | Ok change -> change.Group
                  | Error rejections -> failwith $"{rejections}"

              Assert.empty (WorkGroups.historyFindings "HEAD" [ group ] [ added ])
              Assert.empty (WorkGroups.historyFindings "HEAD" [ added ] [ added ])
              let messages committed current = WorkGroups.historyFindings "HEAD" committed current |> List.map (fun (_, _, message) -> message)
              Assert.isTrue ((messages [ added ] [ group ]).Head.Contains "truncated") "truncation was not reported"
              let reordered = { added with History = List.rev added.History }
              Assert.isTrue ((messages [ added ] [ reordered ]).Head.Contains "rewritten or reordered") "reordering was not reported"
              let rewritten = { added with History = added.History |> List.map (fun entry -> { entry with Reason = Some "edited" }) }
              Assert.isTrue ((messages [ added ] [ rewritten ]).Head.Contains "rewritten") "a rewrite was not reported"
              Assert.isTrue ((messages [ added ] []).Head.Contains "no longer in the store") "a vanished group was not reported")

          t "a version-1 store reads unchanged and is written as version 2 only by a mutation, history intact; list rows round-trip" (fun () ->
              let v1 =
                  """{"schemaVersion":1,"groups":[{"id":"GROUP-A-001","members":["ITEM-1"],"kind":null,"origin":"human-declared","sharedContext":[],"executionRepository":"this-repository","crossRepository":false,"architectureNotes":[],"createdAt":"2026-09-30T12:00:00.000Z","createdBy":{"kind":"human","id":"owner"},"history":[{"operation":"created","member":null,"at":"2026-09-30T12:00:00.000Z","actor":{"kind":"human","id":"owner"},"reason":null}],"checkpoints":[]}]}"""

              match WorkGroupJson.readStore v1 with
              | Error message -> failwith message
              | Ok groups ->
                  let entry = Assert.single groups.Head.History
                  Assert.equal (None, None) (entry.ExecutionId, entry.MemberState)
                  let rewritten = WorkGroupJson.renderStore groups
                  Assert.isTrue (rewritten.Contains "\"schemaVersion\": 2") rewritten
                  Assert.equal (Ok groups) (WorkGroupJson.readStore rewritten)
                  Assert.empty (WorkGroups.historyFindings "HEAD" groups (Result.defaultValue [] (WorkGroupJson.readStore rewritten)))

              Assert.isTrue (WorkGroupJson.readStore """{"schemaVersion":3,"groups":[]}""" |> Result.isError) "an unknown store version was read"
              let joined = created "GROUP-AREA-001" [ "ITEM-1" ]
              let withDone = { joined with Declaration = { joined.Declaration with Members = [ "ITEM-1"; "DONE-1" ] } }
              let summary = WorkGroups.summarize withDone (MemberFacts.ofStanding (context []).Standing)
              let node = (WorkGroupJson.summaryNode summary).AsObject()

              match WorkGroupJson.readSummary node with
              | Error problems -> failwith $"{problems}"
              | Ok read ->
                  Assert.equal ("GROUP-AREA-001", GroupStatus.PartiallyComplete, [ "DONE-1" ], 2, None) (read.Id, read.GroupStatus, read.Completed, read.MemberCount, read.ExecutionMode)
                  Assert.equal (Some "owner/this") read.HomeRepository
                  Assert.equal (Some "this-repository") read.ExecutionRepository)

          t "cli list is read-only, sorted by ID, reports derived status and progress, and filters by status, member and repository" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-002" [ "ITEM-2"; "ITEM-3" ] [] |> ok |> ignore
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [ "--kind"; "shared-files" ] |> ok |> ignore
                  cli clone [ "work"; "start"; "--id"; "ITEM-2"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  let before = lifecycle clone, File.ReadAllText(groupsFile clone), GitFixture.git clone [ "status"; "--porcelain"; "--untracked-files=all" ]
                  let list extra = run clone None ([ "work"; "group"; "list"; "--json" ] @ extra) |> ok
                  let ids (result: Result) = result.Json["groups"].AsArray() |> Seq.map (fun node -> text node["id"]) |> Seq.toList

                  let all = list []
                  Assert.equal "work group list" (text all.Json["command"])
                  Assert.equal "listed" (text all.Json["status"])
                  Assert.equal [ "GROUP-FIXTURE-001"; "GROUP-FIXTURE-002" ] (ids all)
                  let second = all.Json["groups"].[1]
                  Assert.equal "active" (text second["groupStatus"])
                  Assert.equal 2 (second["memberCount"].GetValue<int>())
                  Assert.equal "shared-files" (text all.Json["groups"].[0].["kind"])
                  Assert.isTrue (isNull second["executionMode"] && isNull second["latestCheckpointAt"]) "an unknown value was not null"
                  Assert.equal [ "GROUP-FIXTURE-002" ] (ids (list [ "--status"; "active" ]))
                  Assert.equal [ "GROUP-FIXTURE-001" ] (ids (list [ "--status"; "not-started" ]))
                  Assert.equal [ "GROUP-FIXTURE-002" ] (ids (list [ "--member"; "ITEM-3" ]))
                  Assert.equal [] (ids (list [ "--repository"; "elsewhere" ]))
                  Assert.equal 2 (ids (list [ "--repository"; text second["executionRepository"] ])).Length
                  let textView = run clone None [ "work"; "group"; "list" ] |> ok
                  Assert.isTrue (textView.Output.Contains "GROUP-FIXTURE-002" && textView.Output.Contains "0 of 2 complete") textView.Output
                  Assert.equal 2 (run clone None [ "work"; "group"; "list"; "--status"; "finished" ]).ExitCode
                  Assert.equal before (lifecycle clone, File.ReadAllText(groupsFile clone), GitFixture.git clone [ "status"; "--porcelain"; "--untracked-files=all" ])))

          t "cli repeats are idempotent (changed:false, exit 0, no history) and record the caller's execution; conflicts exit 1" (fun () ->
              withRepository (fun clone ->
                  cli clone [ "work"; "backlog-transition"; "--id"; "ITEM-4"; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
                  cli clone [ "work"; "start"; "--id"; "ITEM-4"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                  let first = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2" ] [] |> ok
                  Assert.equal true (first.Json["changed"].GetValue<bool>())
                  let execution = text first.Json["group"].["history"].[0].["executionId"]
                  Assert.isTrue (execution.StartsWith "EXE-") $"no caller execution recorded: {execution}"
                  let stored () = File.ReadAllText(groupsFile clone)
                  let afterCreate = stored ()

                  let again = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2" ] [] |> ok
                  Assert.equal ("unchanged", false) (text again.Json["status"], again.Json["changed"].GetValue<bool>())
                  Assert.equal afterCreate (stored ())

                  let member' verb id extra = cli clone ([ "work"; "group"; verb; "--group"; "GROUP-FIXTURE-001"; "--member"; id; "--occurred-at"; now (); "--json" ] @ extra)
                  let repeatAdd = member' "add" "ITEM-1" [] |> ok
                  Assert.equal false (repeatAdd.Json["changed"].GetValue<bool>())
                  Assert.equal afterCreate (stored ())
                  member' "remove" "ITEM-2" [ "--reason"; "split out" ] |> ok |> ignore
                  let afterRemove = stored ()
                  let repeatRemove = member' "remove" "ITEM-2" [] |> ok
                  Assert.equal false (repeatRemove.Json["changed"].GetValue<bool>())
                  Assert.equal afterRemove (stored ())
                  let removedOpen = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok
                  Assert.equal "ITEM-2" (text removedOpen.Json["removedOpen"].[0].["workItemId"])
                  Assert.equal "split out" (text removedOpen.Json["removedOpen"].[0].["reason"])

                  let conflict = createGroup clone "GROUP-FIXTURE-001" [ "ITEM-3" ] []
                  Assert.equal 1 conflict.ExitCode
                  Assert.equal [ "duplicate-group" ] (rejectionCodes conflict)
                  Assert.equal 1 (member' "remove" "ITEM-3" []).ExitCode
                  Assert.equal afterRemove (stored ())))

          t "cli no group command alters a member's lifecycle; there is no group completion transition" (fun () ->
              withRepository (fun clone ->
                  let before = lifecycle clone
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1"; "ITEM-2" ] [] |> ok |> ignore
                  cli clone [ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-3"; "--occurred-at"; now () ] |> ok |> ignore
                  cli clone [ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-3"; "--occurred-at"; now () ] |> ok |> ignore
                  cli clone [ "work"; "group"; "remove"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-3"; "--occurred-at"; now (); "--dry-run" ] |> ok |> ignore
                  cli clone [ "work"; "group"; "remove"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-3"; "--occurred-at"; now () ] |> ok |> ignore
                  run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ok |> ignore
                  run clone None [ "work"; "group"; "list" ] |> ok |> ignore
                  cli clone [ "work"; "group"; "checkpoint"; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "s"; "--next-action"; "n" ] |> ignore
                  Assert.equal before (lifecycle clone)
                  let stored = File.ReadAllText(groupsFile clone)

                  for verb in [ "complete"; "done"; "finish" ] do
                      let refused = cli clone [ "work"; "group"; verb; "--group"; "GROUP-FIXTURE-001"; "--occurred-at"; now () ]
                      Assert.isTrue (refused.ExitCode <> 0) $"work group {verb} was accepted"

                  Assert.equal (before, stored) (lifecycle clone, File.ReadAllText(groupsFile clone))))

          t "cli validate refuses a group history rewritten or truncated relative to the committed store" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  cli clone [ "work"; "group"; "add"; "--group"; "GROUP-FIXTURE-001"; "--member"; "ITEM-2"; "--occurred-at"; now (); "--reason"; "same module" ] |> ok |> ignore
                  pushAll clone "record the group" |> ignore
                  run clone None [ "validate" ] |> ok |> ignore
                  let file = groupsFile clone
                  let committed = File.ReadAllText file
                  File.WriteAllText(file, committed.Replace("same module", "rewritten reason"))
                  let rewritten = run clone None [ "validate" ]
                  Assert.equal 1 rewritten.ExitCode
                  Assert.isTrue (rewritten.Error.Contains "append-only") rewritten.Error
                  let node = JsonNode.Parse(committed)
                  let history = node["groups"].[0].["history"].AsArray()
                  history.RemoveAt(history.Count - 1)
                  node["groups"].[0].["members"].AsArray().RemoveAt(1)
                  File.WriteAllText(file, node.ToJsonString())
                  let truncated = run clone None [ "validate" ]
                  Assert.equal 1 truncated.ExitCode
                  Assert.isTrue (truncated.Error.Contains "truncated") truncated.Error
                  File.WriteAllText(file, committed)
                  run clone None [ "validate" ] |> ok |> ignore))

          // ---- PRX-GRP-100..109 (PRAXIS-GROUP-07; PRX-GRP-190 cases 21-26) ----

          t "a cross-repository group needs GROUP-ECHELON- and a home; a local group may not use it or name another repository's item" (fun () ->
              let cross id members = { request id members with CrossRepository = true; HomeRepository = Some "owner/this" }
              Assert.equal [ "cross-repository-id" ] (codes (WorkGroups.create (context []) (cross "GROUP-AREA-001" [ "ITEM-1" ])))
              Assert.equal [ "reserved-area" ] (codes (WorkGroups.create (context []) (request "GROUP-ECHELON-AREA-001" [ "ITEM-1" ])))
              Assert.equal [ "foreign-member" ] (codes (WorkGroups.create (context []) (request "GROUP-AREA-001" [ "ITEM-1"; "owner/other:OPEN-1" ])))
              Assert.equal [ "home-repository-unknown" ] (codes (WorkGroups.create { context [] with ThisRepository = None } { cross "GROUP-ECHELON-AREA-001" [ "ITEM-1" ] with HomeRepository = None }))
              Assert.equal [ "invalid-member-id" ] (codes (WorkGroups.create (context []) (cross "GROUP-ECHELON-AREA-001" [ "owner/other:not an id" ])))

              match WorkGroups.create (context []) (cross "GROUP-ECHELON-AREA-001" [ "owner/this:ITEM-1"; "owner/other:OPEN-1" ]) with
              | Error rejections -> failwith $"{rejections}"
              | Ok change ->
                  // A qualified name of the home is the bare ID: one spelling per member.
                  Assert.equal [ "ITEM-1"; "owner/other:OPEN-1" ] change.Group.Declaration.Members
                  Assert.equal (Some "owner/this") change.Group.HomeRepository
                  let verification = Assert.single change.Group.Verifications
                  Assert.equal (ObservedMember.Read(Some "ready")) verification.Outcome
                  Assert.empty change.Warnings
                  let rendered = WorkGroupJson.renderStore [ change.Group ]
                  Assert.equal (Ok [ change.Group ]) (WorkGroupJson.readStore rendered))

          t "a qualified member is checked by observation: terminal or unknown there is refused, unobservable joins verified false with a warning" (fun () ->
              let cross members = { request "GROUP-ECHELON-AREA-001" members with CrossRepository = true; HomeRepository = Some "owner/this" }
              Assert.equal [ "terminal-member"; "unknown-member" ] (codes (WorkGroups.create (context []) (cross [ "owner/other:SHUT-1"; "owner/other:NOPE-1" ])))

              match WorkGroups.create (context []) (cross [ "ITEM-1"; "owner/offline:WORK-1" ]) with
              | Error rejections -> failwith $"{rejections}"
              | Ok change ->
                  Assert.isTrue (change.Warnings.Head.Contains "verified: false") $"{change.Warnings}"
                  let node = (WorkGroupJson.groupNode change.Group).ToJsonString()
                  Assert.isTrue (node.Contains "\"verified\":false" && node.Contains "\"code\":\"unreachable\"") node)

          t "status is derived from dated observations; an unobservable or stale member is unknown, never complete" (fun () ->
              let group =
                  match WorkGroups.create (context []) { request "GROUP-ECHELON-AREA-001" [ "owner/other:OPEN-1" ] with CrossRepository = true; HomeRepository = Some "owner/this" } with
                  | Ok change -> { change.Group with Declaration = { change.Group.Declaration with Members = [ "owner/other:SHUT-1"; "owner/offline:WORK-1" ] } }
                  | Error rejections -> failwith $"{rejections}"

              let now = DateTimeOffset.Parse "2026-10-06T13:00:00.000Z"
              let facts (observer: string -> string -> MemberObservation) (memberId: string) =
                  match MemberReference.parse memberId with
                  | MemberReference.Qualified(repository, id) -> MemberFacts.ofObservation now 1440 (observer repository id)
                  | MemberReference.Local id -> MemberFacts.ofStanding (context []).Standing id

              let summary = WorkGroups.summarize group (facts observe)
              Assert.equal GroupStatus.Unknown summary.Status
              let offline = summary.Progress.Members |> List.find (fun row -> row.WorkItemId = "owner/offline:WORK-1")
              Assert.equal (MemberCategory.Unknown, Some "owner/offline") (offline.Category, offline.Repository)

              // Every member observed complete, but one observation is stale.
              let allComplete = { group with Declaration = { group.Declaration with Members = [ "owner/other:SHUT-1" ] } }
              Assert.equal GroupStatus.Complete (WorkGroups.summarize allComplete (facts observe)).Status
              let old repository id = { observe repository id with SourceAsOf = Some "2026-10-01T00:00:00.000Z" }
              let stale = WorkGroups.summarize allComplete (facts old)
              Assert.equal GroupStatus.Unknown stale.Status
              Assert.isTrue (stale.Progress.Members.Head.Stale) "a stale observation was not marked stale"
              Assert.equal [ { Repository = "owner/other"; Completed = 0; Total = 1; Unknown = 1 } ] (WorkGroups.repositoryProgress stale.Progress))

          t "dependency edges order members across repositories, unmet or unobservable producers leave consumers waiting or unknown, and cycles are refused" (fun () ->
              let cross deps = { request "GROUP-ECHELON-AREA-001" [ "ITEM-1"; "owner/other:OPEN-1"; "owner/other:OPEN-2" ] with CrossRepository = true; HomeRepository = Some "owner/this"; Dependencies = deps }
              let edge text' = (MemberDependency.tryParse text').Value
              Assert.equal (Some { Consumer = "ITEM-1"; Producer = "owner/other:OPEN-1"; Milestone = ProducerMilestone.Released "v0.8.0" }) (MemberDependency.tryParse "ITEM-1=owner/other:OPEN-1@released:v0.8.0")
              Assert.equal None (MemberDependency.tryParse "ITEM-1=owner/other:OPEN-1@shipped")
              let cycle = [ edge "ITEM-1=owner/other:OPEN-1"; edge "owner/other:OPEN-1=owner/other:OPEN-2@merged"; edge "owner/other:OPEN-2=ITEM-1" ]
              Assert.equal [ "dependency-cycle" ] (codes (WorkGroups.create (context []) (cross cycle)))
              Assert.equal [ "dependency-not-member" ] (codes (WorkGroups.create (context []) (cross [ edge "ITEM-2=ITEM-1" ])))

              match WorkGroups.create (context []) (cross [ edge "ITEM-1=owner/other:OPEN-1@released:v0.8.0"; edge "owner/other:OPEN-2=ITEM-1@merged"; edge "owner/other:OPEN-2=owner/other:OPEN-1" ]) with
              | Error rejections -> failwith $"{rejections}"
              | Ok change ->
                  let reached producer milestone =
                      match producer, milestone with
                      | "owner/other:OPEN-1", ProducerMilestone.Released _ -> Ok true
                      | "ITEM-1", ProducerMilestone.Merged -> Ok false
                      | _ -> Error "owner/other could not be observed"

                  let order = WorkGroups.order change.Group reached
                  Assert.equal [ "satisfied"; "waiting"; "unknown" ] (order |> List.map (fun row -> EdgeState.code row.State))
                  Assert.equal None (WorkGroups.consumerState order "ITEM-1")
                  Assert.equal (Some "waiting") (WorkGroups.consumerState order "owner/other:OPEN-2"))

          t "link records only an immutable reference on the member's own item; repeats are unchanged and a different home is refused" (fun () ->
              let store = { Groups = []; References = [] }

              let linkRequest home : GroupLinkRequest =
                  { GroupId = "GROUP-ECHELON-AREA-001"; HomeRepository = home; WorkItemId = "ITEM-1"; OccurredAt = "2026-10-06T12:00:00.000Z"; Actor = human; ExecutionId = None }

              let linked =
                  match WorkGroups.link (context []) store (linkRequest "owner/home") with
                  | Ok change -> Assert.isTrue change.Changed "the first link did not change the store"; change.Store
                  | Error rejections -> failwith $"{rejections}"

              Assert.equal ([], 1) (linked.Groups, linked.References.Length)

              match WorkGroups.link (context []) linked (linkRequest "owner/home") with
              | Ok change -> Assert.equal (false, linked) (change.Changed, change.Store)
              | Error rejections -> failwith $"{rejections}"

              let refused request = codes (WorkGroups.link (context []) linked request |> Result.map (fun change -> change.Store))
              Assert.equal [ "conflicting-reference" ] (refused (linkRequest "owner/elsewhere"))
              Assert.equal [ "home-is-this-repository" ] (refused { linkRequest "owner/this" with WorkItemId = "ITEM-2" })
              Assert.equal [ "cross-repository-id" ] (refused { linkRequest "owner/home" with GroupId = "GROUP-AREA-001" })
              Assert.equal [ "unknown-member" ] (refused { linkRequest "owner/home" with WorkItemId = "NOPE-1" })
              let unlinked = WorkGroups.unlinkedReferences linked.References (Some "owner/this") (fun _ -> Some false)
              Assert.isTrue ((snd unlinked.Head).Contains "unlinked") $"{unlinked}"
              Assert.empty (WorkGroups.unlinkedReferences linked.References (Some "owner/this") (fun _ -> None))
              let rendered = WorkGroupJson.renderGroupStore linked
              Assert.equal (Ok linked) (WorkGroupJson.readGroupStore rendered))

          t "cli cross-repository group observes a member repository read-only, reports unlinked until link, and never writes there" (fun () ->
              let homeParent = GitFixture.temporaryDirectory "work-group-home"
              let memberParent = GitFixture.temporaryDirectory "work-group-member"

              try
                  let _, home = installedRepository homeParent "home"
                  let _, memberClone = installedRepository memberParent "member"
                  let repositoryOf clone = (RepositoryName.ofRemoteUrl (GitFixture.git clone [ "remote"; "get-url"; "origin" ])).Value
                  let homeName, memberName = repositoryOf home, repositoryOf memberClone

                  for clone, id in [ home, "HOME-1"; memberClone, "MEMB-1"; memberClone, "MEMB-2" ] do
                      cli clone [ "add"; $"Work {id}"; "--id"; id ] |> ok |> ignore
                      cli clone [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore

                  pushAll memberClone "member work" |> ignore
                  let rosJson = Path.Combine(home, "ros.json")
                  let config = JsonNode.Parse(File.ReadAllText rosJson) :?> JsonObject
                  config["planner"] <- JsonNode.Parse($"""{{"grouping":{{"crossRepository":{{"repositories":{{"{memberName}":{{"path":"{memberClone}","ref":"origin/feature/x"}}}}}}}}}}""")
                  File.WriteAllText(rosJson, config.ToJsonString())
                  pushAll home "home work" |> ignore

                  let memberState () =
                      [ ".ros/work/queue.json"; ".ros/context/current.json"; ".ros/events/events.jsonl"; ".ros/work/groups.json" ]
                      |> List.map (fun relative -> let file = Path.Combine(memberClone, relative) in if File.Exists file then File.ReadAllText file else "absent"),
                      GitFixture.git memberClone [ "status"; "--porcelain"; "--untracked-files=all" ],
                      GitFixture.git memberClone [ "for-each-ref" ]

                  let before = memberState ()
                  let groupId = "GROUP-ECHELON-SWEEP-001"

                  let created =
                      cli home [ "work"; "group"; "create"; "--group"; groupId; "--cross-repository"; "--member"; "HOME-1"; "--member"; $"{memberName}:MEMB-1"; "--dependency"; $"HOME-1={memberName}:MEMB-1@released:v1.0.0"; "--occurred-at"; now (); "--json" ] |> ok

                  Assert.equal homeName (text created.Json["group"].["homeRepository"])
                  Assert.equal true (created.Json["group"].["verifications"].[0].["verified"].GetValue<bool>())
                  Assert.equal 1 (cli home [ "work"; "group"; "add"; "--group"; groupId; "--member"; $"{memberName}:NOPE-1"; "--occurred-at"; now () ]).ExitCode

                  let show () = run home None [ "work"; "group"; "show"; groupId; "--json" ] |> ok
                  let first = show ()
                  let memberRow = first.Json["members"].AsArray() |> Seq.find (fun node -> text node["workItemId"] = $"{memberName}:MEMB-1")
                  Assert.equal "ready" (text memberRow["state"])
                  Assert.equal memberName (text memberRow["repository"])
                  Assert.equal "observation" (text memberRow["observation"].["kind"])
                  Assert.equal false (memberRow["observation"].["linked"].GetValue<bool>())
                  Assert.equal $"{memberName}:MEMB-1" (text first.Json["unlinked"].[0])
                  Assert.equal "waiting" (text first.Json["order"].[0].["state"])
                  Assert.equal 2 (first.Json["repositories"].AsArray().Count)
                  let warned = run home None [ "validate" ]
                  Assert.isTrue (warned.Error.Contains "unlinked") warned.Error

                  // The member repository links its own item, then releases the producer.
                  let linked = cli memberClone [ "work"; "group"; "link"; "--group"; groupId; "--home"; homeName; "--member"; "MEMB-1"; "--occurred-at"; now (); "--json" ] |> ok
                  Assert.equal true (linked.Json["changed"].GetValue<bool>())
                  let again = cli memberClone [ "work"; "group"; "link"; "--group"; groupId; "--home"; homeName; "--member"; "MEMB-1"; "--occurred-at"; now (); "--json" ] |> ok
                  Assert.equal false (again.Json["changed"].GetValue<bool>())
                  let stored = JsonNode.Parse(File.ReadAllText(Path.Combine(memberClone, ".ros", "work", "groups.json")))
                  Assert.equal 0 (stored["groups"].AsArray().Count)
                  Assert.equal "MEMB-1" (text stored["references"].[0].["workItemId"])
                  pushAll memberClone "link MEMB-1 to its group" |> ignore
                  GitFixture.git memberClone [ "tag"; "v1.0.0" ] |> ignore
                  let second = show ()
                  Assert.equal 0 (second.Json["unlinked"].AsArray().Count)
                  Assert.equal "satisfied" (text second.Json["order"].[0].["state"])

                  // The member repository sees its reference; the home lists it.
                  let referenced = run memberClone None [ "work"; "group"; "show"; groupId; "--json" ] |> ok
                  Assert.equal "referenced" (text referenced.Json["status"])
                  let afterLink = memberState ()

                  // Nothing the home ran wrote anything in the member repository.
                  for args in [ [ "work"; "group"; "list" ]; [ "work"; "group"; "show"; groupId ]; [ "validate" ]; [ "plan"; "explain-group"; groupId ] ] do
                      run home None args |> ignore
                  Assert.equal afterLink (memberState ())
                  Assert.isTrue (before <> afterLink) "the link did not change the member repository"
                  let explained = run home None [ "plan"; "explain-group"; groupId; "--json" ] |> ok
                  Assert.isTrue (explained.Output.Contains "executes-elsewhere" && explained.Output.Contains $"{memberName}:MEMB-1 executes in {memberName}") explained.Output
                  let members = explained.Json["group"].["members"].AsArray() |> Seq.map (fun node -> text node["workItem"]) |> Seq.toList
                  Assert.equal [ "HOME-1" ] members
              finally
                  GitFixture.cleanup homeParent
                  GitFixture.cleanup memberParent)

          t "cli an unconfigured or missing member repository is unknown with a reason and the rest of the group still renders" (fun () ->
              withRepository (fun clone ->
                  let created =
                      createGroup clone "GROUP-ECHELON-FIXTURE-001" [ "ITEM-1"; "owner/elsewhere:WORK-1" ] [ "--cross-repository"; "--home-repository"; "owner/home" ] |> ok

                  Assert.equal false (created.Json["group"].["verifications"].[0].["verified"].GetValue<bool>())
                  Assert.isTrue (text created.Json["warnings"].[0] |> fun warning -> warning.Contains "unreachable") (created.Json.ToJsonString())
                  let shown = run clone None [ "work"; "group"; "show"; "GROUP-ECHELON-FIXTURE-001"; "--json" ] |> ok
                  Assert.equal "unknown" (text shown.Json["groupStatus"])
                  let row = shown.Json["members"].AsArray() |> Seq.find (fun node -> text node["workItemId"] = "owner/elsewhere:WORK-1")
                  Assert.equal "unreachable" (text row["observation"].["unobservable"].["code"])
                  Assert.equal "remaining" (text (shown.Json["members"].AsArray() |> Seq.find (fun node -> text node["workItemId"] = "ITEM-1")).["category"])
                  run clone None [ "work"; "group"; "list"; "--repository"; "owner/elsewhere" ] |> ok |> ignore
                  run clone None [ "validate" ] |> ok |> ignore))

          t "cli validate reports a stored group whose member is not a recorded work item" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  let file = groupsFile clone
                  File.WriteAllText(file, File.ReadAllText(file).Replace("\"ITEM-1\"", "\"NOPE-1\""))
                  let result = run clone None [ "validate" ]
                  Assert.equal 1 result.ExitCode
                  Assert.isTrue (result.Error.Contains "member NOPE-1 of GROUP-FIXTURE-001 is not a recorded work item") result.Error)) ]
