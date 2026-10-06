namespace Praxis.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Planning
open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Application.Work
open Praxis.Infrastructure.Git
open Praxis.Infrastructure.Json
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group create|show|add|remove|checkpoint` (PRX-GRP-073, phase two):
/// durable, human-declared execution groups recorded in
/// `.ros/work/groups.json`. This module parses arguments, gathers repository
/// facts through the file adapters, and renders; every decision is made by
/// `Praxis.Domain.Work.WorkGroups`.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let showUsage = "work group show GROUP-ID [--config FILE] [--json]"

    let checkpointUsage =
        "work group checkpoint --group GROUP-ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT [--decision TEXT ...] [--dry-run] [--json] [IDENTITY]"

    let removeUsage =
        "work group remove --group GROUP-ID --member ID --occurred-at TIMESTAMP [--allow-empty] [--reason TEXT] [--dry-run] [--json] [IDENTITY]"

    let addUsage =
        "work group add --group GROUP-ID --member ID --occurred-at TIMESTAMP [--config FILE] [--reason TEXT] [--dry-run] [--json] [IDENTITY]"

    let usage =
        showUsage
        + " | "
        + addUsage
        + " | "
        + removeUsage
        + " | "
        + checkpointUsage
        + " | work group create --group GROUP-ID --member ID [--member ID ...] --occurred-at TIMESTAMP [--kind KIND] [--origin ORIGIN] [--shared-context TEXT ...] [--architecture-note TEXT ...] [--execution-repository NAME] [--cross-repository] [--config FILE] [--reason TEXT] [--dry-run] [--json] [IDENTITY]"

    // ---- argument parsing (shared by the family) ----

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    /// Parsed arguments: repeatable value flags, boolean switches, and
    /// everything that was not understood.
    type Arguments =
        { Values: Map<string, string list>
          Switches: Set<string>
          Positional: string list
          Unexpected: string list }

    let parse (valueFlags: string list) (switches: string list) (arguments: string list) : Arguments =
        let values = Set.ofList (valueFlags @ identityFlags)
        let flags = Set.ofList ("--json" :: switches)

        let rec walk (remaining: string list) (parsed: Arguments) =
            match remaining with
            | [] -> { parsed with Values = parsed.Values |> Map.map (fun _ values -> List.rev values); Positional = List.rev parsed.Positional; Unexpected = List.rev parsed.Unexpected }
            | flag :: value :: rest when values.Contains flag && not (value.StartsWith("--", StringComparison.Ordinal)) ->
                let existing = parsed.Values |> Map.tryFind flag |> Option.defaultValue []
                walk rest { parsed with Values = parsed.Values |> Map.add flag (value :: existing) }
            | flag :: rest when flags.Contains flag -> walk rest { parsed with Switches = parsed.Switches.Add flag }
            | token :: rest when not (token.StartsWith("--", StringComparison.Ordinal)) -> walk rest { parsed with Positional = token :: parsed.Positional }
            | token :: rest -> walk rest { parsed with Unexpected = token :: parsed.Unexpected }

        walk arguments { Values = Map.empty; Switches = Set.empty; Positional = []; Unexpected = [] }

    let private all (arguments: Arguments) (flag: string) = arguments.Values |> Map.tryFind flag |> Option.defaultValue []

    let private single (arguments: Arguments) (flag: string) =
        match all arguments flag with
        | [ value ] -> Some value
        | _ -> None

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    let private commonErrors (command: string) (arguments: Arguments) (singles: string list) =
        [ yield! arguments.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'")
          for flag in singles do
              if (all arguments flag).Length > 1 then
                  yield $"{command} accepts {flag} once" ]

    let private occurredAtErrors (command: string) (arguments: Arguments) =
        match all arguments "--occurred-at" with
        | [ value ] when isTimestamp value -> []
        | [ value ] -> [ $"--occurred-at '{value}' is not a timestamp" ]
        | _ -> [ $"{command} requires exactly one --occurred-at TIMESTAMP (the real current time)" ]

    let private groupErrors (command: string) (arguments: Arguments) =
        match all arguments "--group" with
        | [ _ ] -> []
        | [] -> [ $"{command} requires --group GROUP-ID" ]
        | _ -> [ $"{command} names exactly one group; pass --group once" ]

    // ---- repository facts ----

    let standing (root: string) = FileWorkGroupFacts.standing root

    let private contextFor (root: string) (configurationFile: string option) (groups: StoredWorkGroup list) =
        FileWorkGroupFacts.context root configurationFile groups

    // ---- rendering (shared by the family) ----

    let envelope (command: string) (status: string) (fields: (string * JsonNode) list) =
        WorkGroupJson.record ([ "command", WorkGroupJson.text command; "schemaVersion", WorkGroupJson.integer 1; "status", WorkGroupJson.text status ] @ fields)

    let printJson (node: JsonObject) = printf "%s" (WorkGroupJson.render node)

    let reportArgumentErrors (command: string) (commandUsage: string) (errors: string list) =
        errors |> List.iter (eprintfn "ERROR %s")
        eprintfn "Usage: ros %s" commandUsage
        2

    let private reportFailure (asJson: bool) (command: string) (message: string) =
        if asJson then
            printJson (envelope command "failed" [ "failure", WorkGroupJson.record [ "code", WorkGroupJson.text "persistence-failed"; "message", WorkGroupJson.text message ] ])
        else
            eprintfn "ERROR [persistence-failed] %s" message

        1

    let private reportRejections (asJson: bool) (command: string) (groupId: string) (rejections: GroupRejection list) =
        if asJson then
            printJson (envelope command "rejected" [ "groupId", WorkGroupJson.text groupId; "rejections", rejections |> List.map WorkGroupJson.rejectionNode |> WorkGroupJson.array ])
        else
            for rejection in rejections do
                eprintfn "ERROR [%s] %s" (GroupRejection.code rejection) (GroupRejection.message rejection)

            eprintfn "%s refused; nothing was recorded" command

        if rejections |> List.forall GroupRejection.isArgumentError then 2 else 1

    let private stateNotice = $"Praxis state changed in {FileWorkGroupRepository.relativePath}; commit and push it. No member's lifecycle, evidence or attribution was changed."

    /// Runs one mutation of the store: facts, decision, write (unless
    /// `--dry-run` or nothing changed), render. Every mutating verb goes
    /// through here. A repeat whose end state holds reports `changed: false`
    /// and appends no history (PRX-GRP-114).
    let private mutate
        (root: string)
        (command: string)
        (arguments: Arguments)
        (groupId: string)
        (decide: GroupContext -> Result<GroupChange, GroupRejection list>)
        (describe: StoredWorkGroup -> string list)
        =
        let asJson = arguments.Switches.Contains "--json"
        let dryRun = arguments.Switches.Contains "--dry-run"

        let outcome =
            FileWorkGroupRepository.transact root dryRun (fun groups ->
                match contextFor root (single arguments "--config") groups with
                | Error message -> Error(Choice1Of2 message)
                | Ok(_, context) ->
                    decide context
                    |> Result.mapError Choice2Of2
                    |> Result.map (fun change -> (if change.Changed then WorkGroups.upsert groups change.Group else groups), change))

        match outcome with
        | Error message
        | Ok(Error(Choice1Of2 message)) -> reportFailure asJson command message
        | Ok(Error(Choice2Of2 rejections)) -> reportRejections asJson command groupId rejections
        | Ok(Ok change) ->
            let status = if dryRun then "dry-run" elif change.Changed then "recorded" else "unchanged"

            if asJson then
                printJson (envelope command status [ "dryRun", WorkGroupJson.boolean dryRun; "changed", WorkGroupJson.boolean change.Changed; "group", WorkGroupJson.groupNode change.Group ])
            else
                describe change.Group |> List.iter (printfn "%s")

                if not change.Changed then printfn "unchanged: the requested state already holds; nothing was recorded"
                elif dryRun then printfn "dry run: nothing was written"
                else printfn "%s" stateNotice

            0

    // ---- work group create ----

    let private createValues =
        [ "--group"; "--member"; "--occurred-at"; "--kind"; "--origin"; "--shared-context"; "--architecture-note"; "--execution-repository"; "--config"; "--reason" ]

    let private callerExecution (root: string) (rawArguments: string list) =
        FileWorkGroupFacts.callerExecution root (ProvenanceCommands.identityOverridesFrom rawArguments)

    let create (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group create"
        let arguments = parse createValues [ "--cross-repository"; "--dry-run" ] rawArguments

        let kind = single arguments "--kind" |> Option.map (fun value -> value, GroupKind.tryParse value)
        let origin = single arguments "--origin" |> Option.map (fun value -> value, GroupOrigin.tryParse value)

        let errors =
            [ yield! commonErrors command arguments [ "--kind"; "--origin"; "--execution-repository"; "--config"; "--reason" ]
              yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
              yield! groupErrors command arguments
              yield! occurredAtErrors command arguments
              if (all arguments "--member").IsEmpty then
                  yield $"{command} requires at least one --member ID"
              match kind with
              | Some(value, None) -> yield $"--kind '{value}' is not a group kind (shared-area, shared-architecture, dependency-chain, shared-files, shared-data-model, shared-api-surface, shared-migration, shared-test-surface, context-affinity, custom:NAME)"
              | _ -> ()
              match origin with
              | Some(value, None) -> yield $"--origin '{value}' is not a group origin (human-declared, architecture-declared, dependency-derived, planner-recommended)"
              | _ -> () ]

        match errors with
        | _ :: _ -> reportArgumentErrors command usage errors
        | [] ->
            let groupId = (single arguments "--group").Value

            let decide (context: GroupContext) =
                WorkGroups.create
                    context
                    { GroupId = groupId
                      Members = all arguments "--member"
                      Kind = kind |> Option.bind snd
                      Origin = origin |> Option.bind snd |> Option.defaultValue GroupOrigin.HumanDeclared
                      SharedContext = all arguments "--shared-context"
                      ExecutionRepository =
                        single arguments "--execution-repository"
                        |> Option.defaultValue (FilePlanningRepository.readRepository root).Name
                      CrossRepository = arguments.Switches.Contains "--cross-repository"
                      ArchitectureNotes = all arguments "--architecture-note"
                      OccurredAt = (single arguments "--occurred-at").Value
                      Actor = actor
                      Reason = single arguments "--reason"
                      ExecutionId = callerExecution root rawArguments }

            let describe (group: StoredWorkGroup) =
                let declaration = group.Declaration
                let members = String.concat ", " declaration.Members
                let repository = declaration.ExecutionRepository |> Option.defaultValue "unknown"
                let scope = if declaration.CrossRepository then " (cross-repository)" else ""

                [ $"work group {declaration.Id} declared with {declaration.Members.Length} member(s): {members}"
                  $"  executes in {repository}{scope}; origin {GroupOrigin.code declaration.Origin}"
                  $"  recorded by {ActorKind.code group.CreatedBy.Kind}:{group.CreatedBy.Id} at {group.CreatedAt}" ]

            mutate root command arguments groupId decide describe

    // ---- membership changes (add, remove) ----

    let private memberValues = [ "--group"; "--member"; "--occurred-at"; "--config"; "--reason" ]

    let private memberErrors (command: string) (arguments: Arguments) =
        [ yield! commonErrors command arguments [ "--config"; "--reason" ]
          yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
          yield! groupErrors command arguments
          yield! occurredAtErrors command arguments
          match all arguments "--member" with
          | [ _ ] -> ()
          | [] -> yield $"{command} requires --member ID"
          | _ -> yield $"{command} changes exactly one member; pass --member once" ]

    let private memberRequest (root: string) (rawArguments: string list) (arguments: Arguments) (actor: Actor) : GroupMemberRequest =
        { GroupId = (single arguments "--group").Value
          WorkItemId = (single arguments "--member").Value
          OccurredAt = (single arguments "--occurred-at").Value
          Actor = actor
          Reason = single arguments "--reason"
          AllowEmpty = arguments.Switches.Contains "--allow-empty"
          ExecutionId = callerExecution root rawArguments }

    let private changeLines (verb: string) (request: GroupMemberRequest) (group: StoredWorkGroup) =
        let members = match group.Declaration.Members with [] -> "(none)" | ids -> String.concat ", " ids

        [ $"{request.WorkItemId} {verb} {group.Declaration.Id} by {ActorKind.code request.Actor.Kind}:{request.Actor.Id} at {request.OccurredAt}"
          $"  members now: {members}" ]

    let add (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group add"
        let arguments = parse memberValues [ "--dry-run" ] rawArguments

        match memberErrors command arguments with
        | _ :: _ as errors -> reportArgumentErrors command addUsage errors
        | [] ->
            let request = memberRequest root rawArguments arguments actor
            mutate root command arguments request.GroupId (fun context -> WorkGroups.add context request) (changeLines "added to" request)

    let remove (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group remove"
        let arguments = parse memberValues [ "--dry-run"; "--allow-empty" ] rawArguments

        match memberErrors command arguments with
        | _ :: _ as errors -> reportArgumentErrors command removeUsage errors
        | [] ->
            let request = memberRequest root rawArguments arguments actor
            mutate root command arguments request.GroupId (fun context -> WorkGroups.remove context request) (changeLines "removed from" request)

    // ---- work group checkpoint ----

    /// Each member's own latest checkpoint that re-verifies as durable, as
    /// the planner reads it from the live context.
    let private memberCheckpoints (root: string) : Result<string -> MemberCheckpointReference option, string> =
        FilePlanningRepository.readLive root
        |> Result.map (fun live ->
            let byId =
                live
                |> List.choose (fun item ->
                    item.Checkpoint
                    |> Option.map (fun checkpoint ->
                        item.Id,
                        { WorkItemId = item.Id
                          CheckpointId = checkpoint.CheckpointId
                          Commit = checkpoint.Commit }))
                |> Map.ofList

            byId.TryFind)

    /// The caller's own execution for each active member, resolved exactly as
    /// `work checkpoint` resolves it; any other member has none.
    let private ownExecution (root: string) (rawArguments: string list) : Result<string -> ExecutionObservation, string> =
        FileCheckpointRepository.readItems root
        |> Result.map (fun items ->
            let overrides = ProvenanceCommands.identityOverridesFrom rawArguments

            let active =
                items |> List.filter (fun item -> item.State = LiveWorkState.Active) |> List.map (fun item -> item.WorkItemId) |> Set.ofList

            fun id ->
                if active.Contains id then FileCheckpointRepository.resolveExecution root id overrides None
                else ExecutionObservation.NoneActive)

    /// The same Git durability rule as `work checkpoint`, observed now.
    let private durableLocation (root: string) =
        let git = ProcessGitDurability.create root
        let policy = FileCheckpointRepository.readPolicy root

        CheckpointObservation.candidate git policy None ExecutionObservation.NoneActive
        |> CheckpointVerification.verifyLocation (FileWorkConfigRepository.readRepositoryId root)

    let checkpoint (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group checkpoint"
        let arguments = parse [ "--group"; "--occurred-at"; "--summary"; "--next-action"; "--decision" ] [ "--dry-run" ] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let dryRun = arguments.Switches.Contains "--dry-run"

        let errors =
            [ yield! commonErrors command arguments [ "--summary"; "--next-action" ]
              yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
              yield! groupErrors command arguments
              yield! occurredAtErrors command arguments
              if (all arguments "--summary").Length <> 1 then
                  yield $"{command} requires one --summary TEXT describing the milestone"
              if (all arguments "--next-action").Length <> 1 then
                  yield $"{command} requires one --next-action TEXT naming the next intended step" ]

        match errors with
        | _ :: _ -> reportArgumentErrors command checkpointUsage errors
        | [] ->
            let groupId = (single arguments "--group").Value
            let occurredAt = (single arguments "--occurred-at").Value
            let location = durableLocation root

            let checkpointId =
                let commit = location |> Result.map (fun git -> git.LocalCommit.Value) |> Result.defaultValue ""
                "gcp-" + CanonicalJson.sha256HexPrefix 24 (String.concat "\u0000" [ groupId; occurredAt; commit; (single arguments "--summary").Value ])

            let request =
                { GroupId = groupId
                  CheckpointId = checkpointId
                  Summary = (single arguments "--summary").Value
                  NextAction = (single arguments "--next-action").Value
                  Decisions = all arguments "--decision"
                  OccurredAt = occurredAt
                  Actor = actor }

            let outcome =
                FileWorkGroupRepository.transact root dryRun (fun groups ->
                    match contextFor root None groups, memberCheckpoints root, ownExecution root rawArguments with
                    | Error message, _, _
                    | _, Error message, _
                    | _, _, Error message -> Error(Choice1Of2 message)
                    | Ok(_, context), Ok references, Ok execution ->
                        WorkGroups.checkpoint context request (MemberFacts.ofStanding context.Standing) execution references location
                        |> Result.mapError Choice2Of2
                        |> Result.map (fun (group, recorded) -> WorkGroups.upsert groups group, (group, recorded)))

            match outcome with
            | Error message
            | Ok(Error(Choice1Of2 message)) -> reportFailure asJson command message
            | Ok(Error(Choice2Of2 rejections)) ->
                if asJson then
                    printJson (envelope command "rejected" [ "groupId", WorkGroupJson.text groupId; "rejections", rejections |> List.map WorkGroupJson.checkpointRejectionNode |> WorkGroupJson.array ])
                else
                    for rejection in rejections do
                        eprintfn "ERROR [%s] %s" (GroupCheckpointRejection.code rejection) (GroupCheckpointRejection.message rejection)

                    eprintfn "group checkpoint refused; nothing was recorded"

                if rejections |> List.forall GroupCheckpointRejection.isArgumentError then 2 else 1
            | Ok(Ok(group, recorded)) ->
                let status = if dryRun then "dry-run" else "recorded"

                if asJson then
                    printJson (envelope command status [ "dryRun", WorkGroupJson.boolean dryRun; "groupId", WorkGroupJson.text groupId; "checkpoint", WorkGroupJson.checkpointNode recorded ])
                else
                    let listed (values: string list) = match values with [] -> "(none)" | values -> String.concat ", " values
                    let git = recorded.Location
                    printfn "durable group checkpoint %s for %s" recorded.CheckpointId group.Declaration.Id
                    printfn "  commit:        %s on %s" git.LocalCommit.Value git.Branch
                    printfn "  verified at:   %s/%s == local HEAD (read from the remote itself)" git.Remote.Name git.RemoteBranch
                    printfn "  completed:     %s" (listed recorded.Completed)
                    printfn "  active:        %s" (listed recorded.Active)
                    printfn "  blocked:       %s" (listed recorded.Blocked)
                    printfn "  remaining:     %s" (listed recorded.Remaining)

                    if not recorded.Abandoned.IsEmpty then
                        printfn "  abandoned:     %s" (listed recorded.Abandoned)

                    recorded.Decisions |> List.iter (printfn "  decision:      %s")

                    recorded.MemberCheckpoints
                    |> List.iter (fun reference -> printfn "  member checkpoint: %s %s @ %s" reference.WorkItemId reference.CheckpointId reference.Commit)

                    printfn "  summary:       %s" recorded.Summary
                    printfn "  next action:   %s" recorded.NextAction
                    printfn "Members' own checkpoints are referenced, not replaced; no paths or executions are claimed."

                    if dryRun then printfn "dry run: nothing was written"
                    else printfn "Praxis state changed in %s; commit and push it." FileWorkGroupRepository.relativePath

                0

    // ---- work group show ----

    let private showText (group: StoredWorkGroup) (summary: GroupSummary) =
        let progress = summary.Progress
        let declaration = group.Declaration
        let kind = declaration.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified kind"
        let repository = declaration.ExecutionRepository |> Option.defaultValue "unknown"
        let scope = if declaration.CrossRepository then "yes" else "no"
        let bullets (values: string list) = match values with [] -> [ "  (none)" ] | values -> values |> List.map (fun value -> $"  - {value}")

        let memberLine (row: MemberProgress) =
            let state = row.State |> Option.defaultValue "not recorded"
            let planning = row.PlanningState |> Option.map (fun value -> $" [planning: {value}]") |> Option.defaultValue ""
            let listed (label: string) (ids: string list) = match ids with [] -> "" | ids -> "; " + label + " " + String.concat ", " ids
            let waits = listed "waits on" row.WaitsOn
            let gates = listed "gates" row.Gates
            $"  {row.WorkItemId,-24} {MemberCategory.code row.Category,-10} recorded {state}{planning}{waits}{gates}"

        let historyLine (entry: GroupHistoryEntry) =
            let memberText = entry.Member |> Option.map (fun id -> $" {id}") |> Option.defaultValue ""
            let reason = entry.Reason |> Option.map (fun text -> $": {text}") |> Option.defaultValue ""
            let empty = if entry.ExplicitEmpty then " (explicitly left the group empty)" else ""
            $"  {entry.At} {GroupOperation.code entry.Operation}{memberText} by {ActorKind.code entry.Actor.Kind}:{entry.Actor.Id}{empty}{reason}"

        [ yield $"WORK GROUP {declaration.Id} ({kind}, {GroupOrigin.code declaration.Origin})"
          yield $"Executes in: {repository} (cross-repository: {scope})"
          yield $"Status:      {GroupStatus.code summary.Status} (derived from the members' own states; no command sets it)"
          yield $"Progress:    {GroupProgress.summary progress}; each member completes on its own evidence (PRX-GRP-042)"
          for removed in summary.RemovedOpen do
              let reason = removed.Reason |> Option.defaultValue "no reason recorded"
              yield $"Removed open: {removed.WorkItemId} at {removed.RemovedAt}: {reason}"
          yield ""
          yield "MEMBERS"
          yield! progress.Members |> List.map memberLine
          match progress.Members |> List.filter (fun row -> row.Category = MemberCategory.Blocked) with
          | [] -> ()
          | blocked ->
              yield ""
              yield "BLOCKED"

              for row in blocked do
                  let gates = match row.Gates with [] -> "gates no other member" | ids -> "gates " + String.concat ", " ids
                  yield $"  {row.WorkItemId}: {gates}"
          yield ""
          yield "SHARED CONTEXT"
          yield! bullets declaration.SharedContext
          yield ""
          yield "ARCHITECTURE NOTES"
          yield! bullets declaration.ArchitectureNotes
          yield ""
          yield "HISTORY"
          yield! group.History |> List.map historyLine
          yield ""
          yield "LATEST GROUP CHECKPOINT"
          match group.Checkpoints |> List.tryLast with
          | None -> yield "  none recorded"
          | Some latest ->
              let listed (values: string list) = match values with [] -> "(none)" | values -> String.concat ", " values
              yield $"  {latest.CheckpointId} at {latest.RecordedAt} on {latest.Location.Branch} @ {latest.Location.LocalCommit.Value}"
              yield $"  completed then: {listed latest.Completed}; remaining then: {listed latest.Remaining}"
              yield! latest.Decisions |> List.map (fun decision -> $"  decision: {decision}")
              yield $"  summary: {latest.Summary}"
              yield $"  next action: {latest.NextAction}" ]

    /// Read-only: reads the store, the queue, the live context and the
    /// planner's analysis; never takes a lock and never writes.
    let show (root: string) (rawArguments: string list) =
        let command = "work group show"
        let arguments = parse [ "--config" ] [] rawArguments
        let asJson = arguments.Switches.Contains "--json"

        let errors =
            [ yield! commonErrors command arguments [ "--config" ]
              match arguments.Positional with
              | [ _ ] -> ()
              | [] -> yield $"{command} requires GROUP-ID"
              | _ -> yield $"{command} shows exactly one group" ]

        match errors with
        | _ :: _ -> reportArgumentErrors command showUsage errors
        | [] ->
            let groupId = arguments.Positional.Head

            let found =
                FileWorkGroupRepository.read root
                |> Result.bind (fun groups ->
                    match WorkGroups.tryFind groups groupId with
                    | None -> Ok None
                    | Some group ->
                        FileWorkGroupFacts.memberFacts root (single arguments "--config")
                        |> Result.map (fun facts -> Some(group, WorkGroups.summarize group facts)))

            match found with
            | Error message ->
                if asJson then printJson (envelope command "failed" [ "failure", WorkGroupJson.record [ "code", WorkGroupJson.text "read-failed"; "message", WorkGroupJson.text message ] ])
                else eprintfn "ERROR %s" message

                1
            | Ok None ->
                let rejection = GroupRejection.UnknownGroup groupId

                if asJson then printJson (envelope command "not-found" [ "groupId", WorkGroupJson.text groupId; "rejections", WorkGroupJson.array [ WorkGroupJson.rejectionNode rejection ] ])
                else eprintfn "ERROR [%s] %s" (GroupRejection.code rejection) (GroupRejection.message rejection)

                1
            | Ok(Some(group, summary)) ->
                if asJson then
                    printJson (
                        envelope
                            command
                            "found"
                            [ "group", WorkGroupJson.groupNode group
                              "groupStatus", WorkGroupJson.text (GroupStatus.code summary.Status)
                              "progress", WorkGroupJson.progressNode summary.Progress
                              "members", summary.Progress.Members |> List.map WorkGroupJson.memberProgressNode |> WorkGroupJson.array
                              "removedOpen", summary.RemovedOpen |> List.map WorkGroupJson.removedOpenNode |> WorkGroupJson.array ]
                    )
                else
                    showText group summary |> List.iter (printfn "%s")

                0

    // ---- validate ----

    /// `validate`'s findings for stored groups: an unreadable store, a
    /// malformed record, or a stored group that breaks a group invariant.
    let validationFindings (root: string) : (string * string * string) list =
        let path = FileWorkGroupRepository.relativePath

        match FileWorkGroupRepository.parse root with
        | None -> []
        | Some(Error message) -> [ path, "groups", message ]
        | Some(Ok read) ->
            let malformed =
                read.Groups
                |> List.collect (fun (index, id, result) ->
                    match result with
                    | Ok _ -> []
                    | Error problems ->
                        let name = id |> Option.defaultValue $"groups[{index}]"
                        problems |> List.map (fun problem -> path, name, problem))

            let groups = read.Groups |> List.choose (fun (_, _, result) -> match result with Ok group -> Some group | Error _ -> None)

            let invariants =
                match contextFor root None groups with
                | Error message -> [ path, "groups", message ]
                | Ok(_, context) -> WorkGroups.findings context |> List.map (fun (id, field, message) -> path, (if id = "" then field else $"{id}.{field}"), message)

            let checkpoints =
                let owners =
                    FileCheckpointRepository.readEvents root
                    |> List.map (fun (_, event) -> event.EventId, event.WorkItemId)
                    |> Map.ofList

                WorkGroups.checkpointFindings owners.TryFind groups
                |> List.map (fun (id, field, message) -> path, $"{id}.{field}", message)

            let history =
                FileWorkGroupFacts.committed root
                |> List.collect (fun (revision, committed) -> WorkGroups.historyFindings revision committed groups)
                |> List.distinctBy (fun (id, field, _) -> id, field)
                |> List.map (fun (id, field, message) -> path, $"{id}.{field}", message)

            malformed @ invariants @ checkpoints @ history
