namespace Praxis.Application.Web

open System
open System.Text

/// Pure HTTP header parsing for the web adapters.
[<RequireQualifiedAccess>]
module HttpHeaders =
    /// Reads one `; key=value` parameter from a header value, honouring
    /// quoted values and RFC 5987 `key*=utf-8''...` extended values.
    let parameter (name: string) (headerValue: string) : string option =
        let parameters =
            let result = ResizeArray<string * string>()
            let mutable index = 0
            let length = headerValue.Length

            while index < length do
                // skip to after the next ';'
                let separator = headerValue.IndexOf(';', index)

                if separator < 0 then
                    index <- length
                else
                    index <- separator + 1

                    while index < length && Char.IsWhiteSpace headerValue[index] do
                        index <- index + 1

                    let equals = headerValue.IndexOf('=', index)

                    if equals > index then
                        let key = headerValue.Substring(index, equals - index).Trim().ToLowerInvariant()
                        let valueStart = equals + 1

                        if valueStart < length && headerValue[valueStart] = '"' then
                            let builder = StringBuilder()
                            let mutable cursor = valueStart + 1
                            let mutable closed = false

                            while cursor < length && not closed do
                                match headerValue[cursor] with
                                | '\\' when cursor + 1 < length ->
                                    builder.Append headerValue[cursor + 1] |> ignore
                                    cursor <- cursor + 2
                                | '"' ->
                                    closed <- true
                                    cursor <- cursor + 1
                                | other ->
                                    builder.Append other |> ignore
                                    cursor <- cursor + 1

                            result.Add(key, builder.ToString())
                            index <- cursor
                        else
                            let next = headerValue.IndexOf(';', valueStart)
                            let valueEnd = if next < 0 then length else next
                            result.Add(key, headerValue.Substring(valueStart, valueEnd - valueStart).Trim())
                            index <- valueEnd

            result |> Seq.toList

        let lowered = name.ToLowerInvariant()

        let extended =
            parameters
            |> List.tryFind (fst >> (=) (lowered + "*"))
            |> Option.bind (fun (_, value) ->
                match value.IndexOf("''", StringComparison.Ordinal) with
                | -1 -> None
                | index ->
                    try
                        Some(Uri.UnescapeDataString(value.Substring(index + 2)))
                    with _ ->
                        None)

        extended |> Option.orElse (parameters |> List.tryFind (fst >> (=) lowered) |> Option.map snd)
