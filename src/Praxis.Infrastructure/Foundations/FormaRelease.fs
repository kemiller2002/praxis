namespace Praxis.Infrastructure.Foundations

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Reflection
open System.Security.Cryptography
open Praxis.Domain.Foundations

/// The pinned Forma release compiled into this CLI: the unmodified release
/// tarball and its lock (`vendor/forma/`). Its stylesheet is extracted from
/// the tarball only after the digest matches the lock, so a page can only
/// ever be presented with the pinned Forma release (SAF-FORMA-1..3).
[<RequireQualifiedAccess>]
module FormaRelease =
    [<Literal>]
    let LockResource = "praxis.forma/forma.lock"

    [<Literal>]
    let TarballResource = "praxis.forma/forma.tgz"

    /// The release file every page links: Forma's complete presentation.
    [<Literal>]
    let StylesheetEntry = "package/dist/all.css"

    type Assets =
        { Lock: FormaLock
          StylesheetPath: string
          Stylesheet: byte array }

    let private resource (name: string) =
        match Assembly.GetExecutingAssembly().GetManifestResourceStream name with
        | null -> Error $"the Forma release resource '{name}' is not compiled into this CLI"
        | stream ->
            use stream = stream
            use copy = new MemoryStream()
            stream.CopyTo copy
            Ok(copy.ToArray())

    let sha256Hex (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    /// One file from a gzip-compressed npm tarball.
    let extract (entryName: string) (tarball: byte array) =
        use gzip = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress)
        use reader = new TarReader(gzip)

        let rec next () =
            match reader.GetNextEntry() with
            | null -> Error $"the Forma tarball has no {entryName}"
            | entry when entry.Name = entryName && not (isNull entry.DataStream) ->
                use copy = new MemoryStream()
                entry.DataStream.CopyTo copy
                Ok(copy.ToArray())
            | _ -> next ()

        next ()

    /// Verify a lock and tarball and extract the stylesheet.
    let load (lockText: string) (tarball: byte array) : Result<Assets, string> =
        FormaPin.parse lockText
        |> Result.bind (fun lock -> FormaPin.verify lock (sha256Hex tarball))
        |> Result.mapError FormaPin.describe
        |> Result.bind (fun lock ->
            extract StylesheetEntry tarball
            |> Result.map (fun css ->
                { Lock = lock
                  StylesheetPath = FormaPin.stylesheetPath lock
                  Stylesheet = css }))

    let private compiled =
        lazy
            (resource LockResource
             |> Result.bind (fun lockBytes ->
                 resource TarballResource
                 |> Result.bind (load (Text.Encoding.UTF8.GetString lockBytes))))

    /// The release compiled into this CLI, verified once per process.
    let current () = compiled.Force()

    /// The stylesheet path pages link; the pinned version's path even when
    /// the release failed to load, so the failure is visible as a 404 rather
    /// than as an unstyled page that silently uses something else.
    let stylesheetPath () =
        match current () with
        | Ok assets -> assets.StylesheetPath
        | Error _ -> "/forma/unavailable/all.css"

    /// Serve the pinned stylesheet for `GET /forma/<version>/all.css`.
    let tryServe (httpMethod: string) (segments: string list) =
        match httpMethod, segments, current () with
        | "GET", [ "forma"; version; "all.css" ], Ok assets when version = assets.Lock.Version -> Some assets.Stylesheet
        | _ -> None

/// Forma markup shared by the web interface and the hub (SAF-FORMA-2, 5, 6):
/// Forma's own patterns, so Praxis renders work meaning and Forma presents it.
[<RequireQualifiedAccess>]
module FormaMarkup =
    let private escape (text: string) = Net.WebUtility.HtmlEncode text

    /// Forma's status-lozenge states. The status word is always the visible
    /// text, so the state never depends on color alone.
    let lozengeState (status: string) =
        match status.Trim().ToLowerInvariant() with
        | "complete"
        | "completed"
        | "merged" -> "ok"
        | "blocked"
        | "failed" -> "blocked"
        | "abandoned"
        | "unknown" -> "unknown"
        | _ -> "attention"

    /// A refused operation or unreadable result (Forma inline fault).
    let alert (message: string) =
        $"<div class=\"ef-fault ef-fault--inline\" role=\"alert\" data-ef-intent=\"inline\" data-ef-severity=\"error\"><div class=\"ef-fault__marker\" aria-hidden=\"true\">!</div><div class=\"ef-fault__body\"><p class=\"ef-fault__severity\">Error</p><p class=\"ef-fault__message\">{escape message}</p></div></div>"

    /// A completed operation's confirmation (Forma alert).
    let notice (message: string) =
        $"<aside class=\"ef-alert\" role=\"status\" data-tone=\"ok\"><div class=\"ef-alert__icon\" aria-hidden=\"true\">&#10003;</div><div><p>{escape message}</p></div></aside>"

    /// An Aegis operational fault: the safe message and reference only
    /// (Forma fault banner; SAF-FORMA-6).
    let faultBanner (message: string) (reference: string) =
        $"<main id=\"main\" tabindex=\"-1\"><aside class=\"ef-alert ef-fault-banner\" role=\"alert\" data-ef-intent=\"banner\" data-ef-severity=\"error\" aria-labelledby=\"fault-title\"><div class=\"ef-alert__icon ef-fault-banner__marker\" aria-hidden=\"true\">!</div><div class=\"ef-fault-banner__body\"><p class=\"ef-fault__severity\">Error</p><h1 class=\"ef-alert__title\" id=\"fault-title\">Something went wrong</h1><p>{escape message}</p><p class=\"ef-fault-reference\">Reference <code>{escape reference}</code></p></div></aside></main>"

    /// The document shell every page uses: Forma first, then the project's
    /// optional override stylesheet, a skip link and Forma's site typography.
    let head (stylesheetPath: string) =
        [ $"<link rel=\"stylesheet\" href=\"{escape stylesheetPath}\" />"
          "<link rel=\"stylesheet\" href=\"/styles.css\" />" ]

    [<Literal>]
    let BodyOpen = "<body class=\"ef-site\"><a class=\"ef-skip-link\" href=\"#main\">Skip to main content</a><div class=\"ef-container\">"

    [<Literal>]
    let BodyClose = "</div></body>"
