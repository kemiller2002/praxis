namespace Ros.Cli

open System
open Ros.Application.Planning
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Provenance
open Ros.Domain.Work
open Ros.Infrastructure.Artifacts
open Ros.Infrastructure.Planning

/// `work group create` records a human-declared execution group in Praxis
/// state; `work group add` adds one member to it and `work group remove`
/// removes one, and `work group checkpoint` records a durable group-level
/// checkpoint over the members' own (PRX-GRP-073, PRX-GRP-044; phase two).
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

    let private checkpointUsage =
        "work group checkpoint --id GROUP-ID --occurred-at TIMESTAMP --summary TEXT --next-action TEXT [--decision TEXT]* [--dry-run] [--json] [IDENTITY]"

    let usage = String.Join(" | ", [ createUsage; addUsage; removeUsage; checkpointUsage ])

    let private flagsWithValues =
        set
            [ "--id"
              "--member"
              "--kind"
              "--execution-repository"
              "--shared-context"
              "--occurred-at"
              "--config"
              "--summary"
              "--next-action"
              "--decision"
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

    let private checkpointOnly (parsed: Parsed) (command: string) =
        [ "--summary"; "--next-action"; "--decision" ]
        |> List.filter (fun flag -> not (values parsed flag).IsEmpty)
        |> List.map (fun flag -> $"{flag} is a work group checkpoint option, not a {command} option")

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
          yield! checkpointOnly parsed "work group create"
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
          yield! checkpointOnly parsed "work group add"
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
          yield! checkpointOnly parsed "work group remove"
          for flag in [ "--kind"; "--execution-repository"; "--shared-context" ] do
              if not (values parsed flag).IsEmpty then
                  yield $"{flag} is a work group create option; work group remove changes only membership"
          if List.contains "--cross-repository" arguments then
              yield "--cross-repository is a work group create option; work group remove changes only membership"
          for token in parsed.Unexpected do
              yield $"unexpected argument '{token}'" ]

    let private checkpointArgumentErrors (parsed: Parsed) (arguments: string list) =
        [ match values parsed "--id" with
          | [ _ ] -> ()
          | [] -> yield "work group checkpoint requires --id GROUP-ID"
          | _ -> yield "work group checkpoint checkpoints one group; pass --id once"
          match values parsed "--occurred-at" with
          | [ _ ] -> ()
          | _ -> yield "work group checkpoint requires exactly one --occurred-at TIMESTAMP (the real current time)"
          for flag in [ "--summary"; "--next-action" ] do
              match values parsed flag with
              | [ _ ] -> ()
              | [] -> yield $"work group checkpoint requires {flag} TEXT"
              | _ -> yield $"{flag} may be given once"
          for flag in [ "--member"; "--config"; "--kind"; "--execution-repository"; "--shared-context" ] do
              if not (values parsed flag).IsEmpty then
                  yield $"{flag} does not apply to work group checkpoint; members are read from the stored group"
          if List.contains "--cross-repository" arguments then
              yield "--cross-repository does not apply to work group checkpoint"
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

    let private checkpointRequest root (parsed: Parsed) (actor: Actor) : GroupCheckpointRequest =
        { GroupId = values parsed "--id" |> List.head
          Repository = Ros.Infrastructure.Work.FileWorkConfigRepository.readRepositoryId root
          Summary = values parsed "--summary" |> List.head
          NextAction = values parsed "--next-action" |> List.head
          SharedDecisions = values parsed "--decision"
          OccurredAt = values parsed "--occurred-at" |> List.head
          RecordedBy = actor.Id }

    let private describeCheckpoint (checkpoint: GroupCheckpoint) =
        let joined (values: string list) = if values.IsEmpty then "none" else String.Join(", ", values)
        let location = checkpoint.Location
        printfn "  commit:          %s on %s" location.LocalCommit.Value location.Branch
        printfn "  verified at:     %s/%s == local HEAD (read from the remote itself)" location.Remote.Name location.RemoteBranch
        printfn "  active:          %s" (joined checkpoint.Progress.Active)
        printfn "  completed:       %s" (joined checkpoint.Progress.Completed)

        if not checkpoint.Progress.Abandoned.IsEmpty then
            printfn "  abandoned:       %s" (joined checkpoint.Progress.Abandoned)

        printfn "  remaining:       %s" (joined checkpoint.Progress.Remaining)

        for decision in checkpoint.SharedDecisions do
            printfn "  shared decision: %s" decision

        for reference in checkpoint.MemberCheckpoints do
            printfn "  %s's own checkpoint: %s (commit %s, %s)" reference.Member reference.CheckpointId reference.Commit reference.RecordedAt

        for id in checkpoint.UncheckpointedMembers do
            printfn "  %s has no checkpoint of its own yet" id

        for execution in checkpoint.Executions do
            printfn "  execution:       %s (%s)" execution.ExecutionId execution.Member

        printfn "  milestone:       %s" checkpoint.Summary
        printfn "  next action:     %s" checkpoint.NextAction
        printfn "  recorded:        %s by %s" checkpoint.RecordedAt checkpoint.RecordedBy
        printfn "no member's checkpoint, lifecycle state or attribution was changed; each member still checkpoints and completes on its own"

    let private renderCheckpoint asJson (outcome: GroupCheckpointOutcome) =
        match outcome with
        | GroupCheckpointOutcome.Rejected rejections ->
            if asJson then
                rejections
                |> List.map (fun rejection -> GroupCheckpoints.code rejection, GroupCheckpoints.message rejection, GroupCheckpoints.remedy rejection)
                |> PlanningJson.renderGroupCheckpointRejected
                |> printf "%s"

            for rejection in rejections do
                eprintfn "ERROR [%s] %s" (GroupCheckpoints.code rejection) (GroupCheckpoints.message rejection)
                eprintfn "  REMEDY %s" (GroupCheckpoints.remedy rejection)

            eprintfn "work group checkpoint rejected; nothing was recorded"
            if rejections |> List.forall GroupCheckpoints.isArgumentError then 2 else 1
        | GroupCheckpointOutcome.Planned checkpoint ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCheckpointRecorded true FileGroupCheckpointRepository.relativePath checkpoint)
            else
                printfn "dry run: would record %s in %s; nothing was recorded" checkpoint.Id FileGroupCheckpointRepository.relativePath
                describeCheckpoint checkpoint

            0
        | GroupCheckpointOutcome.Recorded checkpoint ->
            if asJson then
                printf "%s" (PlanningJson.renderGroupCheckpointRecorded false FileGroupCheckpointRepository.relativePath checkpoint)
            else
                printfn "durable group checkpoint %s recorded in %s" checkpoint.Id FileGroupCheckpointRepository.relativePath
                describeCheckpoint checkpoint
                printfn "Praxis state changed under .ros/; commit and push it so another executor can find this group checkpoint."

            0

    /// `operation` under the `work-protocol` lock that `work checkpoint`
    /// holds, so a group checkpoint never interleaves with a member's.
    let private locked root (operation: unit -> Result<'outcome, string>) =
        match RegistryLock.acquire root "work-protocol" RegistryLock.defaultSettings with
        | Error failure -> Error failure.Message
        | Ok lease ->
            let result =
                try
                    operation ()
                with error ->
                    Error $"state persistence failed: {error.Message}"

            match lease.Release(), result with
            | Error failure, Ok _ -> Error failure.Message
            | _, value -> value

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
        | "checkpoint" :: rest ->
            withArguments (fun parsed -> checkpointArgumentErrors parsed rest) rest (fun parsed ->
                locked root (fun () ->
                    FileGroupCheckpointRepository.checkpoint
                        root
                        (ProvenanceCommands.identityOverridesFrom rest)
                        (List.contains "--dry-run" rest)
                        (checkpointRequest root parsed actor))
                |> reported (renderCheckpoint (List.contains "--json" rest)))
        | _ ->
            eprintfn "Usage: ros %s" usage
            2
