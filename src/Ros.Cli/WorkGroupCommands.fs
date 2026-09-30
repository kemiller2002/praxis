namespace Ros.Cli

open System
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create`: records a human-declared execution group in Praxis
/// state (PRX-GRP-073, phase two). This module parses, delegates to
/// `FileWorkGroupRepository` (Application operation over the Domain
/// decision) and renders; it holds no grouping policy.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group create --id GROUP-ID --member ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository NAME] [--cross-repository] [--shared-context TEXT]* [--dry-run] [--json] [IDENTITY]"

    let private flagsWithValues =
        set
            [ "--id"
              "--member"
              "--kind"
              "--execution-repository"
              "--shared-context"
              "--occurred-at"
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

    let private argumentErrors (parsed: Parsed) =
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

    let run root (arguments: string list) (actor: Actor) =
        match arguments with
        | "create" :: rest ->
            let parsed = parse { Values = Map.empty; Unexpected = [] } rest

            match argumentErrors parsed with
            | _ :: _ as errors ->
                for error in errors do
                    eprintfn "ERROR %s" error

                eprintfn "Usage: ros %s" usage
                2
            | [] ->
                match FileWorkGroupRepository.create root (List.contains "--dry-run" rest) (request parsed rest actor) with
                | Error message ->
                    eprintfn "ERROR %s" message
                    1
                | Ok outcome -> render (List.contains "--json" rest) outcome
        | _ ->
            eprintfn "Usage: ros %s" usage
            2
