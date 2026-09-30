namespace Ros.Infrastructure.Planning

open System.IO
open Ros.Contracts.Planning
open Ros.Domain.Planning

/// `.ros/work/groups.json`: human-declared execution groups (PRX-GRP-073).
/// An absent file means no group has been declared.
[<RequireQualifiedAccess>]
module FileWorkGroupStore =
    let relativePath = ".ros/work/groups.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    let read (root: string) : Result<StoredGroup list, string> =
        let file = path root

        if File.Exists file then
            PlanningJson.parseStoredGroups (File.ReadAllText file) |> Result.mapError (fun message -> $"{relativePath}: {message}")
        else
            Ok []
