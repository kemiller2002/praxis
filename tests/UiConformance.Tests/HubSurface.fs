namespace Praxis.UiConformance

open Praxis.Tests

/// `praxis hub serve` (project administration hub) over a disposable hub
/// root and two disposable registered repositories.
[<RequireQualifiedAccess>]
module HubSurface =
    open Scenario

    let private shared (origin: string) (path: string) =
        [ { Id = "hub-skip"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link "Skip to main content"
            Outcome = urlIs (origin + path + "#main")
            Intent = "skip to the main content"
            ExpectedOutcome = "the URL targets #main"
            Reset = "reload the page" }
          { Id = "hub-permalink"
            Path = path + "#main"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link (origin + path)
            Outcome = urlIs (origin + path)
            Intent = "open this view at its canonical address"
            ExpectedOutcome = "the canonical URL loads"
            Reset = "none needed (read-only)" }
          { Id = "hub-copy-link"
            Path = path
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = button "Copy link"
            Outcome = shows (text "Link copied.")
            Intent = "copy the view's address"
            ExpectedOutcome = "the status reads \"Link copied.\""
            Reset = "reload the page" } ]

    let private scenarios (origin: string) (first: string) (second: string) =
        let register path name =
            fun page ->
                Observe.fill page (textbox "Repository path") path
                Observe.fill page (textbox "Display name") name

        [ { Id = "hub-register-refused"
            Path = "/"
            Prepare = register "/nonexistent/ui-conformance" "Missing"
            RuleId = "ULI-A07"
            Action = button "Register"
            Outcome = shows (pattern "alert" ".*")
            Intent = "failure path: register a path that is not a Praxis repository"
            ExpectedOutcome = "the refusal is shown as an inline fault; nothing is registered"
            Reset = "register a real repository (hub-register)" }
          { Id = "hub-register"
            Path = "/"
            Prepare = register first "Conformance target"
            RuleId = "ULI-A07"
            Action = button "Register"
            Outcome = shows (pattern "text" "^Registered .+\\.$")
            Intent = "recovery: register a real Praxis repository"
            ExpectedOutcome = "\"Registered ID.\" and the repository is listed"
            Reset = "Unregister" }
          { Id = "hub-register-second"
            Path = "/"
            Prepare = register second "Second target"
            RuleId = "ULI-A02"
            Action = button "Register"
            Outcome = shows (pattern "text" "^Registered .+\\.$")
            Intent = "register a second repository (to unregister later)"
            ExpectedOutcome = "\"Registered ID.\""
            Reset = "Unregister" }
          { Id = "hub-create"
            Path = "/"
            Prepare = fun page -> Observe.fill page (textbox "Title") "Created from the hub"
            RuleId = "ULI-A02"
            Action = button "Create"
            Outcome = shows (pattern "text" "^Created WI-\\d+ in .+\\.$")
            Intent = "create a work item in a registered repository through its own CLI"
            ExpectedOutcome = "\"Created WI-NNNN in REPO.\" and the item appears in the aggregated queue"
            Reset = "each run creates a new item in a disposable repository" }
          { Id = "hub-filter"
            Path = "/"
            Prepare = fun page -> Observe.fill page (Evaluate.within (region "Filter aggregated work") (textbox "Tag")) "x"
            RuleId = "ULI-A02"
            Action = button "Filter"
            Outcome = urlIs (origin + "/?tag=x")
            Intent = "filter the aggregated queue"
            ExpectedOutcome = "the canonical filtered URL loads"
            Reset = "Clear" }
          { Id = "hub-clear"
            Path = "/?tag=x"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = link "Clear"
            Outcome = urlIs (origin + "/")
            Intent = "remove every filter"
            ExpectedOutcome = "the unfiltered hub loads"
            Reset = "none needed (read-only)" }
          { Id = "hub-unregister"
            Path = "/"
            Prepare = nothing
            RuleId = "ULI-A02"
            Action = Evaluate.within (pattern "row" "^ui-conformance-second-") (button "Unregister")
            Outcome = shows (pattern "text" "^Unregistered .+\\.$")
            Intent = "unregister a repository"
            ExpectedOutcome = "\"Unregistered ID.\" and the repository leaves the list"
            Reset = "Register again" } ]
        @ shared origin "/"

    let private groups =
        [ group "ULI-L10" "hub-filter-actions-top" Top "Filter and Clear are the filter form's related actions in one row" Evaluate.contractWidths [ button "Filter"; link "Clear" ]
          group "ULI-L02" "hub-filter-actions-bottom" Bottom "with the top edge, equal bottom edges evidence equal control heights" Evaluate.contractWidths [ button "Filter"; link "Clear" ] ]

    let run (browser: Browser) : SurfaceRecord list =
        let hub = CliHarness.initializedRepository "ui-conformance-hub" (Some "project-administration")
        let first = CliHarness.initializedRepository "ui-conformance-target" None
        let second = CliHarness.initializedRepository "ui-conformance-second" None

        try
            use server = new ServedProcess(hub, [ "hub"; "serve" ])
            let origin = server.Client.BaseAddress.ToString().TrimEnd('/')
            browser.GrantClipboard origin
            let page = browser.NewPage true
            let unknownScope = [ "right-to-left and translated text: English only; not exercised"; "print output: not a print surface" ]

            // The empty hub first: nothing registered yet.
            let emptyEvidence, emptyChecks = layout page origin "/" nothing
            Observe.setViewport page 1280
            let emptyCoverage = coverage page origin "/" ((scenarios origin first second |> List.map _.Action))
            let actions = scenarios origin first second |> List.map (Scenario.run page origin)
            // Then the populated hub (two repositories registered, one removed, one item).
            let populatedEvidence, populatedChecks = layout page origin "/" nothing
            let measured = Scenario.groups page origin "/" groups
            Observe.setViewport page 1280
            let populatedCoverage = coverage page origin "/" ((scenarios origin first second |> List.map _.Action))
            let coverageLine (report: SimpleReport) state = $"{report.RuleId} {report.Status.Text} {state} {report.Subject}: {report.Reason}"

            [ { Id = "hub-home"
                Path = "/"
                Kind = "product"
                ViewportWidths = Evaluate.contractWidths
                Groups = measured
                Actions = actions
                Evidence =
                  (emptyEvidence |> List.map (fun line -> "empty hub: " + line))
                  @ (populatedEvidence |> List.map (fun line -> "populated hub: " + line))
                  @ [ coverageLine emptyCoverage "empty hub"; coverageLine populatedCoverage "populated hub" ]
                Checks = emptyChecks @ populatedChecks @ [ "coverage-empty", emptyCoverage.Status; "coverage-populated", populatedCoverage.Status ]
                Excepted = []
                UnknownScope = unknownScope } ]
        finally
            [ hub; first; second ] |> List.iter CliHarness.removeDirectory
