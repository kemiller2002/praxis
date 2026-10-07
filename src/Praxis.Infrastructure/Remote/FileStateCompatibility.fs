namespace Praxis.Infrastructure.Remote

open System
open System.IO
open System.Text.Json
open Praxis.Domain.Remote

/// Reads the schema version each gating state document declares
/// (PRX-QUAL-010). An unreadable document is an error: an executor that
/// cannot tell which version it would mutate must not mutate it.
[<RequireQualifiedAccess>]
module FileStateCompatibility =
    let private versionOf (element: JsonElement) =
        match element.TryGetProperty "schemaVersion" with
        | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetRawText())
        | _ -> None

    let private observeDocument (root: string) (document: string, relative: string) : Result<(string * string) list, string> =
        let path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

        try
            if not (File.Exists path) then
                Ok []
            elif relative.EndsWith(".jsonl", StringComparison.Ordinal) then
                File.ReadLines path
                |> Seq.filter (fun line -> not (String.IsNullOrWhiteSpace line))
                |> Seq.map (fun line ->
                    use parsed = JsonDocument.Parse line
                    versionOf parsed.RootElement)
                |> Seq.choose id
                |> Seq.distinct
                |> Seq.map (fun version -> document, version)
                |> Seq.toList
                |> Ok
            else
                use parsed = JsonDocument.Parse(File.ReadAllText path)

                match versionOf parsed.RootElement with
                | Some version -> Ok [ document, version ]
                | None -> Error $"{relative} declares no schemaVersion"
        with
        | :? JsonException as error -> Error $"{relative} is not valid JSON: {error.Message}"
        | :? IOException as error -> Error $"{relative} could not be read: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"{relative} could not be read: {error.Message}"

    /// Every `(document, version)` the repository's committed state declares.
    let observe (root: string) : Result<(string * string) list, string> =
        StateCompatibility.documents
        |> List.fold
            (fun acc entry -> acc |> Result.bind (fun observed -> observeDocument root entry |> Result.map (fun found -> observed @ found)))
            (Ok [])

    /// The incompatibilities between this executor and the repository state.
    let assess (root: string) : Result<StateIncompatibility list, string> =
        observe root |> Result.map (StateCompatibility.check StateCompatibility.current)
