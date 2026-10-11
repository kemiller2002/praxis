namespace Praxis.Contracts.Work

open System.Text.Json.Nodes
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module LocalHandoffExplanationJson =
    let private str (value: string) : JsonNode = JsonValue.Create value
    let private obj (fields: (string * JsonNode) list) : JsonNode =
        let node = JsonObject()
        fields |> List.iter (fun (key, value) -> node[key] <- value)
        node
    let private array (values: JsonNode list) : JsonNode =
        let node = JsonArray()
        values |> List.iter node.Add
        node
    let private strings values = values |> List.map str |> array
    let render (report: LocalHandoffExplanation) =
        obj [ "schemaVersion", str "praxis.local-handoff-explanation/1"
              "asOf", str (report.AsOf.ToString("O")); "mode", str "proposal-inspection"
              "limitations", strings report.Limitations
              "assignments", report.Assignments |> List.map (fun a ->
                  obj [ "packet", JsonNode.Parse(LocalAgentHandoffJson.renderPacket a.Packet)
                        "packetDigest", a.PacketDigest |> Option.map str |> Option.defaultValue null
                        "blockingReasons", strings a.BlockingReasons
                        "dependencies", a.Dependencies |> List.map (fun d ->
                            obj [ "workItemId", str d.WorkItemId; "evidenceDigest", str d.EvidenceDigest
                                  "proposedProducers", strings d.ProposedProducers ]) |> array ]) |> array
              "writeConflicts", report.WriteConflicts |> List.map (fun c ->
                  obj [ "leftDispatchId", str c.LeftDispatchId; "rightDispatchId", str c.RightDispatchId; "paths", strings c.Paths ]) |> array ]
        |> fun node -> node.ToJsonString()
