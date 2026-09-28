namespace Praxis.Site

open System
open System.Globalization
open System.Text
open System.Text.Json

/// An immutable, order-preserving JSON value. The snapshot format is plain
/// JSON whose key order and layout are part of the published contract, so the
/// site tools read and write it through this type rather than a mutable DOM.
/// Numbers keep their source text; `undefined` (an absent property) is `None`.
type Json =
    | JNull
    | JBool of bool
    | JNumber of string
    | JString of string
    | JArray of Json list
    | JObject of (string * Json) list

[<RequireQualifiedAccess>]
module Json =
    let private numberText (raw: string) =
        match Int64.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, value -> value.ToString(CultureInfo.InvariantCulture)
        | _ ->
            match Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when Math.Floor value = value && abs value < 1e21 -> value.ToString("F0", CultureInfo.InvariantCulture)
            | true, value -> value.ToString("R", CultureInfo.InvariantCulture)
            | _ -> raw

    let rec private ofElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.fold
                (fun (acc: (string * Json) list) property ->
                    let value = ofElement property.Value
                    // JSON.parse keeps the last duplicate at the first position.
                    if acc |> List.exists (fun (key, _) -> key = property.Name) then
                        acc |> List.map (fun (key, old) -> if key = property.Name then key, value else key, old)
                    else
                        acc @ [ property.Name, value ])
                []
            |> JObject
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.map ofElement |> List.ofSeq |> JArray
        | JsonValueKind.String -> JString(element.GetString())
        | JsonValueKind.Number -> JNumber(numberText (element.GetRawText()))
        | JsonValueKind.True -> JBool true
        | JsonValueKind.False -> JBool false
        | _ -> JNull

    let parse (text: string) =
        use document = JsonDocument.Parse(text)
        ofElement document.RootElement

    let str (value: string) = JString value

    let orNull (value: string option) =
        match value with
        | Some text -> JString text
        | None -> JNull

    /// Property lookup; `None` is JavaScript's `undefined`.
    let prop (name: string) (value: Json option) =
        match value with
        | Some(JObject fields) -> fields |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd
        | _ -> None

    let get (name: string) (value: Json) = prop name (Some value)

    let items (value: Json option) =
        match value with
        | Some(JArray values) -> values
        | _ -> []

    let text (value: Json option) =
        match value with
        | Some(JString text) -> Some text
        | _ -> None

    /// JavaScript truthiness.
    let truthy (value: Json option) =
        match value with
        | None
        | Some JNull
        | Some(JBool false)
        | Some(JString "") -> false
        | Some(JNumber number) -> number <> "0" && number <> "-0" && number <> "NaN"
        | Some _ -> true

    /// `a ?? b`
    let orElse (fallback: Json) (value: Json option) =
        match value with
        | None
        | Some JNull -> fallback
        | Some present -> present

    /// Builds an object, omitting properties that are `undefined`.
    let ofFields (fields: (string * Json option) list) =
        fields |> List.choose (fun (key, value) -> value |> Option.map (fun v -> key, v)) |> JObject

    /// Replaces (or appends) one property.
    let set (name: string) (value: Json) (target: Json) =
        match target with
        | JObject fields when fields |> List.exists (fun (key, _) -> key = name) ->
            fields |> List.map (fun (key, old) -> if key = name then key, value else key, old) |> JObject
        | JObject fields -> JObject(fields @ [ name, value ])
        | other -> other

    /// Property access the way `node[part]` works in JavaScript, for the paths
    /// `data-evidence` keys use: object fields, array indexes and `.length`.
    let step (part: string) (value: Json option) =
        match value with
        | Some(JObject _) -> prop part value
        | Some(JArray values) when part = "length" -> Some(JNumber(string values.Length))
        | Some(JArray values) ->
            match Int32.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, index when index < values.Length && string index = part -> Some values[index]
            | _ -> None
        | Some(JString text) when part = "length" -> Some(JNumber(string text.Length))
        | _ -> None

    let private escapeInto (builder: StringBuilder) (text: string) =
        builder.Append('"') |> ignore

        for index in 0 .. text.Length - 1 do
            let c = text[index]

            match c with
            | '"' -> builder.Append("\\\"") |> ignore
            | '\\' -> builder.Append("\\\\") |> ignore
            | '\b' -> builder.Append("\\b") |> ignore
            | '\f' -> builder.Append("\\f") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | c when c < ' ' -> builder.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c when Char.IsHighSurrogate c && not (index + 1 < text.Length && Char.IsLowSurrogate text[index + 1]) ->
                builder.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c when Char.IsLowSurrogate c && not (index > 0 && Char.IsHighSurrogate text[index - 1]) ->
                builder.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> builder.Append(c) |> ignore

        builder.Append('"') |> ignore

    let rec private write (builder: StringBuilder) (indent: string option) (depth: int) (value: Json) =
        let newline (level: int) =
            match indent with
            | Some unit ->
                builder.Append('\n') |> ignore

                for _ in 1..level do
                    builder.Append(unit) |> ignore
            | None -> ()

        let separator = if indent.IsSome then ": " else ":"

        match value with
        | JNull -> builder.Append("null") |> ignore
        | JBool true -> builder.Append("true") |> ignore
        | JBool false -> builder.Append("false") |> ignore
        | JNumber number -> builder.Append(number) |> ignore
        | JString text -> escapeInto builder text
        | JArray [] -> builder.Append("[]") |> ignore
        | JObject [] -> builder.Append("{}") |> ignore
        | JArray values ->
            builder.Append('[') |> ignore

            values
            |> List.iteri (fun index item ->
                if index > 0 then builder.Append(',') |> ignore
                newline (depth + 1)
                write builder indent (depth + 1) item)

            newline depth
            builder.Append(']') |> ignore
        | JObject fields ->
            builder.Append('{') |> ignore

            fields
            |> List.iteri (fun index (key, item) ->
                if index > 0 then builder.Append(',') |> ignore
                newline (depth + 1)
                escapeInto builder key
                builder.Append(separator) |> ignore
                write builder indent (depth + 1) item)

            newline depth
            builder.Append('}') |> ignore

    /// `JSON.stringify(value, null, 2)`
    let pretty (value: Json) =
        let builder = StringBuilder()
        write builder (Some "  ") 0 value
        builder.ToString()

    /// `JSON.stringify(value)`; `undefined` stays distinct from every value.
    let compact (value: Json option) =
        match value with
        | None -> "undefined"
        | Some present ->
            let builder = StringBuilder()
            write builder None 0 present
            builder.ToString()

    /// `String(value)`
    let rec toText (value: Json option) =
        match value with
        | None -> "undefined"
        | Some JNull -> "null"
        | Some(JBool flag) -> if flag then "true" else "false"
        | Some(JNumber number) -> number
        | Some(JString text) -> text
        | Some(JArray values) ->
            values
            |> List.map (fun item ->
                match item with
                | JNull -> ""
                | other -> toText (Some other))
            |> String.concat ","
        | Some(JObject _) -> "[object Object]"
