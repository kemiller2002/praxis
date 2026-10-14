/// PRAXIS-SITE-13: an execution record on the page is either populated from
/// real Praxis records (every value cites the snapshot) or visibly labelled
/// ILLUSTRATIVE EXECUTION. Examples must never pass for evidence.
module Site.Tests.RecordTests

open System.Text.RegularExpressions
open Site.Tests.Fixture

let private records () =
    Regex.Matches(html (), @"<figure class=""record""[^>]*data-record=""([a-z]+)""[\s\S]*?</figure>")
    |> Seq.map (fun found -> found.Groups[1].Value, found.Value)
    |> List.ofSeq

let private values (markup: string) =
    Regex.Matches(markup, @"<dd\b([^>]*)>([\s\S]*?)</dd>")
    |> Seq.map (fun found -> found.Groups[1].Value, found.Groups[2].Value)
    |> List.ofSeq

let private ofKind (kind: string) =
    records () |> List.filter (fun (found, _) -> found = kind) |> List.map snd

let tests =
    [ test "the page has at least one execution record and every record declares its kind" (fun () ->
          let records = records ()
          Assert.isTrue (records.Length >= 1) "at least one record"
          Assert.equalMessage records.Length (count "<figure class=\"record\"" (html ())) "a record without data-record"

          for kind, _ in records do
              Assert.isTrue (kind = "illustrative" || kind = "real") kind)

      test "illustrative records say so in the header and in the caption" (fun () ->
          for markup in ofKind "illustrative" do
              Assert.matches "class=\"record__label\">Illustrative execution<" markup "header"
              Assert.matches @"<figcaption[^>]*>Illustrative execution\." markup "caption"
              Assert.isFalse (markup.Contains "data-evidence") "illustrative values must not cite evidence")

      test "real records cite the snapshot for every value" (fun () ->
          for markup in ofKind "real" do
              Assert.matches "record__label--real" markup "real label"
              Assert.notMatches "(?i)illustrative" markup "no illustrative wording"

              for attributes, inner in values markup do
                  Assert.isTrue
                      (Regex.IsMatch(attributes, "data-evidence=")
                       || Regex.IsMatch(inner, "data-evidence=")
                       || Regex.IsMatch(attributes, "class=\"none\""))
                      $"uncited value: {inner}")

      test "illustrative identifiers cannot be mistaken for real ones" (fun () ->
          for markup in ofKind "illustrative" do
              for found in Regex.Matches(markup, "EXE-[0-9A-Za-z-]+") do
                  Assert.matches "example" found.Value found.Value) ]
