namespace Praxis.Infrastructure.Foundations

open System
open System.Text.Json
open Praxis.Domain.Foundations

/// What an application's foundations declaration says about routing.
type RoutingDeclaration =
    { Inventories: string list
      Hosting: string option
      LimenRoutingVersion: string option
      LimenRoutingModule: string option }

/// One routing finding: its code suffix (ECHELON-FND-ROUTING-<suffix>),
/// severity ("error" or "info"), message and remediation.
type RoutingFinding =
    { Suffix: string
      Severity: string
      Message: string
      Remediation: string }

/// The routing capability as verified: the four standard facts, details and
/// findings.
type RoutingObservation =
    { Installed: bool
      Pinned: bool
      Used: bool
      EvidencePresent: bool
      Details: string list
      Findings: RoutingFinding list }

/// SAF-URL-8 (each declared inventory exists, is echelon.routes/v1 and
/// satisfies the contract) and SAF-URL-9 (Limen routing is used once the
/// installed Limen ships it). A pending Limen routing check is an info
/// finding (005), never PASS and never FAIL.
[<RequireQualifiedAccess>]
module RoutingFoundation =
    let none =
        { Inventories = []
          Hosting = None
          LimenRoutingVersion = None
          LimenRoutingModule = None }

    let private property (name: string) (element: JsonElement) =
        match element.ValueKind, element.TryGetProperty name with
        | JsonValueKind.Object, (true, value) -> Some value
        | _ -> None

    let private text name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.bind (fun value -> value.GetString() |> Option.ofObj)

    /// The routing settings of a capability object in `.echelon/foundations.json`;
    /// `inventory` is one path or a list of paths.
    let declaration (element: JsonElement) =
        let inventories =
            match property "inventory" element with
            | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.toList
            | Some value when value.ValueKind = JsonValueKind.Array ->
                value.EnumerateArray()
                |> Seq.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)
                |> List.ofSeq
            | _ -> []

        { Inventories = inventories
          Hosting = text "hosting" element
          LimenRoutingVersion = text "limenRoutingVersion" element
          LimenRoutingModule = text "limenRoutingModule" element }

    type private InventoryCheck =
        { Path: string
          Exists: bool
          Supported: bool
          Routes: int
          Problems: string list }

    let private finding suffix severity message remediation =
        { Suffix = suffix
          Severity = severity
          Message = message
          Remediation = remediation }

    let private check (root: string) (staticHosting: bool) (path: string) =
        let exists, inventory = RouteInventoryReader.tryRead root path

        { Path = path
          Exists = exists
          Supported = inventory |> Option.exists RouteInventory.isSupportedVersion
          Routes = inventory |> Option.map (_.Routes >> List.length) |> Option.defaultValue 0
          Problems =
            match exists, inventory with
            | true, None -> [ $"{path} is not a JSON object" ]
            | _, Some parsed -> RouteInventory.problems staticHosting parsed
            | false, None -> [] }

    let private inventoryFindings (inventory: InventoryCheck) =
        [ if not inventory.Exists then
              yield
                  finding
                      "001"
                      "error"
                      $"routing is required but the route inventory {inventory.Path} is missing (SAF-URL-8)."
                      "Publish the route inventory: Limen's Inventory.render / renderRouteInventory writes it; without Limen, start from templates/application-routes.json (schema schemas/echelon-routes-v1.schema.json)."
          if inventory.Exists && not inventory.Supported then
              yield
                  finding
                      "002"
                      "error"
                      $"the route inventory {inventory.Path} does not declare \"schema\": \"{RouteInventory.SchemaId}\"."
                      $"Declare \"schema\": \"{RouteInventory.SchemaId}\" and validate against schemas/echelon-routes-v1.schema.json."
          if inventory.Exists && not inventory.Problems.IsEmpty then
              yield
                  finding
                      "004"
                      "error"
                      ("the route inventory " + inventory.Path + " breaks the deep-linking contract: " + String.Join("; ", inventory.Problems) + ".")
                      "Fix each listed problem; requirements/SHARED-APPLICATION-FOUNDATIONS.md (SAF-URL-1..10) states the rules." ]

    /// `uses names` answers whether application source or project files
    /// reference any of `names`; `limenVersion` is the installed Limen
    /// version, when known.
    let verify (root: string) (uses: string list -> bool) (limenRequired: bool) (limenVersion: string option) (declaration: RoutingDeclaration) =
        let paths =
            if declaration.Inventories.IsEmpty then [ RouteInventoryReader.DefaultRelativePath ] else declaration.Inventories

        // Static hosting is the default: most Echelon applications are GitHub Pages sites.
        let staticHosting = declaration.Hosting |> Option.forall ((<>) "server")
        let checks = paths |> List.map (check root staticHosting)

        let modules =
            declaration.LimenRoutingModule |> Option.map List.singleton |> Option.defaultValue LimenRouting.defaultModules

        let names = String.Join(" or ", modules)

        let used, usageDetail, usageFindings =
            match LimenRouting.availability limenRequired limenVersion declaration.LimenRoutingVersion with
            | LimenRouting.NotApplicable reason -> true, $"limen routing: N/A ({reason})", []
            | LimenRouting.Pending reason ->
                true,
                $"limen routing: pending ({reason})",
                [ finding
                      "005"
                      "info"
                      $"Limen routing usage is pending: {reason}. The usage check (SAF-URL-9) is neither passed nor failed."
                      $"No action until the application can install Limen {LimenRouting.firstRelease} or later; then parse and format routes with {names}." ]
            | LimenRouting.Available release ->
                let found = uses modules
                let state = if found then "used" else "not used"

                found,
                $"limen routing: required since Limen {release}; {names} {state}",
                [ if not found then
                      yield
                          finding
                              "003"
                              "error"
                              $"Limen {release} ships routing, but this application does not use it (SAF-URL-9)."
                              $"Parse and format routes with Limen's routing module ({names}) instead of a local router." ]

        let hosting = if staticHosting then "static" else "server"

        { Installed = checks |> List.forall _.Exists
          Pinned = checks |> List.forall (fun inventory -> inventory.Exists && inventory.Supported)
          Used = used
          EvidencePresent = checks |> List.forall (fun inventory -> inventory.Exists && inventory.Supported && inventory.Problems.IsEmpty)
          Details =
            [ for inventory in checks do
                  let missing = if inventory.Exists then String.Empty else " (missing)"
                  yield $"inventory: {inventory.Path}{missing}, routes: {inventory.Routes}"
                  yield! inventory.Problems |> List.map (sprintf "problem: %s")
              yield $"hosting: {hosting}"
              yield usageDetail ]
          Findings = (checks |> List.collect inventoryFindings) @ usageFindings }
