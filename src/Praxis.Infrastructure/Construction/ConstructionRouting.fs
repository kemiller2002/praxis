namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Construction

/// An `implement` obligation routed to the one host-declared slot whose
/// contracts name it. `SlotPath` is repository-relative.
type RoutedImplementation =
    { Obligation: Artifacts.ConstructionObligation
      Contract: string
      Slot: ConstructionExtensionSlot
      SlotPath: string }

/// A typed exception request: work the host cannot route to a deterministic
/// constructor or a declared slot. It keeps the original requirement keys
/// and never falls back to general repository write.
type RoutingException =
    { ObligationId: string
      Contract: string
      RequirementKeys: string list
      Reason: string }

/// Explicit classification of an admitted construction's work (Praxis #243).
/// Pure: derived from the admitted construction and host slot policy only.
type ConstructionRouting =
    { Binding: ConstructionBinding
      OwnershipRoot: string
      /// (a) Deterministic construction: Ordo's generated artifacts.
      Deterministic: Artifacts.Artifact list
      /// (b) Semantic decisions: `resolve` obligations, for an authorized human.
      Semantic: Artifacts.ConstructionObligation list
      /// (c) Bounded custom implementation: `implement` obligations in slots.
      Custom: RoutedImplementation list
      /// Independent verification: never closed by construction or a slot.
      Verification: Artifacts.ConstructionObligation list
      Exceptions: RoutingException list
      Slots: ConstructionExtensionSlot list
      Mount: string }

[<RequireQualifiedAccess>]
module ConstructionRouting =
    [<Literal>]
    let Schema = "praxis.construction-routing/1"

    /// Autonomous custom-code workers stay disabled until host containment is
    /// qualified (Praxis #231, #235). Nothing in routing or `extend` starts
    /// a worker; this is the reason every such request is refused with.
    [<Literal>]
    let WorkerExecutionDisabled =
        "autonomous custom-code worker execution is disabled: host OS containment is not qualified (Praxis #231, #235); submit handwritten slot files with `construct extend` instead"

    let classify (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) : ConstructionRouting =
        let built = ConstructionAdmission.construction admitted
        let mount = configuration.ConstructionMount
        let slotFor contract = configuration.ExtensionSlots |> List.tryFind (fun slot -> slot.Contracts |> List.contains contract)
        let implementations, semantic, verification =
            built.Obligations |> List.fold (fun (impl, sem, ver) obligation ->
                match obligation.Kind with
                | Artifacts.Implement contract -> impl @ [ obligation, contract ], sem, ver
                | Artifacts.ResolveUnresolved _ -> impl, sem @ [ obligation ], ver
                | Artifacts.VerifyIndependently _ -> impl, sem, ver @ [ obligation ]) ([], [], [])
        let custom, exceptions =
            implementations |> List.fold (fun (routed, refused) (obligation, contract) ->
                match slotFor contract with
                | Some slot -> routed @ [ { Obligation = obligation; Contract = contract; Slot = slot; SlotPath = ConstructionTree.placed mount slot.File } ], refused
                | None ->
                    routed,
                    refused @ [ { ObligationId = obligation.Id; Contract = contract; RequirementKeys = obligation.RequirementKeys
                                  Reason = "no host-declared extension slot implements this contract; a slot needs an operator-authorized host configuration revision and a re-route" } ]) ([], [])
        { Binding = ConstructionAdmission.binding admitted
          OwnershipRoot = ConstructionTree.placed mount built.OwnershipRoot
          Deterministic = built.Artifacts; Semantic = semantic; Custom = custom; Verification = verification
          Exceptions = exceptions; Slots = configuration.ExtensionSlots; Mount = mount }

    /// Slots this construction can use: those implementing at least one of
    /// its `implement` obligations.
    let usableSlots (routing: ConstructionRouting) =
        routing.Custom |> List.map _.Slot |> List.distinctBy _.File

    let private strings (values: string list) = JsonArray(values |> List.map (fun value -> JsonValue.Create value :> JsonNode) |> List.toArray)
    let private array (nodes: JsonNode list) = JsonArray(nodes |> List.toArray)

    let private obj (fields: (string * JsonNode) list) =
        let node = JsonObject()
        fields |> List.iter (fun (name, value) -> node[name] <- value)
        node :> JsonNode

    let private str (value: string) = JsonValue.Create value :> JsonNode
    let private num (value: int) = JsonValue.Create value :> JsonNode

    let private slotNode (slot: ConstructionExtensionSlot) mount =
        obj [ "file", str slot.File; "path", str (ConstructionTree.placed mount slot.File)
              "contracts", strings slot.Contracts; "maxBytes", num slot.MaxBytes ]

    let slotPolicyDigest (routing: ConstructionRouting) =
        ConstructionTree.digest ((array (routing.Slots |> List.map (fun slot -> slotNode slot routing.Mount))).ToJsonString())

    /// The persisted record. It holds no clock reading, so the same inputs
    /// always render the same bytes and the same route ID.
    let private body (routing: ConstructionRouting) =
        let b = routing.Binding
        let bytes (artifact: Artifacts.Artifact) = Text.UTF8Encoding(false).GetByteCount artifact.Content
        obj [ "schemaVersion", str Schema
              "binding",
              obj [ "planDigest", str b.PlanDigest; "groupId", str b.GroupId; "cohortId", str b.CohortId
                    "policyRevision", str b.PolicyRevision; "approver", str b.Approver; "sourceCommit", str b.SourceCommit
                    "generatorVersion", str b.GeneratorVersion; "requirementKeys", strings b.RequirementKeys; "decisionIds", strings b.DecisionIds ]
              "ownershipRoot", str routing.OwnershipRoot
              "slotPolicyDigest", str (slotPolicyDigest routing)
              "deterministic",
              array (routing.Deterministic |> List.map (fun artifact ->
                  obj [ "path", str (ConstructionTree.placed routing.Mount artifact.Path); "sha256", str artifact.Digest; "bytes", num (bytes artifact)
                        "requirementKeys", strings artifact.RequirementKeys; "route", str "deterministic-constructor" ]))
              "semantic",
              array (routing.Semantic |> List.map (fun o ->
                  let subject, question = match o.Kind with Artifacts.ResolveUnresolved(s, q) -> s, q | _ -> "", ""
                  obj [ "id", str o.Id; "subject", str subject; "question", str question; "requirementKeys", strings o.RequirementKeys
                        "route", str "authorized-decision"; "status", str "open" ]))
              "custom",
              array (routing.Custom |> List.map (fun r ->
                  obj [ "id", str r.Obligation.Id; "contract", str r.Contract; "slot", str r.SlotPath; "maxBytes", num r.Slot.MaxBytes
                        "requirementKeys", strings r.Obligation.RequirementKeys; "route", str "bounded-slot"; "status", str "open" ]))
              "verification",
              array (routing.Verification |> List.map (fun o ->
                  obj [ "id", str o.Id; "requirementKeys", strings o.RequirementKeys; "route", str "independent-verification"; "status", str "open" ]))
              "exceptions",
              array (routing.Exceptions |> List.map (fun e ->
                  obj [ "kind", str "unroutable-implementation"; "obligationId", str e.ObligationId; "contract", str e.Contract
                        "requirementKeys", strings e.RequirementKeys; "reason", str e.Reason ]))
              "measurement",
              obj [ "deterministicWrites", num routing.Deterministic.Length
                    "deterministicBytes", num (routing.Deterministic |> List.sumBy bytes)
                    "boundedCustomSlots", num (usableSlots routing).Length
                    "boundedCustomObligations", num routing.Custom.Length
                    "semanticObligations", num routing.Semantic.Length
                    "verificationObligations", num routing.Verification.Length
                    "exceptions", num routing.Exceptions.Length
                    "reasoningTime", str "unavailable"
                    "reasoningTokens", str "unavailable"
                    "note", str "Counts are derived from the admitted construction. Time and tokens are not observed here; execution telemetry owns them." ]
              "workerExecution", obj [ "status", str "disabled"; "reason", str WorkerExecutionDisabled ] ]

    /// Content-addressed: the ID is the digest of the record body.
    let routeId (routing: ConstructionRouting) =
        "route-" + (ConstructionTree.digest ((body routing).ToJsonString())).Substring(7, 24)

    let render (routing: ConstructionRouting) =
        let node = body routing :?> JsonObject
        node["routeId"] <- JsonValue.Create(routeId routing)
        node

    /// Persist the record as `routes/ROUTE-ID.json` under the host
    /// construction root, write-once. A rerun finds identical bytes; any
    /// other content under the same ID refuses.
    let record (configuration: LocalHostConfiguration) (routing: ConstructionRouting) : Result<string, string> =
        match configuration.ConstructionRoot with
        | None -> Error "construction is disabled: the host configuration names no construction root"
        | Some store ->
            try
                let root = LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store
                let routes = Path.Combine(root, "routes")
                if not (LocalHostJournalFiles.noLinks routes) then Error "routing record directory contains a link"
                else
                    Directory.CreateDirectory routes |> ignore
                    let path = Path.Combine(routes, routeId routing + ".json")
                    let content = (render routing).ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
                    if not (LocalHostJournalFiles.noLinks path) then Error "routing record path contains a link"
                    elif File.Exists path then
                        if LocalHostJournalFiles.readBounded path = content then Ok path
                        else Error "a different routing record already exists under this route ID"
                    else
                        LocalHostJournalFiles.writeExclusive path content
                        Ok path
            with error -> Error("routing record refused: " + error.Message)
