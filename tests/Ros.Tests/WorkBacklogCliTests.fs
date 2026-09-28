namespace Ros.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// The backlog-queue writers through the real CLI -- `work
/// backlog-transition`, `work capture`, `work update` and `work attach` --
/// compared byte-for-byte (queue.md) and structurally (queue.json, clock
/// fields removed) with the goldens frozen from the former Node
/// implementation (ported from the retired tests/work-backlog-transition-,
/// work-capture-, work-update- and work-attach-fsharp-differential.test.mjs).
[<RequireQualifiedAccess>]
module WorkBacklogCliTests =
    /// Every retired backlog fixture: an installed, uncommitted repository
    /// with telemetry disabled. The live context keeps the installation work
    /// item `ros init` records, which is why it appears in every queue.md.
    let private withRepository prefix project (prepare: string -> unit) test =
        CliGolden.withRepository
            prefix
            project
            (fun root ->
                CliGolden.disableTelemetry root
                prepare root)
            false
            test

    let private markdown root = CliHarness.read root CliGolden.queueMarkdownPath

    let private goldenText suite key = (CliGolden.golden suite key).GetValue<string>()

    /// Queue fixtures are written as literal JSON text (apostrophes and
    /// escaped quotes exactly as the retired fixtures wrote them).
    let private writeQueue root (json: string) = CliHarness.write root CliGolden.queuePath (json + "\n")

    let private withoutKeys (keys: string list) root =
        CliGolden.without (Set.ofList keys) (CliGolden.queue root)

    let private expectError (message: string) (result: CliHarness.Run) =
        CliGolden.expectExit 1 result
        Assert.equal $"ERROR {message}" (result.Err.Trim())

    // ------------------------------------------------ work backlog-transition

    let private transitionQueue =
        """{
  "schemaVersion": "1.0.0",
  "repository": "repository",
  "nextSeq": 3,
  "items": [
    { "id": "WI-0001", "title": "It's a \"quoted\" title", "description": null, "tags": ["code", "testing"], "priority": "high", "status": "ready",
      "attachments": [{ "id": "att-1", "name": "x.txt", "size": 12, "contentType": "text/plain", "uploadedAt": "2026-01-01T00:00:00.000Z" }],
      "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z", "createdBy": "unknown", "source": "manual", "sourceReference": null },
    { "id": "WI-0002", "title": "Second", "tags": [], "priority": null, "status": "ready",
      "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z" }
  ]
}"""

    let private withTransitionRepository test =
        withRepository
            "ros-backlog-transition-differential"
            "Backlog Transition Differential"
            (fun root -> writeQueue root transitionQueue)
            test

    let private transition root id action (reason: string option) =
        CliHarness.ros
            root
            ([ "work"; "backlog-transition"; "--id"; id; "--action"; action; "--occurred-at"; "2026-09-09T18:00:00.000Z" ]
             @ (reason |> Option.map (fun value -> [ "--reason"; value ]) |> Option.defaultValue []))

    let private transitionGolden = CliGolden.golden "work-backlog-transition"
    let private transitionWithoutClock = withoutKeys [ "updatedAt" ]

    let private backlogTransition =
        [ { Name = "work backlog transition: a block writes queue.json and queue.md exactly, keeping untouched items and escaping"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-0001" "block" (Some "needs review") |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (transitionGolden "test1Queue") (transitionWithoutClock root)
                      Assert.equal (goldenText "work-backlog-transition" "test1Markdown") (markdown root)
                      let updatedAt = (CliGolden.items (CliGolden.queue root).["items"] |> List.head).["updatedAt"].GetValue<string>()
                      Assert.isTrue (not (updatedAt.Contains "\\u0027")) "updatedAt never contains an escaped apostrophe") }

          { Name = "work backlog transition: ready clears blockedReason entirely"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-0001" "block" (Some "needs review") |> ignore
                      transition root "WI-0001" "ready" None |> CliGolden.expectExit 0
                      let first = CliGolden.items (CliGolden.queue root).["items"] |> List.head
                      Assert.isTrue (not (first.AsObject().ContainsKey "blockedReason")) "ready must remove blockedReason"
                      CliGolden.jsonEqual (transitionGolden "test2Queue") (transitionWithoutClock root)
                      Assert.equal (goldenText "work-backlog-transition" "test2Markdown") (markdown root)) }

          { Name = "work backlog transition: abandon records the reason"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-0002" "abandon" (Some "no longer needed") |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (transitionGolden "test3Queue") (transitionWithoutClock root)
                      Assert.equal (goldenText "work-backlog-transition" "test3Markdown") (markdown root)) }

          { Name = "work backlog transition: an illegal transition is rejected with the exact message and writes nothing"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-0001" "abandon" None |> ignore

                      transition root "WI-0001" "ready" None
                      |> expectError "cannot ready backlog item 'WI-0001' from 'abandoned'"

                      CliGolden.jsonEqual (transitionGolden "test4Queue") (transitionWithoutClock root)) }

          { Name = "work backlog transition: a block without a reason is rejected with the exact message"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-0001" "block" None |> expectError "block requires --reason") }

          { Name = "work backlog transition: an unknown id is rejected with the exact message"
            Run =
              fun () ->
                  withTransitionRepository (fun root ->
                      transition root "WI-9999" "ready" None
                      |> expectError "'WI-9999' is not a captured local work item") } ]

    // ----------------------------------------------------------- work capture

    let private withCaptureRepository prepare test =
        withRepository "ros-work-capture-differential" "Work Capture Differential" prepare test

    let private capture root (title: string) (options: string list) =
        CliHarness.ros root ([ "work"; "capture"; "--title"; title; "--occurred-at"; "2026-09-09T18:00:00.000Z" ] @ options)

    let private captureGolden = CliGolden.golden "work-capture"
    let private clockFree = withoutKeys [ "createdAt"; "updatedAt" ]

    let private workCapture =
        [ { Name = "work capture: an auto-generated id is written exactly, keeping untouched items and escaping"
            Run =
              fun () ->
                  let queue =
                      """{ "schemaVersion": "1.0.0", "repository": "repository", "nextSeq": 3,
  "items": [{ "id": "WI-0001", "title": "It's a \"quoted\" title", "tags": ["code"], "priority": "high", "status": "ready" }] }"""

                  withCaptureRepository (fun root -> writeQueue root queue) (fun root ->
                      capture
                          root
                          "  My new task  "
                          [ "--priority"; "high"; "--description"; "  details  "; "--tag"; "alpha"; "--tag"; "beta"; "--actor"; "tester" ]
                      |> CliGolden.expectExit 0

                      CliGolden.jsonEqual (captureGolden "test1Queue") (clockFree root)
                      Assert.equal (goldenText "work-capture" "test1Markdown") (markdown root)) }

          { Name = "work capture: a missing queue.json is created from scratch"
            Run =
              fun () ->
                  withCaptureRepository
                      (fun root ->
                          File.Delete(Path.Combine(root, CliGolden.queuePath))
                          File.Delete(Path.Combine(root, CliGolden.queueMarkdownPath)))
                      (fun root ->
                          capture root "First ever" [] |> CliGolden.expectExit 0
                          CliGolden.jsonEqual (captureGolden "test2Queue") (clockFree root)
                          Assert.equal (goldenText "work-capture" "test2Markdown") (markdown root)) }

          { Name = "work capture: an explicit id never advances nextSeq"
            Run =
              fun () ->
                  withCaptureRepository
                      (fun root -> writeQueue root """{ "schemaVersion": "1.0.0", "repository": "repository", "nextSeq": 5, "items": [] }""")
                      (fun root ->
                          capture root "Custom" [ "--id"; "TASK-CUSTOM" ] |> CliGolden.expectExit 0
                          CliGolden.jsonEqual (captureGolden "test3Queue") (clockFree root)) }

          { Name = "work capture: an empty title is rejected with the exact message and writes nothing"
            Run =
              fun () ->
                  withCaptureRepository CliGolden.noPreparation (fun root ->
                      capture root "   " [] |> expectError "add requires a non-empty title"
                      CliGolden.jsonEqual (captureGolden "test4Queue") (clockFree root)) }

          { Name = "work capture: an invalid priority is rejected with the exact message"
            Run =
              fun () ->
                  withCaptureRepository CliGolden.noPreparation (fun root ->
                      capture root "x" [ "--priority"; "urgent" ]
                      |> expectError "invalid priority 'urgent'; use high, medium, or low") }

          { Name = "work capture: a duplicate explicit id is rejected with the exact message"
            Run =
              fun () ->
                  withCaptureRepository
                      (fun root ->
                          writeQueue
                              root
                              """{ "schemaVersion": "1.0.0", "repository": "repository", "nextSeq": 2, "items": [{ "id": "WI-9000", "title": "Existing", "tags": [], "priority": null, "status": "ready" }] }""")
                      (fun root ->
                          capture root "dup" [ "--id"; "WI-9000" ] |> expectError "work item 'WI-9000' already exists"
                          CliGolden.jsonEqual (captureGolden "test6Queue") (clockFree root)) } ]

    // ------------------------------------------------------------ work update

    let private updateQueue =
        """{
  "schemaVersion": "1.0.0",
  "repository": "repository",
  "nextSeq": 2,
  "items": [
    { "id": "WI-0001", "title": "It's a \"quoted\" title", "description": null, "tags": ["code"], "priority": "high", "status": "ready",
      "attachments": [{ "id": "att-1" }], "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z",
      "createdBy": "unknown", "source": "manual", "sourceReference": null }
  ]
}"""

    let private emptyQueue = """{ "schemaVersion": "1.0.0", "repository": "repository", "nextSeq": 1, "items": [] }"""

    let private withUpdateRepository (queue: string) test =
        withRepository "ros-work-update-differential" "Work Update Differential" (fun root -> writeQueue root queue) test

    let private update root id (options: string list) =
        CliHarness.ros root ([ "work"; "update"; "--id"; id; "--occurred-at"; "2026-09-09T18:00:00.000Z" ] @ options)

    let private updateGolden = CliGolden.golden "work-update"

    let private workUpdate =
        [ { Name = "work update: title, description and priority are written exactly, preserving untouched fields"
            Run =
              fun () ->
                  withUpdateRepository updateQueue (fun root ->
                      update root "WI-0001" [ "--title"; "  New title  "; "--description"; "  new desc  "; "--priority"; "low" ]
                      |> CliGolden.expectExit 0

                      CliGolden.jsonEqual (updateGolden "test1Queue") (clockFree root)
                      Assert.equal (goldenText "work-update" "test1Markdown") (markdown root)) }

          { Name = "work update: a whitespace description clears it to null"
            Run =
              fun () ->
                  withUpdateRepository updateQueue (fun root ->
                      update root "WI-0001" [ "--description"; "   " ] |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (updateGolden "test2Queue") (clockFree root)) }

          { Name = "work update: omitting --tag leaves tags untouched"
            Run =
              fun () ->
                  withUpdateRepository updateQueue (fun root ->
                      update root "WI-0001" [ "--title"; "Only title changes" ] |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (updateGolden "test3Queue") (clockFree root)) }

          { Name = "work update: an id known only to the live context is upserted into the queue"
            Run =
              fun () ->
                  withUpdateRepository emptyQueue (fun root ->
                      update root (CliGolden.installWorkItemId ()) [ "--tag"; "upserted" ] |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (updateGolden "test4Queue") (clockFree root)
                      Assert.equal (goldenText "work-update" "test4Markdown") (markdown root)) }

          { Name = "work update: an id in neither the queue nor the live context is rejected with the exact message"
            Run =
              fun () ->
                  withUpdateRepository updateQueue (fun root ->
                      update root "WI-9999" [ "--title"; "x" ] |> expectError "work item 'WI-9999' was not found"
                      CliGolden.jsonEqual (updateGolden "test5Queue") (clockFree root)) }

          { Name = "work update: an empty title is rejected with the exact message and writes nothing"
            Run =
              fun () ->
                  withUpdateRepository updateQueue (fun root ->
                      update root "WI-0001" [ "--title"; "   " ] |> expectError "title cannot be empty"
                      CliGolden.jsonEqual (updateGolden "test6Queue") (clockFree root)) } ]

    // ------------------------------------------------------------ work attach

    let private attachQueue =
        """{
  "schemaVersion": "1.0.0",
  "repository": "repository",
  "nextSeq": 2,
  "items": [
    { "id": "WI-0001", "title": "It's a \"quoted\" title", "description": null, "tags": ["code"], "priority": "high", "status": "ready",
      "attachments": [{ "id": "ATT-1", "seq": 1, "name": "old.txt", "file": "1-old.txt", "size": 3, "contentType": null, "uploadedAt": "2026-01-01T00:00:00.000Z" }],
      "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z" }
  ]
}"""

    let private withAttachRepository (queue: string) test =
        withRepository
            "ros-work-attach-differential"
            "Work Attach Differential"
            (fun root ->
                CliHarness.write root "upload.txt" "hello world, with a weird name coming"
                writeQueue root queue)
            test

    let private attach root id (file: string) =
        CliHarness.ros root [ "work"; "attach"; "--id"; id; "--occurred-at"; "2026-09-09T18:00:00.000Z"; "--file"; file ]

    /// The retired `withoutClockFields`: item clocks and attachment upload
    /// times removed, and a missing attachments list read as empty.
    let private attachClockFree root =
        let queue = CliGolden.without (Set.ofList [ "createdAt"; "updatedAt"; "uploadedAt" ]) (CliGolden.queue root)

        for item in CliGolden.items queue.["items"] do
            if isNull item.["attachments"] then item.["attachments"] <- JsonArray()

        queue

    let private attachGolden = CliGolden.golden "work-attach"

    let private workAttach =
        [ { Name = "work attach: a named attachment is written exactly, preserving prior attachments and escaping"
            Run =
              fun () ->
                  withAttachRepository attachQueue (fun root ->
                      attach root "WI-0001" "upload.txt=My Cool File!.txt" |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (attachGolden "test1Queue") (attachClockFree root)
                      Assert.equal (goldenText "work-attach" "test1Markdown") (markdown root)
                      let stored = goldenText "work-attach" "test1StoredFile"
                      let attachments = CliGolden.items (CliGolden.items (CliGolden.queue root).["items"] |> List.head).["attachments"]
                      Assert.equal stored ((List.item 1 attachments).["file"].GetValue<string>())

                      Assert.equal
                          (Convert.FromBase64String(goldenText "work-attach" "test1FileBytesBase64") |> Array.toList)
                          (File.ReadAllBytes(Path.Combine(root, ".ros", "work", "attachments", "WI-0001", stored)) |> Array.toList)) }

          { Name = "work attach: without a name the attachment is named after the source file"
            Run =
              fun () ->
                  withAttachRepository attachQueue (fun root ->
                      attach root "WI-0001" "upload.txt" |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (attachGolden "test2Queue") (attachClockFree root)) }

          { Name = "work attach: an id known only to the live context is upserted into the queue"
            Run =
              fun () ->
                  withAttachRepository emptyQueue (fun root ->
                      attach root (CliGolden.installWorkItemId ()) "upload.txt" |> CliGolden.expectExit 0
                      CliGolden.jsonEqual (attachGolden "test3Queue") (attachClockFree root)
                      Assert.equal (goldenText "work-attach" "test3Markdown") (markdown root)) }

          { Name = "work attach: an id in neither the queue nor the live context is rejected with the exact message"
            Run =
              fun () ->
                  withAttachRepository attachQueue (fun root ->
                      attach root "WI-9999" "upload.txt" |> expectError "work item 'WI-9999' was not found"
                      CliGolden.jsonEqual (attachGolden "test4Queue") (attachClockFree root)) }

          { Name = "work attach: an invalidly shaped id is rejected with the exact message"
            Run =
              fun () ->
                  withAttachRepository attachQueue (fun root ->
                      attach root "not-an-id" "upload.txt" |> expectError "invalid work-item ID 'not-an-id'") } ]

    let tests = backlogTransition @ workCapture @ workUpdate @ workAttach
