/// Public site checks (docs/public-site.md, GH-84). Dependency-free.
module Site.Tests.SiteTests

open System.IO
open Praxis.Site
open Site.Tests.Fixture

let tests =
    [ test "PRAXIS-SITE-01: the committed public site passes every site check" (fun () -> Assert.empty (Site.check siteRoot))

      test "PRAXIS-SITE-01: the preview server never serves outside site/" (fun () ->
          Assert.equal (Some(Path.Combine(siteRoot, "index.html"))) (Serve.resolveRequest siteRoot "/")
          Assert.equal (Some(Path.Combine(siteRoot, "assets", "css", "site.css"))) (Serve.resolveRequest siteRoot "/assets/css/site.css")
          Assert.equal (Some(Path.Combine(siteRoot, "release.json"))) (Serve.resolveRequest siteRoot "/../release.json")
          // URL parsing already collapses dot segments; encoded slashes are the
          // remaining escape route, and the server refuses them.
          match Serve.resolveRequest siteRoot "/%2e%2e/%2e%2e/etc/passwd" with
          | Some file -> Assert.isTrue (file.StartsWith(siteRoot + string Path.DirectorySeparatorChar)) file
          | None -> failwith "dot segments are collapsed, not refused"

          Assert.equal None (Serve.resolveRequest siteRoot "/..%2f..%2fetc%2fpasswd"))

      test "PRAXIS-SITE-01: the preview server labels what it serves" (fun () ->
          Assert.equal "text/html; charset=utf-8" (Serve.contentType "index.html")
          Assert.equal "text/css; charset=utf-8" (Serve.contentType "assets/css/site.css")
          Assert.equal "application/json; charset=utf-8" (Serve.contentType "data/gh-84.json")
          Assert.equal "application/octet-stream" (Serve.contentType ".nojekyll"))

      test
          "PRAXIS-SITE-01: the boundary check rejects loopback hosts, operational API paths, local paths, session ids and credentials"
          (fun () ->
              Assert.empty (Html.boundaryProblems "x" "see https://example.com/praxis")

              for text in
                  [ "fetch('http://127.0.0.1:4310/state')"
                    "http://localhost:4310"
                    "fetch('/api/work')"
                    "path /home/user/praxis"
                    "\"sessionId\": \"abc\""
                    "670d8549-5656-53b1-9d67-a781eee8c6f3"
                    "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345" ] do
                  Assert.isFalse (Html.boundaryProblems "x" text).IsEmpty text)

      test "PRAXIS-SITE-01: references must be relative and resolve" (fun () ->
          let exists file = file = "assets/css/site.css"

          Assert.empty (
              Html.referenceProblems
                  "<link href=\"assets/css/site.css\"><a href=\"https://github.com/x\">x</a><a href=\"#main\">m</a>"
                  exists
          )

          Assert.equal 1 (Html.referenceProblems "<link href=\"/assets/css/site.css\">" exists).Length
          Assert.equal 1 (Html.referenceProblems "<img src=\"missing.png\" alt=\"\">" exists).Length)

      test "PRAXIS-SITE-17: structural and accessibility rules catch common defects" (fun () ->
          Assert.isFalse (Html.structureProblems "<main><section></main>").IsEmpty "unclosed section"
          Assert.empty (Html.structureProblems "<main><p>ok<br></p><img src='a' alt=''></main>")

          let page body =
              $"<!doctype html><html lang=\"en\"><head><title>t</title></head><body><a href=\"#main\">Skip</a><header><nav><a href=\"#main\">x</a></nav></header><main id=\"main\">{body}</main><footer></footer></body></html>"

          let problems body = String.concat "," (Html.accessibilityProblems (page body))
          Assert.empty (Html.accessibilityProblems (page "<h1>A</h1><h2>B</h2>"))
          Assert.matches "jumps" (problems "<h1>A</h1><h3>B</h3>") "heading jump"
          Assert.matches "not meaningful" (problems "<h1>A</h1><a href='x'>click here</a>") "link text"
          Assert.matches "accessible name" (problems "<h1>A</h1><button></button>") "button name"
          Assert.matches "no target" (problems "<h1>A</h1><a href='#nowhere'>x</a>") "in-page target") ]
