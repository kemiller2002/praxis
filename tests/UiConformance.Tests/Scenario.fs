namespace Praxis.UiConformance

open System
open System.Text.RegularExpressions
open Praxis.Tests

/// A declared action scenario (ULI-A05): the page and state it starts from,
/// how the control is reached and invoked, and the observable outcome.
type Scenario =
    { Id: string
      Path: string
      Prepare: BrowserPage -> unit
      RuleId: string
      Action: Target
      Outcome: Outcome
      Intent: string
      ExpectedOutcome: string
      Reset: string }

/// An action whose destination is verified without activating it: a link to
/// an external service (the contract forbids claiming the external
/// operation), checked by its exact `href`.
type Destination =
    { Id: string
      Action: Target
      Href: string
      Intent: string }

/// A declared alignment group and the widths at which its peers share a row.
type DeclaredGroup =
    { Group: AlignmentGroup
      Widths: int list }

module Scenario =
    let timeoutMs = 30000

    let link name = Evaluate.target "link" name
    let button name = Evaluate.target "button" name
    let region name = Evaluate.target "region" name
    let navigation name = Evaluate.target "navigation" name
    let textbox name = Evaluate.target "textbox" name
    let combobox name = Evaluate.target "combobox" name
    let text name = Evaluate.target "text" name
    let pattern (role: string) (regex: string) = { Role = role; Name = Pattern(Regex regex); Scope = None }

    let urlIs (address: string) =
        { Kind = Url; Target = None; Expected = Text(Exact address) }

    let shows (target: Target) =
        { Kind = Visible; Target = Some target; Expected = Flag true }

    let textOf (target: Target) (expected: string) =
        { Kind = TextOutcome; Target = Some target; Expected = Text(Exact expected) }

    let opens = { Kind = Open; Target = None; Expected = Flag true }

    let nothing (_: BrowserPage) = ()

    let private nameOf (target: Target) = target.Name.Text

    let private recordOf (scenario: Scenario) (status: string) (evidence: string list) : ActionRecord =
        { Id = scenario.Id
          Role = scenario.Action.Role
          Name = nameOf scenario.Action
          Intent = scenario.Intent
          ExpectedOutcome = scenario.ExpectedOutcome
          Status = status
          Evidence = evidence
          ResetStrategy = scenario.Reset }

    /// Runs one scenario from a fresh load of its start page.
    let run (page: BrowserPage) (origin: string) (scenario: Scenario) : ActionRecord =
        try
            // A fresh document every time: a same-document fragment change
            // would not load, and the scenario's start state must be clean.
            page.Navigate "about:blank"
            page.Navigate(origin + scenario.Path)
            scenario.Prepare page

            let report =
                Observe.runActionAssertion page { RuleId = scenario.RuleId; Action = scenario.Action; Outcome = scenario.Outcome; TimeoutMs = timeoutMs }

            recordOf scenario (Evidence.statusText report.Status) [ $"CDP 1280px: {Evaluate.formatAction report}" ]
        with error ->
            recordOf scenario "unknown" [ $"scenario could not run: {error.Message}" ]

    /// Verifies an external destination by its exact href (not activated).
    let verify (page: BrowserPage) (origin: string) (path: string) (destination: Destination) : ActionRecord =
        let record status evidence : ActionRecord =
            { Id = destination.Id
              Role = destination.Action.Role
              Name = nameOf destination.Action
              Intent = destination.Intent
              ExpectedOutcome = $"navigates to the external destination {destination.Href} (destination verified, external service not exercised)"
              Status = status
              Evidence = evidence
              ResetStrategy = "not activated; no state" }

        try
            page.Navigate(origin + path)

            match Observe.resolve page destination.Action with
            | [ element ] ->
                let href = (Observe.call page element "function() { return this.getAttribute('href'); }" []).GetValue<string>()
                let valid = Uri.TryCreate(href, UriKind.Absolute) |> fst

                if href = destination.Href && valid && href.StartsWith("https://", StringComparison.Ordinal) then
                    record "pass" [ $"ULI-A01 PASS: href is the declared https destination {href}" ]
                else
                    record "fail" [ $"ULI-A01 FAIL: href {href} differs from the declared destination {destination.Href} or is not an absolute https URL" ]
            | found -> record "unknown" [ $"ULI-A01 UNKNOWN: {Evaluate.describeTarget (Some destination.Action)} resolved to {found.Length} elements" ]
        with error ->
            record "unknown" [ $"destination could not be read: {error.Message}" ]

    /// ULI-A10 coverage: every enabled action the page renders is declared.
    let coverage (page: BrowserPage) (origin: string) (path: string) (declared: Target list) : SimpleReport =
        page.Navigate(origin + path)
        let inventory = Observe.inventoryActions page
        let known = declared |> List.map (fun target -> target.Role, target.Name)

        let covered (item: Observe.InventoryItem) =
            known
            |> List.exists (fun (role, name) ->
                role = item.Role
                && match name with
                   | Exact text -> String.Equals(text, Evaluate.normalizeText item.Name, StringComparison.OrdinalIgnoreCase)
                   | Pattern regex -> regex.IsMatch item.Name)

        match inventory |> List.filter (covered >> not) with
        | [] -> { RuleId = "ULI-A10"; Subject = $"action inventory {path}"; Status = Pass; Reason = $"all {inventory.Length} enabled actions have a declared scenario or destination" }
        | missing ->
            let listed = missing |> List.map (fun item -> $"{item.Role} \"{item.Name}\"") |> List.distinct |> String.concat ", "
            { RuleId = "ULI-A10"; Subject = $"action inventory {path}"; Status = Fail; Reason = $"enabled actions without a declared scenario: {listed}" }

    let private line width (report: SimpleReport) = $"{report.RuleId} {report.Status.Text} @{width}px {report.Subject}: {report.Reason}"

    /// The layout sweep at every contract width, plus 200% default text at
    /// 390 and 1280 px (ULI-L05, L06, L09, L02, A01, A06).
    let layout (page: BrowserPage) (origin: string) (path: string) (prepare: BrowserPage -> unit) : string list * (string * Status) list =
        let atWidth width =
            Observe.setViewport page width
            page.Navigate(origin + path)
            prepare page

            [ Observe.measureOverflow page
              LocalChecks.gutter page
              LocalChecks.wordIntegrity page
              LocalChecks.stackedGridLabels page
              LocalChecks.buttonHeightParity page ]
            |> List.map (fun report -> line width report, report.Status)

        let scaled width =
            Observe.setViewport page width
            page.Navigate(origin + path)
            prepare page

            let reports =
                match Observe.scaleDefaultText page 2.0 with
                | Ok() -> [ Observe.measureOverflow page; LocalChecks.gutter page ]
                | Error reason -> [ { RuleId = "ULI-L05"; Subject = "200% text"; Status = Unknown; Reason = reason } ]

            Observe.scaleDefaultText page 1.0 |> ignore
            reports |> List.map (fun report -> (line width report).Replace($"@{width}px", $"@{width}px 200%% text"), report.Status)

        let semantics =
            Observe.setViewport page 1280
            page.Navigate(origin + path)
            prepare page
            [ LocalChecks.inPageDestinations page; LocalChecks.namedFields page ] |> List.map (fun report -> line 1280 report, report.Status)

        let results = (Evaluate.contractWidths |> List.collect atWidth) @ scaled 390 @ scaled 1280 @ semantics
        results |> List.map fst, results |> List.map (fun (text, status) -> text, status)

    /// Measures each declared group at the widths where its peers share a row.
    let groups (page: BrowserPage) (origin: string) (path: string) (declared: DeclaredGroup list) : GroupRecord list =
        declared
        |> List.map (fun item ->
            let reports =
                item.Widths
                |> List.map (fun width ->
                    Observe.setViewport page width
                    page.Navigate(origin + path)
                    width, Observe.measureAlignment page item.Group timeoutMs)

            let statuses = reports |> List.map (fun (_, report) -> report.Status)
            let status = if statuses |> List.contains Fail then "fail" elif statuses |> List.contains Unknown then "unknown" else "pass"

            { Id = item.Group.Id
              Relation =
                match item.Group.Edge with
                | Top -> "top-edge"
                | Bottom -> "bottom-edge"
                | _ -> "centerline"
              ToleranceCssPx = item.Group.ToleranceCssPx
              Targets = item.Group.Peers |> List.map (fun peer -> peer.Role, nameOf peer)
              Rationale = item.Group.Rationale
              Evidence = reports |> List.map (fun (width, report) -> $"@{width}px " + (Evaluate.formatAlignment report).Replace("\n", " | "))
              Status = status })

    let group ruleId id edge rationale widths peers =
        { Group = { RuleId = ruleId; Id = id; Edge = edge; ToleranceCssPx = Evaluate.defaultToleranceCssPx; Peers = peers; Rationale = rationale }
          Widths = widths }
