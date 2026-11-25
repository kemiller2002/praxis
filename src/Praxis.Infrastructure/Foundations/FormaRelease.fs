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
/// Reading a pinned release tarball: digest first, then only named entries.
[<RequireQualifiedAccess>]
module ReleaseTarball =
    let sha256Hex (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let resource (name: string) =
        match Assembly.GetExecutingAssembly().GetManifestResourceStream name with
        | null -> Error $"the release resource '{name}' is not compiled into this CLI"
        | stream ->
            use stream = stream
            use copy = new MemoryStream()
            stream.CopyTo copy
            Ok(copy.ToArray())

    /// Every regular file of a gzip-compressed npm tarball whose entry name
    /// the predicate accepts, by entry name.
    let extractWhere (accept: string -> bool) (tarball: byte array) : Map<string, byte array> =
        use gzip = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress)
        use reader = new TarReader(gzip)

        let rec next (found: Map<string, byte array>) =
            match reader.GetNextEntry() with
            | null -> found
            | entry when accept entry.Name && not (isNull entry.DataStream) ->
                use copy = new MemoryStream()
                entry.DataStream.CopyTo copy
                next (Map.add entry.Name (copy.ToArray()) found)
            | _ -> next found

        next Map.empty

    /// One file from a gzip-compressed npm tarball.
    let extract (entryName: string) (tarball: byte array) =
        extractWhere ((=) entryName) tarball
        |> Map.tryFind entryName
        |> Option.map Ok
        |> Option.defaultValue (Error $"the tarball has no {entryName}")

    /// A repository's own pin, for `foundations verify`: the lock and the
    /// tarball it names, verified against the lock's digest. None when the
    /// repository has no lock.
    let repositoryLock (release: PinnedRelease) (lockPath: string) (tarballName: string -> string) (root: string) : Result<ReleaseLock, string> option =
        let lockFile = Path.Combine(root, lockPath.Replace('/', Path.DirectorySeparatorChar))

        if not (File.Exists lockFile) then
            None
        else
            ReleasePin.parse release (File.ReadAllText lockFile)
            |> Result.mapError (ReleasePin.describe release)
            |> Result.bind (fun lock ->
                let tarball = Path.Combine(Path.GetDirectoryName lockFile |> Option.ofObj |> Option.defaultValue root, tarballName lock.Version)

                if File.Exists tarball then
                    ReleasePin.verify lock (sha256Hex (File.ReadAllBytes tarball)) |> Result.mapError (ReleasePin.describe release)
                else
                    Error $"the {release.Key} tarball named by {lockPath} is missing")
            |> Some

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
        { Lock: ReleaseLock
          StylesheetPath: string
          Stylesheet: byte array }

    let sha256Hex (bytes: byte array) = ReleaseTarball.sha256Hex bytes

    /// Verify a lock and tarball and extract the stylesheet.
    let load (lockText: string) (tarball: byte array) : Result<Assets, string> =
        FormaPin.parse lockText
        |> Result.bind (fun lock -> FormaPin.verify lock (sha256Hex tarball))
        |> Result.mapError FormaPin.describe
        |> Result.bind (fun lock ->
            ReleaseTarball.extract StylesheetEntry tarball
            |> Result.map (fun css ->
                { Lock = lock
                  StylesheetPath = FormaPin.stylesheetPath lock
                  Stylesheet = css }))

    let private compiled =
        lazy
            (ReleaseTarball.resource LockResource
             |> Result.bind (fun lockBytes ->
                 ReleaseTarball.resource TarballResource
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

    /// Where a repository without npm pins its Forma release.
    [<Literal>]
    let LockPath = "vendor/forma/forma.lock"

    let tarballName (version: string) = $"echelon-foundry-design-system-{version}.tgz"

    /// A repository's own Forma pin, for `foundations verify`.
    let repositoryLock (root: string) : Result<ReleaseLock, string> option =
        ReleaseTarball.repositoryLock FormaPin.release LockPath tarballName root

    /// Serve the pinned stylesheet for `GET /forma/<version>/all.css`.
    let tryServe (httpMethod: string) (segments: string list) =
        match httpMethod, segments, current () with
        | "GET", [ "forma"; version; "all.css" ], Ok assets when version = assets.Lock.Version -> Some assets.Stylesheet
        | _ -> None

/// The pinned Echelon theme release compiled into this CLI
/// (`vendor/echelon-theme/`): the Echelon brand layered over Forma. Its
/// stylesheets and the fonts they name are extracted only after the tarball's
/// digest matches the lock, and served under a versioned path.
[<RequireQualifiedAccess>]
module EchelonThemeRelease =
    [<Literal>]
    let LockResource = "praxis.echelon-theme/echelon-theme.lock"

    [<Literal>]
    let TarballResource = "praxis.echelon-theme/echelon-theme.tgz"

    [<Literal>]
    let private Dist = "package/dist/"

    type Assets =
        { Lock: ReleaseLock
          StylesheetPaths: string list
          /// Served files by their path below the versioned base: the theme's
          /// stylesheets and `fonts/<file>.woff2`.
          Files: Map<string, string * byte array> }

    let private contentType (relative: string) =
        if relative.EndsWith ".css" then "text/css; charset=utf-8" else "font/woff2"

    let private served (relative: string) =
        List.contains relative EchelonThemePin.stylesheets
        || (relative.StartsWith "fonts/" && relative.EndsWith ".woff2" && relative.IndexOf('/', "fonts/".Length) < 0)

    /// Verify a lock and tarball and extract the stylesheets and fonts.
    let load (lockText: string) (tarball: byte array) : Result<Assets, string> =
        EchelonThemePin.parse lockText
        |> Result.bind (fun lock -> EchelonThemePin.verify lock (ReleaseTarball.sha256Hex tarball))
        |> Result.mapError EchelonThemePin.describe
        |> Result.bind (fun lock ->
            let files =
                tarball
                |> ReleaseTarball.extractWhere (fun name -> name.StartsWith Dist && served (name.Substring Dist.Length))
                |> Map.toList
                |> List.map (fun (name, bytes) ->
                    let relative = name.Substring Dist.Length
                    relative, (contentType relative, bytes))
                |> Map.ofList

            match EchelonThemePin.stylesheets |> List.filter (fun name -> not (files.ContainsKey name)) with
            | [] ->
                Ok
                    { Lock = lock
                      StylesheetPaths = EchelonThemePin.stylesheetPaths lock
                      Files = files }
            | missing -> Error $"""the Echelon theme tarball has no {String.Join(", ", missing)}""")

    let private compiled =
        lazy
            (ReleaseTarball.resource LockResource
             |> Result.bind (fun lockBytes ->
                 ReleaseTarball.resource TarballResource
                 |> Result.bind (load (Text.Encoding.UTF8.GetString lockBytes))))

    /// The release compiled into this CLI, verified once per process.
    let current () = compiled.Force()

    /// The stylesheet paths pages link, in load order; versioned paths that
    /// answer 404 when the release failed to load, so a failure is visible.
    let stylesheetPaths () =
        match current () with
        | Ok assets -> assets.StylesheetPaths
        | Error _ -> EchelonThemePin.stylesheets |> List.map (fun name -> $"/echelon-theme/unavailable/{name}")

    /// Serve `GET /echelon-theme/<version>/<stylesheet>` and
    /// `GET /echelon-theme/<version>/fonts/<file>.woff2`.
    let tryServe (httpMethod: string) (segments: string list) =
        match httpMethod, segments, current () with
        | "GET", "echelon-theme" :: version :: rest, Ok assets when version = assets.Lock.Version && not rest.IsEmpty ->
            assets.Files |> Map.tryFind (String.Join("/", rest))
        | _ -> None

/// The pinned presentation releases a page is served with: Forma's
/// stylesheet, then the Echelon theme's stylesheets and fonts.
[<RequireQualifiedAccess>]
module PresentationRelease =
    /// Every stylesheet a page links, in load order: Forma's all.css, then
    /// echelon.css, echelon-fonts.css and echelon-app.css.
    let stylesheetPaths () = FormaRelease.stylesheetPath () :: EchelonThemeRelease.stylesheetPaths ()

    /// A pinned presentation file and its content type, or None.
    let tryServe (httpMethod: string) (segments: string list) =
        FormaRelease.tryServe httpMethod segments
        |> Option.map (fun css -> "text/css; charset=utf-8", css)
        |> Option.orElseWith (fun () -> EchelonThemeRelease.tryServe httpMethod segments)

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
        $"<div class=\"forma-fault forma-fault--inline\" role=\"alert\" data-forma-intent=\"inline\" data-forma-severity=\"error\"><div class=\"forma-fault__marker\" aria-hidden=\"true\">!</div><div class=\"forma-fault__body\"><p class=\"forma-fault__severity\">Error</p><p class=\"forma-fault__message\">{escape message}</p></div></div>"

    /// A completed operation's confirmation (Forma alert).
    let notice (message: string) =
        $"<aside class=\"forma-alert\" role=\"status\" data-tone=\"ok\"><div class=\"forma-alert__icon\" aria-hidden=\"true\">&#10003;</div><div><p>{escape message}</p></div></aside>"

    /// An Aegis operational fault: the safe message and reference only
    /// (Forma fault banner; SAF-FORMA-6).
    let faultBanner (message: string) (reference: string) =
        $"<main id=\"main\" tabindex=\"-1\"><aside class=\"forma-alert forma-fault-banner\" role=\"alert\" data-forma-intent=\"banner\" data-forma-severity=\"error\" aria-labelledby=\"fault-title\"><div class=\"forma-alert__icon forma-fault-banner__marker\" aria-hidden=\"true\">!</div><div class=\"forma-fault-banner__body\"><p class=\"forma-fault__severity\">Error</p><h1 class=\"forma-alert__title\" id=\"fault-title\">Something went wrong</h1><p>{escape message}</p><p class=\"forma-fault-reference\">Reference <code>{escape reference}</code></p></div></aside></main>"

    /// The document shell every page uses: Forma first, then the Echelon
    /// theme, then the project's optional override stylesheet, a skip link and
    /// Forma's site typography.
    let head (stylesheetPaths: string list) =
        [ yield! stylesheetPaths |> List.map (fun path -> $"<link rel=\"stylesheet\" href=\"{escape path}\" />")
          "<link rel=\"stylesheet\" href=\"/styles.css\" />" ]

    /// The root element: the Echelon brand selects the theme's scoped rules.
    [<Literal>]
    let HtmlOpen = "<html lang=\"en\" data-forma-brand=\"" + EchelonThemePin.Brand + "\">"

    [<Literal>]
    let BodyOpen = "<body class=\"forma-site\"><a class=\"forma-skip-link\" href=\"#main\">Skip to main content</a><div class=\"forma-container\">"

    [<Literal>]
    let BodyClose = "</div></body>"
