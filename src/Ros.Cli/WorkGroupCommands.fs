namespace Ros.Cli

open System
open System.Globalization
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create`: records a durable human-declared execution group;
/// `work group add`: adds one member to a declared group and records who
/// added it; `work group show`: a read-only view of one (PRX-GRP-073 phase
/// two). This module parses, delegates to `FileWorkGroupRepository` or the
/// planner's read-only port, and renders; the policy lives in
/// `GroupDeclaration`, `GroupMembership` and `GroupView`. It never changes a
/// member's lifecycle state.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group create --id GROUP-ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository REPOSITORY] [--cross-repository] [--shared-context TEXT]* [--architecture-note TEXT]* [--dry-run] [--json] [IDENTITY]"

    let showUsage = "work group show GROUP-ID [--as-of TIMESTAMP] [--json]"

    let addUsage =
        "work group add --id GROUP-ID --member ID --occurred-at TIMESTAMP [--reason TEXT] [--config FILE] [--dry-run] [--json] [IDENTITY]"

    let private identityFlags =
        set
            [ "--actor-kind"
              "--agent"
              "--actor"
              "--provider"
              "--model"
              "--model-version"
              "--runtime"
              "--runtime-version"
              "--session"
              "--conversation"
              "--run"
              "--subagent" ]

    let private flagsWithValues =
        Set.union
            identityFlags
            (set
                [ "--id"
                  "--member"
                  "--occurred-at"
                  "--kind"
                  "--execution-repository"
                  "--shared-context"
                  "--architecture-note"
                  "--reason"
                  "--config" ])

    let private switches = set [ "--dry-run"; "--json"; "--cross-repository" ]

    /// Flags the shared parser knows that only one subcommand accepts.
    let private createOnly =
        set [ "--kind"; "--execution-repository"; "--cross-repository"; "--shared-context"; "--architecture-note" ]

    let private addOnly = set [ "--reason"; "--config" ]

    type private Parsed =
        { Values: Map<string, string list>
          Switches: Set<string>
          Unexpected: string list }

    let rec private parse (parsed: Parsed) (arguments: string list) =
        match arguments with
        | [] -> parsed
        | flag :: value :: rest when flagsWithValues.Contains flag && not (value.StartsWith "--") ->
            let existing = parsed.Values |> Map.tryFind flag |> Option.defaultValue []
            parse { parsed with Values = parsed.Values |> Map.add flag (existing @ [ value ]) } rest
        | switch :: rest when switches.Contains switch -> parse { parsed with Switches = parsed.Switches.Add switch } rest
        | token :: rest -> parse { parsed with Unexpected = parsed.Unexpected @ [ token ] } rest

    let private values (parsed: Parsed) flag = parsed.Values |> Map.tryFind flag |> Option.defaultValue []

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private singleFor (command: string) (parsed: Parsed) flag (required: bool) : Result<string option, string> =
        match values parsed flag, required with
        | [ value ], _ -> Ok(Some value)
        | [], false -> Ok None
        | [], true -> Error $"work group {command} requires {flag}"
        | _ -> Error $"pass {flag} once"

    let private single = singleFor "create"

    /// Flags another subcommand owns, reported rather than silently ignored.
    let private foreign (command: string) (owned: Set<string>) (parsed: Parsed) =
        Set.union (parsed.Values |> Map.keys |> Set.ofSeq) parsed.Switches
        |> Set.intersect owned
        |> Set.toList
        |> List.map (fun flag -> $"work group {command} does not accept {flag}")

    let private occurredAtFor (command: string) (parsed: Parsed) =
        singleFor command parsed "--occurred-at" true
        |> Result.bind (function
            | Some value when isTimestamp value -> Ok value
            | Some value -> Error $"--occurred-at '{value}' is not a timestamp"
            | None -> Error $"work group {command} requires --occurred-at TIMESTAMP (the real current time)")

    let private kind (parsed: Parsed) : Result<GroupKind option, string> =
        single parsed "--kind" false
        |> Result.bind (function
            | None -> Ok None
            | Some value ->
                match GroupKind.tryParse value with
                | Some parsedKind -> Ok(Some parsedKind)
                | None ->
                    let known = GroupKind.knownCodes |> String.concat ", "
                    Error $"--kind '{value}' is not one of {known}, or custom:NAME")

    /// Every argument problem at once, or the declared group.
    let private request (parsed: Parsed) : Result<DeclaredGroup * string, string list> =
        let id = single parsed "--id" true
        let occurredAt = occurredAtFor "create" parsed
        let repository = single parsed "--execution-repository" false
        let groupKind = kind parsed

        let errors =
            [ id |> Result.map ignore
              occurredAt |> Result.map ignore
              repository |> Result.map ignore
              groupKind |> Result.map ignore ]
            |> List.choose (function
                | Error message -> Some message
                | Ok() -> None)
            |> fun found -> found @ foreign "create" addOnly parsed @ (parsed.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'"))

        match errors, id, occurredAt, repository, groupKind with
        | [], Ok(Some groupId), Ok timestamp, Ok executionRepository, Ok declaredKind ->
            Ok(
                { Id = groupId
                  Members = values parsed "--member"
                  Kind = declaredKind
                  Origin = GroupOrigin.HumanDeclared
                  SharedContext = values parsed "--shared-context"
                  ExecutionRepository = executionRepository
                  CrossRepository = parsed.Switches.Contains "--cross-repository"
                  ArchitectureNotes = values parsed "--architecture-note" },
                timestamp
            )
        | errors, _, _, _, _ -> Error errors

    let private describe (entry: StoredGroupDeclaration) =
        let group = entry.Group
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"
        let repository = group.ExecutionRepository |> Option.defaultValue "this repository"
        let scope = if group.CrossRepository then "cross-repository" else "repository-local"
        [ yield $"  members:    {String.Join(' ', group.Members)}"
          yield $"  kind:       {kind}"
          yield $"  executes:   {repository} ({scope})"
          yield! group.SharedContext |> List.map (fun line -> $"  context:    {line}")
          yield! group.ArchitectureNotes |> List.map (fun line -> $"  note:       {line}") ]
        |> List.iter (printfn "%s")

    let private render asJson outcome =
        match outcome with
        | WorkGroupDeclarationOutcome.Rejected(entry, rejections) ->
            if asJson then printf "%s" (WorkGroupJson.renderOutcome "rejected" WorkGroupStore.RelativePath entry rejections)
            rejections |> List.iter (GroupDeclarationRejection.message >> eprintfn "ERROR %s")
            eprintfn "group %s was not declared; nothing was recorded" entry.Group.Id
            1
        | WorkGroupDeclarationOutcome.Planned entry ->
            if asJson then
                printf "%s" (WorkGroupJson.renderOutcome "planned" WorkGroupStore.RelativePath entry [])
            else
                printfn "dry run: would declare group %s in %s; nothing was recorded" entry.Group.Id WorkGroupStore.RelativePath
                describe entry
            0
        | WorkGroupDeclarationOutcome.Recorded entry ->
            if asJson then
                printf "%s" (WorkGroupJson.renderOutcome "created" WorkGroupStore.RelativePath entry [])
            else
                printfn "declared group %s in %s" entry.Group.Id WorkGroupStore.RelativePath
                describe entry
                printfn "no member's lifecycle state changed; 'plan groups' now reads this declaration"
            0

    // ---- add -------------------------------------------------------------------

    type private AdditionArguments =
        { Addition: MemberAdditionRequest
          OccurredAt: string
          ConfigurationFile: string option }

    /// Every argument problem at once, or the requested addition.
    let private additionRequest (parsed: Parsed) : Result<AdditionArguments, string list> =
        let id = singleFor "add" parsed "--id" true
        let workItem = singleFor "add" parsed "--member" true
        let occurredAt = occurredAtFor "add" parsed
        let reason = singleFor "add" parsed "--reason" false
        let configuration = singleFor "add" parsed "--config" false

        let errors =
            [ id |> Result.map ignore
              workItem |> Result.map ignore
              occurredAt |> Result.map ignore
              reason |> Result.map ignore
              configuration |> Result.map ignore ]
            |> List.choose (function
                | Error message -> Some message
                | Ok() -> None)
            |> fun found -> found @ foreign "add" createOnly parsed @ (parsed.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'"))

        match errors, id, workItem, occurredAt, reason, configuration with
        | [], Ok(Some groupId), Ok(Some item), Ok timestamp, Ok why, Ok configurationFile ->
            Ok
                { Addition = { GroupId = groupId; WorkItem = item; Reason = why }
                  OccurredAt = timestamp
                  ConfigurationFile = configurationFile }
        | errors, _, _, _, _, _ -> Error errors

    let private additionPaths = [ WorkGroupStore.RelativePath; GroupMembershipStore.RelativePath ]

    let private renderAddition asJson outcome =
        let json status (addition: MemberAddition) members rejections =
            if asJson then printf "%s" (WorkGroupMembershipJson.renderOutcome status additionPaths addition members rejections)

        let by (addition: MemberAddition) =
            let why = addition.Reason |> Option.map (fun reason -> $" ({reason})") |> Option.defaultValue ""
            $"  added by:   {addition.AddedBy.Id} at {addition.AddedAt}{why}"

        match outcome with
        | WorkGroupAdditionOutcome.Rejected(addition, members, rejections) ->
            json "rejected" addition members rejections
            rejections |> List.iter (GroupAdditionRejection.message >> eprintfn "ERROR %s")
            eprintfn "%s was not added to %s; nothing was recorded" addition.WorkItem addition.GroupId
            1
        | WorkGroupAdditionOutcome.Planned(addition, updated) ->
            json "planned" addition updated.Group.Members []

            if not asJson then
                printfn "dry run: would add %s to group %s; nothing was recorded" addition.WorkItem addition.GroupId
                printfn "  members:    %s" (String.Join(' ', updated.Group.Members))
                printfn "%s" (by addition)
            0
        | WorkGroupAdditionOutcome.Added(addition, updated) ->
            json "added" addition updated.Group.Members []

            if not asJson then
                printfn "added %s to group %s in %s" addition.WorkItem addition.GroupId WorkGroupStore.RelativePath
                printfn "  members:    %s" (String.Join(' ', updated.Group.Members))
                printfn "%s" (by addition)
                printfn "recorded in %s; no member's lifecycle state changed" GroupMembershipStore.RelativePath
            0

    let run root (arguments: string list) (actor: Actor) =
        match arguments with
        | "add" :: rest ->
            let parsed = parse { Values = Map.empty; Switches = Set.empty; Unexpected = [] } rest

            match additionRequest parsed with
            | Error errors ->
                errors |> List.iter (eprintfn "ERROR %s")
                eprintfn "Usage: ros %s" addUsage
                2
            | Ok arguments ->
                let request =
                    { Addition = arguments.Addition
                      OccurredAt = arguments.OccurredAt
                      Actor = actor
                      ConfigurationFile = arguments.ConfigurationFile
                      DryRun = parsed.Switches.Contains "--dry-run" }

                match FileWorkGroupRepository.add root request with
                | Error message ->
                    eprintfn "ERROR %s" message
                    1
                | Ok outcome -> renderAddition (parsed.Switches.Contains "--json") outcome
        | "create" :: rest ->
            let parsed = parse { Values = Map.empty; Switches = Set.empty; Unexpected = [] } rest

            match request parsed with
            | Error errors ->
                errors |> List.iter (eprintfn "ERROR %s")
                eprintfn "Usage: ros %s" usage
                2
            | Ok(group, occurredAt) ->
                let command =
                    { Group = group
                      OccurredAt = occurredAt
                      Actor = actor
                      DryRun = parsed.Switches.Contains "--dry-run" }

                match FileWorkGroupRepository.declare root command with
                | Error message ->
                    eprintfn "ERROR %s" message
                    1
                | Ok outcome -> render (parsed.Switches.Contains "--json") outcome
        | _ ->
            eprintfn "Usage: ros %s | %s | %s" usage addUsage showUsage
            2

    // ---- show -------------------------------------------------------------------

    let private showLines (snapshot: PlanSnapshot) (view: GroupView) =
        let entry = view.Declaration
        let group = entry.Group
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"
        let commit = snapshot.Commit |> Option.map (fun value -> value.Substring(0, min 12 value.Length)) |> Option.defaultValue "unknown"
        let state (value: string option) = value |> Option.defaultValue "untracked"
        let planning (value: PlanningWorkState option) = value |> Option.map PlanningWorkState.code |> Option.defaultValue "unknown"
        let width = view.Members |> List.map (fun item -> item.WorkItem.Length) |> List.fold max 0
        let names (values: string list) = String.Join(", ", values)

        let memberLine (item: GroupMemberView) =
            let waits = if item.WaitsOn.IsEmpty then "" else $"; waits on {names item.WaitsOn}"
            let title = item.Title |> Option.map (fun value -> $"  {value}") |> Option.defaultValue ""
            $"  {item.WorkItem.PadRight width}  recorded {state item.RecordedState}; planning {planning item.PlanningState}{waits}{title}"

        let blockedLines (blocked: BlockedMember) =
            let gates =
                match blocked.GatesMembers, blocked.GatesOthers with
                | [], [] -> "gates no pending work"
                | members, [] -> $"gates members {names members}"
                | [], others -> $"gates non-members {names others}"
                | members, others -> $"gates members {names members}; non-members {names others}"

            [ yield $"  {blocked.WorkItem} [{planning blocked.PlanningState}]: {gates}"
              yield! blocked.Reasons |> List.map (fun reason -> $"    - {reason}") ]

        let repository = group.ExecutionRepository |> Option.defaultValue "this repository"
        let scope = if group.CrossRepository then "cross-repository" else "repository-local"

        [ yield $"group {group.Id} ({GroupOrigin.code group.Origin}, {kind}); declared {entry.DeclaredAt} by {entry.DeclaredBy.Id}"
          yield $"  executes:   {repository} ({scope})"
          yield! group.SharedContext |> List.map (fun line -> $"  context:    {line}")
          yield! group.ArchitectureNotes |> List.map (fun line -> $"  note:       {line}")
          yield $"  planning:   {snapshot.Repository} at {commit}, as of {snapshot.PlannedAt}"
          yield ""
          yield $"Progress: {GroupView.progressStatement view.Progress}"
          yield ""
          yield "Members (own recorded state; planner classification):"
          yield! view.Members |> List.map memberLine
          yield ""
          match view.Blocked with
          | [] -> yield "Blocked: none"
          | blocked ->
              yield "Blocked:"
              yield! blocked |> List.collect blockedLines
          yield ""
          yield "Read-only: nothing was written and no member's lifecycle state changed." ]

    type private ShowOptions =
        { Json: bool
          AsOf: string option
          Positional: string list
          Unexpected: string list }

    let rec private parseShow (options: ShowOptions) (arguments: string list) =
        match arguments with
        | [] -> options
        | "--json" :: rest -> parseShow { options with Json = true } rest
        | "--as-of" :: value :: rest when not (value.StartsWith "--") && options.AsOf.IsNone -> parseShow { options with AsOf = Some value } rest
        | token :: rest when token.StartsWith "--" -> parseShow { options with Unexpected = options.Unexpected @ [ token ] } rest
        | token :: rest -> parseShow { options with Positional = options.Positional @ [ token ] } rest

    let private plannedAt (options: ShowOptions) : Result<string, string> =
        match options.AsOf with
        | None -> Ok(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
        | Some value when isTimestamp value -> Ok value
        | Some value -> Error $"--as-of '{value}' is not a timestamp"

    /// `work group show`: reads the store and the planner's read-only port;
    /// writes nothing. Exit 1 for a group that is not declared.
    let show (root: string) (version: string) (arguments: string list) : int =
        let options = parseShow { Json = false; AsOf = None; Positional = []; Unexpected = [] } arguments

        let usageError (message: string) =
            eprintfn "ERROR %s" message
            eprintfn "Usage: ros %s" showUsage
            2

        match options.Unexpected, options.Positional, plannedAt options with
        | unexpected, _, _ when not unexpected.IsEmpty -> usageError $"unexpected argument(s): {String.Join(' ', unexpected)}"
        | _, positional, _ when positional.Length <> 1 -> usageError "work group show requires exactly one group ID"
        | _, _, Error message -> usageError message
        | _, [ id ], Ok timestamp ->
            let found =
                WorkGroupStore.read root
                |> Result.bind (fun stored ->
                    match GroupView.find stored id with
                    | None -> Ok None
                    | Some entry ->
                        PlanningOperations.analyze (FilePlanningRepository.create root None None) timestamp version
                        |> Result.map (fun (input, analysis) ->
                            Some(analysis.Snapshot, GroupView.project (GroupDeclaration.standing input.Queue input.Live) analysis entry)))

            match found with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok None ->
                if options.Json then printf "%s" (WorkGroupJson.renderNotFound id WorkGroupStore.RelativePath)
                eprintfn "ERROR group %s is not declared in %s (see 'work group create')" id WorkGroupStore.RelativePath
                1
            | Ok(Some(snapshot, view)) ->
                if options.Json then printf "%s" (WorkGroupJson.renderView snapshot view)
                else showLines snapshot view |> List.iter (printfn "%s")
                0
        | _ -> usageError "work group show requires exactly one group ID"
