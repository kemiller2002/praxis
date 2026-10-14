namespace Praxis.UiConformance

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Validates a JSON document against the subset of JSON Schema 2020-12 that
/// visual-engineering's ui-conformance.schema.json uses. A keyword outside the
/// subset is itself an error, so a future schema change cannot pass silently.
[<RequireQualifiedAccess>]
module SchemaCheck =
    let private annotations = set [ "$schema"; "$id"; "title"; "description"; "default"; "$defs" ]

    let private supported =
        set [ "type"; "required"; "properties"; "additionalProperties"; "enum"; "const"; "pattern"; "minLength"; "minItems"; "uniqueItems"; "minimum"; "items"; "$ref"; "allOf"; "if"; "then" ]

    let private kind (node: JsonNode) =
        match node with
        | null -> "null"
        | :? JsonObject -> "object"
        | :? JsonArray -> "array"
        | value ->
            match value.GetValueKind() with
            | JsonValueKind.String -> "string"
            | JsonValueKind.Number -> "number"
            | JsonValueKind.True
            | JsonValueKind.False -> "boolean"
            | _ -> "null"

    let private isInteger (node: JsonNode) =
        kind node = "number" && (let n = node.GetValue<double>() in Math.Floor n = n)

    let private equal (a: JsonNode) (b: JsonNode) = JsonNode.DeepEquals(a, b)

    let rec private check (root: JsonObject) (schema: JsonObject) (path: string) (value: JsonNode) : string list =
        let unknown =
            schema
            |> Seq.map (fun (pair: Collections.Generic.KeyValuePair<string, JsonNode>) -> pair.Key)
            |> Seq.filter (fun key -> not (annotations.Contains key || supported.Contains key))
            |> Seq.map (fun key -> $"{path}: unsupported schema keyword '{key}'")
            |> List.ofSeq

        let keyword (name: string) : JsonNode option = if schema.ContainsKey name then Some schema[name] else None
        let error message = [ $"{path}: {message}" ]

        let typeErrors =
            match keyword "type" with
            | Some expected ->
                let wanted = expected.GetValue<string>()
                let actual = kind value
                let ok = actual = wanted || (wanted = "integer" && isInteger value) || (wanted = "number" && actual = "number")
                if ok then [] else error $"expected {wanted}, found {actual}"
            | None -> []

        let refErrors =
            match keyword "$ref" with
            | Some reference ->
                let name = reference.GetValue<string>().Replace("#/$defs/", "")
                let definitions = root["$defs"].AsObject()
                let definition = definitions[name].AsObject()
                check root definition path value
            | None -> []

        let enumErrors =
            match keyword "enum" with
            | Some options when not (options.AsArray() |> Seq.exists (fun (option: JsonNode) -> equal option value)) -> error $"value {value} is not one of {options.ToJsonString()}"
            | _ -> []

        let constErrors =
            match keyword "const" with
            | Some expected when not (equal expected value) -> error $"value must be {expected.ToJsonString()}"
            | _ -> []

        let stringErrors =
            if kind value <> "string" then []
            else
                let text = value.GetValue<string>()

                (match keyword "pattern" with
                 | Some pattern when not (Regex.IsMatch(text, pattern.GetValue<string>())) -> error $"'{text}' does not match {pattern}"
                 | _ -> [])
                @ (match keyword "minLength" with
                   | Some minimum when text.Length < minimum.GetValue<int>() -> error $"shorter than {minimum}"
                   | _ -> [])

        let numberErrors =
            match keyword "minimum", kind value with
            | Some minimum, "number" when value.GetValue<double>() < minimum.GetValue<double>() -> error $"below minimum {minimum}"
            | _ -> []

        let arrayErrors =
            match value with
            | :? JsonArray as items ->
                let elements = items |> List.ofSeq

                (match keyword "minItems" with
                 | Some minimum when elements.Length < minimum.GetValue<int>() -> error $"fewer than {minimum} items"
                 | _ -> [])
                @ (match keyword "uniqueItems" with
                   | Some unique when unique.GetValue<bool>() && (elements |> List.map (fun (item: JsonNode) -> item.ToJsonString()) |> List.distinct |> List.length) <> elements.Length -> error "items are not unique"
                   | _ -> [])
                @ (match keyword "items" with
                   | Some itemSchema -> elements |> List.mapi (fun index (item: JsonNode) -> check root (itemSchema.AsObject()) $"{path}[{index}]" item) |> List.concat
                   | None -> [])
            | _ -> []

        let objectErrors =
            match value with
            | :? JsonObject as record ->
                let properties = keyword "properties" |> Option.map (fun (node: JsonNode) -> node.AsObject())

                let required =
                    match keyword "required" with
                    | Some names -> names.AsArray() |> Seq.map (fun (name: JsonNode) -> name.GetValue<string>()) |> Seq.filter (record.ContainsKey >> not) |> Seq.map (fun name -> $"{path}: missing required '{name}'") |> List.ofSeq
                    | None -> []

                let additional =
                    match keyword "additionalProperties", properties with
                    | Some allowed, Some declared when kind allowed = "boolean" && not (allowed.GetValue<bool>()) ->
                        record |> Seq.filter (fun (pair: Collections.Generic.KeyValuePair<string, JsonNode>) -> not (declared.ContainsKey pair.Key)) |> Seq.map (fun pair -> $"{path}: unexpected property '{pair.Key}'") |> List.ofSeq
                    | _ -> []

                let nested =
                    match properties with
                    | Some declared ->
                        record
                        |> Seq.choose (fun (pair: Collections.Generic.KeyValuePair<string, JsonNode>) -> if declared.ContainsKey pair.Key then Some(check root (declared[pair.Key].AsObject()) $"{path}.{pair.Key}" pair.Value) else None)
                        |> List.concat
                    | None -> []

                required @ additional @ nested
            | _ -> []

        let allOfErrors =
            match keyword "allOf" with
            | Some branches -> branches.AsArray() |> Seq.map (fun (branch: JsonNode) -> check root (branch.AsObject()) path value) |> List.concat
            | None -> []

        let conditionalErrors =
            match keyword "if", keyword "then" with
            | Some condition, Some consequence when (check root (condition.AsObject()) path value).IsEmpty -> check root (consequence.AsObject()) path value
            | _ -> []

        unknown @ typeErrors @ refErrors @ enumErrors @ constErrors @ stringErrors @ numberErrors @ arrayErrors @ objectErrors @ allOfErrors @ conditionalErrors

    /// Every violation of `schema` by `document`; empty when it validates.
    /// The document is checked as serialized, so in-memory .NET values are
    /// read back as the JSON a consumer would parse.
    let validate (schema: JsonObject) (document: JsonNode) =
        check schema schema "$" (JsonNode.Parse(document.ToJsonString()))
