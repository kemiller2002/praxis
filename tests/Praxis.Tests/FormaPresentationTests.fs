namespace Praxis.Tests

open System
open System.IO
open System.Text
open Praxis.Domain.Foundations
open Praxis.Infrastructure.Foundations
open Praxis.Cli
open Praxis.Domain.Work
open Praxis.Infrastructure.Boundary

/// Praxis web and hub consume a pinned Forma release (SAF-FORMA-1..6,
/// PRX-UI-030): the vendored artifact is the release, the stylesheet comes
/// only from it, and Praxis ships no presentation of its own.
[<RequireQualifiedAccess>]
module FormaPresentationTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json")) && Directory.Exists(Path.Combine(directory.FullName, "vendor", "forma")) then
            directory.FullName
        else
            match directory.Parent with
            | null -> failwith "repository root not found"
            | parent -> repositoryRoot parent

    let private root () = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)
    let private read relative = File.ReadAllText(Path.Combine(root (), relative))
    let private lockText () = read "vendor/forma/forma.lock"

    let private tarball () =
        let lock =
            match FormaPin.parse (lockText ()) with
            | Ok lock -> lock
            | Error error -> failwith (FormaPin.describe error)

        File.ReadAllBytes(Path.Combine(root (), "vendor", "forma", $"echelon-foundry-design-system-{lock.Version}.tgz"))

    let tests =
        [ { Name = "forma: the vendored tarball is the pinned immutable release artifact"
            Run =
              fun () ->
                  match FormaPin.parse (lockText ()) with
                  | Error error -> failwith (FormaPin.describe error)
                  | Ok lock ->
                      Assert.equal (Ok lock) (FormaPin.verify lock (FormaRelease.sha256Hex (tarball ())))
                      Assert.isTrue (lock.Url.Contains $"/releases/download/v{lock.Version}/") "the URL is a release asset"

                  let only =
                      Directory.GetFiles(Path.Combine(root (), "vendor", "forma"))
                      |> Array.map Path.GetFileName
                      |> Array.sort
                      |> List.ofArray

                  Assert.equal 2 only.Length }
          { Name = "forma: the lock refuses floating versions, other URLs and bad digests"
            Run =
              fun () ->
                  let text = lockText ()
                  let refused (edit: string -> string) =
                      match FormaPin.parse (edit text) with
                      | Ok _ -> failwith "expected a refusal"
                      | Error error -> error

                  Assert.equal (FormaLockError.InvalidVersion "^0.4.1") (refused (fun t -> t.Replace("forma 0.4.1", "forma ^0.4.1")))

                  match refused (fun t -> t.Replace("download/v0.4.1/", "download/main/")) with
                  | FormaLockError.UrlNotImmutableRelease _ -> ()
                  | other -> failwith $"unexpected {other}"

                  Assert.equal (FormaLockError.InvalidDigest "abc") (refused (fun t -> t.Replace((t.Split("sha256 ")).[1].Trim(), "abc")))
                  Assert.equal (FormaLockError.Missing "url") (refused (fun t -> t.Replace("url ", "uri ")))

                  match FormaPin.parse text with
                  | Ok lock ->
                      match FormaPin.verify lock (String('0', 64)) with
                      | Error(FormaLockError.DigestMismatch _) -> ()
                      | other -> failwith $"unexpected {other}"
                  | Error error -> failwith (FormaPin.describe error) }
          { Name = "forma: the stylesheet is Forma's all.css from the verified tarball, at a versioned path"
            Run =
              fun () ->
                  match FormaRelease.current () with
                  | Error message -> failwith message
                  | Ok assets ->
                      Assert.equal "/forma/0.4.1/all.css" assets.StylesheetPath
                      let css = Encoding.UTF8.GetString assets.Stylesheet
                      Assert.isTrue (css.Contains ".ef-status-lozenge") "Forma components present"
                      Assert.isTrue (css.Contains "@layer echelon.foundations") "Forma foundations present"
                      Assert.equal (Some assets.Stylesheet) (FormaRelease.tryServe "GET" [ "forma"; "0.4.1"; "all.css" ])
                      Assert.equal None (FormaRelease.tryServe "GET" [ "forma"; "0.4.0"; "all.css" ])
                      Assert.equal None (FormaRelease.tryServe "POST" [ "forma"; "0.4.1"; "all.css" ])

                  let tampered = tarball () |> Array.copy
                  tampered[tampered.Length / 2] <- tampered[tampered.Length / 2] ^^^ 0xFFuy

                  match FormaRelease.load (lockText ()) tampered with
                  | Ok _ -> failwith "a tampered tarball must not be served"
                  | Error message -> Assert.isTrue (message.Contains "does not match the lock") message }
          { Name = "forma: every web and hub page links the pinned stylesheet and uses Forma patterns"
            Run =
              fun () ->
                  let item: WorkListRow =
                      { Id = "WI-1"; Title = "t"; Description = None; Tags = [ "x" ]; Priority = None; Status = "blocked"
                        BlockedReason = None; BacklogActions = []; Attachments = []; LiveWorkItem = None }

                  let pages =
                      [ "web home", WebInterface.renderHome None (Ok [ item ]) [ "notice", "saved" ]
                        "web detail", WebInterface.renderDetail None item None "{}" [ "error", "refused" ]
                        "web missing", WebInterface.renderMissing None "gone"
                        "web validation", WebInterface.renderValidation None (Error "unreadable")
                        "hub home", HubWeb.renderHome (Ok []) (Ok []) [] ]

                  for name, page in pages do
                      let forma = page.IndexOf "/forma/0.4.1/all.css"
                      let local = page.IndexOf "/styles.css"
                      Assert.isTrue (forma > 0 && local > forma) $"{name} links the pinned Forma release before the project override"
                      Assert.isTrue (page.Contains "class=\"ef-skip-link\" href=\"#main\"" && page.Contains "id=\"main\"") $"{name} has Forma's skip link to main"
                      Assert.isTrue (page.Contains "<body class=\"ef-site\">") $"{name} uses Forma site typography"

                  let home = snd pages[0]
                  Assert.isTrue (home.Contains "class=\"ef-data-grid\"") "work queue is a Forma data grid"
                  Assert.isTrue (home.Contains "class=\"ef-status-lozenge\" data-state=\"blocked\">blocked<") "status is a lozenge with visible text"
                  Assert.isTrue (home.Contains "class=\"ef-alert\"") "notices use the Forma alert"
                  Assert.isTrue ((snd pages[1]).Contains "ef-fault ef-fault--inline") "refusals use the Forma inline fault"
                  Assert.isTrue ((Html.fault "msg" "AG-12345").Contains "ef-fault-banner") "operational faults use the Forma fault banner"
                  Assert.isTrue ((HubWeb.renderHome (Ok []) (Ok []) []).Contains "ef-empty-state") "hub empty states are Forma's" }
          { Name = "forma: the web host serves the pinned stylesheet as CSS"
            Run =
              fun () ->
                  match AegisBoundary.configure None [ (Aegis.Sinks.Collector()).Sink() ] with
                  | Error problems -> failwith (String.concat "; " problems)
                  | Ok aegis ->
                      let request path =
                          { Method = "GET"; Segments = path; Query = []; ContentType = None; Body = [||]; Headers = [] }

                      let css = HttpHost.respond aegis (fun _ -> HttpMessages.text 404 "not found") (request [ "forma"; "0.4.1"; "all.css" ])
                      Assert.equal 200 css.Status
                      Assert.isTrue (css.ContentType.StartsWith "text/css") css.ContentType
                      Assert.isTrue ((Encoding.UTF8.GetString css.Body).Contains ".ef-data-grid") "Forma's stylesheet"
                      let other = HttpHost.respond aegis (fun _ -> HttpMessages.text 404 "not found") (request [ "forma"; "9.9.9"; "all.css" ])
                      Assert.equal 404 other.Status }
          { Name = "forma: foundations verify accepts the checksum-verified tarball lock as Forma's pin"
            Run =
              fun () ->
                  let temp = Path.Combine(Path.GetTempPath(), $"praxis-forma-{Guid.NewGuid():N}")
                  Directory.CreateDirectory(Path.Combine(temp, "vendor", "forma")) |> ignore

                  try
                      File.WriteAllText(Path.Combine(temp, "vendor", "forma", "forma.lock"), lockText ())
                      File.WriteAllBytes(Path.Combine(temp, "vendor", "forma", "echelon-foundry-design-system-0.4.1.tgz"), tarball ())
                      Directory.CreateDirectory(Path.Combine(temp, ".echelon")) |> ignore
                      File.WriteAllText(Path.Combine(temp, ".echelon", "foundations.json"), """{ "schemaVersion": 1, "application": "X", "capabilities": { "forma": { "required": true, "version": "0.4.1" } } }""")
                      File.WriteAllText(Path.Combine(temp, "Page.fs"), "let p = \"<span class=\\\"ef-badge\\\">x</span>\"")
                      let result () =
                          match Foundations.verify temp with
                          | Ok report -> report.Capabilities |> List.find (fun c -> c.Name = "forma")
                          | Error message -> failwith message

                      Assert.isTrue (result ()).Passed $"verified lock passes: {result ()}"
                      File.WriteAllBytes(Path.Combine(temp, "vendor", "forma", "echelon-foundry-design-system-0.4.1.tgz"), [| 0uy |])
                      Assert.isTrue (not (result ()).Pinned) "a tarball that does not match the lock is not a pin"
                  finally
                      Directory.Delete(temp, true) }
          { Name = "forma: Praxis ships no presentation of its own (web and hub stylesheets carry no rules)"
            Run =
              fun () ->
                  for relative in [ "web/styles.css"; "web-hub/styles.css" ] do
                      let text = read relative
                      let withoutComments = Text.RegularExpressions.Regex.Replace(text, @"/\*[\s\S]*?\*/", "")
                      Assert.isTrue (String.IsNullOrWhiteSpace withoutComments) $"{relative} must not define presentation; use Forma" } ]
