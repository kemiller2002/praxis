namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Real-effect tests for `adapter call` (`DF-ROS-2026-A007`), covering the
/// JSON store read/mutate/write `FileAdapterRepository.call` performs
/// around the pure `AdapterContract.decide` decisions already covered by
/// `AdapterContractTests.fs`.
[<RequireQualifiedAccess>]
module AdapterCallEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-adapter-call-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private storePath root = Path.Combine(root, "adapter-store.json")

    let private writeStore root (json: string) =
        File.WriteAllText(storePath root, json)

    let private request (json: string) : JsonObject =
        match JsonNode.Parse json with
        | :? JsonObject as obj -> obj
        | _ -> failwith "expected a JSON object"

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private baseStore =
        """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repositories":["protocol-consumer"],"workItems":{"FEAT-900":{"id":"FEAT-900","state":"ready","type":"feature"}},"events":[],"requests":{}}"""

    let tests =
        [ { Name = "call rejects a request missing a required field without touching a nonexistent store file"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let req = request """{"requestId":"r1","operation":"getWorkItem","repository":"x","principal":"p"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Ok result -> failwith $"expected a rejection but got {result}"
                      | Error message ->
                          Assert.equal "adapter request is missing 'protocolVersion'" message
                          Assert.equal false (File.Exists(storePath root))) }

          { Name = "call rejects a protocol-version mismatch without creating or touching the store file"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore
                      let before = File.ReadAllText(storePath root)

                      let req =
                          request """{"protocolVersion":"2.0.0","requestId":"r1","operation":"getWorkItem","repository":"protocol-consumer","principal":"p"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          Assert.equal (Some "failure") (stringField result "outcome")
                          Assert.equal 1 (FileAdapterRepository.exitCode result)
                          Assert.equal before (File.ReadAllText(storePath root))) }

          { Name = "call reads an existing work item verbatim with the read scope"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"r1","operation":"getWorkItem","repository":"protocol-consumer","principal":"p","scopes":["work:read"],"workItem":"FEAT-900"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          Assert.equal (Some "success") (stringField result "outcome")
                          Assert.equal 0 (FileAdapterRepository.exitCode result)

                          match result["data"] with
                          | :? JsonObject as data ->
                              match data["workItem"] with
                              | :? JsonObject as item -> Assert.equal (Some "ready") (stringField item "state")
                              | _ -> failwith "expected a workItem object"
                          | _ -> failwith "expected a data object") }

          { Name = "call transitions a work item, persists the mutation, and replays the cached result on a retry"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"req-same","operation":"transitionWorkItem","repository":"protocol-consumer","principal":"agent:test","scopes":["work:transition"],"workItem":"FEAT-900","expectedState":"ready","targetState":"active"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok first ->
                          let stored = JsonNode.Parse(File.ReadAllText(storePath root)) :?> JsonObject
                          let workItems = stored["workItems"] :?> JsonObject
                          let item = workItems["FEAT-900"] :?> JsonObject
                          Assert.equal (Some "active") (stringField item "state")
                          Assert.equal (Some "agent:test") (stringField item "updatedBy")

                          match FileAdapterRepository.call (storePath root) req with
                          | Error message -> failwith message
                          | Ok retry -> Assert.equal (first.ToJsonString()) (retry.ToJsonString())) }

          { Name = "call rejects an expected-state conflict and leaves the work item untouched"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"req-conflict","operation":"transitionWorkItem","repository":"protocol-consumer","principal":"agent:test","scopes":["work:transition"],"workItem":"FEAT-900","expectedState":"active","targetState":"complete"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          match result["error"] with
                          | :? JsonObject as error ->
                              Assert.equal (Some "state_conflict") (stringField error "code")
                              Assert.equal (Some "expected 'active', found 'ready'") (stringField error "message")
                          | _ -> failwith "expected an error object"

                          let stored = JsonNode.Parse(File.ReadAllText(storePath root)) :?> JsonObject
                          let workItems = stored["workItems"] :?> JsonObject
                          let item = workItems["FEAT-900"] :?> JsonObject
                          Assert.equal (Some "ready") (stringField item "state")) }

          { Name = "call publishes a new event and deduplicates a repeated eventId under a different requestId"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let publish requestId =
                          request (
                              """{"protocolVersion":"1.0.0","requestId":"""
                              + $"\"{requestId}\""
                              + ""","operation":"publishRepositoryEvent","repository":"protocol-consumer","principal":"ci:test","scopes":["event:publish"],"event":{"eventId":"evt-1","type":"work.completed"}}"""
                          )

                      match FileAdapterRepository.call (storePath root) (publish "req-1") with
                      | Error message -> failwith message
                      | Ok _ ->
                          match FileAdapterRepository.call (storePath root) (publish "req-2") with
                          | Error message -> failwith message
                          | Ok _ ->
                              let stored = JsonNode.Parse(File.ReadAllText(storePath root)) :?> JsonObject
                              let events = stored["events"] :?> JsonArray
                              Assert.equal 1 events.Count) }

          { Name = "call rejects an event with no eventId, leaving the events array untouched"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"r1","operation":"publishRepositoryEvent","repository":"protocol-consumer","principal":"ci:test","scopes":["event:publish"],"event":{"type":"work.completed"}}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          match result["error"] with
                          | :? JsonObject as error -> Assert.equal (Some "invalid_event") (stringField error "code")
                          | _ -> failwith "expected an error object"

                          let stored = JsonNode.Parse(File.ReadAllText(storePath root)) :?> JsonObject
                          let events = stored["events"] :?> JsonArray
                          Assert.equal 0 events.Count) }

          { Name = "call reports a remote_outcome_unknown result and exit code 2, without mutating the work item"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeStore root baseStore

                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"req-unknown","operation":"transitionWorkItem","repository":"protocol-consumer","principal":"agent:test","scopes":["work:transition"],"workItem":"FEAT-900","targetState":"active","simulateOutcome":"unknown"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          Assert.equal (Some "unknown") (stringField result "outcome")
                          Assert.equal 2 (FileAdapterRepository.exitCode result)
                          let stored = JsonNode.Parse(File.ReadAllText(storePath root)) :?> JsonObject
                          let workItems = stored["workItems"] :?> JsonObject
                          let item = workItems["FEAT-900"] :?> JsonObject
                          Assert.equal (Some "ready") (stringField item "state")) }

          { Name = "call treats a missing store file as an empty store, rejecting an unauthorized repository"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      let req =
                          request
                              """{"protocolVersion":"1.0.0","requestId":"r1","operation":"getWorkItem","repository":"protocol-consumer","principal":"p","scopes":["work:read"],"workItem":"FEAT-900"}"""

                      match FileAdapterRepository.call (storePath root) req with
                      | Error message -> failwith message
                      | Ok result ->
                          match result["error"] with
                          | :? JsonObject as error -> Assert.equal (Some "repository_unknown") (stringField error "code")
                          | _ -> failwith "expected an error object"

                          Assert.equal true (File.Exists(storePath root))) } ]
