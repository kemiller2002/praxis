namespace Praxis.Tests

open System
open System.IO
open System.Text.Json.Nodes

/// The read views through the real CLI -- `work context`, `work list`,
/// bare `work`, `work show`, `work ready`, `add`, and the `begin`/`done`
/// aliases -- compared with the goldens frozen from the former Node
/// implementation (ported from the retired tests/work-context- and
/// work-list-fsharp-differential.test.mjs).
[<RequireQualifiedAccess>]
module WorkViewCliTests =
    let private work root (arguments: string list) = CliHarness.ros root ("work" :: arguments)

    let private workOk root arguments = work root arguments |> CliGolden.expectExit 0

    let private add root (arguments: string list) =
        CliHarness.ros root ("add" :: arguments) |> CliGolden.expectExit 0

    /// Installed and `git init`-ed but never committed, as the retired
    /// fixtures were.
    let private repository prefix project =
        CliGolden.repository prefix project CliGolden.noPreparation false

    let private withRepository prefix project test =
        CliGolden.withRepository prefix project CliGolden.noPreparation false test

    // ----------------------------------------------------------- work context

    let private contextGolden = CliGolden.golden "work-context"

    /// The retired `normView`: clocks dropped and the execution-id list
    /// reduced to whether it is present at all.
    let private contextView (record: JsonNode) =
        let view = JsonObject()

        for field in [ "schemaVersion"; "protocolVersion"; "repository"; "actor" ] do
            view.[field] <- CliGolden.clone record.[field]

        view.["workItems"] <-
            CliGolden.items record.["workItems"]
            |> List.map (fun item ->
                let projected = CliGolden.without (Set.ofList [ "updatedAt"; "completedAt"; "telemetryExecutionIds" ]) item
                projected.["hasExecIds"] <- JsonValue.Create(item.["telemetryExecutionIds"] :? JsonArray)
                projected)
            |> CliGolden.jsonList

        view :> JsonNode

    let private promote root id title =
        workOk root [ "capture"; "--id"; id; "--title"; title; "--occurred-at"; CliGolden.at () ]
        workOk root [ "backlog-transition"; "--id"; id; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]
        workOk root [ "start"; "--id"; id; "--occurred-at"; CliGolden.at () ]

    let private workContext =
        [ { Name = "work context: without an id every work item is listed with its allowed actions and required evidence"
            Run =
              fun () ->
                  withRepository "ros-work-context" "Work Context Differential" (fun root ->
                      promote root "WI-READY" "Ready item"
                      promote root "WI-ACTIVE" "Active item"
                      workOk root [ "block"; "--id"; "WI-ACTIVE"; "--reason"; "waiting"; "--occurred-at"; CliGolden.at () ]
                      let result = work root [ "context" ]
                      CliGolden.expectExit 0 result
                      CliGolden.jsonEqual (contextGolden "list") (contextView (CliGolden.parse result.Out))) }

          { Name = "work context: an id filters to the requested item, preserving unmodeled fields"
            Run =
              fun () ->
                  withRepository "ros-work-context" "Work Context Differential" (fun root ->
                      promote root "WI-ACTIVE" "Active item"
                      let result = work root [ "context"; "WI-ACTIVE" ]
                      CliGolden.expectExit 0 result
                      CliGolden.jsonEqual (contextGolden "single") (contextView (CliGolden.parse result.Out))) }

          { Name = "work context: an unknown id is rejected with the exact message"
            Run =
              fun () ->
                  withRepository "ros-work-context" "Work Context Differential" (fun root ->
                      let result = work root [ "context"; "WI-NOPE" ]
                      CliGolden.expectExit 1 result
                      Assert.equal "ERROR work item 'WI-NOPE' is not in repository context" (result.Err.Trim())) } ]

    // -------------------------------------------------------------- work list

    let private listGolden = CliGolden.golden "work-list"

    /// Backlog-only, live, blocked, captured and attachment rows, seeded
    /// through the real commands exactly as the retired `seedVariety` did.
    let private seedVariety root =
        add root [ "Ready item"; "--id"; "WI-READY"; "--tag"; "alpha"; "--priority"; "high"; "--description"; "desc text" ]
        workOk root [ "backlog-transition"; "--id"; "WI-READY"; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]
        workOk root [ "start"; "--id"; "WI-READY"; "--occurred-at"; CliGolden.at () ]
        add root [ "Active item"; "--id"; "WI-ACTIVE" ]
        workOk root [ "backlog-transition"; "--id"; "WI-ACTIVE"; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]
        workOk root [ "start"; "--id"; "WI-ACTIVE"; "--occurred-at"; CliGolden.at () ]
        workOk root [ "block"; "--id"; "WI-ACTIVE"; "--reason"; "waiting on review"; "--occurred-at"; CliGolden.at () ]
        add root [ "Captured only item"; "--id"; "WI-CAPTURED" ]

        let source = Path.Combine(Path.GetTempPath(), $"ros-work-list-attachment-{Guid.NewGuid():N}.txt")
        File.WriteAllText(source, "attach\n")

        try
            add root [ "Ready with attachment"; "--id"; "WI-ATTACH" ]
            workOk root [ "attach"; "--id"; "WI-ATTACH"; "--occurred-at"; CliGolden.at (); "--file"; $"{source}=note.txt" ]
            workOk root [ "backlog-transition"; "--id"; "WI-ATTACH"; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]
        finally
            File.Delete source

    /// The read-only view tests share one seeded repository (seeding is by far
    /// the slowest step); it is removed when the test process exits.
    let private seeded =
        lazy
            (let root = repository "ros-work-list" "Work List Differential"
             AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> CliHarness.removeDirectory root)
             seedVariety root
             root)

    /// The retired `normRows`: attachment upload times dropped and rows
    /// ordered by id.
    let private rows (node: JsonNode) =
        CliGolden.items node
        |> List.map (CliGolden.without (Set.singleton "uploadedAt"))
        |> List.sortWith (fun left right -> String.CompareOrdinal(left.["id"].GetValue<string>(), right.["id"].GetValue<string>()))
        |> CliGolden.jsonList

    let private listed (arguments: string list) =
        let result = work seeded.Value arguments
        CliGolden.expectExit 0 result
        rows (CliGolden.parse result.Out)

    let private ids (node: JsonNode) =
        CliGolden.items node |> List.map (fun row -> row.["id"].GetValue<string>())

    let private workList =
        [ { Name = "work list: the merged view covers backlog-only, live, blocked and attachment rows"
            Run = fun () -> CliGolden.jsonEqual (listGolden "mergedWorkView") (listed [ "list" ]) }

          { Name = "work list: bare work is identical to work list"
            Run = fun () -> CliGolden.jsonEqual (listGolden "bareWork") (listed []) }

          { Name = "work list: work show returns the detail and row for backlog-only, live and blocked items"
            Run =
              fun () ->
                  let show = listGolden "show"

                  for id in [ "WI-READY"; "WI-ACTIVE"; "WI-CAPTURED"; "WI-ATTACH" ] do
                      let result = work seeded.Value [ "show"; id ]
                      CliGolden.expectExit 0 result
                      let parsed = CliGolden.parse result.Out
                      CliGolden.jsonEqual show.[id].["detail"] parsed.["detail"]
                      CliGolden.jsonEqual show.[id].["row"] (CliGolden.without (Set.singleton "uploadedAt") parsed) }

          { Name = "work list: work show rejects an unknown id with the exact message"
            Run =
              fun () ->
                  withRepository "ros-work-list" "Work List Differential" (fun root ->
                      let result = work root [ "show"; "WI-NOPE" ]
                      CliGolden.expectExit 1 result
                      Assert.equal "ERROR work item 'WI-NOPE' was not found" (result.Err.Trim())) }

          { Name = "work list: --tag requires every tag to match"
            Run =
              fun () ->
                  let rows = listed [ "list"; "--tag"; "alpha" ]
                  CliGolden.jsonEqual (listGolden "tagAlpha") rows
                  Assert.equal [ "WI-READY" ] (ids rows) }

          { Name = "work list: --status filters on the exact status and combines with --tag"
            Run =
              fun () ->
                  CliGolden.jsonEqual (listGolden "statusReady") (listed [ "list"; "--status"; "ready" ])
                  // WI-READY ends "active" and only the untagged WI-ATTACH ends
                  // "ready", so the combination is a real empty result.
                  let combined = listed [ "list"; "--status"; "ready"; "--tag"; "alpha" ]
                  CliGolden.jsonEqual (listGolden "statusReadyTagAlpha") combined
                  Assert.empty (ids combined) }

          { Name = "work list: work ready without an id is the ready-status view"
            Run = fun () -> CliGolden.jsonEqual (listGolden "readyView") (listed [ "ready" ]) }

          { Name = "work list: work ready with an id is rejected with a redirect to backlog-transition"
            Run =
              fun () ->
                  let result = work seeded.Value [ "ready"; "WI-CAPTURED" ]
                  CliGolden.expectExit 2 result
                  CliGolden.contains "work backlog-transition --action ready --id ID" result.Err }

          { Name = "work list: add writes the fields the former implementation wrote"
            Run =
              fun () ->
                  withRepository "ros-work-list" "Work List Differential" (fun root ->
                      let result =
                          CliHarness.ros
                              root
                              [ "add"; "A new obligation"; "--tag"; "alpha"; "--tag"; "beta"; "--priority"; "high"; "--id"; "WI-NEW"
                                "--description"; "desc"; "--occurred-at"; "2026-01-01T00:00:00.000Z" ]

                      CliGolden.expectExit 0 result
                      let item = CliGolden.parse result.Out
                      let fields = [ "id"; "title"; "status"; "tags"; "priority" ]

                      let project (row: JsonNode) =
                          let projection = JsonObject()

                          for field in fields do
                              projection.[field] <- CliGolden.clone row.[field]

                          projection :> JsonNode

                      CliGolden.jsonEqual (listGolden "addItem") (project item)
                      let listing = work root [ "list" ]
                      CliGolden.expectExit 0 listing

                      CliGolden.jsonEqual
                          (listGolden "addList")
                          (CliGolden.items (rows (CliGolden.parse listing.Out)) |> List.map project |> CliGolden.jsonList)) }

          { Name = "work list: add without a title is rejected as a usage error"
            Run =
              fun () ->
                  withRepository "ros-work-list" "Work List Differential" (fun root ->
                      let result = CliHarness.ros root [ "add" ]
                      CliGolden.expectExit 2 result
                      CliGolden.contains "add requires a title" result.Err) }

          { Name = "work list: work begin and work done behave identically to work start and work complete"
            Run =
              fun () ->
                  let beginRoot = repository "ros-work-list-begin" "Work List Differential"

                  try
                      let startRoot = repository "ros-work-list-start" "Work List Differential"

                      try
                          for root in [ beginRoot; startRoot ] do
                              add root [ "Alias test item"; "--id"; "WI-ALIAS" ]
                              workOk root [ "backlog-transition"; "--id"; "WI-ALIAS"; "--action"; "ready"; "--occurred-at"; CliGolden.at () ]

                          let states (result: CliHarness.Run) =
                              CliGolden.expectExit 0 result

                              CliGolden.items (CliGolden.parse result.Out).["workItems"]
                              |> List.map (fun item -> CliGolden.text item "id", CliGolden.text item "state")

                          let transition root command at extra =
                              work root ([ command; "--id"; "WI-ALIAS"; "--occurred-at"; at ] @ extra)

                          Assert.equal
                              (states (transition startRoot "start" "2026-01-01T00:00:00.000Z" []))
                              (states (transition beginRoot "begin" "2026-01-01T00:00:00.000Z" []))

                          let evidence = [ "--evidence"; "implementation=ros"; "--evidence"; "tests=ros" ]

                          Assert.equal
                              (states (transition startRoot "complete" "2026-01-01T00:01:00.000Z" evidence))
                              (states (transition beginRoot "done" "2026-01-01T00:01:00.000Z" evidence))
                      finally
                          CliHarness.removeDirectory startRoot
                  finally
                      CliHarness.removeDirectory beginRoot } ]

    let tests = workContext @ workList
