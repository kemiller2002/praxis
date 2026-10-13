namespace Praxis.Infrastructure.Construction

open System
open Praxis.Domain.Work
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work
open Ordo.Construction

[<RequireQualifiedAccess>]
type ConstructionWriteKind = Create | Replace | Unchanged | RemoveStale

type ConstructionPlannedWrite = { Kind: ConstructionWriteKind; Path: string; Digest: string option }

/// Deterministic, read-only result of planning a construction against the
/// exact target commit. Paths are repository-relative.
type ConstructionPreview =
    { Binding: ConstructionBinding
      TargetRef: string
      TargetCommit: string
      OwnershipRoot: string
      Writes: ConstructionPlannedWrite list
      ExtensionContracts: Artifacts.ExtensionContract list
      Obligations: Artifacts.ConstructionObligation list }

[<RequireQualifiedAccess>]
type ConstructionOutcome =
    | Applied of candidate: string
    | AlreadyCurrent

type ConstructionReport = { Preview: ConstructionPreview; ConstructionId: string; Outcome: ConstructionOutcome }

/// Preview and host-controlled application of an admitted construction
/// (Praxis #242). Only Ordo-planned generated artifacts under the
/// host-placed ownership root change; the target moves by expected-head
/// compare-and-swap after a durable journal entry.
[<RequireQualifiedAccess>]
module ConstructionApplication =
    let private kind write =
        match write with
        | Construction.Create path -> ConstructionWriteKind.Create, path
        | Construction.Replace path -> ConstructionWriteKind.Replace, path
        | Construction.Unchanged path -> ConstructionWriteKind.Unchanged, path
        | Construction.RemoveStale path -> ConstructionWriteKind.RemoveStale, path

    let private describe (refusals: Construction.TargetRefusal list) =
        refusals |> List.map (function
            | Construction.WouldOverwriteHandwritten path -> "would overwrite handwritten " + path
            | Construction.HandwrittenInGeneratedRoot path -> "handwritten file inside the generated root: " + path
            | Construction.OutsideOwnershipRoot path -> "outside the generated ownership root: " + path)
        |> String.concat "; "

    let private changes (configuration: LocalHostConfiguration) (built: Construction.Construction) (writes: ConstructionPlannedWrite list) =
        let content = built.Artifacts |> List.map (fun artifact -> ConstructionTree.placed configuration.ConstructionMount artifact.Path, artifact.Content) |> Map.ofList
        writes |> List.choose (fun write ->
            match write.Kind with
            | ConstructionWriteKind.Create
            | ConstructionWriteKind.Replace -> Some { ConstructionTreeChange.Path = write.Path; Content = Some content[write.Path] }
            | ConstructionWriteKind.RemoveStale -> Some { ConstructionTreeChange.Path = write.Path; Content = None }
            | ConstructionWriteKind.Unchanged -> None)

    let preview (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) (targetRef: string) : Result<ConstructionPreview, string> =
        let git, root, mount = configuration.Git, configuration.RepositoryRoot, configuration.ConstructionMount
        let built = ConstructionAdmission.construction admitted
        ConstructionTree.readRef git root targetRef |> Result.bind (fun target ->
            ConstructionTree.existing git root target mount built.OwnershipRoot |> Result.bind (fun existing ->
                Construction.planWrites existing built |> Result.mapError (fun refusals -> "construction refused: " + describe refusals)
                |> Result.map (fun planned ->
                    let digests = built.Artifacts |> List.map (fun artifact -> artifact.Path, artifact.Digest) |> Map.ofList
                    { Binding = ConstructionAdmission.binding admitted; TargetRef = targetRef; TargetCommit = target
                      OwnershipRoot = ConstructionTree.placed mount built.OwnershipRoot
                      Writes =
                        planned |> List.map (fun write ->
                            let kind, path = kind write
                            { Kind = kind; Path = ConstructionTree.placed mount path; Digest = digests |> Map.tryFind path })
                      ExtensionContracts = built.ExtensionContracts; Obligations = built.Obligations })))

    let private message (preview: ConstructionPreview) =
        let b = preview.Binding
        String.concat "\n"
            [ sprintf "Construct %s from cohort %s" preview.OwnershipRoot b.CohortId
              ""
              "Applied by the Praxis host from an admitted Ordo construction plan."
              "Generated structure only; open obligations remain open."
              ""
              "Construction-Plan: " + b.PlanDigest
              "Construction-Generator: " + b.GeneratorVersion
              "Construction-Ordo: core " + b.OrdoCoreVersion + ", construction " + b.OrdoConstructionVersion
              "Construction-Group: " + b.GroupId
              "Construction-Cohort: " + b.CohortId
              "Construction-Source: " + b.SourceCommit
              "Construction-Manifest: " + b.ManifestPath + " " + b.ManifestDigest
              "Construction-Blueprint: " + b.BlueprintPath + " " + b.BlueprintDigest
              "Construction-Decisions: " + String.concat " " b.DecisionIds
              "Construction-Policy: " + b.PolicyRevision + " approved by " + b.Approver ]

    /// Stable per (plan, target, observed head): a rerun after a crash finds
    /// the same journal entry.
    let constructionId (preview: ConstructionPreview) =
        "construct-" + (ConstructionTree.digest (String.concat "\n" [ preview.Binding.PlanDigest; preview.TargetRef; preview.TargetCommit ])).Substring(7, 24)

    /// Host record directories in the construction root that are not journal
    /// entries: routing records (Praxis #243) and admitted plans (GH-234).
    let reserved = set [ "routes"; "plans" ]

    /// The construction root also holds routing records under `routes/` and
    /// admitted plans under `plans/`; the integration journal leaves them alone.
    let journal (configuration: LocalHostConfiguration) =
        match configuration.ConstructionRoot with
        | None -> Error "construction is disabled: the host configuration names no construction root"
        | Some store -> FileLocalIntegrationJournal.createReserving reserved configuration.RepositoryRoot store |> Result.map (fun journal -> store, journal)

    let private maxPlanRecordBytes = 1048576

    /// `plans/HEX.json` for an Ordo plan digest `sha256:HEX`, or None.
    let planRecordPath (store: string) (planDigest: string) =
        let hex = if planDigest.StartsWith "sha256:" then planDigest.Substring 7 else ""
        if hex.Length = 64 && hex |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f')) then
            Some(IO.Path.Combine(store, "plans", hex + ".json"))
        else None

    /// Record the admitted plan document, write-once under its Ordo plan
    /// digest (GH-234). It is host evidence a later scope check re-admits
    /// under the then-current policy; it never authorizes anything itself.
    let recordPlan (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) : Result<string, string> =
        match configuration.ConstructionRoot with
        | None -> Error "construction is disabled: the host configuration names no construction root"
        | Some store ->
            try
                let root = LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store
                let plans = IO.Path.Combine(root, "plans")
                let content = ConstructionAdmission.planText admitted
                let bytes = Text.UTF8Encoding(false).GetBytes content
                match planRecordPath root (ConstructionAdmission.binding admitted).PlanDigest with
                | None -> Error "admitted plan digest is not a SHA-256 digest"
                | Some _ when bytes.Length > maxPlanRecordBytes -> Error "admitted plan exceeds 1 MiB"
                | Some _ when not (LocalHostJournalFiles.noLinks plans) -> Error "plan record directory contains a link"
                | Some path ->
                    IO.Directory.CreateDirectory plans |> ignore
                    if not (LocalHostJournalFiles.noLinks path) then Error "plan record path contains a link"
                    elif IO.File.Exists path then
                        if IO.File.ReadAllBytes path = bytes then Ok path
                        else Error "a different plan record already exists under this plan digest"
                    else
                        do
                            use output = new IO.FileStream(path, IO.FileMode.CreateNew, IO.FileAccess.Write, IO.FileShare.None)
                            output.Write(bytes, 0, bytes.Length)
                            output.Flush true
                        Ok path
            with error -> Error("plan record refused: " + error.Message)

    /// Entries whose target already names their candidate were applied before
    /// an interruption; record that, never re-apply.
    let reconcile (configuration: LocalHostConfiguration) (journal: Praxis.Application.Work.LocalIntegrationJournalStore) now =
        journal.Pending configuration.RepositoryIdentity |> Result.bind (fun pending ->
            pending |> List.fold (fun acc id ->
                acc |> Result.bind (fun () ->
                    journal.Load configuration.RepositoryIdentity id |> Result.bind (function
                        | Some entry ->
                            match ConstructionTree.readRef configuration.Git configuration.RepositoryRoot entry.Plan.TargetRef with
                            | Ok head when head = entry.Plan.CandidateCommit -> journal.RecordApplied entry.Plan now
                            | _ -> Ok()
                        | None -> Ok()))) (Ok()))

    /// Whole seconds in UTC, so a replayed candidate is byte-identical.
    let truncate (now: DateTimeOffset) =
        DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, now.UtcDateTime.Minute, now.UtcDateTime.Second, TimeSpan.Zero)

    /// Stage `treeChanges` on `before` in a private index, verify the
    /// candidate, journal it, then move `targetRef` by compare-and-swap.
    /// Shared by `apply` and `extend`; the caller holds the lease.
    let land (configuration: LocalHostConfiguration) (store: string) (journal: Praxis.Application.Work.LocalIntegrationJournalStore)
             (id: string) (targetRef: string) (before: string) (treeChanges: ConstructionTreeChange list)
             (message: string) (packetDigest: string) (submissionDigest: string) (at: DateTimeOffset) =
        let git, root = configuration.Git, configuration.RepositoryRoot
        journal.Load configuration.RepositoryIdentity id |> Result.bind (fun prior ->
            let preparedAt = prior |> Option.map _.Plan.PreparedAt |> Option.defaultValue at
            ConstructionTree.stage git root store configuration.Committer before treeChanges message preparedAt
            |> Result.bind (fun candidate ->
                ConstructionTree.verify git root before candidate treeChanges |> Result.bind (fun () ->
                    let plan : LocalIntegrationPlan =
                        { SchemaVersion = "praxis.local-integration-plan/1"; RepositoryIdentity = configuration.RepositoryIdentity
                          DispatchId = id; AttemptId = "1"; PacketDigest = packetDigest; SubmissionDigest = submissionDigest
                          TargetRef = targetRef; BeforeCommit = before; CandidateCommit = candidate; PreparedAt = preparedAt }
                    journal.Prepare plan |> Result.bind (fun _ ->
                        ConstructionTree.readRef git root targetRef |> Result.bind (fun head ->
                            if head = candidate then Ok()
                            elif head <> before then Error "construction target moved after planning; nothing was applied"
                            else ConstructionTree.compareAndSwap git root targetRef candidate before ("praxis construction " + id))
                        |> Result.bind (fun () -> journal.RecordApplied plan at)
                        |> Result.map (fun () -> candidate)))))

    let apply (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) (targetRef: string) (now: DateTimeOffset) : Result<ConstructionReport, string> =
        let root = configuration.RepositoryRoot
        let at = truncate now
        journal configuration |> Result.bind (fun (store, journal) ->
            FileLocalIntegrationJournal.acquire root store configuration.RepositoryIdentity |> Result.bind (fun lease ->
                use _ = lease
                reconcile configuration journal at |> Result.bind (fun () ->
                    preview configuration admitted targetRef |> Result.bind (fun preview -> recordPlan configuration admitted |> Result.map (fun _ -> preview)) |> Result.bind (fun preview ->
                        let id = constructionId preview
                        let built = ConstructionAdmission.construction admitted
                        match changes configuration built preview.Writes with
                        | [] -> Ok { Preview = preview; ConstructionId = id; Outcome = ConstructionOutcome.AlreadyCurrent }
                        | treeChanges ->
                            let manifest = built.Artifacts |> List.tryFind (fun artifact -> artifact.Path.EndsWith "/construction-manifest.json")
                            let submission = manifest |> Option.map _.Digest |> Option.defaultValue preview.Binding.PlanDigest
                            land configuration store journal id targetRef preview.TargetCommit treeChanges (message preview) preview.Binding.PlanDigest submission at
                            |> Result.map (fun candidate -> { Preview = preview; ConstructionId = id; Outcome = ConstructionOutcome.Applied candidate })))))
