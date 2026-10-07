namespace Praxis.Tests

open System.Text.Json.Nodes
open Praxis.Domain.Work

/// PRAXIS-HYG-02: a backlog row whose terminal status contradicts the live
/// work item with the same ID fails validation, and `work reidentify`
/// repairs exactly that case (an ID reused by a different obligation, as
/// WI-0061 was) without touching the live item.
[<RequireQualifiedAccess>]
module BacklogReidentificationTests =
    let private row id status : BacklogQueueItemRecord =
        { Id = id; Status = status; Priority = None }

    let private live (pairs: (string * LiveWorkState) list) = Map.ofList pairs

    let private request (rowValue: BacklogQueueItemRecord option) : BacklogReidentificationRequest =
        { Id = "WI-0061"
          NewId = "WI-0099"
          Reason = Some "a different obligation reused the ID"
          Row = rowValue
          LiveStates = live [ "WI-0061", LiveWorkState.Complete ]
          KnownIds = set [ "WI-0061"; "WI-0062" ] }

    let private expectRejected expected outcome =
        match outcome with
        | BacklogReidentificationOutcome.Rejected rejection -> Assert.equal expected rejection
        | BacklogReidentificationOutcome.Planned plan -> failwith $"expected a rejection, got {plan}"

    let private disagreement =
        [ { Name = "status agreement: a terminal backlog status that matches the live state is not a finding"
            Run =
              fun () ->
                  Assert.empty (
                      BacklogQueueValidation.contextDisagreements
                          [ row "WI-0001" "complete"; row "WI-0002" "abandoned" ]
                          (live [ "WI-0001", LiveWorkState.Complete; "WI-0002", LiveWorkState.Abandoned ])
                  ) }
          { Name = "status agreement: pre-promotion backlog statuses never disagree, whatever the live state"
            Run =
              fun () ->
                  Assert.empty (
                      BacklogQueueValidation.contextDisagreements
                          [ row "WI-0001" "ready"; row "WI-0002" "captured"; row "WI-0003" "blocked" ]
                          (live
                              [ "WI-0001", LiveWorkState.Complete
                                "WI-0002", LiveWorkState.Active
                                "WI-0003", LiveWorkState.Abandoned ])
                  ) }
          { Name = "status agreement: a terminal backlog row with no live item is not a finding"
            Run = fun () -> Assert.empty (BacklogQueueValidation.contextDisagreements [ row "WI-0001" "abandoned" ] Map.empty) }
          { Name = "status agreement: an abandoned backlog row whose live item is complete is a finding"
            Run =
              fun () ->
                  let finding =
                      Assert.single (
                          BacklogQueueValidation.contextDisagreements
                              [ row "WI-0061" "abandoned" ]
                              (live [ "WI-0061", LiveWorkState.Complete ])
                      )

                  Assert.equal ".ros/work/queue.json" finding.Path
                  Assert.equal BacklogQueueValidation.statusAgreementField finding.Field
                  Assert.equal "backlog item 'WI-0061' is 'abandoned' but live work item 'WI-0061' is 'complete'" finding.Message }
          { Name = "status agreement: a complete backlog row whose live item is still active is a finding"
            Run =
              fun () ->
                  let finding =
                      Assert.single (
                          BacklogQueueValidation.contextDisagreements [ row "WI-0007" "complete" ] (live [ "WI-0007", LiveWorkState.Active ])
                      )

                  Assert.equal "backlog item 'WI-0007' is 'complete' but live work item 'WI-0007' is 'active'" finding.Message } ]

    let private planning =
        [ { Name = "reidentify plan: a disagreeing row gets the new ID with the trimmed reason"
            Run =
              fun () ->
                  match BacklogReidentification.plan { request (Some(row "WI-0061" "abandoned")) with Reason = Some "  reused  " } with
                  | BacklogReidentificationOutcome.Planned plan ->
                      Assert.equal
                          { Id = "WI-0061"
                            NewId = "WI-0099"
                            Reason = "reused" }
                          plan
                  | BacklogReidentificationOutcome.Rejected rejection -> failwith $"unexpected rejection {rejection}" }
          { Name = "reidentify plan: an ID absent from the backlog is refused"
            Run =
              fun () ->
                  BacklogReidentification.plan (request None)
                  |> expectRejected (BacklogReidentificationRejection.NotInBacklog "WI-0061") }
          { Name = "reidentify plan: a missing or blank reason is refused"
            Run =
              fun () ->
                  for reason in [ None; Some "   " ] do
                      BacklogReidentification.plan { request (Some(row "WI-0061" "abandoned")) with Reason = reason }
                      |> expectRejected BacklogReidentificationRejection.ReasonRequired }
          { Name = "reidentify plan: an invalid new ID is refused"
            Run =
              fun () ->
                  BacklogReidentification.plan { request (Some(row "WI-0061" "abandoned")) with NewId = "not an id" }
                  |> expectRejected (BacklogReidentificationRejection.InvalidNewId "not an id") }
          { Name = "reidentify plan: a new ID already used anywhere is refused"
            Run =
              fun () ->
                  BacklogReidentification.plan { request (Some(row "WI-0061" "abandoned")) with NewId = "WI-0062" }
                  |> expectRejected (BacklogReidentificationRejection.NewIdInUse "WI-0062") }
          { Name = "reidentify plan: a row that agrees with its live item, or is pre-promotion, is refused"
            Run =
              fun () ->
                  for status in [ "complete"; "ready"; "captured"; "blocked" ] do
                      BacklogReidentification.plan (request (Some(row "WI-0061" status)))
                      |> expectRejected (BacklogReidentificationRejection.NoDisagreement "WI-0061") } ]

    // ----------------------------------------------------------------- CLI

    let private collidingQueue installId =
        $$"""{
  "schemaVersion": "1.0.0",
  "repository": "repository",
  "nextSeq": 2,
  "items": [
    { "id": "{{installId}}", "title": "A different obligation", "description": null, "tags": [], "priority": "medium", "status": "abandoned",
      "attachments": [], "createdAt": "2026-01-01T00:00:00.000Z", "updatedAt": "2026-01-01T00:00:00.000Z", "createdBy": "unknown",
      "source": "manual", "sourceReference": null, "abandonedReason": "re-captured elsewhere" },
    { "id": "WI-0001", "title": "Untouched", "tags": [], "priority": null, "status": "ready" }
  ]
}"""

    let private withCollision (test: string -> string -> unit) =
        let installId = CliGolden.installWorkItemId ()

        CliGolden.withRepository
            "praxis-reidentify"
            "Reidentify"
            (fun root ->
                CliGolden.disableTelemetry root
                CliHarness.write root CliGolden.queuePath (collidingQueue installId + "\n"))
            false
            (fun root -> test root installId)

    let private reidentify root (arguments: string list) =
        CliHarness.ros root ([ "work"; "reidentify"; "--occurred-at"; "2026-10-06T21:00:00.000Z" ] @ arguments)

    let private queueIds root =
        CliGolden.items (CliGolden.queue root).["items"] |> List.map (fun item -> CliGolden.text item "id")

    let private cli =
        [ { Name = "work reidentify: validate fails on the collision, and reidentify repairs only the backlog row"
            Run =
              fun () ->
                  withCollision (fun root installId ->
                      let before = CliHarness.ros root [ "work"; "backlog-validate"; "--json" ]
                      CliGolden.expectExit 1 before
                      CliGolden.contains $"backlog item '{installId}' is 'abandoned' but live work item '{installId}' is 'complete'" before.Out
                      CliGolden.expectExit 1 (CliHarness.ros root [ "validate" ])
                      let contextBefore = CliHarness.read root CliGolden.contextPath

                      reidentify root [ "--id"; installId; "--new-id"; "WI-0002"; "--reason"; "a different obligation reused the ID" ]
                      |> CliGolden.expectExit 0

                      Assert.equal [ Some "WI-0002"; Some "WI-0001" ] (queueIds root)
                      let renamed = CliGolden.items (CliGolden.queue root).["items"] |> List.head
                      Assert.equal (Some installId) (CliGolden.text renamed "reidentifiedFrom")
                      Assert.equal (Some "a different obligation reused the ID") (CliGolden.text renamed "reidentifiedReason")
                      Assert.equal (Some "abandoned") (CliGolden.text renamed "status")
                      Assert.equal (Some "re-captured elsewhere") (CliGolden.text renamed "abandonedReason")
                      Assert.equal contextBefore (CliHarness.read root CliGolden.contextPath)
                      CliGolden.contains "| WI-0002 | A different obligation | abandoned |" (CliHarness.read root CliGolden.queueMarkdownPath)
                      CliGolden.expectExit 0 (CliHarness.ros root [ "work"; "backlog-validate"; "--json" ])) }
          { Name = "work reidentify: refusals name the cause and write nothing"
            Run =
              fun () ->
                  withCollision (fun root installId ->
                      let queueBefore = CliHarness.read root CliGolden.queuePath

                      let expectRefused (arguments: string list) (message: string) =
                          let result = reidentify root arguments
                          CliGolden.expectExit 1 result
                          Assert.equal $"ERROR {message}" (result.Err.Trim())
                          Assert.equal queueBefore (CliHarness.read root CliGolden.queuePath)

                      expectRefused
                          [ "--id"; installId; "--new-id"; "WI-0002" ]
                          "reidentify requires --reason stating why the backlog row is a different obligation"

                      expectRefused [ "--id"; installId; "--new-id"; "WI-0001"; "--reason"; "x" ] "work item 'WI-0001' already exists"
                      expectRefused [ "--id"; installId; "--new-id"; "bad id"; "--reason"; "x" ] "invalid work-item ID 'bad id'"
                      expectRefused [ "--id"; "WI-0404"; "--new-id"; "WI-0002"; "--reason"; "x" ] "'WI-0404' is not a captured local work item"

                      expectRefused
                          [ "--id"; "WI-0001"; "--new-id"; "WI-0002"; "--reason"; "x" ]
                          "backlog item 'WI-0001' agrees with its live work item (or has none); only a row whose terminal status contradicts the live item with the same ID can be reidentified"

                      CliGolden.expectExit 2 (reidentify root [ "--id"; installId ])) }
          { Name = "work reidentify: a row with attachments is refused, since its stored files are keyed by the old ID"
            Run =
              fun () ->
                  withCollision (fun root installId ->
                      CliGolden.updateJson root CliGolden.queuePath (fun queue ->
                          (CliGolden.find queue.["items"] installId).["attachments"] <- JsonNode.Parse """[{ "id": "att-1" }]""")

                      let queueBefore = CliHarness.read root CliGolden.queuePath
                      let result = reidentify root [ "--id"; installId; "--new-id"; "WI-0002"; "--reason"; "x" ]
                      CliGolden.expectExit 1 result
                      Assert.equal $"ERROR '{installId}' has attachments; reidentifying a row with attachments is not supported" (result.Err.Trim())
                      Assert.equal queueBefore (CliHarness.read root CliGolden.queuePath)) } ]

    let tests = disagreement @ planning @ cli
