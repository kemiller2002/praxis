namespace Praxis.UiConformance

// Native F# port of the pure evaluation layer of
// @echelon-foundry/forma-conformance 0.1.0
//   source: kemiller2002/forma packages/conformance/src/evaluate.mjs
//   commit: 6c71725a927187cacd6c6fc318bf266a21bbbdf8 (Forma PR #158, unpublished)
//
// Why a port and not a verbatim copy: Praxis's own repository owns no
// JavaScript, TypeScript, npm or Node tooling (RQ-ROS-2026-A024, enforced by
// `./praxis architecture check`), so the module cannot be vendored as .mjs.
// This file keeps the module's names, inputs, statuses and decisions
// one-for-one so that it can be replaced by the published package's semantics
// without changing a spec. It cites the contract
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 (ULI-L01..L13, ULI-A01..A10);
// it does not restate or fork the rules.

open System
open System.Text.RegularExpressions

/// PASS / FAIL / UNKNOWN. NOT_APPLICABLE and EXCEPTION are owned decisions the
/// calling test records; the helpers never produce them.
type Status =
    | Pass
    | Fail
    | Unknown

    member status.Text =
        match status with
        | Pass -> "PASS"
        | Fail -> "FAIL"
        | Unknown -> "UNKNOWN"

/// An exact accessible name, or a pattern (the module's string | RegExp).
type Name =
    | Exact of string
    | Pattern of Regex

    member name.Text =
        match name with
        | Exact text -> text
        | Pattern pattern -> string pattern

/// `{ role, name, scope? }`. `scope` is itself a target that narrows lookup.
type Target =
    { Role: string
      Name: Name
      Scope: Target option }

type Box =
    { X: float
      Y: float
      Width: float
      Height: float }

type Edge =
    | Top
    | Bottom
    | Center
    | Left
    | Right

    member edge.Text =
        match edge with
        | Top -> "top"
        | Bottom -> "bottom"
        | Center -> "center"
        | Left -> "left"
        | Right -> "right"

type AlignmentGroup =
    { RuleId: string
      Id: string
      Edge: Edge
      ToleranceCssPx: float
      Peers: Target list
      Rationale: string }

type PeerObservation =
    { Peer: Target
      Count: int
      Box: Box option }

type PeerMeasurement =
    { Element: string
      Status: Status
      Reason: string
      Box: Box option
      Coordinate: float option }

type AlignmentReport =
    { RuleId: string
      Group: string
      Edge: Edge
      ToleranceCssPx: float
      Measurements: PeerMeasurement list
      Status: Status
      DeltaCssPx: float option
      Reason: string }

/// The observable post-state an action must produce (ULI-A02).
type Expected =
    | Text of Name
    | Flag of bool

type OutcomeKind =
    | TextOutcome
    | Visible
    | Checked
    | Attribute of string
    | Open
    | Url

    member kind.Text =
        match kind with
        | TextOutcome -> "text"
        | Visible -> "visible"
        | Checked -> "checked"
        | Attribute _ -> "attribute"
        | Open -> "open"
        | Url -> "url"

/// `target = None` with `Open` is the module's `"controlled"` target.
type Outcome =
    { Kind: OutcomeKind
      Target: Target option
      Expected: Expected }

type ActionContract =
    { RuleId: string
      Action: Target
      Outcome: Outcome
      TimeoutMs: int }

/// A raw observation of an outcome: a string or boolean value, or nothing.
type Observed =
    | Value of string
    | State of bool
    | Nothing

type OutcomeObservation =
    { Value: Observed
      Missing: bool
      Count: int }

type ActionReport =
    { RuleId: string
      Action: string
      Outcome: string
      Expected: string
      Pre: OutcomeObservation option
      Post: OutcomeObservation option
      Status: Status
      Reason: string }

type OverflowObservation =
    { ScrollWidth: float
      ClientWidth: float
      Escapes: string list }

type SimpleReport =
    { RuleId: string
      Subject: string
      Status: Status
      Reason: string }

type LabelObservation =
    { Name: string
      Display: string
      ScrollWidth: float
      ClientWidth: float
      ScrollHeight: float
      ClientHeight: float
      Left: float
      Right: float
      Viewport: float }

[<RequireQualifiedAccess>]
module Evaluate =
    let contractId = "visual-engineering/UI-LAYOUT-INTERACTION"
    let contractVersion = "1.0.0"

    /// Widths every surface is tested at (ULI-L09).
    let contractWidths = [ 320; 390; 768; 1280 ]
    let defaultToleranceCssPx = 1.0
    let defaultTimeoutMs = 5000

    let target role name = { Role = role; Name = Exact name; Scope = None }
    let within scope (target: Target) = { target with Scope = Some scope }

    let describeTarget (target: Target option) =
        match target with
        | None -> "the element the action controls"
        | Some target -> $"{target.Role} \"{target.Name.Text}\""

    let requireSemanticTarget (target: Target) (label: string) =
        if String.IsNullOrWhiteSpace target.Role || target.Name.Text = "" then
            invalidArg label $"{label} must specify both accessible role and name."

        target

    let edgeCoordinate (box: Box) (edge: Edge) =
        match edge with
        | Top -> box.Y
        | Bottom -> box.Y + box.Height
        | Center -> box.Y + box.Height / 2.0
        | Left -> box.X
        | Right -> box.X + box.Width

    // ULI-L01/L02/L10: declared alignment groups ---------------------------

    /// Only a single, rendered, non-zero box can contribute a coordinate.
    let classifyPeerObservation (observation: PeerObservation) =
        let element = describeTarget (Some observation.Peer)
        let unknown reason box = { Element = element; Status = Unknown; Reason = reason; Box = box; Coordinate = None }

        match observation.Count, observation.Box with
        | 0, _ -> unknown "element not found in the accessibility tree (missing or hidden)" None
        | count, _ when count > 1 -> unknown $"accessible name is ambiguous ({count} matches)" None
        | _, None -> unknown "no bounding box (not rendered)" None
        | _, Some box when not (box.Width > 0.0) || not (box.Height > 0.0) -> unknown $"zero-size box ({box.Width}x{box.Height})" (Some box)
        | _, Some box -> { Element = element; Status = Pass; Reason = ""; Box = Some box; Coordinate = None }

    let validateAlignmentGroup (group: AlignmentGroup) =
        if String.IsNullOrWhiteSpace group.Id then invalidArg "group" "A declared alignment group requires an id."
        if group.Peers.Length < 2 then invalidArg "group" $"Alignment group {group.Id} requires at least two declared peers."
        if not (Regex.IsMatch(group.RuleId, @"^ULI-L\d{2}$")) then invalidArg "group" $"Alignment group {group.Id} must cite a ULI-L rule ID."
        if not (group.ToleranceCssPx >= 0.0) then invalidArg "group" $"Alignment group {group.Id} tolerance must be a non-negative number."
        group.Peers |> List.iteri (fun index peer -> requireSemanticTarget peer $"Peer {index + 1} of {group.Id}" |> ignore)
        group

    /// Missing evidence is UNKNOWN, never PASS; a delta above tolerance is FAIL.
    let evaluateAlignment (group: AlignmentGroup) (observations: PeerObservation list) : AlignmentReport =
        let declared = validateAlignmentGroup group

        let measurements =
            observations
            |> List.map classifyPeerObservation
            |> List.map (fun item ->
                match item.Status, item.Box with
                | Pass, Some box -> { item with Coordinate = Some(edgeCoordinate box declared.Edge) }
                | _ -> item)

        let report status delta reason =
            { RuleId = declared.RuleId
              Group = declared.Id
              Edge = declared.Edge
              ToleranceCssPx = declared.ToleranceCssPx
              Measurements = measurements
              Status = status
              DeltaCssPx = delta
              Reason = reason }

        match measurements |> List.filter (fun item -> item.Status <> Pass) with
        | [] ->
            let coordinates = measurements |> List.choose _.Coordinate
            let delta = List.max coordinates - List.min coordinates
            let status = if delta <= declared.ToleranceCssPx then Pass else Fail
            let comparison = if status = Pass then "<=" else ">"
            report status (Some delta) $"{declared.Edge.Text}-edge delta {delta:F2} CSS px {comparison} tolerance {declared.ToleranceCssPx} CSS px"
        | unknown -> report Unknown None (unknown |> List.map (fun item -> $"{item.Element}: {item.Reason}") |> String.concat "; ")

    let sameObservations (first: PeerObservation list) (second: PeerObservation list) =
        first.Length = second.Length
        && List.forall2
            (fun (a: PeerObservation) (b: PeerObservation) ->
                a.Count = b.Count
                && match a.Box, b.Box with
                   | None, None -> true
                   | Some x, Some y -> [ x.X - y.X; x.Y - y.Y; x.Width - y.Width; x.Height - y.Height ] |> List.forall (fun d -> abs d < 0.01)
                   | _ -> false)
            first
            second

    let formatAlignment (report: AlignmentReport) =
        let details =
            report.Measurements
            |> List.map (fun item ->
                let coordinate = item.Coordinate |> Option.map (fun c -> $" {report.Edge.Text}={c:F2}px") |> Option.defaultValue ""
                let reason = if item.Reason = "" then "" else $" ({item.Reason})"
                $"  - {item.Element}: {item.Status.Text}{coordinate}{reason}")
            |> String.concat "\n"

        $"{report.RuleId} {report.Status.Text}: group {report.Group}: {report.Reason}" + (if details = "" then "" else "\n" + details)

    // ULI-A01/A02/A05: action outcomes -------------------------------------

    let normalizeText (value: string) =
        if isNull value then "" else Regex.Replace(value, @"\s+", " ").Trim()

    let matchesExpected (observed: Observed) (expected: Expected) =
        match observed, expected with
        | Nothing, _ -> false
        | Value text, Text(Exact wanted) -> text = wanted
        | Value text, Text(Pattern pattern) -> pattern.IsMatch text
        | State flag, Flag wanted -> flag = wanted
        | State flag, Text(Pattern pattern) -> pattern.IsMatch(if flag then "true" else "false")
        | _ -> false

    let private expectsBoolean kind =
        match kind with
        | Visible
        | Checked
        | Open -> true
        | _ -> false

    let validateActionContract (contract: ActionContract) =
        requireSemanticTarget contract.Action "Action control" |> ignore
        if not (Regex.IsMatch(contract.RuleId, @"^ULI-A\d{2}$")) then invalidArg "contract" "An action assertion must cite a ULI-A rule ID."

        match contract.Outcome.Kind, contract.Outcome.Target with
        | Url, _ -> ()
        | Open, None -> ()
        | kind, None -> invalidArg "contract" $"Outcome target for {kind.Text} must specify both accessible role and name."
        | kind, Some target -> requireSemanticTarget target $"Outcome target for {kind.Text}" |> ignore

        match expectsBoolean contract.Outcome.Kind, contract.Outcome.Expected with
        | true, Flag _
        | false, Text _ -> ()
        | _ -> invalidArg "contract" $"Outcome {contract.Outcome.Kind.Text} requires an explicit expected post-state of the right type."

        if contract.TimeoutMs <= 0 then invalidArg "contract" "Action assertions require a bounded positive timeoutMs."
        contract

    let private expectedText (expected: Expected) =
        match expected with
        | Text name -> name.Text
        | Flag flag -> if flag then "true" else "false"

    let private actionBase (contract: ActionContract) pre post status reason : ActionReport =
        { RuleId = contract.RuleId
          Action = describeTarget (Some contract.Action)
          Outcome = contract.Outcome.Kind.Text
          Expected = expectedText contract.Outcome.Expected
          Pre = pre
          Post = post
          Status = status
          Reason = reason }

    /// A report when the action cannot demonstrate a transition (UNKNOWN for
    /// missing evidence, FAIL when the pre-state already satisfies the
    /// expectation), otherwise None.
    let evaluateActionPremise (contract: ActionContract) (pre: OutcomeObservation) (controlIssue: string option) =
        let report = actionBase contract (Some pre) None

        match controlIssue with
        | Some issue -> Some(report Unknown issue)
        | None when pre.Missing && contract.Outcome.Kind <> Visible -> Some(report Unknown "outcome target not found before activation")
        | None when pre.Count > 1 -> Some(report Unknown $"outcome target name is ambiguous ({pre.Count} matches)")
        | None when matchesExpected pre.Value contract.Outcome.Expected ->
            Some(report Fail "pre-state already satisfies the expected post-state; no transition can be demonstrated")
        | None -> None

    /// PASS only when the pre-state did not satisfy the expectation, the
    /// expected post-state was observed, and the observed value changed.
    let evaluateActionOutcome (contract: ActionContract) (pre: OutcomeObservation) (post: OutcomeObservation option) (controlIssue: string option) =
        match evaluateActionPremise contract pre controlIssue with
        | Some premise -> premise
        | None ->
            let report = actionBase contract (Some pre) post

            match post with
            | None -> report Fail "no post-state observed"
            | Some observed when not (matchesExpected observed.Value contract.Outcome.Expected) -> report Fail "expected post-state never appeared within the timeout"
            | Some observed when observed.Value = pre.Value -> report Fail "post-state equals pre-state"
            | Some _ -> report Pass "expected post-state observed after activation"

    let private show (observation: OutcomeObservation option) =
        match observation |> Option.map _.Value with
        | Some(Value text) -> $"\"{text}\""
        | Some(State flag) -> (if flag then "true" else "false")
        | _ -> "null"

    let formatAction (report: ActionReport) =
        $"{report.RuleId} {report.Status.Text}: {report.Action}: {report.Reason} (pre={show report.Pre}, post={show report.Post})"

    // ULI-L05/L06: overflow and label integrity ----------------------------

    let evaluateOverflow (observation: OverflowObservation) : SimpleReport =
        if not (observation.ClientWidth > 0.0) then
            { RuleId = "ULI-L05"; Subject = "page"; Status = Unknown; Reason = "no viewport width observed" }
        else
            let failed = observation.ScrollWidth - observation.ClientWidth > 0.0 || not observation.Escapes.IsEmpty
            let escapes = observation.Escapes |> List.map (fun escape -> "\n  " + escape) |> String.concat ""

            { RuleId = "ULI-L05"
              Subject = "page"
              Status = if failed then Fail else Pass
              Reason =
                $"document scrollWidth {observation.ScrollWidth} vs clientWidth {observation.ClientWidth}; {observation.Escapes.Length} text run(s) escaping the viewport or a clipping container{escapes}" }

    let evaluateLabelIntegrity (observation: LabelObservation) : SimpleReport =
        if observation.Display = "inline" || not (observation.ClientWidth > 0.0) || not (observation.ClientHeight > 0.0) then
            { RuleId = "ULI-L06"; Subject = observation.Name; Status = Unknown; Reason = "label has no measurable box (inline or zero-size); measure its block container" }
        else
            let clipped = observation.ScrollWidth > observation.ClientWidth + 1.0 || observation.ScrollHeight > observation.ClientHeight + 1.0
            let offscreen = observation.Left < -0.5 || observation.Right > observation.Viewport + 0.5

            { RuleId = "ULI-L06"
              Subject = observation.Name
              Status = if clipped || offscreen then Fail else Pass
              Reason = (if clipped then "content clipped" else "content fits") + "; " + (if offscreen then "box offscreen" else "box on screen") }
