namespace Ros.Contracts.Provenance

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Provenance

/// JSON views emitted by `ros provenance ...` commands. Every view embeds
/// actors in the canonical `ActorJson` form so a consumer (another Echelon
/// system, a metrics pipeline, CI) reads one shape everywhere.
[<RequireQualifiedAccess>]
module ProvenanceReportJson =
    let private options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let render (node: JsonNode) = node.ToJsonString options + "\n"

    let strings (values: string list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value: JsonNode))
        array

    let nodes (values: JsonNode list) : JsonArray =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add value)
        array

    let optional (value: string option) : JsonNode =
        match value with
        | Some text -> JsonValue.Create text
        | None -> null

    let contribution (item: Contribution) : JsonObject =
        let node = JsonObject()
        node["key"] <- JsonValue.Create item.Key
        node["execution"] <- optional (Contribution.execution item)
        node["operations"] <- strings (item.Operations |> List.map ContributionOperation.code)
        node["at"] <- JsonValue.Create item.At
        node["last"] <- optional item.Last
        node["actor"] <- ActorJson.node item.Actor
        node["reason"] <- optional item.Reason
        node["evidence"] <- strings item.Evidence
        node

    let involvement (item: Involvement) : JsonObject =
        let node = JsonObject()
        node["label"] <- JsonValue.Create item.Label
        node["origin"] <- (item.Origin |> Option.map ActorJson.node |> Option.map (fun value -> value :> JsonNode) |> Option.toObj)
        node["modifiers"] <- nodes (item.Modifiers |> List.map (fun actor -> ActorJson.node actor :> JsonNode))
        node["reviewers"] <- nodes (item.Reviewers |> List.map (fun actor -> ActorJson.node actor :> JsonNode))
        node["approvers"] <- nodes (item.Approvers |> List.map (fun actor -> ActorJson.node actor :> JsonNode))
        node["agentToAgentRevision"] <- JsonValue.Create item.AgentToAgentRevision
        node["humanCorrectionOfAgentWork"] <- JsonValue.Create item.HumanCorrectionOfAgentWork
        node

    let finding (item: ProvenanceFinding) : JsonObject =
        let node = JsonObject()
        node["severity"] <- JsonValue.Create(FindingSeverity.code item.Severity)
        node["path"] <- JsonValue.Create item.Path
        node["field"] <- (if item.Field.Length = 0 then null else JsonValue.Create item.Field :> JsonNode)
        node["message"] <- JsonValue.Create item.Message
        node

    let fact (item: ContributionFact) : JsonObject =
        let node = contribution item.Contribution
        node["artifactId"] <- JsonValue.Create item.ArtifactId
        node["path"] <- JsonValue.Create item.Path
        node["kind"] <- JsonValue.Create item.Kind
        node["isOrigin"] <- JsonValue.Create item.IsOrigin
        node

    let actorSummary (item: ActorContributionSummary) : JsonObject =
        let node = JsonObject()
        node["actor"] <- ActorJson.node item.Actor
        node["artifacts"] <- JsonValue.Create item.Artifacts
        node["created"] <- JsonValue.Create item.Created
        node["modified"] <- JsonValue.Create item.Modified
        node["reviewed"] <- JsonValue.Create item.Reviewed
        node["executions"] <- JsonValue.Create item.Executions
        node
