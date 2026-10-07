namespace Ros.Infrastructure.Planning

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Contracts.Planning
open Ros.Domain.Planning
open Ros.Domain.Work
open Ros.Infrastructure.Json
open Ros.Infrastructure.Work

/// Declared work groups on disk: `.ros/work/groups.json`, Praxis state of
/// its own. Reading it never touches the backlog, work context or event log,
/// and writing it touches nothing else, so a group operation cannot change a
/// member's lifecycle state, evidence or attribution. Writers hold the
/// `work-protocol` lock; each write replaces the file atomically.
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let relativePath = ".ros/work/groups.json"

    let path (root: string) = Path.Combine(root, ".ros", "work", "groups.json")

    let private queuePath (root: string) = Path.Combine(root, ".ros", "work", "queue.json")
    let private contextPath (root: string) = Path.Combine(root, ".ros", "context", "current.json")

    let private readObject (file: string) : JsonObject option =
        if File.Exists file then
            match JsonNode.Parse(File.ReadAllText file) with
            | :? JsonObject as value -> Some value
            | _ -> None
        else
            None

    let private text (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonValue as value when value.GetValueKind() = JsonValueKind.String -> Some(value.GetValue<string>())
        | _ -> None

    let private objects (node: JsonObject) (name: string) =
        match node[name] with
        | :? JsonArray as values -> values |> Seq.choose (function :? JsonObject as value -> Some value | _ -> None) |> Seq.toList
        | _ -> []

    /// The repository name the planner reports: the backlog's or work
    /// context's `repository`, else the checkout directory's name.
    let repositoryName (root: string) =
        [ queuePath root; contextPath root ]
        |> List.tryPick (fun file ->
            try
                readObject file |> Option.bind (fun document -> text document "repository")
            with _ ->
                None)
        |> Option.defaultValue (Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)))

    /// Every stored group; none when the file does not exist.
    let read (root: string) : Result<StoredWorkGroup list, string> =
        let file = path root

        if not (File.Exists file) then
            Ok []
        else
            try
                WorkGroupJson.parse (File.ReadAllText file) |> Result.mapError (fun message -> $"{relativePath}: {message}")
            with error ->
                Error $"{relativePath}: {error.Message}"

    /// Replaces the stored groups atomically (temporary file, then rename).
    let write (root: string) (groups: StoredWorkGroup list) : Result<unit, string> =
        let file = path root
        let temporary = Path.Combine(Path.GetDirectoryName file, $".groups.json.{Guid.NewGuid():N}.tmp")

        try
            Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
            File.WriteAllText(temporary, WorkGroupJson.render (WorkGroupJson.document groups), UTF8Encoding(false))
            File.Move(temporary, file, true)
            Ok()
        with error ->
            try
                if File.Exists temporary then File.Delete temporary
            with _ ->
                ()

            Error $"cannot write {relativePath}: {error.Message}"

    /// A group checkpoint's content-addressed ID, computed like a work
    /// event's: over every field but the ID.
    let checkpointId (checkpoint: GroupCheckpoint) =
        CanonicalJson.sha256HexPrefix 24 (CanonicalJson.serializeCompact (WorkGroupJson.checkpointBody checkpoint))

    /// Every tracked work item with its recorded state: the live state when
    /// it has a live record, otherwise its backlog status. `repositories`
    /// names items implemented elsewhere (`grouping.executionRepositories`).
    let catalog (root: string) (repositories: (string * string) list) : Result<WorkCatalog, string> =
        let repository = repositoryName root
        let located = Map.ofList repositories

        let item id state checkpoint =
            { Id = id
              State = state
              ExecutionRepository = located.TryFind id |> Option.defaultValue repository
              LatestCheckpointId = checkpoint }

        try
            let backlog =
                readObject (queuePath root)
                |> Option.map (fun queue ->
                    objects queue "items"
                    |> List.choose (fun node ->
                        match text node "id", text node "status" with
                        | Some id, Some status -> Some(id, item id (RecordedWorkState.Backlog status) None)
                        | _ -> None))
                |> Option.defaultValue []

            FileCheckpointRepository.readItems root
            |> Result.map (fun live ->
                let liveItems =
                    live
                    |> List.map (fun entry ->
                        let checkpoint =
                            match entry.LatestCheckpoint with
                            | Ok(Some recorded) -> Some recorded.CheckpointId
                            | _ -> None

                        entry.WorkItemId, item entry.WorkItemId (RecordedWorkState.Live entry.State) checkpoint)

                { Repository = repository
                  // A live record supersedes the backlog row it was promoted from.
                  Items = backlog @ liveItems |> Map.ofList })
        with error ->
            Error $"cannot read work items: {error.Message}"

    /// `validate`'s contribution: the stored document parses, every group is
    /// structurally sound, and every group checkpoint's ID matches its
    /// content. Path, field and message, like every other contributor.
    let validationFindings (root: string) : (string * string * string) list =
        match read root with
        | Error message -> [ relativePath, "groups", message ]
        | Ok [] -> []
        | Ok groups ->
            match catalog root [] with
            | Error message -> [ relativePath, "groups", message ]
            | Ok known ->
                let structural =
                    WorkGroups.validate known groups
                    |> List.map (fun finding -> relativePath, $"groups.{finding.GroupId}.{finding.Field}", finding.Message)

                let tampered =
                    groups
                    |> List.collect (fun group ->
                        group.Checkpoints
                        |> List.filter (fun checkpoint -> checkpointId checkpoint <> checkpoint.Id)
                        |> List.map (fun checkpoint ->
                            relativePath, $"groups.{group.Id}.checkpoints", $"checkpoint {checkpoint.Id} does not match its content; a recorded group checkpoint is never rewritten"))

                // A referenced member checkpoint must be that member's own
                // recorded checkpoint: the group never invents or replaces one.
                let dangling =
                    groups
                    |> List.collect (fun group ->
                        group.Checkpoints
                        |> List.collect (fun checkpoint ->
                            checkpoint.MemberCheckpoints
                            |> List.choose (fun reference ->
                                match reference.CheckpointId with
                                | Some id when not (FileCheckpointRepository.readHistory root reference.WorkItemId |> List.exists (fun event -> event.EventId = id)) ->
                                    Some(
                                        relativePath,
                                        $"groups.{group.Id}.checkpoints",
                                        $"checkpoint {checkpoint.Id} references {reference.WorkItemId} checkpoint {id}, which is not one of that item's recorded checkpoints"
                                    )
                                | _ -> None)))

                structural @ tampered @ dangling
