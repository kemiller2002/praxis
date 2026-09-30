namespace Ros.Infrastructure.Planning

open System
open System.IO
open Ros.Contracts.Planning
open Ros.Domain.Planning

/// Reads and writes `.ros/work/group-membership.json`, the ledger of members
/// added to declared groups after declaration: who added each, when and why.
/// It touches no other file and no member's lifecycle state (PRX-GRP-002).
[<RequireQualifiedAccess>]
module GroupMembershipStore =
    [<Literal>]
    let RelativePath = ".ros/work/group-membership.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "group-membership.json")

    /// No ledger yet is an empty ledger, not an error.
    let read (root: string) : Result<MemberAddition list, string> =
        let file = path root

        if not (File.Exists file) then
            Ok []
        else
            WorkGroupMembershipJson.parseLedger (File.ReadAllText file)
            |> Result.mapError (fun message -> $"malformed {RelativePath}: {message}")

    /// The ledger's raw content, `None` when it does not exist, so a failed
    /// multi-file write can put it back exactly.
    let snapshot (root: string) : string option =
        let file = path root
        if File.Exists file then Some(File.ReadAllText file) else None

    let private replaceWith (root: string) (content: string) : Result<unit, string> =
        let file = path root
        let temporary = $"{file}.{Guid.NewGuid():N}.tmp"

        try
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            File.WriteAllText(temporary, content)
            File.Move(temporary, file, true)
            Ok()
        with error ->
            if File.Exists temporary then File.Delete temporary
            Error $"cannot write {RelativePath}: {error.Message}"

    let write (root: string) (additions: MemberAddition list) : Result<unit, string> =
        replaceWith root (WorkGroupMembershipJson.renderLedger additions)

    let restore (root: string) (previous: string option) : Result<unit, string> =
        match previous with
        | Some content -> replaceWith root content
        | None ->
            try
                File.Delete(path root)
                Ok()
            with error ->
                Error $"cannot remove {RelativePath}: {error.Message}"
