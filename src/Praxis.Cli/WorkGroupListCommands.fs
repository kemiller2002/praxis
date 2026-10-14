namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group list` (PRX-GRP-110): read-only. Reads the store, the queue,
/// the live context and the planner's analysis; never takes a lock and
/// never writes. Status is derived (PRX-GRP-103/116), never stored.
[<RequireQualifiedAccess>]
module WorkGroupListCommands =
    let usage = "work group list [--status STATUS] [--member ID] [--repository NAME] [--config FILE] [--json]"

    let private statusCodes = GroupStatus.all |> List.map GroupStatus.code |> String.concat ", "

    let private row (summary: GroupSummary) =
        let kind = summary.Kind |> Option.map Praxis.Domain.Planning.GroupKind.code |> Option.defaultValue "-"
        let home = summary.Home |> Option.defaultValue "unknown"
        let mode = summary.ExecutionMode |> Option.defaultValue "-"
        let latest = summary.LatestCheckpointAt |> Option.defaultValue "-"
        let scope = if summary.CrossRepository then "cross-repository" else "local"
        let completed = summary.Progress.Completed.Length

        $"{summary.GroupId,-32} {GroupStatus.code summary.Status,-18} {completed} of {summary.MemberCount} complete  {kind} / {Praxis.Domain.Planning.GroupOrigin.code summary.Origin}  home {home} ({scope})  mode {mode}  checkpoint {latest}"

    let run (root: string) (rawArguments: string list) =
        let command = "work group list"
        let arguments = WorkGroupCommands.parse [ "--status"; "--member"; "--repository"; "--config" ] [] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let value flag = arguments.Values |> Map.tryFind flag |> Option.defaultValue []
        let status = value "--status" |> List.tryHead |> Option.map (fun raw -> raw, GroupStatus.tryParse raw)

        let errors =
            [ yield! arguments.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'")
              yield! arguments.Positional |> List.map (fun token -> $"unexpected argument '{token}'")
              for flag in [ "--status"; "--member"; "--repository"; "--config" ] do
                  if (value flag).Length > 1 then
                      yield $"{command} accepts {flag} once"
              match status with
              | Some(raw, None) -> yield $"--status '{raw}' is not a group status ({statusCodes})"
              | _ -> () ]

        match errors with
        | _ :: _ -> WorkGroupCommands.reportArgumentErrors command usage errors
        | [] ->
            let filter =
                { Status = status |> Option.bind snd
                  Member = value "--member" |> List.tryHead
                  Repository = value "--repository" |> List.tryHead }

            let listed =
                FileWorkGroupRepository.read root
                |> Result.bind (fun groups ->
                    match groups with
                    | [] -> Ok []
                    | groups ->
                        FileWorkGroupFacts.memberFacts root (value "--config" |> List.tryHead)
                        |> Result.map (fun facts -> groups |> List.map (fun group -> WorkGroups.summarize group facts) |> WorkGroups.list filter))

            match listed with
            | Error message ->
                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope command "failed" [ "failure", WorkGroupJson.record [ "code", WorkGroupJson.text "read-failed"; "message", WorkGroupJson.text message ] ]
                    )
                else
                    eprintfn "ERROR %s" message

                1
            | Ok summaries ->
                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope command "listed" [ "count", WorkGroupJson.integer summaries.Length; "groups", summaries |> List.map WorkGroupJson.summaryNode |> WorkGroupJson.array ]
                    )
                else
                    match summaries with
                    | [] -> printfn "no work groups match"
                    | summaries -> summaries |> List.iter (row >> printfn "%s")

                0
