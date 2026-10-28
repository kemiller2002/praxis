namespace Praxis.Infrastructure.Autonomy

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open Praxis.Domain.Autonomy

/// File effects of the autonomy contracts: the trust store, revocations and
/// the durable merge journal (decision log, receipt chain, per-pull-request
/// phase and an append-only event log of every attempt, retry and outcome).
[<RequireQualifiedAccess>]
module AutonomyStore =
    let private full (path: string) = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)

    let private within (root: string) (path: string) =
        let r = full root + string Path.DirectorySeparatorChar
        (full path + string Path.DirectorySeparatorChar).StartsWith(r, StringComparison.Ordinal)

    /// Load the trust store, refusing one inside a repository the run edits:
    /// a trust anchor the agent can rewrite anchors nothing.
    let loadTrustStore (protectedRoots: string list) (path: string) : Result<TrustStore, string> =
        match protectedRoots |> List.tryFind (fun root -> within root path) with
        | Some root -> Error $"the trust store {path} is inside {root}, which the run can edit; keep it outside every repository"
        | None when not (File.Exists path) -> Error $"the trust store {path} does not exist"
        | None -> AutonomyJson.readTrustStore (File.ReadAllText path)

    /// Every readable revocation in the directories given. A revocation only
    /// removes authority, so an unreadable file is reported, never ignored
    /// silently, and a missing directory is simply empty.
    let readRevocations (directories: string list) : Set<string> * string list =
        directories
        |> List.filter Directory.Exists
        |> List.collect (fun dir -> Directory.GetFiles(dir, "*.json") |> Array.sort |> Array.toList)
        |> List.fold
            (fun (ids: Set<string>, problems) file ->
                match AutonomyJson.readRevocation (File.ReadAllText file) with
                | Ok revocation -> ids.Add revocation.RunId, problems
                | Error reason -> ids, problems @ [ $"{file}: {reason}" ])
            (Set.empty, [])

    let private atomicWrite (path: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + $".tmp-{Guid.NewGuid():N}"
        File.WriteAllText(temporary, content, UTF8Encoding false)
        File.Move(temporary, path, true)

    /// The journal of one run. `root` is a directory the coordinator keeps
    /// durably (committed and pushed with the run's Praxis state).
    type Journal =
        { Root: string }

        member journal.PhasePath (repository: string) (pull: int) =
            Path.Combine(journal.Root, "pulls", repository.Replace('/', '_'), $"pr-{pull}.json")

        member journal.EventsPath = Path.Combine(journal.Root, "events.jsonl")
        member journal.ChainPath = Path.Combine(journal.Root, "receipts", "chain.jsonl")

    let journal root = { Root = full root }

    let readPhase (j: Journal) repository pull : JournalPhase =
        let path = j.PhasePath repository pull
        if not (File.Exists path) then JournalPhase.NotStarted
        else
            let node = JsonNode.Parse(File.ReadAllText path)
            match node["phase"].GetValue<string>() with
            | "submitted" -> JournalPhase.Submitted(node["candidateSha"].GetValue<string>(), node["attempt"].GetValue<int>())
            | "completed" -> JournalPhase.Completed(node["mergeCommitSha"].GetValue<string>(), node["receiptDigest"].GetValue<string>())
            | other -> invalidOp $"unknown journal phase {other} in {path}"

    /// A field the journal recorded beside the phase (the submitted
    /// decision's digest), when present.
    let readField (j: Journal) repository pull (name: string) =
        let path = j.PhasePath repository pull
        if not (File.Exists path) then None
        else JsonNode.Parse(File.ReadAllText path)[name] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>())

    /// A stored decision by its content address.
    let readDecision (j: Journal) (digest: string) =
        let path = Path.Combine(j.Root, "decisions", digest.Replace("sha256:", "") + ".json")
        if File.Exists path then Some(JsonNode.Parse(File.ReadAllText path)) else None

    let writePhase (j: Journal) repository pull (phase: JournalPhase) (extra: (string * string) list) (at: DateTimeOffset) =
        let node = JsonObject()
        node["schema"] <- JsonValue.Create "praxis.merge-journal/1"
        node["repository"] <- JsonValue.Create(repository: string)
        node["pullRequest"] <- JsonValue.Create(pull: int)

        match phase with
        | JournalPhase.NotStarted -> node["phase"] <- JsonValue.Create "not-started"
        | JournalPhase.Submitted(candidate, attempt) ->
            node["phase"] <- JsonValue.Create "submitted"
            node["candidateSha"] <- JsonValue.Create candidate
            node["attempt"] <- JsonValue.Create attempt
        | JournalPhase.Completed(commit, digest) ->
            node["phase"] <- JsonValue.Create "completed"
            node["mergeCommitSha"] <- JsonValue.Create commit
            node["receiptDigest"] <- JsonValue.Create digest

        extra |> List.iter (fun (k, v) -> node[k] <- JsonValue.Create v)
        node["updatedAt"] <- JsonValue.Create(Authorization.utc at)
        atomicWrite (j.PhasePath repository pull) (AutonomyJson.pretty node)

    /// Append one event. The event log is append-only; it is the record of
    /// every attempt, retry and outcome.
    let appendEvent (j: Journal) (at: DateTimeOffset) (kind: string) (fields: (string * string) list) =
        let node = JsonObject()
        node["at"] <- JsonValue.Create(Authorization.utc at)
        node["event"] <- JsonValue.Create kind
        fields |> List.iter (fun (k, v) -> node[k] <- JsonValue.Create v)
        Directory.CreateDirectory(j.Root) |> ignore
        File.AppendAllText(j.EventsPath, node.ToJsonString() + "\n", UTF8Encoding false)

    /// Store a decision under its content address and return the address.
    let recordDecision (j: Journal) (decision: Decision) =
        let node = AutonomyJson.decisionNode decision
        let digest = AutonomyJson.digest node
        let path = Path.Combine(j.Root, "decisions", digest.Replace("sha256:", "") + ".json")
        if not (File.Exists path) then atomicWrite path (AutonomyJson.pretty node)
        digest

    let lastReceiptDigest (j: Journal) =
        if File.Exists j.ChainPath then
            File.ReadAllLines j.ChainPath
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.tryLast
            |> Option.map (fun line ->
                let link = JsonNode.Parse line
                link["digest"].GetValue<string>())
        else None

    /// Store a receipt under its content address and chain it.
    let recordReceipt (j: Journal) (receipt: MergeReceipt) =
        let node = AutonomyJson.receiptNode receipt
        let digest = AutonomyJson.digest node
        atomicWrite (Path.Combine(j.Root, "receipts", digest.Replace("sha256:", "") + ".json")) (AutonomyJson.pretty node)
        let link = JsonObject()
        link["digest"] <- JsonValue.Create digest
        link["previous"] <- (match receipt.PreviousReceiptDigest with Some p -> JsonValue.Create p :> JsonNode | None -> null)
        link["repository"] <- JsonValue.Create receipt.Repository
        link["pullRequest"] <- JsonValue.Create receipt.PullRequest
        File.AppendAllText(j.ChainPath, link.ToJsonString() + "\n", UTF8Encoding false)
        digest

    /// Every receipt in chain order.
    let readReceipts (j: Journal) : JsonNode list =
        if not (File.Exists j.ChainPath) then []
        else
            File.ReadAllLines j.ChainPath
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.toList
            |> List.choose (fun line ->
                let link = JsonNode.Parse line
                let path = Path.Combine(j.Root, "receipts", link["digest"].GetValue<string>().Replace("sha256:", "") + ".json")
                if File.Exists path then Some(JsonNode.Parse(File.ReadAllText path)) else None)

    /// Re-derive every receipt's address and check the chain links: a
    /// removed, reordered or edited receipt is reported.
    let verifyChain (j: Journal) : Result<int, string list> =
        if not (File.Exists j.ChainPath) then Ok 0
        else
            let links = File.ReadAllLines j.ChainPath |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.map JsonNode.Parse |> Array.toList

            let problems =
                links
                |> List.mapi (fun index link ->
                    let digest = link["digest"].GetValue<string>()
                    let path = Path.Combine(j.Root, "receipts", digest.Replace("sha256:", "") + ".json")
                    let expectedPrevious = if index = 0 then None else (let previous = links[index - 1] in Some(previous["digest"].GetValue<string>()))
                    [ if not (File.Exists path) then $"receipt {digest} is missing"
                      else
                          let stored = JsonNode.Parse(File.ReadAllText path)
                          if AutonomyJson.digest stored <> digest then $"receipt {digest} was modified"
                          let previous = stored["previousReceiptDigest"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>())
                          if previous <> expectedPrevious then $"receipt {digest} does not chain to its predecessor" ])
                |> List.concat

            if problems.IsEmpty then Ok links.Length else Error problems
