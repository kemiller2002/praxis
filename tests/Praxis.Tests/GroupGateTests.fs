namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Work

/// PRX-GRP-133..135 (PRAXIS-GROUP-10; PRX-GRP-190 cases 36-37): the
/// grouped-execution completion gates, decided purely and enforced through
/// the real `work complete`.
[<RequireQualifiedAccess>]
module GroupGateTests =
    open PraxisCli

    let private t name run = { Name = $"group gates: {name}"; Run = run }

    let private subject =
        { WorkItemId = "ITEM-1"
          GroupId = "GROUP-AREA-001"
          GroupExecutionId = "GEX-20261006T120000000Z-abc123" }

    let private criteria = [ "list is read-only"; "repeats are idempotent" ]

    let private analysis: GroupAnalysis =
        { GroupId = subject.GroupId
          GroupExecutionId = Some subject.GroupExecutionId
          Members =
            [ { WorkItemId = "ITEM-1"; Repository = None; AcceptanceCriteria = criteria }
              { WorkItemId = "ITEM-2"; Repository = None; AcceptanceCriteria = [ "other" ] } ]
          ReuseInventory = [ { Element = "WorkGroups"; Location = "src/a.fs:1"; Disposition = "extended"; Reason = "one module" } ]
          Searches = []
          NewAbstractions = [ { Name = "GroupStatus"; ConsideredExisting = "MemberCategory"; WhyNotReused = "per member only" } ] }

    let private row criterion status deferred : VerificationRow =
        { Member = "ITEM-1"
          Criterion = criterion
          Status = status
          Evidence = { Kind = "test"; Reference = "work group: list"; Result = "passed" }
          DeferredTo = deferred }

    let private verification: GroupVerification =
        { GroupId = subject.GroupId
          GroupExecutionId = Some subject.GroupExecutionId
          Rows = criteria |> List.map (fun criterion -> row criterion CriterionStatus.Met None) }

    let private facts =
        { AnalysisPrecedesChanges = Ok true
          PathExists = fun path -> path = "src/a.fs"
          Standing =
            fun id ->
                match id with
                | "LATER-1" -> MemberStanding.Open "captured"
                | "DONE-1" -> MemberStanding.Terminal "complete"
                | _ -> MemberStanding.Unknown }

    let private parsed value = SourceObservation.Supplied("doc.json", EvidenceReading.Parsed value)
    let private judge facts' analysis' verification' = GroupGates.judge subject facts' (parsed analysis') (parsed verification')

    let private problems (status: FacetStatus) =
        match status with
        | FacetStatus.NotSatisfied reasons
        | FacetStatus.Unavailable reasons -> reasons
        | _ -> []

    let private refusedFor (fragment: string) (status: FacetStatus) =
        match status with
        | FacetStatus.NotSatisfied reasons when reasons |> List.exists (fun reason -> reason.Contains fragment) -> ()
        | other -> failwith $"expected a refusal mentioning '{fragment}', got {other}"

    // ---- CLI fixture ----

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private cli clone arguments = run clone (Some agentA) arguments
    let private file clone relative = Path.Combine(clone, relative)

    /// ITEM-1 and LATER-1 recorded, ITEM-1 begun and placed in a group
    /// execution in `mode` (the record `plan execute-group` writes).
    let private withGroupedMember (mode: string) (test: string -> string -> unit) =
        let parent = GitFixture.temporaryDirectory "group-gates"

        try
            let _, clone = installedRepository parent "clone"

            for id in [ "ITEM-1"; "LATER-1" ] do
                cli clone [ "add"; $"Work {id}"; "--id"; id ] |> ok |> ignore

            cli clone [ "work"; "backlog-transition"; "--id"; "ITEM-1"; "--action"; "ready"; "--occurred-at"; now () ] |> ok |> ignore
            cli clone [ "work"; "group"; "create"; "--group"; subject.GroupId; "--member"; "ITEM-1"; "--occurred-at"; now () ] |> ok |> ignore
            cli clone [ "work"; "start"; "--id"; "ITEM-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
            let context = JsonNode.Parse(File.ReadAllText(file clone ".ros/context/current.json"))
            let item = context["workItems"].AsArray() |> Seq.find (fun node -> text node["id"] = "ITEM-1")
            let executionId = text (item["telemetryExecutionIds"].AsArray() |> Seq.last)
            let store = JsonNode.Parse(File.ReadAllText(file clone ".ros/work/groups.json"))

            let execution =
                JsonNode.Parse(
                    $"""{{"id":"{subject.GroupExecutionId}","groupId":"{subject.GroupId}","actor":{{"kind":"agent","id":"example/agent-a"}},"startedAt":"{now ()}","repository":"x","order":["ITEM-1"],"mode":"{mode}","basis":["test fixture"],"optOuts":[],"members":[{{"workItemId":"ITEM-1","executionId":"{executionId}","begunAt":"{now ()}","mode":"{mode}"}}],"fallback":null,"endedAt":null,"successors":[]}}"""
                )

            store["groups"].[0].["executions"] <- JsonArray(execution)
            File.WriteAllText(file clone ".ros/work/groups.json", store.ToJsonString())
            pushAll clone "begin ITEM-1 in a group execution" |> ignore
            test clone executionId
        finally
            GitFixture.cleanup parent

    let private analysisJson =
        let criteriaJson = criteria |> List.map (fun criterion -> $"\"{criterion}\"") |> String.concat ","

        $"""{{"schema":"praxis.group-analysis/1","groupId":"{subject.GroupId}","groupExecutionId":"{subject.GroupExecutionId}","members":[{{"workItemId":"ITEM-1","repository":null,"acceptanceCriteria":[{criteriaJson}]}}],"reuseInventory":[{{"element":"WorkGroups","location":"src/one.txt:1","disposition":"extended","reason":"one module"}}],"searches":[],"newAbstractions":[]}}"""

    let private verificationJson (status: string) (location: string) =
        let rows =
            criteria
            |> List.map (fun criterion -> $"""{{"member":"ITEM-1","criterion":"{criterion}","status":"{status}","evidence":{{"kind":"location","reference":"{location}","result":"read"}}}}""")
            |> String.concat ","

        $"""{{"schema":"praxis.group-verification/1","groupId":"{subject.GroupId}","groupExecutionId":"{subject.GroupExecutionId}","rows":[{rows}]}}"""

    let private lifecycle clone =
        [ ".ros/work/queue.json"; ".ros/context/current.json"; ".ros/events/events.jsonl" ]
        |> List.map (fun relative -> Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file clone relative))))

    let private complete clone (evidence: string list) =
        cli clone [ "work"; "checkpoint"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--summary"; "done"; "--next-action"; "complete" ] |> ok |> ignore
        let before = lifecycle clone

        let result =
            cli clone ([ "work"; "complete"; "--id"; "ITEM-1"; "--occurred-at"; now (); "--evidence"; "implementation=src/one.txt"; "--evidence"; "tests=src/one.txt" ] @ (evidence |> List.collect (fun entry -> [ "--evidence"; entry ])))

        result, before

    let tests =
        [ t "valid, consistent analysis and verification satisfy group-verified" (fun () ->
              match judge facts analysis verification with
              | FacetStatus.Satisfied evidence -> Assert.isTrue (evidence.Head.Contains subject.GroupExecutionId) $"{evidence}"
              | other -> failwith $"{other}")

          t "missing, ambiguous or malformed evidence is unavailable, never a pass" (fun () ->
              match GroupGates.judge subject facts SourceObservation.NotSupplied (parsed verification) with
              | FacetStatus.Unavailable [ reason ] -> Assert.isTrue (reason.Contains "group-analysis=PATH") reason
              | other -> failwith $"{other}"

              match GroupGates.judge subject facts (SourceObservation.Supplied("a.json", EvidenceReading.Malformed "bad")) (SourceObservation.Ambiguous [ "x"; "y" ]) with
              | FacetStatus.Unavailable reasons -> Assert.equal 2 reasons.Length
              | other -> failwith $"{other}"

              Assert.isTrue (CompletionReadiness.isBlocking true (GroupGates.judge subject facts SourceObservation.NotSupplied SourceObservation.NotSupplied)) "missing evidence did not block")

          t "the analysis must name this group and execution, list the member, and name what each new abstraction considered" (fun () ->
              judge facts { analysis with GroupId = "GROUP-OTHER-001" } verification |> refusedFor "names group GROUP-OTHER-001"
              judge facts { analysis with GroupExecutionId = None } verification |> refusedFor "names no group execution"
              judge facts { analysis with Members = analysis.Members.Tail } verification |> refusedFor "does not list ITEM-1"
              judge facts { analysis with NewAbstractions = [ { Name = "Thing"; ConsideredExisting = " "; WhyNotReused = "" } ] } verification |> refusedFor "names no existing element"
              judge facts { analysis with ReuseInventory = [] } verification |> refusedFor "no searches establish"
              let searched = { analysis with ReuseInventory = []; Searches = [ { Command = "grep -rn X src"; Finding = "nothing" } ] }
              Assert.isTrue (match judge facts searched verification with FacetStatus.Satisfied _ -> true | _ -> false) "an empty inventory with searches was refused")

          t "an analysis committed after the member's first change, or of unknown order, is refused" (fun () ->
              judge { facts with AnalysisPrecedesChanges = Ok false } analysis verification |> refusedFor "not committed before"
              judge { facts with AnalysisPrecedesChanges = Error "no start commit" } analysis verification |> refusedFor "is unknown")

          t "the verified criteria must equal the analysis's, and every non-met row must be deferred to a recorded open item" (fun () ->
              judge facts analysis { verification with Rows = verification.Rows.Tail } |> refusedFor "is not verified: list is read-only"
              judge facts analysis { verification with Rows = verification.Rows @ [ row "invented" CriterionStatus.Met None ] } |> refusedFor "not enumerated in the analysis: invented"
              judge facts analysis { verification with Rows = verification.Rows @ [ verification.Rows.Head ] } |> refusedFor "verified more than once"
              let unmet status deferred = { verification with Rows = [ row criteria[0] status deferred; row criteria[1] CriterionStatus.Met None ] }
              judge facts analysis (unmet CriterionStatus.NotMet None) |> refusedFor "is not-met and is not deferred"
              judge facts analysis (unmet CriterionStatus.PartiallyMet (Some "DONE-1")) |> refusedFor "which is complete"
              judge facts analysis (unmet CriterionStatus.Unknown (Some "NOPE-1")) |> refusedFor "not a recorded work item"
              Assert.isTrue (match judge facts analysis (unmet CriterionStatus.NotMet (Some "LATER-1")) with FacetStatus.Satisfied _ -> true | _ -> false) "a deferred criterion was refused"
              judge facts analysis { verification with GroupExecutionId = None } |> refusedFor "does not name group execution")

          t "a location reference must exist at the completion commit" (fun () ->
              let located path = { verification with Rows = verification.Rows |> List.map (fun entry -> { entry with Evidence = { Kind = "location"; Reference = path; Result = "read" } }) }
              Assert.isTrue (match judge facts analysis (located "src/a.fs:12") with FacetStatus.Satisfied _ -> true | _ -> false) "an existing location was refused"
              judge facts analysis (located "src/gone.fs:3") |> refusedFor "src/gone.fs:3 does not exist"
              Assert.equal "src/a.fs" (GroupGates.locationPath "src/a.fs:12")
              Assert.equal "src/a.fs" (GroupGates.locationPath "src/a.fs"))

          t "the documents decode, and this repository's own group analysis is a valid praxis.group-analysis/1" (fun () ->
              match GroupEvidenceJson.decodeAnalysis analysisJson, GroupEvidenceJson.decodeVerification (verificationJson "met" "src/one.txt:1") with
              | EvidenceReading.Parsed read, EvidenceReading.Parsed rows ->
                  Assert.equal criteria read.Members.Head.AcceptanceCriteria
                  Assert.equal 2 rows.Rows.Length
              | other -> failwith $"{other}"

              match GroupEvidenceJson.decodeAnalysis """{"schema":"praxis.group-analysis/2"}""", GroupEvidenceJson.decodeVerification """{"schema":"praxis.group-verification/1","groupId":"G","rows":[{"member":"M"}]}""" with
              | EvidenceReading.Unsupported _, EvidenceReading.Malformed reason -> Assert.isTrue (reason.Contains "rows[0]") reason
              | other -> failwith $"{other}"

              let rec repositoryRoot (directory: DirectoryInfo) =
                  if File.Exists(Path.Combine(directory.FullName, "release.json")) then directory.FullName else repositoryRoot directory.Parent

              let own = Path.Combine(repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory())), "research", "groups", "GROUP-PRAXIS-GROUPING-001", "group-analysis.json")

              match GroupEvidenceJson.decodeAnalysis (File.ReadAllText own) with
              | EvidenceReading.Parsed read -> Assert.equal 5 read.Members.Length
              | other -> failwith $"{other}")

          t "cli a grouped-mode member without group evidence is refused with exit 3 and no state change" (fun () ->
              withGroupedMember "grouped" (fun clone _ ->
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: change" |> ignore
                  let result, before = complete clone []
                  Assert.equal 3 result.ExitCode
                  Assert.isTrue (result.Output.Contains "\"groupVerified\"" && result.Error.Contains "group-verified") result.Error
                  Assert.equal before (lifecycle clone)))

          t "cli a grouped-mode member completes with a committed analysis made before its change and a consistent verification" (fun () ->
              withGroupedMember "grouped" (fun clone _ ->
                  GitFixture.write clone "research/group/analysis.json" analysisJson
                  pushAll clone "group analysis before code" |> ignore
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: change" |> ignore
                  GitFixture.write clone "research/group/verification.json" (verificationJson "met" "src/gone.txt:1")
                  pushAll clone "verification naming a missing file" |> ignore
                  let evidence = [ "group-analysis=research/group/analysis.json"; "group-verification=research/group/verification.json" ]
                  let missing, before = complete clone evidence
                  Assert.equal 3 missing.ExitCode
                  Assert.isTrue (missing.Error.Contains "src/gone.txt:1 does not exist") missing.Error
                  Assert.equal before (lifecycle clone)

                  // Uncommitted evidence is not evidence: a file Git does not
                  // track is read from HEAD, where it does not exist.
                  File.AppendAllText(file clone ".git/info/exclude", "scratch/\n")
                  GitFixture.write clone "scratch/verification.json" (verificationJson "met" "src/one.txt:1")
                  let uncommitted, _ = complete clone [ "group-analysis=research/group/analysis.json"; "group-verification=scratch/verification.json" ]
                  Assert.equal 3 uncommitted.ExitCode
                  Assert.isTrue (uncommitted.Error.Contains "is not committed at HEAD") uncommitted.Error
                  GitFixture.write clone "research/group/verification.json" (verificationJson "met" "src/one.txt:1")
                  pushAll clone "verification" |> ignore
                  let completed, _ = complete clone evidence
                  ok completed |> ignore
                  let context = JsonNode.Parse(File.ReadAllText(file clone ".ros/context/current.json"))
                  let item = context["workItems"].AsArray() |> Seq.find (fun node -> text node["id"] = "ITEM-1")
                  Assert.equal "satisfied" (text item["completionReadiness"].["facets"].["groupVerified"].["status"])))

          t "cli an analysis committed after the member's first change is refused" (fun () ->
              withGroupedMember "grouped" (fun clone _ ->
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: change first" |> ignore
                  GitFixture.write clone "research/group/analysis.json" analysisJson
                  GitFixture.write clone "research/group/verification.json" (verificationJson "met" "src/one.txt")
                  pushAll clone "analysis afterwards" |> ignore
                  let result, before = complete clone [ "group-analysis=research/group/analysis.json"; "group-verification=research/group/verification.json" ]
                  Assert.equal 3 result.ExitCode
                  Assert.isTrue (result.Error.Contains "not committed before the member's first attributed change") result.Error
                  Assert.equal before (lifecycle clone)))

          t "cli a member that executes independently completes under the normal policy without the group gates" (fun () ->
              withGroupedMember "independent" (fun clone _ ->
                  GitFixture.write clone "src/one.txt" "one\n"
                  pushAll clone "ITEM-1: change" |> ignore
                  let result, _ = complete clone []
                  ok result |> ignore)) ]
