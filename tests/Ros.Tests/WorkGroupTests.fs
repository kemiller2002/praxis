namespace Ros.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Ros.Contracts.Work
open Ros.Domain.Git
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work

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
              "ELSE-1", MemberStanding.Open "ready" ]

    let private context (groups: StoredWorkGroup list) : GroupContext =
        { Groups = groups
          Standing = fun id -> standings |> Map.tryFind id |> Option.defaultValue MemberStanding.Unknown
          RepositoryOf = fun id -> if id = "ELSE-1" then "other-repository" else "this-repository" }

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
          Reason = Some "they share one API surface" }

    let private created (groupId: string) (members: string list) =
        match WorkGroups.create (context []) (request groupId members) with
        | Ok group -> group
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

              let crossRepository = { request "GROUP-AREA-003" [ "ITEM-1"; "ELSE-1" ] with CrossRepository = true }
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
                      [ "ITEM-1", { State = Some "complete"; PlanningState = Some "complete"; WaitsOn = [] }
                        "ITEM-2", { State = Some "blocked"; PlanningState = Some "blocked"; WaitsOn = [] }
                        "ITEM-3", { State = Some "ready"; PlanningState = Some "waiting-on-dependency"; WaitsOn = [ "ITEM-2" ] }
                        "ITEM-4", { State = None; PlanningState = None; WaitsOn = [ "ITEM-2" ] } ]

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
                    AllowEmpty = false }

              let add groups id groupId = WorkGroups.add (context groups) (change id groupId)
              Assert.equal [ "unknown-group" ] (codes (add [ group ] "ITEM-2" "GROUP-AREA-404"))
              Assert.equal [ "invalid-group-id" ] (codes (add [ group ] "ITEM-2" "nope"))
              Assert.equal [ "already-member" ] (codes (add [ group ] "ITEM-1" "GROUP-AREA-001"))
              Assert.equal [ "unknown-member" ] (codes (add [ group ] "NOPE-1" "GROUP-AREA-001"))
              Assert.equal [ "terminal-member" ] (codes (add [ group ] "DONE-1" "GROUP-AREA-001"))
              Assert.equal [ "repository-mismatch" ] (codes (add [ group ] "ELSE-1" "GROUP-AREA-001"))
              let crossRepository = { group with Declaration = { group.Declaration with CrossRepository = true } }
              Assert.equal [] (codes (add [ crossRepository ] "ELSE-1" "GROUP-AREA-001"))

              match add [ group ] "ITEM-2" "GROUP-AREA-001" with
              | Error rejections -> failwith $"{rejections}"
              | Ok updated ->
                  Assert.equal [ "ITEM-1"; "ITEM-2" ] updated.Declaration.Members
                  let entry = List.last updated.History
                  Assert.equal GroupOperation.MemberAdded entry.Operation
                  Assert.equal (Some "ITEM-2") entry.Member
                  Assert.equal human entry.Actor
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

                  for id, code in [ "ITEM-2", "already-member"; "DONE-1", "terminal-member"; "GONE-1", "terminal-member"; "NOPE-1", "unknown-member" ] do
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
                  createGroup clone "GROUP-FIXTURE-002" [ "ITEM-2" ] [ "--cross-repository" ] |> ok |> ignore
                  let add groupId = cli clone [ "work"; "group"; "add"; "--group"; groupId; "--member"; "ITEM-3"; "--config"; configuration; "--occurred-at"; now (); "--json" ]
                  let refused = add "GROUP-FIXTURE-001"
                  Assert.equal 1 refused.ExitCode
                  Assert.equal [ "repository-mismatch" ] (rejectionCodes refused)
                  add "GROUP-FIXTURE-002" |> ok |> ignore))

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
                    AllowEmpty = allowEmpty }

              let remove (target: StoredWorkGroup) id allowEmpty = WorkGroups.remove (context [ target ]) (change id allowEmpty)
              Assert.equal [ "not-member" ] (codes (remove group "ITEM-2" false))
              Assert.equal [ "unknown-group" ] (codes (WorkGroups.remove (context []) (change "ITEM-1" false)))

              let withoutDone =
                  match remove group "DONE-1" false with
                  | Ok updated -> updated
                  | Error rejections -> failwith $"{rejections}"

              Assert.equal [ "ITEM-1" ] withoutDone.Declaration.Members
              let entry = List.last withoutDone.History
              Assert.equal (GroupOperation.MemberRemoved, Some "DONE-1", human, false) (entry.Operation, entry.Member, entry.Actor, entry.ExplicitEmpty)
              Assert.equal [ "last-member" ] (codes (remove withoutDone "ITEM-1" false))

              match remove withoutDone "ITEM-1" true with
              | Error rejections -> failwith $"{rejections}"
              | Ok empty ->
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
              let decide groups request location = WorkGroups.checkpoint (context groups) request facts reference location
              let codes result = match result with Ok _ -> [] | Error rejections -> rejections |> List.map GroupCheckpointRejection.code

              Assert.equal [ "unknown-group" ] (codes (decide [] request (Ok location)))
              Assert.equal [ "blank-summary"; "local-ahead" ] (codes (decide [ group ] { request with Summary = " " } (Error [ CheckpointRejection.LocalAhead(1, { Name = "origin"; Url = None }, "feature/x") ])))

              match decide [ group ] request (Ok location) with
              | Error rejections -> failwith $"{rejections}"
              | Ok(updated, recorded) ->
                  Assert.equal [ recorded ] updated.Checkpoints
                  Assert.equal (group.Declaration, group.History) (updated.Declaration, updated.History)
                  Assert.equal ([], [ "ITEM-2" ], [ "ITEM-3" ], [ "ITEM-1" ]) (recorded.Completed, recorded.Active, recorded.Blocked, recorded.Remaining)
                  Assert.equal [ "cp-2" ] (recorded.MemberCheckpoints |> List.map (fun item -> item.CheckpointId))
                  let rendered = WorkGroupJson.renderStore [ updated ]
                  Assert.equal (Ok [ updated ]) (WorkGroupJson.readStore rendered))

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

          t "cli validate reports a stored group whose member is not a recorded work item" (fun () ->
              withRepository (fun clone ->
                  createGroup clone "GROUP-FIXTURE-001" [ "ITEM-1" ] [] |> ok |> ignore
                  let file = groupsFile clone
                  File.WriteAllText(file, File.ReadAllText(file).Replace("\"ITEM-1\"", "\"NOPE-1\""))
                  let result = run clone None [ "validate" ]
                  Assert.equal 1 result.ExitCode
                  Assert.isTrue (result.Error.Contains "member NOPE-1 of GROUP-FIXTURE-001 is not a recorded work item") result.Error)) ]
