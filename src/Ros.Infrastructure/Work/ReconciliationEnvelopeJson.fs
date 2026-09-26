namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Text.Json
open Ros.Domain.Work

[<RequireQualifiedAccess>]
module ReconciliationEnvelopeJson =
    let private options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    let read path : Result<ReconciliationEnvelope, string list> =
        try
            if not (File.Exists path) then Error [ "envelope-not-found" ]
            else
                let value = JsonSerializer.Deserialize<ReconciliationEnvelope>(File.ReadAllText path, options)
                if isNull (box value) then Error [ "invalid-envelope-json" ] else Ok value
        with :? JsonException ->
            Error [ "invalid-envelope-json" ]
        with _ ->
            Error [ "envelope-read-failed" ]

[<RequireQualifiedAccess>]
module InputDocuments =
    let canonicalRelative = Path.Combine(".praxis", "inbox", "documents")
    let legacyRelative = "input-documents"

    let inventory root =
        [ canonicalRelative; legacyRelative ]
        |> List.collect (fun relative ->
            let path = Path.Combine(Path.GetFullPath root, relative)
            if Directory.Exists path then
                Directory.EnumerateFiles(path)
                |> Seq.filter (fun file -> not (String.Equals(Path.GetFileName file, "README.md", StringComparison.OrdinalIgnoreCase)))
                |> Seq.map (fun file -> relative, file)
                |> Seq.toList
            else [])
        |> List.sortBy snd
