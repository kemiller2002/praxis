namespace Praxis.Site

open System
open System.Globalization
open System.Text.RegularExpressions

/// One start or end tag.
type Token =
    { Name: string
      Closing: bool
      Attributes: Map<string, string>
      SelfClosing: bool
      Index: int }

/// Pure checks for the public site (docs/public-site.md). The site is small,
/// static and hand-written, so a strict tokenizer is enough and keeps the
/// repository free of a validator dependency tree.
[<RequireQualifiedAccess>]
module Html =
    let private voidElements =
        set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "source"; "track"; "wbr" ]

    let private options = RegexOptions.CultureInvariant

    let private tagPattern =
        Regex(
            """<!--[\s\S]*?-->|<!doctype[^>]*>|<\/?([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[^\s"'>\/=]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(\/?)>""",
            options ||| RegexOptions.IgnoreCase
        )

    let private attributePattern =
        Regex("""([^\s"'>\/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?""", options)

    let private attributesOf (raw: string) =
        attributePattern.Matches(raw)
        |> Seq.fold
            (fun (attributes: Map<string, string>) found ->
                let value =
                    [ 2; 3; 4 ]
                    |> List.tryPick (fun group -> if found.Groups[group].Success then Some found.Groups[group].Value else None)
                    |> Option.defaultValue ""

                attributes.Add(found.Groups[1].Value.ToLowerInvariant(), value))
            Map.empty

    /// Tokenizes HTML into start and end tags with attributes, skipping
    /// comments, the doctype, and the raw text inside <script> and <style>.
    let tokenize (html: string) =
        tagPattern.Matches(html)
        |> Seq.fold
            (fun (tokens: Token list, rawUntil: string option) found ->
                let rawName = found.Groups[1]

                if not rawName.Success then
                    tokens, rawUntil
                else
                    let name = rawName.Value.ToLowerInvariant()
                    let closing = found.Value.StartsWith("</", StringComparison.Ordinal)

                    match rawUntil with
                    | Some until when not (closing && name = until) -> tokens, rawUntil
                    | _ ->
                        let token =
                            { Name = name
                              Closing = closing
                              Attributes = attributesOf found.Groups[2].Value
                              SelfClosing = found.Groups[3].Value = "/"
                              Index = found.Index }

                        let next = if not closing && (name = "script" || name = "style") then Some name else None
                        token :: tokens, next)
            ([], None)
        |> fst
        |> List.rev

    let private lineOf (html: string) (index: int) =
        1 + (html.AsSpan(0, index).Count('\n'))

    let private attribute (name: string) (token: Token) = token.Attributes.TryFind name

    let private jsText (value: string option) = defaultArg value "undefined"

    /// Structural well-formedness: every non-void element is closed in order.
    let structureProblems (html: string) =
        let problems, stack =
            tokenize html
            |> List.fold
                (fun (problems: string list, stack: Token list) token ->
                    if voidElements.Contains token.Name then
                        if token.Closing then
                            $"line {lineOf html token.Index}: </{token.Name}> closes a void element" :: problems, stack
                        else
                            problems, stack
                    elif not token.Closing then
                        problems, (if token.SelfClosing then stack else token :: stack)
                    else
                        match stack with
                        | openToken :: rest when openToken.Name = token.Name -> problems, rest
                        | openToken :: rest ->
                            $"line {lineOf html token.Index}: </{token.Name}> does not close <{openToken.Name}>" :: problems, rest
                        | [] -> $"line {lineOf html token.Index}: </{token.Name}> does not close <nothing>" :: problems, [])
                ([], [])

        let unclosed =
            stack |> List.rev |> List.map (fun openToken -> $"line {lineOf html openToken.Index}: <{openToken.Name}> is never closed")

        List.rev problems @ unclosed

    let private tagsPattern = Regex("<[^>]+>", options)
    let private spaces = Regex(@"\s+", options)

    let private textBetween (html: string) (start: int) (endName: string) =
        let close = html.ToLowerInvariant().IndexOf($"</{endName}>", start, StringComparison.Ordinal)

        if close < 0 then
            ""
        else
            spaces.Replace(tagsPattern.Replace(html.Substring(start, close - start), " "), " ").Trim()

    let private headingPattern = Regex(@"^h[1-6]\z", options)
    let private meaningless = Regex(@"^(click here|here|read more|more|link)\z", options ||| RegexOptions.IgnoreCase)

    /// Accessibility and semantics rules that can be decided from markup alone.
    let accessibilityProblems (html: string) =
        let starts = tokenize html |> List.filter (fun token -> not token.Closing)
        let named name = starts |> List.filter (fun token -> token.Name = name)

        let language =
            match named "html" with
            | first :: _ when (attribute "lang" first |> Option.defaultValue "") <> "" -> []
            | _ -> [ "<html> needs a lang attribute" ]

        let title = if (named "title").IsEmpty then [ "document needs a <title>" ] else []

        let landmarks =
            [ "main"; "header"; "footer"; "nav" ]
            |> List.filter (fun landmark -> (named landmark).IsEmpty)
            |> List.map (fun landmark -> $"missing <{landmark}> landmark")

        let oneMain = if (named "main").Length > 1 then [ "more than one <main>" ] else []

        let headings =
            starts
            |> List.filter (fun token -> headingPattern.IsMatch token.Name)
            |> List.map (fun token -> int (token.Name[1]) - int '0')

        let oneH1 =
            if (headings |> List.filter (fun level -> level = 1)).Length <> 1 then
                [ "exactly one <h1> is required" ]
            else
                []

        let jumps =
            headings
            |> List.pairwise
            |> List.filter (fun (previous, level) -> level > previous + 1)
            |> List.map (fun (previous, level) -> $"heading level jumps from h{previous} to h{level}")

        let ids =
            starts
            |> List.choose (fun token ->
                match attribute "id" token with
                | Some id when id <> "" -> Some id
                | _ -> None)

        let duplicates =
            ids
            |> List.indexed
            |> List.filter (fun (index, id) -> List.findIndex ((=) id) ids <> index)
            |> List.map snd
            |> List.distinct

        let duplicateProblem =
            if duplicates.IsEmpty then [] else [ $"""duplicate ids: {String.Join(", ", duplicates)}""" ]

        let idSet = Set.ofList ids

        let inPage =
            named "a"
            |> List.choose (attribute "href")
            |> List.filter (fun href -> href.StartsWith("#", StringComparison.Ordinal) && href.Length > 1)
            |> List.filter (fun href -> not (idSet.Contains(href.Substring 1)))
            |> List.map (fun href -> $"in-page link {href} has no target")

        let labelledBy =
            starts
            |> List.choose (fun token ->
                match attribute "aria-labelledby" token with
                | Some value when value <> "" -> Some value
                | _ -> None)
            |> List.collect (fun value -> Regex.Split(value, @"\s+") |> List.ofArray)
            |> List.filter (fun id -> not (idSet.Contains id))
            |> List.map (fun id -> $"aria-labelledby references missing id {id}")

        let images =
            named "img"
            |> List.filter (fun token -> not (token.Attributes.ContainsKey "alt"))
            |> List.map (fun token ->
                let src = jsText (attribute "src" token)
                $"<img src=\"{src}\"> has no alt")

        let windows =
            named "a"
            |> List.filter (fun token ->
                attribute "target" token = Some "_blank"
                && not ((attribute "rel" token |> Option.defaultValue "").Contains("noopener", StringComparison.Ordinal)))
            |> List.map (fun token ->
                let href = jsText (attribute "href" token)
                $"{href} opens a new window without rel=\"noopener\"")

        let names =
            starts
            |> List.filter (fun token -> token.Name = "a" || token.Name = "button")
            |> List.collect (fun token ->
                let label =
                    match attribute "aria-label" token with
                    | Some label -> label
                    | None -> textBetween html (html.IndexOf('>', token.Index) + 1) token.Name

                [ if label = "" then
                      $"<{token.Name}> at line {lineOf html token.Index} has no accessible name"
                  if meaningless.IsMatch label then
                      $"link text \"{label}\" is not meaningful" ])

        let skip =
            if named "a" |> List.exists (fun token -> attribute "href" token = Some "#main") then
                []
            else
                [ "missing skip link to #main" ]

        List.concat
            [ language
              title
              landmarks
              oneMain
              oneH1
              jumps
              duplicateProblem
              inPage
              labelledBy
              images
              windows
              names
              skip ]

    let private external = Regex("^(https?:|mailto:)", options ||| RegexOptions.IgnoreCase)
    let private queryOrFragment = Regex("[?#]", options)

    /// Every reference must be relative and must resolve inside the site, so the
    /// same files work at a domain root and at a GitHub Pages project path.
    let referenceProblems (html: string) (exists: string -> bool) =
        tokenize html
        |> List.filter (fun token -> not token.Closing)
        |> List.collect (fun token ->
            [ "href"; "src" ]
            |> List.choose (fun name -> attribute name token |> Option.map (fun value -> token, value)))
        |> List.collect (fun (token, value) ->
            if external.IsMatch value || value.StartsWith("#", StringComparison.Ordinal) then
                []
            elif value.StartsWith("/", StringComparison.Ordinal) then
                [ $"<{token.Name}> uses root-relative {value}; use a relative path" ]
            else
                let file = queryOrFragment.Split(value)[0]
                if exists file then [] else [ $"<{token.Name}> references missing {value}" ])

    // JavaScript's \b and \w are ASCII-only; ECMAScript mode matches that.
    let private boundaryRules =
        [ Regex(@"\b(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])\b", RegexOptions.ECMAScript ||| RegexOptions.IgnoreCase), "loopback host"
          Regex(@"\/api\/", options ||| RegexOptions.IgnoreCase), "operational API path"
          Regex(@"(?:^|[\s""'(])\/(?:home|Users|root|tmp|var)\/", options), "local filesystem path"
          Regex(@"[A-Za-z]:\\\\?(?:Users|home)", options), "local filesystem path"
          Regex(@"\b(?:sessionId|conversationId|runId)\b", RegexOptions.ECMAScript), "session identifier field"
          Regex(
              @"\bsk-[A-Za-z0-9_-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----",
              RegexOptions.ECMAScript
          ),
          "credential"
          Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", options ||| RegexOptions.IgnoreCase),
          "UUID (possible session identifier)" ]

    /// The public site must never point at the operational Praxis HTTP adapter
    /// or at anything local, and must never carry credential-shaped values.
    let boundaryProblems (name: string) (text: string) =
        boundaryRules
        |> List.filter (fun (pattern, _) -> pattern.IsMatch text)
        |> List.map (fun (_, label) -> $"{name}: contains a {label}")

    let private channel (value: int) =
        let c = float value / 255.0
        if c <= 0.03928 then c / 12.92 else ((c + 0.055) / 1.055) ** 2.4

    /// WCAG 2.x relative luminance of a #rrggbb colour.
    let luminance (hex: string) =
        let part (start: int) =
            Int32.Parse(hex.Substring(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        0.2126 * channel (part 1) + 0.7152 * channel (part 3) + 0.0722 * channel (part 5)

    /// WCAG 2.x contrast ratio.
    let contrast (a: string) (b: string) =
        let hi = max (luminance a) (luminance b)
        let lo = min (luminance a) (luminance b)
        (hi + 0.05) / (lo + 0.05)
