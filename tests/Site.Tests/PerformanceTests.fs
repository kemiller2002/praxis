/// PRAXIS-SITE-19: static-first budgets and third-party boundaries.
/// Rendered measurements are recorded in docs/site/performance.md.
module Site.Tests.PerformanceTests

open System
open System.IO
open System.IO.Compression
open System.Text.RegularExpressions
open Site.Tests.Fixture

/// gzip size at the highest compression level (the Node suite used zlib level 9).
let private gzipped (file: string) =
    let bytes = File.ReadAllBytes(Path.Combine(siteRoot, file))
    use output = new MemoryStream()

    do
        use gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)
        gzip.Write(bytes, 0, bytes.Length)

    int output.Length

let rec private walk (directory: string) =
    Directory.GetFileSystemEntries directory
    |> List.ofArray
    |> List.collect (fun entry -> if Directory.Exists entry then walk entry else [ Path.GetFileName entry ])

let tests =
    [ test "first-party transfer stays inside budget" (fun () ->
          for file, limit in [ "index.html", 20_000; "assets/css/site.css", 10_000 ] do
              let size = gzipped file
              Assert.isTrue (size <= limit) $"{file}: {size} gzip bytes > {limit}")

      test "the page loads no script at all" (fun () ->
          let html = html ()
          Assert.equal 0 (Regex.Matches(html, @"<script\b[^>]*>").Count)
          Assert.isFalse (exists "site/assets/js") "no site/assets/js directory")

      test "the only third party is Google Fonts, linked as a plain stylesheet" (fun () ->
          let html = html ()

          let external =
              Regex.Matches(html, @"<link\b[^>]*href=""(https?://[^""]+)""[^>]*>")
              |> Seq.map (fun found -> found.Value, Uri(found.Groups[1].Value.Replace("&amp;", "&")).Authority)
              |> List.ofSeq

          for _, host in external do
              Assert.isTrue ([ "fonts.googleapis.com"; "fonts.gstatic.com" ] |> List.contains host) host

          let stylesheet =
              external |> List.tryFind (fun (tag, _) -> tag.Contains("rel=\"stylesheet\"", StringComparison.Ordinal))

          match stylesheet with
          | Some(tag, _) ->
              Assert.notMatches @"\bmedia=|\bon[a-z]+=" tag "no media swap and no inline handler: the stylesheet is a plain link"
          | None -> failwith "the Google Fonts stylesheet is linked"

          // No <noscript> fallback is needed: nothing on the page depends on script.
          Assert.notMatches "<noscript>" html "no noscript fallback")

      test "no analytics, trackers, or embedded frames" (fun () ->
          let html = html ()

          for marker in
              [ "googletagmanager"
                "google-analytics"
                "gtag("
                "plausible"
                "segment."
                "hotjar"
                "<iframe"
                "facebook"
                "doubleclick" ] do
              Assert.isFalse (html.Contains(marker: string)) marker)

      test "no images, fonts, scripts or bundles are shipped" (fun () ->
          let files = walk siteRoot
          let listing = String.Join(", ", files)

          Assert.isFalse
              (files |> List.exists (fun name -> Regex.IsMatch(name, @"\.(woff2?|ttf|otf|png|jpe?g|gif|webp|avif|map|m?js|ts)\z")))
              listing

          Assert.isFalse (files |> List.exists (fun name -> Regex.IsMatch(name, "bundle|chunk|vendor"))) listing) ]
