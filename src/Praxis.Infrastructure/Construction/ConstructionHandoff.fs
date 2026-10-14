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

/// One ECIR node of the cohort's required scope, as the blueprint states it.
type HandoffNode = { Id: string; Kind: string; RequirementKeys: string list; DependsOn: string list; Justification: string option }

/// One approved decision the agent works under. `Source` is
/// `constructionRecord` (operator-approved decision record), `ecirDecision`
/// (a decision node of the approved blueprint) or `decisionDocument` (an
/// accepted decision document the cohort cites as a requirement).
type HandoffDecision = { Id: string; Source: string; Summary: string; RequirementKeys: string list }

/// One generated interface the agent implements: only the members its
/// contracts name, as declared in the generated source.
type HandoffInterface =
    { Interface: string; DeclaredIn: string; Members: string list; Contracts: string list; ObligationIds: string list; ImplementIn: string list }

type HandoffPermittedFile = { Path: string; Kind: string; Contracts: string list; MaxBytes: int }

/// Public declarations of one source file: type definitions in full and
/// module function signatures without bodies.
type HandoffGeneratedApi = { File: string; Namespace: string; Declarations: string list }

/// A frozen acceptance (oracle adapter) contract the agent fills: the public
/// signatures of the existing stub at the admitted source commit, and the
/// public types of the contract files the stub opens.
type HandoffAcceptanceAdapter = { Contract: string; Path: string; Signatures: string list; ContractTypes: HandoffGeneratedApi list }

/// One default-plan obligation as the agent receives it: everything needed to
/// act on it, by reference into the shared requirement and decision lists.
/// Exactly one of `Question` (escalate) and `RequiredBehavior` (implement) is set.
type HandoffWorkItem =
    { Id: string
      Kind: string
      RequirementKeys: string list
      DecisionIds: string list
      Question: string option
      RequiredBehavior: string option
      Interfaces: string list
      Paths: string list
      Dispositions: string list
      Verification: string list }

/// `praxis.construction-handoff/3` (CONSTRUCT-010): the compact typed brief a
/// construction agent needs after mechanical generation. One record per
/// obligation; every approved decision and every excerpt the cohort's
/// required scope cites, each stated once. Deliberately no vocabulary manual,
/// digests, serialization instructions or full source.
type ConstructionHandoff =
    { CohortId: string
      WorkItems: HandoffWorkItem list
      Requirements: HandoffRequirement list
      Decisions: HandoffDecision list
      Interfaces: HandoffInterface list
      AcceptanceAdapters: HandoffAcceptanceAdapter list
      GeneratedApi: HandoffGeneratedApi list
      PermittedFiles: HandoffPermittedFile list
      Operations: string list }

[<RequireQualifiedAccess>]
module ConstructionHandoff =
    [<Literal>]
    let Schema = "praxis.construction-handoff/3"

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

    /// The decision-document id a requirement key names, when the key cites a
    /// document under a `decisions/` directory: `.../decisions/DF-X--slug.md#L1-L9` is `DF-X`.
    let decisionDocumentId (key: string) =
        let path = key.Split('#').[0]
        let parts = path.Split('/')
        if parts.Length < 2 || parts.[parts.Length - 2] <> "decisions" || not (path.EndsWith ".md") then None
        else
            let stem = parts.[parts.Length - 1] |> fun name -> name.Substring(0, name.Length - 3)
            match stem.IndexOf "--" with
            | -1 -> Some stem
            | at -> Some(stem.Substring(0, at))

    /// A short title for an excerpt: its first heading, else its first line.
    let private title (excerpt: string option) =
        let lines = excerpt |> Option.map (fun e -> e.Replace("\r\n", "\n").Split('\n') |> Array.toList) |> Option.defaultValue []
        let nonBlank = lines |> List.map _.Trim() |> List.filter (String.IsNullOrWhiteSpace >> not)
        match nonBlank |> List.tryFind (fun l -> l.StartsWith "#"), nonBlank |> List.tryHead with
        | Some heading, _ -> heading.TrimStart('#').Trim()
        | None, Some first when first.Length > 160 -> first.Substring(0, 157) + "..."
        | None, Some first -> first
        | None, None -> "Accepted decision (text not available in the admitted source)."

    /// Every approved decision, once: all operator-approved construction
    /// records, the blueprint's decision nodes no record restates, and every
    /// accepted decision document a cited requirement names.
    let decisions (decisionRecords: string) (nodes: HandoffNode list) (requirements: HandoffRequirement list) : HandoffDecision list =
        let records =
            try
                items (JsonNode.Parse decisionRecords) "records"
                |> List.choose (fun record ->
                    match text record "nodeId", (match record with :? JsonObject as o -> o["content"] | _ -> null) with
                    | Some nodeId, content when not (isNull content) ->
                        Some { Id = nodeId; Source = "constructionRecord"; Summary = summarize content; RequirementKeys = citedKeys content |> List.distinct }
                    | _ -> None)
            with _ -> []
        let recorded = records |> List.map _.Id |> Set.ofList
        let decisionNodes =
            nodes |> List.filter (fun n -> n.Kind = "decision" && not (recorded.Contains n.Id))
            |> List.map (fun n -> { Id = n.Id; Source = "ecirDecision"; Summary = defaultArg n.Justification "Approved decision."; RequirementKeys = n.RequirementKeys })
        let documents =
            requirements
            |> List.choose (fun r -> decisionDocumentId r.Key |> Option.map (fun id -> id, r))
            |> List.groupBy fst
            |> List.map (fun (id, cited) ->
                { Id = id; Source = "decisionDocument"; Summary = title (cited |> List.tryPick (fun (_, r) -> r.Excerpt)); RequirementKeys = cited |> List.map (fun (_, r) -> r.Key) })
        records @ decisionNodes @ documents

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

    /// Public types of the contract files an acceptance stub opens (or shares
    /// a namespace with). `sources` is (path, content) of candidate files.
    let contractTypes (stubPath: string) (stub: string) (sources: (string * string) list) : HandoffGeneratedApi list =
        let lines (content: string) = content.Replace("\r\n", "\n").Split('\n') |> Array.toList
        let scopeOf (content: string) =
            lines content |> List.tryPick (fun l ->
                let trimmed = l.Trim()
                if trimmed.StartsWith "namespace " then Some(trimmed.Substring(10).Trim())
                elif trimmed.StartsWith "module " && not (trimmed.Contains "=") then Some(trimmed.Substring(7).Trim())
                else None)
        let opened =
            (lines stub |> List.choose (fun l -> let t = l.Trim() in if t.StartsWith "open " then Some(t.Substring(5).Trim()) else None))
            @ (scopeOf stub |> Option.toList)
            |> Set.ofList
        sources
        |> List.filter (fun (path, content) -> path <> stubPath && path.EndsWith ".fs" && not (content.StartsWith Artifacts.GeneratedMarker))
        |> List.choose (fun (path, content) ->
            match scopeOf content with
            | Some scope when opened.Contains scope ->
                let ns, blocks = declarations content
                let kept = blocks |> List.map snd
                if kept.IsEmpty then None else Some { File = path; Namespace = defaultArg ns scope; Declarations = kept }
            | _ -> None)
        |> List.sortBy _.File

    // -------------------------------------------------------------- build

    /// Obligation kinds that ask a question an agent may not settle in code.
    let QuestionKinds = set [ "undecidedEffectFailure"; "conflict"; "uncoveredRequirement"; "unsupportedVocabulary" ]

    /// Every requirement key the handoff cites: obligations, verification,
    /// the cohort's required nodes and the approved decision records.
    let handoffKeys (obligations: DefaultPlanObligation list) (routing: ConstructionRouting) (nodes: HandoffNode list) (decisionRecords: string) =
        let recordKeys = try items (JsonNode.Parse decisionRecords) "records" |> List.collect citedKeys with _ -> []
        (obligations |> List.collect _.RequirementKeys)
        @ (routing.Verification |> List.collect _.RequirementKeys)
        @ (nodes |> List.collect _.RequirementKeys)
        @ recordKeys
        |> List.distinct

    let DispositionRule =
        "Disposition every obligation in dispositions.json. A conflict, undecidedEffectFailure, uncoveredRequirement or "
        + "unsupportedVocabulary obligation needs an approved decision the agent cannot make: escalate it, and state any provisional "
        + "code behavior in its note (each `Escalate:` item below). An extensionBehavior obligation is implemented, or escalated when its text leaves a choice open."

    /// The behavior text of an uncovered requirement Ordo delegates.
    let DelegatedBehavior =
        "Ordo delegates this requirement: implement it in the permitted files under the approved decisions and contracts, and escalate any question it leaves open."

    let DispositionFormat =
        """dispositions.json: {"dispositions":[{"obligationId":"OBL-...","disposition":"implemented|escalated","note":"...","files":["<flat file name>"]}]}, one entry per obligation."""

    /// The nodes one hop from `ids` in either direction, plus `ids` themselves.
    let private neighborhood (nodes: HandoffNode list) (ids: string list) =
        let seeds = Set.ofList ids
        let outward = nodes |> List.filter (fun n -> seeds.Contains n.Id) |> List.collect _.DependsOn
        let inward = nodes |> List.filter (fun n -> n.DependsOn |> List.exists seeds.Contains) |> List.map _.Id
        Set.unionMany [ seeds; Set.ofList outward; Set.ofList inward ]

    let private workItem (nodes: HandoffNode list) (decided: HandoffDecision list) (routing: ConstructionRouting) (o: DefaultPlanObligation) =
        let near = neighborhood nodes o.NodeIds
        let keys = o.RequirementKeys @ (nodes |> List.filter (fun n -> near.Contains n.Id) |> List.collect _.RequirementKeys) |> List.distinct
        let keySet = Set.ofList keys
        let routed = routing.Custom |> List.filter (fun r -> o.ExtensionSurface |> List.contains r.Contract)
        let asks = QuestionKinds.Contains o.Kind
        { Id = o.Id
          Kind = o.Kind
          RequirementKeys = keys
          DecisionIds =
            decided
            |> List.filter (fun d -> near.Contains d.Id || d.RequirementKeys |> List.exists keySet.Contains)
            |> List.map _.Id
          Question = if asks then Some o.Statement else None
          RequiredBehavior = if asks then None else Some o.Statement
          Interfaces = o.ExtensionSurface
          Paths = routed |> List.map _.SlotPath |> List.distinct
          Dispositions = if asks then [ "escalated" ] else [ "implemented"; "escalated" ]
          Verification =
            routing.Verification
            |> List.filter (fun v -> v.RequirementKeys |> List.exists keySet.Contains)
            |> List.choose (fun v -> match v.Kind with Artifacts.VerifyIndependently what -> Some what | _ -> None)
            |> List.distinct }

    let build (cohortId: string) (obligations: DefaultPlanObligation list) (requirements: HandoffRequirement list) (nodes: HandoffNode list)
              (decisionRecords: string) (generated: (string * string) list) (routing: ConstructionRouting)
              (adapters: (ConstructionAcceptanceSlot * string * HandoffGeneratedApi list) list) (acceptanceValidators: string list)
              (recognition: RecognitionSummary option) : ConstructionHandoff =
        // Ordo recognition adds the approved decisions its delegation packets
        // carry (keyed by those packets' requirements) when not already listed.
        let decided =
            let own = decisions decisionRecords nodes requirements
            let known = own |> List.map _.Id |> Set.ofList
            own
            @ (recognition |> Option.map _.Decisions |> Option.defaultValue []
               |> List.filter (fun d -> not (known.Contains d.NodeId))
               |> List.map (fun d -> { Id = d.NodeId; Source = "recognizedDecision"; Summary = defaultArg d.Justification "Approved decision."; RequirementKeys = d.RequirementKeys }))
        let mustEscalate = recognition |> Option.map _.EscalationRequired |> Option.defaultValue Set.empty
        let delegated = recognition |> Option.map _.Delegated |> Option.defaultValue Set.empty
        // Ordo states paths in the construction layout; the agent sees them placed.
        let forbidden = recognition |> Option.map _.ForbiddenPaths |> Option.defaultValue [] |> List.map (ConstructionTree.placed routing.Mount)
        let usable = ConstructionRouting.usableSlots routing
        let interfaces = interfaces generated routing.Custom
        let permitted =
            usable |> List.map (fun slot ->
                { Path = ConstructionTree.placed routing.Mount slot.File
                  Kind = (if ConstructionRouting.isAcceptance routing slot then "acceptanceAdapter" else "constructionContract")
                  Contracts = slot.Contracts; MaxBytes = slot.MaxBytes })
        let flat = permitted |> List.map (fun f -> f.Path.Substring(f.Path.LastIndexOf '/' + 1))
        let unavailable = requirements |> List.filter (fun r -> r.Excerpt.IsNone) |> List.map _.Key
        { CohortId = cohortId
          WorkItems =
            obligations |> List.map (fun o ->
                let item = workItem nodes decided routing o
                // An obligation on a requirement Ordo marks escalation-required
                // cannot be implemented here (its own keys, not supporting ones).
                if o.RequirementKeys |> List.exists mustEscalate.Contains then { item with Dispositions = [ "escalated" ] }
                // An uncovered requirement Ordo delegates (and needs no open
                // decision) is the agent's to implement under the approved decisions.
                elif o.Kind = "uncoveredRequirement" && not o.RequirementKeys.IsEmpty && o.RequirementKeys |> List.forall delegated.Contains then
                    { item with
                        Question = None
                        RequiredBehavior = Some(DelegatedBehavior + " " + o.Statement)
                        Dispositions = [ "implemented"; "escalated" ] }
                else item)
          Requirements = requirements
          Decisions = decided
          Interfaces = interfaces
          AcceptanceAdapters =
            adapters |> List.map (fun (slot, content, types) ->
                { Contract = slot.AcceptanceContract; Path = slot.AcceptancePath; Signatures = adapterSignatures content; ContractTypes = types })
          GeneratedApi = generatedApi generated interfaces
          PermittedFiles = permitted
          Operations =
            [ yield "Write only these files, by flat file name, into the outbox: " + String.concat ", " flat + "; plus dispositions.json."
              yield DispositionFormat
              yield DispositionRule
              yield "`try` builds the candidate; `submit` commits it (at most 3 submits). Nothing else: no other file, path, project, dependency or command."
              yield "Generated, project and dependency files stay untouched. Every required interface member is implemented in a permitted file; there is no default implementation."
              if not acceptanceValidators.IsEmpty then
                  yield "The host runs acceptance validators " + String.concat ", " acceptanceValidators + "; the agent does not."
              if not forbidden.IsEmpty then
                  yield "Never write: " + String.concat ", " forbidden + "."
              if not unavailable.IsEmpty then
                  yield "Requirements without text in the admitted source (" + String.concat ", " unavailable + "): escalate rather than guess." ] }

    // ------------------------------------------------------------- render

    let private str (value: string) = JsonValue.Create value :> JsonNode
    let private strs (values: string list) = JsonArray(values |> List.map str |> List.toArray) :> JsonNode
    let private opt (value: string option) = match value with Some v -> str v | None -> null
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

    let private apiNode (g: HandoffGeneratedApi) =
        obj [ "file", str g.File; "namespace", str g.Namespace; "declarations", strs g.Declarations ] :> JsonNode

    let toJson (handoff: ConstructionHandoff) : JsonObject =
        let alias, text = aliased handoff
        let aliasesOf (keys: string list) = strs (keys |> List.map alias)
        obj [ "schemaVersion", str Schema
              "cohortId", str handoff.CohortId
              "obligations", arr (handoff.WorkItems |> List.map (fun w ->
                  obj [ "id", str w.Id; "kind", str w.Kind; "requirements", aliasesOf w.RequirementKeys; "decisions", strs w.DecisionIds
                        "question", opt (w.Question |> Option.map text); "requiredBehavior", opt (w.RequiredBehavior |> Option.map text)
                        "interfaces", strs w.Interfaces; "paths", strs w.Paths; "dispositions", strs w.Dispositions
                        "verification", strs (w.Verification |> List.map text) ] :> JsonNode))
              "requirements", arr (handoff.Requirements |> List.map (fun r ->
                  obj [ "alias", str (alias r.Key); "key", str r.Key; "excerpt", opt r.Excerpt ] :> JsonNode))
              "decisions", arr (handoff.Decisions |> List.map (fun d ->
                  obj [ "id", str d.Id; "source", str d.Source; "summary", str (text d.Summary); "requirements", aliasesOf d.RequirementKeys ] :> JsonNode))
              "extensionInterfaces", arr (handoff.Interfaces |> List.map (fun i ->
                  obj [ "interface", str i.Interface; "declaredIn", str i.DeclaredIn; "members", strs i.Members
                        "requiredMembers", strs i.Contracts; "implementIn", strs i.ImplementIn ] :> JsonNode))
              "acceptanceAdapters", arr (handoff.AcceptanceAdapters |> List.map (fun a ->
                  obj [ "acceptanceContract", str a.Contract; "path", str a.Path; "signatures", strs a.Signatures
                        "contractTypes", arr (a.ContractTypes |> List.map apiNode) ] :> JsonNode))
              "generatedApi", arr (handoff.GeneratedApi |> List.map apiNode)
              "permittedFiles", arr (handoff.PermittedFiles |> List.map (fun f ->
                  obj [ "path", str f.Path; "kind", str f.Kind; "contracts", strs f.Contracts; "maxBytes", JsonValue.Create f.MaxBytes ] :> JsonNode))
              "operations", strs handoff.Operations ]

    /// Ordo's question boilerplate, shortened only when its exact known form
    /// matches; any other text is kept verbatim. The JSON twin keeps it whole.
    let private conflictForm = RegularExpressions.Regex("^Conflict (\\S+) is inside cohort \\S+ and no approved decision resolves it\\. The blueprint records: (.*)$", RegularExpressions.RegexOptions.Singleline)
    let private uncoveredForm = RegularExpressions.Regex("^Requirement (\\S+) is assigned to cohort \\S+, but no approved decision record constructs it\\. Which approved decision or extension implements it\\?(.*)$", RegularExpressions.RegexOptions.Singleline)

    let shortQuestion (question: string) =
        let conflict = conflictForm.Match question
        let uncovered = uncoveredForm.Match question
        if conflict.Success then conflict.Groups[1].Value + ": " + conflict.Groups[2].Value
        elif uncovered.Success then "No approved decision constructs " + uncovered.Groups[1].Value + "; which decision or extension implements it?" + uncovered.Groups[2].Value
        else question

    /// The worker-facing rendering: the same content as `toJson`, compact.
    /// Verification statements are listed once and referenced by number; an
    /// `Escalate:` item's disposition is always `escalated`.
    let toMarkdown (handoff: ConstructionHandoff) : string =
        let alias, text = aliased handoff
        let refs (keys: string list) = String.concat ", " (keys |> List.map alias)
        let code (values: string list) = String.concat ", " (values |> List.map (sprintf "`%s`"))
        let fenced (body: string) = [ "```fsharp"; body; "```" ]
        let verification = handoff.WorkItems |> List.collect _.Verification |> List.distinct
        let verificationNumber = verification |> List.mapi (fun index v -> v, sprintf "V%d" (index + 1)) |> Map.ofList
        let workItem (w: HandoffWorkItem) =
            [ yield "### " + w.Id
              match w.Question, w.RequiredBehavior with
              | Some q, _ -> yield "Escalate: " + text (shortQuestion q)
              | None, Some b -> yield "Implement: " + text b
              | None, None -> ()
              if not w.RequirementKeys.IsEmpty then yield "Requirements: " + refs w.RequirementKeys
              if not w.DecisionIds.IsEmpty then yield "Decisions: " + String.concat ", " w.DecisionIds
              if not w.Interfaces.IsEmpty then yield "Interfaces: " + code w.Interfaces + (if w.Paths.IsEmpty then "" else " in " + code w.Paths)
              if w.Question.IsNone then yield "Disposition: " + String.concat " or " w.Dispositions
              if not w.Verification.IsEmpty then yield "Verified by: " + String.concat ", " (w.Verification |> List.map (fun v -> verificationNumber[v]))
              yield "" ]
        let api (g: HandoffGeneratedApi) = [ sprintf "#### `%s` (`%s`)" g.File g.Namespace ] @ fenced (String.concat "\n\n" g.Declarations) @ [ "" ]
        [ yield sprintf "# Construction handoff: %s" handoff.CohortId
          yield ""
          for operation in handoff.Operations do yield "- " + operation
          yield ""
          yield "## Obligations"
          yield ""
          if handoff.WorkItems.IsEmpty then yield "None." ; yield ""
          for w in handoff.WorkItems do yield! workItem w
          if not verification.IsEmpty then
              yield "## Independent verification"
              yield ""
              for v in verification do yield sprintf "- **%s**: %s" verificationNumber[v] (text v)
              yield ""
          yield "## Approved decisions"
          yield ""
          if handoff.Decisions.IsEmpty then yield "None."
          for d in handoff.Decisions do
              yield sprintf "- **%s** (%s)%s: %s" d.Id d.Source (if d.RequirementKeys.IsEmpty then "" else " [" + refs d.RequirementKeys + "]") (text d.Summary)
          yield ""
          yield "## Requirements"
          for r in handoff.Requirements do
              yield ""
              yield sprintf "**%s** `%s`" (alias r.Key) r.Key
              match r.Excerpt with
              | Some excerpt -> yield! [ "```text"; excerpt.TrimEnd('\n'); "```" ]
              | None -> yield "_Not available in the admitted source._"
          yield ""
          yield "## Extension interfaces"
          for i in handoff.Interfaces do
              yield ""
              yield sprintf "### `%s` (declared in `%s`; implement in %s; required: %s)" i.Interface i.DeclaredIn (code i.ImplementIn) (String.concat ", " i.Contracts)
              yield! fenced (String.concat "\n" i.Members)
          for a in handoff.AcceptanceAdapters do
              yield ""
              yield sprintf "## Acceptance adapter `%s` (replace the stub in `%s`; keep names and signatures)" a.Contract a.Path
              yield ""
              yield! fenced (String.concat "\n" a.Signatures)
              if not a.ContractTypes.IsEmpty then
                  yield ""
                  yield "### Contract types"
                  yield ""
                  for g in a.ContractTypes do yield! api g
          yield ""
          yield "## Generated API (signatures only)"
          yield ""
          for g in handoff.GeneratedApi do yield! api g
          yield "## Permitted files"
          yield ""
          for f in handoff.PermittedFiles do
              yield sprintf "- `%s` (at most %d bytes)%s: %s" f.Path f.MaxBytes (if f.Kind = "acceptanceAdapter" then " acceptance adapter" else "") (String.concat ", " f.Contracts) ]
        |> String.concat "\n"
        |> fun body -> body + "\n"
