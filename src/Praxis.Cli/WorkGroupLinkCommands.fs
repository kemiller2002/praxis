namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group link` (PRX-GRP-102): run in a member's own repository, it
/// records the only group data that repository holds, an immutable
/// reference from one of its items to a group recorded at its home. It
/// writes the group store's `references` and nothing else; it never reads
/// or writes the home.
[<RequireQualifiedAccess>]
module WorkGroupLinkCommands =
    let usage =
        "work group link --group GROUP-ECHELON-AREA-SEQ --home OWNER/REPO --member ID --occurred-at TIMESTAMP [--dry-run] [--json] [IDENTITY]"

    let run (root: string) (rawArguments: string list) (actor: Actor) =
        let command = "work group link"
        let arguments = WorkGroupCommands.parse [ "--group"; "--home"; "--member"; "--occurred-at" ] [ "--dry-run" ] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let dryRun = arguments.Switches.Contains "--dry-run"
        let value flag = arguments.Values |> Map.tryFind flag |> Option.defaultValue []

        let errors =
            [ yield! arguments.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'")
              yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
              for flag in [ "--group"; "--home"; "--member"; "--occurred-at" ] do
                  if (value flag).Length <> 1 then
                      yield $"{command} requires exactly one {flag}"
              match value "--home" with
              | [ home ] when not (RepositoryName.isValid home) -> yield $"--home '{home}' is not owner/repo"
              | _ -> ()
              match value "--occurred-at" with
              | [ at ] when not (fst (System.DateTimeOffset.TryParse at)) -> yield $"--occurred-at '{at}' is not a timestamp"
              | _ -> () ]

        match errors with
        | _ :: _ -> WorkGroupCommands.reportArgumentErrors command usage errors
        | [] ->
            let request =
                { GroupId = (value "--group").Head
                  HomeRepository = (value "--home").Head
                  WorkItemId = (value "--member").Head
                  OccurredAt = (value "--occurred-at").Head
                  Actor = actor
                  ExecutionId = FileWorkGroupFacts.callerExecution root (ProvenanceCommands.identityOverridesFrom rawArguments) }

            let outcome =
                FileWorkGroupRepository.transactStore root dryRun (fun store ->
                    match FileWorkGroupFacts.context root None store.Groups with
                    | Error message -> Error(Choice1Of2 message)
                    | Ok(_, context) -> WorkGroups.link context store request |> Result.mapError Choice2Of2 |> Result.map (fun change -> change.Store, change))

            match outcome with
            | Error message
            | Ok(Error(Choice1Of2 message)) -> WorkGroupCommands.reportFailure asJson command message
            | Ok(Error(Choice2Of2 rejections)) -> WorkGroupCommands.reportRejections asJson command request.GroupId rejections
            | Ok(Ok change) ->
                let status = if dryRun then "dry-run" elif change.Changed then "recorded" else "unchanged"

                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope
                            command
                            status
                            [ "dryRun", WorkGroupJson.boolean dryRun
                              "changed", WorkGroupJson.boolean change.Changed
                              "reference", WorkGroupJson.referenceNode change.Reference ]
                    )
                else
                    printfn "%s references %s at %s" change.Reference.WorkItemId change.Reference.GroupId change.Reference.HomeRepository

                    if not change.Changed then printfn "unchanged: the reference already holds; nothing was recorded"
                    elif dryRun then printfn "dry run: nothing was written"
                    else printfn "Praxis state changed in %s (references only); commit and push it. The home is never written from here." FileWorkGroupRepository.relativePath

                0
