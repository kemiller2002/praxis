/// PRAXIS-SITE-02: the Echelon Foundry tokens and the text/background pairs
/// the stylesheet actually uses must meet WCAG 2.2 AA contrast.
module Site.Tests.DesignTests

open System.Text.RegularExpressions
open Praxis.Site
open Site.Tests.Fixture

let private tokens () =
    Regex.Matches(css (), @"--([a-z-]+):\s*(#[0-9a-f]{6});")
    |> Seq.fold (fun (found: Map<string, string>) m -> found.Add(m.Groups[1].Value, m.Groups[2].Value)) Map.empty

let private token (name: string) =
    match (tokens ()).TryFind name with
    | Some value -> value
    | None -> failwith $"token --{name} is not defined"

let private core =
    [ "parchment", "#f2efe7"
      "charcoal", "#202421"
      "forged-iron", "#3a403c"
      "oxide-bronze", "#905831"
      "verdigris", "#47756b"
      "carbon", "#171a18"
      "graphite", "#686d68"
      "stone", "#e3e0d7" ]

// [foreground, background, minimum ratio]: 4.5 for body text, 3 for
// non-text indicators such as focus outlines.
let private pairs =
    [ "charcoal", "parchment", 4.5
      "charcoal", "stone", 4.5
      "forged-iron", "parchment", 4.5
      "oxide-bronze", "parchment", 4.5
      "verdigris", "parchment", 4.5
      "graphite", "parchment", 4.5
      "parchment", "carbon", 4.5
      "stone", "carbon", 4.5
      "parchment", "charcoal", 4.5
      "parchment", "forged-iron", 4.5
      "carbon", "parchment", 4.5
      "bronze-on-dark", "carbon", 4.5
      "verdigris-on-dark", "carbon", 4.5
      "graphite-on-dark", "carbon", 4.5
      "oxide-bronze", "parchment", 3.0
      "oxide-bronze", "stone", 3.0
      "bronze-on-dark", "carbon", 3.0 ]

/// JavaScript's `${n}` for the ratios above: 3 rather than 3.0.
let private ratioText (ratio: float) =
    ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)

let tests =
    [ test "core Echelon Foundry tokens are defined exactly" (fun () ->
          let tokens = tokens ()

          for name, value in core do
              Assert.equalMessage (Some value) (tokens.TryFind name) name) ]
    @ (pairs
       |> List.map (fun (fg, bg, minimum) ->
           test $"{fg} on {bg} meets {ratioText minimum}:1" (fun () ->
               let ratio = Html.contrast (token fg) (token bg)
               Assert.isTrue (ratio >= minimum) $"{fg} on {bg} is {ratio:F2}:1")))
    @ [ test "geometry stays square: no border-radius other than zero" (fun () ->
            let radii = Regex.Matches(css (), @"border-radius:\s*([^;]+);") |> Seq.map (fun m -> m.Groups[1].Value.Trim()) |> List.ofSeq
            Assert.empty (radii |> List.filter (fun value -> value <> "0")))

        test "no forbidden visual effects" (fun () ->
            let css = css ()

            for effect in [ "linear-gradient"; "radial-gradient"; "backdrop-filter"; "box-shadow" ] do
                Assert.isFalse (css.Contains(effect: string)) effect)

        test "every font family has a local fallback and no font binaries are referenced" (fun () ->
            let css = css ()

            for name in [ "--font-display"; "--font-body"; "--font-mono" ] do
                let stack = Regex.Match(css, $"{name}:([^;]+);").Groups[1].Value
                Assert.matches @"(serif|sans-serif|monospace)\s*\z" stack name

            Assert.notMatches @"\.(woff2?|ttf|otf)\b" css "font binaries referenced from CSS"
            Assert.matches @"fonts\.googleapis\.com/css2\?[^""]*display=swap" (html ()) "font stylesheet uses display=swap")

        test "reduced motion is respected" (fun () ->
            Assert.matches @"@media \(prefers-reduced-motion: reduce\)" (css ()) "reduced motion")

        test "focus is always visible" (fun () -> Assert.matches @":focus-visible\s*\{\s*outline:" (css ()) "focus")

        test "small labels on stone use a colour that meets AA there" (fun () ->
            Assert.matches @"\.section--stone \.kicker \{\s*color: var\(--forged-iron\);" (css ()) "stone kicker"
            Assert.isTrue (Html.contrast (token "forged-iron") (token "stone") >= 4.5) "forged-iron on stone") ]
