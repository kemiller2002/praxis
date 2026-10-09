namespace Praxis.Cli

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// `plan execute-group` (PRX-GRP-117): the only `plan` verb that mutates,
/// and only Praxis state. It begins the next member through the existing
/// `work begin` transition (passed in, never reimplemented) and records the
/// group execution. It never launches an agent, creates a branch, selects a
/// provider or model, or changes priorities or dependencies.
[<RequireQualifiedAccess>]
module ExecuteGroupCommands =
    let usage =
        "plan execute-group GROUP-ID --occurred-at TIMESTAMP [--member ID] [--mode grouped|independent --reason TEXT] [--independent-member ID --reason TEXT]* [--type TYPE] [--config FILE] [--dry-run] [--json] [IDENTITY]"

    let private identityFlags =
        [ "--actor-kind"; "--agent"; "--actor"; "--provider"; "--model"; "--model-version"; "--runtime"; "--runtime-version"; "--session"; "--conversation"; "--run"; "--subagent" ]

    /// Runs `work begin` with its output captured, so `--json` still prints
    /// exactly one document.
    let private captured (action: unit -> int) =
        let original = Console.Out
        use writer = new StringWriter()
        Console.SetOut writer

        try
            let code = action ()
            code, writer.ToString()
        finally
            Console.SetOut original

    let private planNode (plan: ExecuteGroupPlan) =
        match plan with
        | ExecuteGroupPlan.Begin(execution, isNew, memberId, mode) ->
            [ "groupExecution", WorkGroupJson.executionNode execution
              "newGroupExecution", WorkGroupJson.boolean isNew
              "member", WorkGroupJson.text memberId
              "mode", WorkGroupJson.text (ExecutionMode.code mode) ]
        | ExecuteGroupPlan.AlreadyBegun(execution, memberId) ->
            [ "groupExecution", WorkGroupJson.executionNode execution; "member", WorkGroupJson.text memberId ]
        | ExecuteGroupPlan.FellBack(execution, recommendations) ->
            [ "groupExecution", WorkGroupJson.executionNode execution; "recommendations", WorkGroupJson.texts recommendations ]
        | ExecuteGroupPlan.Finished execution -> [ "groupExecution", WorkGroupJson.executionNode execution ]

    let private describe (plan: ExecuteGroupPlan) =
        match plan with
        | ExecuteGroupPlan.Begin(execution, isNew, memberId, mode) ->
            let started = if isNew then $"started {execution.Id} in {ExecutionMode.code execution.Mode} mode" else $"continuing {execution.Id}"
            [ $"{started}; beginning {memberId} ({ExecutionMode.code mode})"; yield! execution.Basis |> List.map (fun basis -> $"  basis: {basis}") ]
        | ExecuteGroupPlan.AlreadyBegun(execution, memberId) -> [ $"unchanged: {memberId} was already begun in {execution.Id}" ]
        | ExecuteGroupPlan.FellBack(execution, recommendations) ->
            let fallback = execution.Fallback.Value
            [ $"fallback: {execution.Id} stops beginning members in grouped mode ({fallback.Signal}; fallback: independent)"
              yield! fallback.Evidence |> List.map (fun evidence -> $"  evidence: {evidence}")
              yield! recommendations |> List.map (fun recommendation -> $"  next: {recommendation}") ]
        | ExecuteGroupPlan.Finished execution -> [ $"{execution.Id} ended: every runnable member has been begun and none remains" ]

    let run (root: string) (rawArguments: string list) (beginMember: string list -> Actor -> int) (actor: Actor) =
        let command = "plan execute-group"

        let arguments =
            WorkGroupCommands.parse [ "--occurred-at"; "--member"; "--mode"; "--reason"; "--independent-member"; "--type"; "--config" ] [ "--dry-run" ] rawArguments

        let asJson = arguments.Switches.Contains "--json"
        let dryRun = arguments.Switches.Contains "--dry-run"
        let all flag = arguments.Values |> Map.tryFind flag |> Option.defaultValue []
        let single flag = all flag |> List.tryHead
        let mode = single "--mode" |> Option.map (fun raw -> raw, ExecutionMode.tryParse raw)

        let errors =
            [ yield! arguments.Unexpected |> List.map (fun token -> $"unexpected argument '{token}'")
              match arguments.Positional with
              | [ _ ] -> ()
              | _ -> yield $"{command} names exactly one GROUP-ID"
              match all "--occurred-at" with
              | [ at ] when fst (DateTimeOffset.TryParse at) -> ()
              | _ -> yield $"{command} requires one --occurred-at TIMESTAMP (the real current time)"
              for flag in [ "--member"; "--mode"; "--reason"; "--type"; "--config" ] do
                  if (all flag).Length > 1 then yield $"{command} accepts {flag} once"
              match mode with
              | Some(raw, None) -> yield $"--mode '{raw}' is not grouped or independent"
              | _ -> () ]

        match errors with
        | _ :: _ -> WorkGroupCommands.reportArgumentErrors command usage errors
        | [] ->
            let groupId = arguments.Positional.Head
            let occurredAt = (single "--occurred-at").Value
            let reason = single "--reason" |> Option.defaultValue ""
            let report status fields = WorkGroupCommands.printJson (WorkGroupCommands.envelope command status ([ "groupId", WorkGroupJson.text groupId ] @ fields))

            match FileGroupExecution.facts root (single "--config") groupId actor with
            | Error message -> WorkGroupCommands.reportFailure asJson command message
            | Ok(facts, _, _) when
                facts.Group
                |> Option.exists (fun group ->
                    EcirGates.isEcirGroup group.Declaration.SharedContext) ->
                // A model-authored "validated" field or CLI flag cannot
                // substitute for a pinned Ordo validation observation and a
                // separate decision-authorization receipt. Refuse before
                // work begin or any state mutation until the bridge exists.
                WorkGroupCommands.reportFailure asJson command
                    "ECIR group execution refused: no independently verified Ordo validation and decision-authorization evidence. The pinned Ordo/Praxis integration is required; nothing was begun."
            | Ok(facts, _, repository) ->
                let request =
                    { GroupId = groupId
                      OccurredAt = occurredAt
                      Actor = actor
                      Member = single "--member"
                      Mode = mode |> Option.bind snd |> Option.map (fun parsed -> parsed, reason)
                      IndependentMembers = all "--independent-member" |> List.map (fun id -> id, reason)
                      NewExecutionId = FileGroupExecution.newIdentifier groupId occurredAt actor
                      Repository = repository }

                match GroupExecutions.decide request facts with
                | Error rejections ->
                    if asJson then
                        report "rejected" [ "rejections", rejections |> List.map (fun rejection -> WorkGroupJson.record [ "code", WorkGroupJson.text (ExecuteGroupRejection.code rejection); "message", WorkGroupJson.text (ExecuteGroupRejection.message rejection) ] :> JsonNode) |> WorkGroupJson.array ]
                    else
                        rejections |> List.iter (fun rejection -> eprintfn "ERROR [%s] %s" (ExecuteGroupRejection.code rejection) (ExecuteGroupRejection.message rejection))
                        eprintfn "%s refused; nothing was begun or recorded" command

                    if rejections |> List.forall ExecuteGroupRejection.isArgumentError then 2 else 1
                | Ok plan when dryRun ->
                    if asJson then report "dry-run" ([ "changed", WorkGroupJson.boolean false ] @ planNode plan)
                    else describe plan @ [ "dry run: nothing was begun or written" ] |> List.iter (printfn "%s")

                    0
                | Ok(ExecuteGroupPlan.AlreadyBegun _ as plan) ->
                    if asJson then report "unchanged" ([ "changed", WorkGroupJson.boolean false ] @ planNode plan)
                    else describe plan |> List.iter (printfn "%s")

                    0
                | Ok plan ->
                    let begun =
                        match plan with
                        | ExecuteGroupPlan.Begin(_, _, memberId, _) ->
                            let identity = rawArguments |> List.pairwise |> List.filter (fun (flag, _) -> List.contains flag identityFlags) |> List.collect (fun (flag, value) -> [ flag; value ])
                            let workType = single "--type" |> Option.map (fun value -> [ "--type"; value ]) |> Option.defaultValue []
                            let code, output = captured (fun () -> beginMember ([ "--id"; memberId; "--occurred-at"; occurredAt ] @ workType @ identity) actor)
                            if code = 0 then Ok(output, FileGroupExecution.memberExecution root memberId) else Error(code, output)
                        | _ -> Ok("", None)

                    match begun with
                    | Error(code, output) ->
                        eprintfn "%s" output
                        eprintfn "ERROR work begin refused the member; nothing was recorded in the group (exit %d)" code
                        code
                    | Ok(output, execution) ->
                        let recorded =
                            FileWorkGroupRepository.transact root false (fun groups ->
                                match WorkGroups.tryFind groups groupId with
                                | None -> Error $"group {groupId} disappeared"
                                | Some group -> Ok(WorkGroups.upsert groups (GroupExecutions.record request group plan execution), ()))

                        let status =
                            match plan with
                            | ExecuteGroupPlan.FellBack _ -> "fell-back"
                            | ExecuteGroupPlan.Finished _ -> "finished"
                            | _ -> "begun"

                        match recorded with
                        | Error message
                        | Ok(Error message) ->
                            eprintfn "ERROR the member was begun but the group execution could not be recorded: %s" message
                            1
                        | Ok(Ok()) ->
                            if asJson then
                                report status ([ "changed", WorkGroupJson.boolean true; "memberExecution", WorkGroupJson.optionalText execution ] @ planNode plan @ [ "workBegin", WorkGroupJson.text output ])
                            else
                                printf "%s" output
                                describe plan |> List.iter (printfn "%s")
                                printfn "Praxis state changed (work begin and %s); commit and push it. Nothing was launched." FileWorkGroupRepository.relativePath

                            0
