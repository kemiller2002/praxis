namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Typed tests for `work context [ID]` (`FileWorkContextRepository.
/// readContextView`), a pure read-only view requiring no lock at all --
/// the first of the four command-surface rows `EV-ROS-2026-A046` marks
/// "No F# equivalent" and never assigned to MIG-07/MIG-08.
[<RequireQualifiedAccess>]
module WorkContextViewTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-work-context-view-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeContext root (json: string) =
        let contextDirectory = Path.Combine(root, ".ros", "context")
        Directory.CreateDirectory contextDirectory |> ignore
        File.WriteAllText(Path.Combine(contextDirectory, "current.json"), json)

    let private writeRosConfig root =
        File.WriteAllText(
            Path.Combine(root, "ros.json"),
            """{"workProtocol":{"completionEvidence":{"default":["implementation","tests"],"research":["research-record"],"mechanical":[]}}}"""
        )

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private stringArrayField (node: JsonObject) (name: string) : string list =
        match node[name] with
        | :? JsonArray as array ->
            array
            |> Seq.choose (function
                | :? JsonValue as v -> Some(v.GetValue<string>())
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private itemAt (view: JsonObject) (index: int) : JsonObject =
        match view["workItems"] with
        | :? JsonArray as array -> array.[index] :?> JsonObject
        | _ -> failwith "expected a workItems array"

    let tests =
        [ { Name = "readContextView with no requested ID returns every item, each carrying allowedActions and requiredEvidenceForCompletion computed fresh"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeContext
                          root
                          """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"repo","actor":"actor","workItems":[
                              {"id":"WI-A","type":"task","state":"active","semanticState":"active","evidence":[]},
                              {"id":"WI-B","type":"task","state":"blocked","semanticState":"blocked","evidence":[],"blockReason":"waiting"}
                          ]}"""

                      match FileWorkContextRepository.readContextView root None with
                      | Error message -> failwith message
                      | Ok view ->
                          Assert.equal (Some "repo") (stringField view "repository")
                          Assert.equal (Some "actor") (stringField view "actor")

                          let itemA = itemAt view 0
                          Assert.equal [ "block"; "complete" ] (stringArrayField itemA "allowedActions")
                          Assert.equal [ "implementation"; "tests" ] (stringArrayField itemA "requiredEvidenceForCompletion")

                          let itemB = itemAt view 1
                          Assert.equal [ "resume" ] (stringArrayField itemB "allowedActions")
                          Assert.equal (Some "waiting") (stringField itemB "blockReason")) }

          { Name = "readContextView filters to the requested ID, preserving unmodeled fields verbatim"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeRosConfig root

                      writeContext
                          root
                          """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"repo","actor":"actor","workItems":[
                              {"id":"WI-A","type":"task","state":"active","semanticState":"active","evidence":[]},
                              {"id":"WI-R","type":"research","state":"complete","semanticState":"complete","evidence":[],"conclusion":"confirmed"}
                          ]}"""

                      match FileWorkContextRepository.readContextView root (Some "WI-R") with
                      | Error message -> failwith message
                      | Ok view ->
                          match view["workItems"] with
                          | :? JsonArray as array -> Assert.equal 1 array.Count
                          | _ -> failwith "expected a workItems array"

                          let item = itemAt view 0
                          Assert.equal (Some "confirmed") (stringField item "conclusion")
                          Assert.equal [] (stringArrayField item "allowedActions")
                          Assert.equal [ "research-record" ] (stringArrayField item "requiredEvidenceForCompletion")) }

          { Name = "readContextView rejects a requested ID not present in context with production's exact message"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeContext root """{"schemaVersion":"1.0.0","protocolVersion":"1.0.0","repository":"repo","actor":"actor","workItems":[]}"""

                      match FileWorkContextRepository.readContextView root (Some "WI-NOPE") with
                      | Ok view -> failwith $"expected a rejection but got {view}"
                      | Error message -> Assert.equal "work item 'WI-NOPE' is not in repository context" message) }

          { Name = "readContextView synthesizes an empty view when no context file exists yet"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      match FileWorkContextRepository.readContextView root None with
                      | Error message -> failwith message
                      | Ok view ->
                          match view["workItems"] with
                          | :? JsonArray as array -> Assert.equal 0 array.Count
                          | _ -> failwith "expected a workItems array"

                          Assert.equal (Some "1.0.0") (stringField view "schemaVersion")) } ]
