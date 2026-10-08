namespace Praxis.Infrastructure.Foundations

open System
open System.IO
open System.Text.Json
open Praxis.Domain.Foundations

/// Reads the `echelon.routes/v1` route inventory a repository commits
/// (SAF-URL-8; Limen LCP-107/108 define the document).
[<RequireQualifiedAccess>]
module RouteInventoryReader =
    let DefaultRelativePath = ".echelon/routes.json"

    let private property (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value -> Some value
            | _ -> None
        | _ -> None

    let private text name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.bind (fun value -> value.GetString() |> Option.ofObj)

    let private textOrEmpty name element = text name element |> Option.defaultValue ""

    let private flag name element =
        property name element |> Option.exists (fun value -> value.ValueKind = JsonValueKind.True)

    let private items name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
        |> Option.map (fun value -> value.EnumerateArray() |> List.ofSeq)
        |> Option.defaultValue []

    /// A value's canonical text: a string as is, a number or boolean as
    /// written, a set as its members joined by ','; null is no value.
    let rec private canonical (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> value.GetString() |> Option.ofObj
        | JsonValueKind.Number -> Some(value.GetRawText())
        | JsonValueKind.True -> Some "true"
        | JsonValueKind.False -> Some "false"
        | JsonValueKind.Array -> Some(String.Join(",", value.EnumerateArray() |> Seq.choose canonical))
        | _ -> None

    let private parameter element : RouteParameter =
        { Name = textOrEmpty "name" element
          Location = textOrEmpty "in" element
          Type = textOrEmpty "type" element
          Required = flag "required" element
          Default = property "default" element |> Option.bind canonical
          Values = items "values" element |> List.choose canonical }

    let private route element : RouteEntry =
        { Name = textOrEmpty "name" element
          Pattern = textOrEmpty "pattern" element
          Parameters = items "params" element |> List.map parameter }

    let private legacy element : LegacyRoute =
        { Pattern = textOrEmpty "pattern" element
          To = textOrEmpty "to" element
          Parameters = items "params" element |> List.map parameter }

    /// Pure: the inventory, or None when the text is not a JSON object.
    let parse (json: string) : RouteInventory option =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                None
            else
                Some
                    { Schema = text "schema" root
                      Mode = text "mode" root
                      Home = text "home" root
                      SignIn = text "signIn" root
                      NotFound = text "notFound" root
                      Routes = items "routes" root |> List.map route
                      Legacy = items "legacy" root |> List.map legacy }
        with :? JsonException ->
            None

    /// Whether the inventory file exists under `root`, and its parse.
    let tryRead (root: string) (relativePath: string) : bool * RouteInventory option =
        let path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))

        if File.Exists path then true, parse (File.ReadAllText path) else false, None
