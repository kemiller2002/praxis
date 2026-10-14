namespace Praxis.Contracts.Git

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Domain.Git

[<RequireQualifiedAccess>]
module UpstreamSyncContract =
    let private timestamp (value: DateTimeOffset) = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

    let private optional<'value> (node: JsonObject) (name: string) (value: 'value option) (render: 'value -> JsonNode) =
        node[name] <- value |> Option.map render |> Option.defaultValue null

    let private failureNode (failure: GitFailure) =
        let node = JsonObject()
        node["operation"] <- JsonValue.Create failure.Operation
        node["reason"] <- JsonValue.Create(GitUnavailableReason.code failure.Reason)
        node["message"] <- JsonValue.Create failure.Message
        optional node "exitCode" failure.ExitCode (fun value -> JsonValue.Create(value) :> JsonNode)
        node

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let toNode (report: UpstreamSyncReport) =
        let node = JsonObject()
        node["schemaVersion"] <- JsonValue.Create "1.0.0"

        let config = JsonObject()
        config["enabled"] <- JsonValue.Create report.Config.Enabled
        config["remote"] <- JsonValue.Create report.Config.Remote
        config["branch"] <- JsonValue.Create report.Config.Branch
        config["remoteTrackingRef"] <- JsonValue.Create $"refs/remotes/{report.Config.Remote}/{report.Config.Branch}"
        config["maxAgeMinutes"] <- JsonValue.Create report.Config.MaxAge.TotalMinutes
        node["config"] <- config

        match report.Availability with
        | UpstreamSyncAvailability.Disabled ->
            node["outcome"] <- JsonValue.Create "disabled"
            node["failure"] <- null
        | UpstreamSyncAvailability.Available ->
            node["outcome"] <- JsonValue.Create "available"
            node["failure"] <- null
        | UpstreamSyncAvailability.Unavailable failure ->
            node["outcome"] <- JsonValue.Create "unavailable"
            node["failure"] <- failureNode failure

        let attempt = JsonObject()
        optional attempt "at" report.LastAttemptAt (fun value -> JsonValue.Create(timestamp value) :> JsonNode)

        match report.LastAttempt with
        | UpstreamSyncAttempt.Never ->
            attempt["outcome"] <- JsonValue.Create "never"
            attempt["failure"] <- null
        | UpstreamSyncAttempt.Succeeded ->
            attempt["outcome"] <- JsonValue.Create "succeeded"
            attempt["failure"] <- null
        | UpstreamSyncAttempt.Failed failure ->
            attempt["outcome"] <- JsonValue.Create "failed"
            attempt["failure"] <- failureNode failure

        node["lastAttempt"] <- attempt

        let freshness = JsonObject()
        optional freshness "lastSuccessfulCheckAt" report.LastSuccessfulCheckAt (fun value -> JsonValue.Create(timestamp value) :> JsonNode)
        optional freshness "elapsedMilliseconds" report.ElapsedSinceSuccessfulCheck (fun value -> JsonValue.Create(int64 value.TotalMilliseconds) :> JsonNode)
        optional freshness "dueAt" report.DueAt (fun value -> JsonValue.Create(timestamp value) :> JsonNode)
        freshness["stale"] <- JsonValue.Create report.Stale
        node["freshness"] <- freshness

        let commits = JsonObject()
        optional commits "head" report.HeadCommit (fun value -> JsonValue.Create(CommitId.value value) :> JsonNode)
        optional commits "startingUpstream" report.StartingUpstreamCommit (fun value -> JsonValue.Create(CommitId.value value) :> JsonNode)
        optional commits "currentUpstream" report.CurrentUpstreamCommit (fun value -> JsonValue.Create(CommitId.value value) :> JsonNode)
        optional commits "upstreamChangedSinceStart" report.UpstreamChangedSinceStart (fun value -> JsonValue.Create(value) :> JsonNode)
        node["commits"] <- commits

        let relation = JsonObject()
        optional relation "ahead" report.Ahead (fun value -> JsonValue.Create(value) :> JsonNode)
        optional relation "behind" report.Behind (fun value -> JsonValue.Create(value) :> JsonNode)
        node["relation"] <- relation

        let paths = JsonObject()
        paths["incomingFromUpstream"] <- strings report.IncomingUpstreamPaths
        paths["upstreamSinceStart"] <- strings report.UpstreamPathsSinceStart
        paths["local"] <- strings report.LocalChangedPaths
        paths["overlap"] <- strings report.OverlapPaths
        node["paths"] <- paths

        node["integrationRequired"] <- JsonValue.Create report.IntegrationRequired
        node["safeForFinalValidation"] <- JsonValue.Create report.SafeForFinalValidation
        node["validationAgainstCurrentUpstream"] <- JsonValue.Create report.SafeForFinalValidation
        node

    let renderJson report =
        (toNode report).ToJsonString(JsonSerializerOptions(WriteIndented = true, IndentSize = 2))
