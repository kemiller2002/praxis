namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text.Json.Nodes
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
/// (CONSTRUCT-008). Host compute time is measured by the host's own clock and
/// recorded as registered tool metrics on an execution-telemetry step; it is
/// never mixed with model usage, which only the agent runtime can observe.
/// Counts come from a host-owned ledger, so retries are counted across
/// invocations without trusting the caller.
[<RequireQualifiedAccess>]
module ConstructionTelemetry =
    let private wire = function
        | ConstructionTelemetryOutcome.Prepared -> "prepared"
        | ConstructionTelemetryOutcome.Reused -> "reused"
        | ConstructionTelemetryOutcome.Applied -> "applied"
        | ConstructionTelemetryOutcome.AlreadyCurrent -> "already-current"
        | ConstructionTelemetryOutcome.Refused -> "refused"

    let private iso (at: DateTimeOffset) = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

    type private Entry = { Operation: string; GroupId: string; Outcome: string }

    let private ledgerPath (configuration: LocalHostConfiguration) =
        configuration.ConstructionRoot |> Option.map (fun store ->
            Path.Combine(LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store, "telemetry", "ledger.jsonl"))

    let private priorEntries (path: string) =
        if not (File.Exists path) then []
        else
            File.ReadAllLines path |> Array.toList |> List.choose (fun line ->
                try
                    match JsonNode.Parse line with
                    | :? JsonObject as o ->
                        Some { Operation = o["operation"].GetValue<string>(); GroupId = o["groupId"].GetValue<string>(); Outcome = o["outcome"].GetValue<string>() }
                    | _ -> None
                with _ -> None)

    /// Append this invocation to the host ledger and derive its counters.
    let private ledger (configuration: LocalHostConfiguration option) (operation: string) (groupId: string) (cacheKey: string option) outcome (elapsedMs: int64) (finishedAt: DateTimeOffset) =
        match configuration |> Option.bind ledgerPath with
        | None -> Error "no host construction root"
        | Some path ->
            try
                let directory = Path.GetDirectoryName path
                if not (LocalHostJournalFiles.noLinks directory) || not (LocalHostJournalFiles.noLinks path) then Error "telemetry ledger path contains a link"
                else
                    Directory.CreateDirectory directory |> ignore
                    let prior = priorEntries path |> List.filter (fun e -> e.GroupId = groupId)
                    let entry = JsonObject()
                    entry["operation"] <- JsonValue.Create operation
                    entry["groupId"] <- JsonValue.Create groupId
                    entry["cacheKey"] <- (match cacheKey with Some k -> JsonValue.Create k :> JsonNode | None -> null)
                    entry["outcome"] <- JsonValue.Create(wire outcome)
                    entry["hostComputeMs"] <- JsonValue.Create elapsedMs
                    entry["at"] <- JsonValue.Create(iso finishedAt)
                    File.AppendAllText(path, entry.ToJsonString() + "\n")
                    let sameOperation = prior |> List.filter (fun e -> e.Operation = operation)
                    Ok(not sameOperation.IsEmpty,
                       (prior |> List.filter (fun e -> e.Operation = "prepare")).Length + (if operation = "prepare" then 1 else 0),
                       sameOperation.Length,
                       (prior |> List.filter (fun e -> e.Outcome = "refused")).Length + (if outcome = ConstructionTelemetryOutcome.Refused then 1 else 0))
            with error -> Error error.Message

    let private source : CapabilitySource = { Type = "host-measurement"; Name = "praxis-construct"; Mechanism = "host-stopwatch" }

    let private metric stepId (id: string) (value: float) (collectedAt: string) : FileTelemetryFinalizationRepository.RecordMetricRequest =
        { MetricId = id; Value = value; Unit = None; Currency = None; Quality = "observed"
          Confidence = FileTelemetryFinalizationRepository.NoConfidence; Scope = "execution"; Source = source
          PricingSource = None; PricingVersion = None; CollectedAt = Some collectedAt; Step = Some stepId }

    /// Record the invocation as one step of the caller's active execution,
    /// when there is one. A missing execution is reported, never invented.
    let private step (root: string) (target: string option) operation (metrics: (string * float) list) (startedAt: DateTimeOffset) (finishedAt: DateTimeOffset) (refused: bool) =
        let stepId = sprintf "STEP-CONSTRUCT-%s-%s" ((operation: string).ToUpperInvariant()) (Guid.NewGuid().ToString("N").Substring(0, 12))
        FileTelemetryFinalizationRepository.recordStep root target StepTransition.Start stepId (Some("construct " + operation)) None (iso startedAt)
        |> Result.bind (fun (executionId, _) ->
            metrics |> List.fold (fun acc (id, value) ->
                acc |> Result.bind (fun () ->
                    FileTelemetryFinalizationRepository.recordMetric root (Some executionId) (metric stepId id value (iso finishedAt)) |> Result.map ignore)) (Ok())
            |> Result.bind (fun () ->
                let transition = if refused then StepTransition.Fail else StepTransition.Complete
                FileTelemetryFinalizationRepository.recordStep root (Some executionId) transition stepId None (if refused then Some "construction refused" else None) (iso finishedAt))
            |> Result.map (fun _ -> executionId, stepId))

    /// Measure `action`, record ledger and step telemetry, and describe what
    /// was recorded. Telemetry failure never changes the action's result.
    let measure (root: string) (target: string option) (configuration: LocalHostConfiguration option) (operation: string) (groupId: string)
                (action: unit -> Result<'a, string> * string option * ConstructionTelemetryOutcome) : Result<'a, string> * JsonObject =
        let startedAt = DateTimeOffset.UtcNow
        let watch = Diagnostics.Stopwatch.StartNew()
        let result, cacheKey, outcome = action ()
        watch.Stop()
        let finishedAt = DateTimeOffset.UtcNow
        let elapsed = watch.ElapsedMilliseconds
        let node = JsonObject()
        node["operation"] <- JsonValue.Create operation
        node["outcome"] <- JsonValue.Create(wire outcome)
        node["hostComputeMs"] <- JsonValue.Create elapsed
        node["modelUsage"] <- JsonValue.Create "not observed by the host; the agent runtime's execution telemetry owns tokens and model time"
        let retry, previews, retries, refusals =
            match ledger configuration operation groupId cacheKey outcome elapsed finishedAt with
            | Ok(retry, previews, retries, refusals) ->
                node["ledger"] <- JsonValue.Create "recorded"
                retry, previews, retries, refusals
            | Error reason ->
                node["ledger"] <- JsonValue.Create("unavailable: " + reason)
                false, (if operation = "prepare" then 1 else 0), 0, (if outcome = ConstructionTelemetryOutcome.Refused then 1 else 0)
        node["previews"] <- JsonValue.Create previews
        node["retries"] <- JsonValue.Create retries
        node["refusals"] <- JsonValue.Create refusals
        let refused = outcome = ConstructionTelemetryOutcome.Refused
        // Registered metrics only: host compute is tool time (never model
        // time); the invocation is one tool call, a retry, or a failure.
        // Preview and cache-hit counts live in the host ledger and step name.
        let metrics =
            [ "time.tool_ms", float elapsed
              "tool.calls", 1.0
              "tool.retries", (if retry then 1.0 else 0.0)
              "tool.failures", (if refused then 1.0 else 0.0) ]
        match step root target operation metrics startedAt finishedAt refused with
        | Ok(executionId, stepId) ->
            node["executionStep"] <- JsonValue.Create "recorded"
            node["executionId"] <- JsonValue.Create executionId
            node["stepId"] <- JsonValue.Create stepId
        | Error reason -> node["executionStep"] <- JsonValue.Create("unavailable: " + reason)
        result, node
