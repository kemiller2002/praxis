/// The public evidence snapshot must only ever say what the Praxis records say.
module Site.Tests.EvidenceTests

open System
open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private snapshot () = Evidence.readSnapshot root
let private current () = Evidence.readRecords root
let private executions (value: Json) = Json.get "executions" value |> Json.items

let private withExecutions (entries: Json list) (value: Json) =
    Json.set "executions" (JArray entries) value

let private mentions (fragment: string) (problems: string list) =
    problems |> List.exists (fun problem -> problem.Contains(fragment, StringComparison.Ordinal))

let tests =
    [ test "the committed snapshot is consistent with the records" (fun () ->
          Assert.empty (Evidence.snapshotProblems (snapshot ()) (current ())))

      test "every data-evidence value on the page matches the snapshot" (fun () ->
          let html = html ()
          Assert.isTrue (html.Contains "data-evidence=") "page cites evidence"
          Assert.empty (Evidence.htmlProblems (snapshot ()) "index.html" html))

      test "a value that disagrees with the snapshot is caught" (fun () ->
          let snapshot = snapshot ()
          let id = executions snapshot |> List.head |> Json.get "executionId" |> Json.toText
          let forged = $"<dd data-evidence=\"{id}:identity.provider\">openai</dd>"
          Assert.equal 1 (Evidence.htmlProblems snapshot "forged.html" forged).Length

          Assert.equal
              1
              (Evidence.htmlProblems snapshot "missing.html" "<b data-evidence=\"EXE-NOPE:status\">active</b>").Length)

      test "a snapshot that rewrites identity or runs ahead of the record is caught" (fun () ->
          let snapshot = snapshot ()
          let current = current ()
          let first, rest = List.head (executions snapshot), List.tail (executions snapshot)

          let rewrittenFirst =
              first |> Json.set "identity" (Json.get "identity" first |> Option.get |> Json.set "runtime" (JString "chatgpt"))

          let rewritten = snapshot |> withExecutions (rewrittenFirst :: rest)
          Assert.isTrue (Evidence.snapshotProblems rewritten current |> mentions "identity.runtime") "rewritten identity"

          let invented =
              snapshot
              |> withExecutions ((first |> Json.set "executionId" (JString "EXE-20990101T000000000Z-00000000")) :: rest)

          Assert.isTrue (Evidence.snapshotProblems invented current |> mentions "no such execution") "invented execution"

          match current.Executions |> List.tryFind (fun execution -> Json.get "status" execution = Some(JString "active")) with
          | Some active ->
              let ahead = snapshot |> withExecutions [ active |> Json.set "status" (JString "finalized") ]
              Assert.isTrue (Evidence.snapshotProblems ahead current |> mentions "ahead of record") "status ahead of record"
          | None -> ())

      test "unknown model stays unknown, never guessed" (fun () ->
          let snapshot = snapshot ()

          for execution in executions snapshot do
              match Json.get "identity" execution |> Json.prop "model" with
              | Some(JString _) -> ()
              | other -> failwith $"model is not a string: {other}"

          let id = executions snapshot |> List.head |> Json.get "executionId" |> Json.toText

          let recorded =
              (current ()).Executions |> List.head |> Json.get "identity" |> Json.prop "model" |> Json.toText

          Assert.equal (Some recorded) (Evidence.lookup snapshot $"{id}:identity.model"))

      test "the snapshot never carries private identifiers" (fun () ->
          let raw = Json.compact (Some(snapshot ()))

          for field in [ "sessionId"; "conversationId"; "runId"; "dirtyPaths"; "/home/"; "/root/" ] do
              Assert.isFalse (raw.Contains(field: string)) field

          Assert.notMatches "(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}" raw "no UUIDs")

      test "the ledger renderer fills an empty ledger and is idempotent" (fun () ->
          let snapshot = snapshot ()
          let empty = "<tbody>\n            <!-- ledger:start -->\n            <!-- ledger:end -->\n</tbody>"
          let once = Evidence.withLedger empty snapshot
          Assert.equal (Json.get "workItems" snapshot |> Json.items |> List.length) (Regex.Matches(once, "<tr>").Count)
          Assert.equal once (Evidence.withLedger once snapshot))

      test "the snapshot is written in the committed format" (fun () ->
          let snapshot = snapshot ()
          Assert.equal (read "site/data/gh-84.json") (Json.pretty snapshot + "\n")
          Assert.equal (Some(JString Evidence.Note)) (Json.get "note" snapshot)
          Assert.equal (Json.get "asOf" snapshot |> Json.toText) (Evidence.timestamp (DateTimeOffset.Parse(Json.get "asOf" snapshot |> Json.toText)))) ]
