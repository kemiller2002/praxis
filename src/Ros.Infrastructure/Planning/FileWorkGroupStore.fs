namespace Ros.Infrastructure.Planning

open System.IO
open System.Text
open Ros.Contracts.Planning
open Ros.Domain.Planning

/// `.ros/work/groups.json`: declared execution groups (PRX-GRP-073 phase
/// two). A missing file is an empty store. Writes replace the file
/// atomically (temporary file, then rename) and touch nothing else.
[<RequireQualifiedAccess>]
module FileWorkGroupStore =
    let path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    let read (root: string) : Result<GroupStore, string> =
        let file = path root

        if not (File.Exists file) then
            Ok GroupStore.empty
        else
            try
                PlanningJson.parseGroupStore (File.ReadAllText file) |> Result.mapError (fun message -> $"{file}: {message}")
            with error ->
                Error $"cannot read {file}: {error.Message}"

    let write (root: string) (store: GroupStore) : Result<unit, string> =
        let file = path root
        let temporary = file + ".tmp"

        try
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            File.WriteAllText(temporary, PlanningJson.renderGroupStore store, UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            Error $"cannot write {file}: {error.Message}"

    let declarations (root: string) : Result<DeclaredGroup list, string> = read root |> Result.map GroupStore.declarations
