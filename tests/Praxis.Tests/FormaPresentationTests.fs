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

    let private themeLockText () = read "vendor/echelon-theme/echelon-theme.lock"

    let private themeTarball () =
        match EchelonThemePin.parse (themeLockText ()) with
        | Ok lock -> File.ReadAllBytes(Path.Combine(root (), "vendor", "echelon-theme", $"echelon-foundry-echelon-theme-{lock.Version}.tgz"))
        | Error error -> failwith (EchelonThemePin.describe error)

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

                  Assert.equal (ReleaseLockError.InvalidVersion "^1.0.1") (refused (fun t -> t.Replace("forma 1.0.1", "forma ^1.0.1")))

                  match refused (fun t -> t.Replace("download/v1.0.1/", "download/main/")) with
                  | ReleaseLockError.UrlNotImmutableRelease _ -> ()
                  | other -> failwith $"unexpected {other}"

                  Assert.equal (ReleaseLockError.InvalidDigest "abc") (refused (fun t -> t.Replace((t.Split("sha256 ")).[1].Trim(), "abc")))
                  Assert.equal (ReleaseLockError.Missing "url") (refused (fun t -> t.Replace("url ", "uri ")))

                  match FormaPin.parse text with
                  | Ok lock ->
                      match FormaPin.verify lock (String('0', 64)) with
                      | Error(ReleaseLockError.DigestMismatch _) -> ()
                      | other -> failwith $"unexpected {other}"
                  | Error error -> failwith (FormaPin.describe error) }
          { Name = "forma: the stylesheet is Forma's all.css from the verified tarball, at a versioned path"
            Run =
              fun () ->
                  match FormaRelease.current () with
                  | Error message -> failwith message
                  | Ok assets ->
                      Assert.equal "/forma/1.0.1/all.css" assets.StylesheetPath
                      let css = Encoding.UTF8.GetString assets.Stylesheet
                      Assert.isTrue (css.Contains ".forma-status-lozenge") "Forma components present"
                      Assert.isTrue (css.Contains "@layer forma.foundations") "Forma foundations present"
                      Assert.equal (Some assets.Stylesheet) (FormaRelease.tryServe "GET" [ "forma"; "1.0.1"; "all.css" ])
                      Assert.equal None (FormaRelease.tryServe "GET" [ "forma"; "1.0.0"; "all.css" ])
                      Assert.equal None (FormaRelease.tryServe "POST" [ "forma"; "1.0.1"; "all.css" ])

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
                      let order =
                          [ "/forma/1.0.1/all.css"
                            "/echelon-theme/1.1.0/echelon.css"
                            "/echelon-theme/1.1.0/echelon-fonts.css"
                            "/echelon-theme/1.1.0/echelon-app.css"
                            "/styles.css" ]
                          |> List.map page.IndexOf

                      Assert.isTrue (List.forall (fun i -> i > 0) order && order = List.sort order) $"{name} links Forma, then the Echelon theme in its load order, then the project override: {order}"
                      Assert.isTrue (page.Contains "<html lang=\"en\" data-forma-brand=\"echelon\">") $"{name} selects the Echelon brand on the root element"
                      Assert.isTrue (page.Contains "class=\"forma-skip-link\" href=\"#main\"" && page.Contains "id=\"main\"") $"{name} has Forma's skip link to main"
                      Assert.isTrue (page.Contains "<body class=\"forma-site\">") $"{name} uses Forma site typography"

                  let home = snd pages[0]
                  Assert.isTrue (home.Contains "class=\"forma-data-grid\"") "work queue is a Forma data grid"
                  Assert.isTrue (home.Contains "class=\"forma-status-lozenge\" data-state=\"blocked\">blocked<") "status is a lozenge with visible text"
                  Assert.isTrue (home.Contains "class=\"forma-alert\"") "notices use the Forma alert"
                  Assert.isTrue ((snd pages[1]).Contains "forma-fault forma-fault--inline") "refusals use the Forma inline fault"
                  Assert.isTrue ((Html.fault "msg" "AG-12345").Contains "forma-fault-banner") "operational faults use the Forma fault banner"
                  Assert.isTrue ((HubWeb.renderHome (Ok []) (Ok []) []).Contains "forma-empty-state") "hub empty states are Forma's" }
          { Name = "forma: the web host serves the pinned stylesheet as CSS"
            Run =
              fun () ->
                  match AegisBoundary.configure None [ (Aegis.Sinks.Collector()).Sink() ] with
                  | Error problems -> failwith (String.concat "; " problems)
                  | Ok aegis ->
                      let request path =
                          { Method = "GET"; Segments = path; Query = []; ContentType = None; Body = [||]; Headers = [] }

                      let css = HttpHost.respond aegis (fun _ -> HttpMessages.text 404 "not found") (request [ "forma"; "1.0.1"; "all.css" ])
                      Assert.equal 200 css.Status
                      Assert.isTrue (css.ContentType.StartsWith "text/css") css.ContentType
                      Assert.isTrue ((Encoding.UTF8.GetString css.Body).Contains ".forma-data-grid") "Forma's stylesheet"
                      let other = HttpHost.respond aegis (fun _ -> HttpMessages.text 404 "not found") (request [ "forma"; "9.9.9"; "all.css" ])
                      Assert.equal 404 other.Status }
          { Name = "forma: foundations verify accepts the checksum-verified tarball lock as Forma's pin"
            Run =
              fun () ->
                  let temp = Path.Combine(Path.GetTempPath(), $"praxis-forma-{Guid.NewGuid():N}")
                  Directory.CreateDirectory(Path.Combine(temp, "vendor", "forma")) |> ignore

                  try
                      File.WriteAllText(Path.Combine(temp, "vendor", "forma", "forma.lock"), lockText ())
                      File.WriteAllBytes(Path.Combine(temp, "vendor", "forma", "echelon-foundry-design-system-1.0.1.tgz"), tarball ())
                      Directory.CreateDirectory(Path.Combine(temp, ".echelon")) |> ignore
                      File.WriteAllText(Path.Combine(temp, ".echelon", "foundations.json"), """{ "schemaVersion": 1, "application": "X", "capabilities": { "forma": { "required": true, "version": "1.0.1" } } }""")
                      File.WriteAllText(Path.Combine(temp, "Page.fs"), "let p = \"<span class=\\\"forma-badge\\\">x</span>\"")
                      let result () =
                          match Foundations.verify temp with
                          | Ok report -> report.Capabilities |> List.find (fun c -> c.Name = "forma")
                          | Error message -> failwith message

                      Assert.isTrue (result ()).Passed $"verified lock passes: {result ()}"
                      File.WriteAllBytes(Path.Combine(temp, "vendor", "forma", "echelon-foundry-design-system-1.0.1.tgz"), [| 0uy |])
                      Assert.isTrue (not (result ()).Pinned) "a tarball that does not match the lock is not a pin"
                  finally
                      Directory.Delete(temp, true) }
          { Name = "forma: Praxis ships no presentation of its own (web and hub stylesheets carry no rules)"
            Run =
              fun () ->
                  for relative in [ "web/styles.css"; "web-hub/styles.css" ] do
                      let text = read relative
                      let withoutComments = Text.RegularExpressions.Regex.Replace(text, @"/\*[\s\S]*?\*/", "")
                      Assert.isTrue (String.IsNullOrWhiteSpace withoutComments) $"{relative} must not define presentation; use Forma" }
          { Name = "echelon theme: the vendored tarball is the pinned immutable release artifact"
            Run =
              fun () ->
                  match EchelonThemePin.parse (themeLockText ()) with
                  | Error error -> failwith (EchelonThemePin.describe error)
                  | Ok lock ->
                      Assert.equal "1.1.0" lock.Version
                      Assert.equal (Ok lock) (EchelonThemePin.verify lock (FormaRelease.sha256Hex (themeTarball ())))

                  let files = Directory.GetFiles(Path.Combine(root (), "vendor", "echelon-theme"))
                  Assert.equal 2 files.Length

                  let text = themeLockText ()
                  let refused (edit: string -> string) =
                      match EchelonThemePin.parse (edit text) with
                      | Ok _ -> failwith "expected a refusal"
                      | Error error -> error

                  Assert.equal (ReleaseLockError.InvalidVersion "^1.1.0") (refused (fun t -> t.Replace("echelon-theme 1.1.0", "echelon-theme ^1.1.0")))

                  match refused (fun t -> t.Replace("kemiller2002/echelon-design-system", "kemiller2002/forma")) with
                  | ReleaseLockError.UrlNotImmutableRelease _ -> ()
                  | other -> failwith $"unexpected {other}"

                  // A Forma lock is not a theme lock: the key line names the release.
                  Assert.equal (ReleaseLockError.Missing "echelon-theme") (refused (fun _ -> lockText ())) }
          { Name = "echelon theme: stylesheets and fonts come only from the verified tarball, at versioned paths"
            Run =
              fun () ->
                  match EchelonThemeRelease.current () with
                  | Error message -> failwith message
                  | Ok assets ->
                      Assert.equal
                          [ "/echelon-theme/1.1.0/echelon.css"; "/echelon-theme/1.1.0/echelon-fonts.css"; "/echelon-theme/1.1.0/echelon-app.css" ]
                          assets.StylesheetPaths

                      let serve segments = EchelonThemeRelease.tryServe "GET" segments

                      match serve [ "echelon-theme"; "1.1.0"; "echelon-app.css" ] with
                      | Some(contentType, css) ->
                          Assert.isTrue (contentType.StartsWith "text/css") contentType
                          Assert.isTrue ((Encoding.UTF8.GetString css).Contains "[data-forma-brand=\"echelon\"]") "the app layer is scoped to the Echelon brand"
                      | None -> failwith "echelon-app.css is not served"

                      // Every font the font stylesheet names is served beside it, as WOFF2.
                      let fonts =
                          match serve [ "echelon-theme"; "1.1.0"; "echelon-fonts.css" ] with
                          | Some(_, css) ->
                              Text.RegularExpressions.Regex.Matches(Encoding.UTF8.GetString css, "url\\(\"([^\"]+)\"\\)")
                              |> Seq.map _.Groups[1].Value
                              |> List.ofSeq
                          | None -> failwith "echelon-fonts.css is not served"

                      Assert.isTrue (not fonts.IsEmpty) "the font stylesheet names its fonts"

                      for font in fonts do
                          match serve ("echelon-theme" :: "1.1.0" :: List.ofArray (font.Split '/')) with
                          | Some(contentType, bytes) ->
                              Assert.equal "font/woff2" contentType
                              Assert.isTrue (bytes.Length > 0) font
                          | None -> failwith $"{font} is not served"

                      Assert.equal None (serve [ "echelon-theme"; "1.0.0"; "echelon.css" ])
                      Assert.equal None (serve [ "echelon-theme"; "1.1.0"; "package.json" ])
                      Assert.equal None (serve [ "echelon-theme"; "1.1.0"; "echelon-marketing.css" ])
                      Assert.equal None (EchelonThemeRelease.tryServe "POST" [ "echelon-theme"; "1.1.0"; "echelon.css" ])

                  let tampered = themeTarball () |> Array.copy
                  tampered[tampered.Length / 2] <- tampered[tampered.Length / 2] ^^^ 0xFFuy

                  match EchelonThemeRelease.load (themeLockText ()) tampered with
                  | Ok _ -> failwith "a tampered theme tarball must not be served"
                  | Error message -> Assert.isTrue (message.Contains "does not match the lock") message }
          { Name = "echelon theme: the web host serves its stylesheets as CSS and its fonts as WOFF2"
            Run =
              fun () ->
                  match AegisBoundary.configure None [ (Aegis.Sinks.Collector()).Sink() ] with
                  | Error problems -> failwith (String.concat "; " problems)
                  | Ok aegis ->
                      let get path =
                          HttpHost.respond aegis (fun _ -> HttpMessages.text 404 "not found") { Method = "GET"; Segments = path; Query = []; ContentType = None; Body = [||]; Headers = [] }

                      let css = get [ "echelon-theme"; "1.1.0"; "echelon.css" ]
                      Assert.equal 200 css.Status
                      Assert.isTrue (css.ContentType.StartsWith "text/css") css.ContentType
                      let font = get [ "echelon-theme"; "1.1.0"; "fonts"; "manrope-latin-wght-normal.woff2" ]
                      Assert.equal 200 font.Status
                      Assert.equal "font/woff2" font.ContentType
                      Assert.equal 404 (get [ "echelon-theme"; "1.1.0"; "fonts"; "missing.woff2" ]).Status } ]
