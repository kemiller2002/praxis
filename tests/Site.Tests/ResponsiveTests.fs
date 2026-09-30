/// PRAXIS-SITE-18: layout rules that keep the page reflowing from 320px up.
/// Rendered evidence at 320/375/768/1280/1920 is in docs/site/responsive.md.
module Site.Tests.ResponsiveTests

open System
open System.Globalization
open System.Text.RegularExpressions
open Site.Tests.Fixture

let tests =
    [ test "no fixed width can force a 320px viewport to scroll" (fun () ->
          let css = css ()

          let wide =
              Regex.Matches(css, @"(?<![-\w(])(?:width|min-width):\s*(\d+(?:\.\d+)?)(px|rem)", RegexOptions.ECMAScript)
              |> Seq.filter (fun found ->
                  let value = Double.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture)
                  let px = if found.Groups[2].Value = "rem" then value * 16.0 else value
                  px > 320.0)
              |> Seq.map (fun found -> found.Value)
              |> List.ofSeq

          // The only wide minimum is the ledger table, which scrolls inside its own region.
          Assert.equal [ "min-width: 34rem" ] wide
          Assert.matches @"\.ledger \{\s*overflow-x: auto;" css "ledger scrolls")

      test "the viewport is declared and the layout has intentional breakpoints" (fun () ->
          Assert.matches "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" (html ()) "viewport"

          let breakpoints =
              Regex.Matches(css (), @"@media \((?:min|max)-width: ([\d.]+rem)\)")
              |> Seq.map (fun found -> found.Groups[1].Value)
              |> Set.ofSeq

          for value in [ "30rem"; "48rem"; "60rem"; "72rem" ] do
              Assert.isTrue (breakpoints.Contains value || breakpoints.Contains "47.99rem") value)

      test "navigation needs no script and wraps instead of hiding" (fun () ->
          let css = css ()
          Assert.matches @"\.nav-list \{[^}]*flex-wrap: wrap;" css "wraps"
          Assert.notMatches @"\.nav-list[^{]*\{[^}]*display: none" css "never hidden")

      test "long identifiers wrap instead of overflowing" (fun () ->
          let css = css ()

          for selector in [ @"\.record__rows dd"; @"\.claim__a"; @"\.terminal"; @"\.record__steps li > span:first-child" ] do
              Assert.matches $@"{selector} \{{[^}}]*overflow-wrap: anywhere;" css selector) ]
