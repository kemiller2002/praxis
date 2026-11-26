namespace Praxis.Infrastructure.Foundations

open System.Text.Json

/// Pure readers over the `.echelon/foundations.json` declaration elements.
[<RequireQualifiedAccess>]
module FoundationsJson =
    let tryProperty (name: string) (element: JsonElement) =
        match element.ValueKind, element.TryGetProperty name with
        | JsonValueKind.Object, (true, value) -> Some value
        | _ -> None

    let stringProperty name element =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)

    let boolProperty defaultValue name element =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.True -> true
        | Some value when value.ValueKind = JsonValueKind.False -> false
        | _ -> defaultValue
