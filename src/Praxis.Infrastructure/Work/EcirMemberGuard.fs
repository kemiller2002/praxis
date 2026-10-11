namespace Praxis.Infrastructure.Work

open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Planning
open Praxis.Contracts.Work
open Praxis.Domain.Planning
open Praxis.Domain.Work

/// Shared by native begin and runtime-free reconciliation. No host authority
/// is inferred from mutable repository configuration.
[<RequireQualifiedAccess>]
module EcirMemberGuard =
    let private readConfiguration root =
        try
            let configuration =
                let path = Path.Combine(root, "ros.json")
                if not (File.Exists path) then Ok PlannerConfiguration.defaults
                else
                    match JsonNode.Parse(File.ReadAllText path) with
                    | :? JsonObject as document ->
                        match document["planner"] with
                        | null -> Ok PlannerConfiguration.defaults
                        | :? JsonObject as planner -> PlanningJson.parseConfiguration (planner.ToJsonString())
                        | _ -> Error "ros.json planner must be an object"
                    | _ -> Error "ros.json must be an object"
            let path = Path.Combine(root, ".ros", "work", "groups.json")
            let stored =
                if not (File.Exists path) then Ok []
                else WorkGroupJson.readGroupStore (File.ReadAllText path) |> Result.map _.Groups
            configuration |> Result.bind (fun config ->
                stored |> Result.map (fun groups ->
                    { config with Grouping = { config.Grouping with Groups = config.Grouping.Groups @ (groups |> List.map _.Declaration) } }))
        with error -> Error $"cannot read ECIR membership: {error.Message}"

    /// The direct work-begin route must not bypass the ECIR group guard.
    /// Call under work-protocol; group mutations use that same lock first.
    let refuseUngovernedMembers (root: string) (memberIds: string list) =
        readConfiguration root
        |> Result.bind (fun configuration ->
            let repository = FileWorkConfigRepository.readRepositoryId root
            let blocked =
                configuration.Grouping.Groups
                |> List.filter (fun group -> EcirGates.isEcirGroup group.SharedContext)
                |> List.collect (fun group ->
                    group.Members |> List.choose (fun reference ->
                        let local =
                            match MemberReference.parse reference with
                            | MemberReference.Local id -> Some id
                            | MemberReference.Qualified(home, id) when home = repository -> Some id
                            | _ -> None
                        local |> Option.filter (fun id -> List.contains id memberIds)))
                |> List.distinct
            if blocked.IsEmpty then Ok()
            else Error("ECIR member work begin refused: protected host dispatch is not integrated for " + String.concat ", " blocked))

