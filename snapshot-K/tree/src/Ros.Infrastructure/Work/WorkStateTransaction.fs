namespace Ros.Infrastructure.Work

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Ros.Application.Work

[<RequireQualifiedAccess>]
module WorkStateTransaction =
    let private resource = "work-state"
    let private schemaVersion = "1.0.0"

    let private expectedPaths =
        [ ".ros/events/events.jsonl"; ".ros/context/current.json" ]

    let private failure operation path outcome message =
        { Operation = operation
          Path = path
          Message = message
          Outcome = outcome }

    let transactionPath (root: string) =
        Path.Combine(root, ".ros", "transactions", $"{resource}.json")

    let private hashText (content: string) =
        content
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private readHash file =
        if File.Exists file then File.ReadAllText(file, Encoding.UTF8) |> hashText |> Some else None

    let private writeAtomic (file: string) (content: string) =
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

            Error(failure "write work state" None WorkPersistenceOutcome.Indeterminate error.Message)

    type private PendingWrite =
        { Path: string
          BeforeSha256: string option
          AfterSha256: string
          Content: string }

    let private validPaths paths = paths = expectedPaths

    let private parse file =
        try
            use document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8))
            let root = document.RootElement

            let stringProperty (name: string) =
                match root.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None

            let writes =
                match root.TryGetProperty "writes" with
                | true, values when values.ValueKind = JsonValueKind.Array ->
                    values.EnumerateArray()
                    |> Seq.choose (fun value ->
                        let readString (name: string) =
                            match value.TryGetProperty name with
                            | true, item when item.ValueKind = JsonValueKind.String -> Some(item.GetString())
                            | _ -> None

                        let readNullableString (name: string) =
                            match value.TryGetProperty name with
                            | true, item when item.ValueKind = JsonValueKind.Null -> Some None
                            | true, item when item.ValueKind = JsonValueKind.String -> Some(Some(item.GetString()))
                            | _ -> None

                        match readString "path", readNullableString "beforeSha256", readString "afterSha256", readString "content" with
                        | Some path, Some before, Some after, Some content ->
                            Some
                                { Path = path
                                  BeforeSha256 = before
                                  AfterSha256 = after
                                  Content = content }
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            if stringProperty "schemaVersion" <> Some schemaVersion || stringProperty "resource" <> Some resource then
                Error(failure "recover work-state transaction" None WorkPersistenceOutcome.Indeterminate "unsupported transaction record")
            elif not (validPaths (writes |> List.map _.Path))
                 || writes |> List.exists (fun write -> hashText write.Content <> write.AfterSha256) then
                Error(failure "recover work-state transaction" None WorkPersistenceOutcome.Indeterminate "transaction write set is invalid")
            else
                Ok writes
        with error ->
            Error(failure "recover work-state transaction" None WorkPersistenceOutcome.Indeterminate error.Message)

    let private prepareUnsafe (root: string) (writes: WorkStateWrite list) =
        let file = transactionPath root

        if not (validPaths (writes |> List.map _.Path)) then
            Error(failure "prepare work-state transaction" None WorkPersistenceOutcome.Failed "transaction must contain event then context")
        elif File.Exists file then
            Error(failure "prepare work-state transaction" None WorkPersistenceOutcome.Failed "a pending work-state transaction already exists")
        else
            let pending =
                writes
                |> List.map (fun write ->
                    {| path = write.Path
                       beforeSha256 = readHash (Path.Combine(root, write.Path)) |> Option.toObj
                       afterSha256 = hashText write.Content
                       content = write.Content |})

            let content =
                JsonSerializer.Serialize
                    {| schemaVersion = schemaVersion
                       resource = resource
                       writes = pending |}
                + "\n"

            writeAtomic file content

    let prepare (root: string) (writes: WorkStateWrite list) =
        try
            prepareUnsafe root writes
        with error ->
            Error(failure "prepare work-state transaction" None WorkPersistenceOutcome.Failed error.Message)

    let private recoverUnsafe (root: string) =
        let file = transactionPath root

        if not (File.Exists file) then
            Ok()
        else
            match parse file with
            | Error error -> Error error
            | Ok writes ->
                let conflict =
                    writes
                    |> List.tryFind (fun write ->
                        let current = readHash (Path.Combine(root, write.Path))
                        current <> write.BeforeSha256 && current <> Some write.AfterSha256)

                match conflict with
                | Some write ->
                    Error(
                        failure
                            "recover work-state transaction"
                            (Some write.Path)
                            WorkPersistenceOutcome.Indeterminate
                            "target changed after transaction preparation"
                    )
                | None ->
                    let rec replay remaining =
                        match remaining with
                        | [] ->
                            try
                                File.Delete file
                                Ok()
                            with error ->
                                Error(failure "complete work-state transaction" None WorkPersistenceOutcome.Indeterminate error.Message)
                        | write :: rest ->
                            let target = Path.Combine(root, write.Path)

                            if readHash target = Some write.AfterSha256 then
                                replay rest
                            else
                                match writeAtomic target write.Content with
                                | Ok() -> replay rest
                                | Error error -> Error error

                    replay writes

    let recover (root: string) =
        try
            recoverUnsafe root
        with error ->
            Error(failure "recover work-state transaction" None WorkPersistenceOutcome.Indeterminate error.Message)
