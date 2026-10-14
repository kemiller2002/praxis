namespace Praxis.Tests

open System
open System.IO
open System.Text.Json
open Praxis.Contracts.Remote
open Praxis.Domain.Provenance
open Praxis.Domain.Remote

/// The `praxis.remote` protocol contract (PRAXIS-REMOTE-01,
/// `DF-ROS-2026-A041`). These tests pin invariants rather than happy-path
/// output: fail-closed versioning, typed allow-listed operations, untrusted
/// input, secret refusal, identity that is asserted and never inferred,
/// semantic idempotency, repository-state binding, and a failure taxonomy
/// that never conflates Praxis decisions with transport failures.
[<RequireQualifiedAccess>]
module RemoteProtocolTests =
    let private sha = "59b4e032818a4c765886e48c117595dc58019d43"
    let private movedSha = "8646ade0000000000000000000000000000000aa"

    let private root = CliPort.repositoryRoot

    let private fixture name =
        File.ReadAllText(Path.Combine(root.Value, "tests", "fixtures", "remote", name))

    let private parse text = RemoteJson.parseRequest ProtocolVersion.current text

    let private parsed text =
        match parse text with
        | Ok request -> request
        | Error failure -> failwith $"expected a request, got {FailureCode.code failure.Failure.Code}: {failure.Failure.Problems}"

    let private rejected text =
        match parse text with
        | Ok request -> failwith $"expected a rejection, got a request for {Operation.code request.Operation}"
        | Error failure -> failure

    let private fields (failure: RemoteJson.ParseFailure) =
        failure.Failure.Problems |> List.map (fun problem -> problem.Field)

    /// Builds a request document from `(name, json)` members so each test
    /// states only what it varies.
    let private document (members: (string * string) list) =
        "{" + (members |> List.map (fun (name, json) -> $"\"{name}\":{json}") |> String.concat ",") + "}"

    let private envelope operation (arguments: string) =
        [ "protocol", "\"praxis.remote\""
          "protocolVersion", "\"1.0\""
          "requestId", "\"req-test-00000001\""
          "operation", $"\"{operation}\""
          "repository", $"{{\"ref\":\"refs/heads/main\",\"expectedSha\":\"{sha}\"}}"
          "arguments", arguments ]

    let private replace name json (members: (string * string) list) =
        members |> List.map (fun (key, value) -> if key = name then key, json else key, value)

    let private without name (members: (string * string) list) =
        members |> List.filter (fst >> (<>) name)

    let private startRequest = envelope "work.start" "{\"workItemIds\":[\"WI-0100\"]}"

    let private context grants observedSha =
        { Principal = "principal:test"
          Grants = set grants
          ObservedRef = Some "refs/heads/main"
          ObservedSha = observedSha
          Repository = None }

    let private allGrants = Capability.all

    let private decide grants observedSha journal request =
        RequestDecision.decide (context grants observedSha) journal request

    let private rejectionCode decision =
        match decision with
        | Decision.Reject failure -> Some failure.Code
        | _ -> None

    let tests =
        [ { Name = "remote: a well-formed work.start request parses into typed arguments"
            Run =
              fun () ->
                  let request = parsed (fixture "work-start.request.json")
                  Assert.equal Operation.WorkStart request.Operation
                  Assert.equal { Major = 1; Minor = 0 } request.ProtocolVersion
                  Assert.equal (Some sha) request.Repository.ExpectedSha

                  match request.Arguments with
                  | Arguments.WorkStart start ->
                      Assert.equal [ "WI-0100" ] start.WorkItemIds
                      Assert.equal (Some "task") start.Type
                  | other -> failwith $"unexpected arguments {other}" }

          { Name = "remote: every committed request fixture parses and passes value validation"
            Run =
              fun () ->
                  [ "work-start.request.json"; "validate.request.json"; "telemetry-record.request.json" ]
                  |> List.iter (fun name ->
                      let request = parsed (fixture name)
                      Assert.empty (RequestValidation.problems request)
                      Assert.empty (RequestValidation.secretFields request)) }

          { Name = "remote: a different protocol is unsupported, not merely invalid"
            Run =
              fun () ->
                  let failure = rejected (document (startRequest |> replace "protocol" "\"other.protocol\""))
                  Assert.equal FailureCode.UnsupportedProtocol failure.Failure.Code
                  Assert.isTrue (failure.Failure.Message.Contains "praxis.remote 1.4") "diagnostics name the supported version" }

          { Name = "remote: a newer major or newer minor protocol version fails closed with the supported versions"
            Run =
              fun () ->
                  [ "\"2.0\""; "\"1.5\""; "\"0.9\"" ]
                  |> List.iter (fun version ->
                      let failure = rejected (document (startRequest |> replace "protocolVersion" version))
                      Assert.equal FailureCode.UnsupportedProtocol failure.Failure.Code
                      Assert.equal (Outcome.Rejected) (FailureCode.outcome failure.Failure.Code)) }

          { Name = "remote: a future-version document with unknown fields is reported as unsupported, not as unknown fields"
            Run =
              fun () ->
                  let members = (startRequest |> replace "protocolVersion" "\"2.0\"") @ [ "batch", "[]" ]
                  let failure = rejected (document members)
                  Assert.equal FailureCode.UnsupportedProtocol failure.Failure.Code }

          { Name = "remote: a malformed or missing protocol version is an invalid request"
            Run =
              fun () ->
                  Assert.equal FailureCode.InvalidRequest (rejected (document (startRequest |> replace "protocolVersion" "\"v1\""))).Failure.Code
                  Assert.equal FailureCode.InvalidRequest (rejected (document (startRequest |> replace "protocolVersion" "1.0"))).Failure.Code
                  Assert.equal FailureCode.InvalidRequest (rejected (document (startRequest |> without "protocolVersion"))).Failure.Code }

          { Name = "remote: non-JSON and non-object documents are invalid requests"
            Run =
              fun () ->
                  Assert.equal FailureCode.InvalidRequest (rejected "not json").Failure.Code
                  Assert.equal FailureCode.InvalidRequest (rejected "[1,2]").Failure.Code
                  Assert.equal FailureCode.InvalidRequest (rejected "\"work.start\"").Failure.Code }

          { Name = "remote: the operation catalog is an allow-list; shell-like operations do not exist"
            Run =
              fun () ->
                  [ "shell"; "exec"; "run"; "work.delete"; "WORK.START"; "" ]
                  |> List.iter (fun operation ->
                      let failure = rejected (document (startRequest |> replace "operation" $"\"{operation}\""))
                      Assert.equal FailureCode.UnsupportedOperation failure.Failure.Code)

                  Assert.isTrue
                      (Operation.all |> List.forall (fun operation -> Operation.tryParse (Operation.code operation) = Some operation))
                      "every operation code round-trips" }

          { Name = "remote: unknown fields fail safely at every level while x- extensions are tolerated"
            Run =
              fun () ->
                  let topLevel = rejected (document (startRequest @ [ "command", "\"rm -rf /\"" ]))
                  Assert.equal FailureCode.InvalidRequest topLevel.Failure.Code
                  Assert.equal [ "command" ] (fields topLevel)

                  let inArguments = rejected (document (startRequest |> replace "arguments" "{\"workItemIds\":[\"WI-0100\"],\"force\":true}"))
                  Assert.equal [ "arguments.force" ] (fields inArguments)

                  let inRepository =
                      rejected (document (startRequest |> replace "repository" $"{{\"ref\":\"refs/heads/main\",\"expectedSha\":\"{sha}\",\"force\":true}}"))

                  Assert.equal [ "repository.force" ] (fields inRepository)

                  let extended =
                      startRequest
                      @ [ "x-client", "{\"anything\":[1,2,3]}" ]
                      |> replace "arguments" "{\"workItemIds\":[\"WI-0100\"],\"x-note\":\"ignored\"}"

                  Assert.equal Operation.WorkStart (parsed (document extended)).Operation }

          { Name = "remote: every structural problem is reported in one response, not just the first"
            Run =
              fun () ->
                  let members =
                      startRequest
                      |> without "requestId"
                      |> replace "arguments" "{\"type\":7}"
                      |> replace "repository" "{\"ref\":3}"

                  let failure = rejected (document members)
                  let reported = fields failure |> Set.ofList
                  Assert.isTrue (reported.Contains "requestId") "missing requestId reported"
                  Assert.isTrue (reported.Contains "arguments.workItemIds") "missing workItemIds reported"
                  Assert.isTrue (reported.Contains "arguments.type") "mistyped type reported"
                  Assert.isTrue (reported.Contains "repository.ref") "mistyped ref reported" }

          { Name = "remote: arguments are typed per operation and cannot be borrowed from another operation"
            Run =
              fun () ->
                  let failure = rejected (document (envelope "work.block" "{\"workItemIds\":[\"WI-0100\"]}"))
                  Assert.equal [ "arguments.reason" ] (fields failure)

                  let request = parsed (document startRequest)
                  let mismatched = { request with Arguments = Arguments.WorkResume [ "WI-0100" ] }
                  Assert.isTrue (RequestValidation.problems mismatched |> List.exists (fun problem -> problem.Field = "arguments")) "mismatch detected"
                  Assert.equal (Some FailureCode.InvalidRequest) (decide allGrants (Some sha) JournalLookup.NotRecorded mismatched |> rejectionCode) }

          { Name = "remote: hostile values are rejected before anything could reach a command line"
            Run =
              fun () ->
                  let problemsFor members =
                      match parse (document members) with
                      | Ok request -> RequestValidation.problems request |> List.map (fun problem -> problem.Field)
                      | Error failure -> fields failure

                  let hostile =
                      [ startRequest |> replace "arguments" "{\"workItemIds\":[\"--force\"]}", "arguments.workItemIds[0]"
                        startRequest |> replace "arguments" "{\"workItemIds\":[\"$(whoami)\"]}", "arguments.workItemIds[0]"
                        startRequest |> replace "arguments" "{\"workItemIds\":[\"WI-1; rm -rf /\"]}", "arguments.workItemIds[0]"
                        startRequest |> replace "arguments" "{\"workItemIds\":[\"WI-0100\"],\"type\":\"--root=/\"}", "arguments.type"
                        startRequest |> replace "repository" $"{{\"ref\":\"refs/heads/../main\",\"expectedSha\":\"{sha}\"}}", "repository.ref"
                        startRequest |> replace "repository" $"{{\"ref\":\"refs/heads/main\\n\",\"expectedSha\":\"{sha}\"}}", "repository.ref"
                        startRequest |> replace "repository" "{\"ref\":\"refs/heads/main\",\"expectedSha\":\"HEAD\"}", "repository.expectedSha"
                        startRequest |> replace "repository" $"{{\"ref\":\"refs/heads/main\",\"expectedSha\":\"{sha}\\n\"}}", "repository.expectedSha"
                        startRequest |> replace "arguments" "{\"workItemIds\":[\"WI-0100\\n\"]}", "arguments.workItemIds[0]"
                        startRequest @ [ "actor", "{\"kind\":\"x-agent\\n\"}" ], "actor.kind"
                        startRequest |> replace "requestId" "\"req-test-00000001\\n\"", "requestId"
                        envelope "work.block" "{\"workItemIds\":[\"WI-0100\"],\"reason\":\"--help\"}", "arguments.reason"
                        envelope "work.block" "{\"workItemIds\":[\"WI-0100\"],\"reason\":\"a\\u0000b\"}", "arguments.reason"
                        envelope "work.complete" "{\"workItemIds\":[\"WI-0100\"],\"evidence\":[{\"type\":\"tests\",\"path\":\"../../etc/passwd\"}]}", "arguments.evidence[0].path"
                        envelope "work.complete" "{\"workItemIds\":[\"WI-0100\"],\"evidence\":[{\"type\":\"tests\",\"path\":\"/etc/passwd\"}]}", "arguments.evidence[0].path"
                        envelope "work.reconcile" "{\"workItemId\":\"WI-0100\",\"reason\":\"late\",\"commits\":[\"HEAD~1\"]}", "arguments.commits[0]"
                        envelope "work.reconcile" "{\"workItemId\":\"WI-0100\",\"reason\":\"late\",\"ranges\":[\"main..HEAD\"]}", "arguments.ranges[0].base" ]

                  hostile
                  |> List.iter (fun (members, field) ->
                      let reported = problemsFor members
                      Assert.isTrue (reported |> List.contains field) $"expected a problem on {field}, got {reported}") }

          { Name = "remote: a request carrying credential material is refused without echoing the secret"
            Run =
              fun () ->
                  let token = "ghp_" + String('A', 36)
                  let request = parsed (document (envelope "work.block" $"{{\"workItemIds\":[\"WI-0100\"],\"reason\":\"use {token} to push\"}}"))

                  match decide allGrants (Some sha) JournalLookup.NotRecorded request with
                  | Decision.Reject failure ->
                      Assert.equal FailureCode.SecretDetected failure.Code
                      let response = Response.rejected request.ProtocolVersion "test" (Some request.RequestId) (Some "work.block") request.Repository None failure
                      let rendered = RemoteJson.renderResponse response
                      Assert.isTrue (not (rendered.Contains token)) "the rendered response must not contain the secret"
                      Assert.isTrue (not (failure.Message.Contains token)) "the message must not contain the secret"
                      Assert.equal [ "arguments.reason" ] (failure.Problems |> List.map (fun problem -> problem.Field))
                  | other -> failwith $"expected secret refusal, got {other}"

                  [ "github_pat_" + String('b', 30)
                    "sk-ant-" + String('c', 30)
                    "AKIA" + String('D', 16)
                    "-----BEGIN OPENSSH PRIVATE KEY-----" ]
                  |> List.iter (fun secret -> Assert.isTrue (SecretMaterial.looksLikeSecret secret) $"should detect {secret.Substring(0, 6)}...")

                  Assert.isTrue (not (SecretMaterial.looksLikeSecret "EXE-20260928T073932249Z-d48161b9")) "execution IDs are not secrets"
                  Assert.isTrue (not (SecretMaterial.looksLikeSecret sha)) "commit SHAs are not secrets" }

          { Name = "remote: an absent actor stays absent and missing actor fields are unknown, never guessed"
            Run =
              fun () ->
                  let request = parsed (document startRequest)
                  Assert.equal None request.Actor

                  let partial = parsed (document (startRequest @ [ "actor", "{\"kind\":\"agent\"}" ]))

                  match partial.Actor with
                  | Some value ->
                      Assert.equal ActorKind.Agent value.Actor.Kind
                      Assert.equal "unknown" value.Actor.Id
                      Assert.equal (Some "unknown") value.Actor.Provider
                      Assert.equal (Some "unknown") value.Actor.Model
                      Assert.equal (Some "unknown") value.Actor.Runtime
                      Assert.equal None value.SessionId
                  | None -> failwith "expected an actor"

                  Assert.empty (RequestValidation.problems partial) }

          { Name = "remote: a human actor has no provider/model/runtime and extension kinds stay provider-neutral"
            Run =
              fun () ->
                  let human = parsed (document (startRequest @ [ "actor", "{\"kind\":\"human\",\"id\":\"octo\"}" ]))
                  Assert.equal (Some { Actor = { Kind = ActorKind.Human; Id = "octo"; Provider = None; Model = None; Runtime = None }; SessionId = None }) human.Actor

                  let extension =
                      parsed (document (startRequest @ [ "actor", "{\"kind\":\"x-scheduler\",\"id\":\"nightly\",\"provider\":\"any-vendor\",\"runtime\":\"any-runtime\"}" ]))

                  Assert.equal (Some(ActorKind.Extension "x-scheduler")) (extension.Actor |> Option.map (fun value -> value.Actor.Kind))

                  let invalid = rejected (document (startRequest @ [ "actor", "{\"kind\":\"robot\"}" ]))
                  Assert.equal [ "actor.kind" ] (fields invalid) }

          { Name = "remote: the actor is taken only from the request, never from the executor's context"
            Run =
              fun () ->
                  // The trusted context carries a principal and grants but no
                  // actor field at all: the executor cannot become the actor.
                  let request = parsed (document startRequest)
                  Assert.equal Decision.Execute (decide allGrants (Some sha) JournalLookup.NotRecorded request)
                  Assert.equal None request.Actor }

          { Name = "remote: telemetry.record values keep their asserted quality and cannot claim executor observation"
            Run =
              fun () ->
                  let request = parsed (fixture "telemetry-record.request.json")

                  match request.Arguments with
                  | Arguments.TelemetryRecord record ->
                      Assert.equal 1532m record.Value
                      Assert.equal (Some "observed") record.Quality
                      Assert.equal (Some "runtime-api") record.SourceType
                  | other -> failwith $"unexpected {other}"

                  let recordWith (arguments: string) =
                      envelope "telemetry.record" arguments

                  let claimsObservation =
                      parsed (document (recordWith "{\"metric\":\"tokens.input\",\"value\":10,\"sourceType\":\"ros-git\"}"))

                  Assert.equal [ "arguments.sourceType" ] (RequestValidation.problems claimsObservation |> List.map (fun problem -> problem.Field))

                  // A missing value is refused, never defaulted to zero.
                  Assert.equal [ "arguments.value" ] (fields (rejected (document (recordWith "{\"metric\":\"tokens.input\"}"))))
                  Assert.equal [ "arguments.value" ] (fields (rejected (document (recordWith "{\"metric\":\"tokens.input\",\"value\":\"12\"}"))))

                  let negative = parsed (document (recordWith "{\"metric\":\"cost.execution_total\",\"value\":-1,\"currency\":\"USD\"}"))
                  Assert.equal [ "arguments.value" ] (RequestValidation.problems negative |> List.map (fun problem -> problem.Field))

                  Assert.equal [ "arguments.confidence" ] (fields (rejected (document (recordWith "{\"metric\":\"tokens.input\",\"value\":1,\"confidence\":2}"))))

                  let estimated =
                      parsed (document (recordWith "{\"metric\":\"cost.execution_total\",\"value\":0.42,\"currency\":\"USD\",\"quality\":\"estimated\",\"confidence\":\"medium\"}"))

                  match estimated.Arguments with
                  | Arguments.TelemetryRecord record ->
                      Assert.equal (Some "medium") record.Confidence
                      Assert.equal 0.42m record.Value
                  | other -> failwith $"unexpected {other}" }

          { Name = "remote: a mutation must be bound to a ref and an expected SHA; a read need not be"
            Run =
              fun () ->
                  let unbound = parsed (document (startRequest |> without "repository"))
                  Assert.equal (Some FailureCode.InvalidRequest) (decide allGrants (Some sha) JournalLookup.NotRecorded unbound |> rejectionCode)

                  let read = parsed (fixture "validate.request.json")
                  Assert.equal Decision.Execute (decide [ Capability.Read ] (Some movedSha) JournalLookup.NotRecorded read) }

          { Name = "remote: capabilities are required per operation class and read never implies write"
            Run =
              fun () ->
                  let start = parsed (document startRequest)
                  Assert.equal (Some FailureCode.Unauthorized) (decide [ Capability.Read ] (Some sha) JournalLookup.NotRecorded start |> rejectionCode)
                  Assert.equal Decision.Execute (decide [ Capability.Read; Capability.Mutate ] (Some sha) JournalLookup.NotRecorded start)

                  let complete = parsed (document (envelope "work.complete" "{\"workItemIds\":[\"WI-0100\"]}"))
                  Assert.equal (Some FailureCode.Unauthorized) (decide [ Capability.Read; Capability.Mutate ] (Some sha) JournalLookup.NotRecorded complete |> rejectionCode)

                  let reconcile = parsed (document (envelope "work.reconcile" $"{{\"workItemId\":\"WI-0100\",\"reason\":\"late\",\"commits\":[\"{sha}\"]}}"))
                  Assert.equal (Some FailureCode.Unauthorized) (decide [ Capability.Read; Capability.Mutate; Capability.Complete ] (Some sha) JournalLookup.NotRecorded reconcile |> rejectionCode)

                  let read = parsed (fixture "validate.request.json")
                  Assert.equal (Some FailureCode.Unauthorized) (decide [] None JournalLookup.NotRecorded read |> rejectionCode) }

          { Name = "remote: authorization precedes replay so an unauthorized caller cannot read a recorded outcome"
            Run =
              fun () ->
                  let start = parsed (document startRequest)
                  let recorded = JournalLookup.Recorded(RequestFingerprint.compute start)
                  Assert.equal (Some FailureCode.Unauthorized) (decide [ Capability.Read ] (Some sha) recorded start |> rejectionCode) }

          { Name = "remote: a duplicate retry replays even after its own commit moved the ref"
            Run =
              fun () ->
                  let start = parsed (document startRequest)
                  let recorded = JournalLookup.Recorded(RequestFingerprint.compute start)
                  Assert.equal Decision.Replay (decide allGrants (Some sha) recorded start)
                  // The caller retries after a lost result, having refreshed its
                  // expected SHA to the commit its own success produced.
                  let refreshed = { start with Repository = { start.Repository with ExpectedSha = Some movedSha } }
                  Assert.equal Decision.Replay (decide allGrants (Some movedSha) recorded refreshed)
                  Assert.equal Decision.Replay (decide allGrants (Some "0000000000000000000000000000000000000000") recorded start) }

          { Name = "remote: the same request ID with a different semantic payload fails closed"
            Run =
              fun () ->
                  let start = parsed (document startRequest)
                  let recorded = JournalLookup.Recorded(RequestFingerprint.compute start)

                  let otherItem = parsed (document (startRequest |> replace "arguments" "{\"workItemIds\":[\"WI-0200\"]}"))
                  Assert.equal (Some FailureCode.IdempotencyConflict) (decide allGrants (Some sha) recorded otherItem |> rejectionCode)

                  let otherActor = parsed (document (startRequest @ [ "actor", "{\"kind\":\"agent\",\"id\":\"someone/else\"}" ]))
                  Assert.equal (Some FailureCode.IdempotencyConflict) (decide allGrants (Some sha) recorded otherActor |> rejectionCode)

                  let otherOperation = parsed (document (envelope "work.resume" "{\"workItemIds\":[\"WI-0100\"]}"))
                  Assert.equal (Some FailureCode.IdempotencyConflict) (decide allGrants (Some sha) recorded otherOperation |> rejectionCode)
                  Assert.equal FailureCode.IdempotencyConflict FailureCode.IdempotencyConflict
                  Assert.equal RetryAdvice.Never (FailureCode.retry FailureCode.IdempotencyConflict) }

          { Name = "remote: a stale, delayed, or out-of-order mutation is refused rather than applied to another commit"
            Run =
              fun () ->
                  let start = parsed (document startRequest)
                  Assert.equal (Some FailureCode.StaleRef) (decide allGrants (Some movedSha) JournalLookup.NotRecorded start |> rejectionCode)
                  Assert.equal (Some FailureCode.StaleRef) (decide allGrants None JournalLookup.NotRecorded start |> rejectionCode)

                  let elsewhere =
                      RequestDecision.decide
                          { context allGrants (Some sha) with ObservedRef = Some "refs/heads/other" }
                          JournalLookup.NotRecorded
                          start

                  Assert.equal (Some FailureCode.StaleRef) (rejectionCode elsewhere)
                  Assert.equal RetryAdvice.AfterRefresh (FailureCode.retry FailureCode.StaleRef) }

          { Name = "remote: the fingerprint ignores expectedSha, requestedAt and extensions but covers intent"
            Run =
              fun () ->
                  let baseline = parsed (fixture "work-start.request.json")
                  let fingerprint = RequestFingerprint.compute baseline
                  Assert.isTrue (fingerprint.StartsWith "sha256:" && fingerprint.Length = 71) "sha256 fingerprint"

                  let refreshed = { baseline with Repository = { baseline.Repository with ExpectedSha = Some movedSha }; RequestedAt = None }
                  Assert.equal fingerprint (RequestFingerprint.compute refreshed)

                  let withoutExtension =
                      parsed ((fixture "work-start.request.json").Replace("\"x-client\": { \"name\": \"example-client\", \"version\": \"0.1.0\" }", "\"x-other\": 1"))

                  Assert.equal fingerprint (RequestFingerprint.compute withoutExtension)

                  let variants =
                      [ { baseline with Repository = { baseline.Repository with Ref = Some "refs/heads/other" } }
                        { baseline with ExecutionId = Some "EXE-1" }
                        { baseline with Actor = None }
                        { baseline with Arguments = Arguments.WorkStart { WorkItemIds = [ "WI-0100" ]; Type = Some "bug"; Classifications = [] } }
                        { baseline with Operation = Operation.WorkResume; Arguments = Arguments.WorkResume [ "WI-0100" ] } ]

                  variants
                  |> List.iter (fun variant -> Assert.isTrue (RequestFingerprint.compute variant <> fingerprint) $"fingerprint must change for {variant}") }

          { Name = "remote: the fingerprint encoding is unambiguous across field boundaries"
            Run =
              fun () ->
                  let baseline = parsed (document (envelope "work.start" "{\"workItemIds\":[\"A\",\"B\"]}"))
                  let merged = { baseline with Arguments = Arguments.WorkStart { WorkItemIds = [ "AB" ]; Type = None; Classifications = [] } }
                  let shifted = { baseline with Arguments = Arguments.WorkStart { WorkItemIds = [ "A" ]; Type = None; Classifications = [ "B" ] } }
                  let fingerprints = [ baseline; merged; shifted ] |> List.map RequestFingerprint.compute
                  Assert.equal 3 (fingerprints |> List.distinct |> List.length) }

          { Name = "remote: reads are never journalled, so a replay lookup cannot change a read's decision"
            Run =
              fun () ->
                  let read = parsed (fixture "validate.request.json")
                  Assert.equal Decision.Execute (decide [ Capability.Read ] None (JournalLookup.Recorded "sha256:other") read) }

          { Name = "remote: failures decided by Praxis are distinct from executor and transport failures"
            Run =
              fun () ->
                  let praxis = FailureCode.all |> List.filter (fun code -> FailureCode.decidedBy code = DecidedBy.Praxis)
                  let executor = FailureCode.all |> List.filter (fun code -> FailureCode.decidedBy code = DecidedBy.Executor)

                  Assert.isTrue (executor |> List.forall (fun code -> FailureCode.outcome code <> Outcome.Rejected)) "an executor failure is never a Praxis rejection"

                  Assert.isTrue
                      ([ FailureCode.RepositoryWriteFailed; FailureCode.TransportFailed; FailureCode.RateLimited; FailureCode.Timeout; FailureCode.Cancelled ]
                       |> List.forall (fun code -> FailureCode.outcome code = Outcome.Unknown && FailureCode.retry code = RetryAdvice.SameRequest))
                      "an unconfirmed effect is unknown and retried only with the same request ID"

                  Assert.isTrue
                      (praxis |> List.forall (fun code -> FailureCode.retry code <> RetryAdvice.SameRequest || code = FailureCode.Internal))
                      "a Praxis decision is not blindly retried"

                  Assert.isTrue (FailureCode.all |> List.forall (fun code -> FailureCode.outcome code <> Outcome.Succeeded)) "no failure is a success"
                  Assert.isTrue (FailureCode.all |> List.forall (fun code -> FailureCode.tryParse (FailureCode.code code) = Some code)) "codes round-trip" }

          { Name = "remote: a rejected document still yields a correlatable, schema-shaped response"
            Run =
              fun () ->
                  let failure = rejected (document (startRequest |> replace "arguments" "{\"workItemIds\":[]}" |> replace "operation" "\"work.explode\""))
                  let response = RemoteJson.rejection ProtocolVersion.current "3.4.0" (Some sha) failure
                  use json = JsonDocument.Parse(RemoteJson.renderResponse response)
                  let rootElement = json.RootElement
                  Assert.equal "req-test-00000001" (rootElement.GetProperty("requestId").GetString())
                  Assert.equal "rejected" (rootElement.GetProperty("outcome").GetString())
                  Assert.equal "unsupported-operation" (rootElement.GetProperty("failure").GetProperty("code").GetString())
                  Assert.equal "praxis" (rootElement.GetProperty("failure").GetProperty("decidedBy").GetString())
                  Assert.equal "never" (rootElement.GetProperty("failure").GetProperty("retry").GetString())
                  Assert.equal sha (rootElement.GetProperty("repository").GetProperty("observedSha").GetString())
                  Assert.equal JsonValueKind.Null (rootElement.GetProperty("result").ValueKind) }

          { Name = "remote: a hostile request ID is never echoed back into the response"
            Run =
              fun () ->
                  let failure = rejected (document (startRequest |> replace "requestId" "\"$(curl evil)\"" |> replace "operation" "\"nope\""))
                  let response = RemoteJson.rejection ProtocolVersion.current "3.4.0" None failure
                  Assert.equal None response.RequestId }

          { Name = "remote: a success embeds the command's own structured result and a replay is flagged"
            Run =
              fun () ->
                  let request = parsed (fixture "validate.request.json")
                  let response = Response.succeeded "3.4.0" request (Some sha) (Some "{\"valid\":true,\"findings\":[]}")
                  use json = JsonDocument.Parse(RemoteJson.renderResponse response)
                  Assert.equal "succeeded" (json.RootElement.GetProperty("outcome").GetString())
                  Assert.equal JsonValueKind.Null (json.RootElement.GetProperty("failure").ValueKind)
                  Assert.isTrue (json.RootElement.GetProperty("result").GetProperty("valid").GetBoolean()) "result embedded verbatim"
                  Assert.isTrue (not response.Replayed) "fresh response"
                  Assert.isTrue (Response.replayed response).Replayed "replay flagged" }

          { Name = "remote: the committed schemas agree with the typed operation and failure catalogs"
            Run =
              fun () ->
                  let schema name =
                      JsonDocument.Parse(File.ReadAllText(Path.Combine(root.Value, "schemas", name)))

                  use request = schema "praxis-remote-request.schema.json"
                  use response = schema "praxis-remote-response.schema.json"

                  let enumOf (element: JsonElement) =
                      element.EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq

                  let operations = enumOf (request.RootElement.GetProperty("properties").GetProperty("operation").GetProperty("enum"))
                  Assert.equal (Operation.all |> List.map Operation.code |> Set.ofList) operations

                  let failureObject =
                      response.RootElement.GetProperty("properties").GetProperty("failure").GetProperty("oneOf").[1]

                  let failureCodes =
                      failureObject.GetProperty("properties").GetProperty("code").GetProperty("enum") |> enumOf

                  Assert.equal (FailureCode.all |> List.map FailureCode.code |> Set.ofList) failureCodes
                  Assert.equal "praxis.remote" (request.RootElement.GetProperty("properties").GetProperty("protocol").GetProperty("const").GetString()) }

          { Name = "remote: the request schema's per-operation arguments are exactly the typed catalog"
            Run =
              fun () ->
                  use request = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.Value, "schemas", "praxis-remote-request.schema.json")))

                  let names (element: JsonElement) =
                      element.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Set.ofSeq

                  let strings (element: JsonElement) =
                      element.EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Set.ofSeq

                  let conditional =
                      request.RootElement.GetProperty("allOf").EnumerateArray()
                      |> Seq.choose (fun entry ->
                          match entry.GetProperty("if").GetProperty("properties").GetProperty("operation").TryGetProperty "const" with
                          | true, operation -> Some(operation.GetString(), entry.GetProperty("then").GetProperty("properties").GetProperty("arguments"))
                          | _ -> None)
                      |> Map.ofSeq

                  Operation.all
                  |> List.iter (fun operation ->
                      let required, optional = Operation.arguments operation
                      let arguments = conditional[Operation.code operation]
                      Assert.equal (Set.ofList (required @ optional)) (names (arguments.GetProperty "properties"))
                      Assert.equal (Set.ofList required) (strings (arguments.GetProperty "required"))) }

          { Name = "remote 1.2: a batch is validated constituent by constituent and authorized for all of them"
            Run =
              fun () ->
                  let batch (requests: string) =
                      document (envelope "batch" $"{{\"requests\":{requests}}}" |> replace "protocolVersion" "\"1.2\"")

                  let request =
                      parsed (batch """[{"requestId":"req-part-00001","operation":"work.start","arguments":{"workItemIds":["WI-1"]}},{"requestId":"req-part-00002","operation":"work.complete","arguments":{"workItemIds":["WI-1"]}}]""")

                  Assert.empty (RequestValidation.problems request)
                  Assert.isTrue (RequestShape.isMutating request) "a batch with a mutation mutates"
                  Assert.equal (set [ Capability.Mutate; Capability.Complete ]) (RequestShape.capabilities request)
                  Assert.equal (Some FailureCode.Unauthorized) (decide [ Capability.Read; Capability.Mutate ] (Some sha) JournalLookup.NotRecorded request |> rejectionCode)
                  Assert.equal Decision.Execute (decide allGrants (Some sha) JournalLookup.NotRecorded request)

                  let reads = parsed (batch """[{"requestId":"req-part-00003","operation":"validate"}]""")
                  Assert.isTrue (not (RequestShape.isMutating reads)) "a read-only batch does not mutate"

                  let duplicateIds = parsed (batch """[{"requestId":"req-test-00000001","operation":"validate"}]""")
                  Assert.isTrue (RequestValidation.problems duplicateIds |> List.exists (fun problem -> problem.Field = "arguments.requests")) "IDs must differ from the batch's"

                  let hostile = parsed (batch """[{"requestId":"req-part-00004","operation":"work.start","arguments":{"workItemIds":["--root=/"]}}]""")
                  Assert.isTrue (RequestValidation.problems hostile |> List.exists (fun problem -> problem.Field = "arguments.requests[0].arguments.workItemIds[0]")) "constituent values are validated"

                  Assert.equal FailureCode.InvalidRequest (rejected (batch """[{"requestId":"req-part-00005","operation":"batch","arguments":{"requests":[]}}]""")).Failure.Code
                  Assert.equal FailureCode.UnsupportedOperation (rejected (document (envelope "batch" "{\"requests\":[]}"))).Failure.Code

                  let reordered =
                      parsed (batch """[{"requestId":"req-part-00002","operation":"work.complete","arguments":{"workItemIds":["WI-1"]}},{"requestId":"req-part-00001","operation":"work.start","arguments":{"workItemIds":["WI-1"]}}]""")

                  Assert.isTrue (RequestFingerprint.compute reordered <> RequestFingerprint.compute request) "order is part of a batch's intent" }

          { Name = "remote: mutating operations and capability classes are exactly as documented"
            Run =
              fun () ->
                  let mutating = Operation.all |> List.filter Operation.isMutating |> List.map Operation.code |> Set.ofList
                  Assert.equal (set [ "work.start"; "work.resume"; "work.block"; "telemetry.record"; "work.complete"; "work.reconcile"; "step.start"; "step.complete"; "step.fail"; "work.checkpoint"; "work.continue" ]) mutating
                  Assert.equal Capability.Complete (Operation.capability Operation.WorkComplete)
                  Assert.equal Capability.Reconcile (Operation.capability Operation.WorkReconcile)
                  Assert.isTrue (Operation.all |> List.forall (fun operation -> Operation.capability operation <> Capability.Admin)) "admin is reserved" }

          { Name = "remote 1.3: work.block keeps its 1.0-1.2 fingerprint unless unrecoverableReason is supplied"
            Run =
              fun () ->
                  let block unrecoverable =
                      let arguments =
                          match unrecoverable with
                          | Some reason -> $"{{\"workItemIds\":[\"WI-0100\"],\"reason\":\"handoff\",\"unrecoverableReason\":\"{reason}\"}}"
                          | None -> "{\"workItemIds\":[\"WI-0100\"],\"reason\":\"handoff\"}"

                      parsed (document (envelope "work.block" arguments))

                  let plain = block None
                  let canonical = RequestFingerprint.canonical plain
                  // The pre-1.3 encoding of this block, verbatim.
                  Assert.isTrue (canonical.EndsWith "12:workItemIds#=1:1;14:workItemIds[0]=7:WI-0100;6:reason=7:handoff;") canonical
                  Assert.isTrue (not (canonical.Contains "unrecoverableReason")) canonical
                  let withReason = block (Some "cannot push")
                  Assert.isTrue (RequestFingerprint.compute withReason <> RequestFingerprint.compute plain) "a different intent shares a fingerprint"

                  match ExecutionPlan.forRequest "2026-09-29T10:00:00.000Z" withReason with
                  | ExecutionPlan.Command arguments -> Assert.isTrue (List.contains "--unrecoverable-reason" arguments) $"{arguments}"
                  | other -> failwith $"{other}" }

          { Name = "remote 1.3: work.checkpoint maps to the local command in the requester's own execution; work.continue creates one"
            Run =
              fun () ->
                  let checkpoint =
                      document (
                          envelope "work.checkpoint" "{\"workItemId\":\"WI-0100\",\"summary\":\"Part one\",\"nextAction\":\"Part two\",\"stepId\":\"impl-1\"}"
                          |> replace "protocolVersion" "\"1.3\""
                      )

                  let missingExecution = parsed checkpoint
                  Assert.isTrue (RequestValidation.problems missingExecution |> List.exists (fun problem -> problem.Field = "execution.id")) "execution not required"

                  let named =
                      parsed (
                          document (
                              envelope "work.checkpoint" "{\"workItemId\":\"WI-0100\",\"summary\":\"Part one\",\"nextAction\":\"Part two\",\"stepId\":\"impl-1\"}"
                              |> replace "protocolVersion" "\"1.3\""
                              |> fun members -> members @ [ "execution", "{\"id\":\"EXE-20260929T100000000Z-0a1b2c3d\"}" ]
                          )
                      )

                  Assert.empty (RequestValidation.problems named)
                  Assert.equal Capability.Mutate (Operation.capability named.Operation)

                  match ExecutionPlan.forRequest "2026-09-29T10:00:00.000Z" named with
                  | ExecutionPlan.Command arguments ->
                      Assert.equal
                          [ "work"; "checkpoint"; "--id"; "WI-0100"; "--occurred-at"; "2026-09-29T10:00:00.000Z"; "--summary"; "Part one"; "--next-action"; "Part two"; "--step"; "impl-1"; "--execution"; "EXE-20260929T100000000Z-0a1b2c3d"; "--json" ]
                          arguments
                  | other -> failwith $"{other}"

                  let continued =
                      parsed (document (envelope "work.continue" "{\"workItemId\":\"WI-0100\"}" |> replace "protocolVersion" "\"1.3\""))

                  match ExecutionPlan.forRequest "2026-09-29T10:00:00.000Z" continued with
                  | ExecutionPlan.Command arguments ->
                      Assert.equal [ "work"; "continue"; "--id"; "WI-0100"; "--occurred-at"; "2026-09-29T10:00:00.000Z"; "--json" ] arguments
                  | other -> failwith $"{other}"

                  let older = rejected (document (envelope "work.continue" "{\"workItemId\":\"WI-0100\"}" |> replace "protocolVersion" "\"1.2\""))
                  Assert.equal FailureCode.UnsupportedOperation older.Failure.Code

                  let secret = "ghp_" + String.replicate 36 "Q"

                  let leaking =
                      parsed (
                          document (
                              envelope "work.checkpoint" $"{{\"workItemId\":\"WI-0100\",\"summary\":\"{secret}\",\"nextAction\":\"n\"}}"
                              |> replace "protocolVersion" "\"1.3\""
                          )
                      )

                  Assert.equal [ "arguments.summary" ] (RequestValidation.secretFields leaking) } ]
