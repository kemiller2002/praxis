namespace Praxis.Cli

open System
open System.Text.Json.Nodes

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

    let private requirement (a: JsonNode) =
        if str a "actorRequirement" = "human-required" then "<strong class=\"human-required\">human required</strong>" else "<span class=\"muted\">agent or human</span>"

    let private actionRow (a: JsonNode) =
        let target = match str a "target" with "" -> "" | t -> " " + e t
        let available = str a "available" = "true"
        let state = if available then "<span class=\"status-pill status-ready\">legal</span>" else "<span class=\"status-pill status-blocked\">blocked</span>"
        let why = if available then "" else items a "reasons" |> List.map (fun r -> $"<li>{e (r.ToString())}</li>") |> String.concat "" |> sprintf "<ul class=\"reasons\">%s</ul>"
        $"""<tr><td><code>{e (str a "transition")}</code>{target}</td><td>{state}</td><td>{requirement a}</td><td>{why}</td></tr>"""

    let private form (id: string) (a: JsonNode) =
        let action = (str a "transition").Replace("execution.", "")
        let human = str a "actorRequirement" = "human-required"
        let target = $"/executions/{Html.segment id}/transitions"

        let identity =
            if human then
                "<label>Operator (recorded as a human actor) <input name=\"operator\" required /></label><label><input type=\"checkbox\" name=\"human\" value=\"yes\" required /> I am a human operator authorizing this transition</label>"
            else
                ""

        $"""<form method="post" action="{target}"><input type="hidden" name="action" value="{e action}" /><input name="reason" placeholder="reason" />{identity}<button type="submit">{e action}{(if human then " (human)" else "")}</button></form>"""

    let private stepRow (s: JsonNode) =
        let status = str s "status"

        let label =
            match status with
            | "mismatch" -> "receipt mismatch"
            | "indeterminate" -> "unknown effect"
            | "match" -> "receipt matches"
            | other -> other

        $"""<tr><td>{e (str s "stepId")}</td><td><span class="status-pill status-{e status}">{e label}</span></td><td>{e (str s "attempts")}</td></tr>"""

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
        let list title (xs: string list) = match xs with [] -> $"<p class=\"muted\">No {title}.</p>" | _ -> xs |> List.map (fun x -> $"<li>{e x}</li>") |> String.concat "" |> sprintf "<ul>%s</ul>"
        let profile = node["containmentProfile"]

        Html.page
            $"{id} · Praxis execution"
            (String.concat
                "\n"
                [ "<header><h1>Execution</h1><nav><a href=\"/\">Queue</a></nav></header><main>"
                  Html.flash query
                  $"""<p><a href="/work/{Html.segment (str node "workItem" |> fun w -> w.Substring(w.LastIndexOfAny [| ':'; '#' |] + 1))}">&larr; {e (str node "workItem")}</a></p>"""
                  $"<section id=\"execution\"><h2>{e id}</h2>"
                  $"""<p><span class="status-pill status-{e (str node "state")}">{e (str node "state")}</span> role <strong>{e (str node "role")}</strong></p>"""
                  blocked
                  $"""<dl><dt>Actor</dt><dd>{e (str actor "id")} ({e (str actor "kind")})</dd><dt>Execution host</dt><dd>{e (if host = "" then "not reported" else host)}</dd><dt>Baseline</dt><dd><code>{e (str node "baselineRevision")}</code></dd><dt>Candidate</dt><dd><code>{e (str node "candidateRevision")}</code></dd><dt>Evaluator</dt><dd><code>{e (str node["evaluator"] "fingerprint")}</code></dd><dt>Verification</dt><dd>{e (str node["verification"] "outcome")}</dd><dt>Containment</dt><dd>{e (str node "containment")} (source: {e (match str profile "source" with "" -> "no host report" | s -> s)})</dd></dl></section>"""
                  "<section id=\"receipts\"><h3>Step receipts</h3><table><thead><tr><th>Step</th><th>Receipt</th><th>Attempts</th></tr></thead><tbody>"
                  steps |> List.map stepRow |> String.concat "\n"
                  "</tbody></table></section>"
                  "<section id=\"obligations\"><h3>Obligations</h3>"
                  list "unresolved scope effects" (effects |> List.map (fun x -> $"""unresolved scope effect: {str x "resource"} ({str x "classification"})"""))
                  list "workspace divergence" (divergence |> List.map (fun d -> "workspace divergence: " + d.ToString()))
                  "</section><section id=\"unknowns\"><h3>Unknown effects</h3>"
                  list "unknown effects" (unknown |> List.map (fun s -> $"""step {str s "stepId"}: effect unknown; reconcile before retrying"""))
                  "</section><section id=\"containment\"><h3>Containment</h3><ul>"
                  items profile "restrictions" |> List.map (fun r -> $"""<li>{e (str r "dimension")}: {e (str r "status")}</li>""") |> String.concat ""
                  "</ul></section><section id=\"legal-actions\"><h3>Legal actions</h3><table><thead><tr><th>Transition</th><th>Availability</th><th>Who</th><th>Why not</th></tr></thead><tbody>"
                  actions |> List.map actionRow |> String.concat "\n"
                  "</tbody></table>"
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
            |> Seq.map (fun x -> $"""<tr><td><a href="/executions/{Html.segment (str x "executionId")}">{e (str x "executionId")}</a></td><td>{e (str x "role")}</td><td>{e (str x "state")}</td><td>{e (str x["actor"] "id")} ({e (str x["actor"] "kind")})</td><td>{e (str x "containment")}</td></tr>""")
            |> String.concat "\n"
            |> sprintf "<section id=\"executions\" aria-label=\"Executions\"><h3>Executions</h3><table><thead><tr><th>Execution</th><th>Role</th><th>State</th><th>Actor</th><th>Containment</th></tr></thead><tbody>\n%s\n</tbody></table></section>"
        | _ -> "<section id=\"executions\" aria-label=\"Executions\"><h3>Executions</h3><p class=\"muted\">No executions recorded.</p></section>"

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
            | Ok r -> Some(HttpMessages.html 404 (Html.page "Not found" $"<main><p class=\"error\" role=\"alert\">{e (CliProcess.failureMessage r)}</p></main>"))
            | Error message -> Some(HttpMessages.html 500 (Html.page "Error" $"<main><p class=\"error\" role=\"alert\">{e message}</p></main>"))
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
