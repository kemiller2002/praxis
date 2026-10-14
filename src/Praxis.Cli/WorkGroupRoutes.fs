namespace Praxis.Cli

/// Routes `work group VERB` (PRX-GRP-110) to its module, so the family's
/// verbs are dispatched in one place. There is deliberately no `complete`
/// verb: a group has no completion transition (PRX-GRP-116).
[<RequireQualifiedAccess>]
module WorkGroupRoutes =
    let verbs = set [ "show"; "list"; "link"; "add"; "remove"; "checkpoint"; "create" ]

    let run (root: string) (verb: string) (rest: string list) : int =
        let withActor action = ProvenanceCommands.withResolvedActor rest action

        match verb with
        | "show" -> WorkGroupShowCommands.run root rest
        | "list" -> WorkGroupListCommands.run root rest
        | "link" -> withActor (WorkGroupLinkCommands.run root rest)
        | "add" -> withActor (WorkGroupCommands.add root rest)
        | "remove" -> withActor (WorkGroupCommands.remove root rest)
        | "checkpoint" -> withActor (WorkGroupCheckpointCommands.checkpoint root rest)
        | _ -> withActor (WorkGroupCommands.create root rest)
