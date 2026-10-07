namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Ros.Infrastructure.Work

/// Real-effect tests for `adapter publish` (`DF-ROS-2026-A007`), mirroring
/// production's own event republish over `.ros/events/events.jsonl`.
[<RequireQualifiedAccess>]
module AdapterPublishEffectTests =
    let private withTemporaryRoot (run: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), $"ros-adapter-publish-{Guid.NewGuid():N}")
        Directory.CreateDirectory root |> ignore

        try
            run root
        finally
            Directory.Delete(root, true)

    let private writeEvents root (lines: string list) =
        let directory = Path.Combine(root, ".ros", "events")
        Directory.CreateDirectory directory |> ignore
        File.WriteAllText(Path.Combine(directory, "events.jsonl"), (lines |> String.concat "\n") + "\n")

    let private readLines (path: string) : string list =
        if File.Exists path then
            File.ReadAllLines path |> Array.filter (fun line -> line.Trim().Length > 0) |> Array.toList
        else
            []

    let private stringField (node: JsonObject) (name: string) : string option =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = Text.Json.JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let tests =
        [ { Name = "publish appends every event with no destination file yet, reporting zero duplicates"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeEvents
                          root
                          [ """{"eventId":"evt-1","type":"work.started"}"""; """{"eventId":"evt-2","type":"work.completed"}""" ]

                      match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                      | Error message -> failwith message
                      | Ok outcome ->
                          Assert.equal 2 outcome.Published
                          Assert.equal 0 outcome.Duplicates
                          let lines = readLines (Path.Combine(root, ".ros", "mock", "events.jsonl"))
                          Assert.equal 2 lines.Length) }

          { Name = "publish deduplicates by eventId, appending nothing on a repeated call"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeEvents root [ """{"eventId":"evt-1","type":"work.started"}""" ]

                      match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                      | Error message -> failwith message
                      | Ok _ ->
                          match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                          | Error message -> failwith message
                          | Ok outcome ->
                              Assert.equal 0 outcome.Published
                              Assert.equal 1 outcome.Duplicates
                              let lines = readLines (Path.Combine(root, ".ros", "mock", "events.jsonl"))
                              Assert.equal 1 lines.Length) }

          { Name = "publish refreshes every source event's receipt on every call, even an already-published one"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeEvents root [ """{"eventId":"evt-1","type":"work.started"}""" ]

                      match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                      | Error message -> failwith message
                      | Ok _ ->
                          let receiptsPath = Path.Combine(root, ".ros", "publications.json")
                          let firstReceipts = JsonNode.Parse(File.ReadAllText receiptsPath) :?> JsonObject
                          let firstReceipt = firstReceipts["evt-1"] :?> JsonObject
                          Assert.equal (Some "success") (stringField firstReceipt "status")
                          Assert.equal (Some ".ros/mock/events.jsonl") (stringField firstReceipt "target")

                          match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                          | Error message -> failwith message
                          | Ok outcome ->
                              Assert.equal 1 outcome.Duplicates
                              let receipts = JsonNode.Parse(File.ReadAllText receiptsPath) :?> JsonObject
                              Assert.equal 1 (Seq.length (receipts :> seq<_>))) }

          { Name = "publish creates an empty publications.json and the destination directory even with zero events"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      match FileAdapterRepository.publish root ".ros/mock/events.jsonl" with
                      | Error message -> failwith message
                      | Ok outcome ->
                          Assert.equal 0 outcome.Published
                          Assert.equal 0 outcome.Duplicates
                          Assert.equal true (Directory.Exists(Path.Combine(root, ".ros", "mock")))
                          Assert.equal false (File.Exists(Path.Combine(root, ".ros", "mock", "events.jsonl")))
                          let receipts = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".ros", "publications.json"))) :?> JsonObject
                          Assert.equal 0 (Seq.length (receipts :> seq<_>))) }

          { Name = "publish fails without writing the destination or publications.json when the target's directory cannot be created"
            Run =
              fun () ->
                  withTemporaryRoot (fun root ->
                      writeEvents root [ """{"eventId":"evt-1","type":"work.started"}""" ]
                      File.WriteAllText(Path.Combine(root, "not-a-directory"), "occupied\n")

                      match FileAdapterRepository.publish root "not-a-directory/events.jsonl" with
                      | Ok outcome -> failwith $"expected a failure but got {outcome}"
                      | Error _ -> Assert.equal false (File.Exists(Path.Combine(root, ".ros", "publications.json")))) } ]
