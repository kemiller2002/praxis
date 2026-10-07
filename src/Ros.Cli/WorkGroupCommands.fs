namespace Ros.Cli

open System
open System.Globalization
open System.Text.Json.Nodes
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `praxis work group create`: records a human-declared execution group in
/// Praxis state (`.ros/work/groups.json`; PRX-GRP-073 phase two). It writes
/// the group store only; members keep their own lifecycle state.
/// `praxis work group show`: a read-only view of one stored group.
/// `praxis work group add`: one more member for a stored group, recording who
/// added it; the member's own lifecycle is untouched.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let showUsage = "work group show GROUP-ID [--json]"

    let createUsage =
        "work group create --id GROUP-ID --member ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT]* [--architecture-note TEXT]* [--dry-run] [--json]"

    let addUsage =
        "work group add --id GROUP-ID --member ID --occurred-at TIMESTAMP [--dry-run] [--json]"

    let usage = createUsage + " | " + showUsage + " | " + addUsage

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

    let private addRequest (actor: Actor) (arguments: string list) : Result<GroupAddRequest, string> =
        match unexpected arguments, value "--id" arguments, values "--member" arguments, value "--occurred-at" arguments with
        | (_ :: _) as stray, _, _, _ -> Error("unexpected argument(s): " + String.concat " " stray)
        | _, None, _, _ -> Error "work group add requires --id GROUP-ID"
        | _, _, members, _ when members.Length <> 1 -> Error "work group add requires exactly one --member ID"
        | _, _, _, None -> Error "work group add requires --occurred-at TIMESTAMP"
        | _, _, _, Some timestamp when not (isTimestamp timestamp) -> Error $"--occurred-at '{timestamp}' is not a timestamp"
        | _, Some id, members, Some timestamp ->
            Ok
                { GroupId = id
                  WorkItem = List.head members
                  AddedAt = timestamp
                  AddedBy = actor.Id }

    let private envelope (kind: string) (fields: (string * JsonNode) list) : string =
        let root = JsonObject()
        root["schema"] <- JsonValue.Create "praxis.work-group/1.0.0"
        root["kind"] <- JsonValue.Create kind
        fields |> List.iter (fun (name, node) -> root[name] <- node)
        PlanningJson.render root

    let private document (kind: string) (dryRun: bool) (fields: (string * JsonNode) list) : string =
        envelope kind (("dryRun", JsonValue.Create dryRun :> JsonNode) :: fields)

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

    let private emit (kind: string) (describe: StoredGroup -> string) (json: bool) (dryRun: bool) (outcome: Result<StoredGroup, GroupCreateFailure>) =
        match outcome, json with
        | Ok stored, true ->
            printf "%s" (document kind dryRun [ "ok", JsonValue.Create true; "group", PlanningJson.storedGroup stored ])
            0
        | Ok stored, false ->
            printfn "%s" (describe stored)
            0
        | Error(GroupCreateFailure.Rejected rejections), true ->
            printf "%s" (document kind dryRun [ "ok", JsonValue.Create false; "rejections", rejectionsNode rejections ])
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
            eprintfn "Usage: %s" createUsage
            2
        | Ok parsed -> FileWorkGroupRepository.create root dryRun parsed |> emit "work-group-create" (describe dryRun) json dryRun

    // ---- add ----------------------------------------------------------------------

    let private describeAddition (dryRun: bool) (request: GroupAddRequest) (stored: StoredGroup) =
        let verb = if dryRun then "would add" else "added"

        [ $"{verb} {request.WorkItem} to {stored.Group.Id} (by {request.AddedBy} at {request.AddedAt})"
          "  members: " + String.concat ", " stored.Group.Members
          $"  cross-repository: {stored.Group.CrossRepository}"
          $"  {request.WorkItem} keeps its own lifecycle state" ]
        |> String.concat "\n"

    /// Exit 0 added, 1 refused or unreadable, 2 usage.
    let add (root: string) (actor: Actor) (arguments: string list) : int =
        let json = List.contains "--json" arguments
        let dryRun = List.contains "--dry-run" arguments

        match addRequest actor arguments with
        | Error message ->
            eprintfn "ERROR %s" message
            eprintfn "Usage: %s" addUsage
            2
        | Ok parsed -> FileWorkGroupRepository.add root dryRun parsed |> emit "work-group-add" (describeAddition dryRun parsed) json dryRun

    // ---- show ---------------------------------------------------------------------

    let private orUnknown (value: string option) = value |> Option.defaultValue "unknown"

    let private memberLine (entry: GroupViewMember) =
        let planning =
            match entry.PlanningState, entry.Status with
            | Some state, Some status -> $"{PlanningWorkState.code state} ({MemberStatus.code status})"
            | _ -> "unknown"

        let waits = if entry.GatedBy.IsEmpty then "" else $"""; waits on blocked {String.concat ", " entry.GatedBy}"""
        let gates = if entry.Gates.IsEmpty then "" else $"""; gates {String.concat ", " entry.Gates}"""
        $"  {entry.WorkItemId}: recorded {orUnknown entry.RecordedState}; planning {planning}{waits}{gates}"

    let private section (title: string) (lines: string list) =
        match lines with
        | [] -> [ $"{title}: none" ]
        | _ -> $"{title}:" :: (lines |> List.map (sprintf "  %s"))

    let private showText (view: GroupView) =
        let group = view.Declaration.Group
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"

        [ $"{group.Id} ({GroupOrigin.code group.Origin}, kind {kind}); declared by {view.Declaration.DeclaredBy} at {view.Declaration.DeclaredAt}"
          $"Execution repository: {orUnknown view.ExecutionRepository} ({RepositoryBasis.code view.RepositoryBasis}); cross-repository: {group.CrossRepository}"
          $"Progress: {view.Progress.Statement}"
          "Members:"
          yield! view.Members |> List.map memberLine
          yield!
              view.Blocked
              |> List.map (fun entry ->
                  match entry.Gates with
                  | [] -> $"{entry.WorkItemId} gates no other member"
                  | gated -> $"""{entry.WorkItemId} gates {String.concat ", " gated}""")
              |> section "Blocked members"
          yield! section "Shared context" group.SharedContext
          yield! section "Architecture notes" group.ArchitectureNotes
          yield!
              view.PlannerNotes
              |> List.map (fun note -> $"{Ros.Domain.Planning.FindingSeverity.code note.Severity} {GroupNoteCode.code note.Code}: {note.Message}")
              |> section "Planner notes"
          yield! view.Unavailable |> List.map (sprintf "Unavailable: %s")
          "Read-only: nothing was written; members keep their own lifecycle state." ]
        |> String.concat "\n"

    let private plannedAt () =
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    /// The planner's view of the group, read through the planning port, which
    /// has no write operation.
    let private planned (root: string) (version: string) (id: string) =
        PlanningOperations.analyze (FilePlanningRepository.create root None None) (plannedAt ()) version
        |> Result.map (fun (input, analysis) -> Grouping.recommend input analysis)
        |> Result.bind (fun report -> GroupView.planned report id)

    let private view (root: string) (version: string) (id: string) : Result<GroupView, string> =
        FilePlanningRepository.readStoredGroups root
        |> Result.bind (fun stored ->
            GroupView.tryFind stored id
            |> Option.map Ok
            |> Option.defaultValue (Error $"group {id} is not declared (see .ros/work/groups.json or 'work group create')"))
        |> Result.bind (fun stored ->
            FileWorkGroupRepository.memberStatuses root
            |> Result.map (fun statuses -> GroupView.build stored statuses (planned root version id)))

    /// Never writes: it reads the group store, the backlog, the live context
    /// and the planner's read-only analysis. Exit 0 shown, 1 unknown or
    /// unreadable group, 2 usage.
    let show (root: string) (version: string) (arguments: string list) : int =
        let json = List.contains "--json" arguments

        match arguments |> List.filter ((<>) "--json") with
        | [ id ] when not (id.StartsWith "-") ->
            match view root version id, json with
            | Ok shown, true ->
                printf "%s" (envelope "work-group-show" [ "ok", JsonValue.Create true; "view", PlanningJson.groupView shown ])
                0
            | Ok shown, false ->
                printfn "%s" (showText shown)
                0
            | Error message, true ->
                printf "%s" (envelope "work-group-show" [ "ok", JsonValue.Create false; "error", JsonValue.Create message ])
                1
            | Error message, false ->
                eprintfn "ERROR %s" message
                1
        | _ ->
            eprintfn "ERROR work group show requires exactly one GROUP-ID"
            eprintfn "Usage: %s" showUsage
            2
