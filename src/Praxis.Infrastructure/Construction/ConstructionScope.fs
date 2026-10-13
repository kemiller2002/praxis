namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Application.Work
open Praxis.Contracts.Work
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Construction

/// Mandatory construction-package scope verification (GH-234, decision
/// DF-ROS-2026-A074). A host-implemented checklist validator, never a worker
/// command. The host construction journal is *evidence only*: authority comes
/// from the protected ECIR host policy and signed approval, re-verified here at
/// check time, and every journalled write is re-derived by the pinned Ordo
/// engine and compared with the candidate's Git objects. This verifies the
/// scope and provenance of host-applied writes; it is not OS containment.
[<RequireQualifiedAccess>]
module ConstructionScope =
    [<Literal>]
    let Schema = "praxis.construction-scope/1"

    let private constructionNames =
        [ "Construction-Plan"; "Construction-Generator"; "Construction-Ordo"; "Construction-Group"; "Construction-Cohort"
          "Construction-Source"; "Construction-Manifest"; "Construction-Blueprint"; "Construction-Decisions"; "Construction-Policy" ]
    let private extensionNames =
        [ "Extension-Construction"; "Extension-Plan"; "Extension-Group"; "Extension-Cohort"; "Extension-Route"; "Extension-Policy"; "Extension-Slot" ]

    /// Every `Name: value` line of a commit message whose name is a host trailer.
    let trailers (message: string) =
        message.Split('\n') |> Array.toList |> List.map _.TrimEnd('\r') |> List.choose (fun line ->
            match line.IndexOf ": " with
            | -1 -> None
            | at ->
                let name = line.Substring(0, at)
                if List.contains name constructionNames || List.contains name extensionNames then Some(name, line.Substring(at + 2)) else None)

    let private single name (values: (string * string) list) =
        match values |> List.filter (fst >> (=) name) with
        | [ _, value ] -> Some value
        | _ -> None

    [<RequireQualifiedAccess>]
    type private EntryKind = Construction | Extension | Unrecognized

    type private Entry = { Journal: LocalIntegrationJournal; Parents: string list; Trailers: (string * string) list }

    let private kind (entry: Entry) =
        match single "Construction-Plan" entry.Trailers, single "Extension-Construction" entry.Trailers with
        | Some _, None -> EntryKind.Construction
        | None, Some _ -> EntryKind.Extension
        | _ -> EntryKind.Unrecognized

    let private groupOf (values: (string * string) list) =
        match single "Construction-Group" values, single "Extension-Group" values with
        | Some group, None | None, Some group -> Some group
        | _ -> None

    let private framed (values: string list) =
        let buffer = StringBuilder()
        for value in values do buffer.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value).Append('|') |> ignore
        "sha256:" + (Encoding.UTF8.GetBytes(buffer.ToString()) |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

    let private inside (parent: string) (child: string) =
        let parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath parent)
        let child = Path.TrimEndingDirectorySeparator(Path.GetFullPath child)
        String.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

    /// The journal is trusted as *host-produced evidence* only when it lives in
    /// the operator-provisioned construction root: absolute, outside the
    /// repository, unlinked, accepted by the host file-access policy (mode 0700
    /// under `unix-private`), and not inside or around a worker-writable root.
    /// Anything else could hold agent-supplied bytes, so it is refused.
    let private trustedRoot (configuration: LocalHostConfiguration) =
        match configuration.ConstructionRoot with
        | None -> Error "construction-scope-journal-unavailable: the host configuration names no construction root"
        | Some store ->
            try
                let root = LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store
                let workerWritable = [ configuration.CheckoutRoot; configuration.SpoolRoot; configuration.SubmissionRoot ]
                if workerWritable |> List.exists (fun writable -> inside writable root || inside root writable) then
                    Error "construction-scope-journal-untrusted: the construction root overlaps a worker-writable host root"
                else
                    configuration.FileAccess.CheckDirectory root
                    |> Result.mapError ((+) "construction-scope-journal-untrusted: ")
                    |> Result.map (fun () -> root)
            with error -> Error("construction-scope-journal-untrusted: " + error.Message)

    /// Every journal entry, strictly: an entry is exactly `plan.json`, or
    /// `plan.json` plus an `applied.json` bound to that plan's digest, stored
    /// under its own identity; links, partial or foreign layouts refuse.
    let private inventory (root: string) : Result<LocalIntegrationJournal list, string> =
        try
            let paths = Directory.GetFileSystemEntries root |> Array.sort |> Array.toList
            if paths.Length > 512 then invalidOp "the journal inventory exceeds its bound"
            paths |> List.choose (fun path ->
                let name = Path.GetFileName path
                if not (LocalHostJournalFiles.noLinks path) || not (Directory.Exists path) then invalidOp ("unexpected entry " + name)
                elif name = "locks" || ConstructionApplication.reserved.Contains name then None
                else
                    let names = Directory.GetFileSystemEntries path |> Array.map Path.GetFileName |> Array.sort
                    if names <> [| "plan.json" |] && names <> [| "applied.json"; "plan.json" |] then invalidOp ("incomplete or unexpected journal entry " + name)
                    for file in names do
                        if not (LocalHostJournalFiles.noLinks (Path.Combine(path, file))) then invalidOp "a journal entry contains a link"
                    let plan =
                        LocalHostJournalFiles.readBounded (Path.Combine(path, "plan.json")) |> LocalIntegrationJournalJson.readPlan
                        |> function Ok value -> value | Error error -> invalidOp (name + ": " + error)
                    if LocalHostJournalFiles.directory root plan.RepositoryIdentity plan.DispatchId <> path then
                        invalidOp ("journal entry " + name + " is stored under another identity")
                    let applied =
                        if names.Length = 1 then None
                        else
                            LocalHostJournalFiles.readBounded (Path.Combine(path, "applied.json")) |> LocalIntegrationJournalJson.readApplied plan
                            |> function Ok at -> Some at | Error error -> invalidOp (name + ": " + error)
                    Some { Plan = plan; AppliedAt = applied })
            |> Ok
        with error -> Error("construction-scope-journal-unreadable: " + error.Message)

    /// Path and SHA-256 of every file under the construction root except
    /// locks: any change to host evidence changes the pinned input digest.
    let private fingerprint (root: string) =
        let rec walk (directory: string) (depth: int) : string list =
            if depth > 4 then [ "too-deep:" + directory ]
            else
                Directory.GetFileSystemEntries directory |> Array.sort |> Array.toList |> List.collect (fun path ->
                    let relative = Path.GetRelativePath(root, path).Replace('\\', '/')
                    if relative = "locks" then []
                    elif not (LocalHostJournalFiles.noLinks path) then [ "link:" + relative ]
                    elif Directory.Exists path then walk path (depth + 1)
                    else
                        use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                        if input.Length > 8388608L then [ "oversized:" + relative ]
                        else [ relative + "=" + (SHA256.HashData input |> Convert.ToHexString) ])
        try
            let entries = walk root 0
            if entries.Length > 4096 then "unbounded" else framed entries
        with error -> "unreadable:" + error.GetType().Name

    let private readCommit (configuration: LocalHostConfiguration) commit =
        ConstructionTree.commitObject configuration.Git configuration.RepositoryRoot commit

    /// Whether the package is a construction package, from protected,
    /// independent facts only; nothing a worker asserts is consulted. Any one
    /// fact suffices, so omitting trailers cannot evade the row:
    /// (a) the host construction journal holds an entry for the packet's
    ///     native group (an unreadable or untrusted journal counts: fail closed);
    /// (b) a commit between the delegated source and the candidate carries a
    ///     host construction or extension trailer;
    /// (c) with construction enabled, the candidate changes a path under the
    ///     host-placed generated directory (`MOUNT/generated/`).
    let identify (configuration: LocalHostConfiguration) (loadPolicy: unit -> Result<EcirHostPolicy, string>)
                 (packet: LocalWorkerPacket) (candidate: string) : Result<WorkerCheckValidator option, string> =
        let git, repository = configuration.Git, configuration.RepositoryRoot
        ConstructionTree.commitsBetween git repository packet.SourceCommit candidate
        |> Result.bind (fun commits ->
            commits |> List.fold (fun acc commit ->
                acc |> Result.bind (fun found ->
                    if found then Ok true
                    else readCommit configuration commit |> Result.map (fun (_, message) -> not (trailers message).IsEmpty))) (Ok false))
        |> Result.bind (fun trailered ->
            match configuration.ConstructionRoot with
            | None -> Ok(trailered, false, false, "construction-disabled")
            | Some _ ->
                let journal, print =
                    match trustedRoot configuration with
                    | Error _ -> true, "untrusted"
                    | Ok root ->
                        let journalled =
                            match inventory root with
                            | Error _ -> true
                            | Ok entries ->
                                entries |> List.exists (fun entry ->
                                    match readCommit configuration entry.Plan.CandidateCommit with
                                    | Ok(_, message) -> groupOf (trailers message) = Some packet.GroupId
                                    | Error _ -> true)
                        journalled, fingerprint root
                let generated = ConstructionTree.placed configuration.ConstructionMount "generated/"
                ConstructionTree.changedPaths git repository packet.SourceCommit candidate
                |> Result.map (fun paths -> trailered, journal, paths |> List.exists (fun path -> path.StartsWith(generated, StringComparison.Ordinal)), print))
        |> Result.map (fun (trailered, journal, generated, print) ->
            if not (trailered || journal || generated) then None
            else
                let policy = match loadPolicy() with Ok policy -> "policy:" + policy.Revision | Error error -> "policy-unavailable:" + error
                Some { ValidatorId = WorkerChecklist.ConstructionScopeValidatorId
                       InputDigest =
                         framed [ Schema; LocalAgentHandoff.packetDigest packet; candidate; configuration.Digest; policy; print
                                  sprintf "facts:%b:%b:%b" trailered journal generated ] })

    // ------------------------------------------------------------ verification

    type private Pinned =
        { Root: string
          Chain: Entry list
          Construction: Entry
          Extensions: Entry list
          Admitted: ConstructionAdmission.Admitted }

    let private short (entry: Entry) = entry.Journal.Plan.DispatchId

    /// The applied, recognized journal entries for the packet's group, with
    /// per-entry consistency reasons; pending and foreign entries refuse.
    let private evidence (configuration: LocalHostConfiguration) (packet: LocalWorkerPacket) (entries: LocalIntegrationJournal list) =
        let reasons =
            [ for entry in entries do
                  if entry.Plan.RepositoryIdentity <> configuration.RepositoryIdentity then
                      yield "construction-scope-journal-foreign-entry: " + entry.Plan.DispatchId
                  elif entry.AppliedAt.IsNone then
                      yield "construction-scope-journal-unreconciled: " + entry.Plan.DispatchId + " is prepared but has no applied record; rerun the construction command to reconcile it" ]
        let observed =
            entries
            |> List.filter (fun entry -> entry.Plan.RepositoryIdentity = configuration.RepositoryIdentity && entry.AppliedAt.IsSome)
            |> List.map (fun journal ->
                match readCommit configuration journal.Plan.CandidateCommit with
                | Error error -> Error("construction-scope-journal-inconsistent: " + journal.Plan.DispatchId + " candidate commit is unreadable: " + error)
                | Ok(parents, message) -> Ok { Journal = journal; Parents = parents; Trailers = trailers message })
        let failures = observed |> List.choose (function Error reason -> Some reason | Ok _ -> None)
        let group = observed |> List.choose (function Ok entry when groupOf entry.Trailers = Some packet.GroupId -> Some entry | _ -> None)
        let consistency =
            [ for entry in group do
                  let plan = entry.Journal.Plan
                  if entry.Parents <> [ plan.BeforeCommit ] then
                      yield "construction-scope-journal-inconsistent: " + short entry + " candidate is not a single commit on its recorded base"
                  match kind entry with
                  | EntryKind.Unrecognized -> yield "construction-scope-journal-inconsistent: " + short entry + " carries no unambiguous host construction or extension trailers"
                  | EntryKind.Construction ->
                      if not (plan.DispatchId.StartsWith "construct-") then yield "construction-scope-journal-inconsistent: " + short entry + " is not a construction identity"
                      if single "Construction-Plan" entry.Trailers <> Some plan.PacketDigest then
                          yield "construction-scope-plan-digest-mismatch: " + short entry + " journal plan digest differs from its commit's Construction-Plan trailer"
                  | EntryKind.Extension ->
                      if not (plan.DispatchId.StartsWith "extend-") then yield "construction-scope-journal-inconsistent: " + short entry + " is not an extension identity"
                      if single "Extension-Plan" entry.Trailers <> Some plan.PacketDigest then
                          yield "construction-scope-plan-digest-mismatch: " + short entry + " journal plan digest differs from its commit's Extension-Plan trailer" ]
        reasons @ failures @ consistency, group

    /// Walk the candidate back through journalled host entries to their base.
    let private chain (packet: LocalWorkerPacket) (candidate: string) (group: Entry list) =
        let byCandidate = group |> List.map (fun entry -> entry.Journal.Plan.CandidateCommit, entry) |> Map.ofList
        let rec walk (commit: string) (acc: Entry list) =
            match byCandidate |> Map.tryFind commit with
            | Some entry when acc.Length < 256 && not (acc |> List.exists (fun seen -> seen.Journal.Plan.CandidateCommit = commit)) ->
                walk entry.Journal.Plan.BeforeCommit (entry :: acc)
            | _ -> commit, acc
        if group.IsEmpty then
            Error [ "construction-scope-evidence-missing: the host construction journal holds no applied construction for group " + packet.GroupId ]
        elif not (byCandidate.ContainsKey candidate) then
            Error [ sprintf "construction-scope-candidate-not-host-applied: candidate %s is not the exact commit of a journalled host construction or extension for group %s" candidate packet.GroupId ]
        else
            let baseCommit, entries = walk candidate []
            let carried = entries |> List.map (fun entry -> entry.Journal.Plan.DispatchId) |> Set.ofList
            let reasons =
                [ if baseCommit <> packet.SourceCommit then
                      yield sprintf "construction-scope-base-mismatch: the host-applied chain starts at %s, not the delegated source commit %s" baseCommit packet.SourceCommit
                  for entry in group do
                      if not (carried.Contains entry.Journal.Plan.DispatchId) then
                          yield sprintf "construction-scope-evidence-stale: host-applied %s (%s) is not carried by the candidate; a newer or divergent construction exists" (short entry) entry.Journal.Plan.CandidateCommit ]
            match entries |> List.filter (fun entry -> kind entry = EntryKind.Construction) |> List.tryLast with
            | None -> Error(reasons @ [ "construction-scope-evidence-missing: the candidate carries no journalled host construction" ])
            | Some construction ->
                let after = entries |> List.skipWhile (fun entry -> entry <> construction) |> List.tail
                Ok(entries, construction, after, reasons)

    let private planRecord (configuration: LocalHostConfiguration) root (construction: Entry) =
        let digest = construction.Journal.Plan.PacketDigest
        match ConstructionApplication.planRecordPath root digest with
        | None -> Error "construction-scope-plan-record-missing: the journalled plan digest is not a SHA-256 digest"
        | Some path when not (File.Exists path) -> Error("construction-scope-plan-record-missing: no host plan record for " + digest + "; rerun `construct apply` with the same plan to record it")
        | Some path ->
            if not (LocalHostJournalFiles.noLinks path) then Error "construction-scope-journal-untrusted: the plan record is linked"
            else
                let info = FileInfo path
                if info.Length > 1048576L then Error "construction-scope-plan-record-missing: the plan record exceeds 1 MiB"
                else
                    try Ok(UTF8Encoding(false, true).GetString(File.ReadAllBytes path))
                    with error -> Error("construction-scope-plan-record-missing: unreadable plan record: " + error.Message)
        |> Result.mapError (fun reason -> [ reason ])

    let private changesFor (configuration: LocalHostConfiguration) (built: Construction.Construction) writes =
        let content = built.Artifacts |> List.map (fun artifact -> artifact.Path, artifact.Content) |> Map.ofList
        let placed = ConstructionTree.placed configuration.ConstructionMount
        writes |> List.choose (function
            | Construction.Create path | Construction.Replace path -> Some { ConstructionTreeChange.Path = placed path; Content = Some content[path] }
            | Construction.RemoveStale path -> Some { ConstructionTreeChange.Path = placed path; Content = None }
            | Construction.Unchanged _ -> None)

    /// Re-derive the current construction's operations against its recorded
    /// base and compare every pin with the journal, trailers and packet.
    let private constructionReasons (configuration: LocalHostConfiguration) (policy: EcirHostPolicy) (packet: LocalWorkerPacket) (pinned: Pinned) =
        let git, repository, mount = configuration.Git, configuration.RepositoryRoot, configuration.ConstructionMount
        let b = ConstructionAdmission.binding pinned.Admitted
        let built = ConstructionAdmission.construction pinned.Admitted
        let entry = pinned.Construction
        let plan = entry.Journal.Plan
        let trailer name = single name entry.Trailers
        let set (values: string list) = Set.ofList values
        let expectedId =
            ConstructionApplication.constructionId
                { Binding = b; TargetRef = plan.TargetRef; TargetCommit = plan.BeforeCommit; OwnershipRoot = ""
                  Writes = []; ExtensionContracts = []; Obligations = [] }
        let manifest = built.Artifacts |> List.tryFind (fun artifact -> artifact.Path.EndsWith "/construction-manifest.json")
        let submission = manifest |> Option.map _.Digest |> Option.defaultValue b.PlanDigest
        let receipt =
            EcirHostPolicy.resolve repository packet.GroupId (packet.Members |> List.map _.WorkItemId) policy
            |> Result.map (fun (grant, _) -> "sha256:" + EcirDispatchTransaction.hashText grant.ReceiptJson)
        [ if b.PlanDigest <> plan.PacketDigest then
              yield sprintf "construction-scope-plan-digest-mismatch: the host plan record re-admits to %s, but the journal records %s" b.PlanDigest plan.PacketDigest
          if trailer "Construction-Generator" <> Some b.GeneratorVersion then
              yield sprintf "construction-scope-generator-mismatch: the construction commit names generator %s; the pinned engine generates %s" (defaultArg (trailer "Construction-Generator") "none") b.GeneratorVersion
          if trailer "Construction-Ordo" <> Some("core " + b.OrdoCoreVersion + ", construction " + b.OrdoConstructionVersion) then
              yield sprintf "construction-scope-generator-mismatch: the construction commit names Ordo %s; the pinned engine is core %s, construction %s" (defaultArg (trailer "Construction-Ordo") "none") b.OrdoCoreVersion b.OrdoConstructionVersion
          if trailer "Construction-Cohort" <> Some b.CohortId || packet.CohortId <> b.CohortId then
              yield sprintf "construction-scope-cohort-mismatch: packet %s, construction trailer %s, approved grant %s" packet.CohortId (defaultArg (trailer "Construction-Cohort") "none") b.CohortId
          if trailer "Construction-Group" <> Some b.GroupId then yield "construction-scope-trailer-mismatch: Construction-Group"
          if trailer "Construction-Source" <> Some b.SourceCommit then yield "construction-scope-trailer-mismatch: Construction-Source"
          if trailer "Construction-Manifest" <> Some(b.ManifestPath + " " + b.ManifestDigest) then yield "construction-scope-trailer-mismatch: Construction-Manifest"
          if trailer "Construction-Blueprint" <> Some(b.BlueprintPath + " " + b.BlueprintDigest) then yield "construction-scope-trailer-mismatch: Construction-Blueprint"
          if trailer "Construction-Decisions" <> Some(String.concat " " b.DecisionIds) then yield "construction-scope-trailer-mismatch: Construction-Decisions"
          if packet.SourceCommit <> b.SourceCommit || packet.SourceManifestDigest <> b.ManifestDigest || packet.BlueprintDigest <> b.BlueprintDigest then
              yield "construction-scope-source-mismatch: the packet's source commit, manifest or blueprint differs from the approved grant"
          if set (packet.Members |> List.collect _.RequirementKeys) <> set b.RequirementKeys then
              yield "construction-scope-requirement-set-mismatch: the packet's requirement keys differ from the signed approval's"
          if set packet.DecisionIds <> set b.DecisionIds then
              yield "construction-scope-decision-set-mismatch: the packet's decisions differ from the signed approval's"
          if packet.AuthorityRevision <> policy.Revision then
              yield sprintf "construction-scope-authorization-refused: the packet was delegated under policy %s; the protected host policy is now %s" packet.AuthorityRevision policy.Revision
          match receipt with
          | Ok digest when digest = packet.ReceiptDigest -> ()
          | Ok _ -> yield "construction-scope-authorization-refused: the packet's receipt differs from the current host grant"
          | Error error -> yield "construction-scope-authorization-refused: " + error
          if plan.DispatchId <> expectedId then
              yield sprintf "construction-scope-journal-inconsistent: %s does not match the identity %s re-derived from its plan, target and base" plan.DispatchId expectedId
          if plan.SubmissionDigest <> submission then
              yield "construction-scope-digest-inconsistent: " + short entry + " journal submission digest differs from the re-derived construction manifest"
          // Every applied operation, re-planned by the pinned engine on the recorded base.
          match ConstructionTree.existing git repository plan.BeforeCommit mount built.OwnershipRoot with
          | Error error -> yield "construction-scope-operation-mismatch: " + short entry + ": " + error
          | Ok existing ->
              match Construction.planWrites existing built with
              | Error refusals -> yield sprintf "construction-scope-operation-mismatch: %s: the pinned engine refuses its base: %A" (short entry) refusals
              | Ok writes ->
                  match ConstructionTree.verify git repository plan.BeforeCommit plan.CandidateCommit (changesFor configuration built writes) with
                  | Ok() -> ()
                  | Error error -> yield "construction-scope-operation-mismatch: " + short entry + ": " + error ]

    let private routeRecord (root: string) (routing: ConstructionRouting) =
        let path = Path.Combine(root, "routes", ConstructionRouting.routeId routing + ".json")
        let expected = (ConstructionRouting.render routing).ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
        if not (File.Exists path) || not (LocalHostJournalFiles.noLinks path) then Some "construction-scope-route-record-missing: no host routing record for the re-derived route"
        elif LocalHostJournalFiles.readBounded path <> expected then Some "construction-scope-route-record-mismatch: the host routing record differs from the re-derived route"
        else None

    /// Each extension: the current construction, plan, cohort, re-derived
    /// route and slot policy, and exactly its declared slot files and digests.
    let private extensionReasons (configuration: LocalHostConfiguration) (pinned: Pinned) =
        let git, repository = configuration.Git, configuration.RepositoryRoot
        let b = ConstructionAdmission.binding pinned.Admitted
        let routing = ConstructionRouting.classify configuration pinned.Admitted
        let routeId = ConstructionRouting.routeId routing
        let usable = ConstructionRouting.usableSlots routing
        let contracts (slot: ConstructionExtensionSlot) = ConstructionRouting.slotContracts routing slot
        let parseSlot (value: string) =
            match value.Split(' ') |> Array.toList with
            | path :: digest :: named -> Some(path, digest, named)
            | _ -> None
        let perEntry =
            pinned.Extensions |> List.collect (fun entry ->
                let plan = entry.Journal.Plan
                let trailer name = single name entry.Trailers
                let slots = entry.Trailers |> List.filter (fst >> (=) "Extension-Slot") |> List.map (snd >> parseSlot)
                let declared = slots |> List.choose id
                let files =
                    declared |> List.choose (fun (path, digest, _) ->
                        usable |> List.tryFind (fun slot -> ConstructionTree.placed routing.Mount slot.File = path)
                        |> Option.map (fun slot -> { Slot = slot; Path = path; Content = ""; Digest = digest; Contracts = contracts slot }))
                let expectedId = ConstructionExtension.extensionId routing plan.TargetRef plan.BeforeCommit files
                let submission = ConstructionTree.digest (String.concat "\n" (declared |> List.map (fun (path, digest, _) -> path + " " + digest)))
                [ if trailer "Extension-Construction" <> Some(pinned.Construction.Journal.Plan.DispatchId + " " + pinned.Construction.Journal.Plan.CandidateCommit) then
                      yield "construction-scope-extension-stale: " + short entry + " extends a construction other than the candidate's current one"
                  if trailer "Extension-Plan" <> Some b.PlanDigest then yield "construction-scope-plan-digest-mismatch: " + short entry + " extends another plan"
                  if trailer "Extension-Group" <> Some b.GroupId then yield "construction-scope-trailer-mismatch: Extension-Group"
                  if trailer "Extension-Cohort" <> Some b.CohortId then yield "construction-scope-cohort-mismatch: " + short entry + " names cohort " + defaultArg (trailer "Extension-Cohort") "none"
                  if trailer "Extension-Route" <> Some routeId then
                      yield sprintf "construction-scope-slot-policy-changed: %s was routed as %s; the current host slot policy routes %s" (short entry) (defaultArg (trailer "Extension-Route") "none") routeId
                  if slots.IsEmpty || slots |> List.exists Option.isNone then yield "construction-scope-journal-inconsistent: " + short entry + " has no well-formed Extension-Slot trailers"
                  for path, _, named in declared do
                      match usable |> List.tryFind (fun slot -> ConstructionTree.placed routing.Mount slot.File = path) with
                      | None -> yield "construction-scope-slot-not-authorized: " + path + " is not a host-declared slot this construction can use"
                      | Some slot when named <> contracts slot -> yield "construction-scope-slot-not-authorized: " + path + " names contracts the slot policy does not assign"
                      | Some _ -> ()
                  if files.Length = declared.Length && plan.DispatchId <> expectedId then
                      yield sprintf "construction-scope-journal-inconsistent: %s does not match the identity %s re-derived from its route, base and files" plan.DispatchId expectedId
                  if plan.SubmissionDigest <> submission then
                      yield "construction-scope-digest-inconsistent: " + short entry + " journal submission digest differs from its Extension-Slot trailers"
                  match ConstructionTree.changedPaths git repository plan.BeforeCommit plan.CandidateCommit with
                  | Error error -> yield "construction-scope-operation-mismatch: " + short entry + ": " + error
                  | Ok changed ->
                      let allowed = declared |> List.map (fun (path, _, _) -> path) |> Set.ofList
                      for path in changed do
                          if not (allowed.Contains path) then yield "construction-scope-unexpected-file: " + path + " (written by " + short entry + " outside its declared slots)"
                  for path, digest, _ in declared do
                      match ConstructionTree.entryAt git repository plan.CandidateCommit path with
                      | Ok(Some("100644", Some content)) when ConstructionTree.digest content = digest -> ()
                      | Ok _ -> yield "construction-scope-artifact-digest-mismatch: " + path + " in " + short entry + " differs from its Extension-Slot digest"
                      | Error error -> yield "construction-scope-operation-mismatch: " + short entry + ": " + error ])
        let record = if pinned.Extensions.IsEmpty then [] else Option.toList (routeRecord pinned.Root routing)
        perEntry @ record

    /// The candidate's own tree, from Git objects: the generated ownership root
    /// holds exactly the re-derived artifacts, and every other change since the
    /// delegated source is a current host-recorded slot write.
    let private treeReasons (configuration: LocalHostConfiguration) (packet: LocalWorkerPacket) (candidate: string) (pinned: Pinned) =
        let git, repository, mount = configuration.Git, configuration.RepositoryRoot, configuration.ConstructionMount
        let built = ConstructionAdmission.construction pinned.Admitted
        let ownership = ConstructionTree.placed mount built.OwnershipRoot
        let routing = ConstructionRouting.classify configuration pinned.Admitted
        let slotWrites =
            pinned.Extensions |> List.collect (fun entry ->
                entry.Trailers |> List.filter (fst >> (=) "Extension-Slot") |> List.choose (fun (_, value) ->
                    match value.Split(' ') |> Array.toList with
                    | path :: digest :: _ -> Some(path, digest)
                    | _ -> None))
            |> List.fold (fun (latest: Map<string, string>) (path, digest) -> latest.Add(path, digest)) Map.empty
        [ match ConstructionTree.existing git repository candidate mount built.OwnershipRoot with
          | Error error -> yield "construction-scope-unexpected-file: " + error
          | Ok actual ->
              for artifact in built.Artifacts do
                  let path = ConstructionTree.placed mount artifact.Path
                  match actual |> Map.tryFind artifact.Path with
                  | None -> yield "construction-scope-operation-missing: " + path + " is not in the candidate tree"
                  | Some content when ConstructionTree.digest content <> artifact.Digest -> yield "construction-scope-artifact-digest-mismatch: " + path
                  | Some _ -> ()
              let expected = built.Artifacts |> List.map _.Path |> Set.ofList
              for KeyValue(path, _) in actual do
                  if not (expected.Contains path) then
                      yield "construction-scope-unexpected-file: " + ConstructionTree.placed mount path + " (inside the generated ownership root)"
          match ConstructionTree.changedPaths git repository packet.SourceCommit candidate with
          | Error error -> yield "construction-scope-unexpected-file: candidate changes are unobservable: " + error
          | Ok changed ->
              for path in changed do
                  if path.StartsWith(ownership, StringComparison.Ordinal) then ()
                  else
                      match slotWrites |> Map.tryFind path with
                      | None -> yield "construction-scope-unexpected-file: " + path + " (outside the generated ownership root and the host-declared extension slots)"
                      | Some digest ->
                          let limit = routing.Slots @ routing.Acceptance |> List.tryFind (fun slot -> ConstructionTree.placed mount slot.File = path) |> Option.map _.MaxBytes
                          match ConstructionTree.entryAt git repository candidate path, limit with
                          | Ok(Some("100644", Some content)), Some limit
                              when ConstructionTree.digest content = digest && UTF8Encoding(false).GetByteCount content <= limit -> ()
                          | _, None -> yield "construction-scope-slot-not-authorized: " + path
                          | _ -> yield "construction-scope-artifact-digest-mismatch: " + path + " differs from its recorded slot write" ]

    let private check (configuration: LocalHostConfiguration) (loadPolicy: unit -> Result<EcirHostPolicy, string>) (now: DateTimeOffset)
                      (packet: LocalWorkerPacket) (candidate: string) : string list * JsonObject =
        let pins = JsonObject()
        let pin (name: string) (value: string) = pins[name] <- JsonValue.Create value
        let outcome =
            trustedRoot configuration |> Result.mapError List.singleton |> Result.bind (fun root ->
                inventory root |> Result.mapError List.singleton |> Result.bind (fun entries ->
                    let journalReasons, group = evidence configuration packet entries
                    chain packet candidate group
                    |> Result.mapError (fun reasons -> journalReasons @ reasons)
                    |> Result.bind (fun (entries, construction, extensions, chainReasons) ->
                        pins["chain"] <- JsonArray(entries |> List.map (fun entry ->
                            JsonObject([ Collections.Generic.KeyValuePair("id", JsonValue.Create entry.Journal.Plan.DispatchId :> JsonNode)
                                         Collections.Generic.KeyValuePair("before", JsonValue.Create entry.Journal.Plan.BeforeCommit :> JsonNode)
                                         Collections.Generic.KeyValuePair("candidate", JsonValue.Create entry.Journal.Plan.CandidateCommit :> JsonNode) ]) :> JsonNode) |> List.toArray)
                        planRecord configuration root construction
                        |> Result.mapError (fun reasons -> journalReasons @ chainReasons @ reasons)
                        |> Result.bind (fun planText ->
                            loadPolicy() |> Result.mapError (fun error -> journalReasons @ chainReasons @ [ "construction-scope-authorization-refused: " + error ])
                            |> Result.bind (fun policy ->
                                ConstructionAdmission.admit configuration policy now packet.GroupId planText
                                |> Result.mapError (fun error -> journalReasons @ chainReasons @ [ "construction-scope-authorization-refused: " + error ])
                                |> Result.map (fun admitted ->
                                    let pinned = { Root = root; Chain = entries; Construction = construction; Extensions = extensions; Admitted = admitted }
                                    let b = ConstructionAdmission.binding admitted
                                    let built = ConstructionAdmission.construction admitted
                                    pin "policyRevision" policy.Revision
                                    pin "approver" b.Approver
                                    pin "cohortId" b.CohortId
                                    pin "planDigest" b.PlanDigest
                                    pin "generatorVersion" b.GeneratorVersion
                                    pin "ownershipRoot" (ConstructionTree.placed configuration.ConstructionMount built.OwnershipRoot)
                                    pin "routeId" (ConstructionRouting.routeId (ConstructionRouting.classify configuration admitted))
                                    pins["requirementKeys"] <- JsonArray(b.RequirementKeys |> List.map (fun key -> JsonValue.Create key :> JsonNode) |> List.toArray)
                                    journalReasons @ chainReasons
                                    @ constructionReasons configuration policy packet pinned
                                    @ extensionReasons configuration pinned
                                    @ treeReasons configuration packet candidate pinned))))))
        (match outcome with Ok reasons -> reasons | Error reasons -> reasons), pins

    /// Verify at check time. Every reason is reported; an empty list passes.
    let verify (configuration: LocalHostConfiguration) (loadPolicy: unit -> Result<EcirHostPolicy, string>) (clock: unit -> DateTimeOffset)
               (packet: LocalWorkerPacket) (candidate: string) (validator: WorkerCheckValidator) : LocalConstructionScopeObservation =
        let started = clock()
        let reasons, pins =
            if validator.ValidatorId <> WorkerChecklist.ConstructionScopeValidatorId then [ "construction-scope-validator-invalid" ], JsonObject()
            else
                try check configuration loadPolicy started packet candidate
                with error -> [ "construction-scope-host-observation-failed: " + error.GetType().Name + ": " + error.Message ], JsonObject()
        let reasons = reasons |> List.distinct
        let finished = clock()
        let document = JsonObject()
        document["schemaVersion"] <- JsonValue.Create Schema
        document["validatorId"] <- JsonValue.Create validator.ValidatorId
        document["inputDigest"] <- JsonValue.Create validator.InputDigest
        document["packetDigest"] <- JsonValue.Create(LocalAgentHandoff.packetDigest packet)
        document["groupId"] <- JsonValue.Create packet.GroupId
        document["candidateCommit"] <- JsonValue.Create candidate
        document["startedAt"] <- JsonValue.Create(started.ToString "O")
        document["finishedAt"] <- JsonValue.Create(finished.ToString "O")
        document["verdict"] <- JsonValue.Create(if reasons.IsEmpty then "passed" else "refused")
        document["reasons"] <- JsonArray(reasons |> List.map (fun reason -> JsonValue.Create reason :> JsonNode) |> List.toArray)
        document["pins"] <- pins
        document["notice"] <- JsonValue.Create "Verifies the scope and provenance of host-applied construction writes. Not OS containment, which is qualified separately (#231, #235)."
        { StartedAt = started; FinishedAt = finished; Reasons = reasons; Evidence = document.ToJsonString() }

    /// The checklist port for a configured host.
    let host (configuration: LocalHostConfiguration) (loadPolicy: unit -> Result<EcirHostPolicy, string>) (clock: unit -> DateTimeOffset) : LocalConstructionScopeHost =
        { Identify = identify configuration loadPolicy
          Verify = verify configuration loadPolicy clock }
