namespace Praxis.Domain.Planning

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type PlanChangeKind =
    | ItemCompleted
    | ItemBlocked
    | ItemStateChanged
    | WorkAdded
    | WorkRemoved
    | CheckpointRecorded
    | ItemInputsChanged
    | RepositoryChanged
    | CollisionsChanged
    | EvidenceChanged

[<RequireQualifiedAccess>]
module PlanChangeKind =
    let code kind =
        match kind with
        | PlanChangeKind.ItemCompleted -> "item-completed"
        | PlanChangeKind.ItemBlocked -> "item-blocked"
        | PlanChangeKind.ItemStateChanged -> "item-state-changed"
        | PlanChangeKind.WorkAdded -> "work-added"
        | PlanChangeKind.WorkRemoved -> "work-removed"
        | PlanChangeKind.CheckpointRecorded -> "checkpoint-recorded"
        | PlanChangeKind.ItemInputsChanged -> "item-inputs-changed"
        | PlanChangeKind.RepositoryChanged -> "repository-changed"
        | PlanChangeKind.CollisionsChanged -> "collisions-changed"
        | PlanChangeKind.EvidenceChanged -> "evidence-changed"

type PlanChange =
    { Kind: PlanChangeKind
      WorkItem: string option
      Message: string }

type PlanFreshness =
    { Stale: bool
      Changes: PlanChange list }

/// PRX-PLAN-140..142: what a plan was computed against, and whether that
/// still holds. Fingerprints are SHA-256 over canonical text, so they are
/// stable across runs and machines.
[<RequireQualifiedAccess>]
module Snapshot =
    let hash (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private option (value: 'a option) = value |> Option.map string |> Option.defaultValue "-"

    let private estimate (value: Estimate<int64>) =
        $"{option value.Lower}/{option value.Expected}/{option value.Upper}/{EvidenceConfidence.code value.Confidence}"

    let itemCanonical (item: ItemAnalysis) =
        let dependencies =
            item.Dependencies
            |> List.map (fun resolved ->
                $"{DependencyTarget.kindCode resolved.Dependency.Target}:{DependencyTarget.value resolved.Dependency.Target}:{DependencyKind.code resolved.Dependency.Kind}:{DependencyOrigin.code resolved.Dependency.Origin}:{DependencyStatus.code resolved.Status}")
            |> Text.sortOrdinal
            |> String.concat ","

        let checkpointId = item.Checkpoint |> Option.map (fun checkpoint -> checkpoint.CheckpointId)
        let tags = item.Tags |> Text.sortOrdinal |> String.concat ","

        String.concat
            "\n"
            [ item.Id
              item.LifecycleState
              PlanningWorkState.code item.PlanningState
              option checkpointId
              option item.BlockReason
              tags
              dependencies
              item.TaskClass
              RemainingBasis.code item.Basis
              estimate item.RemainingDuration ]

    let digest (item: ItemAnalysis) : ItemDigest =
        { WorkItem = item.Id
          LifecycleState = item.LifecycleState
          PlanningState = item.PlanningState
          CheckpointId = item.Checkpoint |> Option.map (fun checkpoint -> checkpoint.CheckpointId)
          Digest = hash (itemCanonical item) }

    let collisionCanonical (collisions: Collision list) =
        collisions
        |> List.map (fun collision ->
            let signals = collision.Signals |> List.map (fun signal -> $"{CollisionSignal.code signal}={CollisionSignal.detail signal}") |> String.concat ","
            $"{collision.Left}|{collision.Right}|{CollisionRisk.code collision.Risk}|{signals}")
        |> Text.sortOrdinal
        |> String.concat "\n"

    let private observationCanonical (observation: Observation) =
        let kind =
            match observation.Kind with
            | ObservationKind.PullRequestMerged number -> $"pr-merged:{number}"
            | ObservationKind.CommitMerged(commit, into) -> $"commit-merged:{commit}:{into}"
            | ObservationKind.ContinuousIntegrationPassed subject -> $"ci-passed:{subject}"
            | ObservationKind.ContinuousIntegrationFailed subject -> $"ci-failed:{subject}"
            | ObservationKind.ReleaseExists tag -> $"release:{tag}"
            | ObservationKind.ContextPressure(members, indicators) ->
                let names = members |> Text.sortOrdinal |> String.concat ";"
                let counts = indicators |> List.map (fun (name, count) -> $"{name}={count}") |> Text.sortOrdinal |> String.concat ";"
                $"context-pressure:{names}:{counts}"

        $"{kind}@{EvidenceSource.code observation.Provenance.Source}:{observation.Provenance.Reference}"

    /// Grouping settings join the fingerprint only when they differ from the
    /// defaults, so fingerprints of plans saved before grouping existed stay
    /// valid.
    let private groupingCanonical (grouping: GroupingConfiguration) =
        if grouping = GroupingConfiguration.defaults then
            []
        else
            let joined (values: string list) = values |> Text.sortOrdinal |> String.concat ";"

            let groups =
                grouping.Groups
                |> List.map (fun group ->
                    let kind = group.Kind |> Option.map GroupKind.code |> Option.defaultValue ""
                    let repository = group.ExecutionRepository |> Option.defaultValue ""
                    $"{group.Id}={joined group.Members}/{kind}/{GroupOrigin.code group.Origin}/{joined group.SharedContext}/{repository}/{group.CrossRepository}/{joined group.ArchitectureNotes}")
                |> Text.sortOrdinal
                |> String.concat ","

            let architecture =
                grouping.Architecture
                |> List.map (fun decision -> $"{decision.Decision}={joined decision.Members}/{decision.Statement}")
                |> Text.sortOrdinal
                |> String.concat ","

            let repositories = grouping.ExecutionRepositories |> List.map (fun (id, repository) -> $"{id}@{repository}") |> Text.sortOrdinal |> String.concat ","

            [ $"grouping:{grouping.PreferredMinimumSize}-{grouping.PreferredMaximumSize}/{grouping.MaximumAutomaticSize}/{ContextAffinity.code grouping.MinimumAffinity}"
              groups
              architecture
              repositories ]

    let private configurationCanonical (configuration: PlannerConfiguration) =
        let weights = configuration.BalancedWeights
        let fractions = configuration.RemainingFractions
        let pair (low: decimal, high: decimal) = $"{low}-{high}"

        String.concat
            "|"
            [ string configuration.MaxConcurrency
              string configuration.MinimumCostSamples
              string configuration.PraxisStateMergeSafe
              configuration.GenericTags |> Text.sortOrdinal |> String.concat ","
              $"{weights.Duration},{weights.Cost},{weights.ConflictRisk},{weights.Uncertainty},{weights.ContextReuse},{weights.CompletionLikelihood}"
              $"{pair fractions.FinalizationOnly},{pair fractions.VerificationRemaining},{pair fractions.ImplementationInProgress},{pair fractions.Unclassified}"
              configuration.Dependencies |> List.map (fun item -> $"{item.From}>{item.To}:{DependencyKind.code item.Kind}") |> Text.sortOrdinal |> String.concat ","
              configuration.Conflicts |> List.map (fun item -> $"{item.Left}x{item.Right}:{item.Reason}") |> Text.sortOrdinal |> String.concat ","
              configuration.Areas |> List.map (fun (id, paths) -> id + "=" + (paths |> Text.sortOrdinal |> String.concat ";")) |> Text.sortOrdinal |> String.concat ","
              yield! groupingCanonical configuration.Grouping ]

    let evidenceCanonical (input: PlanningInput) =
        let executions =
            History.samples input.Executions
            |> List.map (fun sample -> $"{sample.ExecutionId}:{sample.ProductiveMs}")
            |> Text.sortOrdinal
            |> String.concat ","

        let costs =
            input.Executions
            |> List.choose (fun execution -> History.executionCost execution |> Option.map (fun (amount, _) -> $"{execution.ExecutionId}:{amount}"))
            |> Text.sortOrdinal
            |> String.concat ","

        let observations = input.Observations |> List.map observationCanonical |> Text.sortOrdinal |> String.concat ","

        // Capacity is evidence too; it is appended only when observed, so
        // snapshots of plans without capacity keep their recorded hashes.
        let capacity =
            input.Capacity
            |> List.map (fun provider -> $"{provider.Provider}:{CapacityState.describe provider.State}@{provider.Provenance.Reference}")
            |> Text.sortOrdinal

        String.concat "\n" ([ executions; costs; observations ] @ (if capacity.IsEmpty then [] else [ String.concat "," capacity ]))

    let create (input: PlanningInput) (items: ItemAnalysis list) (collisions: Collision list) : PlanSnapshot =
        let digests = items |> List.map digest
        let workState = digests |> List.map (fun item -> $"{item.WorkItem}:{item.Digest}") |> String.concat "\n" |> hash

        let inputFingerprint =
            String.concat
                "\n--\n"
                [ input.Repository
                  option input.Commit
                  input.PlannerVersion
                  workState
                  evidenceCanonical input
                  configurationCanonical input.Configuration ]
            |> hash

        { Repository = input.Repository
          Commit = input.Commit
          Branch = input.Branch
          PlannedAt = input.PlannedAt
          PlannerVersion = input.PlannerVersion
          WorkStateFingerprint = workState
          InputFingerprint = inputFingerprint
          CollisionFingerprint = hash (collisionCanonical collisions)
          Items = digests }

    /// PRX-PLAN-141: every material difference between the state a plan was
    /// computed against and the current state.
    let compare (previous: PlanSnapshot) (current: PlanSnapshot) : PlanFreshness =
        let before = previous.Items |> List.map (fun item -> item.WorkItem, item) |> Map.ofList
        let after = current.Items |> List.map (fun item -> item.WorkItem, item) |> Map.ofList
        let change kind item message = { Kind = kind; WorkItem = item; Message = message }

        let itemChanges =
            (Map.keys before |> Seq.toList) @ (Map.keys after |> Seq.toList)
            |> Text.distinctOrdinal
            |> List.collect (fun id ->
                match before.TryFind id, after.TryFind id with
                | None, Some now when not (PlanningWorkState.isTerminal now.PlanningState) ->
                    [ change PlanChangeKind.WorkAdded (Some id) $"{id} was added ({PlanningWorkState.code now.PlanningState})" ]
                | None, Some _ -> []
                | Some _, None -> [ change PlanChangeKind.WorkRemoved (Some id) $"{id} is no longer in the inventory" ]
                | None, None -> []
                | Some was, Some now when was.Digest = now.Digest -> []
                | Some was, Some now ->
                    [ if was.LifecycleState <> now.LifecycleState then
                          match now.LifecycleState with
                          | "complete" -> yield change PlanChangeKind.ItemCompleted (Some id) $"{id} completed (was {was.LifecycleState})"
                          | "blocked" -> yield change PlanChangeKind.ItemBlocked (Some id) $"{id} was blocked (was {was.LifecycleState})"
                          | state -> yield change PlanChangeKind.ItemStateChanged (Some id) $"{id} moved from {was.LifecycleState} to {state}"
                      elif was.PlanningState <> now.PlanningState then
                          yield
                              change
                                  PlanChangeKind.ItemStateChanged
                                  (Some id)
                                  $"{id} is now {PlanningWorkState.code now.PlanningState} (was {PlanningWorkState.code was.PlanningState})"
                      if was.CheckpointId <> now.CheckpointId && now.CheckpointId.IsSome then
                          yield change PlanChangeKind.CheckpointRecorded (Some id) $"{id} recorded checkpoint {now.CheckpointId.Value}"
                      if was.LifecycleState = now.LifecycleState && was.PlanningState = now.PlanningState && was.CheckpointId = now.CheckpointId then
                          yield change PlanChangeKind.ItemInputsChanged (Some id) $"{id}'s dependencies, blocker, scope or estimate changed" ])

        let global' =
            [ if previous.Commit <> current.Commit then
                  yield change PlanChangeKind.RepositoryChanged None $"repository moved from {option previous.Commit} to {option current.Commit}"
              if previous.CollisionFingerprint <> current.CollisionFingerprint then
                  yield change PlanChangeKind.CollisionsChanged None "collision relationships changed"
              if previous.InputFingerprint <> current.InputFingerprint
                 && previous.WorkStateFingerprint = current.WorkStateFingerprint
                 && previous.Commit = current.Commit then
                  yield change PlanChangeKind.EvidenceChanged None "telemetry, observations or planner configuration changed" ]

        let changes = itemChanges @ global'
        { Stale = not changes.IsEmpty; Changes = changes }
