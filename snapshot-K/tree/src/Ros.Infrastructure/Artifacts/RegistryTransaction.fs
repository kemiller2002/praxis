namespace Ros.Infrastructure.Artifacts

open System
open System.IO
open System.Text
open System.Text.Json
open Ros.Application.Artifacts
open Ros.Domain.Artifacts

[<RequireQualifiedAccess>]
module RegistryTransaction =
    let private resource = "artifact-registries"
    let private schemaVersion = "1.0.0"

    let private failure operation outcome message =
        { Operation = operation
          Path = None
          Message = message
          Outcome = outcome }

    let private expectedPaths =
        ArtifactKinds.configurations
        |> List.map _.RegistryPath
        |> Set.ofList

    let transactionPath (root: string) =
        Path.Combine(root, ".ros", "transactions", $"{resource}.json")

    let writeAtomic (file: string) (content: string): Result<unit, DependencyFailure> =
        let directory = Path.GetDirectoryName file
        let temporary = Path.Combine(directory, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(temporary, content, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            try
                if File.Exists temporary then File.Delete temporary
            with _ ->
                ()

            Error(failure "write registry" DependencyOutcome.Indeterminate error.Message)

    let private parse (file: string): Result<RegistryChange list, DependencyFailure> =
        try
            use document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8))
            let root = document.RootElement

            let property (name: string): JsonElement option =
                match root.TryGetProperty name with
                | true, value -> Some value
                | false, _ -> None

            let readString (name: string): string option =
                property name
                |> Option.bind (fun value ->
                    if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)

            let writes: RegistryChange list =
                match property "writes" with
                | Some values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value ->
                        let read (name: string): string option =
                            match value.TryGetProperty name with
                            | true, item when item.ValueKind = JsonValueKind.String -> Some(item.GetString())
                            | _ -> None

                        match read "path", read "content" with
                        | Some path, Some content ->
                            Some({ Path = path; Content = content }: RegistryChange)
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            if readString "schemaVersion" <> Some schemaVersion || readString "resource" <> Some resource then
                Error(failure "recover artifact registry transaction" DependencyOutcome.Indeterminate "unsupported transaction record")
            elif List.isEmpty writes
                 || writes.Length <> (writes |> List.map (fun (change: RegistryChange) -> change.Path) |> Set.ofList |> Set.count)
                 || writes |> List.exists (fun (change: RegistryChange) -> not (expectedPaths.Contains change.Path)) then
                Error(failure "recover artifact registry transaction" DependencyOutcome.Indeterminate "transaction write set is invalid")
            else
                Ok writes
        with error ->
            Error(failure "recover artifact registry transaction" DependencyOutcome.Indeterminate error.Message)

    let recover (root: string): Result<unit, DependencyFailure> =
        let file = transactionPath root

        if not (File.Exists file) then
            Ok()
        else
            match parse file with
            | Error error -> Error error
            | Ok writes ->
                let rec replay remaining =
                    match remaining with
                    | [] ->
                        try
                            File.Delete file
                            Ok()
                        with error ->
                            Error(failure "complete artifact registry transaction" DependencyOutcome.Indeterminate error.Message)
                    | (change: RegistryChange) :: rest ->
                        match writeAtomic (Path.Combine(root, change.Path)) change.Content with
                        | Ok() -> replay rest
                        | Error error -> Error error

                replay writes

    let prepare (root: string) (changes: RegistryChange list): Result<unit, DependencyFailure> =
        let file = transactionPath root

        if List.isEmpty changes || changes |> List.exists (fun (change: RegistryChange) -> not (expectedPaths.Contains change.Path)) then
            Error(failure "prepare artifact registry transaction" DependencyOutcome.Failed "transaction write set is invalid")
        else
            let content =
                JsonSerializer.Serialize
                    {| schemaVersion = schemaVersion
                       resource = resource
                       writes = changes |> List.map (fun (change: RegistryChange) -> {| path = change.Path; content = change.Content |}) |}
                + "\n"

            writeAtomic file content

    let complete (root: string): Result<unit, DependencyFailure> =
        let file = transactionPath root

        try
            if File.Exists file then File.Delete file
            Ok()
        with error ->
            Error(failure "complete artifact registry transaction" DependencyOutcome.Indeterminate error.Message)
