namespace Praxis.Application.Web

open System
open Limen.Routing

/// What the queue (web) and the aggregated work list (hub) show: the tag and
/// status filters (SAF-URL-2).
type WorkFilter =
    { Tags: string list
      Status: string option }

/// Every addressable view of `praxis web serve` (SAF-URL-8).
type WebView =
    | Queue of WorkFilter
    | WorkItem of id: string
    | Validation
    | Execution of id: string

/// Every addressable view of `praxis hub serve`.
type HubView = HubWork of repository: string option * filter: WorkFilter

/// What a GET page request is, decided before anything is rendered.
type PageOutcome<'View> =
    /// The location names this view canonically: render it.
    | Show of 'View
    /// The location names a view, but not canonically (an empty form field, a
    /// repeated or comma-split tag, an undeclared parameter, the `/index.html`
    /// alias): answer a redirect to `location`, which replaces the history
    /// entry, so the address bar always holds the canonical URL (SAF-URL-2).
    | Canonical of location: string
    /// Not one of the application's views: render the typed reason with a way
    /// back, never a blank page or another view (SAF-URL-4).
    | Refused of RouteError

/// A validated route table and the typed codec over it.
type Routing<'View> =
    { Table: RouteTable
      Codec: RouteCodec<'View> }

/// The URL state of Praxis's own server-rendered UIs (SAF-URL-1..10), through
/// Limen's pure routing library (DF-ROS-2026-A057). Pure: the CLI adapter
/// supplies the request's path and query and renders the outcome.
[<RequireQualifiedAccess>]
module UrlState =
    let statuses = [ "captured"; "ready"; "blocked"; "active"; "complete"; "abandoned" ]

    /// The server answers every path itself, so routes are real paths.
    let mode = LocationMode.Path

    /// Praxis's UIs have no interface guards; the CLI refuses what may not happen.
    let private guard = Router.allowAll

    let private filterParams =
        [ QueryParam.optional "tag" (ParamType.Set [])
          QueryParam.optional "status" (ParamType.Enum statuses) ]

    let private filterQuery (filter: WorkFilter) =
        Map.ofList
            [ if not filter.Tags.IsEmpty then
                  yield "tag", Value.Members filter.Tags
              match filter.Status with
              | Some status -> yield "status", Value.Text status
              | None -> () ]

    let private filterOf (query: Map<string, Value>) =
        { Tags =
            match query.TryFind "tag" with
            | Some(Value.Members tags) -> tags
            | _ -> []
          Status =
            match query.TryFind "status" with
            | Some(Value.Text status) -> Some status
            | _ -> None }

    let private text (matched: Match) name =
        matched.Chain
        |> List.tryPick (fun level ->
            match level.Params.TryFind name with
            | Some(Value.Text value) -> Some value
            | _ -> None)

    let private roles home =
        { Home = home
          SignIn = None
          NotFound = Some "notFound" }

    let private notFound = Route.create "notFound" "{*rest}"

    let private webToTarget (view: WebView) : Target =
        match view with
        | Queue filter -> { Route = "queue"; Params = Map.empty; Query = filterQuery filter }
        | WorkItem id -> { Route = "work.item"; Params = Map [ "id", Value.Text id ]; Query = Map.empty }
        | Validation -> { Route = "validate"; Params = Map.empty; Query = Map.empty }
        | Execution id -> { Route = "execution"; Params = Map [ "id", Value.Text id ]; Query = Map.empty }

    let private webOfMatch (matched: Match) : Result<WebView, string> =
        match matched.Route, text matched "id" with
        | "queue", _ -> Ok(Queue(filterOf matched.Query))
        | "work.item", Some id -> Ok(WorkItem id)
        | "validate", _ -> Ok Validation
        | "execution", Some id -> Ok(Execution id)
        | route, _ -> Error $"unmapped route {route}"

    /// The web UI's URL space.
    let web: Result<Routing<WebView>, DefinitionError list> =
        RouteTable.define
            [ { Route.create "queue" "" with Query = filterParams }
              { Route.create "work" "work" with
                  Children = [ { Route.create "item" "{id}" with Requires = [ "work-item" ] } ] }
              Route.create "validate" "validate"
              { Route.create "execution" "executions/{id}" with Requires = [ "execution" ] }
              notFound ]
            [ { Path = "index.html"; To = "queue"; Params = [] } ]
            (roles "queue")
        |> Result.map (fun table ->
            { Table = table
              Codec = RouteCodec.create table webToTarget webOfMatch })

    let private hubToTarget (HubWork(repository, filter)) : Target =
        let query = filterQuery filter

        { Route = "work"
          Params = Map.empty
          Query =
            match repository with
            | Some id -> query |> Map.add "repo" (Value.Text id)
            | None -> query }

    let private hubOfMatch (matched: Match) : Result<HubView, string> =
        match matched.Route with
        | "work" ->
            let repository =
                match matched.Query.TryFind "repo" with
                | Some(Value.Text id) -> Some id
                | _ -> None

            Ok(HubWork(repository, filterOf matched.Query))
        | route -> Error $"unmapped route {route}"

    /// The hub's URL space.
    let hub: Result<Routing<HubView>, DefinitionError list> =
        RouteTable.define
            [ { Route.create "work" "" with Query = QueryParam.optional "repo" ParamType.String :: filterParams }
              notFound ]
            [ { Path = "index.html"; To = "work"; Params = [] } ]
            (roles "work")
        |> Result.map (fun table ->
            { Table = table
              Codec = RouteCodec.create table hubToTarget hubOfMatch })

    /// HTML GET forms submit every field, empty or not, and the tag field is
    /// free text. Before resolving: an empty value means "not set", and every
    /// `tag` value (repeated, or comma-separated with spaces) becomes one
    /// sorted, de-duplicated set. Pure.
    let normalize (query: (string * string) list) =
        let present = query |> List.filter (fun (_, value) -> not (String.IsNullOrWhiteSpace value))

        let tags =
            present
            |> List.filter (fst >> (=) "tag")
            |> List.collect (fun (_, value) -> value.Split(',') |> List.ofArray |> List.map _.Trim())
            |> List.filter (String.IsNullOrEmpty >> not)
            |> List.distinct
            |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

        let others = present |> List.filter (fst >> (<>) "tag")
        (if tags.IsEmpty then [] else [ "tag", String.Join(",", tags) ]) @ others

    let private encode (pairs: (string * string) list) =
        match pairs with
        | [] -> ""
        | _ ->
            // A set's members are joined by a literal ","; an encoded comma
            // would stay inside one member. Tags never contain one (normalize).
            let valueText name (value: string) =
                if name = "tag" then String.Join(",", value.Split(',') |> Array.map Uri.EscapeDataString)
                else Uri.EscapeDataString value

            "?" + String.Join("&", pairs |> List.map (fun (name, value) -> $"{Uri.EscapeDataString name}={valueText name value}"))

    /// A location's decoded query pairs, for comparing two locations by
    /// meaning rather than by their percent-encoding.
    let private pairsOf (location: string) =
        match location.IndexOf '?' with
        | -1 -> []
        | index ->
            location.Substring(index + 1).Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
            |> List.map (fun pair ->
                match pair.IndexOf '=' with
                | -1 -> Uri.UnescapeDataString pair, ""
                | equals -> Uri.UnescapeDataString(pair.Substring(0, equals)), Uri.UnescapeDataString(pair.Substring(equals + 1)))

    let private pathOf (location: string) =
        match location.IndexOf '?' with
        | -1 -> location
        | index -> location.Substring(0, index)

    /// A GET page request (its percent-decoded path and decoded query pairs)
    /// as an outcome. Never loops: a canonical location resolves to Show.
    let resolve (routing: Result<Routing<'View>, DefinitionError list>) (path: string) (query: (string * string) list) : PageOutcome<'View> =
        match routing with
        | Error problems -> Refused(RouteError.Unmapped("table", $"%A{problems}"))
        | Ok routing ->
            let location = (if path = "" then "/" else path) + encode (normalize query)

            match RouteCodec.parse routing.Codec guard location with
            | Error problem -> Refused problem
            | Ok view ->
                match RouteCodec.format routing.Codec view with
                | Ok canonical when pathOf canonical = (if path = "" then "/" else path) && pairsOf canonical = query -> Show view
                | Ok canonical -> Canonical canonical
                | Error _ -> Show view

    /// A view's one canonical location: the route formatter links use.
    let format (routing: Result<Routing<'View>, DefinitionError list>) (view: 'View) : string option =
        routing |> Result.toOption |> Option.bind (fun routing -> RouteCodec.format routing.Codec view |> Result.toOption)

    /// The absolute URL "Link to this view" carries (SAF-URL-10).
    let share (host: string) (location: string) =
        Link.share mode { Origin = $"http://{host}"; Path = ""; Query = ""; Hash = "" } location

    /// `.echelon/routes.json` for a surface, byte for byte (SAF-URL-8).
    let inventory (routing: Result<Routing<'View>, DefinitionError list>) =
        routing |> Result.map (fun routing -> Inventory.render mode routing.Table)

    /// The HTTP status and the title a refusal renders with (SAF-URL-4).
    let refusal (problem: RouteError) =
        match problem with
        | RouteError.NotFound -> 404, "Not found", "No page has this address."
        | RouteError.NotPermitted route -> 403, "Not permitted", $"This page ({route}) is not available to you."
        | RouteError.Invalid(_, parameter, value, expected) -> 400, "Invalid link", $"'{value}' is not a valid {parameter}: expected {expected}."
        | RouteError.Malformed part -> 400, "Invalid link", $"The link's {part} is malformed."
        | RouteError.RedirectLoop _ -> 500, "Invalid link", "This link redirects to itself."
        | RouteError.Unmapped(route, _) -> 500, "Unavailable", $"The page {route} could not be shown."

    // ------------------------------------------------------------------
    // Post/redirect/get feedback is transient UI state, so it travels in a
    // short-lived cookie, never in the URL (SAF-URL-5).
    // ------------------------------------------------------------------

    [<Literal>]
    let FlashCookie = "praxis-flash"

    /// The Set-Cookie value that carries one notice or error to the next page.
    let flash (kind: string) (message: string) =
        let value = Uri.EscapeDataString(kind + ":" + message)
        $"{FlashCookie}={value}; Path=/; Max-Age=60; HttpOnly; SameSite=Strict"

    /// The Set-Cookie value that clears it once shown.
    let clearFlash = $"{FlashCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"

    /// The notice or error a Cookie header carries, as (kind, message).
    let readFlash (cookieHeader: string option) =
        cookieHeader
        |> Option.bind (fun header ->
            header.Split(';')
            |> Array.map _.Trim()
            |> Array.tryPick (fun part ->
                if part.StartsWith(FlashCookie + "=", StringComparison.Ordinal) then
                    let value = Uri.UnescapeDataString(part.Substring(FlashCookie.Length + 1))

                    match value.IndexOf ':' with
                    | -1 -> None
                    | index -> Some(value.Substring(0, index), value.Substring(index + 1))
                else
                    None))

    /// A post/redirect/get target that still names its feedback as a
    /// `notice` or `error` query parameter, split into the clean location and
    /// that feedback, which then travels in the flash cookie instead.
    let splitFlash (location: string) =
        let pairs = pairsOf location
        let feedback = pairs |> List.tryFind (fun (name, _) -> name = "notice" || name = "error")
        let rest = pairs |> List.filter (fun (name, _) -> name <> "notice" && name <> "error")
        pathOf location + encode rest, feedback

    /// The location the request named, re-encoded: what "Link to this view"
    /// shares when the page is canonical.
    let locationOf (path: string) (query: (string * string) list) = (if path = "" then "/" else path) + encode query
