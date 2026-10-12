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
        + " | construct route --host-config FILE --group ID --plan FILE"
        + " | construct extend --host-config FILE --group ID --plan FILE --target REF --files DIR [--role implementation]"

    let private value (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.tryPick (fun (flag, value) -> if flag = name then Some value else None)

    let private options = JsonSerializerOptions(WriteIndented = true)

    /// Flags that would ask the host to run a worker or command. They are
    /// refused, never ignored: autonomous custom-code execution stays
    /// disabled until host containment is qualified.
    let private executionFlags = set [ "--worker"; "--command"; "--run"; "--exec"; "--agent-command"; "--adapter" ]

    let private flags (arguments: string list) = arguments |> List.filter (fun argument -> argument.StartsWith "--")

    let private emit result =
        match result with
        | Ok(node: System.Text.Json.Nodes.JsonObject) ->
            Console.Out.WriteLine(node.ToJsonString options)
            0
        | Error(reason: string) ->
            Console.Error.WriteLine("REFUSED " + reason)
            3

    let private usageError () =
        Console.Error.WriteLine("usage: praxis" + usage.Substring 2)
        2

    let run (root: string) (arguments: string list) =
        match arguments with
        | ("route" | "extend") :: rest when flags rest |> List.exists executionFlags.Contains ->
            emit (Error ConstructionRouting.WorkerExecutionDisabled)
        | "route" :: rest ->
            match value "--host-config" rest, value "--group" rest, value "--plan" rest with
            | Some config, Some group, Some plan when flags rest |> List.forall (fun flag -> List.contains flag [ "--host-config"; "--group"; "--plan" ]) ->
                emit (ConstructionService.route root config group plan)
            | _ -> usageError ()
        | "extend" :: rest ->
            let allowed = [ "--host-config"; "--group"; "--plan"; "--target"; "--files"; "--role" ]
            match value "--host-config" rest, value "--group" rest, value "--plan" rest, value "--target" rest, value "--files" rest, value "--role" rest with
            | Some config, Some group, Some plan, Some target, Some files, role when flags rest |> List.forall (fun flag -> List.contains flag allowed) ->
                emit (ConstructionService.extend root config group plan target files (defaultArg role "implementation"))
            | _ -> usageError ()
        | _ ->
            let operation, rest =
                match arguments with
                | "preview" :: rest -> Some ConstructionService.preview, rest
                | "apply" :: rest -> Some ConstructionService.apply, rest
                | _ -> None, arguments
            match operation, value "--host-config" rest, value "--group" rest, value "--plan" rest, value "--target" rest with
            | Some operation, Some config, Some group, Some plan, Some target -> emit (operation root config group plan target)
            | _ -> usageError ()
