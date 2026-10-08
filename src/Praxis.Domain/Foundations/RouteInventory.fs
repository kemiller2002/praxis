namespace Praxis.Domain.Foundations

open System
open System.Text.RegularExpressions

/// One parameter of an addressable view, as an `echelon.routes/v1` inventory
/// declares it. Enumerated fields stay text here: they are what the inventory
/// says, and `RouteInventory.problems` decides whether they are legal.
type RouteParameter =
    { Name: string
      Location: string
      Type: string
      Required: bool
      /// The default's canonical text, when one is declared.
      Default: string option
      Values: string list }

/// One destination: its full name, path pattern and parameters (parent first).
type RouteEntry =
    { Name: string
      Pattern: string
      Parameters: RouteParameter list }

/// A legacy URL that keeps working by redirecting to a current destination
/// (SAF-URL-7, Limen LCP-105). `Params` maps each of the target's parameters
/// to `{source}` (copied from a placeholder of `Pattern`) or a literal.
type LegacyRoute =
    { Name: string
      Pattern: string
      To: string
      Params: (string * string) list }

/// An application's route inventory (`.echelon/routes.json`, schema
/// `echelon.routes/v1`, SAF-URL-8). Limen's `Inventory.render` /
/// `renderRouteInventory` writes it (LCP-107); an application without Limen
/// writes the same document itself.
type RouteInventory =
    { Schema: string option
      Mode: string option
      Home: string option
      SignIn: string option
      NotFound: string option
      Routes: RouteEntry list
      Legacy: LegacyRoute list }

/// The rules a route inventory must satisfy. Pure: the caller reads the file,
/// this module only decides. The rules mirror Limen's route-table refusals
/// (LCP-090, LCP-091, LCP-109), so an inventory Limen renders passes.
[<RequireQualifiedAccess>]
module RouteInventory =
    [<Literal>]
    let SchemaId = "echelon.routes/v1"

    let modes = [ "hash"; "path" ]
    let locations = [ "path"; "query" ]
    let parameterTypes = [ "string"; "int"; "bool"; "date"; "month"; "enum"; "set" ]
    let pathParameterTypes = [ "string"; "int"; "date"; "month"; "enum" ]

    /// Parameter names reserved for credentials, exactly Limen's list
    /// (LCP-109): compared case-insensitively with `-` and `_` removed. URLs
    /// leak through history, referrers, logs, screenshots and chat, so none of
    /// these may be a URL parameter (SAF-URL-5).
    let reservedNames =
        [ "token"
          "accesstoken"
          "idtoken"
          "refreshtoken"
          "password"
          "passwd"
          "secret"
          "clientsecret"
          "apikey"
          "key"
          "session"
          "sessionid"
          "auth"
          "authorization"
          "code"
          "credential"
          "credentials" ]

    let isSupportedVersion (inventory: RouteInventory) = inventory.Schema = Some SchemaId

    let isReservedName (name: string) =
        let normalized = name.ToLowerInvariant().Replace("-", "").Replace("_", "")
        List.contains normalized reservedNames

    let private placeholder =
        Regex(@"\{\*?([A-Za-z][A-Za-z0-9_-]*)(?::[A-Za-z]+)?\}", RegexOptions.CultureInvariant)

    /// The `{name}`, `{name:type}` and wildcard `{*name}` placeholders of a
    /// path pattern, in order.
    let placeholders (pattern: string) =
        placeholder.Matches pattern |> Seq.map _.Groups[1].Value |> List.ofSeq

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (snd >> (<) 1) |> List.map fst

    let private show (allowed: string list) = String.Join("|", allowed)

    let private parameterProblems (owner: string) (parameter: RouteParameter) =
        let at = $"{owner} parameter '{parameter.Name}'"

        [ if String.IsNullOrWhiteSpace parameter.Name then
              yield $"{owner} has a parameter without a name"
          if not (List.contains parameter.Location locations) then
              yield $"{at}: 'in' must be {show locations} (SAF-URL-2)"
          if not (List.contains parameter.Type parameterTypes) then
              yield $"{at}: type must be one of {show parameterTypes} (SAF-URL-2)"
          elif parameter.Location = "path" && not (List.contains parameter.Type pathParameterTypes) then
              yield $"{at}: a path parameter's type must be one of {show pathParameterTypes} (SAF-URL-2)"
          if parameter.Location = "path" && parameter.Default.IsSome then
              yield $"{at}: a path parameter has no default; only query parameters may (SAF-URL-2)"
          if parameter.Type = "enum" && parameter.Values.IsEmpty then
              yield $"{at}: an enum parameter must list its values (SAF-URL-2)"
          for value in duplicates parameter.Values do
              yield $"{at}: value '{value}' is listed more than once (SAF-URL-2)"
          match parameter.Default with
          | Some value when parameter.Type = "enum" && not (List.contains value parameter.Values) ->
              yield $"{at}: default '{value}' is not one of its values (SAF-URL-2)"
          | _ -> ()
          if isReservedName parameter.Name then
              yield $"{at}: the name is reserved for credentials; secrets, tokens and sensitive data never go in a URL (SAF-URL-5)" ]

    let private shapeProblems (owner: string) (pattern: string) (parameters: RouteParameter list) =
        let declaredPath =
            parameters |> List.filter (fun item -> item.Location = "path") |> List.map _.Name |> Set.ofList

        let inPattern = placeholders pattern |> Set.ofList

        [ if not (pattern.StartsWith("/", StringComparison.Ordinal)) then
              yield $"{owner}: pattern must start with '/' (SAF-URL-2)"
          if pattern.IndexOfAny [| '?'; '#' |] >= 0 then
              yield $"{owner}: pattern is a path only; declare query parameters with \"in\": \"query\" (SAF-URL-2)"
          for missing in Set.difference inPattern declaredPath do
              yield $"{owner}: placeholder '{missing}' is not declared as a path parameter (SAF-URL-8)"
          for extra in Set.difference declaredPath inPattern do
              yield $"{owner}: path parameter '{extra}' does not appear in the pattern (SAF-URL-8)"
          for name in duplicates (parameters |> List.map _.Name) do
              yield $"{owner}: parameter '{name}' is declared more than once (SAF-URL-2)"
          yield! parameters |> List.collect (parameterProblems owner) ]

    let private routeProblems (route: RouteEntry) =
        let owner = $"route '{route.Name}'"

        [ if String.IsNullOrWhiteSpace route.Name then
              yield $"route with pattern '{route.Pattern}' has no name (SAF-URL-8)"
          yield! shapeProblems owner route.Pattern route.Parameters ]

    let private sourcePlaceholder = Regex(@"^\{([A-Za-z][A-Za-z0-9_-]*)\}$", RegexOptions.CultureInvariant)

    let private legacyProblems (names: Set<string>) (legacy: LegacyRoute) =
        let owner = $"legacy route '{legacy.Pattern}'"
        let captured = placeholders legacy.Pattern |> Set.ofList

        [ if not (legacy.Pattern.StartsWith("/", StringComparison.Ordinal)) then
              yield $"{owner}: pattern must start with '/' (SAF-URL-7)"
          if not (names.Contains legacy.To) then
              yield $"{owner}: 'to' must name a declared route (SAF-URL-7)"
          for target, source in legacy.Params do
              let matched = sourcePlaceholder.Match source

              if matched.Success && not (captured.Contains matched.Groups[1].Value) then
                  yield $"{owner}: parameter '{target}' copies '{source}', which the pattern does not capture (SAF-URL-7)" ]

    let private reference (names: Set<string>) (field: string) (value: string option) =
        match value with
        | Some name when not (names.Contains name) -> [ $"'{field}' names '{name}', which is not a declared route (SAF-URL-8)" ]
        | _ -> []

    /// Every reason the inventory fails the deep-linking contract; empty when
    /// it satisfies it. The schema id is judged separately
    /// (`isSupportedVersion`), as the inventory's pin. `staticHosting` is
    /// the application's declaration: a statically hosted application uses
    /// hash mode (SAF-URL-6, DF-LIMEN-2026-0006).
    let problems (staticHosting: bool) (inventory: RouteInventory) : string list =
        let names = inventory.Routes |> List.map _.Name |> Set.ofList

        [ if not (inventory.Mode |> Option.exists (fun mode -> List.contains mode modes)) then
              yield $"mode must be {show modes} (SAF-URL-6)"
          elif staticHosting && inventory.Mode <> Some "hash" then
              yield "a statically hosted application uses hash mode so a reloaded deep link never 404s (SAF-URL-6, DF-LIMEN-2026-0006)"
          if inventory.Routes.IsEmpty then
              yield "the inventory lists no addressable views (SAF-URL-8)"
          if inventory.Home.IsNone then
              yield "'home' must name the home route (SAF-URL-4)"
          yield! reference names "home" inventory.Home
          yield! reference names "signIn" inventory.SignIn
          yield! reference names "notFound" inventory.NotFound
          for name in duplicates (inventory.Routes |> List.map _.Name) do
              yield $"route name '{name}' is declared more than once (SAF-URL-8)"
          yield! inventory.Routes |> List.collect routeProblems
          yield! inventory.Legacy |> List.collect (legacyProblems names) ]

/// Semantic-version ordering for release gates. Pure.
[<RequireQualifiedAccess>]
module ReleaseVersion =
    /// (major, minor, patch, isRelease) for "X.Y.Z" or "X.Y.Z-pre"; a leading
    /// "v" is accepted. A prerelease orders before its release.
    let tryParse (text: string) =
        let core, isRelease =
            let trimmed = text.Trim().TrimStart('v', 'V')

            match trimmed.IndexOfAny [| '-'; '+' |] with
            | -1 -> trimmed, true
            | index -> trimmed.Substring(0, index), trimmed.[index] = '+'

        match core.Split('.') |> Array.map Int32.TryParse with
        | [| (true, major); (true, minor); (true, patch) |] -> Some(major, minor, patch, isRelease)
        | _ -> None

    /// Some true when `version` is at or after `minimum`; None when either
    /// does not parse.
    let atLeast (minimum: string) (version: string) =
        match tryParse minimum, tryParse version with
        | Some floor, Some candidate -> Some(candidate >= floor)
        | _ -> None

/// Whether an application must route through Limen's routing / URL-state
/// module yet (SAF-URL-9). Pure.
[<RequireQualifiedAccess>]
module LimenRouting =
    /// The Limen release that ships the routing / URL-state module (Limen
    /// LCP-110: `@echelon-foundry/limen/routing` and
    /// `EchelonFoundry.Limen.Routing`, published at 0.9.0). An application on
    /// an older Limen is pending, never failed. A declaration may name another release with
    /// `routing.limenRoutingVersion`.
    let firstRelease = "0.9.0"

    /// What an application must reference to count as using Limen routing:
    /// the npm subpath, the F# package and the F# namespace (Limen
    /// DF-LIMEN-2026-0006). A declaration may name another module with
    /// `routing.limenRoutingModule`.
    let defaultModules =
        [ "@echelon-foundry/limen/routing"; "EchelonFoundry.Limen.Routing"; "Limen.Routing" ]

    type Availability =
        /// The application does not use Limen, so Limen routing is not its router.
        | NotApplicable of reason: string
        /// The installed Limen does not ship routing yet, or its version is unknown.
        | Pending of reason: string
        /// The installed Limen ships routing; the application must use it.
        | Available of release: string

    let availability (limenRequired: bool) (installedVersion: string option) (declaredFirstRelease: string option) =
        let release = declaredFirstRelease |> Option.defaultValue firstRelease

        if not limenRequired then
            NotApplicable "the application does not declare Limen"
        else
            match installedVersion with
            | None -> Pending $"the installed Limen version is unknown; routing ships in Limen {release}"
            | Some installed ->
                match ReleaseVersion.atLeast release installed with
                | Some true -> Available release
                | Some false -> Pending $"installed Limen {installed} predates the routing module (Limen {release})"
                | None -> Pending $"cannot compare installed Limen '{installed}' with routing release '{release}'"
