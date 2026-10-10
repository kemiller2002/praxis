namespace Praxis.Tests

open System
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Execution
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalAgentHandoffTests =
    let private t name action = { Name = "local handoff: " + name; Run = action }
    let private now = DateTimeOffset.Parse("2026-10-10T12:00:00Z")
    let private sha c = "sha256:" + String(c, 64)
    let private packet: LocalWorkerPacket =
        { SchemaVersion = LocalAgentHandoff.PacketSchema
          DispatchId = "DISPATCH-1"; AttemptId = "ATTEMPT-1"; ParentExecutionId = "EXE-PARENT"; ChildExecutionId = "EXE-CHILD"; WorkerId = "worker-1"
          Role = ExecutionRole.Implementation; RepositoryIdentity = "repo-42"; SourceCommit = String('a', 40)
          SourceManifestDigest = sha 'b'; BlueprintDigest = sha 'c'; GroupId = "GROUP-1"; CohortId = "COHORT-1"
          Members = [ { WorkItemId = "WI-1"; ExecutionId = "EXE-WI1"; RequirementKeys = [ "docs/spec.md#R1" ] }; { WorkItemId = "WI-2"; ExecutionId = "EXE-WI2"; RequirementKeys = [ "docs/naïve.md#R2" ] } ]
          DecisionIds = [ "DEC-1"; "DEC-2" ]; AllowedPaths = [ "src/A.fs"; "src/B.fs" ]
          Prerequisites = [ { WorkItemId = "WI-0"; EvidenceDigest = sha 'd' } ]
          Acceptance =
              [ { ObligationId = "VERIFY-1"; WorkItemId = "WI-1"; RequirementKeys = [ "docs/spec.md#R1" ]; ValidatorId = "independent-test"; InputDigest = sha 'e' }
                { ObligationId = "VERIFY-2"; WorkItemId = "WI-2"; RequirementKeys = [ "docs/naïve.md#R2" ]; ValidatorId = "independent-test"; InputDigest = sha 'f' } ]
          AuthorityRevision = "rev-1"; ReceiptDigest = sha '1'; IssuedAt = now.AddMinutes(-5.); ExpiresAt = now.AddMinutes(30.)
          TimeoutSeconds = 600; MaxOutputBytes = 1048576 }
    let private authority = { Enabled = true; Revision = "rev-1"; ExpectedPacket = packet }
    let private prerequisites = Map.ofList [ "WI-0", sha 'd' ]
    let private result: LocalWorkerResult =
        { SchemaVersion = LocalAgentHandoff.ResultSchema; DispatchId = packet.DispatchId; AttemptId = packet.AttemptId
          ChildExecutionId = packet.ChildExecutionId; WorkerId = packet.WorkerId
          PacketDigest = LocalAgentHandoff.packetDigest packet; OutputCommit = String('b', 40); ChangedPaths = [ "src/A.fs" ]
          Members =
              [ { WorkItemId = "WI-1"; ExecutionId = "EXE-WI1"; ChangedPaths = [ "src/A.fs" ]; Outcome = LocalWorkerOutcome.Submitted; Evidence = [ { ObligationId = "VERIFY-1"; ArtifactPath = "reports/one.json"; Digest = sha '2' } ] }
                { WorkItemId = "WI-2"; ExecutionId = "EXE-WI2"; ChangedPaths = []; Outcome = LocalWorkerOutcome.Submitted; Evidence = [ { ObligationId = "VERIFY-2"; ArtifactPath = "reports/two.json"; Digest = sha '3' } ] } ] }
    let private observed: LocalWorkerResultObservation =
        { WorkerId = packet.WorkerId; ChildExecutionId = packet.ChildExecutionId; AttemptId = packet.AttemptId
          ReservedPacketDigest = result.PacketDigest; OutputCommit = Some result.OutputCommit; SourceIsAncestor = true
          ChangedPaths = Ok result.ChangedPaths
          MemberResults = Map.ofList [ "WI-1", { ExecutionId = "EXE-WI1"; ChangedPaths = Ok [ "src/A.fs" ] }; "WI-2", { ExecutionId = "EXE-WI2"; ChangedPaths = Ok [] } ]
          Evidence = Map.ofList [ "VERIFY-1", ("reports/one.json", sha '2'); "VERIFY-2", ("reports/two.json", sha '3') ]
          Acceptance = packet.Acceptance |> List.map (fun a -> a.ObligationId, { ValidatorId = a.ValidatorId; InputDigest = a.InputDigest; Verdict = Ok() }) |> Map.ofList }
    let private checkPacket candidate = LocalAgentHandoff.validatePacket authority now prerequisites candidate
    let private checkResult observation candidate = LocalAgentHandoff.validateResult authority now prerequisites packet observation candidate
    let private rejected expected = function
        | Error codes -> Assert.isTrue (List.contains expected codes) (sprintf "expected %s, got %A" expected codes)
        | Ok _ -> failwith ("accepted instead of " + expected)
    let private decodeRejected value = Assert.isTrue (Result.isError value) "unsafe JSON decoded"
    let private changeJson (render: 'a -> string) (mutate: JsonNode -> unit) (value: 'a) =
        let node = JsonNode.Parse(render value)
        mutate node
        node.ToJsonString()

    let tests =
        [ t "valid packet and independently observed result only reach awaiting integration" (fun () ->
              Assert.equal (Ok()) (checkPacket packet)
              Assert.equal (Ok LocalResultDisposition.AwaitingIntegration) (checkResult observed result))
          t "strict packet and result round trips retain source identities and lineage" (fun () ->
              Assert.equal (Ok(LocalAgentHandoff.canonicalPacket packet)) (LocalAgentHandoffJson.renderPacket packet |> LocalAgentHandoffJson.readPacket)
              Assert.equal (Ok result) (LocalAgentHandoffJson.renderResult result |> LocalAgentHandoffJson.readResult))
          t "packet identity is order independent and content bound for every field" (fun () ->
              let reordered = { packet with Members = List.rev packet.Members; DecisionIds = List.rev packet.DecisionIds; AllowedPaths = List.rev packet.AllowedPaths; Acceptance = List.rev packet.Acceptance }
              Assert.equal "sha256:bfdb62763eabf54d712de122cb1bf94069a3b89e3e9fc42f9b907bc716d07158" (LocalAgentHandoff.packetDigest packet)
              Assert.equal (LocalAgentHandoff.packetDigest packet) (LocalAgentHandoff.packetDigest reordered)
              Assert.equal (Ok()) (checkPacket reordered)
              let changes =
                  [ { packet with DispatchId = "DISPATCH-2" }; { packet with AttemptId = "ATTEMPT-2" }
                    { packet with ParentExecutionId = "EXE-OTHER" }; { packet with ChildExecutionId = "EXE-OTHER" }; { packet with WorkerId = "worker-2" }
                    { packet with RepositoryIdentity = "repo-43" }; { packet with SourceCommit = String('b', 40) }
                    { packet with SourceManifestDigest = sha 'a' }; { packet with BlueprintDigest = sha 'a' }
                    { packet with GroupId = "GROUP-2" }; { packet with CohortId = "COHORT-2" }
                    { packet with Members = [ packet.Members.Head ] }; { packet with Members = [ { packet.Members.Head with RequirementKeys = [ "other#R" ] }; packet.Members[1] ] }
                    { packet with DecisionIds = [ "DEC-3" ] }; { packet with AllowedPaths = [ "src/C.fs" ] }
                    { packet with Prerequisites = [] }; { packet with Prerequisites = [ { packet.Prerequisites.Head with EvidenceDigest = sha 'a' } ] }
                    { packet with Acceptance = [ { packet.Acceptance.Head with ValidatorId = "worker-self-test" }; packet.Acceptance[1] ] }
                    { packet with Acceptance = [ { packet.Acceptance.Head with InputDigest = sha 'a' }; packet.Acceptance[1] ] }
                    { packet with AuthorityRevision = "rev-2" }; { packet with ReceiptDigest = sha 'a' }
                    { packet with IssuedAt = packet.IssuedAt.AddSeconds 1. }; { packet with ExpiresAt = packet.ExpiresAt.AddSeconds 1. }
                    { packet with TimeoutSeconds = 601 }; { packet with MaxOutputBytes = 1048577 }
                    { packet with Role = ExecutionRole.Verification }; { packet with SchemaVersion = "other/1" } ]
              for changed in changes do
                  Assert.isTrue (LocalAgentHandoff.packetDigest changed <> result.PacketDigest) "changed packet retained fingerprint"
                  checkPacket changed |> rejected "local-packet-not-delegated")
          t "original source coverage and acceptance ownership cannot be dropped, shared or forged" (fun () ->
              for changed in [ { packet with Members = [] }; { packet with Members = packet.Members @ [ packet.Members.Head ] }
                               { packet with Members = [ packet.Members.Head; { packet.Members[1] with RequirementKeys = packet.Members.Head.RequirementKeys } ] } ] do
                  LocalAgentHandoff.packetProblems changed |> fun codes -> Assert.isTrue (List.contains "local-packet-source-coverage" codes) (sprintf "%A" codes)
              checkPacket { packet with Acceptance = packet.Acceptance.Tail } |> rejected "local-packet-acceptance-coverage:WI-1"
              checkPacket { packet with Acceptance = [ { packet.Acceptance.Head with WorkItemId = "WI-2" }; packet.Acceptance[1] ] } |> rejected "local-packet-acceptance")
          t "native member executions and file attribution remain independently bound" (fun () ->
              let duplicate = { packet with Members = [ packet.Members.Head; { packet.Members[1] with ExecutionId = packet.Members.Head.ExecutionId } ] }
              Assert.isTrue (List.contains "local-packet-member-executions" (LocalAgentHandoff.packetProblems duplicate)) "duplicate executions accepted"
              checkResult { observed with MemberResults = Map.empty } result |> rejected "local-result-member-execution-unverified:WI-1"
              checkResult observed { result with Members = [ { result.Members.Head with ExecutionId = "EXE-FORGED" }; result.Members[1] ] } |> rejected "local-result-member-execution-unverified:WI-1"
              let wrong = { observed.MemberResults["WI-1"] with ChangedPaths = Ok [] }
              checkResult { observed with MemberResults = Map.add "WI-1" wrong observed.MemberResults } result |> rejected "local-result-member-paths-unverified:WI-1"
              checkResult observed { result with Members = [ { result.Members.Head with ChangedPaths = [] }; result.Members[1] ] } |> rejected "local-result-attribution-coverage")
          t "authority revocation, revision and exact expiry are rechecked at result intake" (fun () ->
              LocalAgentHandoff.validateResult { authority with Enabled = false } now prerequisites packet observed result |> rejected "local-authority-disabled"
              LocalAgentHandoff.validateResult { authority with Revision = "rev-2" } now prerequisites packet observed result |> rejected "local-authority-revision"
              LocalAgentHandoff.validateResult authority packet.ExpiresAt prerequisites packet observed result |> rejected "local-packet-expired-or-future"
              LocalAgentHandoff.validatePacket authority (packet.IssuedAt.AddTicks(-1L)) prerequisites packet |> rejected "local-packet-expired-or-future")
          t "missing and changed prerequisite evidence cannot become runnable" (fun () ->
              LocalAgentHandoff.validatePacket authority now Map.empty packet |> rejected "local-prerequisite-unverified:WI-0"
              LocalAgentHandoff.validatePacket authority now (Map.ofList [ "WI-0", sha 'e' ]) packet |> rejected "local-prerequisite-unverified:WI-0"
              checkPacket { packet with Prerequisites = [ { packet.Prerequisites.Head with WorkItemId = "WI-1" } ] } |> rejected "local-packet-prerequisites")
          t "unsafe paths, wildcard resources and control state are refused before delegation" (fun () ->
              for path in [ "../escape"; "/absolute"; "src/../escape"; "src//A.fs"; "C:/escape"; "src\\A.fs"; "src/*"; "src/A.fs?"; ".git/config"; ".ROS/work/groups.json"; "x/.praxis/host.json"; "ros.json"; "src/A.fs."; "src/A.fs " ] do
                  checkPacket { packet with AllowedPaths = [ path ] } |> rejected "local-packet-resources"
              checkPacket { packet with AllowedPaths = [ "src/A.fs"; "src/a.fs" ] } |> rejected "local-packet-resources"
              checkResult { observed with ChangedPaths = Ok [ "src/C.fs" ] } { result with ChangedPaths = [ "src/C.fs" ] } |> rejected "local-result-resource-escape")
          t "worker roles cannot receive administration, integration or readonly write authority" (fun () ->
              checkPacket { packet with Role = ExecutionRole.Administration } |> rejected "local-packet-worker-role"
              checkPacket { packet with Role = ExecutionRole.Integration } |> rejected "local-packet-worker-role"
              checkPacket { packet with Role = ExecutionRole.Verification } |> rejected "local-packet-readonly-role"
              let readonly = { packet with Role = ExecutionRole.Verification; AllowedPaths = [] }
              Assert.equal (Ok()) (LocalAgentHandoff.validatePacket { authority with ExpectedPacket = readonly } now prerequisites readonly))
          t "future, excessive lifetime and invalid budgets fail even against matching controller input" (fun () ->
              for changed in [ { packet with TimeoutSeconds = 0 }; { packet with TimeoutSeconds = 86401 }
                               { packet with MaxOutputBytes = -1 }; { packet with MaxOutputBytes = 67108865 }
                               { packet with ExpiresAt = packet.IssuedAt }; { packet with ExpiresAt = packet.IssuedAt.AddHours 25. }
                               { packet with IssuedAt = packet.IssuedAt.ToOffset(TimeSpan.FromHours 1.) } ] do
                  Assert.isTrue (Result.isError (LocalAgentHandoff.validatePacket { authority with ExpectedPacket = changed } now prerequisites changed)) "invalid matching authority input accepted")
          t "strict JSON rejects unknown, omitted and duplicate fields at every object boundary" (fun () ->
              let json = LocalAgentHandoffJson.renderPacket packet
              json.Replace("\"workerId\":", "\"workerId\":\"evil\",\"workerId\":") |> LocalAgentHandoffJson.readPacket |> decodeRejected
              json.Replace("\"workItemId\":", "\"workItemId\":\"evil\",\"workItemId\":") |> LocalAgentHandoffJson.readPacket |> decodeRejected
              for mutate in [ (fun (n: JsonNode) -> n.["approved"] <- JsonValue.Create true)
                              (fun (n: JsonNode) -> n.AsObject().Remove("sourceCommit") |> ignore)
                              (fun (n: JsonNode) -> n.["members"].[0].["approved"] <- JsonValue.Create true)
                              (fun (n: JsonNode) -> n.["acceptance"].[0].["expectedPass"] <- JsonValue.Create true)
                              (fun (n: JsonNode) -> n.["prerequisites"].[0].["verified"] <- JsonValue.Create true) ] do
                  changeJson LocalAgentHandoffJson.renderPacket mutate packet |> LocalAgentHandoffJson.readPacket |> decodeRejected
              let resultJson = LocalAgentHandoffJson.renderResult result
              resultJson.Replace("\"digest\":", "\"digest\":\"forged\",\"digest\":") |> LocalAgentHandoffJson.readResult |> decodeRejected
              changeJson LocalAgentHandoffJson.renderResult (fun (n: JsonNode) -> n.["members"].[0].["evidence"].[0].["approved"] <- JsonValue.Create true) result |> LocalAgentHandoffJson.readResult |> decodeRejected)
          t "bounded JSON refuses malformed types, nulls, absent timezone and unbounded collections" (fun () ->
              for mutate in [ (fun (n: JsonNode) -> n.["sourceCommit"] <- null)
                              (fun (n: JsonNode) -> n.["timeoutSeconds"] <- JsonValue.Create 0.5)
                              (fun (n: JsonNode) -> n.["maxOutputBytes"] <- JsonValue.Create "1048576")
                              (fun (n: JsonNode) -> n.["issuedAt"] <- JsonValue.Create "2026-10-10T11:55:00")
                              (fun (n: JsonNode) -> n.["issuedAt"] <- JsonValue.Create "2026-10-10T12:55:00+01:00")
                              (fun (n: JsonNode) -> n.["decisionIds"] <- JsonValue.Create "DEC-1")
                              (fun (n: JsonNode) -> n.["decisionIds"] <- JsonArray([| for i in 1..257 -> JsonValue.Create("DEC-" + string i) :> JsonNode |])) ] do
                  changeJson LocalAgentHandoffJson.renderPacket mutate packet |> LocalAgentHandoffJson.readPacket |> decodeRejected
              LocalAgentHandoffJson.readPacket (String(' ', 65537)) |> decodeRejected
              LocalAgentHandoffJson.readPacket (String('é', 32769)) |> decodeRejected
              LocalAgentHandoffJson.readPacket "{" |> decodeRejected
              LocalAgentHandoffJson.readPacket "null" |> decodeRejected)
          t "result transport cannot encode completed or silently omit member evidence identity" (fun () ->
              changeJson LocalAgentHandoffJson.renderResult (fun (n: JsonNode) -> n.["members"].[0].["outcome"] <- JsonValue.Create "completed") result |> LocalAgentHandoffJson.readResult |> decodeRejected
              changeJson LocalAgentHandoffJson.renderResult (fun (n: JsonNode) -> (n.["members"].[0].["evidence"].[0]).AsObject().Remove("obligationId") |> ignore) result |> LocalAgentHandoffJson.readResult |> decodeRejected
              LocalAgentHandoffJson.renderResult { result with Members = result.Members @ [ result.Members.Head ] } |> LocalAgentHandoffJson.readResult |> decodeRejected)
          t "wrong dispatch, attempt, worker and child results cannot claim another assignment" (fun () ->
              for changed in [ { result with DispatchId = "DISPATCH-2" }; { result with AttemptId = "ATTEMPT-2" }
                               { result with WorkerId = "worker-2" }; { result with ChildExecutionId = "EXE-OTHER" } ] do
                  checkResult observed changed |> rejected "local-result-identity"
              for changed in [ { observed with AttemptId = "ATTEMPT-2" }; { observed with WorkerId = "worker-2" }; { observed with ChildExecutionId = "EXE-OTHER" } ] do
                  checkResult changed result |> rejected "local-result-identity")
          t "worker and observer agreeing on a different packet digest still fails" (fun () ->
              checkResult { observed with ReservedPacketDigest = sha 'a' } { result with PacketDigest = sha 'a' } |> rejected "local-result-packet-digest")
          t "unavailable commit, wrong ancestry and hidden changed resources fail" (fun () ->
              for changed in [ { observed with OutputCommit = None }; { observed with OutputCommit = Some(String('c', 40)) }; { observed with SourceIsAncestor = false } ] do
                  checkResult changed result |> rejected "local-result-commit-unverified"
              checkResult { observed with ChangedPaths = Error "unavailable" } result |> rejected "local-result-paths-unavailable"
              checkResult { observed with ChangedPaths = Ok [ "src/A.fs"; "src/B.fs" ] } result |> rejected "local-result-paths-mismatch")
          t "missing, shared or substituted per-member obligations cannot blanket complete" (fun () ->
              checkResult observed { result with Members = result.Members.Tail } |> rejected "local-result-members-not-exact"
              checkResult observed { result with Members = [ { result.Members.Head with Evidence = [] }; result.Members[1] ] } |> rejected "local-result-obligations-not-exact:WI-1"
              checkResult observed { result with Members = [ { result.Members.Head with Evidence = result.Members[1].Evidence }; result.Members[1] ] } |> rejected "local-result-obligations-not-exact:WI-1")
          t "claimed evidence bytes cannot replace independent artifact observation" (fun () ->
              checkResult { observed with Evidence = Map.empty } result |> rejected "local-result-evidence-unverified:VERIFY-1"
              checkResult { observed with Evidence = observed.Evidence |> Map.add "VERIFY-1" ("reports/other.json", sha '2') } result |> rejected "local-result-evidence-unverified:VERIFY-1"
              checkResult { observed with Evidence = observed.Evidence |> Map.add "VERIFY-1" ("reports/one.json", sha '3') } result |> rejected "local-result-evidence-unverified:VERIFY-1")
          t "zero exit or self-reported success cannot replace pinned independent acceptance" (fun () ->
              checkResult { observed with Acceptance = Map.empty } result |> rejected "local-result-acceptance-unverified:VERIFY-1"
              let valid = observed.Acceptance["VERIFY-1"]
              for changed in [ { valid with ValidatorId = "worker-self-test" }; { valid with InputDigest = sha 'a' }; { valid with Verdict = Error "test failed" } ] do
                  checkResult { observed with Acceptance = observed.Acceptance |> Map.add "VERIFY-1" changed } result |> rejected "local-result-acceptance-unverified:VERIFY-1")
          t "blocked and failed member results preserve claims but admit no integration" (fun () ->
              for outcome in [ LocalWorkerOutcome.Blocked; LocalWorkerOutcome.Failed ] do
                  let partial = { result with Members = [ { result.Members.Head with Outcome = outcome; Evidence = [] }; result.Members[1] ] }
                  Assert.equal (Ok LocalResultDisposition.NoIntegration) (checkResult observed partial)) ]
