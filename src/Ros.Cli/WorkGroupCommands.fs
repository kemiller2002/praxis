namespace Ros.Cli

open System
open System.Globalization
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `praxis work group create`: records a human-declared execution group in
/// Praxis state (`.ros/work/groups.json`; PRX-GRP-073 phase two). It writes
/// the group store only; members keep their own lifecycle state.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group create --id GROUP-ID --member ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT]* [--architecture-note TEXT]* [--dry-run] [--json]"

    let private valued =
        [ "--id"; "--member"; "--occurred-at"; "--kind"; "--execution-repository"; "--shared-context"; "--architecture-note" ]

    let private switches = [ "--cross-repository"; "--dry-run"; "--json" ]

    /// Tokens the command does not understand: identity flags (and their
    /// values) are accepted because the actor is resolved from them.
    let rec private unexpected (arguments: string list) =
        match arguments with
        | [] -> []
        | flag :: _ :: rest when List.contains flag valued || List.contains flag ProvenanceCommands.identityFlags -> unexpected rest
        | flag :: rest when List.contains flag switches -> unexpected rest
        | token :: rest -> token :: unexpected rest

    let private values (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

    let private value name arguments = values name arguments |> List.tryLast

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private request (actor: Actor) (arguments: string list) : Result<GroupCreateRequest, string> =
        match unexpected arguments, value "--id" arguments, value "--occurred-at" arguments with
        | (_ :: _) as stray, _, _ -> Error("unexpected argument(s): " + String.concat " " stray)
        | _, None, _ -> Error "work group create requires --id GROUP-ID"
        | _, _, None -> Error "work group create requires --occurred-at TIMESTAMP"
        | _, _, Some timestamp when not (isTimestamp timestamp) -> Error $"--occurred-at '{timestamp}' is not a timestamp"
        | _, Some id, Some timestamp ->
            Ok
                { Id = id
                  Members = values "--member" arguments
                  Kind = value "--kind" arguments
                  ExecutionRepository = value "--execution-repository" arguments
                  CrossRepository = List.contains "--cross-repository" arguments
                  SharedContext = values "--shared-context" arguments
                  ArchitectureNotes = values "--architecture-note" arguments
                  DeclaredAt = timestamp
                  DeclaredBy = actor.Id }

    let private document (dryRun: bool) (fields: (string * JsonNode) list) : string =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create "praxis.work-group/1.0.0"
        root["kind"] <- JsonValue.Create "work-group-create"
        root["dryRun"] <- JsonValue.Create dryRun
        fields |> List.iter (fun (name, node) -> root[name] <- node)
        PlanningJson.render root

    let private rejectionsNode (rejections: GroupRejection list) : JsonNode =
        let array = JsonArray()

        rejections
        |> List.iter (fun rejection ->
            let entry = JsonObject()
            entry["code"] <- JsonValue.Create(GroupRejection.code rejection)
            entry["message"] <- JsonValue.Create(GroupRejection.message rejection)
            array.Add entry)

        array

    let private describe (dryRun: bool) (stored: StoredGroup) =
        let group = stored.Group
        let verb = if dryRun then "would declare" else "declared"
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"
        let repository = group.ExecutionRepository |> Option.defaultValue "unspecified"

        [ $"{verb} {group.Id} ({GroupOrigin.code group.Origin}, kind {kind})"
          "  members: " + String.concat ", " group.Members
          $"  execution repository: {repository}; cross-repository: {group.CrossRepository}"
          yield! group.SharedContext |> List.map (sprintf "  shared context: %s")
          yield! group.ArchitectureNotes |> List.map (sprintf "  architecture note: %s")
          "  member lifecycle states are unchanged" ]
        |> String.concat "\n"

    let private emit (json: bool) (dryRun: bool) (outcome: Result<StoredGroup, GroupCreateFailure>) =
        match outcome, json with
        | Ok stored, true ->
            printf "%s" (document dryRun [ "ok", JsonValue.Create true; "group", PlanningJson.storedGroup stored ])
            0
        | Ok stored, false ->
            printfn "%s" (describe dryRun stored)
            0
        | Error(GroupCreateFailure.Rejected rejections), true ->
            printf "%s" (document dryRun [ "ok", JsonValue.Create false; "rejections", rejectionsNode rejections ])
            1
        | Error(GroupCreateFailure.Rejected rejections), false ->
            rejections |> List.iter (GroupRejection.message >> eprintfn "ERROR %s")
            1
        | Error(GroupCreateFailure.Failed message), _ ->
            eprintfn "ERROR %s" message
            1

    let create (root: string) (actor: Actor) (arguments: string list) : int =
        let json = List.contains "--json" arguments
        let dryRun = List.contains "--dry-run" arguments

        match request actor arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            eprintfn "Usage: %s" usage
            2
        | Ok parsed -> FileWorkGroupRepository.create root dryRun parsed |> emit json dryRun
