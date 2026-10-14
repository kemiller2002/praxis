/// PRAXIS-SITE-20: security and privacy properties of the published page.
module Site.Tests.SecurityTests

open System
open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private csp (html: string) =
    let found = Regex.Match(html, @"<meta http-equiv=""Content-Security-Policy"" content=""([^""]+)"">")
    if found.Success then Some found.Groups[1].Value else None

let private directives (policy: string) =
    policy.Split(';')
    |> Array.map (fun part -> Regex.Split(part.Trim(), @"\s+") |> List.ofArray)
    |> Array.choose (fun parts ->
        match parts with
        | name :: values -> Some(name, values)
        | [] -> None)
    |> Map.ofArray

let tests =
    [ test "a strict content security policy is declared" (fun () ->
          match csp (html ()) with
          | None -> failwith "CSP meta present"
          | Some policy ->
              let directives = directives policy
              Assert.equal (Some [ "'none'" ]) (directives.TryFind "default-src")
              Assert.equal (Some [ "'none'" ]) (directives.TryFind "script-src")
              Assert.equal (Some [ "'none'" ]) (directives.TryFind "base-uri")
              Assert.equal (Some [ "'none'" ]) (directives.TryFind "form-action")

              Assert.isFalse
                  (policy.Contains("'unsafe-inline'") || policy.Contains("'unsafe-eval'") || policy.Contains("'unsafe-hashes'") || policy.Contains("*"))
                  policy

              Assert.notMatches "'sha(256|384|512)-" policy "no script hashes: nothing is allowed to run"
              Assert.equal (Some [ "'self'"; "https://fonts.googleapis.com" ]) (directives.TryFind "style-src")
              Assert.equal (Some [ "https://fonts.gstatic.com" ]) (directives.TryFind "font-src"))

      test "there are no inline event handlers and no script blocks" (fun () ->
          let html = html ()
          let handlers = Regex.Matches(html, @"\son[a-z]+=""([^""]*)""") |> Seq.map (fun found -> found.Groups[1].Value) |> List.ofSeq
          Assert.empty handlers
          Assert.notMatches @"<script\b" html "no script elements, inline or external")

      test "external links are https to known hosts and never open new windows" (fun () ->
          let html = html ()
          let hosts = set [ "github.com"; "raw.githubusercontent.com"; "fonts.googleapis.com"; "fonts.gstatic.com" ]

          for found in Regex.Matches(html, @"\b(?:href|src)=""(https?:[^""]+)""") do
              let url = found.Groups[1].Value
              let parsed = Uri(url.Replace("&amp;", "&"))
              Assert.equalMessage "https" parsed.Scheme url
              Assert.isTrue (hosts.Contains parsed.Authority) parsed.Authority

          Assert.notMatches "target=\"_blank\"" html "no new windows"
          Assert.matches "<meta name=\"referrer\" content=\"strict-origin-when-cross-origin\">" html "referrer policy")

      test "nothing private crosses the public boundary" (fun () ->
          let html = html ()
          Assert.empty (Site.check siteRoot)

          for marker in [ "127.0.0.1"; "localhost"; "4310"; "4320"; "/api/"; "sessionId"; "session_" ] do
              Assert.isFalse (html.Contains(marker: string)) marker) ]
