namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work

/// Declared execution groups (`work group ...`, PRX-GRP-073 phase two).
/// Shared fixtures for every member of GROUP-PRAXIS-WORK-GROUP-001
/// (analysis §17): pure-domain cases, then the real CLI against an installed
/// repository with a pushed branch.
[<RequireQualifiedAccess>]
module WorkGroupTests =
    open PraxisCli

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"

    let private actor: Actor =
        { Kind = ActorKind.Agent
          Id = "example/agent-a"
          Provider = Some "example"
          Model = Some "unknown"
          Runtime = Some "agent-a" }

    // ---- domain fixtures ----

    let private fact id state terminal repository =
        id,
        { WorkItemId = id
          RecordedState = state
          Terminal = terminal
          ExecutionRepository = repository }

    let private facts =
        Map.ofList
            [ fact "FEAT-1" "ready" false "here"
              fact "FEAT-2" "active" false "here"
              fact "FEAT-3" "complete" true "here"
              fact "EXT-1" "ready" false "elsewhere" ]

    let private declaration id members =
        { Id = id
          Kind = Some GroupKind.SharedApiSurface
          ExecutionRepository = "here"
          CrossRepository = false
          SharedContext = [ "one store" ]
          ArchitectureNotes = [ "one design" ]
          Members = members
          OccurredAt = "2026-09-30T12:00:00.000Z"
          Actor = actor
          Reason = Some "declared together" }

    let private codes (result: Result<'a, GroupRejection list>) =
        match result with
        | Ok _ -> []
        | Error rejections -> rejections |> List.map GroupRejection.code

    let private createdGroup members =
        match WorkGroups.create (WorkGroups.empty "here") facts (declaration "GROUP-HERE-001" members) with
        | Ok(stored, group) -> stored, group
        | Error rejections -> failwith $"%A{rejections}"

    // ---- CLI fixtures ----

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "work-group"

        try
            let _, clone = installedRepository parent "clone-a"
            test clone
        finally
            GitFixture.cleanup parent

    let private cli clone arguments = run clone (Some agentA) arguments

    let private capture clone (id: string) =
        cli clone [ "add"; $"Work item {id}"; "--id"; id ] |> ok |> ignore
        cli clone [ "work"; "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore

    let private groupsPath clone = Path.Combine(clone, ".ros", "work", "groups.json")

    /// Every file a member's lifecycle, evidence or attribution lives in.
    let private memberState clone =
        [ ".ros/work/queue.json"; ".ros/work/queue.md"; ".ros/context/current.json"; ".ros/events/events.jsonl" ]
        |> List.map (fun relative ->
            let file = Path.Combine(clone, relative)
            relative, (if File.Exists file then Some(File.ReadAllText file) else None))

    let private telemetryFiles clone =
        let directory = Path.Combine(clone, ".ros", "telemetry", "executions")
        if Directory.Exists directory then Directory.GetFiles directory |> Array.map File.ReadAllText |> Array.toList else []

    /// Runs `action` and asserts no member-state file or telemetry changed
    /// (analysis D11).
    let private leavesMembersUntouched clone (action: unit -> 'a) =
        let before = memberState clone, telemetryFiles clone
        let result = action ()
        Assert.equal before (memberState clone, telemetryFiles clone)
        result

    let private create clone (id: string) (members: string list) (extra: string list) =
        cli clone ([ "work"; "group"; "create"; "--id"; id; "--occurred-at"; now (); "--json" ] @ (members |> List.collect (fun m -> [ "--member"; m ])) @ extra)

    let private rejectionCodes (result: Result) =
        match result.Json["rejections"] with
        | :? JsonArray as array -> array |> Seq.map (fun node -> text node["code"]) |> Seq.toList
        | _ -> []

    let private storedGroup clone (id: string) =
        (JsonNode.Parse(File.ReadAllText(groupsPath clone)).["groups"].AsArray())
        |> Seq.map (fun node -> node.AsObject())
        |> Seq.find (fun group -> text group["id"] = id)

    let private texts (node: JsonNode) = node.AsArray() |> Seq.map text |> Seq.toList

    let private createTests =
        [ // ---- PRAXIS-GROUP-01: create ----
          { Name = "work group (domain): create records members, kind, repository and a 'created' history entry with its actor"
            Run =
              fun () ->
                  let stored, group = createdGroup [ "FEAT-1"; "FEAT-2" ]
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] group.Members
                  Assert.equal (Some GroupKind.SharedApiSurface) group.Kind
                  Assert.equal GroupOrigin.HumanDeclared group.Origin
                  Assert.equal "here" group.ExecutionRepository
                  let entry = Assert.single group.History
                  Assert.equal GroupHistoryOperation.Created entry.Operation
                  Assert.equal actor entry.Actor
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] entry.WorkItemIds
                  Assert.equal [ group ] stored.Groups }
          { Name = "work group (domain): create refuses unknown, terminal and foreign-repository members and duplicate IDs, together"
            Run =
              fun () ->
                  let stored, _ = createdGroup [ "FEAT-1" ]
                  let result = WorkGroups.create stored facts (declaration "GROUP-HERE-001" [ "NOPE-1"; "FEAT-3"; "EXT-1" ])
                  Assert.equal [ "group-exists"; "unknown-work-item"; "terminal-work-item"; "repository-mismatch" ] (codes result)
                  // A cross-repository group admits a member from another repository.
                  let cross = { declaration "GROUP-HERE-002" [ "FEAT-1"; "EXT-1" ] with CrossRepository = true }
                  Assert.equal [] (codes (WorkGroups.create stored facts cross)) }
          { Name = "work group (domain): an invalid ID, no members and a repeated member are argument errors"
            Run =
              fun () ->
                  let result = WorkGroups.create (WorkGroups.empty "here") facts (declaration "group-1" [])
                  Assert.equal [ "invalid-group-id"; "no-members" ] (codes result)
                  Assert.isTrue (match result with Error rejections -> rejections |> List.forall GroupRejection.isArgumentError | Ok _ -> false) "argument errors"
                  Assert.equal [ "repeated-member" ] (codes (WorkGroups.create (WorkGroups.empty "here") facts (declaration "GROUP-X-1" [ "FEAT-1"; "FEAT-1" ]))) }
          { Name = "work group (domain): member facts mark terminal queue or live states and map execution repositories"
            Run =
              fun () ->
                  let known =
                      WorkGroups.memberFacts
                          "local"
                          [ "B-1", "other" ]
                          [ "A-1", "ready"; "B-1", "ready"; "C-1", "abandoned" ]
                          [ "A-1", LiveWorkState.Complete; "D-1", LiveWorkState.Active ]

                  Assert.equal ("complete", true, "local") (known["A-1"].RecordedState, known["A-1"].Terminal, known["A-1"].ExecutionRepository)
                  Assert.equal ("ready", false, "other") (known["B-1"].RecordedState, known["B-1"].Terminal, known["B-1"].ExecutionRepository)
                  Assert.equal ("abandoned", true) (known["C-1"].RecordedState, known["C-1"].Terminal)
                  Assert.equal ("active", false) (known["D-1"].RecordedState, known["D-1"].Terminal) }
          { Name = "work group (domain): a stored group projects to the planner as a declared group; a configured group with the same ID shadows it"
            Run =
              fun () ->
                  let _, group = createdGroup [ "FEAT-1"; "FEAT-2" ]
                  let declared = WorkGroups.toDeclared group
                  Assert.equal "GROUP-HERE-001" declared.Id
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] declared.Members
                  Assert.equal (Some "here") declared.ExecutionRepository
                  Assert.equal [ "one design" ] declared.ArchitectureNotes
                  let merged = WorkGroups.mergeInto [ group ] GroupingConfiguration.defaults
                  Assert.equal [ declared ] merged.Groups
                  let configured = { declared with Members = [ "FEAT-9" ] }
                  let shadowed = WorkGroups.mergeInto [ group ] { GroupingConfiguration.defaults with Groups = [ configured ] }
                  Assert.equal [ configured ] shadowed.Groups }
          { Name = "work group (domain): validate reports unknown and repeated members and duplicate IDs, never a member that completed later"
            Run =
              fun () ->
                  let stored, group = createdGroup [ "FEAT-1"; "FEAT-2" ]
                  Assert.empty (WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) stored)
                  let broken = { stored with Groups = [ group; { group with Members = [ "FEAT-1"; "FEAT-1"; "GONE-1" ] } ] }
                  let messages = WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) broken |> List.map _.Message
                  Assert.isTrue (messages |> List.exists (fun m -> m.Contains "declared more than once")) "duplicate ID"
                  Assert.isTrue (messages |> List.exists (fun m -> m.Contains "FEAT-1 is listed more than once")) "repeated member"
                  Assert.isTrue (messages |> List.exists (fun m -> m.Contains "GONE-1 is not a work item")) "unknown member" }
          { Name = "work group create (cli): records the group in .ros/work/groups.json, never touches member state, and the planner reads it as grouping.groups"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"

                      let result =
                          leavesMembersUntouched clone (fun () ->
                              create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] [ "--kind"; "shared-api-surface"; "--shared-context"; "one store"; "--architecture-note"; "one design" ]
                              |> ok)

                      let document = result.Json
                      Assert.equal "work group create" (text document["command"])
                      Assert.equal "created" (text document["status"])
                      Assert.equal "GROUP-FIXTURE-001" (text document["groupId"])
                      let group = storedGroup clone "GROUP-FIXTURE-001"
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (texts group["members"])
                      Assert.equal "shared-api-surface" (text group["kind"])
                      Assert.equal "human-declared" (text group["origin"])
                      Assert.equal false (group["crossRepository"].GetValue<bool>())
                      Assert.equal "example/agent-a" (text group["createdBy"].["id"])
                      Assert.equal "created" (text group["history"].[0].["operation"])

                      let explained = run clone None [ "plan"; "explain-group"; "GROUP-FIXTURE-001" ] |> ok
                      Assert.isTrue (explained.Output.Contains "declared a member of GROUP-FIXTURE-001") explained.Output
                      Assert.isTrue (explained.Output.Contains "shared-api-surface, human-declared") explained.Output
                      Assert.isTrue (explained.Output.Contains "one design") explained.Output
                      let groups = run clone None [ "plan"; "groups"; "--json" ] |> ok
                      Assert.isTrue (groups.Output.Contains "GROUP-FIXTURE-001") "plan groups must list the stored group"
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "work group create (cli): --dry-run decides and reports but writes nothing"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      let result = leavesMembersUntouched clone (fun () -> create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [ "--dry-run" ] |> ok)
                      Assert.equal "dry-run" (text result.Json["status"])
                      Assert.equal "FEAT-1" (text result.Json["group"].["members"].[0])
                      Assert.isTrue (not (File.Exists(groupsPath clone))) "a dry run wrote groups.json"
                      let refused = create clone "GROUP-FIXTURE-001" [ "NOPE-1" ] [ "--dry-run" ]
                      Assert.equal 1 refused.ExitCode
                      Assert.equal [ "unknown-work-item" ] (rejectionCodes refused)) }
          { Name = "work group create (cli): unknown, terminal and duplicate refusals exit 1; argument errors exit 2; nothing is written"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"
                      cli clone [ "work"; "abandon"; "--id"; "FEAT-2"; "--reason"; "not needed"; "--occurred-at"; now () ] |> ok |> ignore

                      leavesMembersUntouched clone (fun () ->
                          let unknown = create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "NOPE-1" ] []
                          Assert.equal 1 unknown.ExitCode
                          Assert.equal [ "unknown-work-item" ] (rejectionCodes unknown)
                          let terminal = create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] []
                          Assert.equal 1 terminal.ExitCode
                          Assert.equal [ "terminal-work-item" ] (rejectionCodes terminal)
                          Assert.isTrue (not (File.Exists(groupsPath clone))) "a refused create wrote groups.json"
                          create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                          let duplicate = create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] []
                          Assert.equal 1 duplicate.ExitCode
                          Assert.equal [ "group-exists" ] (rejectionCodes duplicate)
                          Assert.equal 2 (create clone "group-lower" [ "FEAT-1" ] []).ExitCode
                          Assert.equal 2 (create clone "GROUP-FIXTURE-002" [] []).ExitCode
                          Assert.equal 2 (cli clone [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-003"; "--member"; "FEAT-1" ]).ExitCode
                          Assert.equal 2 (create clone "GROUP-FIXTURE-004" [ "FEAT-1" ] [ "--kind"; "nonsense" ]).ExitCode)

                      Assert.equal 1 ((JsonNode.Parse(File.ReadAllText(groupsPath clone))).["groups"].AsArray().Count)) }
          { Name = "work group create (cli): a member mapped to another repository is refused unless the group is cross-repository"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "EXT-1"
                      let config = Path.Combine(clone, "planner.json")
                      File.WriteAllText(config, """{ "grouping": { "executionRepositories": { "EXT-1": "other-repository" } } }""")
                      let mismatch = create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "EXT-1" ] [ "--config"; config ]
                      Assert.equal 1 mismatch.ExitCode
                      Assert.equal [ "repository-mismatch" ] (rejectionCodes mismatch)
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "EXT-1" ] [ "--config"; config; "--cross-repository" ] |> ok |> ignore
                      Assert.equal true ((storedGroup clone "GROUP-FIXTURE-001").["crossRepository"].GetValue<bool>())) }
          { Name = "work group (cli): validate checks stored groups"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                      run clone None [ "validate" ] |> ok |> ignore
                      let document = JsonNode.Parse(File.ReadAllText(groupsPath clone))
                      document["groups"].[0].["members"].AsArray().Add(JsonValue.Create "GONE-1")
                      File.WriteAllText(groupsPath clone, document.ToJsonString())
                      let invalid = run clone None [ "validate" ]
                      Assert.equal 1 invalid.ExitCode
                      Assert.isTrue (invalid.Error.Contains ".ros/work/groups.json") invalid.Error
                      Assert.isTrue (invalid.Error.Contains "GONE-1 is not a work item") invalid.Error
                      File.WriteAllText(groupsPath clone, "{ not json")
                      Assert.equal 1 (run clone None [ "validate" ]).ExitCode) } ]

    // ---- PRAXIS-GROUP-02: show ----

    let private startMechanical clone (id: string) =
        cli clone [ "work"; "start"; "--id"; id; "--type"; "mechanical"; "--occurred-at"; now () ] |> ok |> ignore

    let private show clone (id: string) (extra: string list) = run clone None ([ "work"; "group"; "show"; id ] @ extra)

    let private showTests =
        [ { Name = "work group (domain): the view keeps each member's own state, counts partial progress and inverts gating"
            Run =
              fun () ->
                  let _, group = createdGroup [ "FEAT-1"; "FEAT-2" ]
                  let view = WorkGroups.view facts None group
                  Assert.equal [ "unknown"; "unknown" ] (view.Members |> List.map _.Status)
                  Assert.equal [ "ready"; "active" ] (view.Members |> List.map _.RecordedState)
                  Assert.equal 2 view.Progress.Unknown
                  Assert.equal 0 view.Progress.Complete
                  Assert.isTrue (view.Progress.Statement.Contains "never implies that every member succeeded") view.Progress.Statement }
          { Name = "work group show (cli): members keep their own recorded and planning states; progress is partial; blocked members name who they gate"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      for id in [ "FEAT-1"; "FEAT-2"; "FEAT-3" ] do
                          capture clone id

                      create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2"; "FEAT-3" ] [ "--architecture-note"; "one design"; "--shared-context"; "one store" ] |> ok |> ignore
                      startMechanical clone "FEAT-3"
                      cli clone [ "work"; "complete"; "--id"; "FEAT-3"; "--occurred-at"; now () ] |> ok |> ignore
                      startMechanical clone "FEAT-1"
                      cli clone [ "work"; "block"; "--id"; "FEAT-1"; "--reason"; "waiting on a decision"; "--occurred-at"; now () ] |> ok |> ignore
                      let config = Path.Combine(clone, "planner.json")
                      File.WriteAllText(config, """{ "dependencies": [ { "from": "FEAT-2", "to": "FEAT-1", "kind": "hard" } ] }""")
                      let groups = File.ReadAllText(groupsPath clone)

                      let document =
                          leavesMembersUntouched clone (fun () -> (show clone "GROUP-FIXTURE-001" [ "--json"; "--config"; config ] |> ok).Json)

                      Assert.equal groups (File.ReadAllText(groupsPath clone))
                      Assert.equal "work group show" (text document["command"])
                      Assert.equal "shown" (text document["status"])
                      let members = document["members"].AsArray() |> Seq.map (fun node -> text node["workItemId"], node.AsObject()) |> Map.ofSeq
                      Assert.equal ("blocked", "blocked") (text members["FEAT-1"].["recordedState"], text members["FEAT-1"].["status"])
                      Assert.equal ("complete", "complete") (text members["FEAT-3"].["recordedState"], text members["FEAT-3"].["status"])
                      Assert.equal "ready" (text members["FEAT-2"].["recordedState"])
                      Assert.equal [ "FEAT-1" ] (texts members["FEAT-2"].["gatedBy"])
                      Assert.equal [ "FEAT-2" ] (texts members["FEAT-1"].["gates"])
                      Assert.equal 3 (document["progress"].["total"].GetValue<int>())
                      Assert.equal 1 (document["progress"].["complete"].GetValue<int>())
                      Assert.equal 1 (document["progress"].["blocked"].GetValue<int>())
                      Assert.equal "FEAT-1" (text document["blocked"].[0].["workItemId"])
                      Assert.equal [ "FEAT-2" ] (texts document["blocked"].[0].["gates"])
                      Assert.equal [ "one design" ] (texts document["architectureNotes"])
                      Assert.isTrue ((text document["executionRepository"]).Length > 0) "execution repository"
                      let textView = show clone "GROUP-FIXTURE-001" [ "--config"; config ] |> ok
                      Assert.isTrue (textView.Output.Contains "1 of 3 complete; blocked: FEAT-1") textView.Output
                      Assert.isTrue (textView.Output.Contains "gates FEAT-2") textView.Output
                      Assert.isTrue (textView.Output.Contains "architecture note:    one design") textView.Output
                      Assert.isTrue (textView.Output.Contains "execution repository:") textView.Output) }
          { Name = "work group show (cli): an unknown group exits 1; a missing ID exits 2; nothing is written"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"

                      leavesMembersUntouched clone (fun () ->
                          let unknown = show clone "GROUP-NOPE-001" [ "--json" ]
                          Assert.equal 1 unknown.ExitCode
                          Assert.equal [ "unknown-group" ] (rejectionCodes unknown)
                          Assert.equal 1 (show clone "GROUP-NOPE-001" []).ExitCode
                          Assert.equal 2 (run clone None [ "work"; "group"; "show" ]).ExitCode)

                      Assert.isTrue (not (File.Exists(groupsPath clone))) "show wrote groups.json") } ]

    // ---- PRAXIS-GROUP-03: add ----

    let private change groupId workItemId =
        { GroupId = groupId
          WorkItemId = workItemId
          OccurredAt = "2026-09-30T13:00:00.000Z"
          Actor = { actor with Id = "example/agent-b" }
          Reason = Some "belongs here" }

    let private add clone (groupId: string) (member': string) (extra: string list) =
        cli clone ([ "work"; "group"; "add"; "--id"; groupId; "--member"; member'; "--occurred-at"; now (); "--json" ] @ extra)

    let private addTests =
        [ { Name = "work group (domain): add appends the member and a 'member-added' history entry naming who added it"
            Run =
              fun () ->
                  let stored, _ = createdGroup [ "FEAT-1" ]

                  match WorkGroups.addMember stored facts (change "GROUP-HERE-001" "FEAT-2") with
                  | Error rejections -> failwith $"%A{rejections}"
                  | Ok(next, group) ->
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] group.Members
                      let entry = List.last group.History
                      Assert.equal GroupHistoryOperation.MemberAdded entry.Operation
                      Assert.equal [ "FEAT-2" ] entry.WorkItemIds
                      Assert.equal "example/agent-b" entry.Actor.Id
                      Assert.equal (Some "belongs here") entry.Reason
                      Assert.equal [ group ] next.Groups
                      Assert.equal group.Members (WorkGroups.membersFromHistory group.History)
                      Assert.empty (WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) next) }
          { Name = "work group (domain): add refuses unknown, terminal, present and foreign-repository items and unknown groups"
            Run =
              fun () ->
                  let stored, _ = createdGroup [ "FEAT-1" ]
                  let addCodes id = codes (WorkGroups.addMember stored facts (change "GROUP-HERE-001" id))
                  Assert.equal [ "unknown-work-item" ] (addCodes "NOPE-1")
                  Assert.equal [ "terminal-work-item" ] (addCodes "FEAT-3")
                  Assert.equal [ "already-member" ] (addCodes "FEAT-1")
                  Assert.equal [ "repository-mismatch" ] (addCodes "EXT-1")
                  Assert.equal [ "unknown-group" ] (codes (WorkGroups.addMember stored facts (change "GROUP-HERE-404" "FEAT-2")))
                  Assert.equal [ "invalid-group-id" ] (codes (WorkGroups.addMember stored facts (change "nope" "FEAT-2")))
                  let cross = { stored with Groups = stored.Groups |> List.map (fun group -> { group with CrossRepository = true }) }
                  Assert.equal [] (codes (WorkGroups.addMember cross facts (change "GROUP-HERE-001" "EXT-1"))) }
          { Name = "work group (domain): validate reports members that the membership history does not explain"
            Run =
              fun () ->
                  let stored, group = createdGroup [ "FEAT-1" ]
                  let unexplained = { stored with Groups = [ { group with Members = [ "FEAT-1"; "FEAT-2" ] } ] }
                  let messages = WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) unexplained |> List.map _.Message
                  Assert.isTrue (messages |> List.exists (fun m -> m.Contains "do not match the membership history")) $"%A{messages}" }
          { Name = "work group add (cli): adds the member with provenance, leaves the item untouched, and show and the planner see it"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                      let result = leavesMembersUntouched clone (fun () -> add clone "GROUP-FIXTURE-001" "FEAT-2" [ "--reason"; "shares the store" ] |> ok)
                      Assert.equal "added" (text result.Json["status"])
                      Assert.equal "FEAT-2" (text result.Json["workItemId"])
                      let group = storedGroup clone "GROUP-FIXTURE-001"
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (texts group["members"])
                      let entry = group["history"].AsArray() |> Seq.last
                      Assert.equal "member-added" (text entry["operation"])
                      Assert.equal [ "FEAT-2" ] (texts entry["workItemIds"])
                      Assert.equal "example/agent-a" (text entry["actor"].["id"])
                      Assert.equal "agent" (text entry["actor"].["kind"])
                      Assert.equal "shares the store" (text entry["reason"])
                      let shown = (run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok).Json
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (shown["members"].AsArray() |> Seq.map (fun node -> text node["workItemId"]) |> Seq.toList)
                      let explained = run clone None [ "plan"; "explain-group"; "GROUP-FIXTURE-001" ] |> ok
                      Assert.isTrue (explained.Output.Contains "FEAT-2 [") explained.Output
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "work group add (cli): refusals exit 1, argument errors exit 2, --dry-run writes nothing, cross-repository admits a mapped item"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      for id in [ "FEAT-1"; "FEAT-2"; "FEAT-3"; "EXT-1" ] do
                          capture clone id

                      create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                      cli clone [ "work"; "abandon"; "--id"; "FEAT-3"; "--reason"; "not needed"; "--occurred-at"; now () ] |> ok |> ignore
                      let config = Path.Combine(clone, "planner.json")
                      File.WriteAllText(config, """{ "grouping": { "executionRepositories": { "EXT-1": "other-repository" } } }""")
                      let before = File.ReadAllText(groupsPath clone)

                      leavesMembersUntouched clone (fun () ->
                          let refusal id extra = add clone "GROUP-FIXTURE-001" id extra
                          Assert.equal [ "unknown-work-item" ] (rejectionCodes (refusal "NOPE-1" []))
                          Assert.equal [ "terminal-work-item" ] (rejectionCodes (refusal "FEAT-3" []))
                          Assert.equal [ "already-member" ] (rejectionCodes (refusal "FEAT-1" []))
                          let mismatch = refusal "EXT-1" [ "--config"; config ]
                          Assert.equal 1 mismatch.ExitCode
                          Assert.equal [ "repository-mismatch" ] (rejectionCodes mismatch)
                          Assert.equal [ "unknown-group" ] (rejectionCodes (add clone "GROUP-FIXTURE-404" "FEAT-2" []))
                          Assert.equal 1 (add clone "GROUP-FIXTURE-404" "FEAT-2" []).ExitCode
                          Assert.equal 2 (add clone "nope" "FEAT-2" []).ExitCode
                          Assert.equal 2 (cli clone [ "work"; "group"; "add"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now () ]).ExitCode
                          Assert.equal "dry-run" (text (refusal "FEAT-2" [ "--dry-run" ] |> ok).Json["status"]))

                      Assert.equal before (File.ReadAllText(groupsPath clone))
                      create clone "GROUP-FIXTURE-002" [ "FEAT-1" ] [ "--cross-repository" ] |> ok |> ignore
                      add clone "GROUP-FIXTURE-002" "EXT-1" [ "--config"; config ] |> ok |> ignore
                      Assert.equal [ "FEAT-1"; "EXT-1" ] (texts (storedGroup clone "GROUP-FIXTURE-002").["members"])) } ]

    // ---- PRAXIS-GROUP-04: remove ----

    let private remove clone (groupId: string) (member': string) (extra: string list) =
        cli clone ([ "work"; "group"; "remove"; "--id"; groupId; "--member"; member'; "--occurred-at"; now (); "--json" ] @ extra)

    let private removeTests =
        [ { Name = "work group (domain): remove drops only the membership and records a 'member-removed' entry with its actor"
            Run =
              fun () ->
                  let stored, _ = createdGroup [ "FEAT-1"; "FEAT-2" ]

                  match WorkGroups.removeMember stored facts (change "GROUP-HERE-001" "FEAT-1") with
                  | Error rejections -> failwith $"%A{rejections}"
                  | Ok(next, group) ->
                      Assert.equal [ "FEAT-2" ] group.Members
                      let entry = List.last group.History
                      Assert.equal GroupHistoryOperation.MemberRemoved entry.Operation
                      Assert.equal [ "FEAT-1" ] entry.WorkItemIds
                      Assert.equal "example/agent-b" entry.Actor.Id
                      Assert.equal group.Members (WorkGroups.membersFromHistory group.History)
                      Assert.empty (WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) next)
                      // Re-adding after removal is a new, recorded membership.
                      match WorkGroups.addMember next facts (change "GROUP-HERE-001" "FEAT-1") with
                      | Ok(_, again) -> Assert.equal [ "FEAT-2"; "FEAT-1" ] (WorkGroups.membersFromHistory again.History)
                      | Error rejections -> failwith $"%A{rejections}" }
          { Name = "work group (domain): remove refuses a non-member, the last member and an unknown group"
            Run =
              fun () ->
                  let stored, _ = createdGroup [ "FEAT-1" ]
                  Assert.equal [ "not-member" ] (codes (WorkGroups.removeMember stored facts (change "GROUP-HERE-001" "FEAT-2")))
                  Assert.equal [ "last-member" ] (codes (WorkGroups.removeMember stored facts (change "GROUP-HERE-001" "FEAT-1")))
                  Assert.equal [ "unknown-group" ] (codes (WorkGroups.removeMember stored facts (change "GROUP-HERE-404" "FEAT-1"))) }
          { Name = "work group remove (cli): removing a completed member never touches its lifecycle, evidence or attribution; provenance is recorded"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] [] |> ok |> ignore
                      cli clone [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      GitFixture.write clone "src/feature.txt" "feature\n"
                      pushAll clone "feature work" |> ignore
                      cli clone [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ] |> ok |> ignore
                      cli clone [ "work"; "complete"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--evidence"; "implementation=src/feature.txt"; "--evidence"; "tests=README.md" ] |> ok |> ignore
                      let result = leavesMembersUntouched clone (fun () -> remove clone "GROUP-FIXTURE-001" "FEAT-1" [ "--reason"; "delivered separately" ] |> ok)
                      Assert.equal "removed" (text result.Json["status"])
                      let group = storedGroup clone "GROUP-FIXTURE-001"
                      Assert.equal [ "FEAT-2" ] (texts group["members"])
                      let entry = group["history"].AsArray() |> Seq.last
                      Assert.equal "member-removed" (text entry["operation"])
                      Assert.equal "example/agent-a" (text entry["actor"].["id"])
                      Assert.equal "delivered separately" (text entry["reason"])
                      let item = (run clone None [ "work"; "context"; "FEAT-1" ] |> ok).Json["workItems"].[0]
                      Assert.equal "complete" (text item["semanticState"])
                      Assert.isTrue (item["evidence"].AsArray().Count > 0) "the member's evidence must remain"
                      let shown = (run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok).Json
                      Assert.equal [ "FEAT-2" ] (shown["members"].AsArray() |> Seq.map (fun node -> text node["workItemId"]) |> Seq.toList)
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "work group remove (cli): a non-member and the last member are refused with exit 1; --dry-run writes nothing"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                      let before = File.ReadAllText(groupsPath clone)

                      leavesMembersUntouched clone (fun () ->
                          let notMember = remove clone "GROUP-FIXTURE-001" "FEAT-2" []
                          Assert.equal 1 notMember.ExitCode
                          Assert.equal [ "not-member" ] (rejectionCodes notMember)
                          let last = remove clone "GROUP-FIXTURE-001" "FEAT-1" []
                          Assert.equal 1 last.ExitCode
                          Assert.equal [ "last-member" ] (rejectionCodes last)
                          Assert.equal 2 (cli clone [ "work"; "group"; "remove"; "--id"; "GROUP-FIXTURE-001"; "--member"; "FEAT-1" ]).ExitCode)

                      add clone "GROUP-FIXTURE-001" "FEAT-2" [] |> ok |> ignore
                      let afterAdd = File.ReadAllText(groupsPath clone)
                      Assert.isTrue (before <> afterAdd) "add must change the store"
                      Assert.equal "dry-run" (text (remove clone "GROUP-FIXTURE-001" "FEAT-1" [ "--dry-run" ] |> ok).Json["status"])
                      Assert.equal afterAdd (File.ReadAllText(groupsPath clone))) } ]

    // ---- PRAXIS-GROUP-05: checkpoint ----

    let private sha = String.replicate 40 "a"

    let private location: Ros.Domain.Work.GitDurableLocation =
        { Repository = "here"
          Branch = "feature/x"
          LocalCommit = (Ros.Domain.Git.CommitId.tryParse sha).Value
          Remote = { Name = "origin"; Url = None }
          RemoteBranch = "feature/x"
          RemoteCommit = (Ros.Domain.Git.CommitId.tryParse sha).Value }

    let private request groupId =
        { GroupId = groupId
          Summary = "Milestone reached"
          NextAction = "Implement the next member"
          Decisions = [ "D1 one store" ]
          OccurredAt = "2026-09-30T14:00:00.000Z"
          Actor = actor }

    let private memberSummary id : CheckpointSummary =
        { CheckpointId = $"cp-{id}"
          ExecutionId = $"EXE-{id}"
          RecordedAt = "2026-09-30T13:30:00.000Z"
          Branch = "feature/x"
          Commit = sha
          Summary = "member work"
          NextAction = "more"
          Verified = true }

    let private groupCheckpoint clone (groupId: string) (extra: string list) =
        cli clone ([ "work"; "group"; "checkpoint"; "--id"; groupId; "--occurred-at"; now (); "--summary"; "Milestone reached"; "--next-action"; "Implement the next member"; "--json" ] @ extra)

    let private checkpointTests =
        [ { Name = "work group (domain): a group checkpoint records the durable location, member progress, decisions and references to members' own checkpoints"
            Run =
              fun () ->
                  let stored, group = createdGroup [ "FEAT-1"; "FEAT-2" ]
                  let view = WorkGroups.view facts None group
                  let references = Map.ofList [ "FEAT-2", memberSummary "FEAT-2" ]

                  match WorkGroups.checkpoint stored view (Ok location) references (request "GROUP-HERE-001") with
                  | Error rejections -> failwith $"%A{rejections}"
                  | Ok(next, recorded) ->
                      Assert.equal sha recorded.Commit
                      Assert.equal ("feature/x", "origin", "feature/x") (recorded.Branch, recorded.Remote, recorded.RemoteBranch)
                      Assert.equal [ "FEAT-2" ] recorded.ActiveMembers
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] recorded.RemainingMembers
                      Assert.equal [ "D1 one store" ] recorded.Decisions
                      Assert.equal [ None; Some "cp-FEAT-2" ] (recorded.MemberCheckpoints |> List.map _.CheckpointId)
                      Assert.equal (WorkGroups.checkpointId recorded) recorded.CheckpointId
                      Assert.isTrue (recorded.CheckpointId.StartsWith "GCP-") recorded.CheckpointId
                      let saved = (WorkGroups.tryFind "GROUP-HERE-001" next).Value
                      Assert.equal [ recorded ] saved.Checkpoints
                      Assert.equal group.Members saved.Members
                      Assert.empty (WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) next)
                      let edited = { next with Groups = [ { saved with Checkpoints = [ { recorded with Summary = "rewritten" } ] } ] }
                      let messages = WorkGroups.validate (Set.ofList [ "FEAT-1"; "FEAT-2" ]) edited |> List.map _.Message
                      Assert.isTrue (messages |> List.exists (fun m -> m.Contains "does not match its content")) $"%A{messages}" }
          { Name = "work group (domain): durability rejections are the work checkpoint's own; a group with nothing remaining is refused"
            Run =
              fun () ->
                  let stored, group = createdGroup [ "FEAT-1" ]
                  let view = WorkGroups.view facts None group
                  let remote: Ros.Domain.Git.RemoteIdentity = { Name = "origin"; Url = None }
                  let notDurable = WorkGroups.checkpoint stored view (Error [ CheckpointRejection.LocalAhead(1, remote, "feature/x") ]) Map.empty (request "GROUP-HERE-001")
                  Assert.equal [ "local-ahead" ] (codes notDurable)
                  let blank = WorkGroups.checkpoint stored view (Error [ CheckpointRejection.BlankSummary ]) Map.empty (request "GROUP-HERE-001")
                  Assert.isTrue (match blank with Error rejections -> rejections |> List.forall GroupRejection.isArgumentError | Ok _ -> false) "blank text is an argument error"
                  let finished = { view with Members = view.Members |> List.map (fun row -> { row with Status = "complete"; RecordedState = "complete" }) }
                  Assert.equal [ "nothing-remaining" ] (codes (WorkGroups.checkpoint stored finished (Ok location) Map.empty (request "GROUP-HERE-001")))
                  Assert.equal [ "unknown-group" ] (codes (WorkGroups.checkpoint stored view (Ok location) Map.empty (request "GROUP-HERE-404"))) }
          { Name = "work group checkpoint (cli): verified like work checkpoint; references the member's own checkpoint without replacing it; claims no paths"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      capture clone "FEAT-2"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1"; "FEAT-2" ] [] |> ok |> ignore
                      cli clone [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      GitFixture.write clone "src/feature.txt" "feature\n"
                      let head = pushAll clone "feature work"
                      let own = (cli clone [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--summary"; "slice"; "--next-action"; "next"; "--json" ] |> ok).Json
                      let ownId = text own["checkpoint"].["id"]
                      let pushed = pushAll clone "praxis state"

                      let result =
                          leavesMembersUntouched clone (fun () -> groupCheckpoint clone "GROUP-FIXTURE-001" [ "--decision"; "D1 one store" ] |> ok)

                      Assert.isTrue (head <> pushed) "fixture"
                      let document = result.Json
                      Assert.equal "recorded" (text document["status"])
                      Assert.equal 0 (document["claimedPaths"].AsArray().Count)
                      let recorded = document["checkpoint"]
                      Assert.equal pushed (text recorded["commit"])
                      Assert.equal "feature/x" (text recorded["branch"])
                      Assert.equal [ "FEAT-1" ] (texts recorded["activeMembers"])
                      Assert.equal [ "FEAT-1"; "FEAT-2" ] (texts recorded["remainingMembers"])
                      Assert.equal [ "D1 one store" ] (texts recorded["decisions"])
                      Assert.equal "example/agent-a" (text recorded["actor"].["id"])
                      let references = recorded["memberCheckpoints"].AsArray()
                      Assert.equal ownId (text references[0].["checkpointId"])
                      Assert.isTrue (isNull references[1].["checkpointId"]) "FEAT-2 has no checkpoint of its own"
                      let context = (run clone None [ "work"; "context"; "FEAT-1" ] |> ok).Json
                      Assert.equal ownId (text context["workItems"].[0].["latestCheckpoint"].["id"])
                      let shown = (run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001"; "--json" ] |> ok).Json
                      Assert.equal (text recorded["checkpointId"]) (text shown["latestCheckpoint"].["checkpointId"])
                      let textShown = run clone None [ "work"; "group"; "show"; "GROUP-FIXTURE-001" ] |> ok
                      Assert.isTrue (textShown.Output.Contains "shared decision: D1 one store") textShown.Output
                      pushAll clone "group checkpoint" |> ignore
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "work group checkpoint (cli): unpushed and uncommitted work are refused like work checkpoint; blank text exits 2; unknown group exits 1; nothing is recorded"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      capture clone "FEAT-1"
                      create clone "GROUP-FIXTURE-001" [ "FEAT-1" ] [] |> ok |> ignore
                      pushAll clone "praxis state" |> ignore
                      let before = File.ReadAllText(groupsPath clone)

                      let blank =
                          cli clone [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "  "; "--next-action"; "x"; "--json" ]

                      Assert.equal 2 blank.ExitCode
                      Assert.equal [ "blank-summary" ] (rejectionCodes blank)
                      GitFixture.write clone "src/feature.txt" "one\n"
                      GitFixture.commitAll clone "not pushed" |> ignore
                      let ahead = groupCheckpoint clone "GROUP-FIXTURE-001" []
                      Assert.equal 1 ahead.ExitCode
                      Assert.equal [ "local-ahead" ] (rejectionCodes ahead)
                      GitFixture.git clone [ "push"; "-q" ] |> ignore
                      GitFixture.write clone "src/feature.txt" "two\n"
                      Assert.equal [ "uncommitted-changes" ] (rejectionCodes (groupCheckpoint clone "GROUP-FIXTURE-001" []))
                      Assert.equal 2 (cli clone [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-001"; "--occurred-at"; now (); "--summary"; "x" ]).ExitCode
                      let unknown = groupCheckpoint clone "GROUP-NOPE-001" []
                      Assert.equal 1 unknown.ExitCode
                      Assert.equal [ "unknown-group" ] (rejectionCodes unknown)
                      Assert.equal before (File.ReadAllText(groupsPath clone))) } ]

    let tests = createTests @ showTests @ addTests @ removeTests @ checkpointTests
