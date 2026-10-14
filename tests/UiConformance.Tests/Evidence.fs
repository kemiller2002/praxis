namespace Praxis.UiConformance

open System.Text.Json
open System.Text.Json.Nodes

/// Evidence in the shape of visual-engineering's ui-conformance.schema.json
/// (standardVersion 1.0.0). Statuses are the schema's lowercase vocabulary.
type ActionRecord =
    { Id: string
      Role: string
      Name: string
      Intent: string
      ExpectedOutcome: string
      Status: string
      Evidence: string list
      ResetStrategy: string }

type GroupRecord =
    { Id: string
      Relation: string
      ToleranceCssPx: float
      Targets: (string * string) list
      Rationale: string
      Evidence: string list
      Status: string }

type SurfaceRecord =
    { Id: string
      Path: string
      Kind: string
      ViewportWidths: int list
      Groups: GroupRecord list
      Actions: ActionRecord list
      Evidence: string list
      Checks: (string * Status) list
      /// Failing check lines covered by an owned, bounded exception.
      Excepted: string list
      UnknownScope: string list }

type ExceptionRecord =
    { RuleId: string
      Surface: string
      Reason: string
      Owner: string
      ReviewTrigger: string
      Evidence: string list }

[<RequireQualifiedAccess>]
module Evidence =
    let statusText (status: Status) =
        match status with
        | Pass -> "pass"
        | Fail -> "fail"
        | Unknown -> "unknown"

    /// A check's status after exceptions: a covered FAIL is "exception".
    let checkStatus (surface: SurfaceRecord) (line: string, status: Status) =
        if status = Fail && List.contains line surface.Excepted then "exception"
        elif status = Unknown && line.Contains ": not applicable: " then "not-applicable"
        else statusText status

    /// Every status the surface recorded: checks, groups and actions.
    let statuses (surface: SurfaceRecord) =
        (surface.Checks |> List.map (checkStatus surface)) @ (surface.Groups |> List.map _.Status) @ (surface.Actions |> List.map _.Status)

    /// A surface fails on any failing check, group or action; it is unknown
    /// while anything measured is unknown; a covered deviation makes it
    /// "exception", never "pass"; it passes only on evidence. Scope that was
    /// never exercised stays listed in `unknownScope`.
    let surfaceStatus (surface: SurfaceRecord) =
        let all = statuses surface

        if all |> List.contains "fail" then "fail"
        elif all |> List.contains "unknown" || all.IsEmpty then "unknown"
        elif all |> List.contains "exception" then "exception"
        else "pass"

    let private strings (values: string list) = JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> Array.ofList)

    let private locator (role: string, name: string) =
        let node = JsonObject()
        node["role"] <- role
        node["name"] <- name
        node :> JsonNode

    let private action (record: ActionRecord) =
        let node = JsonObject()
        node["id"] <- record.Id
        node["control"] <- locator (record.Role, record.Name)
        node["intent"] <- record.Intent
        node["expectedOutcome"] <- record.ExpectedOutcome
        node["status"] <- record.Status
        node["evidence"] <- strings record.Evidence
        node["resetStrategy"] <- record.ResetStrategy
        node :> JsonNode

    let private group (record: GroupRecord) =
        let node = JsonObject()
        node["id"] <- record.Id
        node["relation"] <- record.Relation
        node["toleranceCssPx"] <- record.ToleranceCssPx
        node["targets"] <- JsonArray(record.Targets |> List.map locator |> Array.ofList)
        node["rationale"] <- record.Rationale
        node["evidence"] <- strings (record.Evidence @ [ $"group status: {record.Status}" ])
        node :> JsonNode

    let private surface (record: SurfaceRecord) =
        let node = JsonObject()
        node["id"] <- record.Id
        node["path"] <- record.Path
        node["kind"] <- record.Kind
        node["status"] <- surfaceStatus record
        node["viewportWidths"] <- JsonArray(record.ViewportWidths |> List.map (fun width -> JsonValue.Create width :> JsonNode) |> Array.ofList)
        node["alignmentGroups"] <- JsonArray(record.Groups |> List.map group |> Array.ofList)
        node["actions"] <- JsonArray(record.Actions |> List.map action |> Array.ofList)
        node["evidence"] <- strings record.Evidence
        node["unknownScope"] <- strings record.UnknownScope
        node :> JsonNode

    let private exceptionNode (record: ExceptionRecord) =
        let node = JsonObject()
        node["ruleId"] <- record.RuleId
        node["surface"] <- record.Surface
        node["reason"] <- record.Reason
        node["owner"] <- record.Owner
        node["reviewTrigger"] <- record.ReviewTrigger
        node["evidence"] <- strings record.Evidence
        node :> JsonNode

    let document (repository: string) (revision: string) (classification: string) (surfaces: SurfaceRecord list) (exceptions: ExceptionRecord list) (notes: string) =
        let node = JsonObject()
        node["standardVersion"] <- "1.0.0"
        node["repository"] <- repository
        node["revision"] <- revision
        node["classification"] <- classification
        node["surfaces"] <- JsonArray(surfaces |> List.map surface |> Array.ofList)
        node["exceptions"] <- JsonArray(exceptions |> List.map exceptionNode |> Array.ofList)
        node["notes"] <- notes
        node

    let render (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
