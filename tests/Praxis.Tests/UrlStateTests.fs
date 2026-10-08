namespace Praxis.Tests

open System
open System.IO
open Limen.Routing
open Praxis.Application.Web

/// The URL state of Praxis's own web and hub UIs (SAF-URL-1..10, WI-0078,
/// DF-ROS-2026-A057), through Limen.Routing: pure tests of the tables, the
/// canonical form, the typed refusals, the round trip and the flash cookie.
[<RequireQualifiedAccess>]
module UrlStateTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, ".echelon")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    let private none = { Tags = []; Status = None }

    /// A location's path and decoded query, as the HTTP adapter receives them.
    let private request (location: string) =
        match location.IndexOf '?' with
        | -1 -> location, []
        | index ->
            location.Substring(0, index),
            location.Substring(index + 1).Split('&')
            |> List.ofArray
            |> List.map (fun pair ->
                let equals = pair.IndexOf '='
                Uri.UnescapeDataString(pair.Substring(0, equals)), Uri.UnescapeDataString(pair.Substring(equals + 1)))

    let private resolveWeb (location: string) =
        let path, query = request location
        UrlState.resolve UrlState.web path query

    let tests =
        [ { Name = "url state: the web and hub route tables are valid Limen.Routing tables"
            Run =
              fun () ->
                  Assert.isTrue (Result.isOk UrlState.web) $"{UrlState.web}"
                  Assert.isTrue (Result.isOk UrlState.hub) $"{UrlState.hub}" }

          { Name = "url state: .echelon/routes.json and routes.hub.json are Inventory.render of the tables, byte for byte (SAF-URL-8)"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
                  Assert.equal (Ok(File.ReadAllText(Path.Combine(root, ".echelon", "routes.json")))) (UrlState.inventory UrlState.web)
                  Assert.equal (Ok(File.ReadAllText(Path.Combine(root, ".echelon", "routes.hub.json")))) (UrlState.inventory UrlState.hub) }

          { Name = "url state: canonical locations show their view; every other spelling redirects to the canonical one (SAF-URL-2)"
            Run =
              fun () ->
                  Assert.equal (Show(Queue none)) (resolveWeb "/")
                  Assert.equal (Show(Queue { Tags = [ "a"; "b" ]; Status = Some "ready" })) (resolveWeb "/?tag=a,b&status=ready")
                  Assert.equal (Show(WorkItem "WI-0001")) (resolveWeb "/work/WI-0001")
                  Assert.equal (Show Validation) (resolveWeb "/validate")
                  Assert.equal (Show(Execution "EXE-1")) (resolveWeb "/executions/EXE-1")
                  Assert.equal (Canonical "/") (UrlState.resolve UrlState.web "/" [ "tag", ""; "status", "" ])
                  Assert.equal (Canonical "/?tag=a,b") (UrlState.resolve UrlState.web "/" [ "tag", "b, a"; "tag", "a" ])
                  Assert.equal (Canonical "/?tag=a,b&status=ready") (UrlState.resolve UrlState.web "/" [ "status", "ready"; "tag", "a,b" ])
                  Assert.equal (Canonical "/") (resolveWeb "/index.html")
                  Assert.equal (Canonical "/validate") (resolveWeb "/validate?undeclared=1")
                  Assert.equal (Canonical "/?repo=r1&tag=x") (UrlState.resolve UrlState.hub "/" [ "tag", "x"; "repo", "r1"; "status", "" ]) }

          { Name = "url state: an unknown page or an invalid value is a typed refusal with its status, never a blank page (SAF-URL-4)"
            Run =
              fun () ->
                  match resolveWeb "/nothing-here", resolveWeb "/?status=nope" with
                  | Refused RouteError.NotFound, Refused(RouteError.Invalid(_, "status", "nope", _) as invalid) ->
                      let status, title, _ = UrlState.refusal RouteError.NotFound
                      Assert.equal (404, "Not found") (status, title)
                      let status, title, message = UrlState.refusal invalid
                      Assert.equal (400, "Invalid link") (status, title)
                      Assert.isTrue (message.Contains "nope") message
                  | other -> failwith $"%A{other}"

                  let status, title, _ = UrlState.refusal(RouteError.NotPermitted "work.item")
                  Assert.equal (403, "Not permitted") (status, title) }

          { Name = "url state: format then resolve is the view, for generated views (SAF-URL-9 round trip)"
            Run =
              fun () ->
                  let random = Random 20261008
                  let alphabet = "abcdefghijklmnopqrstuvwxyz0123456789-"
                  let word () = String(Array.init (random.Next(1, 8)) (fun _ -> alphabet[random.Next alphabet.Length]))

                  let generated () =
                      match random.Next 4 with
                      | 0 ->
                          let tags = List.init (random.Next 4) (fun _ -> word ()) |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                          let status = if random.Next 2 = 0 then None else Some UrlState.statuses[random.Next UrlState.statuses.Length]
                          Queue { Tags = tags; Status = status }
                      | 1 -> WorkItem("WI-" + word ())
                      | 2 -> Validation
                      | _ -> Execution("EXE-" + word ())

                  for _ in 1..300 do
                      let view = generated ()

                      match UrlState.format UrlState.web view with
                      | Some location -> Assert.equal (Show view) (resolveWeb location)
                      | None -> failwith $"{view} has no canonical location" }

          { Name = "url state: post/redirect/get feedback moves from the URL to a short-lived flash cookie (SAF-URL-5)"
            Run =
              fun () ->
                  Assert.equal ("/work/WI-1", Some("notice", "Captured WI-1.")) (UrlState.splitFlash "/work/WI-1?notice=Captured%20WI-1.")
                  Assert.equal ("/?status=ready", Some("error", "a reason")) (UrlState.splitFlash "/?status=ready&error=a%20reason")
                  Assert.equal ("/validate", None) (UrlState.splitFlash "/validate")
                  let cookie = UrlState.flash "error" "reason: required; really"
                  Assert.isTrue (cookie.Contains "HttpOnly" && cookie.Contains "SameSite=Strict" && cookie.Contains "Max-Age=60") cookie
                  let header = "other=1; " + cookie.Split(';').[0] + "; more=2"
                  Assert.equal (Some("error", "reason: required; really")) (UrlState.readFlash(Some header))
                  Assert.equal None (UrlState.readFlash(Some "other=1"))
                  Assert.equal None (UrlState.readFlash None) }

          { Name = "url state: Link to this view is the absolute canonical URL (SAF-URL-10)"
            Run =
              fun () ->
                  Assert.equal "http://127.0.0.1:4310/?status=ready" (UrlState.share "127.0.0.1:4310" "/?status=ready")
                  Assert.equal "/?tag=a,b" (UrlState.locationOf "/" [ "tag", "a,b" ]) } ]
