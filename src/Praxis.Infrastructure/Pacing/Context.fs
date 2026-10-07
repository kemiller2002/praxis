namespace Praxis.Infrastructure.Pacing

open System
open System.IO
open System.Text.Json
open Praxis.Application.Pacing
open Praxis.Domain.Pacing

[<RequireQualifiedAccess>]
module PacingContext =
    let private resolveModel
        (provider: ProviderId)
        (explicitModel: string option)
        (payload: string)
        : string option =
        match explicitModel with
        | Some _ -> explicitModel
        | None ->
            try
                use document = JsonDocument.Parse payload
                let root = document.RootElement

                match PacingJson.tryProperty "model" root |> Option.bind PacingJson.tryString with
                | Some model -> Some model
                | None when provider = ProviderId.Claude ->
                    let transcript =
                        PacingJson.tryProperty "transcript_path" root
                        |> Option.bind PacingJson.tryString

                    let agent =
                        PacingJson.tryProperty "agent_id" root
                        |> Option.bind PacingJson.tryString

                    let path =
                        match transcript, agent with
                        | Some file, Some id when file.EndsWith(".jsonl", StringComparison.Ordinal) ->
                            Path.Combine(
                                Path.GetDirectoryName file,
                                Path.GetFileNameWithoutExtension file,
                                "subagents",
                                $"agent-{id}.jsonl"
                            )
                        | Some file, _ -> file
                        | _ -> ""

                    if String.IsNullOrWhiteSpace path || not (File.Exists path) then
                        None
                    else
                        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        let length = min stream.Length (1L <<< 20)
                        stream.Seek(-length, SeekOrigin.End) |> ignore
                        use reader = new StreamReader(stream)
                        let lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)

                        lines
                        |> Array.rev
                        |> Array.tryPick (fun line ->
                            try
                                use row = JsonDocument.Parse line
                                let entry = row.RootElement

                                let kind =
                                    PacingJson.tryProperty "type" entry
                                    |> Option.bind PacingJson.tryString

                                if kind = Some "assistant" then
                                    PacingJson.tryProperty "message" entry
                                    |> Option.bind (PacingJson.tryProperty "model")
                                    |> Option.bind PacingJson.tryString
                                    |> Option.filter (fun value ->
                                        not (value.StartsWith("<", StringComparison.Ordinal)))
                                else
                                    None
                            with :? JsonException ->
                                None)
                | None -> None
            with
            // An unreadable payload or transcript leaves the model unknown,
            // which pacing evaluates conservatively.
            | :? JsonException -> None
            | :? IOException -> None
            | :? UnauthorizedAccessException -> None
            | :? ArgumentException -> None

    let resolver : PacingContextResolver =
        { ResolveModel = resolveModel }
