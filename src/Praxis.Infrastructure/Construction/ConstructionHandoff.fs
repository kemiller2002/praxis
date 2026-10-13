namespace Praxis.Infrastructure.Construction

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Ordo.Construction
open Praxis.Infrastructure.Planning

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

type HandoffPermittedFile = { Path: string; Kind: string; Contracts: string list; MaxBytes: int }

/// A frozen acceptance (oracle adapter) contract the agent fills: the
/// public signatures of the existing stub at the admitted source commit.
type HandoffAcceptanceAdapter = { Contract: string; Path: string; Signatures: string list }

/// Public declarations of one generated file: type definitions in full and
/// module function signatures without bodies.
type HandoffGeneratedApi = { File: string; Namespace: string; Declarations: string list }

/// An open question to escalate. `FromObligation` marks one whose text is the
/// obligation's own (listed once, under the obligation id).
type HandoffQuestion = { Subject: string; Question: string; FromObligation: bool }

type HandoffAcceptance = { Label: string; Expectation: string; RequirementKeys: string list }

/// `praxis.construction-handoff/2` (CONSTRUCT-008): the minimal brief a
/// construction agent needs after mechanical generation. Deliberately no
/// vocabulary manual, digests, serialization instructions or full source.
type ConstructionHandoff =
    { CohortId: string
      Obligations: HandoffObligation list
      Requirements: HandoffRequirement list
      Decisions: HandoffDecision list
      Interfaces: HandoffInterface list
      AcceptanceAdapters: HandoffAcceptanceAdapter list
      GeneratedApi: HandoffGeneratedApi list
      PermittedFiles: HandoffPermittedFile list
      UnresolvedQuestions: HandoffQuestion list
      Acceptance: HandoffAcceptance list
      AllowedOperations: string list }

[<RequireQualifiedAccess>]
module ConstructionHandoff =
    [<Literal>]
    let Schema = "praxis.construction-handoff/2"

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

    // ------------------------------------------------------ generated API

    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    /// A `let` line without its body: `let f (x: T) : R` (private lets are None).
    let private letSignature (trimmed: string) =
        if not (trimmed.StartsWith "let ") || trimmed.StartsWith "let private " || trimmed.StartsWith "let internal " then None
        elif trimmed.EndsWith " =" then Some(trimmed.Substring(0, trimmed.Length - 2))
        else
            match trimmed.IndexOf " = " with
            | -1 -> Some trimmed
            | at -> Some(trimmed.Substring(0, at))

    let private identifiers (text: string) =
        RegularExpressions.Regex.Matches(text, "[A-Za-z_][A-Za-z0-9_]*") |> Seq.map _.Value |> Set.ofSeq

    /// Top-level declarations of one generated F# file, in order: (declared
    /// type name or None, rendered text). Types are kept whole (a member body,
    /// if any, is cut); modules keep doc comments and public `let` signatures.
    let private declarations (source: string) : string option * (string option * string) list =
        let lines = source.Replace("\r\n", "\n").Split('\n')
        let ns = lines |> Array.tryFind (fun l -> l.StartsWith "namespace ") |> Option.map (fun l -> l.Substring(10).Trim())
        let starts =
            lines |> Array.indexed |> Array.choose (fun (i, l) ->
                if indentOf l = 0 && (l.StartsWith "type " || l.StartsWith "and " || l.StartsWith "module ") then Some i else None)
        let blocks =
            starts |> Array.toList |> List.mapi (fun n start ->
                let finish = if n + 1 < starts.Length then starts[n + 1] else lines.Length
                // Leading doc comments and attributes belong to the declaration.
                let mutable lead = start
                while lead > 0 && (lines[lead - 1].StartsWith "///" || lines[lead - 1].StartsWith "[<") do lead <- lead - 1
                let header = lines[lead .. start - 1] |> Array.toList
                let body = lines[start + 1 .. finish - 1] |> Array.toList
                // Trailing doc/attribute lines belong to the next declaration.
                let body = body |> List.rev |> List.skipWhile (fun l -> String.IsNullOrWhiteSpace l || l.StartsWith "///" || l.StartsWith "[<") |> List.rev
                let head = lines[start]
                if head.StartsWith "module " then
                    let memberIndent = body |> List.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.map indentOf |> Option.defaultValue 4
                    let kept =
                        body |> List.fold (fun (acc, docs) line ->
                            let trimmed = line.Trim()
                            if indentOf line <> memberIndent || String.IsNullOrWhiteSpace line then acc, (if String.IsNullOrWhiteSpace line then [] else docs)
                            elif trimmed.StartsWith "///" then acc, docs @ [ line ]
                            else
                                match letSignature trimmed with
                                | Some signature -> acc @ docs @ [ String(' ', memberIndent) + signature ], []
                                | None -> acc, []) ([], [])
                        |> fst
                    None, String.concat "\n" (header @ [ head.TrimEnd() ] @ kept)
                else
                    let name = head.Split([| ' '; '<'; '=' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryItem 1
                    let memberIndent = body |> List.tryFind (String.IsNullOrWhiteSpace >> not) |> Option.map indentOf |> Option.defaultValue 4
                    let kept =
                        body |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))
                        |> List.fold (fun (acc, skipping) line ->
                            let trimmed = line.Trim()
                            if skipping && indentOf line > memberIndent then acc, true
                            elif indentOf line = memberIndent && (trimmed.StartsWith "member " || trimmed.StartsWith "static member " || trimmed.StartsWith "override ") && trimmed.Contains " =" then
                                acc @ [ line.Substring(0, line.IndexOf " =") ], true
                            else acc @ [ line ], false) ([], false)
                        |> fst
                    name, String.concat "\n" (header @ [ head.TrimEnd() ] @ kept))
        ns, blocks

    /// Generated API (signatures only): the types the extension interfaces and
    /// public module signatures name, closed over the types they mention.
    let generatedApi (generated: (string * string) list) (interfaces: HandoffInterface list) : HandoffGeneratedApi list =
        let files = generated |> List.filter (fun (path, _) -> path.EndsWith ".fs") |> List.map (fun (path, content) -> path, declarations content)
        let types = files |> List.collect (fun (_, (_, blocks)) -> blocks |> List.choose (fun (name, text) -> name |> Option.map (fun n -> n, text))) |> Map.ofList
        let modules = files |> List.collect (fun (_, (_, blocks)) -> blocks |> List.filter (fst >> Option.isNone) |> List.map snd)
        let seeds =
            (interfaces |> List.collect _.Members) @ modules
            |> List.map identifiers |> Set.unionMany |> Set.filter types.ContainsKey
        let rec close (known: Set<string>) =
            let next = known |> Seq.map (fun name -> identifiers types[name] |> Set.filter types.ContainsKey) |> Set.unionMany |> Set.union known
            if next = known then known else close next
        let wanted = close seeds
        files |> List.choose (fun (path, (ns, blocks)) ->
            let kept = blocks |> List.filter (fun (name, _) -> match name with Some n -> wanted.Contains n | None -> true) |> List.map snd
            if kept.IsEmpty then None else Some { File = path; Namespace = defaultArg ns ""; Declarations = kept })

    /// Public surface of an acceptance adapter stub: doc comments and public
    /// `let` signatures, no bodies.
    let adapterSignatures (content: string) : string list =
        content.Replace("\r\n", "\n").Split('\n') |> Array.toList
        |> List.choose (fun line ->
            let trimmed = line.Trim()
            if trimmed.StartsWith "///" then Some trimmed
            else letSignature trimmed)

    // -------------------------------------------------------------- build

    /// Obligation kinds that ask a question an agent may not settle in code.
    let QuestionKinds = set [ "undecidedEffectFailure"; "conflict"; "uncoveredRequirement"; "unsupportedVocabulary" ]

    /// Every requirement key the handoff cites: obligations and acceptance.
    let handoffKeys (obligations: DefaultPlanObligation list) (routing: ConstructionRouting) =
        (obligations |> List.collect _.RequirementKeys) @ (routing.Verification |> List.collect _.RequirementKeys) |> List.distinct

    let DispositionRule =
        "Disposition every obligation in dispositions.json as implemented or escalated. A conflict, undecidedEffectFailure or "
        + "uncoveredRequirement obligation cannot be implemented without an approved decision: escalate it, and describe any "
        + "provisional code behavior in its note."

    let build (cohortId: string) (obligations: DefaultPlanObligation list) (requirements: HandoffRequirement list) (decisionRecords: string)
              (generated: (string * string) list) (routing: ConstructionRouting) (adapters: (ConstructionAcceptanceSlot * string) list)
              (acceptanceValidators: string list) : ConstructionHandoff =
        let handoffObligations =
            obligations |> List.map (fun o ->
                { Id = o.Id; Kind = o.Kind; Statement = o.Statement; RequirementKeys = o.RequirementKeys; NodeIds = o.NodeIds; ExtensionSurface = o.ExtensionSurface })
        let asked = obligations |> List.filter (fun t -> QuestionKinds.Contains t.Kind)
        let askedKeys = asked |> List.collect _.RequirementKeys |> Set.ofList
        let questions =
            (asked |> List.map (fun o -> { Subject = o.Id; Question = o.Statement; FromObligation = true }))
            // A plan-carried resolve whose requirements a typed question already
            // asks about is the same question; ask it once.
            @ (routing.Semantic
               |> List.filter (fun o -> o.RequirementKeys.IsEmpty || not (Set.isSubset (Set.ofList o.RequirementKeys) askedKeys))
               |> List.choose (fun o ->
                   match o.Kind with
                   | Artifacts.ResolveUnresolved(subject, q) | Artifacts.ResolveEffectFailure(_, subject, q) -> Some { Subject = subject; Question = q; FromObligation = false }
                   | _ -> None))
            @ (requirements |> List.filter (fun r -> r.Excerpt.IsNone) |> List.map (fun r ->
                { Subject = r.Key; Question = "The admitted source holds no text with this requirement's pinned content; ask the operator for it rather than guessing."; FromObligation = false }))
            |> List.distinctBy _.Question
        let usable = ConstructionRouting.usableSlots routing
        let interfaces = interfaces generated routing.Custom
        let permitted =
            usable |> List.map (fun slot ->
                { Path = ConstructionTree.placed routing.Mount slot.File
                  Kind = (if ConstructionRouting.isAcceptance routing slot then "acceptanceAdapter" else "constructionContract")
                  Contracts = slot.Contracts; MaxBytes = slot.MaxBytes })
        let flat = permitted |> List.map (fun f -> f.Path.Substring(f.Path.LastIndexOf '/' + 1))
        { CohortId = cohortId
          Obligations = handoffObligations
          Requirements = requirements
          Decisions = decisions decisionRecords handoffObligations
          Interfaces = interfaces
          AcceptanceAdapters =
            adapters |> List.map (fun (slot, content) ->
                { Contract = slot.AcceptanceContract; Path = slot.AcceptancePath; Signatures = adapterSignatures content })
          GeneratedApi = generatedApi generated interfaces
          PermittedFiles = permitted
          UnresolvedQuestions = questions
          Acceptance =
            (routing.Verification |> List.map (fun o ->
                let statement = match o.Kind with Artifacts.VerifyIndependently what -> what | _ -> "Independent verification."
                { Label = "independent verification"; Expectation = statement; RequirementKeys = o.RequirementKeys }))
            @ (acceptanceValidators |> List.map (fun id ->
                { Label = "host validator " + id; Expectation = "The host runs this acceptance validator; the agent does not."; RequirementKeys = [] }))
            @ [ { Label = "boundary"; Expectation = "Only the permitted files change. Generated, project and dependency files stay untouched; anything else is refused."; RequirementKeys = [] }
                { Label = "build"; Expectation = "Every required interface member is implemented in a permitted file so the generated code compiles; there is no default implementation."; RequirementKeys = [] } ]
          AllowedOperations =
            [ "Write only these files, as flat file names in the outbox: " + String.concat ", " flat + "; plus dispositions.json."
              "`try` builds the candidate."
              "`submit` commits it (at most 3 submits)."
              "Nothing else: no other file, path, project, dependency or command." ] }

    // ------------------------------------------------------------- render

    let private str (value: string) = JsonValue.Create value :> JsonNode
    let private strs (values: string list) = JsonArray(values |> List.map str |> List.toArray) :> JsonNode
    let private obj (fields: (string * JsonNode) list) =
        let node = JsonObject()
        fields |> List.iter (fun (name, value) -> node[name] <- value)
        node
    let private arr (nodes: JsonNode list) = JsonArray(nodes |> List.toArray) :> JsonNode

    /// Short aliases R1..Rn for every cited requirement key, in legend order.
    let private aliases (handoff: ConstructionHandoff) =
        handoff.Requirements |> List.mapi (fun index r -> r.Key, sprintf "R%d" (index + 1))

    let private aliased (handoff: ConstructionHandoff) =
        let table = aliases handoff
        let map = Map.ofList table
        let alias (key: string) = map |> Map.tryFind key |> Option.defaultValue key
        // Longest keys first so a key that prefixes another never splits it.
        let text (value: string) = table |> List.sortByDescending (fun (k, _) -> k.Length) |> List.fold (fun (acc: string) (k, a) -> acc.Replace(k, a)) value
        alias, text

    let toJson (handoff: ConstructionHandoff) : JsonObject =
        let alias, text = aliased handoff
        let aliasesOf (keys: string list) = strs (keys |> List.map alias)
        obj [ "schemaVersion", str Schema
              "cohortId", str handoff.CohortId
              "requirements", arr (handoff.Requirements |> List.map (fun r ->
                  obj [ "alias", str (alias r.Key); "key", str r.Key; "excerpt", (match r.Excerpt with Some e -> str e | None -> null) ] :> JsonNode))
              "obligations", arr (handoff.Obligations |> List.map (fun o ->
                  obj [ "id", str o.Id; "kind", str o.Kind; "requirements", aliasesOf o.RequirementKeys; "nodeIds", strs o.NodeIds
                        "extensionSurface", strs o.ExtensionSurface; "question", str (text o.Statement) ] :> JsonNode))
              "dispositionRule", str DispositionRule
              "unresolvedQuestions", arr (handoff.UnresolvedQuestions |> List.map (fun q ->
                  (if q.FromObligation then obj [ "obligationId", str q.Subject ] else obj [ "subject", str q.Subject; "question", str (text q.Question) ]) :> JsonNode))
              "decisions", arr (handoff.Decisions |> List.map (fun d -> obj [ "nodeId", str d.NodeId; "summary", str d.Summary ] :> JsonNode))
              "extensionInterfaces", arr (handoff.Interfaces |> List.map (fun i ->
                  obj [ "interface", str i.Interface; "declaredIn", str i.DeclaredIn; "members", strs i.Members
                        "requiredMembers", strs i.Contracts; "implementIn", strs i.ImplementIn ] :> JsonNode))
              "acceptanceAdapters", arr (handoff.AcceptanceAdapters |> List.map (fun a ->
                  obj [ "acceptanceContract", str a.Contract; "path", str a.Path; "signatures", strs a.Signatures ] :> JsonNode))
              "generatedApi", arr (handoff.GeneratedApi |> List.map (fun g ->
                  obj [ "file", str g.File; "namespace", str g.Namespace; "declarations", strs g.Declarations ] :> JsonNode))
              "permittedFiles", arr (handoff.PermittedFiles |> List.map (fun f ->
                  obj [ "path", str f.Path; "kind", str f.Kind; "contracts", strs f.Contracts; "maxBytes", JsonValue.Create f.MaxBytes ] :> JsonNode))
              "allowedOperations", strs handoff.AllowedOperations
              "acceptance", arr (handoff.Acceptance |> List.map (fun a ->
                  obj [ "label", str a.Label; "expectation", str a.Expectation; "requirements", aliasesOf a.RequirementKeys ] :> JsonNode)) ]

    /// The human-readable twin. Same content, no additions.
    let toMarkdown (handoff: ConstructionHandoff) : string =
        let alias, text = aliased handoff
        let out = StringBuilder()
        let line (value: string) = out.Append(value).Append('\n') |> ignore
        let refs (keys: string list) = if keys.IsEmpty then "" else " [" + String.concat ", " (keys |> List.map alias) + "]"
        let questionNumber =
            handoff.UnresolvedQuestions |> List.mapi (fun index q -> q.Subject, index + 1) |> Map.ofList
        line (sprintf "# Construction handoff: %s" handoff.CohortId)
        line ""
        line "## Requirements"
        for r in handoff.Requirements do
            line ""
            line (sprintf "**%s** `%s`" (alias r.Key) r.Key)
            line ""
            match r.Excerpt with
            | Some excerpt ->
                line "```text"
                line (excerpt.TrimEnd('\n'))
                line "```"
            | None -> line "_Not available in the admitted source; see unresolved questions._"
        line ""
        line "## Obligations"
        line ""
        if handoff.Obligations.IsEmpty then line "None."
        for o in handoff.Obligations do
            let surface = if o.ExtensionSurface.IsEmpty then "" else " Surface: " + String.concat ", " (o.ExtensionSurface |> List.map (sprintf "`%s`")) + "."
            match questionNumber |> Map.tryFind o.Id with
            | Some n -> line (sprintf "- **%s** (%s)%s: unresolved question %d; escalate.%s" o.Id o.Kind (refs o.RequirementKeys) n surface)
            | None -> line (sprintf "- **%s** (%s)%s: %s%s" o.Id o.Kind (refs o.RequirementKeys) (text o.Statement) surface)
        line ""
        line "## Unresolved questions"
        line ""
        if handoff.UnresolvedQuestions.IsEmpty then line "None."
        handoff.UnresolvedQuestions |> List.iteri (fun index q -> line (sprintf "%d. **%s**: %s" (index + 1) (alias q.Subject) (text q.Question)))
        line ""
        line DispositionRule
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
            line (sprintf "Required members: %s." (String.concat ", " i.Contracts))
        if not handoff.AcceptanceAdapters.IsEmpty then
            line ""
            line "## Acceptance adapter to implement"
            for a in handoff.AcceptanceAdapters do
                line ""
                line (sprintf "### `%s` (replace the stub in `%s`; keep the names and signatures)" a.Contract a.Path)
                line ""
                line "```fsharp"
                for signature in a.Signatures do line signature
                line "```"
        line ""
        line "## Generated API (signatures only)"
        for g in handoff.GeneratedApi do
            line ""
            line (sprintf "### `%s` (namespace `%s`)" g.File g.Namespace)
            line ""
            line "```fsharp"
            line (String.concat "\n\n" g.Declarations)
            line "```"
        line ""
        line "## Permitted files"
        line ""
        for f in handoff.PermittedFiles do
            let what = if f.Kind = "acceptanceAdapter" then "acceptance adapter " else ""
            line (sprintf "- `%s` (at most %d bytes): %s%s" f.Path f.MaxBytes what (String.concat ", " f.Contracts))
        line ""
        line "## Allowed operations"
        line ""
        for operation in handoff.AllowedOperations do line ("- " + operation)
        line ""
        line "## Acceptance expectations"
        line ""
        for a in handoff.Acceptance do line (sprintf "- **%s**%s: %s" a.Label (refs a.RequirementKeys) a.Expectation)
        out.ToString()
