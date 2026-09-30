namespace Ros.Cli

open System
open System.Globalization
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create`: records a durable human-declared execution group
/// (PRX-GRP-073 phase two). This module parses, delegates to
/// `FileWorkGroupRepository` and renders; the declaration policy lives in
/// `GroupDeclaration`. It never changes a member's lifecycle state.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group create --id GROUP-ID --member ID [--member ID]* --occurred-at TIMESTAMP [--kind KIND] [--execution-repository REPOSITORY] [--cross-repository] [--shared-context TEXT]* [--architecture-note TEXT]* [--dry-run] [--json] [IDENTITY]"

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
                  "--architecture-note" ])

    let private switches = set [ "--dry-run"; "--json"; "--cross-repository" ]

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

    let private single (parsed: Parsed) flag (required: bool) : Result<string option, string> =
        match values parsed flag, required with
        | [ value ], _ -> Ok(Some value)
        | [], false -> Ok None
        | [], true -> Error $"work group create requires {flag}"
        | _ -> Error $"pass {flag} once"

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
        let occurredAt = single parsed "--occurred-at" true |> Result.bind (function
            | Some value when isTimestamp value -> Ok value
            | Some value -> Error $"--occurred-at '{value}' is not a timestamp"
            | None -> Error "work group create requires --occurred-at TIMESTAMP (the real current time)")
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
            |> fun found -> found @ (parsed.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'"))

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

    let run root (arguments: string list) (actor: Actor) =
        match arguments with
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
            eprintfn "Usage: ros %s" usage
            2
