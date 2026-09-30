namespace Ros.Cli

open System
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create` records a human-declared execution group in Praxis
/// state; `work group add` adds one member to it and `work group remove`
/// removes one (PRX-GRP-073, phase two).
/// This module parses, delegates to
/// `FileWorkGroupRepository` (Application operation over the Domain
/// decision) and renders; it holds no grouping policy.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let private createUsage =
        "work group create --id GROUP-ID --member ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT]* [--dry-run] [--json] [IDENTITY]"

    let private addUsage =
        "work group add --id GROUP-ID --member ID --occurred-at TIMESTAMP [--config FILE] [--dry-run] [--json] [IDENTITY]"

    let private removeUsage =
        "work group remove --id GROUP-ID --member ID --occurred-at TIMESTAMP [--dry-run] [--json] [IDENTITY]"

    let usage = String.Join(" | ", [ createUsage; addUsage; removeUsage ])

    let private flagsWithValues =
        set
            [ "--id"
              "--member"
              "--kind"
              "--execution-repository"
              "--shared-context"
              "--occurred-at"
              "--config"
              "--actor-kind"
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

    let private switches = set [ "--dry-run"; "--json"; "--cross-repository" ]

    type private Parsed =
        { Values: Map<string, string list>
          Unexpected: string list }

    let rec private parse (parsed: Parsed) (arguments: string list) =
        match arguments with
        | [] -> parsed
        | flag :: value :: rest when flagsWithValues.Contains flag && not (value.StartsWith "--") ->
            let previous = parsed.Values.TryFind flag |> Option.defaultValue []
            parse { parsed with Values = parsed.Values.Add(flag, previous @ [ value ]) } rest
        | switch :: rest when switches.Contains switch -> parse parsed rest
        | token :: rest -> parse { parsed with Unexpected = parsed.Unexpected @ [ token ] } rest

    let private values (parsed: Parsed) flag = parsed.Values.TryFind flag |> Option.defaultValue []

    let private single (parsed: Parsed) flag =
        match values parsed flag with
        | [ value ] -> Ok(Some value)
        | [] -> Ok None
        | _ -> Error $"{flag} may be given once"

    let private createArgumentErrors (parsed: Parsed) =
        [ match values parsed "--id" with
          | [ _ ] -> ()
          | [] -> yield "work group create requires --id GROUP-ID"
          | _ -> yield "work group create records one group; pass --id once"
          match values parsed "--occurred-at" with
          | [ _ ] -> ()
          | _ -> yield "work group create requires exactly one --occurred-at TIMESTAMP (the real current time)"
          if (values parsed "--member").IsEmpty then
              yield "work group create requires --member ID (at least two)"
          for flag in [ "--kind"; "--execution-repository" ] do
              match single parsed flag with
              | Error message -> yield message
              | Ok _ -> ()
          match values parsed "--kind" with
          | [ kind ] when (GroupKind.tryParse kind).IsNone ->
              yield $"--kind '{kind}' is not a group kind (shared-area, shared-architecture, dependency-chain, shared-files, shared-data-model, shared-api-surface, shared-migration, shared-test-surface, context-affinity, custom:NAME)"
          | _ -> ()
          if not (values parsed "--config").IsEmpty then
              yield "--config applies to work group add, not work group create"
          for token in parsed.Unexpected do
              yield $"unexpected argument '{token}'" ]

    let private addArgumentErrors (parsed: Parsed) (arguments: string list) =
        [ match values parsed "--id" with
          | [ _ ] -> ()
          | [] -> yield "work group add requires --id GROUP-ID"
          | _ -> yield "work group add changes one group; pass --id once"
          match values parsed "--member" with
          | [ _ ] -> ()
          | [] -> yield "work group add requires --member ID"
          | _ -> yield "work group add adds one member; pass --member once"
          match values parsed "--occurred-at" with
          | [ _ ] -> ()
          | _ -> yield "work group add requires exactly one --occurred-at TIMESTAMP (the real current time)"
          match single parsed "--config" with
          | Error message -> yield message
          | Ok _ -> ()
          for flag in [ "--kind"; "--execution-repository"; "--shared-context" ] do
              if not (values parsed flag).IsEmpty then
                  yield $"{flag} is a work group create option; work group add changes only membership"
          if List.contains "--cross-repository" arguments then
              yield "--cross-repository is a work group create option; work group add changes only membership"
          for token in parsed.Unexpected do
              yield $"unexpected argument '{token}'" ]

    let private removeArgumentErrors (parsed: Parsed) (arguments: string list) =
        [ match values parsed "--id" with
          | [ _ ] -> ()
          | [] -> yield "work group remove requires --id GROUP-ID"
          | _ -> yield "work group remove changes one group; pass --id once"
          match values parsed "--member" with
          | [ _ ] -> ()
          | [] -> yield "work group remove requires --member ID"
          | _ -> yield "work group remove removes one member; pass --member once"
          match values parsed "--occurred-at" with
          | [ _ ] -> ()
          | _ -> yield "work group remove requires exactly one --occurred-at TIMESTAMP (the real current time)"
          if not (values parsed "--config").IsEmpty then
              yield "--config applies to work group add, not work group remove"
          for flag in [ "--kind"; "--execution-repository"; "--shared-context" ] do
              if not (values parsed flag).IsEmpty then
                  yield $"{flag} is a work group create option; work group remove changes only membership"
          if List.contains "--cross-repository" arguments then
              yield "--cross-repository is a work group create option; work group remove changes only membership"
          for token in parsed.Unexpected do
              yield $"unexpected argument '{token}'" ]

    let private request (parsed: Parsed) (arguments: string list) (actor: Actor) : GroupCreationRequest =
        { Id = values parsed "--id" |> List.head
          Members = values parsed "--member"
          Kind = values parsed "--kind" |> List.tryHead |> Option.bind GroupKind.tryParse
          SharedContext = values parsed "--shared-context"
          ExecutionRepository = values parsed "--execution-repository" |> List.tryHead
          CrossRepository = List.contains "--cross-repository" arguments
          OccurredAt = values parsed "--occurred-at" |> List.head
          CreatedBy = actor.Id }

    let private describe (group: StoredGroup) =
        let declaration = group.Declaration
        let joined (values: string list) = String.Join(", ", values)
        printfn "  members: %s" (joined declaration.Members)
        printfn "  kind: %s" (declaration.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified (the planner infers it)")
        printfn "  origin: %s" (GroupOrigin.code declaration.Origin)
        printfn "  execution repository: %s%s" (declaration.ExecutionRepository |> Option.defaultValue "unspecified") (if declaration.CrossRepository then " (cross-repository)" else "")

        for context in declaration.SharedContext do
            printfn "  shared context: %s" context

        printfn "  declared by %s at %s" group.CreatedBy group.CreatedAt

        for addition in group.Additions do
            printfn "  %s added by %s at %s" addition.Member addition.AddedBy addition.AddedAt

        for removal in group.Removals do
            printfn "  %s removed by %s at %s" removal.Member removal.RemovedBy removal.RemovedAt

        printfn "member lifecycle states are unchanged; each member is still started, checkpointed and completed on its own"

    let private render asJson (outcome: GroupCreationOutcome) =
        match outcome with
        | GroupCreationOutcome.Rejected rejections ->
            let messages = rejections |> List.map GroupDeclaration.message

            if asJson then
                printf "%s" (PlanningJson.renderGroupRejected messages)

            for message in messages do
                eprintfn "ERROR %s" message

            eprintfn "work group create rejected; nothing was recorded"
            1
        | GroupCreationOutcome.Planned group ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCreated true FileWorkGroupStore.relativePath group)
            else
                printfn "dry run: would record %s in %s; nothing was recorded" group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0
        | GroupCreationOutcome.Recorded group ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCreated false FileWorkGroupStore.relativePath group)
            else
                printfn "recorded %s in %s" group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0

    let private additionRequest (parsed: Parsed) (actor: Actor) : MemberAdditionRequest =
        { GroupId = values parsed "--id" |> List.head
          Member = values parsed "--member" |> List.head
          OccurredAt = values parsed "--occurred-at" |> List.head
          AddedBy = actor.Id }

    let private renderAddition asJson (outcome: MemberAdditionOutcome) =
        let added (group: StoredGroup) = List.last group.Additions

        match outcome with
        | MemberAdditionOutcome.Rejected rejections ->
            let messages = rejections |> List.map GroupDeclaration.additionMessage

            if asJson then
                printf "%s" (PlanningJson.renderGroupRejected messages)

            for message in messages do
                eprintfn "ERROR %s" message

            eprintfn "work group add rejected; nothing was recorded"
            1
        | MemberAdditionOutcome.Planned group ->
            if asJson then
                printf "%s" (PlanningJson.renderMemberAdded true FileWorkGroupStore.relativePath (added group) group)
            else
                printfn "dry run: would add %s to %s in %s; nothing was recorded" (added group).Member group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0
        | MemberAdditionOutcome.Recorded group ->
            let addition = added group

            if asJson then
                printf "%s" (PlanningJson.renderMemberAdded false FileWorkGroupStore.relativePath addition group)
            else
                printfn "added %s to %s in %s" addition.Member group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0

    let private removalRequest (parsed: Parsed) (actor: Actor) : MemberRemovalRequest =
        { GroupId = values parsed "--id" |> List.head
          Member = values parsed "--member" |> List.head
          OccurredAt = values parsed "--occurred-at" |> List.head
          RemovedBy = actor.Id }

    let private renderRemoval asJson (outcome: MemberRemovalOutcome) =
        let removed (group: StoredGroup) = List.last group.Removals

        match outcome with
        | MemberRemovalOutcome.Rejected rejections ->
            let messages = rejections |> List.map GroupDeclaration.removalMessage

            if asJson then
                printf "%s" (PlanningJson.renderGroupRejected messages)

            for message in messages do
                eprintfn "ERROR %s" message

            eprintfn "work group remove rejected; nothing was recorded"
            1
        | MemberRemovalOutcome.Planned group ->
            if asJson then
                printf "%s" (PlanningJson.renderMemberRemoved true FileWorkGroupStore.relativePath (removed group) group)
            else
                printfn "dry run: would remove %s from %s in %s; nothing was recorded" (removed group).Member group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0
        | MemberRemovalOutcome.Recorded group ->
            let removal = removed group

            if asJson then
                printf "%s" (PlanningJson.renderMemberRemoved false FileWorkGroupStore.relativePath removal group)
            else
                printfn "removed %s from %s in %s" removal.Member group.Declaration.Id FileWorkGroupStore.relativePath
                describe group

            0

    let private withArguments (errors: Parsed -> string list) (rest: string list) (continuation: Parsed -> int) =
        let parsed = parse { Values = Map.empty; Unexpected = [] } rest

        match errors parsed with
        | _ :: _ as found ->
            for error in found do
                eprintfn "ERROR %s" error

            eprintfn "Usage: ros %s" usage
            2
        | [] -> continuation parsed

    let private reported (render: 'outcome -> int) (result: Result<'outcome, string>) =
        match result with
        | Error message ->
            eprintfn "ERROR %s" message
            1
        | Ok outcome -> render outcome

    let run root (arguments: string list) (actor: Actor) =
        match arguments with
        | "create" :: rest ->
            withArguments createArgumentErrors rest (fun parsed ->
                FileWorkGroupRepository.create root (List.contains "--dry-run" rest) (request parsed rest actor)
                |> reported (render (List.contains "--json" rest)))
        | "add" :: rest ->
            withArguments (fun parsed -> addArgumentErrors parsed rest) rest (fun parsed ->
                FileWorkGroupRepository.add root (values parsed "--config" |> List.tryHead) (List.contains "--dry-run" rest) (additionRequest parsed actor)
                |> reported (renderAddition (List.contains "--json" rest)))
        | "remove" :: rest ->
            withArguments (fun parsed -> removeArgumentErrors parsed rest) rest (fun parsed ->
                FileWorkGroupRepository.remove root (List.contains "--dry-run" rest) (removalRequest parsed actor)
                |> reported (renderRemoval (List.contains "--json" rest)))
        | _ ->
            eprintfn "Usage: ros %s" usage
            2
