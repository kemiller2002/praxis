namespace Praxis.Infrastructure.Construction

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ordo.Construction

type HandoffObligation =
    { Id: string; Kind: string; Statement: string; RequirementKeys: string list; NodeIds: string list; ExtensionSurface: string list }

/// Exact text of one cited requirement, read from the admitted pinned source;
/// None when no committed file holds bytes with the pinned digest.
type HandoffRequirement = { Key: string; Excerpt: string option }

type HandoffDecision = { NodeId: string; Summary: string }

/// One generated interface the agent implements: only the members its
/// contracts name, as declared in the generated source.
type HandoffInterface =
    { Interface: string; DeclaredIn: string; Members: string list; Contracts: string list; ObligationIds: string list; ImplementIn: string list }

type HandoffPermittedFile = { Path: string; Contracts: string list; MaxBytes: int }

type HandoffQuestion = { Subject: string; Question: string }

type HandoffAcceptance = { Id: string; Expectation: string; RequirementKeys: string list }

/// `praxis.construction-handoff/1` (CONSTRUCT-008): the minimal brief a
/// construction agent needs after mechanical generation. Deliberately no
/// vocabulary manual, digests, serialization instructions or full source.
type ConstructionHandoff =
    { CohortId: string
      Obligations: HandoffObligation list
      Requirements: HandoffRequirement list
      Decisions: HandoffDecision list
      Interfaces: HandoffInterface list
      PermittedFiles: HandoffPermittedFile list
      UnresolvedQuestions: HandoffQuestion list
      Acceptance: HandoffAcceptance list }

[<RequireQualifiedAccess>]
module ConstructionHandoff =
    [<Literal>]
    let Schema = "praxis.construction-handoff/1"

    // ------------------------------------------------------------ excerpts

    type ManifestRequirement = { Key: string; Document: string; Location: string; ContentDigest: string }

    /// Requirement entries of an ECIR source manifest; unreadable entries are skipped.
    let manifestRequirements (manifestText: string) : ManifestRequirement list =
        try
            match JsonNode.Parse manifestText with
            | :? JsonObject as root ->
                match root["requirements"] with
                | :? JsonArray as items ->
                    items |> Seq.choose (function
                        | :? JsonObject as item ->
                            let text (name: string) = match item[name] with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None
                            match text "key", text "document", text "location", text "contentDigest" with
                            | Some key, Some document, Some location, Some digest -> Some { Key = key; Document = document; Location = location; ContentDigest = digest }
                            | _ -> None
                        | _ -> None) |> Seq.toList
                | _ -> []
            | _ -> []
        with _ -> []

    /// `L116-L129` or `L104` as an inclusive 1-based line range.
    let private lineRange (location: string) =
        let number (text: string) = if text.StartsWith "L" then (match Int32.TryParse(text.Substring 1) with | true, n when n > 0 -> Some n | _ -> None) else None
        match location.Split('-') with
        | [| single |] -> number single |> Option.map (fun n -> n, n)
        | [| first; last |] -> Option.map2 (fun a b -> a, b) (number first) (number last) |> Option.filter (fun (a, b) -> a <= b)
        | _ -> None

    /// Candidate excerpt texts at the requirement's own document path, when
    /// the governed repository carries that document.
    let private fromDocument (content: string) (location: string) =
        match lineRange location with
        | None -> []
        | Some(first, last) ->
            let lines = content.Replace("\r\n", "\n").Split('\n')
            if last > lines.Length then []
            else
                let joined = String.Join("\n", lines.[first - 1 .. last - 1])
                [ joined + "\n"; joined ]

    let private maxScanBytes = 65536L
    let private maxScanReads = 512

    /// Exact excerpt text for each wanted requirement, verified against the
    /// pinned content digest in the source manifest. `readFile` reads one
    /// committed file of the admitted source commit; `files` lists them.
    let excerpts (readFile: string -> Result<string, string>) (files: (string * int64) list) (manifest: ManifestRequirement list) (keys: string list) : HandoffRequirement list =
        let wanted = keys |> List.distinct |> List.choose (fun key -> manifest |> List.tryFind (fun r -> r.Key = key))
        let documentPath (document: string) =
            let parts = document.Split('/')
            if parts.Length > 2 then Some(String.Join("/", parts |> Array.skip 2)) else None
        let present = files |> List.map fst |> Set.ofList
        let byDocument =
            wanted |> List.choose (fun r ->
                documentPath r.Document |> Option.filter present.Contains |> Option.bind (fun path ->
                    match readFile path with
                    | Ok content -> fromDocument content r.Location |> List.tryFind (fun text -> ConstructionTree.digest text = r.ContentDigest) |> Option.map (fun text -> r.Key, text)
                    | Error _ -> None))
            |> Map.ofList
        let missing = wanted |> List.filter (fun r -> not (byDocument.ContainsKey r.Key))
        let byContent =
            if missing.IsEmpty then Map.empty
            else
                let digests = missing |> List.map (fun r -> r.ContentDigest, r.Key) |> List.groupBy fst |> List.map (fun (d, ks) -> d, ks |> List.map snd) |> Map.ofList
                let candidates =
                    files
                    |> List.filter (fun (path, size) -> size > 0L && size <= maxScanBytes && not (path.StartsWith "."))
                    |> List.sortBy (fun (path, _) -> (if path.Contains("requirement", StringComparison.OrdinalIgnoreCase) then 0 else 1), path)
                    |> List.truncate maxScanReads
                candidates |> List.fold (fun (found: Map<string, string>) (path, _) ->
                    if missing |> List.forall (fun r -> found.ContainsKey r.Key) then found
                    else
                        match readFile path with
                        | Ok content ->
                            match digests |> Map.tryFind (ConstructionTree.digest content) with
                            | Some ks -> ks |> List.fold (fun (acc: Map<string, string>) k -> if acc.ContainsKey k then acc else acc.Add(k, content)) found
                            | None -> found
                        | Error _ -> found) Map.empty
        keys |> List.distinct |> List.map (fun key ->
            { Key = key; Excerpt = byDocument |> Map.tryFind key |> Option.orElse (byContent |> Map.tryFind key) })

    // ----------------------------------------------------------- decisions

    let private texts (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as o ->
            match o[name] with
            | :? JsonArray as items -> items |> Seq.choose (function :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None) |> Seq.toList
            | _ -> []
        | _ -> []

    let private text (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as o -> match o[name] with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None
        | _ -> None

    let rec private citedKeys (node: JsonNode) : string list =
        match node with
        | :? JsonObject as o -> (texts node "requirementKeys") @ (o |> Seq.collect (fun pair -> if pair.Key = "requirementKeys" || isNull pair.Value then [] else citedKeys pair.Value) |> Seq.toList)
        | :? JsonArray as a -> a |> Seq.collect (fun item -> if isNull item then [] else citedKeys item) |> Seq.toList
        | _ -> []

    let private items (node: JsonNode) (name: string) =
        match node with
        | :? JsonObject as o -> match o[name] with :? JsonArray as a -> a |> Seq.filter (isNull >> not) |> Seq.toList | _ -> []
        | _ -> []

    let private summarize (content: JsonNode) =
        match text content "kind" with
        | Some "stateModel" ->
            let states =
                items content "states" |> List.map (fun s ->
                    let name = text s "name" |> Option.defaultValue "?"
                    match text s "role" with Some role when role <> "ordinary" -> sprintf "%s (%s)" name role | _ -> name)
            let transitions =
                items content "transitions" |> List.map (fun t ->
                    sprintf "%s: %s -> %s" (text t "command" |> Option.defaultValue "?") (text t "from" |> Option.defaultValue "?") (text t "to" |> Option.defaultValue "?"))
            sprintf "Workflow %s. States: %s. Transitions: %s." (text content "workflow" |> Option.defaultValue "?") (String.concat ", " states) (String.concat "; " transitions)
        | Some "portContract" ->
            let ports =
                items content "ports" |> List.map (fun p ->
                    let name = text p "port" |> Option.defaultValue "?"
                    match text p "command" with
                    | Some command ->
                        let failure =
                            match p with
                            | :? JsonObject as o when o.ContainsKey "onFailure" ->
                                match o["onFailure"] with
                                | null -> "failure handling NOT DECIDED"
                                | value -> "on failure: " + value.ToJsonString().Trim('"')
                            | _ -> "failure handling NOT DECIDED"
                        sprintf "%s for %s (%s)" name command failure
                    | None -> name)
            "Ports: " + String.concat "; " ports + "."
        | Some other -> "Approved " + other + " record."
        | None -> "Approved record."

    /// Approved decision records relevant to the obligations: those an
    /// obligation names, or whose content cites a key an obligation cites.
    let decisions (decisionRecords: string) (obligations: HandoffObligation list) : HandoffDecision list =
        let nodes = obligations |> List.collect _.NodeIds |> Set.ofList
        let keys = obligations |> List.collect _.RequirementKeys |> Set.ofList
        try
            items (JsonNode.Parse decisionRecords) "records"
            |> List.choose (fun record ->
                match text record "nodeId", (match record with :? JsonObject as o -> o["content"] | _ -> null) with
                | Some nodeId, content when not (isNull content) ->
                    if nodes.Contains nodeId || citedKeys content |> List.exists keys.Contains then Some { NodeId = nodeId; Summary = summarize content }
                    else None
                | _ -> None)
        with _ -> []

    // ---------------------------------------------------------- interfaces

    /// `Iface.Member/qualifier` -> (Iface, Member).
    let private contractParts (contract: string) =
        match contract.IndexOf '.' with
        | -1 -> None
        | at ->
            let memberName = contract.Substring(at + 1).Split('/').[0]
            Some(contract.Substring(0, at), memberName)

    /// The `abstract` member declarations of `type NAME =` in generated
    /// source, keyed by member name, each with its continuation lines.
    let private declaredMembers (source: string) (name: string) : (string * string) list =
        let lines = source.Replace("\r\n", "\n").Split('\n')
        let indent (line: string) = line.Length - line.TrimStart().Length
        match lines |> Array.tryFindIndex (fun line -> line.Trim() = sprintf "type %s =" name) with
        | None -> []
        | Some start ->
            let baseIndent = indent lines[start]
            let body =
                lines |> Array.skip (start + 1)
                |> Array.takeWhile (fun line -> String.IsNullOrWhiteSpace line || indent line > baseIndent)
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
            let memberIndent = body |> Array.tryHead |> Option.map indent |> Option.defaultValue 0
            body |> Array.fold (fun (acc: (string * string) list) line ->
                let trimmed = line.Trim()
                if indent line = memberIndent && trimmed.StartsWith "abstract " then
                    let memberName = trimmed.Substring(9).Split([| ':'; ' ' |], StringSplitOptions.RemoveEmptyEntries).[0]
                    acc @ [ memberName, trimmed ]
                elif indent line > memberIndent && not acc.IsEmpty then
                    let (n, text) = List.last acc
                    (acc |> List.take (acc.Length - 1)) @ [ n, text + " " + trimmed ]
                else acc) []

    /// Interface signatures for the routed custom contracts, extracted from
    /// the generated source. `generated` is (placed path, content).
    let interfaces (generated: (string * string) list) (routed: RoutedImplementation list) : HandoffInterface list =
        routed
        |> List.choose (fun r -> contractParts r.Contract |> Option.map (fun (iface, memberName) -> iface, memberName, r))
        |> List.groupBy (fun (iface, _, _) -> iface)
        |> List.map (fun (iface, group) ->
            let declaring = generated |> List.tryFind (fun (_, content) -> not (List.isEmpty (declaredMembers content iface)))
            let declared = declaring |> Option.map (fun (_, content) -> declaredMembers content iface) |> Option.defaultValue []
            let wanted = group |> List.map (fun (_, m, _) -> m) |> List.distinct
            { Interface = iface
              DeclaredIn = declaring |> Option.map fst |> Option.defaultValue "(not found in generated source)"
              Members = wanted |> List.map (fun m -> declared |> List.tryFind (fun (n, _) -> n = m) |> Option.map snd |> Option.defaultValue (sprintf "abstract %s: (declaration not found in generated source)" m))
              Contracts = group |> List.map (fun (_, _, r) -> r.Contract) |> List.distinct
              ObligationIds = group |> List.map (fun (_, _, r) -> r.Obligation.Id) |> List.distinct
              ImplementIn = group |> List.map (fun (_, _, r) -> r.SlotPath) |> List.distinct })

    // -------------------------------------------------------------- build

    let private questionKinds = set [ "undecidedEffectFailure"; "conflict"; "unsupportedVocabulary" ]

    let build (cohortId: string) (obligations: DefaultPlanObligation list) (requirements: HandoffRequirement list) (decisionRecords: string)
              (generated: (string * string) list) (routing: ConstructionRouting) (acceptanceValidators: string list) : ConstructionHandoff =
        let handoffObligations =
            obligations |> List.map (fun o ->
                { Id = o.Id; Kind = o.Kind; Statement = o.Statement; RequirementKeys = o.RequirementKeys; NodeIds = o.NodeIds; ExtensionSurface = o.ExtensionSurface })
        let questions =
            (obligations |> List.filter (fun o -> questionKinds.Contains o.Kind) |> List.map (fun o -> { Subject = o.Id; Question = o.Statement }))
            @ (routing.Semantic |> List.choose (fun o ->
                match o.Kind with
                | Artifacts.ResolveUnresolved(_, q) | Artifacts.ResolveEffectFailure(_, _, q) -> Some { Subject = o.Id; Question = q }
                | _ -> None))
            @ (requirements |> List.filter (fun r -> r.Excerpt.IsNone) |> List.map (fun r ->
                { Subject = r.Key; Question = "The admitted source holds no text with this requirement's pinned content; ask the operator for it rather than guessing." }))
            |> List.distinctBy _.Question
        let usable = ConstructionRouting.usableSlots routing
        { CohortId = cohortId
          Obligations = handoffObligations
          Requirements = requirements
          Decisions = decisions decisionRecords handoffObligations
          Interfaces = interfaces generated routing.Custom
          PermittedFiles = usable |> List.map (fun slot -> { Path = ConstructionTree.placed routing.Mount slot.File; Contracts = slot.Contracts; MaxBytes = slot.MaxBytes })
          UnresolvedQuestions = questions
          Acceptance =
            (routing.Verification |> List.map (fun o ->
                let statement = match o.Kind with Artifacts.VerifyIndependently what -> what | _ -> "Independent verification."
                { Id = o.Id; Expectation = statement; RequirementKeys = o.RequirementKeys }))
            @ (acceptanceValidators |> List.map (fun id ->
                { Id = id; Expectation = "The host acceptance validator " + id + " judges the result; it is not run by the agent."; RequirementKeys = [] }))
            @ [ { Id = "boundary"; Expectation = "Only the permitted files change. Generated files, project and dependency files stay untouched; anything else is refused."; RequirementKeys = [] }
                { Id = "build"; Expectation = "Every listed interface member is implemented in a permitted file so the generated code compiles; there is no default implementation."; RequirementKeys = [] } ] }

    // ------------------------------------------------------------- render

    let private str (value: string) = JsonValue.Create value :> JsonNode
    let private strs (values: string list) = JsonArray(values |> List.map str |> List.toArray) :> JsonNode
    let private obj (fields: (string * JsonNode) list) =
        let node = JsonObject()
        fields |> List.iter (fun (name, value) -> node[name] <- value)
        node
    let private arr (nodes: JsonNode list) = JsonArray(nodes |> List.toArray) :> JsonNode

    let toJson (handoff: ConstructionHandoff) : JsonObject =
        obj [ "schemaVersion", str Schema
              "cohortId", str handoff.CohortId
              "obligations", arr (handoff.Obligations |> List.map (fun o ->
                  obj [ "id", str o.Id; "kind", str o.Kind; "question", str o.Statement; "requirementKeys", strs o.RequirementKeys
                        "nodeIds", strs o.NodeIds; "extensionSurface", strs o.ExtensionSurface ] :> JsonNode))
              "requirements", arr (handoff.Requirements |> List.map (fun r ->
                  obj [ "key", str r.Key; "excerpt", (match r.Excerpt with Some e -> str e | None -> null) ] :> JsonNode))
              "decisions", arr (handoff.Decisions |> List.map (fun d -> obj [ "nodeId", str d.NodeId; "summary", str d.Summary ] :> JsonNode))
              "extensionInterfaces", arr (handoff.Interfaces |> List.map (fun i ->
                  obj [ "interface", str i.Interface; "declaredIn", str i.DeclaredIn; "members", strs i.Members; "contracts", strs i.Contracts
                        "obligationIds", strs i.ObligationIds; "implementIn", strs i.ImplementIn ] :> JsonNode))
              "permittedFiles", arr (handoff.PermittedFiles |> List.map (fun f ->
                  obj [ "path", str f.Path; "contracts", strs f.Contracts; "maxBytes", JsonValue.Create f.MaxBytes ] :> JsonNode))
              "unresolvedQuestions", arr (handoff.UnresolvedQuestions |> List.map (fun q -> obj [ "subject", str q.Subject; "question", str q.Question ] :> JsonNode))
              "acceptance", arr (handoff.Acceptance |> List.map (fun a ->
                  obj [ "id", str a.Id; "expectation", str a.Expectation; "requirementKeys", strs a.RequirementKeys ] :> JsonNode)) ]

    /// The human-readable twin. Same content, no additions.
    let toMarkdown (handoff: ConstructionHandoff) : string =
        let out = StringBuilder()
        let line (text: string) = out.Append(text).Append('\n') |> ignore
        let keys (values: string list) = if values.IsEmpty then "" else " Requirements: " + String.concat ", " (values |> List.map (sprintf "`%s`")) + "."
        line (sprintf "# Construction handoff: %s" handoff.CohortId)
        line ""
        line "## Obligations"
        line ""
        if handoff.Obligations.IsEmpty then line "None."
        for o in handoff.Obligations do
            let surface = if o.ExtensionSurface.IsEmpty then "" else " Surface: " + String.concat ", " (o.ExtensionSurface |> List.map (sprintf "`%s`")) + "."
            line (sprintf "- **%s** (%s): %s%s%s" o.Id o.Kind o.Statement (keys o.RequirementKeys) surface)
        line ""
        line "## Requirement excerpts"
        for r in handoff.Requirements do
            line ""
            line (sprintf "### `%s`" r.Key)
            line ""
            match r.Excerpt with
            | Some excerpt ->
                line "```text"
                line (excerpt.TrimEnd('\n'))
                line "```"
            | None -> line "_Not available in the admitted source; see unresolved questions._"
        line ""
        line "## Approved decisions"
        line ""
        if handoff.Decisions.IsEmpty then line "None relevant."
        for d in handoff.Decisions do line (sprintf "- **%s**: %s" d.NodeId d.Summary)
        line ""
        line "## Extension interfaces to implement"
        for i in handoff.Interfaces do
            line ""
            line (sprintf "### `%s` (declared in `%s`; implement in %s)" i.Interface i.DeclaredIn (String.concat ", " (i.ImplementIn |> List.map (sprintf "`%s`"))))
            line ""
            line "```fsharp"
            for m in i.Members do line m
            line "```"
            line ""
            line (sprintf "Obligations: %s." (String.concat ", " i.ObligationIds))
        line ""
        line "## Permitted files"
        line ""
        for f in handoff.PermittedFiles do line (sprintf "- `%s` (at most %d bytes): %s" f.Path f.MaxBytes (String.concat ", " f.Contracts))
        line ""
        line "## Unresolved questions"
        line ""
        if handoff.UnresolvedQuestions.IsEmpty then line "None."
        handoff.UnresolvedQuestions |> List.iteri (fun index q -> line (sprintf "%d. **%s**: %s" (index + 1) q.Subject q.Question))
        line ""
        line "## Acceptance expectations"
        line ""
        for a in handoff.Acceptance do line (sprintf "- **%s**: %s%s" a.Id a.Expectation (keys a.RequirementKeys))
        out.ToString()
