namespace Ros.Contracts.Execution

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Ros.Domain.Execution

/// The `ordo.execution/1` wire shapes as Praxis persists them under
/// `.ros/executions/<execution-id>/`: `envelope.json` (materialized current
/// envelope) and `events.jsonl` (append-only step ledger and transitions).
[<RequireQualifiedAccess>]
module ExecutionJson =
    [<Literal>]
    let Schema = "ordo.execution/1"

    let options =
        JsonSerializerOptions(WriteIndented = true, IndentSize = 2, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let compact =
        JsonSerializerOptions(WriteIndented = false, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private str (v: string) : JsonNode = JsonValue.Create v
    let private opt (v: string option) : JsonNode = v |> Option.map str |> Option.toObj
    let private arr (xs: JsonNode list) : JsonNode = (let a = JsonArray() in xs |> List.iter a.Add; a)

    let private obj (fields: (string * JsonNode) list) : JsonNode =
        let o = JsonObject()
        fields |> List.iter (fun (k, v) -> o[k] <- v)
        o

    let timestamp (at: DateTimeOffset) = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let rec expected (e: ExpectedReceipt) : JsonNode =
        match e with
        | ExpectedReceipt.ArtifactExists r -> obj [ "kind", str "artifact-exists"; "reference", str r ]
        | ExpectedReceipt.ArtifactIdentity(r, d) -> obj [ "kind", str "artifact-identity"; "reference", str r; "digest", str d ]
        | ExpectedReceipt.ArtifactAbsent r -> obj [ "kind", str "artifact-absent"; "reference", str r ]
        | ExpectedReceipt.CommandSucceeded c -> obj [ "kind", str "command-succeeded"; "command", str c ]
        | ExpectedReceipt.StateEquals(k, v) -> obj [ "kind", str "state-equals"; "key", str k; "value", str v ]
        | ExpectedReceipt.ConformsToContract(r, c) -> obj [ "kind", str "conforms-to-contract"; "reference", str r; "contract", str c ]
        | ExpectedReceipt.VerificationSatisfied fp -> obj [ "kind", str "verification-satisfied"; "evaluator", str fp ]
        | ExpectedReceipt.TransitionObserved t -> obj [ "kind", str "transition-observed"; "transition", str t ]
        | ExpectedReceipt.AllOf parts -> obj [ "kind", str "all-of"; "receipts", arr (parts |> List.map expected) ]

    let fact (f: ObservedFact) : JsonNode =
        match f with
        | ObservedFact.ArtifactObserved(r, d) -> obj [ "kind", str "artifact-observed"; "reference", str r; "digest", opt d ]
        | ObservedFact.ArtifactNotFound r -> obj [ "kind", str "artifact-not-found"; "reference", str r ]
        | ObservedFact.CommandExited(c, code) -> obj [ "kind", str "command-exited"; "command", str c; "exitCode", JsonValue.Create code ]
        | ObservedFact.CommandOutcomeUnknown(c, why) -> obj [ "kind", str "command-outcome-unknown"; "command", str c; "reason", str why ]
        | ObservedFact.StateObserved(k, v) -> obj [ "kind", str "state-observed"; "key", str k; "value", str v ]
        | ObservedFact.ContractChecked(r, c, ok) ->
            obj [ "kind", str "contract-checked"; "reference", str r; "contract", str c; "conforms", JsonValue.Create ok ]
        | ObservedFact.VerificationObserved(fp, passed) ->
            obj [ "kind", str "verification-observed"; "evaluator", str fp; "passed", JsonValue.Create passed ]
        | ObservedFact.TransitionRecorded t -> obj [ "kind", str "transition-recorded"; "transition", str t ]
        | ObservedFact.Unobservable(s, why) -> obj [ "kind", str "unobservable"; "subject", str s; "reason", str why ]

    let observed (o: ObservedReceipt) : JsonNode =
        let source, observer =
            match o.Source with
            | ObservationSource.Host x -> "host", x
            | ObservationSource.Independent x -> "independent", x
            | ObservationSource.SelfReported x -> "self-reported", x

        obj [ "source", str source; "observer", str observer; "facts", arr (o.Facts |> List.map fact); "narrative", opt o.Narrative ]

    let rec result (r: ReceiptResult) : JsonNode =
        obj
            [ "result", str (ReceiptOutcome.toWire r.Outcome)
              "expected", expected r.Expected
              "reason", opt r.Reason
              "evidence", arr (r.Evidence |> List.map fact)
              "constituents", arr (r.Constituents |> List.map result) ]

    // ---- reading (receipts come from hosts and operators as JSON) ----

    let private member' (name: string) (node: JsonNode) =
        match node with
        | :? JsonObject as o ->
            let mutable found: JsonNode = null
            if o.TryGetPropertyValue(name, &found) then Option.ofObj found else None
        | _ -> None

    let private text name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<string>() with
            | true, s -> Ok s
            | _ -> Error $"{name} must be a string"
        | _ -> Error $"{name} is required"

    let private optionalText name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None

    let private boolean name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<bool>() with
            | true, b -> Ok b
            | _ -> Error $"{name} must be a boolean"
        | _ -> Error $"{name} is required"

    let private integer name node =
        match member' name node with
        | Some(:? JsonValue as v) ->
            match v.TryGetValue<int>() with
            | true, n -> Ok n
            | _ -> Error $"{name} must be an integer"
        | _ -> Error $"{name} is required"

    let private items name node =
        match member' name node with
        | Some(:? JsonArray as a) -> a |> Seq.map (fun x -> x) |> Seq.toList
        | _ -> []

    let private traverse f xs =
        List.foldBack (fun x acc -> Result.bind (fun ys -> f x |> Result.map (fun y -> y :: ys)) acc) xs (Ok [])

    let private both (a: Result<'a, string>) (b: Result<'b, string>) = Result.bind (fun x -> Result.map (fun y -> x, y) b) a

    let rec readExpected (node: JsonNode) : Result<ExpectedReceipt, string> =
        text "kind" node
        |> Result.bind (fun kind ->
            match kind with
            | "artifact-exists" -> text "reference" node |> Result.map ExpectedReceipt.ArtifactExists
            | "artifact-absent" -> text "reference" node |> Result.map ExpectedReceipt.ArtifactAbsent
            | "artifact-identity" -> both (text "reference" node) (text "digest" node) |> Result.map ExpectedReceipt.ArtifactIdentity
            | "command-succeeded" -> text "command" node |> Result.map ExpectedReceipt.CommandSucceeded
            | "state-equals" -> both (text "key" node) (text "value" node) |> Result.map ExpectedReceipt.StateEquals
            | "conforms-to-contract" -> both (text "reference" node) (text "contract" node) |> Result.map ExpectedReceipt.ConformsToContract
            | "verification-satisfied" -> text "evaluator" node |> Result.map ExpectedReceipt.VerificationSatisfied
            | "transition-observed" -> text "transition" node |> Result.map ExpectedReceipt.TransitionObserved
            | "all-of" -> items "receipts" node |> traverse readExpected |> Result.map ExpectedReceipt.AllOf
            | other -> Error $"unknown expected receipt kind '{other}'")

    let readFact (node: JsonNode) : Result<ObservedFact, string> =
        text "kind" node
        |> Result.bind (fun kind ->
            match kind with
            | "artifact-observed" -> text "reference" node |> Result.map (fun r -> ObservedFact.ArtifactObserved(r, optionalText "digest" node))
            | "artifact-not-found" -> text "reference" node |> Result.map ObservedFact.ArtifactNotFound
            | "command-exited" -> both (text "command" node) (integer "exitCode" node) |> Result.map ObservedFact.CommandExited
            | "command-outcome-unknown" -> both (text "command" node) (text "reason" node) |> Result.map ObservedFact.CommandOutcomeUnknown
            | "state-observed" -> both (text "key" node) (text "value" node) |> Result.map ObservedFact.StateObserved
            | "contract-checked" ->
                both (both (text "reference" node) (text "contract" node)) (boolean "conforms" node)
                |> Result.map (fun ((r, c), ok) -> ObservedFact.ContractChecked(r, c, ok))
            | "verification-observed" -> both (text "evaluator" node) (boolean "passed" node) |> Result.map ObservedFact.VerificationObserved
            | "transition-recorded" -> text "transition" node |> Result.map ObservedFact.TransitionRecorded
            | "unobservable" -> both (text "subject" node) (text "reason" node) |> Result.map ObservedFact.Unobservable
            | other -> Error $"unknown observed fact kind '{other}'")

    let readObserved (node: JsonNode) : Result<ObservedReceipt, string> =
        let source =
            match optionalText "source" node, optionalText "observer" node |> Option.defaultValue "unknown" with
            | Some "host", o -> Ok(ObservationSource.Host o)
            | Some "independent", o -> Ok(ObservationSource.Independent o)
            | Some "self-reported", o
            | None, o -> Ok(ObservationSource.SelfReported o)
            | Some other, _ -> Error $"unknown observation source '{other}'"

        both source (items "facts" node |> traverse readFact)
        |> Result.map (fun (s, facts) -> { Source = s; Facts = facts; Narrative = optionalText "narrative" node })

    let rec readResult (node: JsonNode) : Result<ReceiptResult, string> =
        both (member' "expected" node |> Option.map readExpected |> Option.defaultValue (Error "expected is required")) (text "result" node)
        |> Result.bind (fun (e, outcome) ->
            let parsed =
                match outcome with
                | "match" -> Ok ReceiptOutcome.Match
                | "mismatch" -> Ok ReceiptOutcome.Mismatch
                | "indeterminate" -> Ok ReceiptOutcome.Indeterminate
                | other -> Error $"unknown receipt result '{other}'"

            both parsed (both (items "evidence" node |> traverse readFact) (items "constituents" node |> traverse readResult))
            |> Result.map (fun (o, (evidence, constituents)) ->
                { Expected = e
                  Outcome = o
                  Reason = optionalText "reason" node
                  Evidence = evidence
                  Constituents = constituents }))

    // ---- envelope ----

    let private boundary (b: MutationBoundary) : JsonNode =
        obj
            [ "scopes", arr (b.Scopes |> List.map str)
              "projections", arr (b.Projections |> List.map (fun p -> obj [ "scope", str p.Scope; "patterns", arr (p.Patterns |> List.map str) ]))
              "evaluatorReferences", arr (b.EvaluatorReferences |> List.map str) ]

    let private evaluator (e: EvaluatorIdentity) : JsonNode =
        obj
            [ "fingerprint", str e.Fingerprint
              "inputs", arr (e.Inputs |> List.map (fun i -> obj [ "kind", str i.Kind; "reference", str i.Reference; "digest", str i.Digest ])) ]

    let private containmentEvidence (c: Containment) : JsonNode =
        match c with
        | Containment.HostEnforced(m, rs, ev) -> obj [ "mechanism", str m; "restrictions", arr (rs |> List.map str); "evidence", arr (ev |> List.map str) ]
        | Containment.SemanticOnly m -> obj [ "mechanism", str m; "restrictions", arr []; "evidence", arr [] ]
        | Containment.Unknown -> null

    let envelope (e: ExecutionEnvelope) : JsonNode =
        let reason =
            match e.State with
            | ExecutionState.Blocked r
            | ExecutionState.Failed r
            | ExecutionState.Abandoned r -> Some r
            | _ -> None

        obj
            [ "schema", str Schema
              "executionId", str e.ExecutionId
              "workItem", str e.WorkItem
              "actor",
              obj
                  [ "id", str e.Actor.Id
                    "kind", str e.Actor.Kind
                    "provider", opt e.Actor.Provider
                    "model", opt e.Actor.Model
                    "runtime", opt e.Actor.Runtime ]
              "role", str (ExecutionRole.toWire e.Authority.Role)
              "capabilities", arr (RoleAuthority.effective e.Authority |> Set.toList |> List.map (Capability.toWire >> str))
              "prohibitions", arr (e.Authority.Prohibits |> Set.toList |> List.map (Capability.toWire >> str))
              "baselineRevision", str e.BaselineRevision
              "candidateRevision", opt e.CandidateRevision
              "workspace", opt (e.Workspace |> Option.map _.Id)
              "workspaceBinding",
              (match e.Workspace with
               | Some w -> obj [ "id", str w.Id; "branch", opt w.Branch; "path", opt w.Path; "mechanism", str w.Mechanism ]
               | None -> null)
              "containment", str (Containment.toWire e.Containment)
              "containmentDetail", containmentEvidence e.Containment
              "securitySandbox", JsonValue.Create(Containment.isSecuritySandbox e.Containment)
              "mutationBoundary", boundary e.Boundary
              "evaluator", (e.Evaluator |> Option.map evaluator |> Option.toObj)
              "humanOnlyTransitions", arr (e.HumanOnlyTransitions |> List.map str)
              "parentExecution", opt e.Parent
              "startedAt", str (timestamp e.StartedAt)
              "state", str (ExecutionState.toWire e.State)
              "stateReason", opt reason ]

    let private parseTime (raw: string) =
        match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
        | true, t -> Ok t
        | _ -> Error $"'{raw}' is not a timestamp"

    let readEnvelope (node: JsonNode) : Result<ExecutionEnvelope, string> =
        let actorNode = member' "actor" node |> Option.toObj
        let boundaryNode = member' "mutationBoundary" node |> Option.toObj

        let strings name n = items name n |> List.choose (fun x -> match x with :? JsonValue as v -> (match v.TryGetValue<string>() with | true, s -> Some s | _ -> None) | _ -> None)

        let role =
            text "role" node
            |> Result.bind (fun r -> ExecutionRole.tryParse r |> Option.map Ok |> Option.defaultValue (Error $"unknown role '{r}'"))

        let evaluatorResult =
            match member' "evaluator" node with
            | Some(:? JsonObject as e) ->
                items "inputs" e
                |> traverse (fun i ->
                    both (both (text "kind" i) (text "reference" i)) (text "digest" i)
                    |> Result.map (fun ((k, r), d) -> { Kind = k; Reference = r; Digest = d }))
                |> Result.bind EvaluatorIdentity.create
                |> Result.map Some
            | _ -> Ok None

        let state =
            let reason = optionalText "stateReason" node |> Option.defaultValue ""

            text "state" node
            |> Result.bind (fun s ->
                match s with
                | "active" -> Ok ExecutionState.Active
                | "blocked" -> Ok(ExecutionState.Blocked reason)
                | "completed" -> Ok ExecutionState.Completed
                | "failed" -> Ok(ExecutionState.Failed reason)
                | "abandoned" -> Ok(ExecutionState.Abandoned reason)
                | "interrupted" -> Ok ExecutionState.Interrupted
                | other -> Error $"unknown execution state '{other}'")

        let workspace =
            match member' "workspaceBinding" node with
            | Some(:? JsonObject as w) ->
                text "id" w
                |> Result.map (fun id ->
                    Some
                        { Id = id
                          Branch = optionalText "branch" w
                          Path = optionalText "path" w
                          Mechanism = optionalText "mechanism" w |> Option.defaultValue "unknown" })
            | _ -> Ok None

        let containment =
            match optionalText "containment" node, member' "containmentDetail" node with
            | Some "semantic-only", Some d -> Containment.SemanticOnly(optionalText "mechanism" d |> Option.defaultValue "unknown")
            | Some "host-enforced", Some d ->
                Containment.HostEnforced(optionalText "mechanism" d |> Option.defaultValue "unknown", strings "restrictions" d, strings "evidence" d)
            | _ -> Containment.Unknown

        both (both (both (text "executionId" node) (text "workItem" node)) (both role evaluatorResult)) (both (both state workspace) (both (text "baselineRevision" node) (text "startedAt" node |> Result.bind parseTime)))
        |> Result.bind (fun (((id, work), (role, ev)), ((st, ws), (baseline, started))) ->
            text "id" actorNode
            |> Result.map (fun actorId ->
                { ExecutionId = id
                  WorkItem = work
                  Actor =
                    { Id = actorId
                      Kind = optionalText "kind" actorNode |> Option.defaultValue "unknown"
                      Provider = optionalText "provider" actorNode
                      Model = optionalText "model" actorNode
                      Runtime = optionalText "runtime" actorNode }
                  Authority = RoleAuthority.defaultFor role
                  BaselineRevision = baseline
                  CandidateRevision = optionalText "candidateRevision" node
                  Workspace = ws
                  Containment = containment
                  Boundary =
                    { Scopes = strings "scopes" boundaryNode
                      Projections =
                        items "projections" boundaryNode
                        |> List.choose (fun p -> optionalText "scope" p |> Option.map (fun s -> { Scope = s; Patterns = strings "patterns" p }))
                      EvaluatorReferences = strings "evaluatorReferences" boundaryNode }
                  Evaluator = ev
                  HumanOnlyTransitions = strings "humanOnlyTransitions" node
                  Parent = optionalText "parentExecution" node
                  StartedAt = started
                  State = st }))

    // ---- ledger entries ----

    let private reconciliation (r: Reconciliation) =
        match r with
        | Reconciliation.Occurred e -> "occurred", e
        | Reconciliation.DidNotOccur e -> "did-not-occur", e
        | Reconciliation.StillUnknown e -> "unknown", e

    let entry (e: StepEntry) : JsonNode =
        match e with
        | StepEntry.Declared(s, seq, name, deps, exp, safe, at) ->
            obj
                [ "entry", str "declared"
                  "step", str s
                  "sequence", JsonValue.Create seq
                  "name", str name
                  "dependsOn", arr (deps |> List.map str)
                  "expected", expected exp
                  "retrySafe", opt safe
                  "at", str (timestamp at) ]
        | StepEntry.Started(s, n, at) -> obj [ "entry", str "started"; "step", str s; "attempt", JsonValue.Create n; "at", str (timestamp at) ]
        | StepEntry.Observed(s, n, o, r, at) ->
            obj
                [ "entry", str "observed"
                  "step", str s
                  "attempt", JsonValue.Create n
                  "observed", observed o
                  "comparison", result r
                  "at", str (timestamp at) ]
        | StepEntry.Reconciled(s, n, f, at) ->
            let token, detail = reconciliation f

            obj
                [ "entry", str "reconciled"
                  "step", str s
                  "attempt", JsonValue.Create n
                  "finding", str token
                  "detail", str detail
                  "at", str (timestamp at) ]

    let readEntry (node: JsonNode) : Result<StepEntry, string> =
        let at () = text "at" node |> Result.bind parseTime

        text "entry" node
        |> Result.bind (fun kind ->
            match kind with
            | "declared" ->
                both (both (text "step" node) (integer "sequence" node)) (both (text "name" node) (member' "expected" node |> Option.map readExpected |> Option.defaultValue (Error "expected is required")))
                |> Result.bind (fun ((s, seq), (name, exp)) ->
                    at ()
                    |> Result.map (fun t ->
                        let deps = items "dependsOn" node |> List.choose (fun x -> match x with :? JsonValue as v -> (match v.TryGetValue<string>() with | true, d -> Some d | _ -> None) | _ -> None)
                        StepEntry.Declared(s, seq, name, deps, exp, optionalText "retrySafe" node, t)))
            | "started" -> both (both (text "step" node) (integer "attempt" node)) (at ()) |> Result.map (fun ((s, n), t) -> StepEntry.Started(s, n, t))
            | "observed" ->
                both
                    (both (text "step" node) (integer "attempt" node))
                    (both (member' "observed" node |> Option.map readObserved |> Option.defaultValue (Error "observed is required")) (member' "comparison" node |> Option.map readResult |> Option.defaultValue (Error "comparison is required")))
                |> Result.bind (fun ((s, n), (o, r)) -> at () |> Result.map (fun t -> StepEntry.Observed(s, n, o, r, t)))
            | "reconciled" ->
                both (both (text "step" node) (integer "attempt" node)) (both (text "finding" node) (text "detail" node))
                |> Result.bind (fun ((s, n), (finding, detail)) ->
                    let f =
                        match finding with
                        | "occurred" -> Ok(Reconciliation.Occurred detail)
                        | "did-not-occur" -> Ok(Reconciliation.DidNotOccur detail)
                        | "unknown" -> Ok(Reconciliation.StillUnknown detail)
                        | other -> Error $"unknown reconciliation finding '{other}'"

                    both f (at ()) |> Result.map (fun (f, t) -> StepEntry.Reconciled(s, n, f, t)))
            | other -> Error $"unknown ledger entry '{other}'")

    let legalActions (actions: LegalAction list) : JsonNode =
        arr (
            actions
            |> List.map (fun a ->
                obj
                    [ "transition", str a.Transition
                      "target", opt a.Target
                      "available", JsonValue.Create a.Available
                      "reasons", arr (a.Reasons |> List.map str)
                      "actorRequirement", str a.ActorRequirement ])
        )

    let scopeEffect (e: ScopeEffect) : JsonNode =
        let classification =
            match e.Classification with
            | MutationClass.Within s -> "within:" + s
            | MutationClass.Outside -> "outside-boundary"
            | MutationClass.EvaluatorAuthority -> "evaluator-authority"

        obj [ "resource", str e.Resource; "classification", str classification; "explanation", opt e.Explanation ]
