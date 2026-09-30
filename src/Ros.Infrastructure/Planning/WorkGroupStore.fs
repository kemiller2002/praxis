namespace Ros.Infrastructure.Planning

open System
open System.IO
open Ros.Contracts.Planning
open Ros.Domain.Planning

/// Reads and writes `.ros/work/groups.json`, the durable store of
/// human-declared execution groups. It touches no other file: declaring a
/// group never changes a member's lifecycle state (PRX-GRP-002).
[<RequireQualifiedAccess>]
module WorkGroupStore =
    [<Literal>]
    let RelativePath = ".ros/work/groups.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    /// No store yet is an empty store, not an error.
    let read (root: string) : Result<StoredGroupDeclaration list, string> =
        let file = path root

        if not (File.Exists file) then
            Ok []
        else
            WorkGroupJson.parseStore (File.ReadAllText file)
            |> Result.mapError (fun message -> $"malformed {RelativePath}: {message}")

    let write (root: string) (entries: StoredGroupDeclaration list) : Result<unit, string> =
        let file = path root
        let temporary = $"{file}.{Guid.NewGuid():N}.tmp"

        try
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            File.WriteAllText(temporary, WorkGroupJson.renderStore entries)
            File.Move(temporary, file, true)
            Ok()
        with error ->
            if File.Exists temporary then File.Delete temporary
            Error $"cannot write {RelativePath}: {error.Message}"
