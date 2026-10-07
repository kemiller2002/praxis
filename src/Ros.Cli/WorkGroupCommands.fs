namespace Ros.Cli

open System
open System.Globalization
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create|add|show`: declared execution groups in Praxis state
/// (PRX-GRP-073 phase two). Parses, delegates to `WorkGroupOperations`, and
/// renders; it holds no group policy.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let showUsage = "work group show GROUP-ID [--json]"

    let createUsage =
        "work group create --id GROUP-ID --member ITEM [--member ITEM]* --occurred-at TIMESTAMP [--kind KIND] [--origin ORIGIN] [--shared-context TEXT]* [--execution-repository NAME] [--cross-repository] [--architecture-note TEXT]* [--dry-run] [--json] [IDENTITY]"

    let addUsage =
        "work group add --id GROUP-ID --member ITEM --occurred-at TIMESTAMP [--config FILE] [--dry-run] [--json] [IDENTITY]"

    let usage = createUsage + " | " + addUsage + " | " + showUsage

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    let private createFlags =
        set (
            [ "--id"; "--member"; "--occurred-at"; "--kind"; "--origin"; "--shared-context"; "--execution-repository"; "--architecture-note" ]
            @ identityFlags
        )

    let private addFlags = set ([ "--id"; "--member"; "--occurred-at"; "--config" ] @ identityFlags)

    let private switches = set [ "--dry-run"; "--json"; "--cross-repository" ]

    /// Every flag value in argument order, keyed by flag.
    type private Parsed =
        { Values: (string * string) list
          Unexpected: string list }

    let rec private parseWith (flagsWithValues: Set<string>) (parsed: Parsed) (arguments: string list) =
        match arguments with
        | [] -> parsed
        | flag :: value :: rest when flagsWithValues.Contains flag && not (value.StartsWith "--") ->
            parseWith flagsWithValues { parsed with Values = parsed.Values @ [ flag, value ] } rest
        | switch :: rest when switches.Contains switch -> parseWith flagsWithValues parsed rest
        | token :: rest -> parseWith flagsWithValues { parsed with Unexpected = parsed.Unexpected @ [ token ] } rest

    let private parse = parseWith createFlags

    let private valuesOf (flag: string) (parsed: Parsed) =
        parsed.Values |> List.filter (fst >> (=) flag) |> List.map snd

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private single (flag: string) (parsed: Parsed) : Result<string option, string> =
        match valuesOf flag parsed with
        | [] -> Ok None
        | [ value ] -> Ok(Some value)
        | _ -> Error $"pass {flag} at most once"

    let private parseOptional what (tryParse: string -> 'a option) (value: string option) : Result<'a option, string> =
        match value with
        | None -> Ok None
        | Some text ->
            tryParse text
            |> Option.map (Some >> Ok)
            |> Option.defaultValue (Error $"unknown {what} '{text}'")

    /// The declaration the arguments describe, or every argument error.
    let private declaration (arguments: string list) (parsed: Parsed) : Result<DeclaredGroup * string, string list> =
        let ids = valuesOf "--id" parsed
        let occurredAt = valuesOf "--occurred-at" parsed
        let kind = single "--kind" parsed |> Result.bind (parseOptional "group kind" GroupKind.tryParse)
        let origin = single "--origin" parsed |> Result.bind (parseOptional "group origin" GroupOrigin.tryParse)
        let repository = single "--execution-repository" parsed

        let errors =
            [ match ids with
              | [ _ ] -> ()
              | [] -> yield "work group create requires --id GROUP-ID"
              | _ -> yield "work group create declares exactly one group; pass --id once"
              match occurredAt with
              | [ value ] when isTimestamp value -> ()
              | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
              | _ -> yield "work group create requires exactly one --occurred-at TIMESTAMP (the real current time)"
              for result in [ kind |> Result.map ignore; origin |> Result.map ignore; repository |> Result.map ignore ] do
                  match result with
                  | Error message -> yield message
                  | Ok() -> ()
              for token in parsed.Unexpected do
                  yield $"unexpected argument '{token}'" ]

        match errors, kind, origin, repository with
        | [], Ok kind, Ok origin, Ok repository ->
            Ok(
                { Id = List.head ids
                  Members = valuesOf "--member" parsed
                  Kind = kind
                  Origin = origin |> Option.defaultValue GroupOrigin.HumanDeclared
                  SharedContext = valuesOf "--shared-context" parsed
                  ExecutionRepository = repository
                  CrossRepository = List.contains "--cross-repository" arguments
                  ArchitectureNotes = valuesOf "--architecture-note" parsed },
                List.head occurredAt
            )
        | errors, _, _, _ -> Error errors

    let private renderText (verb: string) (stored: StoredGroup) (members: (string * string) list) =
        let group = stored.Declaration
        printfn "%s group %s (%s, %s) with %d member(s)" verb group.Id (GroupOrigin.code group.Origin) (group.Kind |> Option.map GroupKind.code |> Option.defaultValue "kind unspecified") members.Length

        for id, state in members do
            printfn "  %s  %s" id state

        group.ExecutionRepository |> Option.iter (printfn "execution repository: %s")

        if group.CrossRepository then
            printfn "cross-repository: yes"

        for line in group.SharedContext do
            printfn "shared context: %s" line

        for line in group.ArchitectureNotes do
            printfn "architecture note: %s" line

        printfn "membership is advisory: no member's lifecycle state, evidence or attribution changed"

    let private render asJson (id: string) (outcome: GroupCreateOutcome) =
        match outcome with
        | GroupCreateOutcome.Rejected rejections ->
            let messages = rejections |> List.map GroupRejection.message

            if asJson then
                printf "%s" (PlanningJson.renderGroupRejected id messages)
            else
                for message in messages do
                    eprintfn "ERROR %s" message

                eprintfn "group %s was not recorded" id

            1
        | GroupCreateOutcome.Planned(stored, members) ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCreated true stored members)
            else
                renderText "dry run: would record" stored members
                printfn "nothing was recorded"

            0
        | GroupCreateOutcome.Recorded(stored, members) ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCreated false stored members)
            else
                renderText "recorded" stored members

            0

    let create root (arguments: string list) (actor: Actor) =
        let parsed = parse { Values = []; Unexpected = [] } arguments

        match declaration arguments parsed with
        | Error errors ->
            for error in errors do
                eprintfn "ERROR %s" error

            eprintfn "Usage: ros %s" createUsage
            2
        | Ok(group, occurredAt) ->
            let request =
                { Declaration = group
                  OccurredAt = occurredAt
                  Actor = Some actor
                  DryRun = List.contains "--dry-run" arguments }

            match WorkGroupOperations.create (FileWorkGroupRepository.create root) request with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok outcome -> render (List.contains "--json" arguments) group.Id outcome

    /// The add request the arguments describe (with the optional planner
    /// configuration file), or every argument error. `--cross-repository`
    /// belongs to the group's declaration, so add refuses it.
    let private addRequest (arguments: string list) (parsed: Parsed) : Result<GroupAddRequest * string option, string list> =
        let one flag what =
            match valuesOf flag parsed with
            | [ value ] -> Ok value
            | [] -> Error $"work group add requires {flag} {what}"
            | _ -> Error $"work group add takes exactly one {flag}; add members one at a time"

        let groupId = one "--id" "GROUP-ID"
        let memberId = one "--member" "ITEM"

        let occurredAt =
            match valuesOf "--occurred-at" parsed with
            | [ value ] when isTimestamp value -> Ok value
            | [ value ] -> Error $"--occurred-at '{value}' is not a timestamp"
            | _ -> Error "work group add requires exactly one --occurred-at TIMESTAMP (the real current time)"

        let configuration = single "--config" parsed

        let errors =
            [ for result in [ groupId |> Result.map ignore; memberId |> Result.map ignore; occurredAt |> Result.map ignore; configuration |> Result.map ignore ] do
                  match result with
                  | Error message -> yield message
                  | Ok() -> ()
              if List.contains "--cross-repository" arguments then
                  yield "--cross-repository is part of a group's declaration; work group add does not change it"
              for token in parsed.Unexpected do
                  yield $"unexpected argument '{token}'" ]

        match errors, groupId, memberId, occurredAt, configuration with
        | [], Ok groupId, Ok memberId, Ok occurredAt, Ok configuration ->
            Ok(
                { GroupId = groupId
                  Member = memberId
                  OccurredAt = occurredAt
                  Actor = None
                  DryRun = List.contains "--dry-run" arguments },
                configuration
            )
        | errors, _, _, _, _ -> Error errors

    let private addText (verb: string) (stored: StoredGroup) (added: AddedMember) =
        let group = stored.Declaration
        let by = added.Change.Actor |> Option.map (fun actor -> actor.Id) |> Option.defaultValue "unknown"
        let members = String.concat ", " group.Members

        [ $"{verb} {added.Change.Member} to group {group.Id} ({added.State}; executes in {added.ExecutionRepository})"
          $"added at {added.Change.OccurredAt} by {by}"
          $"members ({group.Members.Length}): {members}"
          "membership is advisory: the member's lifecycle state, evidence and attribution are unchanged" ]
        |> List.iter (printfn "%s")

    let private renderAdd asJson (request: GroupAddRequest) (outcome: GroupAddOutcome) =
        match outcome with
        | GroupAddOutcome.Rejected rejections ->
            let messages = rejections |> List.map GroupRejection.message

            if asJson then
                printf "%s" (PlanningJson.renderMemberRejected request.GroupId request.Member messages)
            else
                for message in messages do
                    eprintfn "ERROR %s" message

                eprintfn "%s was not added to %s" request.Member request.GroupId

            1
        | GroupAddOutcome.Planned(stored, added) ->
            if asJson then
                printf "%s" (PlanningJson.renderMemberAdded true stored added.Change added.State added.ExecutionRepository)
            else
                addText "dry run: would add" stored added
                printfn "nothing was recorded"

            0
        | GroupAddOutcome.Recorded(stored, added) ->
            if asJson then
                printf "%s" (PlanningJson.renderMemberAdded false stored added.Change added.State added.ExecutionRepository)
            else
                addText "added" stored added

            0

    /// `work group add`: exit 0 added (or dry run), 1 refusal or IO error
    /// (nothing written), 2 argument error.
    let add root (version: string) (arguments: string list) (actor: Actor) =
        match addRequest arguments (parseWith addFlags { Values = []; Unexpected = [] } arguments) with
        | Error errors ->
            for error in errors do
                eprintfn "ERROR %s" error

            eprintfn "Usage: ros %s" addUsage
            2
        | Ok(request, configuration) ->
            let request = { request with Actor = Some actor }
            let plannedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            let planning = FilePlanningRepository.create root None configuration

            match WorkGroupOperations.add (FileWorkGroupRepository.create root) planning plannedAt version request with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok outcome -> renderAdd (List.contains "--json" arguments) request outcome

    let private showText (plan: PlanSnapshot) (view: GroupView) =
        let group = view.Group.Declaration
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "kind unspecified"
        let declaredBy = view.Group.DeclaredBy |> Option.map (fun actor -> actor.Id) |> Option.defaultValue "unknown"
        let list (values: string list) = if values.IsEmpty then "-" else String.concat ", " values

        let recorded (entry: GroupMemberView) =
            match entry.Recorded with
            | None -> "unknown"
            | Some(MemberState.Open code)
            | Some(MemberState.Terminal code) -> code

        let planning (entry: GroupMemberView) =
            entry.Planning |> Option.map (fun planned -> PlanningWorkState.code planned.PlanningState) |> Option.defaultValue "not in planning inventory"

        let repository =
            match group.ExecutionRepository, view.PlannerExecutionRepository with
            | Some declared, _ -> $"{declared} (declared)"
            | None, Some resolved -> $"{resolved} (resolved by the planner)"
            | None, None -> "unknown"

        let crossRepository = if group.CrossRepository then "yes" else "no"

        [ yield $"Group {group.Id} ({GroupOrigin.code group.Origin}, {kind}); declared {view.Group.DeclaredAt} by {declaredBy}"
          yield $"Execution repository: {repository}; cross-repository: {crossRepository}"
          yield $"Progress: {view.Progress.Statement}"
          yield ""
          yield "Members (recorded state | planning state | status):"
          for entry in view.Members do
              let gatedBy = entry.Planning |> Option.map (fun planned -> planned.GatedBy) |> Option.defaultValue []
              let waits = if gatedBy.IsEmpty then "" else $"; gated by {list gatedBy}"
              let gates = if entry.Gates.IsEmpty then "" else $"; gates {list entry.Gates}"
              yield $"  {entry.WorkItemId}  {recorded entry} | {planning entry} | {GroupView.statusCode entry}{waits}{gates}"
          yield ""
          yield "Blocked members:"
          if view.Blocked.IsEmpty then yield "  none"
          for entry in view.Blocked do
              yield $"  {entry.WorkItemId} gates {list entry.Gates}"
          for line in group.SharedContext do
              yield $"Shared context: {line}"
          for line in group.ArchitectureNotes do
              yield $"Architecture note: {line}"
          for note in view.Notes do
              yield $"Note [{GroupNoteCode.code note.Code}] {note.Message}"
          let commit = plan.Commit |> Option.map (fun value -> value.Substring(0, min 12 value.Length)) |> Option.defaultValue "unknown"
          yield $"Planned {plan.PlannedAt} at {commit}"
          yield view.Statement ]
        |> List.iter (printfn "%s")

    /// `work group show GROUP-ID`: read-only. Exit 0 shown, 1 unknown group or
    /// read error, 2 argument error.
    let show root (version: string) (arguments: string list) =
        let asJson = List.contains "--json" arguments
        let positional = arguments |> List.filter ((<>) "--json")

        match positional with
        | [ id ] when not (id.StartsWith "--") ->
            let plannedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

            match WorkGroupOperations.show (FileWorkGroupRepository.create root) (FilePlanningRepository.create root None None) plannedAt version id with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok(GroupShowOutcome.NotFound id) ->
                if asJson then
                    printf "%s" (PlanningJson.renderGroupNotFound id)
                else
                    eprintfn "ERROR no declared group %s; see 'plan groups' or .ros/work/groups.json" id

                1
            | Ok(GroupShowOutcome.Shown(view, plan)) ->
                if asJson then printf "%s" (PlanningJson.renderGroupShown plan view) else showText plan view
                0
        | _ ->
            eprintfn "ERROR work group show requires exactly one group ID"
            eprintfn "Usage: ros %s" showUsage
            2

    /// Stored-group findings for the unified `validate`, as (path, field, message).
    let findings root : Result<(string * string * string) list, string> =
        let path = ".ros/work/groups.json"

        WorkGroupOperations.findings (FileWorkGroupRepository.create root)
        |> Result.map (List.map (fun finding -> path, finding.Field, finding.Message))
