namespace Praxis.Infrastructure.Pacing

open System
open System.Globalization
open System.Text.Json

[<RequireQualifiedAccess>]
module PacingJson =
    let tryProperty (name: string) (element: JsonElement) : JsonElement option =
        if element.ValueKind <> JsonValueKind.Object then
            None
        else
            let mutable value = Unchecked.defaultof<JsonElement>
            if element.TryGetProperty(name, &value) then Some value else None

    let tryString (element: JsonElement) : string option =
        if element.ValueKind = JsonValueKind.String then
            element.GetString() |> Option.ofObj
        else
            None

    let tryDecimal (element: JsonElement) : decimal option =
        if element.ValueKind <> JsonValueKind.Number then
            None
        else
            let mutable value = 0m

            if element.TryGetDecimal(&value) then
                Some value
            else
                let mutable number = 0.0
                if element.TryGetDouble(&number) && Double.IsFinite number then Some(decimal number) else None

    let tryInt (element: JsonElement) : int option =
        if element.ValueKind <> JsonValueKind.Number then
            None
        else
            let mutable value = 0
            if element.TryGetInt32(&value) then Some value else None

    let tryEpoch (element: JsonElement) : DateTimeOffset option =
        match tryDecimal element with
        | Some value when value > 0m ->
            try
                Some(DateTimeOffset.FromUnixTimeSeconds(int64 value))
            with _ ->
                None
        | _ -> None

    let tryTimestamp (element: JsonElement) : DateTimeOffset option =
        match tryString element with
        | Some text ->
            let mutable value = DateTimeOffset.MinValue

            if DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, &value) then
                Some value
            else
                None
        | None -> tryEpoch element

    let readNumber (name: string) (element: JsonElement) : decimal option =
        tryProperty name element |> Option.bind tryDecimal

    let readReset (name: string) (element: JsonElement) : DateTimeOffset option =
        tryProperty name element |> Option.bind tryTimestamp
