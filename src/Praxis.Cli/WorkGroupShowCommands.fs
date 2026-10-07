namespace Praxis.Cli

open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `work group show` (PRX-GRP-107): read-only, the consolidated status
/// table of a group, across repositories. Members elsewhere are dated
/// observations, never copies of their state. In a member repository it
/// shows the references its items hold to groups homed elsewhere.
[<RequireQualifiedAccess>]
module WorkGroupShowCommands =
    let usage = WorkGroupCommands.showUsage

    let private bullets (values: string list) =
        match values with
        | [] -> [ "  (none)" ]
        | values -> values |> List.map (fun value -> $"  - {value}")

    let private memberLine (row: MemberProgress) =
        let state = row.State |> Option.defaultValue "not recorded"
        let planning = row.PlanningState |> Option.map (fun value -> $" [planning: {value}]") |> Option.defaultValue ""
        let listed (label: string) (ids: string list) = match ids with [] -> "" | ids -> "; " + label + " " + String.concat ", " ids

        let observed =
            match row.Observation with
            | None -> ""
            | Some observation ->
                let at = observation.SourceAsOf |> Option.defaultValue observation.ObservedAt
                let commit = observation.Commit |> Option.map (fun value -> value.Substring(0, min 12 value.Length)) |> Option.defaultValue "-"

                match observation.Outcome with
                | ObservedMember.Unobservable why -> $"; observed: unknown ({Unobservable.code why}: {Unobservable.reason why})"
                | ObservedMember.Read _ when row.Stale -> $"; observed: STALE at {commit} as of {at}"
                | ObservedMember.Read _ ->
                    let linked = match observation.Linked with Some false -> "; UNLINKED" | _ -> ""
                    $"; observed at {commit} as of {at}{linked}"

        let waits = listed "waits on" row.WaitsOn
        let gates = listed "gates" row.Gates
        $"  {row.WorkItemId,-24} {MemberCategory.code row.Category,-10} recorded {state}{planning}{waits}{gates}{observed}"

    let private historyLine (entry: GroupHistoryEntry) =
        let memberText = entry.Member |> Option.map (fun id -> $" {id}") |> Option.defaultValue ""
        let reason = entry.Reason |> Option.map (fun text -> $": {text}") |> Option.defaultValue ""
        let empty = if entry.ExplicitEmpty then " (explicitly left the group empty)" else ""
        $"  {entry.At} {GroupOperation.code entry.Operation}{memberText} by {ActorKind.code entry.Actor.Kind}:{entry.Actor.Id}{empty}{reason}"

    let private text (group: StoredWorkGroup) (summary: GroupSummary) (order: OrderRow list) =
        let progress = summary.Progress
        let declaration = group.Declaration
        let kind = declaration.Kind |> Option.map GroupKind.code |> Option.defaultValue "unspecified kind"
        let repository = declaration.ExecutionRepository |> Option.defaultValue "unknown"
        let scope = if declaration.CrossRepository then "yes" else "no"
        let home = group.HomeRepository |> Option.defaultValue "not recorded"

        [ yield $"WORK GROUP {declaration.Id} ({kind}, {GroupOrigin.code declaration.Origin})"
          yield $"Home:        {home}; executes in {repository} (cross-repository: {scope})"
          yield $"Status:      {GroupStatus.code summary.Status} (derived from the members' own states; no command sets it)"
          yield $"Progress:    {GroupProgress.summary progress}; each member completes on its own evidence (PRX-GRP-042)"
          if declaration.CrossRepository then
              for byRepository in WorkGroups.repositoryProgress progress do
                  yield $"  {byRepository.Repository}: {byRepository.Completed} of {byRepository.Total} complete ({byRepository.Unknown} unknown)"
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
          if not order.IsEmpty then
              yield ""
              yield "ORDER"

              for row in order do
                  let reason = match row.State with EdgeState.Unknown reason -> $" ({reason})" | _ -> ""
                  yield $"  {row.Dependency.Consumer} after {row.Dependency.Producer} {ProducerMilestone.code row.Dependency.Milestone}: {EdgeState.code row.State}{reason}"
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

    /// What a member repository knows of a group homed elsewhere: only its
    /// own references, and whether the home lists them (observed).
    let private references (root: string) (asJson: bool) (groupId: string) (found: GroupReference list) =
        let membership =
            match FileWorkGroupFacts.homeMembership root None with
            | Ok membership -> membership
            | Error _ -> fun _ -> None

        let linked (reference: GroupReference) = membership reference

        if asJson then
            WorkGroupCommands.printJson (
                WorkGroupCommands.envelope
                    "work group show"
                    "referenced"
                    [ "groupId", WorkGroupJson.text groupId
                      "references",
                      found
                      |> List.map (fun reference ->
                          let node = (WorkGroupJson.referenceNode reference).AsObject()
                          node["linked"] <- (linked reference |> Option.map WorkGroupJson.boolean |> Option.toObj)
                          node :> System.Text.Json.Nodes.JsonNode)
                      |> WorkGroupJson.array ]
            )
        else
            printfn "WORK GROUP %s is recorded at its home; this repository holds only references:" groupId

            for reference in found do
                let state = match linked reference with Some true -> "linked" | Some false -> "UNLINKED (the home does not list it)" | None -> "home not observable"
                printfn "  %s -> home %s, linked at %s: %s" reference.WorkItemId reference.HomeRepository reference.LinkedAt state

        0

    let run (root: string) (rawArguments: string list) =
        let command = "work group show"
        let arguments = WorkGroupCommands.parse [ "--config" ] [] rawArguments
        let asJson = arguments.Switches.Contains "--json"
        let config = arguments.Values |> Map.tryFind "--config" |> Option.bind List.tryHead

        let errors =
            [ yield! arguments.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'")
              if (arguments.Values |> Map.tryFind "--config" |> Option.defaultValue []).Length > 1 then
                  yield $"{command} accepts --config once"
              match arguments.Positional with
              | [ _ ] -> ()
              | [] -> yield $"{command} requires GROUP-ID"
              | _ -> yield $"{command} shows exactly one group" ]

        match errors with
        | _ :: _ -> WorkGroupCommands.reportArgumentErrors command usage errors
        | [] ->
            let groupId = arguments.Positional.Head

            let found =
                FileWorkGroupRepository.readStore root
                |> Result.bind (fun store ->
                    match WorkGroups.tryFind store.Groups groupId with
                    | None -> Ok(Choice2Of2(store.References |> List.filter (fun reference -> reference.GroupId = groupId)))
                    | Some group ->
                        FileWorkGroupFacts.memberFactsFor root config (Some groupId)
                        |> Result.bind (fun facts ->
                            FileWorkGroupFacts.milestones root config group
                            |> Result.map (fun reached -> Choice1Of2(group, WorkGroups.summarize group facts, WorkGroups.order group reached))))

            match found with
            | Error message ->
                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope command "failed" [ "failure", WorkGroupJson.record [ "code", WorkGroupJson.text "read-failed"; "message", WorkGroupJson.text message ] ]
                    )
                else
                    eprintfn "ERROR %s" message

                1
            | Ok(Choice2Of2((_ :: _) as found)) -> references root asJson groupId found
            | Ok(Choice2Of2 []) ->
                let rejection = GroupRejection.UnknownGroup groupId

                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope command "not-found" [ "groupId", WorkGroupJson.text groupId; "rejections", WorkGroupJson.array [ WorkGroupJson.rejectionNode rejection ] ]
                    )
                else
                    eprintfn "ERROR [%s] %s" (GroupRejection.code rejection) (GroupRejection.message rejection)

                1
            | Ok(Choice1Of2(group, summary, order)) ->
                if asJson then
                    WorkGroupCommands.printJson (
                        WorkGroupCommands.envelope
                            command
                            "found"
                            [ "group", WorkGroupJson.groupNode group
                              "groupStatus", WorkGroupJson.text (GroupStatus.code summary.Status)
                              "progress", WorkGroupJson.progressNode summary.Progress
                              "repositories", WorkGroups.repositoryProgress summary.Progress |> List.map WorkGroupJson.repositoryProgressNode |> WorkGroupJson.array
                              "members", summary.Progress.Members |> List.map WorkGroupJson.memberProgressNode |> WorkGroupJson.array
                              "order", order |> List.map WorkGroupJson.orderNode |> WorkGroupJson.array
                              "consumers",
                              order
                              |> List.map (fun row -> row.Dependency.Consumer)
                              |> List.distinct
                              |> List.map (fun consumer ->
                                  WorkGroupJson.record [ "member", WorkGroupJson.text consumer; "state", WorkGroupJson.optionalText (WorkGroups.consumerState order consumer) ] :> System.Text.Json.Nodes.JsonNode)
                              |> WorkGroupJson.array
                              "unlinked", WorkGroupJson.texts (WorkGroups.unlinkedMembers summary.Progress)
                              "removedOpen", summary.RemovedOpen |> List.map WorkGroupJson.removedOpenNode |> WorkGroupJson.array ]
                    )
                else
                    text group summary order |> List.iter (printfn "%s")

                0
