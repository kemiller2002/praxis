namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Construction

/// The result of `construct prepare` (CONSTRUCT-008).
type PreparedReport =
    { /// The exact `praxis.construction-prepared/1` bytes.
      Text: string
      CacheKey: string
      /// True when a validated artifact with this cache key was reused.
      Reused: bool
      HandoffJson: JsonObject
      HandoffMarkdown: string
      Obligations: int
      PermittedFiles: string list }

/// What `construct apply --prepared` did.
type PreparedApplication =
    { CacheKey: string
      Construction: ConstructionReport
      Extension: ExtensionReport option }

/// A refusal `construct apply --prepared` recorded before any effect.
/// `Denied` lists the refused handwritten operations as (repository path, reason).
type PreparedRefusal = { Reasons: string list; RecordPath: string option; Denied: (string * string) list }

/// Mechanical-first construction (CONSTRUCT-008): `prepare` admits the cohort
/// under the signed grant, requires the grant-bound decision records, calls
/// the single default-plan seam, validates its plan through the ordinary
/// admission and records a reusable prepared artifact; `applyPrepared`
/// re-derives the host binding, refuses any drift, applies generated files
/// through the existing apply path and then only host-permitted handwritten
/// files through the existing extension path.
[<RequireQualifiedAccess>]
module ConstructionPrepared =
    [<Literal>]
    let Schema = "praxis.construction-prepared/1"

    [<Literal>]
    let RefusalSchema = "praxis.construction-refusal/1"

    let private maxPreparedBytes = 4194304

    let private str (value: string) = JsonValue.Create value :> JsonNode
    let private strs (values: string list) = JsonArray(values |> List.map str |> List.toArray) :> JsonNode
    let private arr (nodes: JsonNode list) = JsonArray(nodes |> List.toArray) :> JsonNode
    let private obj (fields: (string * JsonNode) list) =
        let node = JsonObject()
        fields |> List.iter (fun (name, value) -> node[name] <- value)
        node

    let private options = JsonSerializerOptions(WriteIndented = true)

    /// The Praxis build that renders the artifact and handoff.
    let praxisVersion =
        match typeof<PreparedReport>.Assembly.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false) with
        | [| :? System.Reflection.AssemblyInformationalVersionAttribute as attribute |] -> attribute.InformationalVersion
        | _ -> string (typeof<PreparedReport>.Assembly.GetName().Version)

    let private acceptanceNodes (configuration: LocalHostConfiguration) =
        configuration.AcceptanceSlots |> List.map (fun slot ->
            obj [ "path", str slot.AcceptancePath; "acceptanceContract", str slot.AcceptanceContract; "maxBytes", JsonValue.Create slot.AcceptanceMaxBytes ] :> JsonNode)

    /// Digest of the host's construction placement policy: the mount and every
    /// declared slot. A slot change is a policy change and invalidates reuse.
    let hostConstructionPolicyDigest (configuration: LocalHostConfiguration) =
        let slots =
            configuration.ExtensionSlots |> List.map (fun slot ->
                obj [ "file", str slot.File; "contracts", strs slot.Contracts; "maxBytes", JsonValue.Create slot.MaxBytes ] :> JsonNode)
        let acceptance = acceptanceNodes configuration
        let fields = [ "mount", str configuration.ConstructionMount; "slots", arr slots ] @ (if acceptance.IsEmpty then [] else [ "acceptanceSlots", arr acceptance ])
        ConstructionTree.digest ((obj fields).ToJsonString())

    /// Every host-derived input the artifact depends on, in a fixed order.
    /// Nothing here comes from an agent; all of it is known before generation.
    let hostBinding (generator: DefaultPlanGenerator) (vocabulary: string) (configuration: LocalHostConfiguration) (groupId: string) (cohort: ConstructionAdmission.AdmittedCohort) (decisionsDigest: string) : (string * JsonNode) list =
        let grant = ConstructionAdmission.cohortGrant cohort
        let approval = ConstructionAdmission.cohortApproval cohort
        [ "policyRevision", str (ConstructionAdmission.cohortPolicyRevision cohort)
          "groupId", str groupId
          "cohortId", str grant.CohortId
          "sourceCommit", str grant.Source.Commit
          "manifestPath", str grant.Source.ManifestPath
          "manifestDigest", str (ConstructionAdmission.cohortManifestDigest cohort)
          "blueprintPath", str grant.Source.BlueprintPath
          "blueprintDigest", str (ConstructionAdmission.cohortBlueprintDigest cohort)
          "decisionsDigest", str decisionsDigest
          "requirementKeys", strs approval.RequirementKeys
          "decisionIds", strs approval.DecisionIds
          "approver", str approval.Approver
          "approvalExpiresAt", str approval.ExpiresAt
          "vocabularyVersion", str vocabulary
          "generatorId", str generator.Id
          "generatorVersion", str generator.Version
          "ordoCoreVersion", str (ConstructionAdmission.ordoCoreVersion ())
          "ordoConstructionVersion", str (ConstructionAdmission.ordoConstructionVersion ())
          "constructionMount", str configuration.ConstructionMount
          "hostConstructionPolicyDigest", str (hostConstructionPolicyDigest configuration) ]
        // Acceptance adapter slots (CONSTRUCT-008) are bound explicitly when declared.
        @ (if configuration.AcceptanceSlots.IsEmpty then [] else [ "acceptanceSlots", arr (acceptanceNodes configuration) ])
        @ [ "acceptanceValidators", strs (configuration.AcceptanceValidators |> Map.keys |> Seq.sort |> Seq.toList)
            "validatorPolicyRevision", str configuration.ValidatorPolicyRevision
            "praxisVersion", str praxisVersion
            "handoffSchema", str ConstructionHandoff.Schema ]

    /// Every acceptance slot must name an existing regular handwritten file of
    /// the admitted source commit; its content is the frozen adapter stub.
    let acceptanceAdapters (configuration: LocalHostConfiguration) (cohort: ConstructionAdmission.AdmittedCohort) : Result<(ConstructionAcceptanceSlot * string) list, string> =
        let source = (ConstructionAdmission.cohortGrant cohort).Source.Commit
        configuration.AcceptanceSlots |> List.fold (fun acc slot ->
            acc |> Result.bind (fun found ->
                let refuse reason = Error("acceptance slot " + slot.AcceptancePath + " refused: " + reason)
                match ConstructionTree.entryAt configuration.Git configuration.RepositoryRoot source slot.AcceptancePath with
                | Error reason -> refuse reason
                | Ok None -> refuse "absent from the admitted source commit"
                | Ok(Some("100644", Some content)) when content.StartsWith Artifacts.GeneratedMarker -> refuse "the admitted source holds a generated file there"
                | Ok(Some("100644", Some content)) -> Ok(found @ [ slot, content ])
                | Ok(Some(mode, _)) -> refuse (sprintf "not a regular file in the admitted source (mode %s)" mode))) (Ok [])

    /// Reuse key: digest over the artifact schema and every binding input
    /// (blueprint, manifest, cohort, decision records, vocabulary, generator,
    /// host policy revision and placement policy, source, approval).
    let cacheKey (binding: (string * JsonNode) list) =
        let lines = binding |> List.map (fun (name, value) -> name + "=" + value.ToJsonString())
        ConstructionTree.digest (String.concat "\n" (Schema :: lines))

    // ------------------------------------------------------------- inputs

    /// The grant-bound decision records: the presented file when given (its
    /// digest must equal the signed one), else the host's stored copy.
    let private decisionRecords (configuration: LocalHostConfiguration) (cohort: ConstructionAdmission.AdmittedCohort) (presented: string option) =
        match (ConstructionAdmission.cohortApproval cohort).ConstructionDecisionsDigest with
        | None ->
            Error "the signed grant binds no construction decisions; mechanical-first construction needs an operator-approved ordo.construction-decisions/1 file bound with `local operator grant-draft --decisions FILE`"
        | Some signed ->
            let text =
                match presented with
                | Some path ->
                    ConstructionDecisions.readFile path |> Result.bind (fun (text, digest) ->
                        if digest <> signed then Error "construction decisions differ from the digest bound by the signed grant" else Ok text)
                | None -> ConstructionDecisions.load configuration signed
            text |> Result.bind (fun text ->
                ConstructionDecisions.envelope text |> Result.bind (fun envelope ->
                    let grant = ConstructionAdmission.cohortGrant cohort
                    if envelope.CohortId <> grant.CohortId then Error("construction decisions are for cohort " + envelope.CohortId + ", not the granted " + grant.CohortId)
                    elif envelope.BlueprintDigest <> ConstructionAdmission.cohortCanonicalBlueprintDigest cohort then
                        Error "construction decisions were approved against a different blueprint"
                    else Ok(text, signed)))

    let private admitted (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) now groupId (generator: DefaultPlanGenerator) (vocabulary: string) (presented: string option) =
        ConstructionAdmission.admitCohort configuration policy now groupId |> Result.bind (fun cohort ->
            decisionRecords configuration cohort presented |> Result.map (fun (text, digest) ->
                let binding = hostBinding generator vocabulary configuration groupId cohort digest
                cohort, text, binding, cacheKey binding))

    // ----------------------------------------------------------- generate

    let private nodeKind (kind: Ordo.Core.ConstructionIr.NodeKind) =
        match kind with
        | Ordo.Core.ConstructionIr.Decision -> "decision"
        | Ordo.Core.ConstructionIr.Contract -> "contract"
        | Ordo.Core.ConstructionIr.Invariant -> "invariant"
        | Ordo.Core.ConstructionIr.Interface -> "interface"
        | Ordo.Core.ConstructionIr.Cohort -> "cohort"
        | Ordo.Core.ConstructionIr.VerificationObligation -> "verificationObligation"
        | Ordo.Core.ConstructionIr.Conflict -> "conflict"
        | Ordo.Core.ConstructionIr.Deferral -> "deferral"
        | Ordo.Core.ConstructionIr.EngineeringRationale -> "engineeringRationale"

    /// The cohort's required-scope ECIR nodes, as the handoff states them.
    let handoffNodes (cohort: ConstructionAdmission.AdmittedCohort) : HandoffNode list =
        let view = Ordo.Core.ConstructionIr.preparedCohortView (ConstructionAdmission.cohortPrepared cohort)
        view.Blueprint.Nodes
        |> List.filter (fun n -> view.RequiredNodeIds.Contains n.Id)
        |> List.map (fun n -> { Id = n.Id; Kind = nodeKind n.Kind; RequirementKeys = n.RequirementKeys; DependsOn = n.DependsOn; Justification = n.Justification })

    let private maxContractSourceBytes = 65536L
    let private maxContractSources = 256

    /// Candidate contract sources for an acceptance stub: committed F# files,
    /// the stub's own directory first (a contract project the stub references
    /// may live elsewhere), bounded in size and number.
    let private contractSources (read: string -> Result<string, string>) (files: (string * int64) list) (stubPath: string) =
        let directory = match stubPath.LastIndexOf '/' with -1 -> "" | at -> stubPath.Substring(0, at + 1)
        files
        |> List.filter (fun (path, size) -> path.EndsWith ".fs" && path <> stubPath && size > 0L && size <= maxContractSourceBytes && not (path.StartsWith "."))
        |> List.sortBy (fun (path, _) -> (if path.StartsWith directory then 0 else 1), path)
        |> List.truncate maxContractSources
        |> List.choose (fun (path, _) -> read path |> Result.toOption |> Option.map (fun content -> path, content))


    let private obligationNode (o: DefaultPlanObligation) =
        obj [ "id", str o.Id; "kind", str o.Kind; "requirementKeys", strs o.RequirementKeys; "nodeIds", strs o.NodeIds
              "question", str o.Statement; "extensionSurface", strs o.ExtensionSurface ] :> JsonNode

    let private sameSet (left: string list) (right: string list) = Set.ofList left = Set.ofList right && left.Length = right.Length

    /// Run the seam, then hold its output to the ordinary rules: the plan must
    /// pass admission (and so `ConstructionPlan.validate`), and digest, layout
    /// and generator identity must agree with what Praxis observes.
    let private generate (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) now groupId (generator: DefaultPlanGenerator)
                         (vocabulary: string) (cohort: ConstructionAdmission.AdmittedCohort) (adapters: (ConstructionAcceptanceSlot * string) list)
                         (decisions: string) (binding: (string * JsonNode) list) (key: string) =
        generator.Generate (ConstructionAdmission.cohortPrepared cohort) decisions vocabulary
        |> Result.mapError (fun reasons -> "default plan refused: " + String.concat "; " reasons)
        |> Result.bind (fun output ->
            if output.Provenance.GeneratorId <> generator.Id || output.Provenance.GeneratorVersion <> generator.Version then
                Error "default plan provenance names a different generator than the host pinned"
            elif output.Provenance.VocabularyVersion <> vocabulary then Error "default plan uses a vocabulary the host did not request"
            else
                ConstructionAdmission.admit configuration policy now groupId output.PlanText |> Result.bind (fun admittedPlan ->
                    let b = ConstructionAdmission.binding admittedPlan
                    let built = ConstructionAdmission.construction admittedPlan
                    let routing = ConstructionRouting.classify configuration admittedPlan
                    let layout = built.Artifacts |> List.map _.Path
                    if b.PlanDigest <> output.PlanDigest then Error "default plan digest differs from the admitted plan's digest"
                    elif not (sameSet output.GeneratedLayout layout) then Error "default plan layout differs from the files the constructor emits"
                    elif not routing.Exceptions.IsEmpty then
                        Error("extension contracts have no host-permitted file: " + String.concat ", " (routing.Exceptions |> List.map _.Contract)
                              + "; an operator must declare a slot in the host configuration")
                    else
                        let git, root = configuration.Git, configuration.RepositoryRoot
                        let source = b.SourceCommit
                        ConstructionTree.regularFiles git root source |> Result.map (fun files ->
                            let read path = ConstructionTree.readCommitted git root source path |> Result.map fst
                            let nodes = handoffNodes cohort
                            let cited = ConstructionHandoff.handoffKeys output.Obligations routing nodes decisions
                            let requirements =
                                ConstructionHandoff.excerpts read files (ConstructionHandoff.manifestRequirements (ConstructionAdmission.cohortManifest cohort)) cited
                            let generated = built.Artifacts |> List.map (fun a -> ConstructionTree.placed configuration.ConstructionMount a.Path, a.Content)
                            let typedAdapters =
                                adapters |> List.map (fun (slot, content) ->
                                    slot, content, ConstructionHandoff.contractTypes slot.AcceptancePath content (contractSources read files slot.AcceptancePath))
                            let handoff =
                                ConstructionHandoff.build b.CohortId output.Obligations requirements nodes decisions generated routing typedAdapters
                                    (configuration.AcceptanceValidators |> Map.keys |> Seq.toList)
                            let permitted = ConstructionRouting.usableSlots routing
                            // Dokimos requires blueprint, sourceManifest and decisionRecords;
                            // Ordo's own digests win, host-observed ones fill any gap.
                            let inputDigests (reported: (string * string) list) =
                                let view = Ordo.Core.ConstructionIr.preparedCohortView (ConstructionAdmission.cohortPrepared cohort)
                                let required =
                                    [ "blueprint", ConstructionAdmission.cohortCanonicalBlueprintDigest cohort
                                      "sourceManifest", view.Manifest.Digest
                                      "decisionRecords", ConstructionDecisions.digest decisions ]
                                let names = reported |> List.map fst |> Set.ofList
                                (reported @ (required |> List.filter (fun (name, _) -> not (names.Contains name))))
                                |> List.map (fun (name, digest) -> name, str digest)
                            let artifact =
                                obj [ "schemaVersion", str Schema
                                      "cacheKey", str key
                                      "hostBinding", obj binding
                                      "defaultPlan",
                                      obj [ "plan", JsonNode.Parse b.PlanCanonicalJson
                                            "planDigest", str b.PlanDigest
                                            "obligations", arr (output.Obligations |> List.map obligationNode)
                                            "generatedLayout", strs output.GeneratedLayout
                                            "provenance",
                                            obj [ "generatorId", str output.Provenance.GeneratorId
                                                  "generatorVersion", str output.Provenance.GeneratorVersion
                                                  "vocabularyVersion", str output.Provenance.VocabularyVersion
                                                  "inputDigests", obj (inputDigests output.Provenance.InputDigests)
                                                  "defaultPlanDigest", str output.Provenance.DefaultPlanDigest ] ]
                                      "construction",
                                      obj [ "ownershipRoot", str (ConstructionTree.placed configuration.ConstructionMount built.OwnershipRoot)
                                            "generatorVersion", str built.GeneratorVersion
                                            "generatedFiles", arr (built.Artifacts |> List.map (fun a ->
                                                obj [ "path", str (ConstructionTree.placed configuration.ConstructionMount a.Path); "sha256", str a.Digest ] :> JsonNode))
                                            "extensionContracts", strs (built.ExtensionContracts |> List.map _.Name)
                                            "obligationIds", strs (built.Obligations |> List.map _.Id) ]
                                      "permittedExtensionFiles", strs (permitted |> List.map (fun slot -> ConstructionTree.placed configuration.ConstructionMount slot.File))
                                      "permittedExtensionSlots", arr (permitted |> List.map (fun slot ->
                                          obj [ "path", str (ConstructionTree.placed configuration.ConstructionMount slot.File); "contracts", strs slot.Contracts
                                                "maxBytes", JsonValue.Create slot.MaxBytes
                                                "kind", str (if ConstructionRouting.isAcceptance routing slot then "acceptanceAdapter" else "constructionContract") ] :> JsonNode))
                                      "handoff", ConstructionHandoff.toJson handoff
                                      "handoffMarkdown", str (ConstructionHandoff.toMarkdown handoff)
                                      // The contract version the brief carries (VAC-002-P3-01 R-PRAX-1, OD-6).
                                      "authorityContract",
                                      obj [ "status", str "present"
                                            "sha256", str (Praxis.Domain.Work.AuthorityContract.digest Praxis.Domain.Work.AuthorityContract.Text)
                                            "frozenVersion", str Praxis.Domain.Work.AuthorityContract.Version ] ]
                            artifact.ToJsonString options + "\n")))

    // ------------------------------------------------------------- reading

    let private parse (text: string) =
        try
            match JsonNode.Parse text with
            | :? JsonObject as node when (match node["schemaVersion"] with :? JsonValue as v -> v.ToJsonString() = "\"" + Schema + "\"" | _ -> false) -> Ok node
            | _ -> Error "not a praxis.construction-prepared/1 artifact"
        with error -> Error("prepared artifact is not JSON: " + error.Message)

    let private field (node: JsonObject) (path: string list) =
        path |> List.fold (fun (current: JsonNode) name ->
            match current with
            | :? JsonObject as o -> o[name]
            | _ -> null) (node :> JsonNode)

    let private textAt node path =
        match field node path with
        | :? JsonValue as value -> (try Some(value.GetValue<string>()) with _ -> None)
        | _ -> None

    let private report (key: string) (reused: bool) (text: string) =
        parse text |> Result.bind (fun node ->
            match field node [ "handoff" ], textAt node [ "handoffMarkdown" ] with
            | (:? JsonObject as handoff), Some markdown ->
                Ok { Text = text; CacheKey = key; Reused = reused; HandoffJson = handoff.DeepClone() :?> JsonObject; HandoffMarkdown = markdown
                     Obligations = (match field node [ "defaultPlan"; "obligations" ] with :? JsonArray as a -> a.Count | _ -> 0)
                     PermittedFiles =
                        match field node [ "permittedExtensionFiles" ] with
                        | :? JsonArray as files -> files |> Seq.choose (fun f -> match f with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None) |> Seq.toList
                        | _ -> [] }
            | _ -> Error "prepared artifact lacks its handoff")

    /// Re-validate a stored artifact before reuse: same key, and its plan
    /// still passes admission under the current grant with the same digest.
    let private revalidate configuration policy now groupId (key: string) (text: string) =
        parse text |> Result.bind (fun node ->
            if textAt node [ "cacheKey" ] <> Some key then Error "stored prepared artifact is filed under another cache key"
            else
                match field node [ "defaultPlan"; "plan" ], textAt node [ "defaultPlan"; "planDigest" ] with
                | (:? JsonObject as plan), Some digest ->
                    ConstructionAdmission.admit configuration policy now groupId (plan.ToJsonString()) |> Result.bind (fun admittedPlan ->
                        if (ConstructionAdmission.binding admittedPlan).PlanDigest <> digest then Error "stored prepared plan no longer admits to its recorded digest"
                        else Ok admittedPlan)
                | _ -> Error "stored prepared artifact lacks its plan")

    // ------------------------------------------------------------- prepare

    let prepare (generator: DefaultPlanGenerator) (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (now: DateTimeOffset)
                (groupId: string) (expectedCohort: string option) (vocabulary: string option) (decisionsPath: string option) : Result<PreparedReport, string> =
        let vocabulary = defaultArg vocabulary OrdoDefaultPlan.VocabularyVersion
        (if List.contains vocabulary OrdoDefaultPlan.SupportedVocabularies then Ok() else Error("unsupported construction vocabulary " + vocabulary))
        |> Result.bind (fun () -> admitted configuration policy now groupId generator vocabulary decisionsPath)
        |> Result.bind (fun (cohort, decisions, binding, key) ->
            match expectedCohort with
            | Some expected when expected <> (ConstructionAdmission.cohortGrant cohort).CohortId ->
                Error("the signed grant for group " + groupId + " is for cohort " + (ConstructionAdmission.cohortGrant cohort).CohortId + ", not " + expected)
            | _ -> Ok(cohort, decisions, binding, key))
        |> Result.bind (fun (cohort, decisions, binding, key) ->
            ConstructionDecisions.store configuration decisions |> Result.bind (fun _ ->
                ConstructionHostStore.read configuration "prepared" key |> Result.mapError ((+) "prepared cache unreadable: ") |> Result.bind (function
                    | Some text ->
                        revalidate configuration policy now groupId key text
                        |> Result.mapError ((+) "cached prepared artifact refused: ")
                        |> Result.bind (fun _ -> report key true text)
                    | None ->
                        acceptanceAdapters configuration cohort |> Result.bind (fun adapters ->
                            generate configuration policy now groupId generator vocabulary cohort adapters decisions binding key) |> Result.bind (fun text ->
                            ConstructionHostStore.writeOnce configuration "prepared" key text
                            |> Result.mapError ((+) "prepared artifact not recorded: ")
                            |> Result.bind (fun _ -> report key false text)))))

    // --------------------------------------------------------------- apply

    /// Field-by-field differences between the artifact's binding and the
    /// binding re-derived from current host state.
    let drift (recorded: JsonObject) (current: (string * JsonNode) list) : string list =
        let names = (current |> List.map fst) @ (recorded |> Seq.map _.Key |> Seq.toList) |> List.distinct
        names |> List.choose (fun name ->
            let now = current |> List.tryFind (fun (n, _) -> n = name) |> Option.map (fun (_, v) -> v.ToJsonString())
            let was = match recorded[name] with null -> None | value -> Some(value.ToJsonString())
            if now = was then None
            else Some(sprintf "%s %s -> %s" name (defaultArg was "(absent)") (defaultArg now "(absent)")))

    let private readPrepared (path: string) =
        try
            let full = Path.GetFullPath path
            let info = FileInfo full
            if not info.Exists then Error "prepared artifact does not exist"
            elif not (isNull info.LinkTarget) then Error "prepared artifact is a symbolic link"
            elif info.Length > int64 maxPreparedBytes then Error "prepared artifact exceeds 4 MiB"
            else Ok(File.ReadAllText(full, UTF8Encoding(false, true)))
        with error -> Error("prepared artifact unreadable: " + error.Message)

    /// Record a refusal under the host construction root (content-addressed).
    let recordRefusal (configuration: LocalHostConfiguration) (operation: string) (groupId: string) (key: string option) (reasons: string list) =
        let node =
            obj [ "schemaVersion", str RefusalSchema; "operation", str operation; "groupId", str groupId
                  "cacheKey", (match key with Some k -> str k | None -> null); "reasons", strs reasons ]
        let text = node.ToJsonString options + "\n"
        ConstructionHostStore.writeOnce configuration "refusals" (ConstructionTree.digest text) text |> Result.toOption

    /// Re-validate, apply generated files, then the permitted handwritten
    /// files. Refusals return the reasons and where they were recorded.
    let applyPrepared (generator: DefaultPlanGenerator) (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (now: DateTimeOffset)
                      (groupId: string) (preparedPath: string) (targetRef: string) (extensions: string option) : Result<PreparedApplication, PreparedRefusal> =
        let refuseDenied key reasons denied = Error { Reasons = reasons; RecordPath = recordRefusal configuration "apply" groupId key reasons; Denied = denied }
        let refuse key reasons = refuseDenied key reasons []
        match readPrepared preparedPath |> Result.bind (fun text -> parse text |> Result.map (fun node -> text, node)) with
        | Error reason -> refuse None [ reason ]
        | Ok(text, node) ->
            let suppliedKey = textAt node [ "cacheKey" ]
            // The vocabulary is the preparer's choice, recorded in the binding;
            // it enters the cache key, so a changed one cannot match the host copy.
            let vocabulary = textAt node [ "hostBinding"; "vocabularyVersion" ] |> Option.defaultValue OrdoDefaultPlan.VocabularyVersion
            match admitted configuration policy now groupId generator vocabulary None with
            | Error reason -> refuse suppliedKey [ "current host state refuses the prepared construction: " + reason ]
            | Ok(_, _, binding, key) ->
                let recorded = match field node [ "hostBinding" ] with :? JsonObject as o -> o | _ -> JsonObject()
                match drift recorded binding with
                | _ :: _ as changes -> refuse suppliedKey (("prepared artifact drifted from current host state: " + String.concat "; " changes) :: [])
                | [] when suppliedKey <> Some key -> refuse suppliedKey [ "prepared artifact cache key differs from the key of its own binding" ]
                | [] ->
                    match ConstructionHostStore.read configuration "prepared" key with
                    | Error reason -> refuse (Some key) [ "prepared cache unreadable: " + reason ]
                    | Ok None -> refuse (Some key) [ "the host holds no validated prepared artifact for this cache key; run construct prepare" ]
                    | Ok(Some stored) when stored <> text -> refuse (Some key) [ "prepared artifact differs from the host's validated artifact for its cache key" ]
                    | Ok(Some stored) ->
                        match revalidate configuration policy now groupId key stored with
                        | Error reason -> refuse (Some key) [ reason ]
                        | Ok admittedPlan ->
                            let routing = ConstructionRouting.classify configuration admittedPlan
                            let permitted =
                                match field node [ "permittedExtensionFiles" ] with
                                | :? JsonArray as files -> files |> Seq.choose (fun f -> match f with :? JsonValue as v -> (try Some(v.GetValue<string>()) with _ -> None) | _ -> None) |> Set.ofSeq
                                | _ -> Set.empty
                            // Judge handwritten files before any effect.
                            let placed name = ConstructionTree.placed configuration.ConstructionMount name
                            let judged =
                                match extensions with
                                | None -> Ok []
                                | Some directory ->
                                    match ConstructionExtension.inspect routing directory with
                                    | Error reason -> Error([ reason ], [ directory, reason ])
                                    | Ok entries ->
                                        let denied =
                                            entries |> List.choose (fun (name, verdict) ->
                                                match verdict with
                                                | Error reason -> Some(placed name, reason)
                                                | Ok file when not (permitted.Contains file.Path) -> Some(file.Path, name + ": not a permitted file of the prepared artifact")
                                                | Ok _ -> None)
                                        if denied.IsEmpty then Ok(entries |> List.choose (function _, Ok file -> Some file | _ -> None))
                                        else Error([ "extension refused: " + String.concat "; " (denied |> List.map snd) ], denied)
                            // Placement on the target tree is part of the same pre-check:
                            // a link, submodule, generated file or missing adapter at a
                            // slot path refuses everything before any effect (B1).
                            let placed =
                                judged |> Result.bind (fun accepted ->
                                    if accepted.IsEmpty then Ok accepted
                                    else
                                        match ConstructionTree.readRef configuration.Git configuration.RepositoryRoot targetRef with
                                        | Error reason -> Error([ reason ], [])
                                        | Ok head ->
                                            let verdicts = accepted |> List.map (fun file -> file, ConstructionExtension.placement configuration.Git configuration.RepositoryRoot routing head file)
                                            match verdicts |> List.choose (function file, Error reason -> Some(file.Path, reason) | _ -> None) with
                                            | [] -> Ok accepted
                                            | denied -> Error([ "extension refused: " + String.concat "; " (denied |> List.map snd) ], denied))
                            match placed with
                            | Error(reasons, denied) -> refuseDenied (Some key) reasons denied
                            | Ok accepted ->
                                match ConstructionApplication.apply configuration admittedPlan targetRef now with
                                | Error reason -> refuse (Some key) [ reason ]
                                | Ok construction ->
                                    match extensions with
                                    | None -> Ok { CacheKey = key; Construction = construction; Extension = None }
                                    | Some directory ->
                                        match ConstructionExtension.extend configuration admittedPlan targetRef directory now with
                                        | Error reason -> refuse (Some key) [ "generated files applied; handwritten files refused: " + reason ]
                                        | Ok extension ->
                                            let landed = extension.Files |> List.map (fun (file, _) -> file.Path, file.Digest) |> Set.ofList
                                            let judgedSet = accepted |> List.map (fun file -> file.Path, file.Digest) |> Set.ofList
                                            if landed <> judgedSet then refuse (Some key) [ "handwritten files changed between judgement and application" ]
                                            else Ok { CacheKey = key; Construction = construction; Extension = Some extension }
