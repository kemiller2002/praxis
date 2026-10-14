namespace Praxis.UiConformance

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Tests

/// `praxis web serve`: the work backlog queue, item, validation and
/// execution (agent activity) pages, driven through a real served CLI over a
/// disposable fixture repository.
[<RequireQualifiedAccess>]
module WebSurfaces =
    open Scenario

    /// Fixture work items, one per state each scenario starts from.
    let private captured = [ "WI-QREADY"; "WI-EDIT"; "WI-ABANDON"; "WI-READY" ]
    let private ready = [ "WI-START"; "WI-BBLOCK" ]
    let private active = [ "WI-BLOCK"; "WI-DONE"; "WI-EXEC"; "WI-EXEC2" ]
    let private liveBlocked = [ "WI-QRESUME"; "WI-RESUME" ]

    let private seed (server: ServedProcess) =
        let post path body =
            let response = server.PostJson(path, body)
            let status = int response.StatusCode
            let body = response.Content.ReadAsStringAsync().Result
            if status <> 200 then failwith $"fixture {path} answered {status}: {body}"

        for id in captured @ ready @ active @ liveBlocked do
            post "/api/work" $"""{{"title":"{id} fixture","id":"{id}","tags":["fixture"]}}"""

        for id in ready @ active @ liveBlocked do
            post $"/api/work/{id}/ready" "{}"

        for id in active @ liveBlocked do
            post $"/api/work/{id}/start" """{"type":"feature"}"""

        for id in liveBlocked do
            post $"/api/work/{id}/block" """{"reason":"fixture"}"""

    let private rows (server: ServedProcess) =
        (JsonNode.Parse(server.Get("/api/work").Content.ReadAsStringAsync().Result)).AsArray()
        |> Seq.map (fun row -> row["id"].GetValue<string>())
        |> List.ofSeq

    let private executionOf (server: ServedProcess) (id: string) =
        let body = server.Get($"/api/executions?workItem={id}").Content.ReadAsStringAsync().Result
        let listed = JsonNode.Parse(body).AsArray()
        listed[0].["executionId"].GetValue<string>()

    let private row (id: string) = pattern "row" $"^{id}\\b"
    let private notice (text: string) = shows (Scenario.text text)

    /// The share block every page ends with (SAF-URL-10).
    let private shareScenarios (prefix: string) (origin: string) (path: string) =
        [ { Id = $"{prefix}-skip"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link "Skip to main content"
            Outcome = urlIs (origin + path + "#main")
            Intent = "skip repeated navigation to the main content"
            ExpectedOutcome = "the URL targets #main"
            Reset = "reload the page" }
          { Id = $"{prefix}-permalink"
            Path = path + "#main"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link (origin + path)
            Outcome = urlIs (origin + path)
            Intent = "open this view at its canonical address"
            ExpectedOutcome = "the address bar holds the canonical URL"
            Reset = "none needed (read-only)" }
          { Id = $"{prefix}-copy-link"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = button "Copy link"
            Outcome = notice "Link copied."
            Intent = "copy the view's address in one action (url-state.js enhancement)"
            ExpectedOutcome = "the status reads \"Link copied.\" and the clipboard holds the address"
            Reset = "reload the page" } ]

    /// A link to the page itself starts from the page's #main, so activating
    /// it still demonstrates a transition (the fragment is dropped).
    let private startFor (path: string) (destination: string) = if path = destination then path + "#main" else path

    let private navScenarios (prefix: string) (origin: string) (path: string) =
        [ { Id = $"{prefix}-nav-validate"
            Path = startFor path "/validate"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (navigation "Primary") (link "Validate")
            Outcome = urlIs (origin + "/validate")
            Intent = "open the validation findings"
            ExpectedOutcome = "the validation page loads"
            Reset = "none needed (read-only)" }
          { Id = $"{prefix}-nav-queue"
            Path = startFor path "/"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (navigation "Primary") (link "Queue")
            Outcome = urlIs (origin + "/")
            Intent = "return to the work queue"
            ExpectedOutcome = "the queue loads"
            Reset = "none needed (read-only)" } ]

    let private queueRowScenarios (origin: string) (id: string) (links: (string * string) list) =
        [ { Id = $"queue-{id}-id"
            Path = "/"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (row id) (link id)
            Outcome = urlIs (origin + $"/work/{id}")
            Intent = $"open {id}"
            ExpectedOutcome = $"/work/{id} loads"
            Reset = "none needed (read-only)" }
          { Id = $"queue-{id}-show"
            Path = "/"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (row id) (link "Show")
            Outcome = urlIs (origin + $"/work/{id}")
            Intent = $"show {id}"
            ExpectedOutcome = $"/work/{id} loads"
            Reset = "none needed (read-only)" } ]
        @ (links
           |> List.map (fun (name, anchor) ->
               { Id = $"queue-{id}-{anchor}"
                 Path = "/"
                 Prepare = nothing
                 RuleId = "ULI-A02"
                 Action = Evaluate.within (row id) (link name)
                 Outcome = urlIs (origin + $"/work/{id}#{anchor}")
                 Intent = $"go to the {name} form of {id} (it needs input)"
                 ExpectedOutcome = $"/work/{id}#{anchor} loads"
                 Reset = "none needed (read-only)" }))

    let private queueScenarios (origin: string) (ids: string list) =
        let queueOnly =
            [ { Id = "queue-filter"
                Path = "/"
                Prepare = fun page -> Observe.fill page (Evaluate.within (region "Filter work") (textbox "Tag")) "fixture"
                RuleId = "ULI-A02"
                Action = button "Filter"
                Outcome = urlIs (origin + "/?tag=fixture")
                Intent = "filter the queue by tag"
                ExpectedOutcome = "the canonical filtered URL loads"
                Reset = "Clear" }
              { Id = "queue-clear"
                Path = "/?tag=fixture"
                Prepare = nothing
                RuleId = "ULI-A02"
                Action = link "Clear"
                Outcome = urlIs (origin + "/")
                Intent = "remove every filter"
                ExpectedOutcome = "the unfiltered queue loads"
                Reset = "none needed (read-only)" }
              { Id = "queue-add-refused"
                Path = "/"
                Prepare = fun page -> Observe.fill page (textbox "Title") "   "
                RuleId = "ULI-A07"
                Action = button "Add"
                Outcome = shows (pattern "alert" ".*")
                Intent = "failure path: capture with a blank title"
                ExpectedOutcome = "the CLI's refusal is shown as an inline fault and nothing is captured"
                Reset = "fill a title and retry (queue-add)" }
              { Id = "queue-add"
                Path = "/"
                Prepare = fun page -> Observe.fill page (textbox "Title") "Captured through the browser"
                RuleId = "ULI-A02"
                Action = button "Add"
                Outcome = shows (pattern "text" "^Captured WI-\\d+\\.$")
                Intent = "capture a new work item"
                ExpectedOutcome = "the new item's page loads with \"Captured WI-NNNN.\""
                Reset = "each run captures a new item in a disposable fixture" }
              { Id = "queue-mark-ready"
                Path = "/"
                Prepare = nothing
                RuleId = "ULI-A02"
                Action = Evaluate.within (row "WI-QREADY") (button "Mark ready")
                Outcome = notice "Marked WI-QREADY ready."
                Intent = "mark a captured item ready in one click"
                ExpectedOutcome = "the item's page reports \"Marked WI-QREADY ready.\""
                Reset = "disposable fixture item (terminal for this run)" }
              { Id = "queue-resume"
                Path = "/"
                Prepare = nothing
                RuleId = "ULI-A02"
                Action = Evaluate.within (row "WI-QRESUME") (button "Resume")
                Outcome = notice "Resumed WI-QRESUME."
                Intent = "resume blocked work in one click"
                ExpectedOutcome = "the item's page reports \"Resumed WI-QRESUME.\""
                Reset = "disposable fixture item (terminal for this run)" } ]

        let rowLinks (id: string) =
            match id with
            | id when List.contains id captured -> [ "Abandon", "abandon" ]
            | id when List.contains id ready -> [ "Block", "block"; "Start", "start"; "Abandon", "abandon" ]
            | id when List.contains id active -> [ "Block", "block"; "Complete", "complete" ]
            | _ -> []

        let navigation = ids |> List.collect (fun id -> queueRowScenarios origin id (rowLinks id))
        navigation @ navScenarios "queue" origin "/" @ shareScenarios "queue" origin "/" @ queueOnly

    let private detail (id: string) = $"/work/{id}"

    let private detailScenarios (origin: string) (executionId: string) =
        let section name = region name

        [ { Id = "item-back"
            Path = detail "WI-EDIT"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link "← Queue"
            Outcome = urlIs (origin + "/")
            Intent = "return to the queue"
            ExpectedOutcome = "the queue loads"
            Reset = "none needed (read-only)" }
          { Id = "item-raw-record"
            Path = detail "WI-EDIT"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.target "disclosure" "Raw record"
            Outcome = opens
            Intent = "show the raw work show record"
            ExpectedOutcome = "the details element opens"
            Reset = "reload the page" }
          { Id = "item-save"
            Path = detail "WI-EDIT"
            Prepare = fun page -> Observe.fill page (Evaluate.within (section "Edit work item") (textbox "Title")) "Edited in the browser"
            RuleId = "ULI-A02"
            Action = button "Save"
            Outcome = textOf (pattern "heading" "^WI-EDIT:") "WI-EDIT: Edited in the browser"
            Intent = "edit the item's title"
            ExpectedOutcome = "the item heading shows the new title"
            Reset = "edit again (idempotent per value)" }
          { Id = "item-attach"
            Path = detail "WI-EDIT"
            Prepare =
              fun page ->
                  let file = Path.Combine(Path.GetTempPath(), "ui-conformance-evidence.txt")
                  File.WriteAllText(file, "conformance attachment")
                  let inputs = Observe.resolve page (Evaluate.within (section "Attach files") (button "File"))
                  Observe.attachFiles page inputs.Head [ file ]
            RuleId = "ULI-A02"
            Action = button "Attach"
            Outcome = shows (link "ui-conformance-evidence.txt")
            Intent = "attach a file to the item"
            ExpectedOutcome = "a download link for the attachment appears"
            Reset = "attachments accumulate in the disposable fixture" }
          { Id = "item-abandon"
            Path = detail "WI-ABANDON"
            Prepare = fun page -> Observe.fill page (textbox "Reason for abandoning") "superseded by the conformance fixture"
            RuleId = "ULI-A02"
            Action = Evaluate.within (section "Allowed actions") (button "Abandon")
            Outcome = notice "Abandoned WI-ABANDON."
            Intent = "abandon a captured item with a reason"
            ExpectedOutcome = "\"Abandoned WI-ABANDON.\""
            Reset = "terminal state of a disposable fixture item" }
          { Id = "item-mark-ready"
            Path = detail "WI-READY"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (section "Allowed actions") (button "Mark ready")
            Outcome = notice "Marked WI-READY ready."
            Intent = "mark the item ready"
            ExpectedOutcome = "\"Marked WI-READY ready.\""
            Reset = "disposable fixture item" }
          { Id = "item-start"
            Path = detail "WI-START"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = button "Start"
            Outcome = notice "Started WI-START."
            Intent = "start the ready item with a work type"
            ExpectedOutcome = "\"Started WI-START.\""
            Reset = "disposable fixture item" }
          { Id = "item-block-backlog"
            Path = detail "WI-BBLOCK"
            Prepare = fun page -> Observe.fill page (textbox "Reason for blocking") "waiting on a dependency"
            RuleId = "ULI-A02"
            Action = Evaluate.within (section "Allowed actions") (button "Block")
            Outcome = notice "Blocked WI-BBLOCK."
            Intent = "block a ready item with a reason"
            ExpectedOutcome = "\"Blocked WI-BBLOCK.\""
            Reset = "disposable fixture item" }
          { Id = "item-block-live"
            Path = detail "WI-BLOCK"
            Prepare = fun page -> Observe.fill page (textbox "Reason for blocking") "waiting on review"
            RuleId = "ULI-A02"
            Action = Evaluate.within (section "Allowed actions") (button "Block")
            Outcome = notice "Blocked WI-BLOCK."
            Intent = "block active work with a reason"
            ExpectedOutcome = "\"Blocked WI-BLOCK.\""
            Reset = "disposable fixture item" }
          { Id = "item-complete-refused"
            Path = detail "WI-DONE"
            Prepare = nothing
            RuleId = "ULI-A07"
            Action = button "Complete"
            Outcome = shows (pattern "alert" ".*")
            Intent = "failure path: complete without the required implementation and tests evidence"
            ExpectedOutcome = "the CLI's refusal is shown as an inline fault and the work stays active"
            Reset = "name the evidence and retry (item-complete)" }
          { Id = "item-complete"
            Path = detail "WI-DONE"
            Prepare =
              fun page ->
                  let types = Observe.resolve page (textbox "Evidence type")
                  let paths = Observe.resolve page (textbox "Evidence path")

                  [ types[0], "implementation"; paths[0], "README.md"; types[1], "tests"; paths[1], "README.md" ]
                  |> List.iter (fun (element, value) ->
                      page.Send("DOM.focus", Cdp.args [ "backendNodeId", Cdp.num element.Backend ]) |> ignore
                      page.Send("Input.insertText", Cdp.args [ "text", Cdp.str value ]) |> ignore)
            RuleId = "ULI-A02"
            Action = button "Complete"
            Outcome = notice "Completed WI-DONE."
            Intent = "recovery: complete with implementation and tests evidence after the refusal"
            ExpectedOutcome = "\"Completed WI-DONE.\" (the earlier refusal changed nothing)"
            Reset = "terminal state of a disposable fixture item" }
          { Id = "item-resume"
            Path = detail "WI-RESUME"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (section "Allowed actions") (button "Resume")
            Outcome = notice "Resumed WI-RESUME."
            Intent = "resume blocked work"
            ExpectedOutcome = "\"Resumed WI-RESUME.\""
            Reset = "disposable fixture item" }
          { Id = "item-execution-link"
            Path = detail "WI-EXEC"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link executionId
            Outcome = urlIs (origin + $"/executions/{executionId}")
            Intent = "open the agent execution behind the work item"
            ExpectedOutcome = "the execution page loads"
            Reset = "none needed (read-only)" } ]
        @ navScenarios "item" origin (detail "WI-EDIT")
        @ shareScenarios "item" origin (detail "WI-EDIT")

    let private executionScenarios (origin: string) (executionId: string) (secondExecution: string) =
        let path = $"/executions/{executionId}"

        [ { Id = "execution-back"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link "← WI-EXEC"
            Outcome = urlIs (origin + "/work/WI-EXEC")
            Intent = "return to the work item"
            ExpectedOutcome = "the work item page loads"
            Reset = "none needed (read-only)" }
          { Id = "execution-nav-queue"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (navigation "Primary") (link "Queue")
            Outcome = urlIs (origin + "/")
            Intent = "return to the queue"
            ExpectedOutcome = "the queue loads"
            Reset = "none needed (read-only)" }
          { Id = "execution-block"
            Path = path
            Prepare = fun page -> Observe.fill page (textbox "Reason for block") "operator pause"
            RuleId = "ULI-A02"
            Action = button "block"
            Outcome = notice "Transition accepted."
            Intent = "block the execution through a legal transition"
            ExpectedOutcome = "\"Transition accepted.\" and the execution state reads blocked"
            Reset = "disposable fixture execution" }
          { Id = "execution-abandon"
            Path = $"/executions/{secondExecution}"
            Prepare = fun page -> Observe.fill page (textbox "Reason for abandon") "operator abandons the attempt"
            RuleId = "ULI-A02"
            Action = button "abandon"
            Outcome = notice "Transition accepted."
            Intent = "abandon the execution through a legal transition"
            ExpectedOutcome = "\"Transition accepted.\""
            Reset = "disposable fixture execution (terminal)" } ]
        @ shareScenarios "execution" origin path

    let private queueGroups =
        [ group "ULI-L10" "filter-actions-top" Top "Filter and Clear are the filter form's related actions in one row" Evaluate.contractWidths [ button "Filter"; link "Clear" ]
          group "ULI-L02" "filter-actions-bottom" Bottom "with the top edge, equal bottom edges evidence equal control heights" Evaluate.contractWidths [ button "Filter"; link "Clear" ]
          group "ULI-L10" "primary-navigation" Center "the primary navigation links share one row" Evaluate.contractWidths [ Evaluate.within (navigation "Primary") (link "Queue"); Evaluate.within (navigation "Primary") (link "Validate") ] ]

    /// Runs every web surface and returns its evidence.
    let run (browser: Browser) : SurfaceRecord list =
        let root = CliHarness.initializedRepository "ui-conformance-web" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "ui conformance fixture"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ])
            let origin = server.Client.BaseAddress.ToString().TrimEnd('/')
            seed server
            let executionId = executionOf server "WI-EXEC"
            let secondExecution = executionOf server "WI-EXEC2"
            let ids = rows server
            browser.GrantClipboard origin
            let page = browser.NewPage true

            let surface id path kind groups scenarios destinations prepare unknownScope =
                let evidence, checks = layout page origin path prepare
                let measured = Scenario.groups page origin path groups
                Observe.setViewport page 1280
                let declared = (scenarios |> List.map (fun (s: Scenario) -> s.Action)) @ (destinations |> List.map (fun (d: Destination) -> d.Action))
                let coverageReport = coverage page origin path declared
                let actions = scenarios |> List.map (Scenario.run page origin)

                { Id = id
                  Path = path
                  Kind = kind
                  ViewportWidths = Evaluate.contractWidths
                  Groups = measured
                  Actions = actions
                  Evidence = evidence @ [ $"{coverageReport.RuleId} {coverageReport.Status.Text} {coverageReport.Subject}: {coverageReport.Reason}" ]
                  Checks = checks @ [ "coverage", coverageReport.Status ]
                  Excepted = []
                  UnknownScope = unknownScope }

            let rtl = "right-to-left and translated text: the UI ships English only (no locale switch); not exercised"
            let print = "print output: not a print surface"

            // Order matters only for the disposable fixture: read-only
            // scenarios first, then each mutation on its own fixture item.
            let queue = surface "web-queue" "/" "product" queueGroups (queueScenarios origin ids) [] nothing [ rtl; print; "same-named row buttons (Mark ready, Resume) on other rows share rowActionCell; one per name is exercised" ]
            let validate = surface "web-validate" "/validate" "product" [] (navScenarios "validate" origin "/validate" @ shareScenarios "validate" origin "/validate") [] nothing [ rtl; print ]
            let execution = surface "web-execution" $"/executions/{executionId}" "product" [] (executionScenarios origin executionId secondExecution) [] nothing [ rtl; print; "execution abandon/rebind/complete transitions: same form as block; only block is exercised" ]
            let item = surface "web-item" (detail "WI-EDIT") "product" [] (detailScenarios origin executionId) [] nothing [ rtl; print ]
            [ queue; item; validate; execution ]
        finally
            CliHarness.removeDirectory root
