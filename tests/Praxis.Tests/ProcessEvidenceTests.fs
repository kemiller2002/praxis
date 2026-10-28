namespace Praxis.Tests

open System.IO
open System.Text.Json.Nodes
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// WI-0103: process evidence recorded in the existing event log, with the
/// statement kept apart from what Praxis observed and a provenance class that
/// writing a record can never upgrade.
[<RequireQualifiedAccess>]
module ProcessEvidenceTests =
    open PraxisCli

    let private sha = String.replicate 40 "a"

    let private request kind =
        { WorkItemId = "FEAT-1"
          OccurredAt = "2026-10-28T08:00:00Z"
          Kind = Some kind
          Summary = Some "statement"
          Subjects = [ "REQ-1" ]
          Candidate = Some sha
          References = []
          Decision = None
          Outcome = None
          Attestation = None }

    let private statementOf (value: ProcessEvidenceRequest) =
        match ProcessEvidence.validate value with
        | Ok statement -> statement
        | Error errors -> failwith (String.concat "; " errors)

    let private errorsOf (value: ProcessEvidenceRequest) =
        match ProcessEvidence.validate value with
        | Ok _ -> []
        | Error errors -> errors

    let private actorOf kind =
        { Kind = kind
          Id = "someone"
          Provider = None
          Model = None
          Runtime = None }

    let private discovered = { Mechanism = "whitelisted-github-actions-environment"; ActorKindSource = "discovery-mechanism"; RunId = Some "1" }
    let private declared = { Mechanism = "explicit-environment"; ActorKindSource = "environment"; RunId = None }

    let private classOf actorKind basis value =
        (ProcessEvidence.classify (actorOf actorKind) basis (statementOf value)).Class

    let private agentA = agent "example/agent-a" "example" "agent-a" "session-a"
    let private agentB = agent "example/agent-b" "example" "agent-b" "session-b"

    let private withRepository (test: string -> unit) =
        let parent = GitFixture.temporaryDirectory "process-evidence"

        try
            let _, clone = installedRepository parent "clone"
            test clone
        finally
            GitFixture.cleanup parent

    let private evidenceEvents (clone: string) =
        File.ReadAllLines(Path.Combine(clone, ".ros", "events", "events.jsonl"))
        |> Array.filter (fun line -> line.Contains "\"work.evidence.recorded\"")
        |> Array.map (fun line -> JsonNode.Parse line :?> JsonObject)
        |> Array.toList

    let private record clone who (arguments: string list) =
        run clone (Some who) ([ "work"; "evidence"; "--id"; "FEAT-1"; "--occurred-at"; now (); "--json" ] @ arguments)

    let tests =
        [ { Name = "process evidence: arguments are refused with one diagnostic per problem"
            Run =
              fun () ->
                  Assert.isTrue (errorsOf { request "independent-review" with Kind = None } |> List.exists (fun e -> e.Contains "--kind")) "missing kind"
                  Assert.isTrue (errorsOf (request "audit") |> List.exists (fun e -> e.Contains "'audit'")) "unknown kind"
                  Assert.isTrue (errorsOf { request "test-result" with Outcome = Some "green" } |> List.exists (fun e -> e.Contains "'green'")) "bad outcome"
                  Assert.isTrue (errorsOf (request "test-result") |> List.exists (fun e -> e.Contains "requires --outcome")) "missing outcome"
                  Assert.isTrue (errorsOf { request "independent-review" with Outcome = Some "approved"; Candidate = None } |> List.exists (fun e -> e.Contains "--candidate")) "review without candidate"
                  Assert.isTrue (errorsOf { request "release-selection" with Candidate = Some "abc123" } |> List.exists (fun e -> e.Contains "40-character")) "short sha"
                  Assert.isTrue (errorsOf { request "scope-change" with Subjects = [] } |> List.exists (fun e -> e.Contains "--subject")) "scope change without subject"
                  Assert.isTrue (errorsOf { request "release-selection" with Attestation = Some "github-review:x" } |> List.exists (fun e -> e.Contains "owner-approval")) "attestation elsewhere"
                  Assert.isTrue (errorsOf { request "owner-approval" with Outcome = Some "approved"; Attestation = Some "email:x" } |> List.exists (fun e -> e.Contains "'email'")) "unknown attestation"
                  Assert.isTrue (errorsOf { request "release-selection" with Summary = Some "  " } |> List.exists (fun e -> e.Contains "--summary")) "blank summary"
                  Assert.empty (errorsOf (request "release-selection")) }
          { Name = "process evidence: an agent's statement stays an assertion whatever it claims"
            Run =
              fun () ->
                  let ownerApproval = { request "owner-approval" with Outcome = Some "approved" }
                  Assert.equal EvidenceProvenance.AgentAssertion (classOf ActorKind.Agent declared ownerApproval)
                  let transcribed = ProcessEvidence.classify (actorOf ActorKind.Agent) declared (statementOf ownerApproval)
                  Assert.equal (Some "transcribed") transcribed.Authenticity
                  Assert.equal EvidenceProvenance.AgentAssertion (classOf ActorKind.Agent declared (request "release-selection"))
                  Assert.equal EvidenceProvenance.AgentAssertion (classOf ActorKind.Agent discovered { request "test-result" with Outcome = Some "pass" })
                  Assert.equal EvidenceProvenance.ReviewerFinding (classOf ActorKind.Agent declared { request "independent-review" with Outcome = Some "approved" }) }
          { Name = "process evidence: an owner decision needs a human actor and is unattested without an attestation"
            Run =
              fun () ->
                  let approval = { request "owner-approval" with Outcome = Some "approved" }
                  let plain = ProcessEvidence.classify (actorOf ActorKind.Human) declared (statementOf approval)
                  Assert.equal EvidenceProvenance.OwnerDecision plain.Class
                  Assert.equal (Some "unattested") plain.Authenticity
                  let attested = ProcessEvidence.classify (actorOf ActorKind.Human) declared (statementOf { approval with Attestation = Some "github-review:o/r#1@2" })
                  Assert.equal (Some "attestation-referenced") attested.Authenticity }
          { Name = "process evidence: only undeclared CI automation is tool-observed"
            Run =
              fun () ->
                  let result = { request "test-result" with Outcome = Some "pass" }
                  Assert.equal EvidenceProvenance.ToolObserved (classOf ActorKind.Automation discovered result)
                  Assert.equal EvidenceProvenance.AgentAssertion (classOf ActorKind.Automation { discovered with ActorKindSource = "flag" } result)
                  Assert.equal EvidenceProvenance.AgentAssertion (classOf ActorKind.Automation declared result)
                  let verified =
                      ProcessEvidenceKind.all
                      |> List.map (fun kind -> { request (ProcessEvidenceKind.code kind) with Outcome = ProcessEvidenceKind.outcomes kind |> Option.map List.head })
                      |> List.collect (fun value ->
                          [ ActorKind.Agent; ActorKind.Human; ActorKind.Automation ]
                          |> List.collect (fun kind -> [ classOf kind discovered value; classOf kind declared value ]))

                  Assert.isTrue (verified |> List.forall ((<>) EvidenceProvenance.VerifiedAcceptance)) "a record was classified verified-acceptance" }
          { Name = "cli work evidence: a statement is appended to the event log with observed commit, execution and identity"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      let head = (GitFixture.git clone [ "rev-parse"; "HEAD" ]).Trim()

                      let result =
                          record clone agentA [ "--kind"; "release-selection"; "--summary"; "candidate chosen"; "--candidate"; head; "--reference"; "evidence/verdict.json" ]
                          |> ok

                      let event = result.Json
                      Assert.equal "work.evidence.recorded" (text event["type"])
                      Assert.equal "release-selection" (text event["kind"])
                      Assert.equal "agent-assertion" (text event["provenance"].["class"])
                      Assert.equal head (text event["statement"].["candidate"])
                      Assert.equal head (text event["observed"].["commit"])
                      Assert.isTrue ((text event["observed"].["execution"]).StartsWith "EXE-") "no execution observed"
                      Assert.equal "self-reported" (text event["observed"].["identity"].["assurance"])
                      Assert.equal "example/agent-a" (text event["actor"].["id"])
                      let stored = evidenceEvents clone |> Assert.single
                      Assert.equal (text event["eventId"]) (text stored["eventId"])
                      run clone None [ "validate" ] |> ok |> ignore) }
          { Name = "cli work evidence: another actor's execution is refused and nothing is recorded"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore
                      let first = record clone agentA [ "--kind"; "assignment"; "--summary"; "owns src"; "--subject"; "src/" ] |> ok
                      let executionOfA = text first.Json["observed"].["execution"]

                      let borrowed =
                          record clone agentB [ "--kind"; "assignment"; "--summary"; "owns src"; "--subject"; "src/"; "--execution"; executionOfA ]

                      Assert.equal 1 borrowed.ExitCode
                      Assert.isTrue (borrowed.Error.Contains "does not match execution") borrowed.Error
                      Assert.equal 1 (evidenceEvents clone).Length) }
          { Name = "cli work evidence: an agent-transcribed owner approval is recorded as the agent's assertion; bad arguments exit 2"
            Run =
              fun () ->
                  withRepository (fun clone ->
                      run clone (Some agentA) [ "work"; "start"; "--id"; "FEAT-1"; "--type"; "feature"; "--occurred-at"; now () ] |> ok |> ignore

                      let transcribed =
                          record clone agentA [ "--kind"; "owner-approval"; "--summary"; "owner said yes in chat"; "--outcome"; "approved"; "--subject"; "DF-1" ]
                          |> ok

                      Assert.equal "agent-assertion" (text transcribed.Json["provenance"].["class"])
                      Assert.equal "transcribed" (text transcribed.Json["provenance"].["authenticity"])
                      let bad = record clone agentA [ "--kind"; "test-result"; "--summary"; "x"; "--candidate"; "abc" ]
                      Assert.equal 2 bad.ExitCode
                      Assert.equal 1 (evidenceEvents clone).Length) } ]
