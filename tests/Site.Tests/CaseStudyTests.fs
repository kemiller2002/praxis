/// PRAXIS-SITE-23: the case study is generated from the records and makes no
/// claim the records or Git history cannot back.
module Site.Tests.CaseStudyTests

open System
open System.Diagnostics
open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private caseStudy (html: string) =
    let start = html.IndexOf("id=\"case-study\"", StringComparison.Ordinal)
    html.Substring(start, html.IndexOf("id=\"closing\"", StringComparison.Ordinal) - start)

/// `git log -1 --format=%B <sha>`, or None when the commit is not available
/// (a shallow clone, or no git at all).
let private commitMessage (sha: string) =
    try
        let start =
            ProcessStartInfo("git", WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

        [ "log"; "-1"; "--format=%B"; sha ] |> List.iter start.ArgumentList.Add
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEndAsync()
        child.StandardError.ReadToEnd() |> ignore
        child.WaitForExit()
        if child.ExitCode = 0 then Some output.Result else None
    with _ ->
        None

let tests =
    [ test "the ledger is exactly what the renderer produces from the snapshot" (fun () ->
          let html = html ()
          let snapshot = Evidence.readSnapshot root
          Assert.isTrue (Evidence.withValues (Evidence.withLedger html snapshot) snapshot = html) "run praxis-site evidence --render"
          let rows = count @"<tr>\s*<th scope=""row"">" (caseStudy html)
          Assert.equal (Json.get "workItems" snapshot |> Json.items |> List.length) rows)

      test "no row claims completion the snapshot does not record" (fun () ->
          let study = caseStudy (html ())

          for item in Evidence.readSnapshot root |> Json.get "workItems" |> Json.items do
              let id = Json.get "id" item |> Json.toText
              let shown = Regex.Match(study, $"data-evidence=\"{Regex.Escape id}:state\">([a-z]+)<").Groups[1].Value
              Assert.equalMessage (Json.get "state" item |> Json.toText) shown id)

      test "the self-correction story matches Git history" (fun () ->
          match commitMessage "588599e", commitMessage "6d7b300" with
          | Some mislabelled, Some corrected ->
              Assert.matches "^PRAXIS-SITE-15: complete with evidence" mislabelled "588599e"
              Assert.matches @"previous commit \(588599e\) carried this title" corrected "6d7b300"
          | _ -> () // shallow clone: nothing to compare against
      )

      test "deployment is not claimed as a record" (fun () ->
          let html = html ()
          Assert.matches "Deployment is not a Praxis record" (caseStudy html) "case study"
          Assert.notMatches "(?i)deployed successfully|is live|now live" html "page")

      test "the closing states the spec's lines and call to action" (fun () ->
          let html = html ()
          let closing = html.Substring(html.IndexOf("id=\"closing\"", StringComparison.Ordinal))

          Assert.matches
              @"Don&rsquo;t just ship software\. <span class=""closing__turn"">Know how it came to exist\.</span>"
              closing
              "closing lines"

          Assert.matches @"Requirements\. Execution\. Evidence\. Provenance\." closing "closing litany"
          Assert.matches ">Show me the record<" closing "call to action") ]
