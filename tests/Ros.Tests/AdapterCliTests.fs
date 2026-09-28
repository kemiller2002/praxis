namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// End-to-end `ros-fs adapter call` and `ros-fs adapter publish` tests,
/// ported from the former tests/adapter-{call,publish}-fsharp-differential.test.mjs.
/// The adapter-call goldens (printed results and the resulting store) were
/// captured from the retired Node reference adapter and frozen.
[<RequireQualifiedAccess>]
module AdapterCliTests =
    let private callProject = "Adapter Call Differential"
    let private publishProject = "Adapter Publish Differential"

    let private baseStore =
        """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repositories":["protocol-consumer"],
            "workItems":{"FEAT-900":{"id":"FEAT-900","state":"ready","type":"feature"}},"events":[],"requests":{}}"""

    let private call root (request: string) =
        CliHarness.write root "adapter-request.json" (CliPort.compact (CliPort.parse request))
        CliHarness.ros root [ "adapter"; "call"; "--store"; "adapter-store.json"; "--request"; "adapter-request.json" ]

    /// The printed result must be exactly the two-space-indented document.
    let private printed (expected: string) (result: CliHarness.Run) =
        Assert.equal (CliPort.indented expected) (result.Out.Trim())

    let private withStore (run: string -> unit) =
        CliPort.withRepository callProject (fun root ->
            CliHarness.write root "adapter-store.json" (CliPort.indented baseStore)
            run root)

    let private readStore root = CliPort.readJson root "adapter-store.json"

    let private result (requestId: string) (operation: string) (outcome: string) (body: string) (protocolVersion: string) =
        CliPort.fill
            [ "requestId", requestId; "operation", operation; "outcome", outcome; "body", body; "protocolVersion", protocolVersion ]
            """{"schemaVersion":"1.0.0","protocolVersion":"{{protocolVersion}}","requestId":"{{requestId}}","operation":"{{operation}}","outcome":"{{outcome}}",{{body}}}"""

    let private readyItem = """"data":{"workItem":{"id":"FEAT-900","state":"ready","type":"feature"}}"""
    let private activeItem = """"data":{"workItem":{"id":"FEAT-900","state":"active","type":"feature","updatedBy":"agent:test"}}"""
    let private failure (code: string) (message: string) = $""""error":{{"code":"{code}","message":"{message}"}}"""

    let private transition (requestId: string) (extra: string) =
        CliPort.fill
            [ "requestId", requestId; "extra", extra ]
            """{"protocolVersion":"1.0.0","requestId":"{{requestId}}","operation":"transitionWorkItem","repository":"protocol-consumer","principal":"agent:test",
                "scopes":["work:transition"],"workItem":"FEAT-900","expectedState":"ready","targetState":"active"{{extra}}}"""

    let private store (workItem: string) (events: string) (requests: (string * string) list) =
        let entries = requests |> List.map (fun (id, document) -> $"\"{id}\":{document}") |> String.concat ","

        $"""{{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repositories":["protocol-consumer"],"workItems":{{"FEAT-900":{workItem}}},"events":{events},"requests":{{{entries}}}}}"""

    let private readyFeature = """{"id":"FEAT-900","state":"ready","type":"feature"}"""
    let private activeFeature = """{"id":"FEAT-900","state":"active","type":"feature","updatedBy":"agent:test"}"""

    let private callTests =
        [ { Name = "adapter cli: call reads and transitions a work item without vendor assumptions"
            Run = fun () ->
                withStore (fun root ->
                    let read =
                        call root """{"protocolVersion":"1.0.0","requestId":"req-get-1","operation":"getWorkItem","repository":"protocol-consumer","principal":"agent:test","scopes":["work:read"],"workItem":"FEAT-900"}"""

                    CliPort.exitCode 0 read
                    let readResult = result "req-get-1" "getWorkItem" "success" readyItem "1.0.0"
                    printed readResult read

                    let transitioned = call root (transition "req-transition-1" "")
                    CliPort.exitCode 0 transitioned
                    let transitionResult = result "req-transition-1" "transitionWorkItem" "success" activeItem "1.0.0"
                    printed transitionResult transitioned

                    CliPort.deepEqual
                        (store activeFeature "[]" [ "req-get-1", readResult; "req-transition-1", transitionResult ])
                        (readStore root)) }
          { Name = "adapter cli: call retries are idempotent and state conflicts are explicit"
            Run = fun () ->
                withStore (fun root ->
                    let first = call root (transition "req-same" "")
                    let retry = call root (transition "req-same" "")
                    Assert.equal (first.Out.Trim()) (retry.Out.Trim())
                    let firstResult = result "req-same" "transitionWorkItem" "success" activeItem "1.0.0"
                    printed firstResult first

                    let conflict = call root ((transition "req-conflict" "").Replace("\"targetState\":\"active\"", "\"targetState\":\"complete\""))
                    CliPort.exitCode 1 conflict
                    let conflictResult = result "req-conflict" "transitionWorkItem" "failure" (failure "state_conflict" "expected 'ready', found 'active'") "1.0.0"
                    printed conflictResult conflict

                    CliPort.deepEqual
                        (store activeFeature "[]" [ "req-same", firstResult; "req-conflict", conflictResult ])
                        (readStore root)) }
          { Name = "adapter cli: call enforces repository, authorization, protocol and unknown outcomes"
            Run = fun () ->
                withStore (fun root ->
                    let request (requestId: string) (protocolVersion: string) (repository: string) (scopes: string) (extra: string) =
                        CliPort.fill
                            [ "requestId", requestId; "protocolVersion", protocolVersion; "repository", repository; "scopes", scopes; "extra", extra ]
                            """{"protocolVersion":"{{protocolVersion}}","operation":"transitionWorkItem","repository":"{{repository}}","principal":"agent:test",
                                "scopes":{{scopes}},"workItem":"FEAT-900","targetState":"active","requestId":"{{requestId}}"{{extra}}}"""

                    let forbidden = result "req-forbidden" "transitionWorkItem" "failure" (failure "forbidden" "principal lacks work:transition scope") "1.0.0"
                    let unknownRepository = result "req-repo" "transitionWorkItem" "failure" (failure "repository_unknown" "repository 'other' is not authorized") "1.0.0"
                    let mismatch = result "req-version" "transitionWorkItem" "failure" (failure "protocol_mismatch" "adapter supports protocol 1.0.0") "2.0.0"
                    let unknownOutcome = result "req-unknown" "transitionWorkItem" "unknown" (failure "remote_outcome_unknown" "the remote effect could not be confirmed") "1.0.0"

                    for scenario, exit, expected in
                        [ request "req-forbidden" "1.0.0" "protocol-consumer" "[]" "", 1, forbidden
                          request "req-repo" "1.0.0" "other" "[]" "", 1, unknownRepository
                          request "req-version" "2.0.0" "protocol-consumer" "[]" "", 1, mismatch
                          request "req-unknown" "1.0.0" "protocol-consumer" """["work:transition"]""" ""","simulateOutcome":"unknown" """, 2, unknownOutcome ] do
                        let outcome = call root scenario
                        CliPort.exitCode exit outcome
                        printed expected outcome

                    let final = readStore root
                    Assert.equal "ready" (CliPort.text (CliPort.at [ "workItems"; "FEAT-900"; "state" ] final))
                    // A protocol mismatch is refused before it is recorded.
                    CliPort.deepEqual
                        (store readyFeature "[]" [ "req-forbidden", forbidden; "req-repo", unknownRepository; "req-unknown", unknownOutcome ])
                        final) }
          { Name = "adapter cli: call deduplicates published event ids"
            Run = fun () ->
                withStore (fun root ->
                    let publish (requestId: string) =
                        call
                            root
                            (CliPort.fill
                                [ "requestId", requestId ]
                                """{"protocolVersion":"1.0.0","operation":"publishRepositoryEvent","repository":"protocol-consumer","principal":"ci:test",
                                    "scopes":["event:publish"],"event":{"eventId":"evt-1","type":"work.completed"},"requestId":"{{requestId}}"}""")

                    CliPort.exitCode 0 (publish "req-event-1")
                    CliPort.exitCode 0 (publish "req-event-2")
                    let published requestId = result requestId "publishRepositoryEvent" "success" "\"data\":{\"eventId\":\"evt-1\"}" "1.0.0"

                    CliPort.deepEqual
                        (store readyFeature """[{"eventId":"evt-1","type":"work.completed"}]""" [ "req-event-1", published "req-event-1"; "req-event-2", published "req-event-2" ])
                        (readStore root)) }
          { Name = "adapter cli: call rejects a request missing a required field, and a missing request file"
            Run = fun () ->
                withStore (fun root ->
                    let missing = call root """{"requestId":"r1","operation":"getWorkItem","repository":"x","principal":"p"}"""
                    CliPort.exitCode 1 missing
                    Assert.equal "ERROR adapter request is missing 'protocolVersion'" (missing.Err.Trim())

                    let notFound = CliHarness.ros root [ "adapter"; "call"; "--store"; "adapter-store.json"; "--request"; "nope.json" ]
                    CliPort.exitCode 1 notFound
                    Assert.equal "ERROR adapter request not found: nope.json" (notFound.Err.Trim())) } ]

    let private publish root (target: string) =
        CliHarness.ros root [ "adapter"; "publish"; "--target"; target ]

    let private lineCount root (relativePath: string) =
        let path = Path.Combine(root, relativePath)

        if File.Exists path then
            File.ReadAllLines path |> Array.filter (fun line -> line <> "") |> Array.length
        else
            0

    let private beginWork root (ids: string list) =
        CliHarness.rosOk root ([ "work"; "begin" ] @ (ids |> List.collect (fun id -> [ "--id"; id ])) @ [ "--occurred-at"; CliHarness.now () ])
        |> ignore

    let private publishTests =
        [ { Name = "adapter cli: publish appends events once and records a receipt per event"
            Run = fun () ->
                CliPort.withRepository publishProject (fun root ->
                    beginWork root [ "FEAT-142"; "OBL-009" ]
                    // The F# install event plus the two begin events, as with the
                    // Node bootstrap fixture this test used to install from.
                    let first = publish root ".ros/mock/events.jsonl"
                    CliPort.exitCode 0 first
                    CliPort.contains "published 3 event" first.Out
                    CliPort.contains "published 0 event" (publish root ".ros/mock/events.jsonl").Out
                    Assert.equal 3 (lineCount root ".ros/mock/events.jsonl")

                    let receipts = CliPort.readJson root ".ros/publications.json" |> fun node -> node.AsObject() |> Seq.toList
                    Assert.equal 3 receipts.Length
                    Assert.isTrue (receipts |> List.forall (fun receipt -> CliPort.stringOf receipt.Value "status" = Some "success")) "every receipt must be a success") }
          { Name = "adapter cli: publish fails on a write failure without creating publications.json"
            Run = fun () ->
                CliPort.withRepository publishProject (fun root ->
                    beginWork root [ "TASK-FAILURE" ]
                    CliHarness.write root "not-a-directory" "occupied\n"
                    let failed = publish root "not-a-directory/events.jsonl"
                    CliPort.exitCode 1 failed
                    Assert.isTrue (not (Text.RegularExpressions.Regex.IsMatch(failed.Out, "published \\d"))) "a failed publish must not report publication"
                    Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "publications.json")))) "no receipts may be written") }
          { Name = "adapter cli: publish requires --target"
            Run = fun () ->
                CliPort.withRepository publishProject (fun root ->
                    let result = CliHarness.ros root [ "adapter"; "publish" ]
                    CliPort.exitCode 1 result
                    Assert.equal "ERROR adapter publish requires --target" (result.Err.Trim())) }
          { Name = "adapter cli: publish with no source events writes empty receipts and no target file"
            Run = fun () ->
                CliPort.withRepository publishProject (fun root ->
                    File.Delete(Path.Combine(root, ".ros", "events", "events.jsonl"))
                    let result = publish root ".ros/mock/events.jsonl"
                    CliPort.exitCode 0 result
                    CliPort.contains "published 0 event(s); 0 duplicate" result.Out
                    CliPort.deepEqual "{}" (CliPort.readJson root ".ros/publications.json")
                    Assert.isTrue (not (File.Exists(Path.Combine(root, ".ros", "mock", "events.jsonl")))) "no target file may be created") } ]

    let tests = callTests @ publishTests
