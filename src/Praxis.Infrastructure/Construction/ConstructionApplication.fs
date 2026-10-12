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

    let private journal (configuration: LocalHostConfiguration) =
        match configuration.ConstructionRoot with
        | None -> Error "construction is disabled: the host configuration names no construction root"
        | Some store -> FileLocalIntegrationJournal.create configuration.RepositoryRoot store |> Result.map (fun journal -> store, journal)

    /// Entries whose target already names their candidate were applied before
    /// an interruption; record that, never re-apply.
    let private reconcile (configuration: LocalHostConfiguration) (journal: Praxis.Application.Work.LocalIntegrationJournalStore) now =
        journal.Pending configuration.RepositoryIdentity |> Result.bind (fun pending ->
            pending |> List.fold (fun acc id ->
                acc |> Result.bind (fun () ->
                    journal.Load configuration.RepositoryIdentity id |> Result.bind (function
                        | Some entry ->
                            match ConstructionTree.readRef configuration.Git configuration.RepositoryRoot entry.Plan.TargetRef with
                            | Ok head when head = entry.Plan.CandidateCommit -> journal.RecordApplied entry.Plan now
                            | _ -> Ok()
                        | None -> Ok()))) (Ok()))

    let apply (configuration: LocalHostConfiguration) (admitted: ConstructionAdmission.Admitted) (targetRef: string) (now: DateTimeOffset) : Result<ConstructionReport, string> =
        let git, root = configuration.Git, configuration.RepositoryRoot
        let at = DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, now.UtcDateTime.Day, now.UtcDateTime.Hour, now.UtcDateTime.Minute, now.UtcDateTime.Second, TimeSpan.Zero)
        journal configuration |> Result.bind (fun (store, journal) ->
            FileLocalIntegrationJournal.acquire root store configuration.RepositoryIdentity |> Result.bind (fun lease ->
                use _ = lease
                reconcile configuration journal at |> Result.bind (fun () ->
                    preview configuration admitted targetRef |> Result.bind (fun preview ->
                        let id = constructionId preview
                        let built = ConstructionAdmission.construction admitted
                        match changes configuration built preview.Writes with
                        | [] -> Ok { Preview = preview; ConstructionId = id; Outcome = ConstructionOutcome.AlreadyCurrent }
                        | treeChanges ->
                            journal.Load configuration.RepositoryIdentity id |> Result.bind (fun prior ->
                                let preparedAt = prior |> Option.map _.Plan.PreparedAt |> Option.defaultValue at
                                ConstructionTree.stage git root store configuration.Committer preview.TargetCommit treeChanges (message preview) preparedAt
                                |> Result.bind (fun candidate ->
                                    ConstructionTree.verify git root preview.TargetCommit candidate treeChanges |> Result.bind (fun () ->
                                        let manifest = built.Artifacts |> List.tryFind (fun artifact -> artifact.Path.EndsWith "/construction-manifest.json")
                                        let plan : LocalIntegrationPlan =
                                            { SchemaVersion = "praxis.local-integration-plan/1"; RepositoryIdentity = configuration.RepositoryIdentity
                                              DispatchId = id; AttemptId = "1"; PacketDigest = preview.Binding.PlanDigest
                                              SubmissionDigest = manifest |> Option.map _.Digest |> Option.defaultValue preview.Binding.PlanDigest
                                              TargetRef = targetRef; BeforeCommit = preview.TargetCommit; CandidateCommit = candidate; PreparedAt = preparedAt }
                                        journal.Prepare plan |> Result.bind (fun _ ->
                                            ConstructionTree.readRef git root targetRef |> Result.bind (fun head ->
                                                if head = candidate then Ok()
                                                elif head <> preview.TargetCommit then Error "construction target moved after planning; nothing was applied"
                                                else ConstructionTree.compareAndSwap git root targetRef candidate preview.TargetCommit ("praxis construction " + id))
                                            |> Result.bind (fun () -> journal.RecordApplied plan at)
                                            |> Result.map (fun () -> { Preview = preview; ConstructionId = id; Outcome = ConstructionOutcome.Applied candidate })))))))))
