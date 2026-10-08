namespace Praxis.Tests

open System
open System.IO
open Praxis.Cli
open Praxis.Domain.Foundations
open Praxis.Infrastructure.Foundations

/// The routing foundation (URL-addressable state, SAF-URL-1..10): the
/// `echelon.routes/v1` route inventory, and Limen routing use once the
/// installed Limen ships it (WI-0077).
[<RequireQualifiedAccess>]
module RoutingFoundationsTests =
    let private withTemp action =
        let root = Path.Combine(Path.GetTempPath(), $"praxis-routing-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore

        try
            action root
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    let private write (root: string) (relativePath: string) (content: string) =
        let path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    let private declaration (capabilities: string) =
        $"""{{ "schemaVersion": 1, "application": "Example", "capabilities": {{ {capabilities} }} }}"""

    let private routingOnly = declaration "\"routing\": { \"required\": true }"

    /// An inventory as Limen's Inventory.render writes it (sorted keys).
    let private validInventory =
        """{
  "home": "home",
  "legacy": [
    { "params": [ { "in": "path", "name": "id", "required": true, "type": "int" } ], "pattern": "/cases/{id:int}", "to": "investigations.investigation" }
  ],
  "mode": "hash",
  "notFound": "notFound",
  "routes": [
    { "guard": null, "name": "home", "params": [], "pattern": "/", "requires": [], "returnTarget": true },
    {
      "guard": null,
      "name": "investigations.investigation",
      "params": [
        { "default": null, "in": "path", "name": "id", "required": true, "type": "int" },
        { "default": "evidence", "in": "query", "name": "tab", "required": false, "type": "enum", "values": ["branches", "evidence"] },
        { "default": null, "in": "query", "name": "on", "required": false, "type": "date" },
        { "default": [], "in": "query", "name": "status", "required": false, "type": "set", "values": ["open", "overdue"] }
      ],
      "pattern": "/investigations/{id:int}",
      "requires": ["investigation"],
      "returnTarget": true
    },
    { "guard": null, "name": "notFound", "params": [ { "in": "path", "name": "rest", "required": true, "type": "string" } ], "pattern": "/{*rest}", "requires": [], "returnTarget": false }
  ],
  "schema": "echelon.routes/v1",
  "signIn": null
}
"""

    let private report root =
        match Foundations.verify root with
        | Ok value -> value
        | Error message -> failwith message

    let private routing root =
        (report root).Capabilities |> List.find (fun item -> item.Name = "routing")

    let private codes root = (report root).Findings |> List.map _.Code

    let private inventory text =
        match RouteInventoryReader.parse text with
        | Some value -> value
        | None -> failwith "inventory did not parse"

    /// A repository requiring Limen (installed `installed`) and routing.
    let private limenApplication root (installed: string) (routingRelease: string option) =
        let release =
            routingRelease |> Option.map (fun value -> $", \"limenRoutingVersion\": \"{value}\"") |> Option.defaultValue ""

        write
            root
            ".echelon/foundations.json"
            (declaration $"\"limen\": {{ \"required\": true, \"version\": \"{installed}\" }}, \"routing\": {{ \"required\": true{release} }}")

        write root ".echelon/limen.json" $"{{ \"package\": \"@echelon-foundry/limen\", \"installedVersion\": \"{installed}\" }}"
        write root ".echelon/routes.json" validInventory
        write root "src/main.ts" "import { boot } from \"@echelon-foundry/limen\";\nboot(WebAssembly);"

    let rec private praxisRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, "templates")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "Praxis repository root not found"
            | parent -> praxisRoot parent

    let tests =
        [ { Name = "routing foundation: a missing route inventory is ECHELON-FND-ROUTING-001 (SAF-URL-8)"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" routingOnly
                      Assert.equal [ "ECHELON-FND-ROUTING-001" ] (codes root)
                      Assert.isTrue (not (report root).Passed) "a required routing foundation without an inventory fails") }

          { Name = "routing foundation: a Limen-rendered inventory passes, and Limen routing is N/A without Limen"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" routingOnly
                      write root ".echelon/routes.json" validInventory
                      Assert.empty (report root).Findings
                      let capability = routing root
                      Assert.isTrue capability.Passed "a valid inventory passes"
                      Assert.isTrue (capability.Details |> List.exists (fun detail -> detail.StartsWith("limen routing: N/A", StringComparison.Ordinal))) $"{capability.Details}") }

          { Name = "routing foundation: every declared inventory is checked"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" (declaration "\"routing\": { \"required\": true, \"inventory\": [\".echelon/routes.json\", \".echelon/routes.admin.json\"] }")
                      write root ".echelon/routes.json" validInventory
                      Assert.equal [ "ECHELON-FND-ROUTING-001" ] (codes root)
                      write root ".echelon/routes.admin.json" validInventory
                      Assert.empty (codes root)) }

          { Name = "routing foundation: an inventory that is not echelon.routes/v1 is ECHELON-FND-ROUTING-002"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" routingOnly
                      write root ".echelon/routes.json" (validInventory.Replace("echelon.routes/v1", "echelon.routes/v2"))
                      Assert.equal [ "ECHELON-FND-ROUTING-002" ] (codes root)) }

          { Name = "routing foundation: static hosting needs hash mode (SAF-URL-6, DF-LIMEN-2026-0006); server hosting may use path"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" routingOnly
                      write root ".echelon/routes.json" (validInventory.Replace("\"mode\": \"hash\"", "\"mode\": \"path\""))
                      let finding = (report root).Findings |> List.exactlyOne
                      Assert.equal "ECHELON-FND-ROUTING-004" finding.Code
                      Assert.isTrue (finding.Message.Contains "DF-LIMEN-2026-0006") finding.Message
                      write root ".echelon/foundations.json" (declaration "\"routing\": { \"required\": true, \"hosting\": \"server\" }")
                      Assert.empty (codes root)) }

          { Name = "routing foundation: a credential parameter is ECHELON-FND-ROUTING-004 (SAF-URL-5)"
            Run =
              fun () ->
                  withTemp (fun root ->
                      write root ".echelon/foundations.json" routingOnly
                      write root ".echelon/routes.json" (validInventory.Replace("\"name\": \"on\"", "\"name\": \"access_token\""))
                      let finding = (report root).Findings |> List.exactlyOne
                      Assert.equal "ECHELON-FND-ROUTING-004" finding.Code
                      Assert.isTrue (finding.Message.Contains "SAF-URL-5") finding.Message) }

          { Name = "routing foundation: before Limen 0.9.0 routing usage is a pending note, not a failure"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenApplication root "0.8.0" None
                      let result = report root
                      Assert.isTrue result.Passed $"pending is not a failure: {result.Findings}"
                      let note = result.Findings |> List.exactlyOne
                      Assert.equal "ECHELON-FND-ROUTING-005" note.Code
                      Assert.equal "info" note.Severity
                      Assert.isTrue ((Foundations.renderText result).Contains "NOTE ECHELON-FND-ROUTING-005") "the text report marks it as a note"
                      Assert.isTrue ((Foundations.renderText result).Contains "application foundations passed (1 note(s))") "the summary counts the note") }

          { Name = "routing foundation: on Limen 0.9.0, not using its routing module is ECHELON-FND-ROUTING-003 (SAF-URL-9)"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenApplication root "0.9.0" None
                      Assert.equal [ "ECHELON-FND-ROUTING-003" ] (codes root)
                      write root "src/routes.ts" "import { defineRoutes, createRouteCodec } from \"@echelon-foundry/limen/routing\";"
                      Assert.empty (codes root)
                      Assert.isTrue (routing root).Used "the routing module is used") }

          { Name = "routing foundation: the F# EchelonFoundry.Limen.Routing package counts as Limen routing use"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenApplication root "0.9.0" None
                      write root "src/App/App.fsproj" "<Project><ItemGroup><PackageReference Include=\"EchelonFoundry.Limen.Routing\" Version=\"0.9.0\" /></ItemGroup></Project>"
                      Assert.empty (codes root)) }

          { Name = "routing foundation: limenRoutingVersion and limenRoutingModule override the defaults"
            Run =
              fun () ->
                  withTemp (fun root ->
                      limenApplication root "0.9.0" (Some "0.10.0")
                      Assert.equal [ "ECHELON-FND-ROUTING-005" ] (codes root)
                      limenApplication root "0.10.0" (Some "0.10.0")
                      let declared = File.ReadAllText(Path.Combine(root, ".echelon", "foundations.json"))
                      write root ".echelon/foundations.json" (declared.Replace("\"limenRoutingVersion\"", "\"limenRoutingModule\": \"@echelon-foundry/limen/url-state\", \"limenRoutingVersion\""))
                      write root "src/routes.ts" "import { navigate } from \"@echelon-foundry/limen/routing\";"
                      Assert.equal [ "ECHELON-FND-ROUTING-003" ] (codes root)
                      write root "src/routes.ts" "import { navigate } from \"@echelon-foundry/limen/url-state\";"
                      Assert.empty (codes root)) }

          { Name = "route inventory rules: each SAF-URL rule an inventory can show is enforced"
            Run =
              fun () ->
                  let valid = inventory validInventory
                  Assert.empty (RouteInventory.problems true valid)

                  let single edit =
                      match RouteInventory.problems true (edit valid) with
                      | [ problem ] -> problem
                      | other -> failwith $"expected one problem, got {other}"

                  let route = valid.Routes[1]
                  let withRoute changed (i: RouteInventory) = { i with Routes = [ i.Routes[0]; changed; i.Routes[2] ] }
                  let withParameter index changed = withRoute { route with Parameters = route.Parameters |> List.mapi (fun i p -> if i = index then changed else p) }
                  let contains (text: string) (problem: string) = Assert.isTrue (problem.Contains text) problem

                  single (fun i -> { i with Mode = Some "query" }) |> contains "mode must be"
                  single (fun i -> { i with Home = Some "nowhere" }) |> contains "'home' names 'nowhere'"
                  single (fun i -> { i with Home = None }) |> contains "'home' must name"
                  single (fun i -> { i with SignIn = Some "signIn" }) |> contains "'signIn'"
                  single (fun i -> { i with Routes = i.Routes @ [ i.Routes[0] ] }) |> contains "route name 'home'"
                  single (withRoute { route with Pattern = "/investigations/{id:int}?tab=x" }) |> contains "path only"
                  Assert.equal 2 (RouteInventory.problems true (withRoute { route with Pattern = "/investigations/{caseId}" } valid)).Length
                  single (withParameter 1 { route.Parameters[1] with Default = Some "timeline" }) |> contains "not one of its values"
                  single (withParameter 1 { route.Parameters[1] with Values = []; Default = None }) |> contains "must list its values"
                  single (withParameter 3 { route.Parameters[3] with Values = [ "open"; "open" ] }) |> contains "listed more than once"
                  single (withParameter 1 { route.Parameters[1] with Location = "fragment" }) |> contains "'in' must be"
                  single (withParameter 1 { route.Parameters[1] with Type = "period" }) |> contains "type must be one of"
                  single (withParameter 0 { route.Parameters[0] with Type = "set" }) |> contains "path parameter's type"
                  single (withParameter 0 { route.Parameters[0] with Required = false }) |> contains "always required"
                  single (withParameter 0 { route.Parameters[0] with Default = Some "1" }) |> contains "path parameter has no default"
                  single (withParameter 2 { route.Parameters[2] with Name = "Client-Secret" }) |> contains "SAF-URL-5"
                  single (fun i -> { i with Legacy = [ { i.Legacy[0] with To = "gone" } ] }) |> contains "'to' must name a declared route"
                  single (fun i -> { i with Legacy = [ { i.Legacy[0] with Parameters = [] } ] }) |> contains "placeholder 'id'"

                  Assert.isTrue
                      (RouteInventory.problems true { valid with Routes = []; Home = None; NotFound = None; Legacy = [] }
                       |> List.exists (fun p -> p.Contains "no addressable views"))
                      "empty inventory" }

          { Name = "route inventory rules: reserved names are exactly Limen's LCP-109 list, ignoring case, '-' and '_'"
            Run =
              fun () ->
                  for name in [ "token"; "access_token"; "ID-Token"; "refreshToken"; "password"; "passwd"; "secret"; "client_secret"; "API_KEY"; "key"; "Session"; "session_id"; "auth"; "Authorization"; "code"; "credential"; "credentials" ] do
                      Assert.isTrue (RouteInventory.isReservedName name) $"{name} is reserved"

                  for name in [ "id"; "investigationId"; "tab"; "month"; "sort"; "q"; "page"; "branch"; "keyword"; "zipcode"; "author" ] do
                      Assert.isTrue (not (RouteInventory.isReservedName name)) $"{name} is not reserved" }

          { Name = "route inventory rules: placeholders round-trip through generated patterns"
            Run =
              fun () ->
                  let random = Random 20261008
                  let letters = "abcdefghijklmnopqrstuvwxyz"
                  let types = [| ""; ":int"; ":date"; ":month" |]

                  for _ in 1..200 do
                      let names =
                          List.init (random.Next(0, 5)) (fun index ->
                              String(Array.init (random.Next(1, 8)) (fun _ -> letters[random.Next letters.Length])) + string index)

                      let segment index (name: string) =
                          let typed = "{" + name + types[random.Next types.Length] + "}"
                          if index % 2 = 0 then $"seg{index}/{typed}" else typed

                      let pattern = "/" + String.Join("/", names |> List.mapi segment)
                      Assert.equal names (RouteInventory.placeholders pattern) }

          { Name = "release versions order by major, minor, patch, with a prerelease before its release"
            Run =
              fun () ->
                  Assert.equal (Some true) (ReleaseVersion.atLeast "0.9.0" "0.9.0")
                  Assert.equal (Some true) (ReleaseVersion.atLeast "0.9.0" "v0.10.0")
                  Assert.equal (Some false) (ReleaseVersion.atLeast "0.9.0" "0.8.9")
                  Assert.equal (Some false) (ReleaseVersion.atLeast "0.9.0" "0.9.0-rc.1")
                  Assert.equal (Some true) (ReleaseVersion.atLeast "0.9.0-rc.1" "0.9.0")
                  Assert.equal None (ReleaseVersion.atLeast "0.9.0" "latest") }

          { Name = "Limen routing availability: N/A without Limen, pending before 0.9.0, then available"
            Run =
              fun () ->
                  let isPending =
                      function
                      | LimenRouting.Pending _ -> true
                      | _ -> false

                  Assert.isTrue
                      (match LimenRouting.availability false (Some "0.9.0") None with
                       | LimenRouting.NotApplicable _ -> true
                       | _ -> false)
                      "no Limen, no Limen routing"

                  Assert.equal "0.9.0" LimenRouting.firstRelease
                  Assert.isTrue (isPending (LimenRouting.availability true (Some "0.8.0") None)) "Limen 0.8.0 predates routing"
                  Assert.isTrue (isPending (LimenRouting.availability true None None)) "unknown installed version"
                  Assert.equal (LimenRouting.Available "0.9.0") (LimenRouting.availability true (Some "0.9.1") None) }

          { Name = "routing templates: the shipped template and Praxis's own inventories satisfy the contract"
            Run =
              fun () ->
                  let root = praxisRoot (DirectoryInfo AppContext.BaseDirectory)
                  let read (relative: string) = inventory (File.ReadAllText(Path.Combine(root, relative)))
                  let template = read "templates/application-routes.json"
                  Assert.isTrue (RouteInventory.isSupportedVersion template) "echelon.routes/v1"
                  Assert.equal (Some "hash") template.Mode
                  Assert.empty (RouteInventory.problems true template)
                  Assert.empty (RouteInventory.problems false (read ".echelon/routes.json"))
                  Assert.empty (RouteInventory.problems false (read ".echelon/routes.hub.json")) } ]
