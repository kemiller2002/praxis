namespace Praxis.UiConformance

/// Owned, bounded deviations (UI-LAYOUT-INTERACTION "EXCEPTION"): each names
/// its rule, the surfaces and the one check it covers, the product reason,
/// the owner and the trigger that ends it. A covered FAIL is reported as
/// `exception` in the evidence -- never as `pass` -- and every other FAIL
/// still fails the run. These are Forma-originated defects: Praxis renders
/// unmodified Forma (SAF-FORMA-1..3) and may not patch it locally.
type Allowance =
    { RuleId: string
      Surfaces: string list
      /// Whether a failing check line is the deviation this allowance owns.
      Covers: string -> bool
      Reason: string
      Owner: string
      ReviewTrigger: string
      Evidence: string list }

[<RequireQualifiedAccess>]
module Exceptions =
    let private forma = "Forma (kemiller2002/forma); Praxis consumes the pinned release vendor/forma/forma.lock"
    let private trigger = "the next Forma release pinned in vendor/forma/forma.lock: re-run ui:conformance and remove this allowance when the check passes"

    /// The "Name=height" pairs of a button-heights line.
    let private heights (line: string) =
        System.Text.RegularExpressions.Regex.Matches(line, @"(?:: |, )([^=,:]+)=(\d+(?:\.\d+)?)")
        |> Seq.map (fun (m: System.Text.RegularExpressions.Match) -> m.Groups[1].Value.Trim(), float m.Groups[2].Value)
        |> List.ofSeq

    /// Every button but the named ones shares one height (within 1 CSS px),
    /// and at least one named button is present: the spread is theirs alone.
    let private onlyTheseDiffer (names: string list) (line: string) =
        let found = heights line
        let others = found |> List.filter (fun (name, _) -> not (List.contains name names)) |> List.map snd

        line.Contains "button heights"
        && found |> List.exists (fun (name, _) -> List.contains name names)
        && not others.IsEmpty
        && List.max others - List.min others <= 1.0

    let allowances: Allowance list =
        [ { RuleId = "ULI-L06"
            Surfaces = [ "web-queue"; "web-execution"; "hub-home" ]
            Covers = fun line -> line.Contains "word integrity in tables"
            Reason =
              "Forma 0.4.1 sets `:where(td, th) { overflow-wrap: anywhere }`, so the auto table layout may squeeze a data-grid column until short label words split across lines (\"Priori|ty\", \"Unregis|ter\") at 768 and 1280 CSS px. Data-grid cell wrapping is Forma's primitive (ULI-L03)."
            Owner = forma
            ReviewTrigger = trigger
            Evidence = [ "word integrity in tables: see the surface evidence lines for the split words per width" ] }
          { RuleId = "ULI-L02"
            Surfaces = [ "hub-home" ]
            Covers = fun line -> line.Contains "Unregister=" && onlyTheseDiffer [ "Unregister"; "Copy link" ] line
            Reason =
              "The same Forma data-grid wrapping splits the Unregister button label inside its table cell, so that button grows taller than the hub's other buttons; every button now uses one Forma geometry (ef-button)."
            Owner = forma
            ReviewTrigger = trigger
            Evidence = [ "button heights: Unregister grows only inside the repositories data grid" ] }
          { RuleId = "ULI-L02"
            Surfaces = [ "web-queue"; "web-item"; "web-validate"; "web-execution"; "hub-home" ]
            Covers = onlyTheseDiffer [ "Copy link" ]
            Reason =
              "Copy link starts `hidden` and the approved url-state.js (DF-ROS-2026-A058) reveals it. Forma 0.4.1's `.ef-site .ef-button { display: inline-flex }` overrides the `hidden` attribute, so giving Copy link the Forma button geometry would show it with JavaScript off (caught by the browser suite). It keeps the base button geometry, a few pixels shorter than the other buttons, until Forma honours `[hidden]` on ef-button."
            Owner = forma
            ReviewTrigger = trigger
            Evidence = [ "button heights: only Copy link differs; every other button shares the ef-button height" ] }
          { RuleId = "ULI-L05"
            Surfaces = [ "web-queue"; "web-execution"; "hub-home" ]
            Covers = fun line -> line.Contains "200% text page"
            Reason =
              "At 200% default text on a 390 px viewport Forma's stacked data grid keeps its label track at `minmax(6.5rem, 0.4fr)` (208 px), leaving status lozenges no room, so they escape the viewport. The stacked data-grid composition is Forma's primitive (ULI-L03/L09)."
            Owner = forma
            ReviewTrigger = trigger
            Evidence = [ "status lozenges in stacked data-grid cells escape at 390 px with 200% text" ] }
          { RuleId = "ULI-L09"
            Surfaces = [ "web-queue"; "web-execution"; "hub-home" ]
            Covers = fun line -> line.Contains "200% text inline gutter"
            Reason = "Consequence of the stacked data-grid label track above: cells pushed past the viewport edge leave no gutter at 390 px with 200% text."
            Owner = forma
            ReviewTrigger = trigger
            Evidence = [ "stacked data-grid cells touch the viewport edge at 390 px with 200% text" ] } ]

    let private covers (surface: SurfaceRecord) (line: string) (allowance: Allowance) =
        List.contains surface.Id allowance.Surfaces && line.StartsWith(allowance.RuleId + " FAIL") && allowance.Covers line

    /// The surface with its covered FAIL lines marked, and the exception
    /// records for the allowances that actually applied.
    let apply (surface: SurfaceRecord) : SurfaceRecord * ExceptionRecord list =
        let covered =
            surface.Checks
            |> List.filter (fun (_, status) -> status = Fail)
            |> List.choose (fun (line, _) -> allowances |> List.tryFind (covers surface line) |> Option.map (fun allowance -> line, allowance))

        let records =
            covered
            |> List.groupBy (snd >> _.RuleId)
            |> List.map (fun (_, items) ->
                let allowance = snd items.Head

                { RuleId = allowance.RuleId
                  Surface = surface.Id
                  Reason = allowance.Reason
                  Owner = allowance.Owner
                  ReviewTrigger = allowance.ReviewTrigger
                  Evidence = allowance.Evidence @ (items |> List.map fst) })

        { surface with Excepted = covered |> List.map fst }, records
