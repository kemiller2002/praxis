namespace Praxis.Infrastructure.Foundations

open System.IO
open System.Text.Json
open Praxis.Domain.Foundations

/// Reads the Limen installation manifest a repository commits.
[<RequireQualifiedAccess>]
module LimenManifestReader =
    let RelativePath = ".echelon/limen.json"

    let private stringField (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
        | _ -> None

    /// Pure: the manifest's pin fields, or None when the text is not a JSON object.
    let parse (text: string) : LimenManifest option =
        try
            use document = JsonDocument.Parse text

            if document.RootElement.ValueKind = JsonValueKind.Object then
                Some
                    { Package = stringField "package" document.RootElement
                      InstalledVersion = stringField "installedVersion" document.RootElement }
            else
                None
        with :? JsonException ->
            None

    /// The manifest under `root`, or None when it is absent or unreadable.
    let tryRead (root: string) : LimenManifest option =
        let path = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar))

        if File.Exists path then parse (File.ReadAllText path) else None
