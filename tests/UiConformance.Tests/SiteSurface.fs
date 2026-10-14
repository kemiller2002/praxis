namespace Praxis.UiConformance

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open Praxis.Tests

/// The public site (site/, deployed to GitHub Pages by deploy-pages.yml),
/// served by its own preview server (`SiteTools serve`).
[<RequireQualifiedAccess>]
module SiteSurface =
    open Scenario

    let private repository =
        let rec up (directory: DirectoryInfo) =
            if isNull directory then failwith "the Praxis repository root (Praxis.slnx) was not found above the test binary"
            elif File.Exists(Path.Combine(directory.FullName, "Praxis.slnx")) then directory.FullName
            else up directory.Parent

        lazy (up (DirectoryInfo AppContext.BaseDirectory))

    /// Starts `SiteTools serve` on a free loopback port; the returned
    /// disposable stops it.
    let private serve () =
        let root = repository.Force()

        let tool =
            [ "Release"; "Debug" ]
            |> List.map (fun configuration -> Path.Combine(root, "site-tools", "bin", configuration, "net10.0", "praxis-site.dll"))
            |> List.tryFind File.Exists
            |> Option.defaultWith (fun () -> failwith "site-tools is not built: run dotnet build Praxis.slnx --configuration Release")

        let launch () =
            let probe = new TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            let port = (probe.LocalEndpoint :?> IPEndPoint).Port
            probe.Stop()
            let start = ProcessStartInfo("dotnet", WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)
            [ tool; "serve"; "--port"; string port ] |> List.iter start.ArgumentList.Add
            let child = Process.Start start
            let errors = Text.StringBuilder()
            child.ErrorDataReceived.Add(fun line -> if not (isNull line.Data) then lock errors (fun () -> errors.AppendLine line.Data |> ignore))
            child.BeginOutputReadLine()
            child.BeginErrorReadLine()
            let origin = $"http://127.0.0.1:{port}"
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds 5.0)
            let deadline = DateTime.UtcNow.AddSeconds 60.0

            // Startup polling of a child process (not a UI wait).
            let rec wait () =
                let ready = try client.GetAsync(origin + "/").Result.IsSuccessStatusCode with _ -> false

                if ready then Ok()
                elif child.HasExited || DateTime.UtcNow > deadline then Error $"exited: {child.HasExited}: {errors}"
                else
                    Threading.Thread.Sleep 100
                    wait ()

            match wait () with
            | Ok() -> Ok(origin, child)
            | Error reason ->
                if not child.HasExited then child.Kill true
                child.Dispose()
                Error reason

        // The preview server occasionally exits during startup on a busy
        // machine; like ServedProcess, retry a bounded number of launches.
        let rec attempt remaining =
            match launch (), remaining with
            | Ok started, _ -> started
            | Error _, remaining when remaining > 1 -> attempt (remaining - 1)
            | Error reason, _ -> failwith $"the site preview server did not start ({reason})"

        let origin, child = attempt 3

        origin,
        { new IDisposable with
            member _.Dispose() =
                if not child.HasExited then
                    child.Kill true
                    child.WaitForExit 10000 |> ignore

                child.Dispose() }

    /// In-page links: (scenario id, accessible name, optional scope, #fragment).
    let private inPage =
        [ "site-brand", "Praxis, top of page", None, "top"
          "site-nav-proposition", "What is Praxis", Some(navigation "Primary"), "proposition"
          "site-nav-how", "How it works", Some(navigation "Primary"), "how"
          "site-nav-evidence", "Evidence", Some(navigation "Primary"), "evidence"
          "site-nav-agents", "Agents", Some(navigation "Primary"), "agents"
          "site-nav-get-started", "Get started", Some(navigation "Primary"), "get-started"
          "site-hero-evidence", "See the evidence", None, "evidence"
          "site-verify", "See a claim interrogated", None, "verify"
          "site-reconciliation", "How reconciliation works", None, "reconciliation"
          "site-identity", "How identity is recorded", None, "agents"
          "site-handoff", "A real handoff, as recorded", None, "evidence"
          "site-resilience", "What survives, and what is still direction", None, "resilience"
          "site-closing-record", "Show me the record", None, "evidence"
          "site-closing-get-started", "Get started", Some(pattern "region" "^Don.t just ship software"), "get-started" ]

    let private external =
        let github = "https://github.com/kemiller2002/praxis"

        [ "site-nav-github", "GitHub", Some(navigation "Primary"), github
          "site-hero-github", "View on GitHub", None, github
          "site-issue-84", "https://github.com/kemiller2002/praxis/issues/84", None, github + "/issues/84"
          "site-install-sh", "install-native.sh", None, github + "/blob/main/scripts/install-native.sh"
          "site-install-ps1", "install-native.ps1", None, github + "/blob/main/scripts/install-native.ps1"
          "site-release-bundle", "release bundle", None, github + "/releases"
          "site-work-protocol", "work protocol", None, github + "/blob/main/docs/work-protocol.md"
          "site-cli-reference", "CLI reference", None, github + "/blob/main/docs/cli.md"
          "site-native-installation", "native installation", None, github + "/blob/main/docs/native-installation.md"
          "site-issue-84-text", "issue #84", None, github + "/issues/84"
          "site-actions-run", "GitHub Actions run", None, github + "/actions"
          "site-footer-source", "Source on GitHub", Some(navigation "Project"), github
          "site-footer-releases", "Releases", Some(navigation "Project"), github + "/releases"
          "site-footer-issues", "Issues", Some(navigation "Project"), github + "/issues"
          "site-footer-license", "MIT License", Some(navigation "Project"), github + "/blob/main/LICENSE"
          "site-footer-page-source", "Source for this page", Some(navigation "Project"), github + "/tree/main/site" ]

    let private scoped (scope: Target option) (target: Target) =
        match scope with
        | Some region -> Evaluate.within region target
        | None -> target

    let private scenarios (origin: string) =
        let anchors =
            inPage
            |> List.map (fun (id, name, scope, fragment) ->
                { Id = id
                  Path = "/"
                  Prepare = Observe.reduceMotion
                  RuleId = "ULI-A02"
                  Action = scoped scope (link name)
                  Outcome = urlIs (origin + "/#" + fragment)
                  Intent = $"jump to the #{fragment} section"
                  ExpectedOutcome = $"the URL targets #{fragment} (the destination exists: in-page destination check)"
                  Reset = "reload the page" })

        let data =
            [ "site-snapshot", "snapshot"; "site-snapshot-text", "the public evidence snapshot" ]
            |> List.map (fun (id, name) ->
                { Id = id
                  Path = "/"
                  Prepare = nothing
                  RuleId = "ULI-A02"
                  Action = link name
                  Outcome = urlIs (origin + "/data/gh-84.json")
                  Intent = "open the public evidence snapshot the page cites"
                  ExpectedOutcome = "the same-origin JSON snapshot loads"
                  Reset = "navigate back" })

        let skip =
            { Id = "site-skip"
              Path = "/"
              Prepare = nothing
              RuleId = "ULI-A02"
              Action = link "Skip to content"
              Outcome = urlIs (origin + "/#main")
              Intent = "skip to the main content"
              ExpectedOutcome = "the URL targets #main"
              Reset = "reload the page" }

        skip :: anchors @ data

    let private destinations =
        external
        |> List.map (fun (id, name, scope, href) ->
            { Id = id
              Action = scoped scope (link name)
              Href = href
              Intent = $"open {href}" })

    let private groups =
        [ group "ULI-L10" "hero-actions" Top "the hero's two calls to action share a row where they fit side by side" [ 768; 1280 ] [ link "See the evidence"; link "View on GitHub" ]
          group "ULI-L10" "closing-actions" Top "the closing calls to action share a row where they fit side by side" [ 768; 1280 ] [ link "Show me the record"; Evaluate.within (pattern "region" "^Don.t just ship software") (link "Get started") ]
          group
              "ULI-L10"
              "primary-navigation"
              Center
              "the primary navigation is one row at desktop width"
              [ 1280 ]
              ([ "What is Praxis"; "How it works"; "Evidence"; "Agents"; "Get started"; "GitHub" ] |> List.map (link >> Evaluate.within (navigation "Primary"))) ]

    let run (browser: Browser) : SurfaceRecord list =
        let origin, server = serve ()

        try
            let page = browser.NewPage true
            let evidence, checks = layout page origin "/" nothing
            let measured = Scenario.groups page origin "/" groups
            Observe.setViewport page 1280
            let declared = (scenarios origin |> List.map _.Action) @ (destinations |> List.map _.Action)
            let coverageReport = coverage page origin "/" declared
            let actions = (scenarios origin |> List.map (Scenario.run page origin)) @ (destinations |> List.map (verify page origin "/"))

            [ { Id = "site-home"
                Path = "/"
                Kind = "marketing"
                ViewportWidths = Evaluate.contractWidths
                Groups = measured
                Actions = actions
                Evidence = evidence @ [ $"{coverageReport.RuleId} {coverageReport.Status.Text} {coverageReport.Subject}: {coverageReport.Reason}" ]
                Checks = checks @ [ "coverage", coverageReport.Status ]
                Excepted = []
                UnknownScope =
                  [ "right-to-left and translated text: English only; not exercised"
                    "external destinations: verified by exact https href; the external services were not exercised" ] } ]
        finally
            server.Dispose()
