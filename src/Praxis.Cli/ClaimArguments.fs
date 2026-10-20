namespace Praxis.Cli

open System
open Praxis.Domain.Work
open Praxis.Infrastructure.Work

/// Shared flag parsing for the claim, completion and verifier commands.
[<RequireQualifiedAccess>]
module ClaimArguments =
    let optionValue (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.tryPick (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let required name arguments =
        optionValue name arguments |> Option.map Ok |> Option.defaultValue (Error $"{name} is required")

    let number name (parse: string -> bool * 'value) arguments =
        match optionValue name arguments with
        | None -> Ok None
        | Some text ->
            match parse text with
            | true, value -> Ok(Some value)
            | _ -> Error $"{name} must be a number"

    let seconds name (fallback: float) arguments =
        number name Double.TryParse arguments
        |> Result.bind (fun value ->
            match value with
            | Some value when value <= 0.0 -> Error $"{name} must be positive"
            | value -> Ok(TimeSpan.FromSeconds(value |> Option.defaultValue fallback)))

    let storeChoice arguments =
        match optionValue "--store" arguments, optionValue "--remote" arguments with
        | (None | Some "local"), None -> Ok ClaimStoreChoice.Local
        | Some "git", remote -> Ok(ClaimStoreChoice.GitRef(remote |> Option.defaultValue "origin"))
        | (None | Some "local"), Some _ -> Error "--remote requires --store git"
        | Some other, _ -> Error $"unknown --store '{other}' (expected local or git)"

    let validation (text: string) =
        let nameAndRest = text.Split('=', 2)

        if nameAndRest.Length <> 2 then
            Error $"--validation '{text}' must be NAME=STATUS[:EVIDENCE-ID,...]"
        else
            let statusAndEvidence = nameAndRest[1].Split(':', 2)

            match CheckStatus.tryParse statusAndEvidence[0] with
            | None -> Error $"--validation '{text}' has an unknown status (passed, failed or unavailable)"
            | Some status ->
                let evidence =
                    if statusAndEvidence.Length = 2 then
                        statusAndEvidence[1].Split(',', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
                    else
                        []

                Ok { Name = nameAndRest[0]; Status = status; EvidenceIds = evidence }

    let evidence (text: string) =
        match text.Split('=', 2) with
        | [| id; rest |] when id <> "" && rest <> "" ->
            match rest.LastIndexOf "@sha256:" with
            | -1 -> Ok { Id = id; Path = rest; ClaimedDigest = None }
            | at -> Ok { Id = id; Path = rest.Substring(0, at); ClaimedDigest = Some(rest.Substring(at + 1)) }
        | _ -> Error $"--evidence '{text}' must be ID=PATH[@sha256:HEX]"

    let collect parse values =
        values
        |> List.fold (fun state value -> state |> Result.bind (fun items -> parse value |> Result.map (fun item -> items @ [ item ]))) (Ok [])

    let print (text: string, code: int) =
        printfn "%s" text
        code

    let invalid (usage: string) (message: string) =
        eprintfn "ERROR %s" message
        eprintfn "usage: %s" usage
        2
