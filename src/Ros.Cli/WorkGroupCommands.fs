namespace Ros.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Contracts.Work
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Planning
open Ros.Infrastructure.Work

/// `work group create|show|add|remove|checkpoint` (PRX-GRP-073, phase two).
/// This module parses, composes the file adapters with the pure
/// `WorkGroups` decisions, and renders. It decides nothing itself, and no
/// command here writes anything but `.ros/work/groups.json` (analysis D11).
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group show GROUP-ID [--config FILE] [--json] | work group create --id GROUP-ID --member ID [--member ID ...] --occurred-at TIMESTAMP [--kind KIND] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT ...] [--architecture-note TEXT ...] [--reason TEXT] [--config FILE] [--dry-run] [--json] [IDENTITY]"

    // ---- arguments ----

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    type private Arguments =
        { Values: (string * string) list
          Switches: string list
          Positional: string list
          Unexpected: string list }

    /// Splits a command line into `--flag value` pairs (only for `valued`
    /// flags and identity flags), known switches, positionals and anything
    /// unexpected.
    let rec private split (valued: Set<string>) (switches: Set<string>) (arguments: string list) : Arguments =
        let next = split valued switches

        match arguments with
        | [] -> { Values = []; Switches = []; Positional = []; Unexpected = [] }
        | flag :: value :: rest when (valued.Contains flag || List.contains flag identityFlags) && not (value.StartsWith "--") ->
            let parsed = next rest
            { parsed with Values = (flag, value) :: parsed.Values }
        | flag :: rest when switches.Contains flag ->
            let parsed = next rest
            { parsed with Switches = flag :: parsed.Switches }
        | token :: rest when token.StartsWith "--" ->
            let parsed = next rest
            { parsed with Unexpected = token :: parsed.Unexpected }
        | token :: rest ->
            let parsed = next rest
            { parsed with Positional = token :: parsed.Positional }

    let private values (name: string) (arguments: Arguments) =
        arguments.Values |> List.filter (fst >> (=) name) |> List.map snd

    let private single (name: string) (arguments: Arguments) =
        match values name arguments with
        | [ value ] -> Some value
        | _ -> None

    let private has (name: string) (arguments: Arguments) = List.contains name arguments.Switches

    let private isTimestamp (value: string) =
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) |> fst

    /// Problems common to every mutating verb: exactly one group ID and one
    /// real timestamp, no stray tokens, at most one of each single flag.
    let private commonErrors (verb: string) (singles: string list) (arguments: Arguments) =
        [ match values "--id" arguments with
          | [ _ ] -> ()
          | [] -> yield $"work group {verb} requires --id GROUP-ID"
          | _ -> yield $"work group {verb} names exactly one group; pass --id once"
          match values "--occurred-at" arguments with
          | [ value ] when isTimestamp value -> ()
          | [ value ] -> yield $"--occurred-at '{value}' is not a timestamp"
          | _ -> yield $"work group {verb} requires exactly one --occurred-at TIMESTAMP (the real current time)"
          for name in singles do
              if (values name arguments).Length > 1 then
                  yield $"{name} may be given only once"
          for token in arguments.Unexpected @ arguments.Positional do
              yield $"unexpected argument '{token}'" ]

    let private usageFailure (errors: string list) (usageText: string) =
        errors |> List.iter (eprintfn "ERROR %s")
        eprintfn "Usage: ros %s" usageText
        2

    // ---- facts shared by every verb ----

    let private resolve (root: string) (path: string) =
        if Path.IsPathRooted path then path else Path.GetFullPath(Path.Combine(root, path))

    /// The planner configuration named by `--config`, if any.
    let private configuration (root: string) (arguments: Arguments) : Result<PlannerConfiguration option, string> =
        match single "--config" arguments with
        | None -> Ok None
        | Some path ->
            let file = resolve root path

            if File.Exists file then
                PlanningJson.parseConfiguration (File.ReadAllText file) |> Result.map Some
            else
                Error $"{file} does not exist"

    /// Every known work item's facts (analysis §8: read once per command).
    let private facts (root: string) (configuration: PlannerConfiguration option) : Result<Map<string, GroupMemberFacts>, string> =
        FilePlanningRepository.readQueue root
        |> Result.bind (fun queue ->
            FilePlanningRepository.readLive root
            |> Result.map (fun live ->
                WorkGroups.memberFacts
                    (FilePlanningRepository.readRepository root).Name
                    (configuration |> Option.map (fun value -> value.Grouping.ExecutionRepositories) |> Option.defaultValue [])
                    (queue |> List.map (fun item -> item.Id, item.Status))
                    (live |> List.map (fun item -> item.Id, item.State))))

    // ---- rendering ----

    let private yesNo value = if value then "yes" else "no"

    let private print (document: JsonObject) = printf "%s" (document.ToJsonString WorkGroupJson.options)

    let private failed (asJson: bool) (verb: string) (groupId: string) (message: string) =
        if asJson then
            let document = WorkGroupJson.envelope verb groupId "failed"
            document["failure"] <- WorkGroupJson.failureNode message
            print document
        else
            eprintfn "ERROR [persistence-failed] %s" message

        1

    let private rejected (asJson: bool) (verb: string) (groupId: string) (rejections: GroupRejection list) =
        if asJson then
            let document = WorkGroupJson.envelope verb groupId "rejected"
            document["rejections"] <- rejections |> Seq.map (fun rejection -> WorkGroupJson.groupRejectionNode rejection :> JsonNode) |> Seq.fold (fun (array: JsonArray) node -> array.Add node; array) (JsonArray())
            print document
        else
            for rejection in rejections do
                eprintfn "ERROR [%s] %s" (GroupRejection.code rejection) (GroupRejection.message rejection)
                eprintfn "  REMEDY %s" (GroupRejection.remedy rejection)

            eprintfn "work group %s rejected; nothing was recorded" verb

        if rejections |> List.forall GroupRejection.isArgumentError then 2 else 1

    let private groupLines (group: StoredGroup) =
        let members = String.concat ", " group.Members
        let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified"

        [ $"  members:              {members}"
          $"  kind:                 {kind}"
          $"  origin:               {GroupOrigin.code group.Origin}"
          $"  execution repository: {group.ExecutionRepository} (cross-repository: {yesNo group.CrossRepository})" ]
        @ (group.SharedContext |> List.map (sprintf "  shared context:       %s"))
        @ (group.ArchitectureNotes |> List.map (sprintf "  architecture note:    %s"))

    let private statePersisted (dryRun: bool) =
        if dryRun then
            "dry run: nothing was written."
        else
            "No member's lifecycle state, evidence or attribution changed. Praxis state changed in .ros/work/groups.json; commit and push it."

    // ---- work group create ----

    let private createValued =
        set [ "--id"; "--member"; "--occurred-at"; "--kind"; "--execution-repository"; "--shared-context"; "--architecture-note"; "--reason"; "--config" ]

    let private createSwitches = set [ "--cross-repository"; "--dry-run"; "--json" ]

    let private create (root: string) (arguments: string list) (actor: Actor) =
        let parsed = split createValued createSwitches arguments

        let errors =
            commonErrors "create" [ "--kind"; "--execution-repository"; "--reason"; "--config" ] parsed
            @ [ match single "--kind" parsed with
                | Some kind when (GroupKind.tryParse kind).IsNone ->
                    yield $"--kind '{kind}' is not a group kind (shared-area, shared-architecture, dependency-chain, shared-files, shared-data-model, shared-api-surface, shared-migration, shared-test-surface, context-affinity, custom:NAME)"
                | _ -> () ]

        match errors with
        | _ :: _ -> usageFailure errors usage
        | [] ->
            let groupId = (single "--id" parsed).Value
            let asJson = has "--json" parsed
            let dryRun = has "--dry-run" parsed

            let decided =
                configuration root parsed
                |> Result.bind (fun config -> facts root config)
                |> Result.bind (fun known ->
                    let declaration =
                        { Id = groupId
                          Kind = single "--kind" parsed |> Option.bind GroupKind.tryParse
                          ExecutionRepository = single "--execution-repository" parsed |> Option.defaultValue (FilePlanningRepository.readRepository root).Name
                          CrossRepository = has "--cross-repository" parsed
                          SharedContext = values "--shared-context" parsed
                          ArchitectureNotes = values "--architecture-note" parsed
                          Members = values "--member" parsed
                          OccurredAt = (single "--occurred-at" parsed).Value
                          Actor = actor
                          Reason = single "--reason" parsed }

                    FileWorkGroupRepository.mutate root dryRun (fun stored -> WorkGroups.create stored known declaration))

            match decided with
            | Error message -> failed asJson "create" groupId message
            | Ok(Error rejections) -> rejected asJson "create" groupId rejections
            | Ok(Ok group) ->
                if asJson then
                    let document = WorkGroupJson.envelope "create" groupId (if dryRun then "dry-run" else "created")
                    document["dryRun"] <- JsonValue.Create dryRun
                    document["group"] <- WorkGroupJson.groupNode group
                    print document
                else
                    printfn "%s group %s (%d member(s))" (if dryRun then "would create" else "created") group.Id group.Members.Length
                    groupLines group |> List.iter (printfn "%s")
                    printfn "  recorded by:          %s:%s" (ActorKind.code actor.Kind) actor.Id
                    printfn "%s" (statePersisted dryRun)

                0

    // ---- work group show ----

    let showUsage = "work group show GROUP-ID [--config FILE] [--json]"

    /// Read-only (B8): the store is read, the planner's read-only port is
    /// queried, and nothing is written.
    let private show (root: string) (version: string) (arguments: string list) =
        let parsed = split (set [ "--config" ]) (set [ "--json" ]) arguments

        let errors =
            [ match parsed.Positional with
              | [ _ ] -> ()
              | [] -> yield "work group show requires one GROUP-ID"
              | _ -> yield "work group show names exactly one group"
              if (values "--config" parsed).Length > 1 then
                  yield "--config may be given only once"
              for token in parsed.Unexpected do
                  yield $"unexpected argument '{token}'" ]

        match errors with
        | _ :: _ -> usageFailure errors showUsage
        | [] ->
            let groupId = List.head parsed.Positional
            let asJson = has "--json" parsed
            let plannedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            let port = FilePlanningRepository.create root None (single "--config" parsed |> Option.map (resolve root))

            let outcome =
                FileWorkGroupRepository.read root
                |> Result.bind (fun stored ->
                    match WorkGroups.tryFind groupId stored with
                    | None -> Ok(Error [ GroupRejection.UnknownGroup groupId ])
                    | Some group ->
                        configuration root parsed
                        |> Result.bind (facts root)
                        |> Result.bind (fun known ->
                            PlanningOperations.analyze port plannedAt version
                            |> Result.map (fun (input, analysis) ->
                                let planned = (Grouping.recommend input analysis).Groups |> List.tryFind (fun candidate -> WorkGroupId.value candidate.Id = groupId)
                                Ok(WorkGroups.view known planned group))))

            match outcome with
            | Error message -> failed asJson "show" groupId message
            | Ok(Error rejections) -> rejected asJson "show" groupId rejections
            | Ok(Ok view) ->
                if asJson then
                    print (WorkGroupJson.viewInto (WorkGroupJson.envelope "show" groupId "shown") view)
                else
                    let group = view.Group
                    let origin = GroupOrigin.code group.Origin
                    printfn "GROUP %s (%s)" group.Id origin
                    groupLines group |> List.skip 1 |> List.iter (printfn "%s")
                    printfn "  created:              %s by %s:%s" group.CreatedAt (ActorKind.code group.CreatedBy.Kind) group.CreatedBy.Id
                    printfn "Progress: %s" view.Progress.Statement
                    printfn "Members:"

                    for row in view.Members do
                        let gatedBy = if row.GatedBy.IsEmpty then "" else $"""; gated by {String.concat ", " row.GatedBy}"""
                        let gates = if row.Gates.IsEmpty then "" else $"""; gates {String.concat ", " row.Gates}"""
                        printfn "  %s  recorded: %s; planning: %s; status: %s%s%s" row.WorkItemId row.RecordedState row.PlanningState row.Status gatedBy gates

                    printfn "Read-only: nothing was written."

                0

    // ---- dispatch ----

    let run (root: string) (version: string) (arguments: string list) =
        match arguments with
        | "create" :: rest -> ProvenanceCommands.withResolvedActor rest (create root rest)
        | "show" :: rest -> show root version rest
        | _ ->
            eprintfn "ERROR unknown work group command"
            eprintfn "Usage: ros %s" usage
            2
