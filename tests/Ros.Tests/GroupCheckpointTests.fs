namespace Ros.Tests

open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Git
open Ros.Domain.Planning
open Ros.Domain.Work

/// `work group checkpoint`: a durable group-level checkpoint over the
/// members' own checkpoints (PRAXIS-GROUP-05; requirements/PLANNING-WORK-GROUPS.md
/// PRX-GRP-043, PRX-GRP-044).
module GroupCheckpointTests =
    let private t name run = { Name = $"work group checkpoint: {name}"; Run = run }

    // ---- Domain ------------------------------------------------------------------

    let private commit (digit: char) = (CommitId.tryParse (System.String(digit, 40))).Value
    let private remote: RemoteIdentity = { Name = "origin"; Url = Some "https://example.invalid/praxis.git" }

    let private location: GitDurableLocation =
        { Repository = "praxis"
          Branch = "feature/group"
          LocalCommit = commit 'a'
          Remote = remote
          RemoteBranch = "feature/group"
          RemoteCommit = commit 'a' }

    let private group: StoredGroup =
        { Declaration =
            { Id = "GROUP-PRAXIS-TEST-001"
              Members = [ "M-1"; "M-2"; "M-3"; "M-4"; "M-5" ]
              Kind = Some GroupKind.SharedApiSurface
              Origin = GroupOrigin.HumanDeclared
              SharedContext = []
              ExecutionRepository = Some "praxis"
              CrossRepository = false
              ArchitectureNotes = [] }
          CreatedAt = "2026-09-30T10:00:00.000Z"
          CreatedBy = "agent:declarer"
          Additions = []
          Removals = [] }

    let private reference id checkpointId : MemberCheckpointReference =
        { Member = id
          CheckpointId = checkpointId
          ExecutionId = $"EXE-{id}"
          Commit = (commit 'b').Value
          RecordedAt = "2026-09-30T11:00:00.000Z" }

    let private observations: GroupCheckpointObservations =
        { Stored = [ group ]
          Lifecycle = Map.ofList [ "M-1", "active"; "M-2", "active"; "M-3", "complete"; "M-4", "abandoned"; "M-5", "ready" ]
          MemberCheckpoints = Map.ofList [ "M-1", reference "M-1" "cp-m1"; "M-3", reference "M-3" "cp-m3"; "OTHER", reference "OTHER" "cp-other" ]
          Executions = Map.ofList [ "M-1", ExecutionObservation.Resolved("EXE-mine", []); "M-2", ExecutionObservation.NoneActive ]
          History = []
          Durability = Ok location }

    let private request: GroupCheckpointRequest =
        { GroupId = "GROUP-PRAXIS-TEST-001"
          Repository = "praxis"
          Summary = "  Shared parser landed  "
          NextAction = "Implement M-2 on the shared parser"
          SharedDecisions = [ "one JSON contract for every member" ]
          OccurredAt = "2026-09-30T12:00:00.000Z"
          RecordedBy = "agent:grouper" }

    let private recorded observations request =
        match GroupCheckpoints.decide observations request with
        | Ok checkpoint -> checkpoint
        | Error found -> failwith $"%A{found}"

    let private rejected observations request =
        match GroupCheckpoints.decide observations request with
        | Error found -> found
        | Ok _ -> failwith "expected a rejection"

    // ---- CLI fixture -------------------------------------------------------------

    let private agentA = PraxisCli.agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = PraxisCli.agent "example/agent-b" "example" "agent-b" "session-b"

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "group-checkpoint"

        try
            let _, clone = PraxisCli.installedRepository parent "clone"
            test clone
        finally
            GitFixture.cleanup parent

    let private start clone who (id: string) =
        PraxisCli.run clone (Some who) [ "work"; "start"; "--id"; id; "--type"; "feature"; "--occurred-at"; PraxisCli.now () ] |> PraxisCli.ok |> ignore

    /// FEAT-1 (agent A, with its own checkpoint) and FEAT-2 (agent B, none)
    /// active in the stored group GROUP-FIXTURE-CLI-001, everything pushed.
    let private groupedRepository (test: string -> unit) =
        withRepository (fun clone ->
            start clone agentA "FEAT-1"
            start clone agentB "FEAT-2"
            GitFixture.write clone "src/feat1.txt" "one\n"
            PraxisCli.pushAll clone "FEAT-1 slice" |> ignore

            PraxisCli.run
                clone
                (Some agentA)
                [ "work"; "checkpoint"; "--id"; "FEAT-1"; "--occurred-at"; PraxisCli.now (); "--summary"; "FEAT-1 slice"; "--next-action"; "next" ]
            |> PraxisCli.ok
            |> ignore

            PraxisCli.run
                clone
                (Some agentA)
                [ "work"; "group"; "create"; "--id"; "GROUP-FIXTURE-CLI-001"; "--member"; "FEAT-1"; "--member"; "FEAT-2"; "--occurred-at"; PraxisCli.now () ]
            |> PraxisCli.ok
            |> ignore

            PraxisCli.pushAll clone "Praxis state" |> ignore
            test clone)

    let private groupCheckpoint clone who (extra: string list) =
        PraxisCli.run
            clone
            (Some who)
            ([ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CLI-001"; "--occurred-at"; PraxisCli.now ()
               "--summary"; "Shared design settled"; "--next-action"; "Implement FEAT-2"; "--decision"; "one parser for both"; "--json" ]
             @ extra)

    let private codes (result: PraxisCli.Result) =
        result.Json["rejections"].AsArray() |> Seq.map (fun node -> PraxisCli.text node["code"]) |> Seq.toList

    let private read clone relative = File.ReadAllText(Path.Combine(clone, relative))
    let private checkpointsFile clone = Path.Combine(clone, ".ros", "work", "group-checkpoints.json")

    let tests =
        [ t "records members by their own states and references their own checkpoints" (fun () ->
              let checkpoint = recorded observations request
              Assert.equal "GROUP-PRAXIS-TEST-001/GCP-001" checkpoint.Id
              Assert.equal [ "M-1"; "M-2" ] checkpoint.Progress.Active
              Assert.equal [ "M-3" ] checkpoint.Progress.Completed
              Assert.equal [ "M-4" ] checkpoint.Progress.Abandoned
              Assert.equal [ "M-5" ] checkpoint.Progress.Remaining
              Assert.equal [ reference "M-1" "cp-m1"; reference "M-3" "cp-m3" ] checkpoint.MemberCheckpoints
              Assert.equal [ "M-2"; "M-4"; "M-5" ] checkpoint.UncheckpointedMembers
              Assert.equal [ ({ Member = "M-1"; ExecutionId = "EXE-mine" }: GroupMemberExecution) ] checkpoint.Executions
              Assert.equal location checkpoint.Location
              Assert.equal "Shared parser landed" checkpoint.Summary
              Assert.equal [ "one JSON contract for every member" ] checkpoint.SharedDecisions)

          t "checkpoint IDs are sequential per group" (fun () ->
              let first = recorded observations request
              let second = recorded { observations with History = [ first ] } { request with OccurredAt = "2026-09-30T13:00:00.000Z" }
              Assert.equal "GROUP-PRAXIS-TEST-001/GCP-002" second.Id)

          t "the durability verdict of work checkpoint refuses it, with work checkpoint's own codes" (fun () ->
              let refusals =
                  [ CheckpointRejection.LocalAhead(2, remote, "feature/group")
                    CheckpointRejection.UncommittedChanges [ "src/x.fs" ] ]

              let found = rejected { observations with Durability = Error refusals } request
              Assert.equal (refusals |> List.map GroupCheckpointRejection.NotDurable) found
              Assert.equal [ "local-ahead"; "uncommitted-changes" ] (found |> List.map GroupCheckpoints.code))

          t "an unknown group, a group with no active member and an executor with no execution are refused" (fun () ->
              Assert.equal
                  [ GroupCheckpointRejection.UnknownGroup "GROUP-PRAXIS-TEST-404" ]
                  (rejected observations { request with GroupId = "GROUP-PRAXIS-TEST-404" })

              let idle = observations.Lifecycle |> Map.map (fun _ state -> if state = "active" then "blocked" else state)
              Assert.equal [ GroupCheckpointRejection.NoActiveMember "GROUP-PRAXIS-TEST-001" ] (rejected { observations with Lifecycle = idle } request)

              let foreign = Map.ofList [ "M-1", ExecutionObservation.NoneActive; "M-2", ExecutionObservation.Refused "not yours" ]

              Assert.equal
                  [ GroupCheckpointRejection.NoOwnExecution("GROUP-PRAXIS-TEST-001", [ "M-1: none is yours"; "M-2: not yours" ]) ]
                  (rejected { observations with Executions = foreign } request))

          t "blank text, a bad timestamp and a backdated checkpoint are refused together" (fun () ->
              let found =
                  rejected observations { request with Summary = " "; NextAction = ""; SharedDecisions = [ "" ]; OccurredAt = "later" }

              Assert.equal
                  [ GroupCheckpointRejection.BlankSummary
                    GroupCheckpointRejection.BlankNextAction
                    GroupCheckpointRejection.BlankSharedDecision
                    GroupCheckpointRejection.InvalidTimestamp "later" ]
                  found

              Assert.isTrue (found |> List.forall GroupCheckpoints.isArgumentError) "blank text is the caller's to fix"
              let first = recorded observations request

              Assert.equal
                  [ GroupCheckpointRejection.PredatesHistory("2026-09-30T11:00:00.000Z", "2026-09-30T12:00:00.000Z") ]
                  (rejected { observations with History = [ first ] } { request with OccurredAt = "2026-09-30T11:00:00.000Z" }))

          t "group checkpoints round-trip through the JSON contract" (fun () ->
              let first = recorded observations request
              let second = recorded { observations with History = [ first ] } { request with OccurredAt = "2026-09-30T13:00:00.000Z" }
              let rendered = PlanningJson.renderGroupCheckpoints [ first; second ]
              Assert.equal (Ok [ first; second ]) (PlanningJson.parseGroupCheckpoints rendered)
              Assert.isTrue (PlanningJson.parseGroupCheckpoints "{\"schema\":\"other\",\"checkpoints\":[]}" |> Result.isError) "an unknown schema is refused")

          t "validate refuses a reference to another member's checkpoint and an unknown group" (fun () ->
              let checkpoint = recorded observations request
              let owners = Map.ofList [ "cp-m1", "M-1"; "cp-m3", "M-3" ]
              Assert.equal [] (GroupCheckpoints.findings (set [ group.Declaration.Id ]) owners [ checkpoint ])
              let laundered = { checkpoint with MemberCheckpoints = [ { reference "M-1" "cp-m3" with Member = "M-1" } ] }

              let messages =
                  GroupCheckpoints.findings Set.empty owners [ laundered ] |> List.map (fun finding -> finding.Field, finding.Message)

              Assert.equal
                  [ "groupId", "group GROUP-PRAXIS-TEST-001 is not a stored group"
                    "memberCheckpoints", "checkpoint cp-m3 belongs to M-3, not M-1; a member never claims another's checkpoint" ]
                  messages)

          t "cli records a verified group checkpoint and changes no member's checkpoint, events or state" (fun () ->
              groupedRepository (fun clone ->
                  let events = read clone ".ros/events/events.jsonl"
                  let context = read clone ".ros/context/current.json"
                  let head = GitFixture.git clone [ "rev-parse"; "HEAD" ]
                  let result = groupCheckpoint clone agentA [] |> PraxisCli.ok
                  let checkpoint = result.Json["checkpoint"]
                  Assert.equal "work-group-checkpoint-recorded" (PraxisCli.text result.Json["kind"])
                  Assert.equal "GROUP-FIXTURE-CLI-001/GCP-001" (PraxisCli.text checkpoint["id"])
                  Assert.equal head (PraxisCli.text checkpoint["location"].["localCommit"])
                  Assert.equal head (PraxisCli.text checkpoint["location"].["remoteCommit"])
                  Assert.equal "feature/x" (PraxisCli.text checkpoint["location"].["branch"])
                  Assert.equal [ "FEAT-1"; "FEAT-2" ] (checkpoint["members"].["active"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList)
                  Assert.equal "FEAT-1" (PraxisCli.text checkpoint["memberCheckpoints"].[0].["member"])
                  Assert.equal [ "FEAT-2" ] (checkpoint["uncheckpointedMembers"].AsArray() |> Seq.map PraxisCli.text |> Seq.toList)
                  Assert.equal "FEAT-1" (PraxisCli.text checkpoint["executions"].[0].["member"])
                  Assert.equal 1 (checkpoint["executions"].AsArray().Count)
                  Assert.equal "one parser for both" (PraxisCli.text checkpoint["sharedDecisions"].[0])
                  Assert.equal events (read clone ".ros/events/events.jsonl")
                  Assert.equal context (read clone ".ros/context/current.json")
                  let recordedId = PraxisCli.text checkpoint["memberCheckpoints"].[0].["checkpointId"]
                  let history = PraxisCli.run clone None [ "work"; "checkpoint"; "show"; "FEAT-1"; "--json" ] |> PraxisCli.ok
                  Assert.isTrue (history.Output.Contains recordedId) "the reference is FEAT-1's own checkpoint"
                  PraxisCli.run clone None [ "validate" ] |> PraxisCli.ok |> ignore))

          t "cli refuses an unpushed commit or uncommitted work exactly as work checkpoint does, writing nothing" (fun () ->
              groupedRepository (fun clone ->
                  GitFixture.write clone "src/feat2.txt" "two\n"
                  let dirty = groupCheckpoint clone agentA []
                  Assert.equal 1 dirty.ExitCode
                  Assert.equal [ "uncommitted-changes" ] (codes dirty)
                  GitFixture.commitAll clone "unpushed" |> ignore
                  let unpushed = groupCheckpoint clone agentA []
                  Assert.equal 1 unpushed.ExitCode
                  Assert.equal [ "local-ahead" ] (codes unpushed)
                  Assert.isTrue (not (File.Exists(checkpointsFile clone))) "nothing was recorded"))

          t "cli refuses an executor with no execution of an active member" (fun () ->
              groupedRepository (fun clone ->
                  let stranger = PraxisCli.agent "example/agent-c" "example" "agent-c" "session-c"
                  let result = groupCheckpoint clone stranger []
                  Assert.equal 1 result.ExitCode
                  Assert.equal [ "missing-execution" ] (codes result)
                  Assert.isTrue (not (File.Exists(checkpointsFile clone))) "nothing was recorded"))

          t "cli dry run verifies and writes nothing; malformed arguments exit 2" (fun () ->
              groupedRepository (fun clone ->
                  let planned = groupCheckpoint clone agentB [ "--dry-run" ] |> PraxisCli.ok
                  Assert.equal "work-group-checkpoint-planned" (PraxisCli.text planned.Json["kind"])
                  Assert.equal "FEAT-2" (PraxisCli.text planned.Json["checkpoint"].["executions"].[0].["member"])
                  Assert.isTrue (not (File.Exists(checkpointsFile clone))) "a dry run records nothing"

                  let missing =
                      PraxisCli.run clone (Some agentA) [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CLI-001"; "--occurred-at"; PraxisCli.now () ]

                  Assert.equal 2 missing.ExitCode

                  let blank =
                      PraxisCli.run
                          clone
                          (Some agentA)
                          [ "work"; "group"; "checkpoint"; "--id"; "GROUP-FIXTURE-CLI-001"; "--occurred-at"; PraxisCli.now (); "--summary"; " "; "--next-action"; "x"; "--json" ]

                  Assert.equal 2 blank.ExitCode
                  Assert.equal [ "blank-summary" ] (codes blank)

                  let withMember = groupCheckpoint clone agentA [ "--member"; "FEAT-1" ]
                  Assert.equal 2 withMember.ExitCode)) ]
