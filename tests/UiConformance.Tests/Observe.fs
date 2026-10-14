namespace Praxis.UiConformance

// Native F# counterpart of the browser adapter layer of
// @echelon-foundry/forma-conformance 0.1.0
//   source: kemiller2002/forma packages/conformance/src/index.mjs
//   commit: 6c71725a927187cacd6c6fc318bf266a21bbbdf8 (Forma PR #158, unpublished)
//
// The module drives Playwright; Praxis may not (RQ-ROS-2026-A024), so this
// adapter observes the same things over the Chrome DevTools Protocol with the
// BCL-only harness the browser suite already uses (tests/Praxis.Tests/
// BrowserHarness.fs). It keeps the module's observation rules:
//   * targets resolve by accessible role and name (Accessibility.queryAXTree,
//     the browser's own computation), never by coordinates or CSS selectors;
//   * `disclosure` addresses a native <summary> by its text, `text` an element
//     by exact visible text;
//   * observations are taken after `document.fonts.ready` and two animation
//     frames, retried until two consecutive readings agree, bounded by a
//     deadline -- no fixed sleeps;
//   * a missing, ambiguous, hidden, disabled or inert control is UNKNOWN.
// Decisions stay in FormaConformance.fs (Evaluate); this file only observes.

open System
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Praxis.Tests

/// A resolved element: its DevTools backend node id.
type Element = { Backend: int }

[<RequireQualifiedAccess>]
module Observe =
    let private arg (value: JsonNode) =
        let wrapper = JsonObject()
        wrapper["value"] <- value
        wrapper :> JsonNode

    /// The page's settle point: fonts loaded, then two animation frames.
    let settleExpression =
        "document.fonts.ready.then(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true)))))"

    let settle (page: BrowserPage) =
        try
            page.Evaluate settleExpression |> ignore
            true
        with _ ->
            false

    let private objectId (page: BrowserPage) (element: Element) =
        let resolved = page.Send("DOM.resolveNode", Cdp.args [ "backendNodeId", Cdp.num element.Backend ])
        Cdp.text resolved [ "object"; "objectId" ]

    /// Runs `declaration` with `this` bound to the element (DevTools, not page
    /// script) and returns its JSON value.
    let call (page: BrowserPage) (element: Element) (declaration: string) (arguments: JsonNode list) : JsonNode =
        let parameters =
            Cdp.args
                [ "functionDeclaration", Cdp.str declaration
                  "objectId", Cdp.str (objectId page element)
                  "arguments", (JsonArray(arguments |> List.map arg |> Array.ofList) :> JsonNode)
                  "returnByValue", Cdp.flag true
                  "awaitPromise", Cdp.flag true ]

        let result = page.Send("Runtime.callFunctionOn", parameters)

        match result["exceptionDetails"] with
        | null -> Cdp.at result [ "result"; "value" ]
        | details -> failwith $"evaluation failed: {details.ToJsonString()}"

    let private documentBackend (page: BrowserPage) =
        Cdp.int (page.Send("DOM.getDocument", Cdp.args [ "depth", Cdp.num 0 ])) [ "root"; "backendNodeId" ]

    /// Elements an expression evaluated in the page returns as an array.
    let private elementsOf (page: BrowserPage) (expression: string) =
        let evaluated = page.Send("Runtime.evaluate", Cdp.args [ "expression", Cdp.str expression; "returnByValue", Cdp.flag false ])

        match evaluated["exceptionDetails"] with
        | null -> ()
        | details -> failwith $"evaluation failed: {details.ToJsonString()}"

        let arrayId = Cdp.text evaluated [ "result"; "objectId" ]
        let properties = page.Send("Runtime.getProperties", Cdp.args [ "objectId", Cdp.str arrayId; "ownProperties", Cdp.flag true ])

        (Cdp.at properties [ "result" ]).AsArray()
        |> Seq.filter (fun property -> Regex.IsMatch(Cdp.text property [ "name" ], "^\\d+$"))
        |> Seq.choose (fun property ->
            match Cdp.at property [ "value"; "objectId" ] with
            | null -> None
            | id ->
                let described = page.Send("DOM.describeNode", Cdp.args [ "objectId", Cdp.str (id.GetValue<string>()) ])
                Some { Backend = Cdp.int described [ "node"; "backendNodeId" ] })
        |> List.ofSeq

    /// Chrome applies CSS `text-transform` to the names it computes; the
    /// module's Playwright resolution does not. An exact name therefore also
    /// matches its case-transformed rendering (ordinal, ignoring case).
    let private namePredicate (name: Name) =
        match name with
        | Exact text -> fun (candidate: string) -> String.Equals(Evaluate.normalizeText candidate, text, StringComparison.OrdinalIgnoreCase)
        | Pattern pattern -> fun (candidate: string) -> pattern.IsMatch candidate

    /// Elements under `root` whose computed role and accessible name match.
    let private byRole (page: BrowserPage) (root: Element) (role: string) (name: Name) =
        let parameters = Cdp.args [ "backendNodeId", Cdp.num root.Backend; "role", Cdp.str role ]

        let matches = namePredicate name

        let candidates =
            (Cdp.at (page.Send("Accessibility.queryAXTree", parameters)) [ "nodes" ]).AsArray()
            |> Seq.filter (fun node -> not (node["ignored"] |> Option.ofObj |> Option.map (fun flag -> flag.GetValue<bool>()) |> Option.defaultValue false))
            |> Seq.choose (fun node ->
                node["backendDOMNodeId"]
                |> Option.ofObj
                |> Option.map (fun id -> Cdp.text node [ "name"; "value" ], { Backend = id.GetValue<int>() }))
            |> Seq.distinctBy snd
            |> List.ofSeq

        // ARIA names a row from its content; Chrome leaves table rows
        // unnamed, which Playwright does not. A row therefore also matches
        // on its normalized text content.
        let content (element: Element) =
            Evaluate.normalizeText ((call page element "function() { return this.innerText; }" []).GetValue<string>())

        candidates
        |> List.filter (fun (computed, element) -> matches computed || (role = "row" && computed = "" && matches (content element)))
        |> List.map snd

    /// `disclosure` (<summary> by text) and `text` (exact visible text) are
    /// the module's two pseudo-roles; both are matched in the page.
    let private byText (page: BrowserPage) (root: Element) (selector: string) (name: Name) =
        let candidates =
            let rootId = objectId page root

            let evaluated =
                page.Send(
                    "Runtime.callFunctionOn",
                    Cdp.args
                        [ "functionDeclaration",
                          Cdp.str (
                              "function(selector) { const scope = this.nodeType === 9 ? this.documentElement : this; "
                              + "return [...scope.querySelectorAll(selector)].filter(e => e.checkVisibility()); }"
                          )
                          "objectId", Cdp.str rootId
                          "arguments", (JsonArray(arg (Cdp.str selector)) :> JsonNode)
                          "returnByValue", Cdp.flag false ]
                )

            let arrayId = Cdp.text evaluated [ "result"; "objectId" ]
            let properties = page.Send("Runtime.getProperties", Cdp.args [ "objectId", Cdp.str arrayId; "ownProperties", Cdp.flag true ])

            (Cdp.at properties [ "result" ]).AsArray()
            |> Seq.filter (fun property -> Regex.IsMatch(Cdp.text property [ "name" ], "^\\d+$"))
            |> Seq.choose (fun property ->
                match Cdp.at property [ "value"; "objectId" ] with
                | null -> None
                | id -> Some { Backend = Cdp.int (page.Send("DOM.describeNode", Cdp.args [ "objectId", Cdp.str (id.GetValue<string>()) ])) [ "node"; "backendNodeId" ] })
            |> List.ofSeq

        let matches = namePredicate name

        candidates
        |> List.filter (fun element ->
            let text = (call page element "function() { return this.textContent; }" []).GetValue<string>()
            matches (Evaluate.normalizeText text))

    /// Resolves a declared target to every matching element (0, 1 or many).
    let rec resolve (page: BrowserPage) (target: Target) : Element list =
        let roots =
            match target.Scope with
            | None -> [ { Backend = documentBackend page } ]
            | Some scope -> resolve page scope

        match roots with
        | [ root ] ->
            match target.Role with
            | "disclosure" -> byText page root "summary" target.Name
            | "text" -> byText page root "body *:not(script):not(style):not(option)" target.Name |> List.filter (fun element -> (call page element "function() { return this.children.length === 0; }" []).GetValue<bool>())
            | role -> byRole page root role target.Name
        | _ -> []

    let box (page: BrowserPage) (element: Element) : Box option =
        let value =
            call page element "function() { if (!this.checkVisibility()) return null; const r = this.getBoundingClientRect(); return [r.x, r.y, r.width, r.height]; }" []

        match value with
        | null -> None
        | node ->
            let numbers = node.AsArray() |> Seq.map (fun item -> item.GetValue<float>()) |> Array.ofSeq
            Some { X = numbers[0]; Y = numbers[1]; Width = numbers[2]; Height = numbers[3] }

    /// Retries `observe` after each settle until `stable previous current`,
    /// or None at the deadline.
    let stableReading (page: BrowserPage) (timeoutMs: int) (observe: unit -> 'reading) (same: 'reading -> 'reading -> bool) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec loop previous =
            settle page |> ignore
            let current = try Some(observe ()) with _ -> None

            match previous, current with
            | Some before, Some now when same before now -> Some now
            | _ when DateTime.UtcNow > deadline -> None
            | _ -> loop current

        loop None

    /// Polls `observe` (after each settle) until `accept` holds or the deadline passes.
    let until (page: BrowserPage) (timeoutMs: int) (observe: unit -> 'value option) (accept: 'value -> bool) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

        let rec loop () =
            settle page |> ignore

            match (try observe () with _ -> None) with
            | Some value when accept value -> true
            | _ when DateTime.UtcNow > deadline -> false
            | _ -> loop ()

        loop ()

    // ULI-L01/L02/L10 --------------------------------------------------------

    let private observePeer (page: BrowserPage) (peer: Target) : PeerObservation =
        let found = resolve page peer
        let measured = match found with [ one ] -> box page one | _ -> None
        { Peer = peer; Count = found.Length; Box = measured }

    /// Measures a declared group once fonts are ready and layout is stable.
    let measureAlignment (page: BrowserPage) (group: AlignmentGroup) (timeoutMs: int) : AlignmentReport =
        let declared = Evaluate.validateAlignmentGroup group

        match stableReading page timeoutMs (fun () -> declared.Peers |> List.map (observePeer page)) Evaluate.sameObservations with
        | Some observations -> Evaluate.evaluateAlignment declared observations
        | None ->
            { RuleId = declared.RuleId
              Group = declared.Id
              Edge = declared.Edge
              ToleranceCssPx = declared.ToleranceCssPx
              Measurements = []
              Status = Unknown
              DeltaCssPx = None
              Reason = "layout did not stabilize within the timeout" }

    // ULI-A01/A02/A05/A06 ----------------------------------------------------

    let url (page: BrowserPage) =
        let index, entries = page.History()
        entries[index]

    let private readOpen =
        "function() { const e = this; if (e.hasAttribute('popover')) return e.matches(':popover-open'); if ('open' in e) return Boolean(e.open); return e.getAttribute('aria-expanded') === 'true'; }"

    /// The element the action names in markup: the <details> of a
    /// <summary>, or the target of popovertarget, commandfor or aria-controls.
    let private controlled (page: BrowserPage) (control: Element) =
        let declaration =
            "function() { const byId = id => (id ? this.ownerDocument.getElementById(id) : null); "
            + "if (this.tagName === 'SUMMARY') return this.parentElement && this.parentElement.tagName === 'DETAILS' ? this.parentElement : null; "
            + "return byId(this.getAttribute('popovertarget')) || byId(this.getAttribute('commandfor')) || byId(this.getAttribute('aria-controls')) || (this.closest('form[method=dialog]') ? this.closest('dialog') : null); }"

        let result =
            page.Send("Runtime.callFunctionOn", Cdp.args [ "functionDeclaration", Cdp.str declaration; "objectId", Cdp.str (objectId page control); "returnByValue", Cdp.flag false ])

        match Cdp.at result [ "result"; "objectId" ] with
        | null -> None
        | id -> Some { Backend = Cdp.int (page.Send("DOM.describeNode", Cdp.args [ "objectId", Cdp.str (id.GetValue<string>()) ])) [ "node"; "backendNodeId" ] }

    let observeOutcome (page: BrowserPage) (control: Element option) (outcome: Outcome) : OutcomeObservation =
        match outcome.Kind, outcome.Target with
        | Url, _ -> { Value = Value(url page); Missing = false; Count = 1 }
        | Open, None ->
            match control |> Option.bind (controlled page) with
            | Some details -> { Value = State((call page details readOpen []).GetValue<bool>()); Missing = false; Count = 1 }
            | None -> { Value = Nothing; Missing = true; Count = 0 }
        | kind, Some target ->
            let found = resolve page target

            match kind, found with
            | Visible, [ one ] -> { Value = State((box page one).IsSome); Missing = false; Count = 1 }
            | Visible, many -> { Value = State false; Missing = many.IsEmpty; Count = many.Length }
            | _, [ one ] ->
                let value =
                    match kind with
                    | TextOutcome -> Value(Evaluate.normalizeText ((call page one "function() { return this.textContent; }" []).GetValue<string>()))
                    | Checked -> State((call page one "function() { return Boolean(this.checked); }" []).GetValue<bool>())
                    | Attribute name ->
                        match call page one "function(name) { return this.getAttribute(name); }" [ Cdp.str name ] with
                        | null -> Nothing
                        | text -> Value(text.GetValue<string>())
                    | Open -> State((call page one readOpen []).GetValue<bool>())
                    | _ -> Nothing

                { Value = value; Missing = false; Count = 1 }
            | _, many -> { Value = Nothing; Missing = many.IsEmpty; Count = many.Length }
        | _, None -> { Value = Nothing; Missing = true; Count = 0 }

    let private inspectControl (page: BrowserPage) (found: Element list) =
        match found with
        | [] -> Error "action control not found"
        | _ :: _ :: _ -> Error $"action control name is ambiguous ({found.Length} matches)"
        | [ one ] ->
            match box page one with
            | None -> Error "action control is not visible"
            | Some _ when (call page one "function() { return this.matches(':disabled') || this.getAttribute('aria-disabled') === 'true'; }" []).GetValue<bool>() ->
                Error "action control is disabled"
            | Some _ when (call page one "function() { return Boolean(this.closest('[inert]')); }" []).GetValue<bool>() -> Error "action control is inert"
            | Some _ -> Ok one

    /// Focus as a keyboard user reaches it, then a real pointer activation at
    /// the element's current box (computed, never a hardcoded coordinate).
    let activate (page: BrowserPage) (element: Element) =
        page.Send("DOM.focus", Cdp.args [ "backendNodeId", Cdp.num element.Backend ]) |> ignore
        page.Send("DOM.scrollIntoViewIfNeeded", Cdp.args [ "backendNodeId", Cdp.num element.Backend ]) |> ignore
        settle page |> ignore

        // The viewport-relative box (DOM.getBoxModel reports document
        // coordinates, which miss once the page is scrolled).
        let current =
            box page element |> Option.defaultWith (fun () -> failwith "the control has no box to activate")

        let x = current.X + current.Width / 2.0
        let y = current.Y + current.Height / 2.0

        for kind in [ "mouseMoved"; "mousePressed"; "mouseReleased" ] do
            page.Send("Input.dispatchMouseEvent", Cdp.args [ "type", Cdp.str kind; "x", Cdp.real x; "y", Cdp.real y; "button", Cdp.str "left"; "clickCount", Cdp.num 1 ]) |> ignore

    /// Capture the pre-state, activate the control through its role and
    /// name, wait (bounded) for the declared post-state, and report.
    let runActionAssertion (page: BrowserPage) (contract: ActionContract) : ActionReport =
        let declared = Evaluate.validateActionContract contract

        match inspectControl page (resolve page declared.Action) with
        | Error issue -> Evaluate.evaluateActionOutcome declared { Value = Nothing; Missing = true; Count = 0 } None (Some issue)
        | Ok control ->
            let pre = observeOutcome page (Some control) declared.Outcome

            match Evaluate.evaluateActionPremise declared pre None with
            | Some premise -> premise
            | None ->
                activate page control

                let observePost () =
                    try Some(observeOutcome page (Some control) declared.Outcome) with _ -> None

                until page declared.TimeoutMs observePost (fun observed -> Evaluate.matchesExpected observed.Value declared.Outcome.Expected) |> ignore
                Evaluate.evaluateActionOutcome declared pre (observePost ()) None

    // Inventory ---------------------------------------------------------------

    /// The module's ACTION_SELECTOR, verbatim.
    let actionSelector =
        [ "a[href]"; "button"; "summary"; "input[type=submit]"; "input[type=button]"; "input[type=reset]"; "input[type=image]"
          "[role=button]"; "[role=link]"; "[role=menuitem]"; "[role=menuitemcheckbox]"; "[role=menuitemradio]"; "[role=tab]"; "[role=switch]" ]
        |> String.concat ", "

    type InventoryItem = { Role: string; Name: string; Element: Element }

    /// Enabled, rendered, non-inert action controls with the role and name
    /// the browser computes. A list of what needs a contract, not a no-op detector.
    let inventoryActions (page: BrowserPage) : InventoryItem list =
        let expression =
            $"[...document.querySelectorAll({Text.Json.JsonSerializer.Serialize actionSelector})].filter(e => {{ const r = e.getBoundingClientRect(); const s = getComputedStyle(e); "
            + "return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && e.checkVisibility({ visibilityProperty: true, contentVisibilityAuto: true }) "
            + "&& !(e.matches(':disabled') || e.getAttribute('aria-disabled') === 'true') && !e.closest('[inert]'); })"

        elementsOf page expression
        |> List.map (fun element ->
            let tag = (call page element "function() { return this.tagName; }" []).GetValue<string>()

            if tag = "SUMMARY" then
                { Role = "disclosure"; Name = Evaluate.normalizeText ((call page element "function() { return this.textContent; }" []).GetValue<string>()); Element = element }
            else
                let ax = page.Send("Accessibility.getPartialAXTree", Cdp.args [ "backendNodeId", Cdp.num element.Backend; "fetchRelatives", Cdp.flag false ])
                let node = (Cdp.at ax [ "nodes" ]).AsArray() |> Seq.tryHead

                match node with
                | Some node -> { Role = Cdp.text node [ "role"; "value" ]; Name = Cdp.text node [ "name"; "value" ]; Element = element }
                | None -> { Role = "unknown"; Name = ""; Element = element })

    // ULI-L05/L06/L09 ---------------------------------------------------------

    /// The module's measureOverflow page observation, verbatim.
    let private overflowExpression =
        """(() => {
    const viewport = document.documentElement.clientWidth;
    const clippingAncestor = element => {
      for (let node = element; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowX;
        if (overflow === "auto" || overflow === "scroll") return { scroller: true };
        if (overflow === "hidden" || overflow === "clip") {
          const rect = node.getBoundingClientRect();
          return rect.width <= 1 || rect.height <= 1 ? { hidden: true } : { rect };
        }
      }
      return null;
    };
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const texts = [];
    for (let text = walker.nextNode(); text; text = walker.nextNode()) texts.push(text);
    const escapes = texts.flatMap(text => {
      const element = text.parentElement;
      if (!text.textContent.trim() || !element?.checkVisibility()) return [];
      const own = element.getBoundingClientRect();
      if (own.width <= 1 && own.height <= 1) return [];
      const clip = clippingAncestor(element);
      if (clip?.scroller || clip?.hidden) return [];
      const range = document.createRange();
      range.selectNodeContents(text);
      const escaped = [...range.getClientRects()].find(rect => rect.width > 0 &&
        (rect.right > viewport + 0.5 || rect.left < -0.5 || (clip?.rect && (rect.right > clip.rect.right + 0.5 || rect.left < clip.rect.left - 0.5))));
      return escaped ? [`${element.tagName.toLowerCase()} "${text.textContent.trim().slice(0, 40)}" ${escaped.left.toFixed(1)}..${escaped.right.toFixed(1)}`] : [];
    });
    return { scrollWidth: document.documentElement.scrollWidth, clientWidth: viewport, escapes };
  })()"""

    let measureOverflow (page: BrowserPage) : SimpleReport =
        settle page |> ignore
        let observed = page.Evaluate overflowExpression

        Evaluate.evaluateOverflow
            { ScrollWidth = observed["scrollWidth"].GetValue<float>()
              ClientWidth = observed["clientWidth"].GetValue<float>()
              Escapes = observed["escapes"].AsArray() |> Seq.map (fun item -> item.GetValue<string>()) |> List.ofSeq }

    let measureLabelIntegrity (page: BrowserPage) (element: Element) : SimpleReport =
        let observed =
            call
                page
                element
                "function() { const r = this.getBoundingClientRect(); return { name: this.textContent.replace(/\\s+/g, ' ').trim(), display: getComputedStyle(this).display, scrollWidth: this.scrollWidth, clientWidth: this.clientWidth, scrollHeight: this.scrollHeight, clientHeight: this.clientHeight, left: r.left, right: r.right, viewport: document.documentElement.clientWidth }; }"
                []

        let number (name: string) = observed[name].GetValue<float>()

        Evaluate.evaluateLabelIntegrity
            { Name = observed["name"].GetValue<string>()
              Display = observed["display"].GetValue<string>()
              ScrollWidth = number "scrollWidth"
              ClientWidth = number "clientWidth"
              ScrollHeight = number "scrollHeight"
              ClientHeight = number "clientHeight"
              Left = number "left"
              Right = number "right"
              Viewport = number "viewport" }

    /// Sets the CSS viewport, as a device of that width presents the page.
    let setViewport (page: BrowserPage) (width: int) =
        page.Send("Emulation.setDeviceMetricsOverride", Cdp.args [ "width", Cdp.num width; "height", Cdp.num 900; "deviceScaleFactor", Cdp.num 1; "mobile", Cdp.flag false ]) |> ignore
        settle page |> ignore

    /// The module's scaleDefaultText: the browser's default font size, as a
    /// user's setting changes it. Ok when the root font size followed.
    let scaleDefaultText (page: BrowserPage) (factor: float) =
        let standard = int (Math.Round(16.0 * factor))
        let fixedSize = int (Math.Round(13.0 * factor))
        let sizes = Cdp.args [ "standard", Cdp.num standard; "fixed", Cdp.num fixedSize ]
        page.Send("Page.setFontSizes", Cdp.args [ "fontSizes", sizes :> JsonNode ]) |> ignore

        let followed =
            until page 5000 (fun () -> Some((page.Evaluate "parseFloat(getComputedStyle(document.documentElement).fontSize)").GetValue<float>())) (fun size -> size > 16.0 * factor - 1.0)

        if followed then Ok() else Error "default text size did not change (is the root font size fixed in px?)"

    let reduceMotion (page: BrowserPage) =
        let feature = Cdp.args [ "name", Cdp.str "prefers-reduced-motion"; "value", Cdp.str "reduce" ]
        page.Send("Emulation.setEmulatedMedia", Cdp.args [ "features", (JsonArray(feature :> JsonNode) :> JsonNode) ]) |> ignore

    // Input (semantic paths; the values a person types) -----------------------

    /// Types into a text control as a person does (focus, select, insert).
    let fill (page: BrowserPage) (target: Target) (text: string) =
        match resolve page target with
        | [ element ] ->
            page.Send("DOM.focus", Cdp.args [ "backendNodeId", Cdp.num element.Backend ]) |> ignore
            call page element "function() { this.select(); }" [] |> ignore
            page.Send("Input.insertText", Cdp.args [ "text", Cdp.str text ]) |> ignore
        | found -> failwith $"fill: {Evaluate.describeTarget (Some target)} resolved to {found.Length} elements"

    /// Chooses an option of a <select> by its value (the browser's own selection).
    let choose (page: BrowserPage) (target: Target) (value: string) =
        match resolve page target with
        | [ element ] -> call page element "function(v) { this.value = v; this.dispatchEvent(new Event('change', { bubbles: true })); }" [ Cdp.str value ] |> ignore
        | found -> failwith $"choose: {Evaluate.describeTarget (Some target)} resolved to {found.Length} elements"

    /// Puts files on a file input, as the browser's file picker does.
    let attachFiles (page: BrowserPage) (element: Element) (paths: string list) =
        page.Send("DOM.setFileInputFiles", Cdp.args [ "backendNodeId", Cdp.num element.Backend; "files", (JsonArray(paths |> List.map Cdp.str |> Array.ofList) :> JsonNode) ]) |> ignore
