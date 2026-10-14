namespace Praxis.Cli

open System
open System.Text.Json.Nodes
open Praxis.Infrastructure.Foundations

/// The control plane's declared listen scope (PRX-CTL-003).
type ControlPlaneScope = { Host: string; Port: int }

/// Execution routes of `praxis web serve`: the local control-plane API
/// (PRX-CTL-001/005) and operator pages (PRX-UI-001/020). Every route runs
/// `praxis execution ... --json` and renders what it returns; legality,
/// reasons and actor requirements come only from the CLI (PRX-UI-034).
[<RequireQualifiedAccess>]
module WebExecutions =
    type private Run = string list -> Result<ProcessResult, string>

    let private e = Html.escape
    let private str (n: JsonNode) (k: string) = match n with null -> "" | n -> (match n[k] with :? JsonValue as v -> v.ToString() | _ -> "")
    let private items (n: JsonNode) (k: string) = match n with null -> [] | n -> (match n[k] with :? JsonArray as a -> a |> Seq.choose Option.ofObj |> Seq.toList | _ -> [])
    let private parse (text: string) = try JsonNode.Parse text |> Option.ofObj with :? Text.Json.JsonException -> None

    /// The CLI's JSON, or its refusal as a structured error: 409 for a
    /// governance refusal (exit 3), 400 otherwise. State is unchanged.
    let private api (run: Run) (args: string list) =
        match run args with
        | Error message -> HttpMessages.jsonError 500 message
        | Ok r when r.Exit = 0 -> HttpMessages.json 200 r.Out
        | Ok r ->
            let node = JsonObject()
            node["error"] <- JsonValue.Create(CliProcess.failureMessage r)
            node["refused"] <- JsonValue.Create(r.Exit = 3)
            HttpMessages.jsonNode (if r.Exit = 3 then 409 else 400) node

    let private field name body = HttpMessages.jsonString name body |> HttpMessages.nonBlank

    /// The CLI line for a transition request. A human operator acting
    /// through the form is recorded as a human actor (PRX-UI-004/027).
    let transitionArgs (id: string) (body: RequestBody) =
        let reason = field "reason" body |> Option.defaultValue ""

        let identity =
            match field "human" body, field "operator" body with
            | Some _, Some name -> [ "--actor-kind"; "human"; "--actor"; name ]
            | _ -> []

        match field "action" body with
        | Some "rebind" -> Some([ "execution"; "rebind"; id; "--reason"; reason ] @ identity)
        | Some action -> Some([ "execution"; "transition"; id; "--action"; action; "--reason"; reason ] @ identity)
        | None -> None

    let scopeDocument (scope: ControlPlaneScope) : JsonNode =
        let loopback = List.contains scope.Host [ "127.0.0.1"; "localhost"; "::1" ]
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.control-plane/1"
        node["host"] <- JsonValue.Create scope.Host
        node["port"] <- JsonValue.Create scope.Port
        node["loopbackOnly"] <- JsonValue.Create loopback
        node["exposure"] <- JsonValue.Create(if loopback then "local machine only" else "network: every interface the host name resolves to")
        node["authentication"] <- JsonValue.Create "none"
        node["authority"] <- JsonValue.Create "the praxis CLI over repository state; the host keeps no state"
        node

    // ---------------------------------------------------------------- HTML
    // Forma presentation (SAF-FORMA-2..6) with the composition of the work
    // pages: lozenges carry their state as text, tables are data grids whose
    // cells name their columns, and every field has a visible label
    // (visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 ULI-L04/L09, ULI-A06).

    let private lozenge (state: string) (text: string) =
        $"<span class=\"ef-status-lozenge\" data-state=\"{FormaMarkup.lozengeState state}\">{e text}</span>"

    let private cell (column: string) (content: string) = $"<td data-label=\"{e column}\">{content}</td>"

    let private grid (label: string) (headers: string list) (rows: string list) =
        let head = headers |> List.map (fun header -> $"<th>{e header}</th>") |> String.concat ""
        $"<div class=\"ef-data-grid\" role=\"region\" tabindex=\"0\" aria-label=\"{e label}\"><table><thead><tr>{head}</tr></thead><tbody>\n" + String.concat "\n" rows + "\n</tbody></table></div>"

    let private requirement (a: JsonNode) =
        if str a "actorRequirement" = "human-required" then "<strong class=\"human-required\">human required</strong>" else "<span>agent or human</span>"

    let private actionRow (a: JsonNode) =
        let target = match str a "target" with "" -> "" | t -> " " + e t
        let available = str a "available" = "true"
        let state = if available then lozenge "ready" "legal" else lozenge "blocked" "blocked"
        let why = if available then "" else items a "reasons" |> List.map (fun r -> $"<li>{e (r.ToString())}</li>") |> String.concat "" |> sprintf "<ul class=\"reasons\">%s</ul>"
        "<tr>" + cell "Transition" $"""<code>{e (str a "transition")}</code>{target}""" + cell "Availability" state + cell "Who" (requirement a) + cell "Why not" why + "</tr>"

    let private form (id: string) (a: JsonNode) =
        let action = (str a "transition").Replace("execution.", "")
        let human = str a "actorRequirement" = "human-required"
        let target = $"/executions/{Html.segment id}/transitions"
        let reasonId = $"reason-{e action}"

        let identity =
            if human then
                $"<div class=\"ef-field\"><label class=\"ef-field__label\" for=\"operator-{e action}\">Operator (recorded as a human actor)</label><input id=\"operator-{e action}\" name=\"operator\" required /></div><label><input type=\"checkbox\" name=\"human\" value=\"yes\" required /> I am a human operator authorizing this transition</label>"
            else
                ""

        $"""<form method="post" action="{target}"><div class="ef-stack"><input type="hidden" name="action" value="{e action}" /><div class="ef-field"><label class="ef-field__label" for="{reasonId}">Reason for {e action}</label><input id="{reasonId}" name="reason" placeholder="reason" /></div>{identity}<div class="ef-actions"><button type="submit" class="ef-button">{e action}{(if human then " (human)" else "")}</button></div></div></form>"""

    let private stepRow (s: JsonNode) =
        let status = str s "status"

        let label, state =
            match status with
            | "mismatch" -> "receipt mismatch", "blocked"
            | "indeterminate" -> "unknown effect", "unknown"
            | "match" -> "receipt matches", "complete"
            | other -> other, other

        "<tr>" + cell "Step" (e (str s "stepId")) + cell "Receipt" (lozenge state label) + cell "Attempts" (e (str s "attempts")) + "</tr>"

    /// One execution: state, actor and host, evaluator, containment,
    /// receipts, obligations, unknowns and legal actions.
    let renderExecution (node: JsonNode) (query: (string * string) list) =
        let id = str node "executionId"
        let actor = node["actor"]
        let host = [ str actor "provider"; str actor "model"; str actor "runtime" ] |> List.filter ((<>) "") |> String.concat " / "
        let steps = items node "steps"
        let effects = items node "scopeEffects"
        let divergence = items node "divergence"
        let unknown = steps |> List.filter (fun s -> str s "status" = "indeterminate")
        let actions = items node "legalActions"
        let blocked = if str node "state" = "blocked" then $"""<p><strong>Blocked:</strong> {e (str node "stateReason")}</p>""" else ""
        let list title (xs: string list) = match xs with [] -> $"<p>No {title}.</p>" | _ -> xs |> List.map (fun x -> $"<li>{e x}</li>") |> String.concat "" |> sprintf "<ul>%s</ul>"
        let profile = node["containmentProfile"]
        let shown (value: string) = if value = "" then "not recorded" else value

        let receipts =
            match steps with
            | [] -> "<p>No step receipts recorded.</p>"
            | _ -> grid "Step receipts" [ "Step"; "Receipt"; "Attempts" ] (steps |> List.map stepRow)

        Html.page
            $"{id} · Praxis execution"
            (String.concat
                "\n"
                [ "<header class=\"ef-section\"><h1>Execution</h1><nav class=\"ef-cluster\" aria-label=\"Primary\"><a href=\"/\">Queue</a></nav></header><main id=\"main\" tabindex=\"-1\">"
                  Html.flash query
                  $"""<p><a href="/work/{Html.segment (str node "workItem" |> fun w -> w.Substring(w.LastIndexOfAny [| ':'; '#' |] + 1))}">&larr; {e (str node "workItem")}</a></p>"""
                  $"<section id=\"execution\" class=\"ef-section\" aria-label=\"Execution\"><h2>{e id}</h2>"
                  $"""<p class="ef-cluster">{lozenge (str node "state") (str node "state")} <span>role <strong>{e (str node "role")}</strong></span></p>"""
                  blocked
                  $"""<dl><dt>Actor</dt><dd>{e (str actor "id")} ({e (str actor "kind")})</dd><dt>Execution host</dt><dd>{e (if host = "" then "not reported" else host)}</dd><dt>Baseline</dt><dd><code>{e (shown (str node "baselineRevision"))}</code></dd><dt>Candidate</dt><dd><code>{e (shown (str node "candidateRevision"))}</code></dd><dt>Evaluator</dt><dd><code>{e (shown (str node["evaluator"] "fingerprint"))}</code></dd><dt>Verification</dt><dd>{e (shown (str node["verification"] "outcome"))}</dd><dt>Containment</dt><dd>{e (str node "containment")} (source: {e (match str profile "source" with "" -> "no host report" | s -> s)})</dd></dl></section>"""
                  "<section id=\"receipts\" class=\"ef-section\" aria-label=\"Step receipts\"><h3>Step receipts</h3>"
                  receipts
                  "</section>"
                  "<section id=\"obligations\" class=\"ef-section\" aria-label=\"Obligations\"><h3>Obligations</h3>"
                  list "unresolved scope effects" (effects |> List.map (fun x -> $"""unresolved scope effect: {str x "resource"} ({str x "classification"})"""))
                  list "workspace divergence" (divergence |> List.map (fun d -> "workspace divergence: " + d.ToString()))
                  "</section><section id=\"unknowns\" class=\"ef-section\" aria-label=\"Unknown effects\"><h3>Unknown effects</h3>"
                  list "unknown effects" (unknown |> List.map (fun s -> $"""step {str s "stepId"}: effect unknown; reconcile before retrying"""))
                  "</section><section id=\"containment\" class=\"ef-section\" aria-label=\"Containment\"><h3>Containment</h3><ul>"
                  items profile "restrictions" |> List.map (fun r -> $"""<li>{e (str r "dimension")}: {e (str r "status")}</li>""") |> String.concat ""
                  "</ul></section><section id=\"legal-actions\" class=\"ef-section\" aria-label=\"Legal actions\"><h3>Legal actions</h3>"
                  grid "Legal actions" [ "Transition"; "Availability"; "Who"; "Why not" ] (actions |> List.map actionRow)
                  actions
                  |> List.filter (fun a -> str a "available" = "true" && List.contains (str a "transition") [ "execution.block"; "execution.resume"; "execution.complete"; "execution.abandon"; "execution.rebind" ])
                  |> List.map (form id)
                  |> String.concat "\n"
                  "</section></main>" ])

    /// The executions section of a work item's detail page.
    let workSection (run: Run) (workItem: string) =
        match run [ "execution"; "list"; "--work-item"; workItem; "--json" ] |> Result.toOption |> Option.filter (fun r -> r.Exit = 0) |> Option.bind (fun r -> parse r.Out) with
        | Some(:? JsonArray as list) when list.Count > 0 ->
            list
            |> Seq.choose Option.ofObj
            |> Seq.map (fun x ->
                "<tr>"
                + cell "Execution" $"""<a href="/executions/{Html.segment (str x "executionId")}">{e (str x "executionId")}</a>"""
                + cell "Role" (e (str x "role"))
                + cell "State" (lozenge (str x "state") (str x "state"))
                + cell "Actor" $"""{e (str x["actor"] "id")} ({e (str x["actor"] "kind")})"""
                + cell "Containment" (e (str x "containment"))
                + "</tr>")
            |> List.ofSeq
            |> grid "Executions" [ "Execution"; "Role"; "State"; "Actor"; "Containment" ]
            |> sprintf "<section id=\"executions\" class=\"ef-section\" aria-label=\"Executions\"><h3>Executions</h3>%s</section>"
        | _ -> "<section id=\"executions\" class=\"ef-section\" aria-label=\"Executions\"><h3>Executions</h3><p>No executions recorded.</p></section>"

    let private failurePage (title: string) (message: string) =
        Html.page title $"<main id=\"main\" tabindex=\"-1\">{FormaMarkup.alert message}<p><a href=\"/\">&larr; Queue</a></p></main>"

    // -------------------------------------------------------------- routing

    /// Handles an execution route, or `None` for every other request.
    let tryHandle (scope: ControlPlaneScope) (run: Run) (request: HttpRequestData) : HttpResponseData option =
        let workFilter = HttpMessages.field "workItem" request.Query |> HttpMessages.nonBlank |> Option.map (fun w -> [ "--work-item"; w ]) |> Option.defaultValue []
        let back id query = HttpMessages.redirect ($"/executions/{Html.segment id}" + Html.queryString query)

        match request.Method, request.Segments with
        | "GET", [ "api"; "control-plane" ] -> Some(HttpMessages.jsonNode 200 (scopeDocument scope))
        | "GET", [ "api"; "executions" ] -> Some(api run ([ "execution"; "list"; "--json" ] @ workFilter))
        | "GET", [ "api"; "executions"; id ] -> Some(api run [ "execution"; "show"; id; "--json" ])
        | "GET", [ "api"; "executions"; id; "actions" ] -> Some(api run [ "execution"; "actions"; id; "--json" ])
        | "POST", [ "api"; "executions"; id; "transitions" ] ->
            match HttpMessages.parseBody request |> Result.map (transitionArgs id) with
            | Error message -> Some(HttpMessages.jsonError 400 message)
            | Ok None -> Some(HttpMessages.jsonError 400 "a transition request needs an action")
            | Ok(Some args) ->
                match api run args with
                | r when r.Status = 200 -> Some(api run [ "execution"; "show"; id; "--json" ])
                | refused -> Some refused
        | "GET", [ "executions"; id ] ->
            match run [ "execution"; "show"; id; "--json" ] with
            | Ok r when r.Exit = 0 -> parse r.Out |> Option.map (fun node -> HttpMessages.html 200 (renderExecution node request.Query))
            | Ok r -> Some(HttpMessages.html 404 (failurePage "Not found" (CliProcess.failureMessage r)))
            | Error message -> Some(HttpMessages.html 500 (failurePage "Error" message))
        | "POST", [ "executions"; id; "transitions" ] ->
            match HttpMessages.parseBody request |> Result.map (transitionArgs id) with
            | Ok(Some args) ->
                match run args with
                | Ok r when r.Exit = 0 -> Some(back id [ "notice", "Transition accepted." ])
                | Ok r -> Some(back id [ "error", CliProcess.failureMessage r ])
                | Error message -> Some(back id [ "error", message ])
            | Ok None -> Some(back id [ "error", "a transition request needs an action" ])
            | Error message -> Some(back id [ "error", message ])
        | _ -> None
