/// PRAXIS-SITE-25: regression tests for claims the adversarial audit corrected.
/// Each test pins the honest wording against the code that makes it true.
module Site.Tests.ClaimsTests

open System.Text.RegularExpressions
open Site.Tests.Fixture

let private text () =
    let stripped = Regex.Replace(html (), "<[^>]+>", " ").Replace("&rsquo;", "'")
    Regex.Replace(stripped, @"\s+", " ")

let tests =
    [ test "attribution enforcement is described with its leniency while work is open" (fun () ->
          let text = text ()
          Assert.matches @"not request\.HasActiveOrBlockedWork" (read "src/Praxis.Domain/Work/Attribution.fs") "Attribution.fs"
          Assert.matches "when any work item is active or blocked, unattributed changes are not reported" text "page"
          Assert.notMatches "Never quietly absorbed|not absorbed into the nearest" text "page")

      test "handoff copy matches resume semantics: rejoin if open, new child only after finalize" (fun () ->
          let text = text ()
          Assert.matches "resuming rejoins any execution still open" text "rejoin"
          Assert.matches "Once an execution is finalized, resuming opens a new one" text "new child"
          Assert.notMatches "When work changes hands, the next actor opens a new execution" text "old wording")

      test "the GH-84 story discloses that the same session resumed" (fun () ->
          let text = text ()
          Assert.matches "the same Claude Code session resumed the work" text "same session"
          Assert.matches "Both executions carry the same runtime session identifier" text "same identifier")

      test "token and cost totals are reported from the total metrics, not their components" (fun () ->
          let evidence = read "site-tools/Evidence.fs"
          Assert.matches @"metricStatus record ""tokens\."" ""tokens\.total""" evidence "tokens"
          Assert.matches @"metricStatus record ""cost\."" ""cost\.execution_total""" evidence "cost")

      test "test evidence is described as a named file, not proof that tests passed" (fun () ->
          let text = text ()
          Assert.matches "completion requires naming an existing test file as evidence" text "named file"
          Assert.notMatches "A work item without it stays open" text "old wording")

      test "event attribution is not claimed for legacy events" (fun () ->
          let text = text ()
          Assert.notMatches "Each event carries the actor" text "old wording"
          Assert.matches "Events written by current versions carry the actor" text "current versions") ]
