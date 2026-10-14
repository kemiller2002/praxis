namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text.Json.Nodes
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// The binding between a prepared artifact and the worker workspace made for
/// it, kept host-side next to the prepared artifact.
type WorkspaceRecord = { Workspace: string; CacheKey: string; HandoffSha256: string }

/// What a worker may hand back: the slot files to apply (staged host-side) and
/// the dispositions, which are recorded and never applied.
type WorkspaceSubmission = { Staged: string; Dispositions: string option; Denied: (string * string) list }

/// The worker workspace (CONSTRUCT-010): the only directory a construction
/// worker is given. It holds `HANDOFF.md` and an `outbox/`; every host
/// artifact (prepared plan, decision records, digests, cache, policy, the
/// governed repository) stays outside it. The layout rule is checked before
/// anything is written, so a workspace that would expose a host artifact, or
/// that lies inside a host location, is never created.
[<RequireQualifiedAccess>]
module ConstructionWorkspace =
    [<Literal>]
    let Schema = "praxis.construction-workspace/1"

    [<Literal>]
    let HandoffName = "HANDOFF.md"

    [<Literal>]
    let OutboxName = "outbox"

    [<Literal>]
    let DispositionsName = "dispositions.json"

    let private full (path: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    /// Whether `inner` is `outer` or lies under it (ordinal, on full paths).
    let within (outer: string) (inner: string) =
        let o, i = full outer, full inner
        i = o || i.StartsWith(o + string Path.DirectorySeparatorChar, StringComparison.Ordinal)

    /// Every way the workspace and the named host locations overlap: a host
    /// location inside the workspace exposes it to the worker; a workspace
    /// inside a host location lets the worker write there.
    let layoutProblems (workspace: string) (hostLocations: (string * string) list) : string list =
        hostLocations
        |> List.choose (fun (name, location) ->
            if within workspace location then Some(sprintf "the host %s (%s) would be inside the workspace" name location)
            elif within location workspace then Some(sprintf "the workspace would be inside the host %s (%s)" name location)
            else None)

    /// The host locations a workspace must stay clear of, by name.
    let hostLocations (configuration: LocalHostConfiguration) (hostArtifacts: string list) : (string * string) list =
        [ yield "configuration directory", Path.GetDirectoryName(full configuration.Path)
          yield "repository", configuration.RepositoryRoot
          yield "policy root", configuration.PolicyRoot
          yield "journal root", configuration.JournalRoot
          yield "spool root", configuration.SpoolRoot
          yield "submission root", configuration.SubmissionRoot
          yield "integration root", configuration.IntegrationRoot
          yield "evidence root", configuration.EvidenceRoot
          yield "checkout root", configuration.CheckoutRoot
          match configuration.ConstructionRoot with Some root -> yield "construction root", root | None -> ()
          for artifact in hostArtifacts do yield "artifact", artifact ]
        |> List.filter (fun (_, location) -> not (String.IsNullOrWhiteSpace location))

    let recordPath (preparedPath: string) =
        let path = full preparedPath
        (if path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then path.Substring(0, path.Length - 5) else path) + ".workspace.json"

    let private sha256 (text: string) = ConstructionTree.digest text

    /// A fresh workspace: absent, or an empty real directory, under an
    /// existing parent with no link on the way.
    let private fresh (workspace: string) =
        let path = full workspace
        let parent = Path.GetDirectoryName path
        if isNull parent || not (Directory.Exists parent) then Error("workspace parent does not exist: " + workspace)
        elif not (LocalHostJournalFiles.noLinks parent) then Error("workspace lies under a symbolic link: " + workspace)
        elif File.Exists path then Error("workspace is a file: " + workspace)
        elif Directory.Exists path && not (isNull (DirectoryInfo path).LinkTarget) then Error("workspace is a symbolic link: " + workspace)
        elif Directory.Exists path && not (Seq.isEmpty (Directory.EnumerateFileSystemEntries path)) then Error("workspace is not empty: " + workspace)
        else Ok path

    /// Create the workspace (HANDOFF.md and an empty outbox) and its host-side
    /// record, after the layout rule has passed.
    let create (configuration: LocalHostConfiguration) (preparedPath: string) (hostArtifacts: string list) (workspace: string) (cacheKey: string) (handoffMarkdown: string) =
        let record = recordPath preparedPath
        match layoutProblems workspace (hostLocations configuration (record :: hostArtifacts)) with
        | problems when not problems.IsEmpty -> Error("workspace refused: " + String.concat "; " problems)
        | _ ->
            fresh workspace |> Result.bind (fun path ->
                try
                    Directory.CreateDirectory path |> ignore
                    File.WriteAllText(Path.Combine(path, HandoffName), handoffMarkdown, Text.UTF8Encoding(false))
                    Directory.CreateDirectory(Path.Combine(path, OutboxName)) |> ignore
                    let node = JsonObject()
                    node["schema"] <- JsonValue.Create Schema
                    node["workspace"] <- JsonValue.Create path
                    node["cacheKey"] <- JsonValue.Create cacheKey
                    node["handoffSha256"] <- JsonValue.Create(sha256 handoffMarkdown)
                    File.WriteAllText(record, node.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n", Text.UTF8Encoding(false))
                    Ok({ Workspace = path; CacheKey = cacheKey; HandoffSha256 = sha256 handoffMarkdown }, record)
                with error -> Error("workspace not written: " + error.Message))

    /// The host-side record binding `preparedPath` to its workspace.
    let readRecord (preparedPath: string) : Result<WorkspaceRecord, string> =
        let path = recordPath preparedPath
        try
            if not (File.Exists path) then Error "no workspace was prepared for this artifact (prepare with --workspace)"
            else
                match JsonNode.Parse(File.ReadAllText path) with
                | :? JsonObject as node ->
                    let text (name: string) = match node[name] with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None
                    match text "schema", text "workspace", text "cacheKey", text "handoffSha256" with
                    | Some Schema, Some workspace, Some key, Some digest -> Ok { Workspace = workspace; CacheKey = key; HandoffSha256 = digest }
                    | _ -> Error "workspace record is not a praxis.construction-workspace/1 record"
                | _ -> Error "workspace record is not a JSON object"
        with error -> Error("workspace record unreadable: " + error.Message)

    /// Judge the outbox without following links and stage its regular files
    /// (other than the dispositions) into a fresh host directory, so the bytes
    /// judged are the bytes applied. Anything that is not a regular file is a
    /// denied write; the dispositions are read and never applied.
    let submission (record: WorkspaceRecord) (workspace: string) (stagingParent: string) : Result<WorkspaceSubmission, string> =
        if full workspace <> full record.Workspace then Error("workspace differs from the one prepared for this artifact: " + workspace)
        else
            let outbox = Path.Combine(full workspace, OutboxName)
            try
                if not (Directory.Exists outbox) then Error "workspace has no outbox"
                elif not (LocalHostJournalFiles.noLinks outbox) then Error "workspace outbox is or lies under a symbolic link"
                else
                    let entries = Directory.GetFileSystemEntries outbox |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b)) |> Array.toList
                    let regular (entry: string) =
                        let info = FileInfo entry
                        isNull info.LinkTarget && info.Exists && not (Directory.Exists entry)
                        && not (info.Attributes.HasFlag FileAttributes.ReparsePoint) && not (info.Attributes.HasFlag FileAttributes.Device)
                    let denied =
                        entries |> List.filter (regular >> not) |> List.map (fun entry ->
                            entry, Path.GetFileName entry + ": not a regular file; the outbox holds only regular files")
                    let dispositions =
                        entries |> List.tryFind (fun entry -> Path.GetFileName entry = DispositionsName && regular entry) |> Option.map File.ReadAllText
                    let staged = Path.Combine(stagingParent, "outbox")
                    Directory.CreateDirectory staged |> ignore
                    entries
                    |> List.filter (fun entry -> regular entry && Path.GetFileName entry <> DispositionsName)
                    |> List.iter (fun entry -> File.WriteAllBytes(Path.Combine(staged, Path.GetFileName entry), File.ReadAllBytes entry))
                    Ok { Staged = staged; Dispositions = dispositions; Denied = denied }
            with error -> Error("workspace outbox unreadable: " + error.Message)
