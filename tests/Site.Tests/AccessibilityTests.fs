/// PRAXIS-SITE-17: accessibility rules that can be decided from the source.
/// Rendered-page evidence (axe-core, keyboard traversal, text spacing, reflow)
/// is recorded in docs/site/accessibility.md.
module Site.Tests.AccessibilityTests

open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let tests =
    [ test "the markup-level checks pass" (fun () -> Assert.empty (Html.accessibilityProblems (html ())))

      test "scrollable regions can be reached and scrolled from the keyboard" (fun () ->
          let html = html ()

          let scrollers =
              Regex.Matches(css (), @"\.([a-z_-]+)\s*\{[^}]*overflow-x:\s*auto")
              |> Seq.map (fun found -> found.Groups[1].Value)
              |> List.ofSeq

          Assert.isTrue (scrollers.Length > 0) "a scrollable region exists"

          for name in scrollers do
              let element = Regex.Match(html, $"<[a-z]+ class=\"{name}\"[^>]*>")
              Assert.isTrue element.Success name
              Assert.matches "tabindex=\"0\"" element.Value $"{name} is focusable"
              Assert.matches "role=\"region\"" element.Value $"{name} is a named region"
              Assert.matches "aria-labelledby=\"[^\"]+\"" element.Value $"{name} has a name")

      test "aria-label is only used where the role allows it" (fun () ->
          for found in Regex.Matches(html (), @"<(pre|div|span|p)\b[^>]*aria-label=") do
              failwith $"aria-label on a generic {found.Value}")

      test "hover never reveals anything focus does not" (fun () ->
          for found in Regex.Matches(css (), @"([^{}]*:hover[^{]*)\{([^}]*)\}") do
              let body = found.Groups[2].Value
              Assert.notMatches "display|visibility|opacity|height" body $"hover-only change: {body.Trim()}")

      test "interactive controls meet the 44px target in the main flows" (fun () ->
          let css = css ()

          for selector in [ ".button"; ".nav-list a"; ".principles__link a"; ".site-footer__nav a" ] do
              let rule = Regex.Match(css, selector.Replace(".", @"\.") + @"\s*\{([^}]*)\}")
              Assert.isTrue rule.Success selector
              Assert.matches @"min-height:\s*(44|48)px" rule.Groups[1].Value selector)

      test "reduced motion removes motion rather than shortening it" (fun () ->
          let block = Regex.Match(css (), @"@media \(prefers-reduced-motion: reduce\) \{([\s\S]*?)\n\}").Groups[1].Value
          Assert.matches "animation: none !important" block "animation"
          Assert.matches "transition: none !important" block "transition"
          Assert.matches "scroll-behavior: auto" block "scroll-behavior")

      test "the page ships no script, so nothing it needs depends on one" (fun () ->
          let html = html ()
          Assert.notMatches "<script" html "no script element"
          Assert.notMatches @"\son[a-z]+=" html "no inline event handler"
          Assert.isFalse (exists "site/assets/js") "no site/assets/js directory") ]
