namespace Praxis.Cli

open System
open System.Text.Json
open Praxis.Infrastructure.Construction

/// `praxis construct ...`: host-controlled application of an Ordo
/// construction plan (Praxis #242). The plan is the only agent input; the
/// host policy, signed approval, committed sources and pinned engine decide
/// everything else. Exit codes: 0 success, 2 invalid arguments, 3 refused.
[<RequireQualifiedAccess>]
module ConstructionCommands =
    let usage =
        " | construct preview --host-config FILE --group ID --plan FILE --target REF"
        + " | construct apply --host-config FILE --group ID --plan FILE --target REF"

    let private value (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name then Some value else None)

    let private options = JsonSerializerOptions(WriteIndented = true)

    let run (root: string) (arguments: string list) =
        let operation, rest =
            match arguments with
            | "preview" :: rest -> Some ConstructionService.preview, rest
            | "apply" :: rest -> Some ConstructionService.apply, rest
            | _ -> None, arguments
        match operation, value "--host-config" rest, value "--group" rest, value "--plan" rest, value "--target" rest with
        | Some operation, Some config, Some group, Some plan, Some target ->
            match operation root config group plan target with
            | Ok node ->
                Console.Out.WriteLine(node.ToJsonString options)
                0
            | Error reason ->
                Console.Error.WriteLine("REFUSED " + reason)
                3
        | _ ->
            Console.Error.WriteLine("usage: praxis" + usage.Substring 2)
            2
