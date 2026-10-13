namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading
open Praxis.Domain.Telemetry
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

[<RequireQualifiedAccess>]
type ConstructionTelemetryOutcome =
    | Prepared
    | Reused
    | Applied
    | AlreadyCurrent
    | Refused

/// Host measurement of `construct prepare` / `construct apply --prepared`
/// (CONSTRUCT-008). Host compute time comes from the host's own clock and is
/// recorded on an execution-telemetry step with source type `host`, never as
/// model usage. Counts come from a host-owned, locked ledger. A value the host
/// cannot establish is null ("unavailable"), never zero.
///
/// A retry is a repeat call of the same operation in the SAME execution after
/// an earlier refused call of that operation; a refused call is a failure, not
/// a retry. Without a known execution, retries are unavailable.
[<RequireQualifiedAccess>]
module ConstructionTelemetry =
    let private wire = function
        | ConstructionTelemetryOutcome.Prepared -> "prepared"
        | ConstructionTelemetryOutcome.Reused -> "reused"
        | ConstructionTelemetryOutcome.Applied -> "applied"
        | ConstructionTelemetryOutcome.AlreadyCurrent -> "already-current"
        | ConstructionTelemetryOutcome.Refused -> "refused"

    let private iso (at: DateTimeOffset) = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    type private Entry = { Operation: string; GroupId: string; ExecutionId: string option; Outcome: string }

    type private Counts = { Prepares: int; Retry: bool option; Retries: int option; Failures: int option }

    let private ledgerPath (configuration: LocalHostConfiguration) =
        configuration.ConstructionRoot |> Option.map (fun store ->
            Path.Combine(LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store, "telemetry", "ledger.jsonl"))

    let private entries (text: string) =
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList |> List.choose (fun line ->
            try
                match JsonNode.Parse line with
                | :? JsonObject as o ->
                    let execution = match o["executionId"] with null -> None | value -> Some(value.GetValue<string>())
                    Some { Operation = o["operation"].GetValue<string>(); GroupId = o["groupId"].GetValue<string>(); ExecutionId = execution; Outcome = o["outcome"].GetValue<string>() }
                | _ -> None
            with _ -> None)

    /// Open the ledger exclusively (bounded wait), read it, append one entry.
    let private append (path: string) (build: Entry list -> string) =
        let rec attempt remaining =
            try Some(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            with :? IOException when remaining > 0 -> Thread.Sleep 50; attempt (remaining - 1)
        match attempt 100 with
        | None -> Error "telemetry ledger is locked"
        | Some stream ->
            use stream = stream
            let existing =
                use reader = new StreamReader(stream, UTF8Encoding(false), false, 4096, true)
                reader.ReadToEnd()
            let prior = entries existing
            let line = build prior
            let bytes = UTF8Encoding(false).GetBytes(line + "\n")
            stream.Seek(0L, SeekOrigin.End) |> ignore
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true
            Ok prior

    let private ledger (configuration: LocalHostConfiguration option) (operation: string) (groupId: string) (executionId: string option)
                       (cacheKey: string option) outcome (elapsedMs: int64) (finishedAt: DateTimeOffset) : Result<Counts, string> =
        match configuration |> Option.bind ledgerPath with
        | None -> Error "no host construction root"
        | Some path ->
            try
                let directory = Path.GetDirectoryName path
                if not (LocalHostJournalFiles.noLinks directory) || not (LocalHostJournalFiles.noLinks path) then Error "telemetry ledger path contains a link"
                else
                    Directory.CreateDirectory directory |> ignore
                    let refused = outcome = ConstructionTelemetryOutcome.Refused
                    let isRetry (prior: Entry list) =
                        executionId |> Option.map (fun id ->
                            prior |> List.exists (fun e -> e.ExecutionId = Some id && e.Operation = operation && e.Outcome = "refused"))
                    append path (fun prior ->
                        let entry = JsonObject()
                        entry["operation"] <- JsonValue.Create operation
                        entry["groupId"] <- JsonValue.Create groupId
                        entry["executionId"] <- (match executionId with Some id -> JsonValue.Create(id: string) :> JsonNode | None -> null)
                        entry["cacheKey"] <- (match cacheKey with Some k -> JsonValue.Create(k: string) :> JsonNode | None -> null)
                        entry["outcome"] <- JsonValue.Create(wire outcome)
                        entry["retry"] <- (match isRetry prior with Some r -> JsonValue.Create r :> JsonNode | None -> null)
                        entry["hostComputeMs"] <- JsonValue.Create elapsedMs
                        entry["at"] <- JsonValue.Create(iso finishedAt)
                        entry.ToJsonString())
                    |> Result.map (fun prior ->
                        let retry = isRetry prior
                        let inExecution =
                            executionId |> Option.map (fun id -> prior |> List.filter (fun e -> e.ExecutionId = Some id && e.Operation = operation))
                        let priorRetries =
                            // Every earlier call after the first refused one was a retry.
                            inExecution |> Option.map (fun calls ->
                                calls |> List.mapi (fun index _ -> calls |> List.take index |> List.exists (fun e -> e.Outcome = "refused"))
                                |> List.filter id |> List.length)
                        { Prepares = (prior |> List.filter (fun e -> e.GroupId = groupId && e.Operation = "prepare")).Length + (if operation = "prepare" then 1 else 0)
                          Retry = retry
                          Retries = Option.map2 (fun count r -> count + (if r then 1 else 0)) priorRetries retry
                          Failures = inExecution |> Option.map (fun calls -> (calls |> List.filter (fun e -> e.Outcome = "refused")).Length + (if refused then 1 else 0)) })
            with error -> Error error.Message

    /// Host measurements are marked `host` so a consumer never adds them to
    /// the agent runtime's own tool calls.
    let private source : CapabilitySource = { Type = "host"; Name = "praxis-construct"; Mechanism = "host-stopwatch" }

    let private metric stepId (id: string) (value: float) (collectedAt: string) : FileTelemetryFinalizationRepository.RecordMetricRequest =
        { MetricId = id; Value = value; Unit = None; Currency = None; Quality = "observed"
          Confidence = FileTelemetryFinalizationRepository.NoConfidence; Scope = "execution"; Source = source
          PricingSource = None; PricingVersion = None; CollectedAt = Some collectedAt; Step = Some stepId }

    let private step (root: string) (executionId: string) operation (metrics: (string * float) list) (startedAt: DateTimeOffset) (finishedAt: DateTimeOffset) (refused: bool) =
        let stepId = sprintf "STEP-CONSTRUCT-%s-%s" ((operation: string).ToUpperInvariant()) (Guid.NewGuid().ToString("N").Substring(0, 12))
        FileTelemetryFinalizationRepository.recordStep root (Some executionId) StepTransition.Start stepId (Some("construct " + operation)) None (iso startedAt)
        |> Result.bind (fun _ ->
            metrics |> List.fold (fun acc (id, value) ->
                acc |> Result.bind (fun () ->
                    FileTelemetryFinalizationRepository.recordMetric root (Some executionId) (metric stepId id value (iso finishedAt)) |> Result.map ignore)) (Ok())
            |> Result.bind (fun () ->
                let transition = if refused then StepTransition.Fail else StepTransition.Complete
                FileTelemetryFinalizationRepository.recordStep root (Some executionId) transition stepId None (if refused then Some "construction refused" else None) (iso finishedAt))
            |> Result.map (fun _ -> stepId))

    let private count (value: int option) : JsonNode = match value with Some n -> JsonValue.Create n :> JsonNode | None -> null

    /// Measure `action`, record ledger and step telemetry, and describe what
    /// was recorded. Telemetry failure never changes the action's result.
    let measure (root: string) (target: string option) (configuration: LocalHostConfiguration option) (operation: string) (groupId: string)
                (action: unit -> Result<'a, string> * string option * ConstructionTelemetryOutcome) : Result<'a, string> * JsonObject =
        let execution = FileTelemetryFinalizationRepository.resolveActiveExecution root target
        let startedAt = DateTimeOffset.UtcNow
        let watch = Diagnostics.Stopwatch.StartNew()
        let result, cacheKey, outcome = action ()
        watch.Stop()
        let finishedAt = DateTimeOffset.UtcNow
        let elapsed = watch.ElapsedMilliseconds
        let refused = outcome = ConstructionTelemetryOutcome.Refused
        let node = JsonObject()
        node["operation"] <- JsonValue.Create operation
        node["outcome"] <- JsonValue.Create(wire outcome)
        node["hostComputeMs"] <- JsonValue.Create elapsed
        node["source"] <- JsonValue.Create "host"
        node["modelUsage"] <- JsonValue.Create "unavailable: not observed by the host; the agent runtime's execution telemetry owns tokens and model time"
        let counts = ledger configuration operation groupId (Result.toOption execution) cacheKey outcome elapsed finishedAt
        match counts with
        | Ok counts ->
            node["ledger"] <- JsonValue.Create "recorded"
            node["prepares"] <- JsonValue.Create counts.Prepares
            node["retry"] <- (match counts.Retry with Some r -> JsonValue.Create r :> JsonNode | None -> null)
            node["retries"] <- count counts.Retries
            node["failures"] <- count counts.Failures
        | Error reason ->
            node["ledger"] <- JsonValue.Create("unavailable: " + reason)
            for name in [ "prepares"; "retry"; "retries"; "failures" ] do node[name] <- null
        let retry = match counts with Ok c -> c.Retry | Error _ -> None
        let metrics =
            [ "time.tool_ms", float elapsed
              "tool.calls", 1.0
              "tool.failures", (if refused then 1.0 else 0.0) ]
            @ (match retry with Some r -> [ "tool.retries", (if r then 1.0 else 0.0) ] | None -> [])
        match execution |> Result.bind (fun id -> step root id operation metrics startedAt finishedAt refused |> Result.map (fun stepId -> id, stepId)) with
        | Ok(executionId, stepId) ->
            node["executionStep"] <- JsonValue.Create "recorded"
            node["executionId"] <- JsonValue.Create executionId
            node["stepId"] <- JsonValue.Create stepId
        | Error reason -> node["executionStep"] <- JsonValue.Create("unavailable: " + reason)
        result, node
