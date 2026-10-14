namespace Praxis.Site

open System
open System.IO
open System.Net
open System.Text
open System.Text.RegularExpressions

/// Local preview server for the public site (docs/public-site.md). Serves only
/// site/, binds to loopback, and refuses path traversal. A development
/// convenience only; deployment is static hosting.
[<RequireQualifiedAccess>]
module Serve =
    let private types =
        Map
            [ ".html", "text/html; charset=utf-8"
              ".css", "text/css; charset=utf-8"
              ".js", "text/javascript; charset=utf-8"
              ".json", "application/json; charset=utf-8"
              ".svg", "image/svg+xml"
              ".png", "image/png"
              ".ico", "image/x-icon"
              ".txt", "text/plain; charset=utf-8" ]

    /// `path.extname`: a leading dot alone (".nojekyll") is not an extension.
    let contentType (file: string) =
        let name = Path.GetFileName file
        let dot = name.LastIndexOf('.')
        let extension = if dot <= 0 then "" else name.Substring dot
        types.TryFind extension |> Option.defaultValue "application/octet-stream"

    let private absolute = Regex(@"^[A-Za-z][A-Za-z0-9+.-]*://[^/?#]*", RegexOptions.CultureInvariant)
    let private singleDot = Regex(@"^(\.|%2e)\z", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)
    let private doubleDot = Regex(@"^(\.|%2e)(\.|%2e)\z", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    /// The pathname a WHATWG URL parser produces for a request target:
    /// query and fragment removed, backslashes as slashes, and "." and ".."
    /// segments (including their percent-encoded forms) collapsed.
    let pathname (url: string) =
        let withoutOrigin = absolute.Replace(url, "")
        let cut = withoutOrigin.IndexOfAny([| '?'; '#' |])
        let path = (if cut >= 0 then withoutOrigin.Substring(0, cut) else withoutOrigin).Replace('\\', '/')
        let segments = path.TrimStart('/').Split('/') |> List.ofArray
        let lastIndex = segments.Length - 1

        let kept =
            segments
            |> List.indexed
            |> List.fold
                (fun (acc: string list) (index, segment) ->
                    let last = index = lastIndex

                    if doubleDot.IsMatch segment then
                        let popped = match acc with _ :: rest -> rest | [] -> []
                        if last then "" :: popped else popped
                    elif singleDot.IsMatch segment then
                        if last then "" :: acc else acc
                    else
                        segment :: acc)
                []
            |> List.rev

        "/" + String.Join("/", kept)

    /// The file a request for `url` maps to under `root`, or `None` when it
    /// would escape `root`.
    let resolveRequest (root: string) (url: string) =
        try
            let decoded = Uri.UnescapeDataString(pathname url)
            let relative = if decoded.EndsWith("/", StringComparison.Ordinal) then decoded + "index.html" else decoded
            let full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, "." + relative)))

            if full = root || full.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
                Some full
            else
                None
        with _ ->
            None

    let private respond (siteRoot: string) (context: HttpListenerContext) =
        let response = context.Response

        try
            match resolveRequest siteRoot context.Request.RawUrl with
            | Some file when File.Exists file ->
                response.StatusCode <- 200
                response.ContentType <- contentType file
                response.Headers["cache-control"] <- "no-store"
                let bytes = File.ReadAllBytes file
                response.ContentLength64 <- int64 bytes.Length
                response.OutputStream.Write(bytes, 0, bytes.Length)
            | found ->
                let status, body = if found.IsNone then 403, "Forbidden\n" else 404, "Not found\n"
                let bytes = Encoding.UTF8.GetBytes(body: string)
                response.StatusCode <- status
                response.ContentType <- "text/plain; charset=utf-8"
                response.ContentLength64 <- int64 bytes.Length
                response.OutputStream.Write(bytes, 0, bytes.Length)
        finally
            response.Close()

    /// Serves `root`/site on 127.0.0.1:`port` until the process ends.
    let run (root: string) (port: int) =
        let siteRoot = Path.Combine(root, "site")
        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()
        let shown = Repository.relative Environment.CurrentDirectory siteRoot
        printfn "Praxis public site: http://127.0.0.1:%d/ (serving %s)" port (if shown = "" then "." else shown)

        while listener.IsListening do
            let context = listener.GetContext()
            respond siteRoot context
