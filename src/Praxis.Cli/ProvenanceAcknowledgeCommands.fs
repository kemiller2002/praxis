namespace Praxis.Cli

open Praxis.Domain.Provenance

/// `provenance acknowledge-unrecorded` (DF-ROS-2026-A055): records, under the
/// acknowledging actor's own identity and execution, that no Praxis execution
/// recorded an artifact's creation. It names and infers no creator, requires a
/// reason, and is refused when the artifact already records 'created' (the
/// rules live in `ArtifactProvenance`; this adapter only parses and delegates
/// to `provenance record`).
[<RequireQualifiedAccess>]
module ProvenanceAcknowledgeCommands =
    let usage =
        "provenance acknowledge-unrecorded --path PATH --reason TEXT [--execution EXE-ID] [--occurred-at TIMESTAMP] [--json] [IDENTITY]"

    let private optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private nonBlank (value: string) = value.Trim().Length > 0

    let run root (arguments: string list) =
        match optionValue "--path" arguments, optionValue "--reason" arguments |> Option.filter nonBlank with
        | None, _ ->
            eprintfn "ERROR provenance acknowledge-unrecorded requires --path PATH"
            2
        | _, None ->
            eprintfn "ERROR provenance acknowledge-unrecorded requires --reason TEXT: why the artifact's creation was never recorded"
            2
        | Some _, Some _ when arguments |> List.exists (fun argument -> argument = "--operation" || argument = "--id") ->
            eprintfn "ERROR provenance acknowledge-unrecorded takes --path only and records the origin-unrecorded operation itself"
            2
        | Some _, Some _ -> ProvenanceCommands.run root ("record" :: "--operation" :: "origin-unrecorded" :: arguments)

    /// What `validate` reports for each acknowledged origin, distinctly from
    /// a recorded creation: `PATH: message`.
    let notes root : string list =
        match ProvenanceCommands.findingsOf FindingSeverity.Info root with
        | Ok findings ->
            findings
            |> List.filter (fun finding -> finding.Field = "provenance.origin")
            |> List.map (fun finding -> $"{finding.Path}: {finding.Message}")
        | Error _ -> []
