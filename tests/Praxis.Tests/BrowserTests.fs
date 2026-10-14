namespace Praxis.Tests

open System

/// The approved `url-state.js` enhancement in a real browser (DF-ROS-2026-A058,
/// SAF-URL-3 and SAF-URL-10), and the script-free fallback with JavaScript off.
/// Each test starts a real `praxis web serve` (or `hub serve`) and drives
/// headless Chrome with real mouse input. Run with `--suite browser`.
[<RequireQualifiedAccess>]
module BrowserTests =
    let private withWeb (test: ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-browser" None
        CliHarness.optOutOfDurableCheckpoints root
        CliHarness.commitAll root "browser suite fixture"

        try
            use server = new ServedProcess(root, [ "web"; "serve" ])
            test server
        finally
            CliHarness.removeDirectory root

    let private withHub (test: ServedProcess -> unit) =
        let root = CliHarness.initializedRepository "ros-browser-hub" (Some "project-administration")

        try
            use server = new ServedProcess(root, [ "hub"; "serve" ])
            test server
        finally
            CliHarness.removeDirectory root

    let private origin (server: ServedProcess) = server.Client.BaseAddress.ToString().TrimEnd('/')

    /// Fills the filter form through the DevTools DOM (so it works with page
    /// script off) and submits it with a real click on its button.
    let private refine (page: BrowserPage) (tag: string) (status: string) =
        page.SetAttribute("form[data-refine] input[name=tag]", "value", tag)
        page.SetAttribute($"form[data-refine] select[name=status] option[value=\"{status}\"]", "selected", "")
        page.AndWaitForLoad(fun () -> page.Click "form[data-refine] button[type=submit]")

    let private current (page: BrowserPage) =
        let index, entries = page.History()
        entries[index], entries.Length

    let private noProblems (page: BrowserPage) =
        Assert.equal [] (page.Problems())

    let tests =
        [ { Name = "browser: with the enhancement, a filter refinement replaces the history entry and lands on the canonical URL (SAF-URL-3)"
            Run =
              fun () ->
                  withWeb (fun server ->
                      use browser = Browser.Launch()
                      let page = browser.NewPage true
                      page.Navigate(origin server + "/")
                      let _, before = current page
                      refine page "b, a" "ready"
                      Assert.equal (origin server + "/?tag=a,b&status=ready", before) (current page)
                      refine page "a" "active"
                      Assert.equal (origin server + "/?tag=a&status=active", before) (current page)
                      noProblems page) }

          { Name = "browser: with the enhancement, a hub filter refinement replaces the history entry too"
            Run =
              fun () ->
                  withHub (fun server ->
                      use browser = Browser.Launch()
                      let page = browser.NewPage true
                      page.Navigate(origin server + "/")
                      let _, before = current page
                      refine page "x" "blocked"
                      Assert.equal (origin server + "/?tag=x&status=blocked", before) (current page)
                      noProblems page) }

          { Name = "browser: with the enhancement, Copy link puts the view's absolute URL on the clipboard in one action (SAF-URL-10)"
            Run =
              fun () ->
                  withWeb (fun server ->
                      use browser = Browser.Launch()
                      browser.GrantClipboard(origin server)
                      let page = browser.NewPage true
                      page.Navigate(origin server + "/?status=ready")
                      Assert.isTrue (page.IsRendered "button[data-copy]") "the script reveals the Copy link button"
                      page.Click "button[data-copy]"
                      let deadline = DateTime.UtcNow.AddSeconds 10.0

                      while not ((page.OuterHtml "#share-status").Contains "Link copied.") && DateTime.UtcNow < deadline do
                          Threading.Thread.Sleep 50

                      Http.contains "Link copied." (page.OuterHtml "#share-status")
                      Assert.equal (origin server + "/?status=ready") (page.Evaluate("navigator.clipboard.readText()").GetValue<string>())
                      noProblems page) }

          { Name = "browser: with JavaScript off, the filter form still works and pushes history, and the address can be selected by hand"
            Run =
              fun () ->
                  withWeb (fun server ->
                      use browser = Browser.Launch()
                      let page = browser.NewPage false
                      page.Navigate(origin server + "/")
                      let _, before = current page
                      refine page "b, a" "ready"
                      Assert.equal (origin server + "/?tag=a,b&status=ready", before + 1) (current page)
                      Assert.isTrue (not (page.IsRendered "button[data-copy]")) "without script the Copy link button stays hidden"
                      Assert.isTrue (page.IsRendered "#share-url") "the address field is shown"
                      Assert.equal (Some(origin server + "/?tag=a,b&status=ready")) (page.Attributes "#share-url" |> List.tryFind (fst >> (=) "value") |> Option.map snd)
                      Assert.isTrue (page.Attributes "#share-url" |> List.exists (fst >> (=) "readonly")) "the field is read-only and selectable") } ]
