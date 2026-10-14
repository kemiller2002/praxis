namespace Praxis.Infrastructure.Construction

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open Praxis.Application.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// How one check process runs. `Containment` names the mechanism the runner
/// applies. Production uses the qualified Linux profile; tests may inject a
/// direct runner, which is never reachable from the CLI.
type ConstructionCheckRunner =
    { Containment: string
      Run: LocalWorkerProcessSpec -> Result<LocalControllerToolObservation, string> }

/// One file of the checkout, as observed without following links.
type CheckoutEntry =
    | FileEntry of sha256: string
    | LinkEntry of target: string

/// `praxis construct check` (CONSTRUCT-010): runs one host-approved check
/// command (`constructionChecks` in the host configuration) on a fresh
/// detached checkout of a candidate built from a prepared artifact. It
/// observes the outcome, keeps stdout and stderr as host evidence, and
/// refuses any write the check makes to the candidate outside build output.
/// It is agent feedback only: never acceptance evidence, never a substitute
/// for Dokimos.
[<RequireQualifiedAccess>]
module ConstructionCheck =
    [<Literal>]
    let Schema = "praxis.construction-check/1"

    [<Literal>]
    let Notice = "Agent feedback only. Not acceptance evidence; independent verification (Dokimos) is unaffected."

    /// Build-output directories a check may write anywhere in the checkout.
    let OutputDirectories = set [ "bin"; "obj"; "TestResults" ]

    let private tail (limit: int) (text: string) = if text.Length <= limit then text else text.Substring(text.Length - limit)

    // ------------------------------------------------------------- pure

    /// The check's verdict from the observed process and the writes it made.
    /// An exit above 128 on a POSIX host is a death by signal (128 + N).
    let classify (posix: bool) (outcome: LocalWorkerProcessOutcome) (unauthorized: string list) : string * int option * int option =
        match outcome with
        | _ when not unauthorized.IsEmpty -> "unauthorizedWrite", (match outcome with LocalWorkerProcessOutcome.Exited code -> Some code | _ -> None), None
        | LocalWorkerProcessOutcome.Exited 0 -> "passed", Some 0, None
        | LocalWorkerProcessOutcome.Exited code when posix && code > 128 && code < 160 -> "crashed", Some code, Some(code - 128)
        | LocalWorkerProcessOutcome.Exited code -> "failed", Some code, None
        | LocalWorkerProcessOutcome.TimedOut -> "timedOut", None, None
        | LocalWorkerProcessOutcome.OutputLimitExceeded -> "outputLimitExceeded", None, None
        | LocalWorkerProcessOutcome.Cancelled -> "cancelled", None, None
        | LocalWorkerProcessOutcome.PreflightRefused _
        | LocalWorkerProcessOutcome.StartUncertain _
        | LocalWorkerProcessOutcome.ObservationFailed _
        | LocalWorkerProcessOutcome.StreamFailed _ -> "error", None, None

    let private detail (outcome: LocalWorkerProcessOutcome) =
        match outcome with
        | LocalWorkerProcessOutcome.PreflightRefused reason
        | LocalWorkerProcessOutcome.StartUncertain reason
        | LocalWorkerProcessOutcome.ObservationFailed reason
        | LocalWorkerProcessOutcome.StreamFailed reason -> Some reason
        | _ -> None

    /// Every path whose entry differs between two observations, outside the
    /// build-output directories (already excluded from both).
    let unauthorizedWrites (before: Map<string, CheckoutEntry>) (after: Map<string, CheckoutEntry>) =
        Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun path -> Map.tryFind path before <> Map.tryFind path after)
        |> Set.toList

    /// Replace the `{candidate}` role token; the configuration parser already
    /// refused any other placeholder.
    let relocate (checkout: string) (value: string) = value.Replace("{candidate}", checkout, StringComparison.Ordinal)

    // ---------------------------------------------------------- effects

    let private sha256File (path: string) =
        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        "sha256:" + (SHA256.HashData input |> Convert.ToHexString).ToLowerInvariant()

    /// The checkout's entries without following any link: regular files by
    /// digest, links by target. The worktree's `.git` file and build-output
    /// directories are not part of the candidate and are skipped.
    let observe (checkout: string) : Map<string, CheckoutEntry> =
        let rec walk (directory: string) (relative: string) : (string * CheckoutEntry) list =
            Directory.GetFileSystemEntries directory
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Array.toList
            |> List.collect (fun entry ->
                let name = Path.GetFileName entry
                let path = if relative = "" then name else relative + "/" + name
                let info = FileInfo entry
                if not (isNull info.LinkTarget) then [ path, LinkEntry info.LinkTarget ]
                elif Directory.Exists entry then
                    if OutputDirectories.Contains name || (relative = "" && name = ".git") then [] else walk entry path
                elif relative = "" && name = ".git" then []
                else [ path, FileEntry(sha256File entry) ])
        walk checkout "" |> Map.ofList

    /// The production runner: the host's qualified containment profile, or a
    /// refusal. Checks run submitted code, which Praxis never runs uncontained.
    let containedRunner (configuration: LocalHostConfiguration) (now: DateTimeOffset) : Result<ConstructionCheckRunner, string> =
        match configuration.Containment with
        | None -> Error "construct check runs submitted code and requires a qualified containment profile; the host configuration declares none"
        | Some containment ->
            LocalContainmentQualification.admit containment.Settings containment.Evidence containment.MaxEvidenceAge now
            |> Result.mapError (fun reason -> "construct check requires qualified containment: " + reason)
            |> Result.map (fun (tools, _) ->
                { Containment = LinuxContainment.Profile
                  Run =
                    fun spec ->
                        let stages = Collections.Concurrent.ConcurrentBag<string>()
                        try
                            LocalHostContainment.confine configuration containment.Settings tools stages.Add spec
                            |> Result.map (fun confined -> (LocalWorkerProcess.runControllerTool confined CancellationToken.None).GetAwaiter().GetResult())
                        finally
                            for stage in stages do LinuxContainmentHost.removeStage stage })

    let private preparedFacts (preparedPath: string) =
        try
            match JsonNode.Parse(File.ReadAllText preparedPath) with
            | :? JsonObject as node ->
                let text (n: JsonNode) = match n with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None
                let generated =
                    match node["construction"] with
                    | :? JsonObject as construction ->
                        match construction["generatedFiles"] with
                        | :? JsonArray as files -> files |> Seq.choose (fun f -> match f with :? JsonObject as o -> Option.map2 (fun p d -> p, d) (text o["path"]) (text o["sha256"]) | _ -> None) |> Seq.toList
                        | _ -> []
                    | _ -> []
                match text node["cacheKey"] with
                | Some key when not generated.IsEmpty -> Ok(key, generated)
                | _ -> Error "not a prepared construction artifact with generated files"
            | _ -> Error "prepared artifact is not a JSON object"
        with error -> Error("prepared artifact unreadable: " + error.Message)

    let private evidenceRoot (preparedPath: string) (checkId: string) =
        let full = Path.GetFullPath preparedPath
        let stem = if full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then full.Substring(0, full.Length - 5) else full
        Path.Combine(stem + ".checks", checkId)

    let private write (path: string) (content: string) = File.WriteAllText(path, content, Text.UTF8Encoding(false))

    let private stream (directory: string) (name: string) (content: string) =
        let path = Path.Combine(directory, name)
        write path content
        JsonObject(
            [ Collections.Generic.KeyValuePair("path", JsonValue.Create path :> JsonNode)
              Collections.Generic.KeyValuePair("sha256", JsonValue.Create(sha256File path) :> JsonNode)
              Collections.Generic.KeyValuePair("bytes", JsonValue.Create(FileInfo(path).Length) :> JsonNode)
              Collections.Generic.KeyValuePair("tail", JsonValue.Create(tail 2000 content) :> JsonNode) ])

    /// Run check `checkId` on `candidateRef`, which must carry exactly the
    /// generated files of `preparedPath`. Refusals before the run are errors;
    /// any run, whatever its outcome, is a recorded result.
    let run (runner: ConstructionCheckRunner) (configuration: LocalHostConfiguration) (preparedPath: string) (candidateRef: string) (checkId: string) : Result<JsonObject, string> =
        let git, repository = configuration.Git, configuration.RepositoryRoot
        let cancel = CancellationToken.None
        match configuration.ConstructionChecks |> Map.tryFind checkId with
        | None -> Error("unknown construction check '" + checkId + "'; the host declares: " + String.concat ", " (configuration.ConstructionChecks |> Map.keys))
        | Some template ->
            let command = template.Command
            let pinned =
                try
                    if not (File.Exists command.Executable) || sha256File command.Executable <> command.ExecutableDigest then Error "check adapter executable differs from its pinned digest"
                    else
                        match command.Dependencies |> Map.toList |> List.tryFind (fun (path, digest) -> not (File.Exists path) || sha256File path <> digest) with
                        | Some(path, _) -> Error("check adapter dependency differs from its pinned digest: " + path)
                        | None -> Ok()
                with error -> Error("check adapter pins unreadable: " + error.Message)
            pinned
            |> Result.bind (fun () -> preparedFacts preparedPath)
            |> Result.bind (fun (cacheKey, generated) ->
                FileLocalGitCandidates.resolveCommit git repository candidateRef cancel |> Result.mapError (fun reason -> "candidate does not resolve to a commit (" + candidateRef + "): " + reason) |> Result.bind (fun candidate ->
                    let mismatched =
                        generated |> List.filter (fun (path, digest) ->
                            FileLocalGitCandidates.blobDigest git repository candidate path cancel <> Ok digest)
                    if not mismatched.IsEmpty then
                        Error("candidate does not carry the prepared artifact's generated files: " + String.concat ", " (mismatched |> List.map fst))
                    else
                        FileLocalGitCandidates.provisionCheckout git repository configuration.CheckoutRoot "check" candidate cancel |> Result.bind (fun checkout ->
                            try
                                let before = observe checkout
                                let spec : LocalWorkerProcessSpec =
                                    { Executable = command.Executable; ExecutableDigest = command.ExecutableDigest
                                      Arguments = command.Arguments |> List.map (relocate checkout)
                                      WorkingDirectory = checkout
                                      Environment = command.Environment |> Map.map (fun _ value -> relocate checkout value)
                                      Input = relocate checkout command.Input
                                      TimeoutMilliseconds = command.TimeoutMilliseconds; MaxOutputBytes = command.MaxOutputBytes }
                                let clock = Stopwatch.StartNew()
                                runner.Run spec |> Result.map (fun observed ->
                                    let elapsed = clock.ElapsedMilliseconds
                                    let writes = unauthorizedWrites before (observe checkout)
                                    let status, exitCode, signal = classify (not (OperatingSystem.IsWindows())) observed.Outcome writes
                                    let root = evidenceRoot preparedPath checkId
                                    Directory.CreateDirectory root |> ignore
                                    let attempt = (Directory.GetDirectories root).Length + 1
                                    let directory = Path.Combine(root, sprintf "%03d" attempt)
                                    Directory.CreateDirectory directory |> ignore
                                    let node = JsonObject()
                                    node["schemaVersion"] <- JsonValue.Create Schema
                                    node["check"] <- JsonValue.Create checkId
                                    node["cacheKey"] <- JsonValue.Create cacheKey
                                    node["candidate"] <- JsonValue.Create candidate
                                    node["attempt"] <- JsonValue.Create attempt
                                    node["status"] <- JsonValue.Create status
                                    node["exitCode"] <- (match exitCode with Some code -> JsonValue.Create code :> JsonNode | None -> null)
                                    node["signal"] <- (match signal with Some value -> JsonValue.Create value :> JsonNode | None -> null)
                                    node["detail"] <- (match detail observed.Outcome with Some reason -> JsonValue.Create reason :> JsonNode | None -> null)
                                    node["wallMilliseconds"] <- JsonValue.Create elapsed
                                    node["timeoutMilliseconds"] <- JsonValue.Create command.TimeoutMilliseconds
                                    node["maxOutputBytes"] <- JsonValue.Create command.MaxOutputBytes
                                    node["containment"] <- JsonValue.Create runner.Containment
                                    node["unauthorizedWrites"] <- JsonArray(writes |> List.map (fun w -> JsonValue.Create w :> JsonNode) |> List.toArray)
                                    node["stdout"] <- stream directory "stdout.txt" observed.StandardOutput
                                    node["stderr"] <- stream directory "stderr.txt" observed.StandardError
                                    node["notice"] <- JsonValue.Create Notice
                                    write (Path.Combine(directory, "result.json")) (node.ToJsonString(Text.Json.JsonSerializerOptions(WriteIndented = true)) + "\n")
                                    node)
                            finally
                                FileLocalGitCandidates.removeCheckout git repository checkout cancel |> ignore)))
