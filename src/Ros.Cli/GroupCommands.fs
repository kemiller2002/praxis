namespace Ros.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Planning

/// `praxis work group ...`: durable, human-declared execution groups
/// (requirements/PLANNING-WORK-GROUPS.md phase two, PRX-GRP-073). This module
/// parses, gathers the stored groups and work items, asks
/// `Ros.Domain.Planning.WorkGroups` to decide, and writes only
/// `.ros/work/groups.json` under the `work-protocol` lock. No group command
/// writes a member's backlog row, live record, events or telemetry.
[<RequireQualifiedAccess>]
module GroupCommands =
    let usage =
        "work group show GROUP-ID [--json] [--config FILE] | work group create --id GROUP-ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--origin ORIGIN] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT]* [--architecture-note TEXT]* [--reason TEXT] [--config FILE] [--dry-run] [--json] [IDENTITY]"

    // ---- arguments ----

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    let private optionValues (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (flag, value) -> if flag = name && not (value.StartsWith "--") then Some value else None)

    let private optionValue (name: string) (arguments: string list) = optionValues name arguments |> List.tryHead

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    /// Every token that is neither a known `--flag VALUE` pair nor a known
    /// switch.
    let rec private unexpected (valued: Set<string>) (switches: Set<string>) (arguments: string list) =
        match arguments with
        | [] -> []
        | flag :: value :: rest when valued.Contains flag && not (value.StartsWith "--") -> unexpected valued switches rest
        | flag :: rest when valued.Contains flag -> $"{flag} requires a value" :: unexpected valued switches rest
        | flag :: rest when switches.Contains flag -> unexpected valued switches rest
        | token :: rest -> $"unexpected argument '{token}'" :: unexpected valued switches rest

    let private commonErrors (command: string) (valued: string list) (switches: string list) (arguments: string list) =
        [ match optionValues "--id" arguments with
          | [ _ ] -> ()
          | [] -> yield $"{command} requires --id GROUP-ID"
          | _ -> yield $"{command} names exactly one group; pass --id once"
          match optionValues "--occurred-at" arguments with
          | [ value ] when isTimestamp value -> ()
          | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
          | _ -> yield $"{command} requires exactly one --occurred-at TIMESTAMP (the real current time)"
          yield! unexpected (Set.ofList ([ "--id"; "--occurred-at" ] @ valued @ identityFlags)) (Set.ofList ([ "--dry-run"; "--json" ] @ switches)) arguments ]

    let private reportUsage (errors: string list) =
        errors |> List.iter (eprintfn "ERROR %s")
        eprintfn "Usage: ros %s" usage
        2

    // ---- gathering ----

    let private resolve (root: string) (file: string) = if Path.IsPathRooted file then file else Path.Combine(root, file)

    /// The planner configuration the caller named, if any: it supplies
    /// declared group IDs and where items execute.
    let private readConfiguration (root: string) (arguments: string list) : Result<PlannerConfiguration, string> =
        match optionValue "--config" arguments |> Option.map (resolve root) with
        | None -> Ok PlannerConfiguration.defaults
        | Some file when not (File.Exists file) -> Error $"{file} does not exist"
        | Some file -> PlanningJson.parseConfiguration (File.ReadAllText file)

    /// Everything a group decision reads.
    type Context =
        { Stored: StoredWorkGroup list
          Catalog: WorkCatalog
          Configuration: PlannerConfiguration }

    let private gather (root: string) (arguments: string list) : Result<Context, string> =
        readConfiguration root arguments
        |> Result.bind (fun configuration ->
            FileWorkGroupRepository.read root
            |> Result.bind (fun stored ->
                FileWorkGroupRepository.catalog root configuration.Grouping.ExecutionRepositories
                |> Result.map (fun catalog ->
                    { Stored = stored
                      Catalog = catalog
                      Configuration = configuration })))

    /// The stored groups with one group added or replaced.
    let replace (groups: StoredWorkGroup list) (changed: StoredWorkGroup) =
        (groups |> List.filter (fun group -> group.Id <> changed.Id)) @ [ changed ]

    // ---- rendering ----

    let private memberLines (group: StoredWorkGroup) =
        group.Members
        |> List.map (fun entry ->
            let reason = entry.Reason |> Option.map (fun text -> $" ({text})") |> Option.defaultValue ""
            $"  {entry.WorkItemId} in {entry.ExecutionRepository}, added {entry.AddedAt} by {Actor.describe entry.AddedBy}{reason}")

    let private document (command: string) (groupId: string) (status: string) (fields: (string * JsonNode) list) =
        WorkGroupJson.record
            ([ "command", WorkGroupJson.text command
               "schemaVersion", WorkGroupJson.integer 1
               "groupId", WorkGroupJson.text groupId
               "status", WorkGroupJson.text status ]
             @ fields)

    // ---- the shared mutation pipeline ----

    /// What a successful decision produced: the changed group, the full set
    /// of groups to store, and the lines describing the change.
    type Decided =
        { Group: StoredWorkGroup
          Groups: StoredWorkGroup list
          Lines: string list
          Fields: (string * JsonNode) list }

    let private withLock (root: string) (run: unit -> Result<'a, string>) : Result<'a, string> =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    run ()
                with error ->
                    Error $"state persistence failed: {error.Message}"

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

    /// Gathers, decides, and (unless `--dry-run`) stores the result, all
    /// under the `work-protocol` lock. A refusal records nothing.
    let mutate (root: string) (command: string) (arguments: string list) (decide: Context -> Result<Decided, GroupRejection list>) =
        let groupId = (optionValue "--id" arguments).Value
        let asJson = List.contains "--json" arguments
        let dryRun = List.contains "--dry-run" arguments

        let outcome =
            let run () =
                gather root arguments
                |> Result.bind (fun context ->
                    match decide context with
                    | Error rejections -> Ok(Error rejections)
                    | Ok decided when dryRun -> Ok(Ok decided)
                    | Ok decided -> FileWorkGroupRepository.write root decided.Groups |> Result.map (fun () -> Ok decided))

            if dryRun then run () else withLock root run

        match outcome with
        | Error message ->
            if asJson then
                printf "%s" (WorkGroupJson.render (document command groupId "failed" [ "failure", WorkGroupJson.record [ "code", WorkGroupJson.text "persistence-failed"; "message", WorkGroupJson.text message ] ]))
            else
                eprintfn "ERROR [persistence-failed] %s" message

            1
        | Ok(Error rejections) ->
            if asJson then
                printf "%s" (WorkGroupJson.render (document command groupId "rejected" [ "rejections", WorkGroupJson.rejections rejections ]))
            else
                rejections |> List.iter (fun rejection -> eprintfn "ERROR [%s] %s" (GroupRejection.code rejection) (GroupRejection.message rejection))
                eprintfn "%s rejected; nothing was recorded" command

            if rejections |> List.forall GroupRejection.isArgumentError then 2 else 1
        | Ok(Ok decided) ->
            let status = if dryRun then "dry-run" else "recorded"

            if asJson then
                printf "%s" (WorkGroupJson.render (document command groupId status ([ "group", WorkGroupJson.group decided.Group ] @ decided.Fields)))
            else
                decided.Lines |> List.iter (printfn "%s")

                if dryRun then
                    printfn "dry run: nothing was recorded"
                else
                    printfn "Praxis state changed in %s; commit and push it so another executor can see the group." FileWorkGroupRepository.relativePath

            0

    // ---- work group create ----

    let private parseWith (what: string) (parse: string -> 'a option) (value: string) =
        match parse value with
        | Some parsed -> Ok parsed
        | None -> Error $"unknown {what} '{value}'"

    let private createErrors (arguments: string list) =
        commonErrors
            "work group create"
            [ "--member"; "--kind"; "--origin"; "--execution-repository"; "--shared-context"; "--architecture-note"; "--reason"; "--config" ]
            [ "--cross-repository" ]
            arguments
        @ [ match optionValue "--kind" arguments |> Option.map (parseWith "group kind" GroupKind.tryParse) with
            | Some(Error message) -> yield message
            | _ -> ()
            match optionValue "--origin" arguments |> Option.map (parseWith "group origin" GroupOrigin.tryParse) with
            | Some(Error message) -> yield message
            | _ -> () ]

    let create (root: string) (arguments: string list) (actor: Actor) =
        match createErrors arguments with
        | _ :: _ as errors -> reportUsage errors
        | [] ->
            let request =
                { Id = (optionValue "--id" arguments).Value
                  Members = optionValues "--member" arguments
                  Kind = optionValue "--kind" arguments |> Option.bind GroupKind.tryParse
                  Origin = optionValue "--origin" arguments |> Option.bind GroupOrigin.tryParse |> Option.defaultValue GroupOrigin.HumanDeclared
                  ExecutionRepository = optionValue "--execution-repository" arguments
                  CrossRepository = List.contains "--cross-repository" arguments
                  SharedContext = optionValues "--shared-context" arguments
                  ArchitectureNotes = optionValues "--architecture-note" arguments
                  OccurredAt = (optionValue "--occurred-at" arguments).Value
                  Actor = actor
                  Reason = optionValue "--reason" arguments }

            mutate root "work group create" arguments (fun context ->
                let declaredIds =
                    (context.Stored |> List.map (fun group -> group.Id))
                    @ (context.Configuration.Grouping.Groups |> List.map (fun group -> group.Id))

                WorkGroups.create context.Catalog declaredIds request
                |> Result.map (fun group ->
                    let scope = if group.CrossRepository then " (cross-repository)" else ""

                    { Group = group
                      Groups = context.Stored @ [ group ]
                      Lines =
                        [ $"group {group.Id} declared ({GroupOrigin.code group.Origin}) with {group.Members.Length} member(s), executing in {group.ExecutionRepository}{scope}" ]
                        @ memberLines group
                        @ [ "Members' lifecycle states, evidence and attribution are unchanged." ]
                      Fields = [] }))

    // ---- work group show ----

    let private showLines (view: GroupView) =
        let group = view.Group
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"
        let scope = if group.CrossRepository then " (cross-repository)" else ""
        let state (value: RecordedWorkState option) = value |> Option.map RecordedWorkState.code |> Option.defaultValue "no longer tracked"
        let planning (value: PlanningWorkState option) = value |> Option.map PlanningWorkState.code |> Option.defaultValue "unknown"

        let memberLine (entry: GroupMemberView) =
            let checkpoint = entry.LatestCheckpointId |> Option.map (fun id -> $"; latest checkpoint {id}") |> Option.defaultValue ""
            $"  {entry.Membership.WorkItemId}  recorded {state entry.RecordedState}; planning {planning entry.PlanningState}{checkpoint}"

        let blockedLines =
            match GroupView.blocked view with
            | [] -> [ "  none" ]
            | blocked ->
                blocked
                |> List.map (fun entry ->
                    let reason = entry.BlockReason |> Option.map (fun text -> $" ({text})") |> Option.defaultValue ""
                    let gates = if entry.Gates.IsEmpty then "gates nothing open" else $"""gates {String.concat ", " entry.Gates}"""
                    $"  {entry.Membership.WorkItemId}{reason}: {gates}")

        let listed (title: string) (values: string list) =
            match values with
            | [] -> [ $"{title}: none" ]
            | values -> $"{title}:" :: (values |> List.map (fun value -> $"  - {value}"))

        let checkpointLines =
            match view.LatestCheckpoint with
            | None -> [ "Latest group checkpoint: none" ]
            | Some checkpoint ->
                [ $"Latest group checkpoint: {checkpoint.Id} at {checkpoint.Location.LocalCommit.Value} on {checkpoint.Location.Branch} ({checkpoint.RecordedAt})"
                  $"  completed: {checkpoint.Summary}"
                  $"  next action: {checkpoint.NextAction}" ]

        [ $"GROUP {group.Id} ({kind}, {GroupOrigin.code group.Origin})"
          $"Execution repository: {group.ExecutionRepository}{scope}"
          $"Declared {group.CreatedAt} by {Actor.describe group.CreatedBy}"
          $"Progress: {GroupView.describeProgress view.Progress} (each member completes on its own evidence; the group never implies every member succeeded)"
          "Members:" ]
        @ (view.Members |> List.map memberLine)
        @ [ if not view.PlanningAvailable then "  (planning states unknown: the planner could not read this repository)"
            "Blocked members:" ]
        @ blockedLines
        @ listed "Shared context" group.SharedContext
        @ listed "Architecture notes" group.ArchitectureNotes
        @ checkpointLines

    /// `work group show GROUP-ID`: read-only; it never writes.
    let show (root: string) (arguments: string list) =
        let configFile = optionValue "--config" arguments

        match arguments with
        | [] -> reportUsage [ "work group show requires a GROUP-ID" ]
        | groupId :: _ when groupId.StartsWith "--" -> reportUsage [ "work group show requires a GROUP-ID first" ]
        | groupId :: rest ->
            match unexpected (Set.ofList [ "--config" ]) (Set.ofList [ "--json" ]) rest with
            | _ :: _ as errors -> reportUsage errors
            | [] ->
            match gather root arguments with
            | Error message ->
                eprintfn "ERROR %s" message
                1
            | Ok context ->
                match WorkGroups.tryFind context.Stored groupId with
                | None ->
                    eprintfn "ERROR %s" (GroupRejection.message (GroupRejection.GroupNotFound groupId))
                    1
                | Some group ->
                    let analysis =
                        let port = FilePlanningRepository.create root None (configFile |> Option.map (resolve root))

                        match Ros.Application.Planning.PlanningOperations.analyze port (DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")) Lifecycle.Version with
                        | Ok(_, value) -> Some value
                        | Error _ -> None

                    let view = GroupView.build context.Catalog analysis group

                    if List.contains "--json" arguments then
                        printf "%s" (WorkGroupJson.render (WorkGroupJson.view view))
                    else
                        showLines view |> List.iter (printfn "%s")

                    0

    let run (root: string) (arguments: string list) =
        match arguments with
        | "create" :: rest -> ProvenanceCommands.withResolvedActor rest (create root rest)
        | "show" :: rest -> show root rest
        | _ -> reportUsage [ "unknown work group command" ]
