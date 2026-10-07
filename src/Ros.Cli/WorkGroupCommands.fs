namespace Ros.Cli

open System
open System.Globalization
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Infrastructure.Planning

/// `work group create`: records a declared execution group in Praxis state
/// (PRX-GRP-073 phase two). Parses, delegates to `WorkGroupOperations`, and
/// renders; it holds no group policy.
[<RequireQualifiedAccess>]
module WorkGroupCommands =
    let usage =
        "work group create --id GROUP-ID --member ITEM [--member ITEM]* --occurred-at TIMESTAMP [--kind KIND] [--origin ORIGIN] [--shared-context TEXT]* [--execution-repository NAME] [--cross-repository] [--architecture-note TEXT]* [--dry-run] [--json] [IDENTITY]"

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    let private flagsWithValues =
        set (
            [ "--id"; "--member"; "--occurred-at"; "--kind"; "--origin"; "--shared-context"; "--execution-repository"; "--architecture-note" ]
            @ identityFlags
        )

    let private switches = set [ "--dry-run"; "--json"; "--cross-repository" ]

    /// Every flag value in argument order, keyed by flag.
    type private Parsed =
        { Values: (string * string) list
          Unexpected: string list }

    let rec private parse (parsed: Parsed) (arguments: string list) =
        match arguments with
        | [] -> parsed
        | flag :: value :: rest when flagsWithValues.Contains flag && not (value.StartsWith "--") ->
            parse { parsed with Values = parsed.Values @ [ flag, value ] } rest
        | switch :: rest when switches.Contains switch -> parse parsed rest
        | token :: rest -> parse { parsed with Unexpected = parsed.Unexpected @ [ token ] } rest

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

            eprintfn "Usage: ros %s" usage
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

    /// Stored-group findings for the unified `validate`, as (path, field, message).
    let findings root : Result<(string * string * string) list, string> =
        let path = ".ros/work/groups.json"

        WorkGroupOperations.findings (FileWorkGroupRepository.create root)
        |> Result.map (List.map (fun finding -> path, finding.Field, finding.Message))
