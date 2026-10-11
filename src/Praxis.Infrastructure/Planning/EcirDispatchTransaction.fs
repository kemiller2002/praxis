namespace Praxis.Infrastructure.Planning

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// State rendered in isolation by the normal work/group planners. The host
/// must hold both repository locks and its policy lock until prepare succeeds.
type EcirDispatchWrite =
    { Path: string
      BeforeSha256: string option
      Content: string }

[<RequireQualifiedAccess>]
module EcirDispatchTransaction =
    let hash (bytes: byte array) = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let hashText (text: string) = Encoding.UTF8.GetBytes text |> hash
    let private currentHash path = if File.Exists path then File.ReadAllBytes path |> hash |> Some else None

    let private safePath (root: string) (relative: string) =
        let segments = relative.Split('/')
        let mutable path = Path.GetFullPath root
        not (String.IsNullOrWhiteSpace relative || Path.IsPathRooted relative || relative.Contains('\\'))
        && (segments |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> ".." && not (segment.Contains ':')))
        && not (segments |> Array.exists (fun segment ->
            path <- Path.Combine(path, segment)
            // LinkTarget also detects dangling links, unlike File.Exists.
            (FileInfo(path).LinkTarget <> null || DirectoryInfo(path).LinkTarget <> null)))
        && (relative = ".ros/context/current.json"
            || relative = ".ros/events/events.jsonl"
            || relative = ".ros/work/groups.json"
            || (segments.Length = 4 && segments[0] = ".ros" && segments[1] = "telemetry"
                && segments[2] = "executions" && segments[3].EndsWith(".json", StringComparison.Ordinal))
            || (segments.Length = 4 && segments[0] = ".ros" && segments[1] = "executions"
                && (segments[3] = "envelope.json" || segments[3] = "events.jsonl"))
            || (segments.Length = 4 && segments[0] = ".ros" && segments[1] = "work"
                && segments[2] = "ecir-dispatches" && segments[3].EndsWith(".json", StringComparison.Ordinal)))

    let private writeAtomic (path: string) (content: string) (replace: bool) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            use output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            let bytes = Encoding.UTF8.GetBytes content
            output.Write(bytes, 0, bytes.Length)
            output.Flush(true)
            output.Dispose()
            File.Move(temporary, path, replace)
        finally
            if File.Exists temporary then File.Delete temporary

    let private pathsValid root (writes: EcirDispatchWrite list) =
        let paths = writes |> List.map _.Path
        let expected = Set.ofList [ ".ros/context/current.json"; ".ros/events/events.jsonl"; ".ros/work/groups.json" ]
        writes.Length >= 4 && writes.Length <= 64
        && paths.Length = (Set.ofList paths).Count
        && Set.isSubset expected (Set.ofList paths)
        && (paths |> List.filter (fun path -> path.StartsWith(".ros/work/ecir-dispatches/", StringComparison.Ordinal))).Length = 1
        && (paths |> List.forall (safePath root))

    /// journalPath is provisioned by the protected HOST, outside the agent's
    /// repository. This module does not establish trust in that path. Neither
    /// a repo-authored journal nor a callback alone authorizes any work.
    let prepare (root: string) (journalPath: string) (writes: EcirDispatchWrite list) =
        try
            if not (Path.IsPathFullyQualified journalPath)
               || Path.GetFullPath(journalPath).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath root) + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
                Error "ECIR journal must be provisioned outside the repository by the host"
            elif File.Exists journalPath then Error "ECIR dispatch already has a pending host journal"
            elif not (pathsValid root writes) then Error "ECIR dispatch write set is incomplete or unsafe"
            elif writes |> List.exists (fun write -> currentHash (Path.Combine(root, write.Path)) <> write.BeforeSha256) then
                Error "ECIR repository changed before transaction preparation"
            else
                let serialized =
                    JsonSerializer.Serialize
                        {| schemaVersion = "ecir.dispatch-transaction/1"
                           repositoryRoot = Path.GetFullPath root
                           writes = writes |> List.map (fun write ->
                               {| path = write.Path
                                  beforeSha256 = write.BeforeSha256 |> Option.toObj
                                  afterSha256 = hashText write.Content
                                  content = write.Content |}) |}
                // CreateNew reserves the final name atomically. Rename with
                // overwrite=false can race on Unix (check followed by rename).
                // The caller's host lock excludes recovery until the flush.
                // An interrupted partial preparation is retained and fails
                // closed; no repository target has been written at this point.
                Directory.CreateDirectory(Path.GetDirectoryName journalPath) |> ignore
                use output = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                let bytes = Encoding.UTF8.GetBytes serialized
                output.Write(bytes, 0, bytes.Length)
                output.Flush(true)
                Ok()
        with e -> Error("cannot prepare ECIR host transaction: " + e.Message)

    let private exactFields (names: string list) (entry: JsonElement) =
        let actual = entry.EnumerateObject() |> Seq.map _.Name |> Seq.toList
        if actual.Length <> names.Length || Set.ofList actual <> Set.ofList names then
            invalidOp "ECIR journal fields are missing, unexpected or duplicated"

    /// Recovery is a continuation of the accepted transaction, not another
    /// authorization or work-begin. Call only under the same protected host and
    /// repository locks. The optional hook supports deterministic crash tests.
    let recover (root: string) (journalPath: string) (afterWrite: int -> unit) =
        try
            if not (File.Exists journalPath) then Ok()
            else
                use doc = JsonDocument.Parse(File.ReadAllText journalPath)
                let entry = doc.RootElement
                exactFields [ "schemaVersion"; "repositoryRoot"; "writes" ] entry
                if entry.GetProperty("schemaVersion").GetString() <> "ecir.dispatch-transaction/1"
                   || entry.GetProperty("repositoryRoot").GetString() <> Path.GetFullPath root then
                    Error "ECIR host journal version or repository identity differs"
                else
                    let writes =
                        entry.GetProperty("writes").EnumerateArray()
                        |> Seq.map (fun item ->
                            exactFields [ "path"; "beforeSha256"; "afterSha256"; "content" ] item
                            let before = item.GetProperty "beforeSha256"
                            let write =
                                { Path = item.GetProperty("path").GetString()
                                  BeforeSha256 = if before.ValueKind = JsonValueKind.Null then None else Some(before.GetString())
                                  Content = item.GetProperty("content").GetString() }
                            if hashText write.Content <> item.GetProperty("afterSha256").GetString() then
                                invalidOp "ECIR journal content hash differs"
                            write)
                        |> Seq.toList
                    if not (pathsValid root writes) then Error "ECIR host journal write set is unsafe"
                    elif writes |> List.exists (fun write ->
                        let current = currentHash (Path.Combine(root, write.Path))
                        current <> write.BeforeSha256 && current <> Some(hashText write.Content)) then
                        Error "ECIR transaction target changed after preparation; recovery refused"
                    else
                        // Check every target before replay: a conflict on a late
                        // file must not modify an earlier target.
                        writes |> List.iteri (fun index write ->
                            let target = Path.Combine(root, write.Path)
                            if currentHash target <> Some(hashText write.Content) then writeAtomic target write.Content true
                            afterWrite index)
                        File.Delete journalPath
                        Ok()
        with e -> Error("ECIR transaction remains pending: " + e.Message)
