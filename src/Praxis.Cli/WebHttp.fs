namespace Praxis.Cli

open System
open System.IO
open System.Net
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Web
open Praxis.Infrastructure.Boundary
open Praxis.Infrastructure.Foundations

/// A request as the web adapters see it: already read off the wire, so every
/// routing and parsing decision below is a pure function of this value.
type HttpRequestData =
    { Method: string
      /// Path segments, percent-decoded (`/api/work/WI-1` -> `["api"; "work"; "WI-1"]`).
      Segments: string list
      Query: (string * string) list
      ContentType: string option
      Body: byte array
      /// Request headers, names lower-cased (the Cookie and Host the URL gate reads).
      Headers: (string * string) list }

type HttpResponseData =
    { Status: int
      ContentType: string
      Headers: (string * string) list
      Body: byte array }

/// One `multipart/form-data` part. `FileName` is `Some` for a file input
/// (possibly `Some ""` when the browser submitted an empty file picker).
type MultipartPart =
    { Name: string
      FileName: string option
      ContentType: string option
      Data: byte array }

/// An uploaded file, paired with the display name it should be stored under.
type Upload = { Name: string; Data: byte array }

/// A decoded request body. Form bodies (urlencoded or multipart) carry their
/// text fields in submission order and their non-empty file uploads.
type RequestBody =
    | NoBody
    | JsonBody of JsonNode
    | FormBody of fields: (string * string) list * uploads: Upload list

[<RequireQualifiedAccess>]
module Html =
    /// Escapes text for both element content and double-quoted attributes.
    let escape (value: string) =
        let builder = StringBuilder(value.Length)

        for character in value do
            match character with
            | '&' -> builder.Append "&amp;" |> ignore
            | '<' -> builder.Append "&lt;" |> ignore
            | '>' -> builder.Append "&gt;" |> ignore
            | '"' -> builder.Append "&quot;" |> ignore
            | '\'' -> builder.Append "&#39;" |> ignore
            | other -> builder.Append other |> ignore

        builder.ToString()

    /// A path segment for a URL the page links or posts to.
    let segment (value: string) = Uri.EscapeDataString value

    let queryString (pairs: (string * string) list) =
        match pairs |> List.filter (fun (_, value) -> not (String.IsNullOrEmpty value)) with
        | [] -> ""
        | present ->
            present
            |> List.map (fun (name, value) -> $"{Uri.EscapeDataString name}={Uri.EscapeDataString value}")
            |> String.concat "&"
            |> sprintf "?%s"

    let formatSize (bytes: int64) =
        if bytes < 1024L then $"{bytes} B"
        elif bytes < 1024L * 1024L then (float bytes / 1024.0).ToString("0.0", Globalization.CultureInfo.InvariantCulture) + " KB"
        else (float bytes / (1024.0 * 1024.0)).ToString("0.0", Globalization.CultureInfo.InvariantCulture) + " MB"

    let options (selected: string) (values: string list) =
        values
        |> List.map (fun value ->
            let mark = if value = selected then " selected" else ""
            $"<option value=\"{escape value}\"{mark}>{escape value}</option>")
        |> String.concat ""

    /// The notice/error banner every page shows after a post/redirect/get.
    let flash (query: (string * string) list) =
        let pick name =
            query |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.filter (String.IsNullOrWhiteSpace >> not)

        [ pick "notice" |> Option.map FormaMarkup.notice
          pick "error" |> Option.map FormaMarkup.alert ]
        |> List.choose id
        |> String.concat "\n"

    /// An operational fault: the safe message and its reference only (Forma fault banner).
    let fault (message: string) (reference: string) = FormaMarkup.faultBanner message reference
    let page (title: string) (body: string) =
        String.concat "\n"
            [ "<!doctype html>"
              "<html lang=\"en\">"
              "<head>"
              "<meta charset=\"utf-8\" />"
              "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />"
              $"<title>{escape title}</title>"
              yield! FormaMarkup.head (FormaRelease.stylesheetPath ())
              "</head>"
              FormaMarkup.BodyOpen
              body
              FormaMarkup.BodyClose
              "</html>"
              "" ]

[<RequireQualifiedAccess>]
module HttpMessages =
    let private utf8 = UTF8Encoding(false)

    let private jsonOptions =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// Node-compatible `JSON.stringify(value, null, 2)` rendering.
    let renderJson (node: JsonNode) = node.ToJsonString jsonOptions

    let response status contentType (body: byte array) =
        { Status = status
          ContentType = contentType
          Headers = []
          Body = body }

    let json status (text: string) =
        response status "application/json; charset=utf-8" (utf8.GetBytes text)

    let jsonNode status (node: JsonNode) = json status (renderJson node)

    let jsonError status (message: string) =
        let node = JsonObject()
        node["error"] <- JsonValue.Create message
        jsonNode status node

    let html status (text: string) =
        response status "text/html; charset=utf-8" (utf8.GetBytes text)

    let text status (text: string) =
        response status "text/plain; charset=utf-8" (utf8.GetBytes text)

    let css (text: string) =
        response 200 "text/css; charset=utf-8" (utf8.GetBytes text)

    /// 303 See Other: the post/redirect/get step after every form post. A
    /// `notice` or `error` in the target moves to the flash cookie, so the
    /// address bar never carries transient feedback (SAF-URL-5).
    let redirect (target: string) =
        let location, feedback = UrlState.splitFlash target

        { Status = 303
          ContentType = "text/plain; charset=utf-8"
          Headers = ("Location", location) :: (feedback |> Option.map (fun (kind, message) -> "Set-Cookie", UrlState.flash kind message) |> Option.toList)
          Body = utf8.GetBytes $"See {location}" }

    /// Splits `a=1&b=two+words` into ordered, decoded pairs.
    let parseUrlEncoded (text: string) : (string * string) list =
        let decode (value: string) = WebUtility.UrlDecode value

        text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun pair ->
            match pair.IndexOf '=' with
            | -1 -> decode pair, ""
            | index -> decode (pair.Substring(0, index)), decode (pair.Substring(index + 1)))
        |> Array.toList

    let pathSegments (rawPath: string) =
        rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map Uri.UnescapeDataString
        |> Array.toList

    /// Reads one `; key=value` parameter from a header value (Praxis.Application.Web.HttpHeaders).
    let headerParameter = HttpHeaders.parameter

    let private indexOf (haystack: byte array) (needle: byte array) (start: int) =
        if start >= haystack.Length then
            -1
        else
            let found = MemoryExtensions.IndexOf(ReadOnlySpan<byte>(haystack, start, haystack.Length - start), ReadOnlySpan<byte>(needle))
            if found < 0 then -1 else found + start

    /// Parses a `multipart/form-data` body into its parts, in order.
    let parseMultipart (boundary: string) (body: byte array) : Result<MultipartPart list, string> =
        let delimiter = Encoding.ASCII.GetBytes("--" + boundary)
        let separator = Encoding.ASCII.GetBytes("\r\n--" + boundary)
        let headerEnd = Encoding.ASCII.GetBytes "\r\n\r\n"

        let parseHeaders (text: string) =
            text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            |> Array.choose (fun line ->
                match line.IndexOf ':' with
                | -1 -> None
                | index -> Some(line.Substring(0, index).Trim().ToLowerInvariant(), line.Substring(index + 1).Trim()))
            |> Array.toList

        let rec loop (position: int) (acc: MultipartPart list) =
            // `position` sits just after a delimiter.
            if position + 2 <= body.Length && body[position] = byte '-' && body[position + 1] = byte '-' then
                Ok(List.rev acc)
            elif position + 2 > body.Length || body[position] <> byte '\r' || body[position + 1] <> byte '\n' then
                Error "malformed multipart body: expected a line break after the boundary"
            else
                let headersStart = position + 2

                match indexOf body headerEnd headersStart with
                | -1 -> Error "malformed multipart body: unterminated part headers"
                | headersStop ->
                    let headers = parseHeaders (Encoding.UTF8.GetString(body, headersStart, headersStop - headersStart))
                    let contentStart = headersStop + headerEnd.Length

                    match indexOf body separator contentStart with
                    | -1 -> Error "malformed multipart body: missing closing boundary"
                    | contentStop ->
                        let header name = headers |> List.tryFind (fst >> (=) name) |> Option.map snd

                        match header "content-disposition" with
                        | None -> Error "malformed multipart body: a part has no Content-Disposition"
                        | Some disposition ->
                            match headerParameter "name" disposition with
                            | None -> Error "malformed multipart body: a part has no field name"
                            | Some fieldName ->
                                let part =
                                    { Name = fieldName
                                      FileName = headerParameter "filename" disposition
                                      ContentType = header "content-type"
                                      Data = body[contentStart .. contentStop - 1] }

                                loop (contentStop + separator.Length) (part :: acc)

        match indexOf body delimiter 0 with
        | -1 -> Error "malformed multipart body: boundary not found"
        | start -> loop (start + delimiter.Length) []

    /// Pairs each `file` part with the `name` text field at the same position
    /// (the optional display-name override next to each file picker), falling
    /// back to the uploaded file's own name, and drops empty pickers.
    let uploadsFrom (parts: MultipartPart list) : Upload list =
        let files = parts |> List.filter (fun part -> part.Name = "file" && part.FileName.IsSome)

        let names =
            parts
            |> List.filter (fun part -> part.Name = "name" && part.FileName.IsNone)
            |> List.map (fun part -> Encoding.UTF8.GetString(part.Data).Trim())

        files
        |> List.mapi (fun index part ->
            let fileName = part.FileName |> Option.defaultValue ""

            let displayName =
                match names |> List.tryItem index with
                | Some name when name <> "" -> name
                | _ -> Path.GetFileName fileName

            if fileName = "" && part.Data.Length = 0 then None else Some { Name = displayName; Data = part.Data })
        |> List.choose id

    let fieldsFrom (parts: MultipartPart list) =
        parts
        |> List.filter (fun part -> part.FileName.IsNone)
        |> List.map (fun part -> part.Name, Encoding.UTF8.GetString part.Data)

    let private mediaType (contentType: string option) =
        contentType
        |> Option.map (fun value -> value.Split(';').[0].Trim().ToLowerInvariant())
        |> Option.defaultValue ""

    /// Decodes a request body by its Content-Type.
    let parseBody (request: HttpRequestData) : Result<RequestBody, string> =
        match mediaType request.ContentType with
        | _ when request.Body.Length = 0 -> Ok NoBody
        | "multipart/form-data" ->
            match request.ContentType |> Option.bind (headerParameter "boundary") with
            | None -> Error "multipart/form-data request has no boundary"
            | Some boundary ->
                parseMultipart boundary request.Body
                |> Result.map (fun parts -> FormBody(fieldsFrom parts, uploadsFrom parts))
        | "application/x-www-form-urlencoded" -> Ok(FormBody(parseUrlEncoded (Encoding.UTF8.GetString request.Body), []))
        | _ ->
            try
                match JsonNode.Parse(Encoding.UTF8.GetString request.Body) with
                | null -> Ok NoBody
                | node -> Ok(JsonBody node)
            with error ->
                Error $"invalid JSON body: {error.Message}"

    let field (name: string) (fields: (string * string) list) =
        fields |> List.tryFind (fst >> (=) name) |> Option.map snd

    let fieldValues (name: string) (fields: (string * string) list) =
        fields |> List.filter (fst >> (=) name) |> List.map snd

    /// Splits a comma-separated tag field, trimming and dropping empties.
    let splitTags (value: string) =
        value.Split(',') |> Array.map (fun tag -> tag.Trim()) |> Array.filter ((<>) "") |> Array.toList

    let nonBlank (value: string option) =
        value |> Option.map (fun text -> text.Trim()) |> Option.filter ((<>) "")

    let jsonString (name: string) (body: RequestBody) : string option =
        match body with
        | JsonBody(:? JsonObject as node) ->
            match node[name] with
            | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
            | _ -> None
        | FormBody(fields, _) -> field name fields
        | _ -> None

    /// A string-array JSON field (`tags`), or the comma-separated form field
    /// of the same name. `None` when the field is absent altogether.
    let stringList (name: string) (body: RequestBody) : string list option =
        match body with
        | JsonBody(:? JsonObject as node) ->
            match node[name] with
            | :? JsonArray as values ->
                values
                |> Seq.choose (fun item ->
                    match item with
                    | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
                    | _ -> None)
                |> Seq.toList
                |> Some
            | _ -> None
        | FormBody(fields, _) -> field name fields |> Option.map splitTags
        | _ -> None

    let uploads (body: RequestBody) =
        match body with
        | FormBody(_, files) -> files
        | _ -> []

[<RequireQualifiedAccess>]
module HttpHost =
    let private maxFormBytes = 1_000_000L
    let private maxUploadBytes = 25_000_000L

    let private readBody (request: HttpListenerRequest) : Result<byte array, int * string> =
        let isMultipart =
            request.ContentType
            |> Option.ofObj
            |> Option.exists (fun value -> value.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))

        let limit = if isMultipart then maxUploadBytes else maxFormBytes

        if request.ContentLength64 > limit then
            Error(413, $"request body exceeds {limit} bytes")
        elif not request.HasEntityBody then
            Ok [||]
        else
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 81920
            let mutable total = 0L
            let mutable tooLarge = false
            let mutable read = request.InputStream.Read(chunk, 0, chunk.Length)

            while read > 0 && not tooLarge do
                total <- total + int64 read

                if total > limit then
                    tooLarge <- true
                else
                    buffer.Write(chunk, 0, read)
                    read <- request.InputStream.Read(chunk, 0, chunk.Length)

            if tooLarge then Error(413, $"request body exceeds {limit} bytes") else Ok(buffer.ToArray())

    let private write (context: HttpListenerContext) (response: HttpResponseData) =
        let output = context.Response
        output.StatusCode <- response.Status
        output.ContentType <- response.ContentType
        output.Headers["X-Content-Type-Options"] <- "nosniff"
        output.Headers["Content-Security-Policy"] <- UrlEnhancement.contentSecurityPolicy

        for name, value in response.Headers do
            output.Headers[name] <- value

        output.ContentLength64 <- int64 response.Body.Length
        output.OutputStream.Write(response.Body, 0, response.Body.Length)
        output.Close()

    /// One request's answer; an unexpected failure is recorded at the web
    /// boundary (SAF-AEGIS-1) and answered with a safe message and reference.
    let respond (aegis: Aegis.AegisConfig) (handler: HttpRequestData -> HttpResponseData) (data: HttpRequestData) =
        match AegisBoundary.capture aegis OperationalBoundary.WebRequest $"web.{data.Method.ToLowerInvariant()}" None "could not answer the request" (fun () -> FormaRelease.tryServe data.Method data.Segments |> Option.map (HttpMessages.response 200 "text/css; charset=utf-8") |> Option.defaultWith (fun () -> handler data)) with
        | Ok response -> response
        | Error fault -> HttpMessages.html 500 (Html.page "Error" (Html.fault fault.UserMessage (Aegis.Presentation.reference fault)))
    let private handle (aegis: Aegis.AegisConfig) (handler: HttpRequestData -> HttpResponseData) (context: HttpListenerContext) =
        let request = context.Request
        let answer () =
            match readBody request with
            | Error(status, message) -> HttpMessages.text status message
            | Ok body ->
                { Method = request.HttpMethod.ToUpperInvariant(); Segments = HttpMessages.pathSegments request.Url.AbsolutePath
                  Query = HttpMessages.parseUrlEncoded request.Url.Query; ContentType = request.ContentType |> Option.ofObj; Body = body
                  Headers = [ for name in request.Headers.AllKeys do if not (isNull name) then yield name.ToLowerInvariant(), request.Headers[name] ] }
                |> respond aegis handler

        // A disconnecting client is a recorded web fault; the connection is aborted.
        match AegisBoundary.capture aegis OperationalBoundary.WebRequest "web.connection" None "could not complete the connection" (fun () -> write context (answer ())) with
        | Ok() -> ()
        | Error _ -> context.Response.Abort()

    /// The listener prefixes for a host. A loopback bind answers to both
    /// `127.0.0.1` and `localhost`, since the listener matches on the Host
    /// header a browser sends; a wildcard host binds every interface.
    let prefixes (host: string) (port: int) =
        match host with
        | "127.0.0.1"
        | "localhost" -> [ $"http://127.0.0.1:{port}/"; $"http://localhost:{port}/" ]
        | "0.0.0.0"
        | "*"
        | "+" -> [ $"http://+:{port}/" ]
        | other when other.Contains ':' && not (other.StartsWith "[") -> [ $"http://[{other}]:{port}/" ]
        | other -> [ $"http://{other}:{port}/" ]

    /// Serves `handler` until the process is interrupted (Ctrl+C / SIGTERM).
    /// Requests are handled concurrently; every state change the handlers
    /// make goes through the CLI's own locking.
    let serve (host: string) (port: int) (banner: string list) (handler: HttpRequestData -> HttpResponseData) : int =
        match AegisBoundary.configure None [ Aegis.Sinks.console ] with
        | Error problems -> problems |> List.iter (eprintfn "ERROR Aegis configuration: %s"); 1
        | Ok aegis ->
            use listener = new HttpListener()
            prefixes host port |> List.iter listener.Prefixes.Add

            match
                (try
                    listener.Start()
                    Ok()
                 with error ->
                     Error error.Message)
            with
            | Error message ->
                eprintfn "ERROR cannot listen on %s:%d: %s" host port message
                1
            | Ok() ->
                banner |> List.iter (printfn "%s")
                Console.Out.Flush()
                use stopped = new ManualResetEventSlim(false)

                let stop () =
                    if not stopped.IsSet then
                        stopped.Set()

                        try
                            listener.Stop()
                        with _ ->
                            ()

                use _cancel =
                    Console.CancelKeyPress.Subscribe(fun args ->
                        args.Cancel <- true
                        stop ())

                use _terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                                     System.Runtime.InteropServices.PosixSignal.SIGTERM,
                                     fun context ->
                                         context.Cancel <- true
                                         stop ())

                while not stopped.IsSet do
                    try
                        let context = listener.GetContext()
                        Task.Run(fun () -> handle aegis handler context) |> ignore
                    with _ when stopped.IsSet ->
                        ()

                0

/// Every GET page of `web serve` and `hub serve` passes here first
/// (SAF-URL-1..10, DF-ROS-2026-A057): a canonical location renders, any other
/// spelling of a view redirects to its canonical URL, and a location that is no
/// view renders its typed refusal with a way back. A shown flash is cleared,
/// and the page offers its canonical URL as "Link to this view".
[<RequireQualifiedAccess>]
module UrlGate =
    let private header name (request: HttpRequestData) =
        request.Headers |> List.tryPick (fun (key, value) -> if key = name then Some value else None)

    let refused (problem: Limen.Routing.RouteError) =
        let status, title, message = UrlState.refusal problem
        let back = "<p><a class=\"ef-button\" href=\"/\">&larr; Back to the start</a></p>"
        HttpMessages.html status (Html.page $"{title} · Praxis" $"<main id=\"main\" tabindex=\"-1\"><h1>{Html.escape title}</h1>{FormaMarkup.alert message}{back}</main>")

    let private linkSection (share: string) (location: string) =
        $"<section class=\"ef-section\" aria-label=\"Link to this view\"><h2>Link to this view</h2><p><a href=\"{Html.escape location}\" rel=\"bookmark\">{Html.escape share}</a></p><p>Copy it with your browser's Copy Link, or select it here:</p><input id=\"share-url\" type=\"text\" readonly value=\"{Html.escape share}\" aria-label=\"Address of this view\" /><p><button type=\"button\" hidden data-copy=\"share-url\" data-status=\"share-status\">Copy link</button> <span id=\"share-status\" role=\"status\"></span></p></section>\n</main>"

    let private decorate (share: string) (location: string) (shown: bool) (response: HttpResponseData) =
        let page = response.Status = 200 && response.ContentType.StartsWith("text/html", StringComparison.Ordinal)
        let body = if page then Encoding.UTF8.GetBytes((Encoding.UTF8.GetString response.Body).Replace("</main>", linkSection share location).Replace("</head>", UrlEnhancement.scriptTag + "\n</head>")) else response.Body
        { response with Body = body; Headers = response.Headers @ (if shown then [ "Set-Cookie", UrlState.clearFlash ] else []) }

    let serve (routing: Result<Routing<'View>, Limen.Routing.DefinitionError list>) (handler: HttpRequestData -> HttpResponseData) (request: HttpRequestData) =
        match request.Method, request.Segments with
        | "GET", ("api" :: _ | [ "styles.css" ]) -> handler request
        | "GET", [ "url-state.js" ] -> HttpMessages.response 200 "text/javascript; charset=utf-8" UrlEnhancement.bytes
        | "GET", segments ->
            let path = "/" + String.Join("/", segments |> List.map Html.segment)

            match UrlState.resolve routing path request.Query with
            | Canonical location -> HttpMessages.redirect location
            | Refused problem -> refused problem
            | Show _ ->
                let flash = UrlState.readFlash (header "cookie" request) |> Option.toList
                let location = UrlState.locationOf path request.Query
                let share = UrlState.share (header "host" request |> Option.defaultValue "localhost") location
                decorate share location (not flash.IsEmpty) (handler { request with Query = request.Query @ flash })
        | _ -> handler request
