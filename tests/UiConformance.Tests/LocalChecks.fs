namespace Praxis.UiConformance

open System.Text.Json.Nodes
open Praxis.Tests

/// A check that finds the phenomenon absent reports UNKNOWN with a reason
/// starting "not applicable:"; the evidence records it as not-applicable (a
/// documented absence), never as pass.
///
/// Praxis-local geometry observations that the forma-conformance helpers do
/// not (yet) provide. Each cites the ULI rule it evidences and reports
/// PASS / FAIL / UNKNOWN the same way: no observation is UNKNOWN, never PASS.
/// Candidates for upstreaming to forma-conformance, not a fork of it.
[<RequireQualifiedAccess>]
module LocalChecks =
    let private report ruleId subject status reason : SimpleReport =
        { RuleId = ruleId; Subject = subject; Status = status; Reason = reason }

    let private strings (node: JsonNode) =
        match node with
        | :? JsonArray as items -> items |> Seq.map (fun item -> item.GetValue<string>()) |> List.ofSeq
        | _ -> []

    /// ULI-L02/L07: equivalent controls (every rendered button and Forma
    /// button-styled link on the page) share one intrinsic height.
    let buttonHeightParity (page: BrowserPage) =
        Observe.settle page |> ignore

        let observed =
            page.Evaluate
                "[...document.querySelectorAll('main button, main .ef-button, main .button, main input[type=submit]')].filter(e => e.checkVisibility()).map(e => `${(e.getAttribute('aria-label') || e.textContent).replace(/\\s+/g, ' ').trim()}=${e.getBoundingClientRect().height.toFixed(1)}`)"
            |> strings

        let heights = observed |> List.map (fun item -> item.Substring(item.LastIndexOf '=' + 1) |> float)

        match heights with
        | []
        | [ _ ] -> report "ULI-L02" "button heights" Unknown "not applicable: fewer than two rendered buttons to compare"
        | _ ->
            let delta = List.max heights - List.min heights
            let status = if delta <= 1.0 then Pass else Fail
            let listed = String.concat ", " observed
            report "ULI-L02" "button heights" status $"height spread {delta:F1} CSS px across {heights.Length} buttons (tolerance 1): {listed}"

    /// ULI-L05/L09: content keeps an inline gutter -- no text or control
    /// touches either viewport edge.
    let gutter (page: BrowserPage) =
        Observe.settle page |> ignore

        let touching =
            page.Evaluate
                "(() => { const w = document.documentElement.clientWidth; const scrolled = e => { for (let n = e.parentElement; n && n !== document.body; n = n.parentElement) { const o = getComputedStyle(n).overflowX; if (o === 'auto' || o === 'scroll') return true; } return false; }; return [...document.querySelectorAll('body *')].filter(e => e.checkVisibility() && e.children.length === 0 && !e.closest('.ef-skip-link, .skip-link') && !scrolled(e) && (e.textContent.trim() || e.matches('input, select, textarea, button'))).map(e => [e, e.getBoundingClientRect()]).filter(([, r]) => r.width > 0 && (r.left < 1 || r.right > w - 1)).map(([e, r]) => `${e.tagName.toLowerCase()} \"${(e.getAttribute('aria-label') || e.textContent).replace(/\\s+/g, ' ').trim().slice(0, 30)}\" ${r.left.toFixed(0)}..${r.right.toFixed(0)}`).slice(0, 8); })()"
            |> strings

        match touching with
        | [] -> report "ULI-L09" "inline gutter" Pass "no rendered text or control touches a viewport edge"
        | found -> report "ULI-L09" "inline gutter" Fail $"""content touches a viewport edge: {String.concat "; " found}"""

    /// ULI-L06/L05: a short word (a label word, not an identifier or path)
    /// is never broken across lines inside a table cell or header.
    let wordIntegrity (page: BrowserPage) =
        Observe.settle page |> ignore

        let broken =
            page.Evaluate
                "(() => { const out = []; for (const cell of document.querySelectorAll('th, td')) { if (!cell.checkVisibility()) continue; const walker = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT); for (let t = walker.nextNode(); t; t = walker.nextNode()) { const re = /(?<![A-Za-z0-9._\\/-])[A-Za-z]{3,12}(?![A-Za-z0-9._\\/-])/g; let m; while ((m = re.exec(t.textContent))) { const r = document.createRange(); r.setStart(t, m.index); r.setEnd(t, m.index + m[0].length); const tops = new Set([...r.getClientRects()].filter(x => x.width > 0).map(x => Math.round(x.top))); if (tops.size > 1) out.push(m[0]); } } } return [...new Set(out)].slice(0, 12); })()"
            |> strings

        match broken with
        | [] -> report "ULI-L06" "word integrity in tables" Pass "no label word is split across lines"
        | words -> report "ULI-L06" "word integrity in tables" Fail $"""words split across lines: {String.concat ", " words}"""

    /// ULI-L09: when a Forma data grid stacks (header hidden), every cell
    /// names its column and holds a single content item, so the stacked
    /// composition keeps each value beside its label.
    let stackedGridLabels (page: BrowserPage) =
        Observe.settle page |> ignore

        let observed =
            page.Evaluate
                "(() => { const grids = [...document.querySelectorAll('.ef-data-grid')].filter(g => g.checkVisibility()); const stacked = grids.filter(g => { const h = g.querySelector('thead'); return h && getComputedStyle(h).display === 'none'; }); const problems = []; for (const g of stacked) for (const td of g.querySelectorAll('td')) { const label = getComputedStyle(td, '::before').content; const named = label && label !== 'none' && label !== 'normal' && label !== '\"\"'; const items = [...td.childNodes].filter(n => n.nodeType === 1 || (n.nodeType === 3 && n.textContent.trim())).length; if (!named) problems.push(`unlabelled cell \"${td.textContent.trim().slice(0, 24)}\"`); if (items > 1) problems.push(`cell with ${items} loose items \"${td.textContent.trim().slice(0, 24)}\"`); } return { grids: grids.length, stacked: stacked.length, problems: [...new Set(problems)].slice(0, 10) }; })()"

        let grids = observed["grids"].GetValue<int>()
        let stacked = observed["stacked"].GetValue<int>()
        let problems = strings observed["problems"]

        match grids, stacked, problems with
        | 0, _, _ -> report "ULI-L09" "stacked data grid" Unknown "not applicable: no data grid rendered on this page state"
        | _, 0, _ -> report "ULI-L09" "stacked data grid" Pass $"{grids} grid(s) render as tables at this width"
        | _, _, [] -> report "ULI-L09" "stacked data grid" Pass $"{stacked} stacked grid(s): every cell is labelled and holds one item"
        | _, _, found -> report "ULI-L09" "stacked data grid" Fail (String.concat "; " found)

    /// ULI-A01: every in-page link (`#id`) names an element that exists.
    let inPageDestinations (page: BrowserPage) =
        let missing =
            page.Evaluate
                "[...document.querySelectorAll('a[href^=\"#\"]')].map(a => a.getAttribute('href')).filter(h => h.length > 1 && !document.getElementById(decodeURIComponent(h.slice(1))))"
            |> strings
            |> List.distinct

        match missing with
        | [] -> report "ULI-A01" "in-page destinations" Pass "every in-page link names an existing element"
        | found -> report "ULI-A01" "in-page destinations" Fail $"""in-page links with no destination: {String.concat ", " found}"""

    /// ULI-A06: every text entry control has an accessible name.
    let namedFields (page: BrowserPage) =
        let unnamed =
            page.Evaluate
                "[...document.querySelectorAll('input:not([type=hidden]), select, textarea')].filter(e => e.checkVisibility() && !(e.labels && e.labels.length) && !e.getAttribute('aria-label') && !e.getAttribute('aria-labelledby')).map(e => `${e.tagName.toLowerCase()}[name=${e.name}]`)"
            |> strings
            |> List.distinct

        match unnamed with
        | [] -> report "ULI-A06" "named fields" Pass "every rendered field has an accessible name"
        | found -> report "ULI-A06" "named fields" Fail $"""fields without an accessible name: {String.concat ", " found}"""
