namespace Praxis.Tests

open System
open System.IO
open System.Text
open Praxis.Domain.Foundations
open Praxis.Infrastructure.Foundations

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

                  Assert.equal (FormaLockError.InvalidDigest "abc") (refused (fun t -> t.Replace(t.Split("sha256 ")[1].Trim(), "abc")))
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
          { Name = "forma: Praxis ships no presentation of its own (web and hub stylesheets carry no rules)"
            Run =
              fun () ->
                  for relative in [ "web/styles.css"; "web-hub/styles.css" ] do
                      let text = read relative
                      let withoutComments = Text.RegularExpressions.Regex.Replace(text, @"/\*[\s\S]*?\*/", "")
                      Assert.isTrue (String.IsNullOrWhiteSpace withoutComments) $"{relative} must not define presentation; use Forma" } ]
