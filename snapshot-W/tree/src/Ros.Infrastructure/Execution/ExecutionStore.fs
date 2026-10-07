namespace Ros.Infrastructure.Execution

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Ros.Contracts.Execution
open Ros.Domain.Execution

/// Durable execution state under `.ros/executions/<execution-id>/`:
/// `envelope.json` is the materialized current envelope; `events.jsonl` is
/// the append-only ledger (step entries, transitions, scope resolutions).
/// A resumed execution is reconstructed from these files alone, never from
/// a conversation.
[<RequireQualifiedAccess>]
module ExecutionStore =
    let directory (root: string) = Path.Combine(root, ".ros", "executions")
    let private executionDir root (id: string) = Path.Combine(directory root, id)
    let private envelopePath root id = Path.Combine(executionDir root id, "envelope.json")
    let private eventsPath root id = Path.Combine(executionDir root id, "events.jsonl")

    /// A new execution identity: `EXE-<utc stamp>-<random>`. A competing
    /// attempt at the same work item always gets a new one (ORD-EXEC-074).
    let newId (now: DateTimeOffset) =
        let stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'")
        let suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes 4).ToLowerInvariant()
        $"EXE-{stamp}-{suffix}"

    let exists root id = File.Exists(envelopePath root id)

    let private writeAtomically (path: string) (text: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
        let temp = path + $".{Guid.NewGuid():N}.tmp"
        File.WriteAllText(temp, text)
        File.Move(temp, path, true)

    let saveEnvelope root (envelope: ExecutionEnvelope) =
        writeAtomically (envelopePath root envelope.ExecutionId) ((ExecutionJson.envelope envelope).ToJsonString ExecutionJson.options + "\n")

    let loadEnvelope root id : Result<ExecutionEnvelope, string> =
        let path = envelopePath root id

        if not (File.Exists path) then
            Error $"execution {id} not found"
        else
            match JsonNode.Parse(File.ReadAllText path) with
            | null -> Error $"execution {id} envelope is empty"
            | node -> ExecutionJson.readEnvelope node

    let list root =
        let dir = directory root

        if Directory.Exists dir then
            Directory.GetDirectories dir |> Array.map Path.GetFileName |> Array.choose Option.ofObj |> Array.sort |> Array.toList
        else
            []

    /// Append one ledger line. Existing lines are never rewritten.
    let private appendLine root id (node: JsonNode) =
        let path = eventsPath root id
        Directory.CreateDirectory(Path.GetDirectoryName path |> Option.ofObj |> Option.defaultValue ".") |> ignore
        File.AppendAllText(path, node.ToJsonString ExecutionJson.compact + "\n")

    let appendEntry root id (entry: StepEntry) = appendLine root id (ExecutionJson.entry entry)

    let appendRecord root id (kind: string) (fields: (string * string option) list) (at: DateTimeOffset) =
        let node = JsonObject()
        node["entry"] <- JsonValue.Create kind
        fields |> List.iter (fun (k, v) -> node[k] <- (v |> Option.map (fun s -> JsonValue.Create s :> JsonNode) |> Option.toObj))
        node["at"] <- JsonValue.Create(ExecutionJson.timestamp at)
        appendLine root id node

    let private lines root id =
        let path = eventsPath root id

        if File.Exists path then
            File.ReadAllLines path |> Array.filter (fun l -> l.Trim().Length > 0) |> Array.toList
        else
            []

    let private parseLine (line: string) = JsonNode.Parse line |> Option.ofObj

    let private kindOf (node: JsonNode) =
        match node["entry"] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> s
            | _ -> ""
        | _ -> ""

    /// Step entries, in append order.
    let readEntries root id : Result<StepEntry list, string> =
        lines root id
        |> List.choose parseLine
        |> List.filter (fun n -> List.contains (kindOf n) [ "declared"; "started"; "observed"; "reconciled" ])
        |> List.fold (fun acc n -> acc |> Result.bind (fun xs -> ExecutionJson.readEntry n |> Result.map (fun e -> e :: xs))) (Ok [])
        |> Result.map List.rev

    /// Non-step records (transitions, scope resolutions, expansions).
    let readRecords root id kind =
        lines root id |> List.choose parseLine |> List.filter (fun n -> kindOf n = kind)

    let private field (name: string) (node: JsonNode) =
        match node[name] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None

    let resolvedResources root id =
        readRecords root id "scope-resolved" |> List.choose (field "resource") |> Set.ofList

    let recordField = field

/// Git adapter for workspace binding and boundary observation. A worktree is
/// an isolation *mechanism*, never a security sandbox (ORD-EXEC-032).
[<RequireQualifiedAccess>]
module GitWorkspace =
    let private run (cwd: string) (arguments: string list) =
        let info = ProcessStartInfo("git", arguments)
        info.WorkingDirectory <- cwd
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false

        try
            use p = Process.Start info
            let out = p.StandardOutput.ReadToEnd()
            let err = p.StandardError.ReadToEnd()
            p.WaitForExit()
            if p.ExitCode = 0 then Ok(out.Trim()) else Error(err.Trim())
        with ex ->
            Error ex.Message

    let head cwd = run cwd [ "rev-parse"; "HEAD" ]
    let resolve cwd (revision: string) = run cwd [ "rev-parse"; "--verify"; revision + "^{commit}" ]
    let remoteUrl cwd = run cwd [ "remote"; "get-url"; "origin" ] |> Result.toOption

    /// Create `branch` at `baseline` in a new worktree at `path`.
    let createWorktree cwd (branch: string) (path: string) (baseline: string) =
        run cwd [ "worktree"; "add"; "-b"; branch; path; baseline ]

    let removeWorktree cwd (path: string) = run cwd [ "worktree"; "remove"; path ]

    /// Resources mutated since the baseline: committed changes plus the
    /// working tree (including untracked files) of the workspace.
    let changedPaths cwd (baseline: string) =
        run cwd [ "diff"; "--name-only"; baseline ]
        |> Result.bind (fun committedAndTracked ->
            run cwd [ "ls-files"; "--others"; "--exclude-standard" ]
            |> Result.map (fun untracked ->
                (committedAndTracked.Split('\n') |> Array.toList) @ (untracked.Split('\n') |> Array.toList)
                |> List.map _.Trim()
                |> List.filter (fun p -> p.Length > 0)
                |> List.distinct
                |> List.sort))

    /// Whether the workspace has changes no commit captures (Praxis state
    /// under `.ros/` excluded).
    let hasUncommittedChanges cwd =
        run cwd [ "status"; "--porcelain" ]
        |> Result.map (fun out ->
            out.Split('\n')
            |> Array.map (fun l -> if l.Length > 3 then l.Substring(3).Trim() else "")
            |> Array.exists (fun p -> p.Length > 0 && not (p.StartsWith(".ros/", StringComparison.Ordinal))))
        |> Result.defaultValue false

    /// Content digest of a file, as evaluator-closure evidence.
    let fileDigest (path: string) =
        if File.Exists path then
            use stream = File.OpenRead path
            Ok("sha256:" + Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant())
        else
            Error $"{path} does not exist"

    let relativeTo (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')
