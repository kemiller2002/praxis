namespace Praxis.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Praxis.Application.Web

/// The limits of the one approved browser script (DF-ROS-2026-A058): what it
/// may touch, how it is pinned, and how the servers deliver it. These tests
/// fail if `url-state.js` grows a capability the decision did not approve.
[<RequireQualifiedAccess>]
module UrlEnhancementTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, ".echelon")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    /// The script's code with comments and string literals removed, so only
    /// what it executes is audited.
    let private code (source: string) =
        let comments = Regex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)
        let strings = Regex("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`")
        strings.Replace(comments.Replace(source, " "), "\"\"")

    let private identifiers (source: string) =
        Regex.Matches(code source, @"[A-Za-z_$][A-Za-z0-9_$]*") |> Seq.map _.Value |> Set.ofSeq

    /// Every name the approved script may use: the DOM queries and events of
    /// the two behaviours, `location.replace`, and `navigator.clipboard.writeText`.
    /// A new name (a new API, a new capability) fails the test until the
    /// decision is amended and this list with it.
    let private approved =
        set
            [ "addEventListener"; "append"; "button"; "clipboard"; "document"; "event"; "field"; "forEach"; "form"; "FormData"
              "function"; "getAttribute"; "getElementById"; "hidden"; "if"; "location"; "name"; "navigator"; "new"
              "preventDefault"; "query"; "querySelectorAll"; "replace"; "return"; "select"; "status"; "text"; "textContent"
              "then"; "toString"; "trim"; "typeof"; "URLSearchParams"; "value"; "var"; "writeText"; "false" ]

    /// Capabilities the decision rules out, named so a failure says why.
    let private forbidden =
        [ "fetch"; "XMLHttpRequest"; "WebSocket"; "EventSource"; "sendBeacon"; "localStorage"; "sessionStorage"; "indexedDB"
          "caches"; "cookie"; "eval"; "Function"; "setTimeout"; "setInterval"; "import"; "postMessage"; "Worker"
          "serviceWorker"; "innerHTML"; "outerHTML"; "insertAdjacentHTML"; "write"; "open"; "pushState"; "readText" ]

    let tests =
        [ { Name = "url enhancement: url-state.js uses only the approved names, and none of the forbidden capabilities (DF-ROS-2026-A058)"
            Run =
              fun () ->
                  let used = identifiers UrlEnhancement.source
                  Assert.equal Set.empty (Set.difference used approved)
                  Assert.equal [] (forbidden |> List.filter used.Contains)
                  Assert.isTrue (UrlEnhancement.source.Contains "\"use strict\";") "the script runs in strict mode" }

          { Name = "url enhancement: the audit catches a script that grows a capability"
            Run =
              fun () ->
                  for grown in [ "fetch(\"/api/work\");"; "localStorage.setItem(\"a\", \"b\");"; "eval(\"1\");"; "document.cookie = \"\";"; "new XMLHttpRequest();" ] do
                      let used = identifiers (UrlEnhancement.source + "\n" + grown)
                      Assert.isTrue (not (Set.difference used approved).IsEmpty) $"'{grown}' must fail the audit"

                  Assert.equal Set.empty (Set.difference (identifiers "// fetch eval\nvar text = \"localStorage\";") approved) }

          { Name = "url enhancement: url-state.js stays small: at most 40 lines"
            Run =
              fun () ->
                  let lines = UrlEnhancement.source.TrimEnd().Split('\n').Length
                  Assert.isTrue (lines <= 40) $"url-state.js has {lines} lines; the approved exception is a few dozen" }

          { Name = "url enhancement: the page pins the script by its SHA-256 integrity, and the CSP admits no inline script, eval or network connection"
            Run =
              fun () ->
                  Assert.equal ("sha256-" + Convert.ToBase64String(SHA256.HashData UrlEnhancement.bytes)) UrlEnhancement.integrity
                  Assert.equal $"<script src=\"/url-state.js\" integrity=\"{UrlEnhancement.integrity}\" defer></script>" UrlEnhancement.scriptTag
                  let policy = UrlEnhancement.contentSecurityPolicy
                  for missing in [ "unsafe-inline"; "unsafe-eval"; "unsafe-hashes"; "wasm-unsafe-eval"; "*" ] do
                      Assert.isTrue (not (policy.Contains missing)) $"the CSP must not grant {missing}: {policy}"
                  for directive in [ "script-src 'self'"; "connect-src 'none'"; "object-src 'none'"; "base-uri 'none'"; "require-trusted-types-for 'script'" ] do
                      Http.contains directive policy }

          { Name = "url enhancement: ros.json excepts exactly this one script, citing the accepted decision"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
                  let ros = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ros.json")))
                  let scripts =
                      (ros["implementationPolicy"]["exceptions"]).AsArray()
                      |> Seq.map (fun entry -> entry["path"].GetValue<string>(), entry["decision"].GetValue<string>())
                      |> Seq.filter (fun (path, _) -> path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/", StringComparison.Ordinal) && path.StartsWith("src/", StringComparison.Ordinal))
                      |> List.ofSeq
                  Assert.equal [ "src/Praxis.Application/Web/url-state.js", "DF-ROS-2026-A058" ] scripts
                  let decision = Directory.GetFiles(Path.Combine(root, "research", "decisions"), "DF-ROS-2026-A058--*.md") |> Array.exactlyOne |> File.ReadAllText
                  Http.contains "status: accepted" decision }

          { Name = "web serve: pages carry exactly the one pinned script and the CSP; the script is served with its pinned bytes; the Copy link button starts hidden"
            Run =
              fun () ->
                  let root = CliHarness.initializedRepository "ros-enhancement" None
                  CliHarness.optOutOfDurableCheckpoints root
                  CliHarness.commitAll root "enhancement fixture"

                  try
                      use server = new ServedProcess(root, [ "web"; "serve" ])
                      let page = server.Get "/"
                      let html = Http.body page
                      Assert.equal [ UrlEnhancement.scriptTag ] (Regex.Matches(html, @"<script\b[^>]*>.*?</script>", RegexOptions.Singleline) |> Seq.map _.Value |> List.ofSeq)
                      Assert.equal UrlEnhancement.contentSecurityPolicy (page.Headers.GetValues "Content-Security-Policy" |> Seq.exactlyOne)
                      Http.contains "<form method=\"get\" action=\"/\" data-refine>" html
                      Http.contains "<button type=\"button\" hidden data-copy=\"share-url\"" html
                      let script = server.Get UrlEnhancement.Path
                      Assert.equal 200 (Http.status script)
                      Http.contains "text/javascript" (string script.Content.Headers.ContentType)
                      Assert.equal UrlEnhancement.bytes (script.Content.ReadAsByteArrayAsync().Result)
                      let refused = server.Get "/nothing-here"
                      Assert.isTrue (not ((Http.body refused).Contains "<script")) "a refusal page carries no script"
                      Assert.equal UrlEnhancement.contentSecurityPolicy (refused.Headers.GetValues "Content-Security-Policy" |> Seq.exactlyOne)
                  finally
                      CliHarness.removeDirectory root } ]
