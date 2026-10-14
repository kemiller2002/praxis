namespace Praxis.Infrastructure.Work

open System.IO
open System.Text.Json.Nodes
open Praxis.Contracts.Work
open Praxis.Domain.Provenance
open Praxis.Domain.Work

type NativeCompletionEffectRequest =
    { ExpectedContext: WorkContextPlanningView
      Plan: WorkContextPlan
      EventActor: Actor
      ReadinessRecords: Map<string, JsonObject>
      ExplicitConclusion: string option
      /// A protected local host must recheck integration/authority here. The
      /// ordinary CLI has already passed its native planner and guards.
      BeforeEffects: unit -> Result<unit, string>
      /// Finalization can take time. Recheck candidate/authority immediately
      /// before persisting completion, without requiring finalized executions
      /// to become active again.
      BeforeApply: unit -> Result<unit, string> }

type NativeCompletionEffectPorts =
    { ReadContext: unit -> Result<WorkContextPlanningView, string>
      FinalizeMember: string -> Result<unit, string>
      Apply: Map<string, string> -> NativeCompletionEffectRequest -> Result<JsonArray * string list, string>
      SyncBacklog: string -> string -> LiveWorkItem list -> Result<unit, string> }

/// Shared native effect tail, used by the real work-complete CLI. Call only
/// under the existing work-protocol lock after recovery, evidence/quality,
/// checkpoint checks and telemetry resolution. Not an authorization endpoint.
[<RequireQualifiedAccess>]
module FileNativeCompletionEffects =
    let private readContext root =
        let path = Path.Combine(root, ".ros", "context", "current.json")
        if File.Exists path then WorkContextPlanContract.parseJson (File.ReadAllText path)
        else Ok { WorkItems = []; StartedAt = None; BaselineDirtyPaths = [] }

    let create root : NativeCompletionEffectPorts =
        { ReadContext = fun () -> readContext root
          FinalizeMember = fun id ->
              if FileWorkConfigRepository.readTelemetryEnabled root then
                  FileTelemetryFinalizationRepository.finalizeWorkExecutions root id
              else Ok()
          Apply = fun conclusions request ->
              FileWorkContextRepository.applyContextPlanWithRecords root request.Plan.Repository conclusions
                  request.ReadinessRecords request.ReadinessRecords request.EventActor request.Plan
          SyncBacklog = FileBacklogQueueRepository.markComplete root }

    /// Reconcile the derived backlog after native event/context recovery.
    /// Uses only already-complete native items and their original timestamp;
    /// never repeats a transition or invents completion for active work.
    let recoverBacklogLockedWith (ports: NativeCompletionEffectPorts) =
        ports.ReadContext() |> Result.bind (fun context ->
            context.WorkItems
            |> List.filter (fun item -> item.SemanticState = LiveWorkState.Complete && item.CompletedAt.IsSome)
            |> List.fold (fun acc item ->
                acc |> Result.bind (fun () -> ports.SyncBacklog item.Id item.CompletedAt.Value context.WorkItems)) (Ok()))

    let recoverBacklogLocked root = recoverBacklogLockedWith (create root)

    let private valid (request: NativeCompletionEffectRequest) =
        let plan = request.Plan
        let ids = plan.ItemPlans |> List.map (fun item -> item.Item.Id)
        not ids.IsEmpty && ids.Length = (Set.ofList ids).Count
        && plan.ItemPlans |> List.forall (fun item ->
            item.Item.SemanticState = LiveWorkState.Complete && item.Event.EventType = "work.completed"
            && item.Item.Id = item.Event.WorkItemId && item.Event.Repository = plan.Repository
            && item.Event.OccurredAt = plan.UpdatedAt && item.Item.CompletedAt = Some plan.UpdatedAt
            && item.Event.Evidence = item.Item.Evidence
            && item.Event.TelemetryExecutionIds = item.Item.TelemetryExecutionIds
            && request.ExpectedContext.WorkItems |> List.exists (fun previous ->
                previous.Id = item.Item.Id && previous.SemanticState = LiveWorkState.Active)
            && plan.WorkItems |> List.filter (fun current -> current.Id = item.Item.Id) = [ item.Item ])
        && plan.WorkItems.Length = request.ExpectedContext.WorkItems.Length
        && (plan.WorkItems |> List.map _.Id |> Set.ofList) = (request.ExpectedContext.WorkItems |> List.map _.Id |> Set.ofList)
        && plan.WorkItems |> List.forall (fun current ->
            List.contains current.Id ids || List.contains current request.ExpectedContext.WorkItems)

    let private effects (ports: NativeCompletionEffectPorts) (request: NativeCompletionEffectRequest) =
        let ids = request.Plan.ItemPlans |> List.map (fun item -> item.Item.Id)
        ids |> List.fold (fun acc id -> acc |> Result.bind (fun () -> ports.FinalizeMember id)) (Ok())
        |> Result.bind (fun () -> request.BeforeApply())
        |> Result.bind (fun () -> ports.ReadContext() |> Result.bind (fun current ->
            if current <> request.ExpectedContext then Error "native context changed during completion finalization"
            else Ok()))
        |> Result.bind (fun () ->
            let conclusions =
                request.Plan.ItemPlans |> List.choose (fun item ->
                    match item.Item.WorkType, request.ExplicitConclusion with
                    | "research", value -> Some(item.Item.Id, Option.defaultValue "inconclusive" value)
                    | _, Some value -> Some(item.Item.Id, value)
                    | _ -> None) |> Map.ofList
            ports.Apply conclusions request |> Result.bind (fun written ->
                ports.ReadContext() |> Result.bind (fun updated ->
                    ids |> List.fold (fun acc id ->
                        acc |> Result.bind (fun () -> ports.SyncBacklog id request.Plan.UpdatedAt updated.WorkItems)) (Ok())
                    |> Result.map (fun () -> written))))

    /// The injected ports exercise failures around actual native persistence.
    /// No effect is attempted until the whole plan, context and boundary guard
    /// pass. Mid-effect failures remain errors and require native recovery.
    let commitLockedWith (ports: NativeCompletionEffectPorts) (request: NativeCompletionEffectRequest) =
        let unchanged () = ports.ReadContext() |> Result.bind (fun current ->
            if current = request.ExpectedContext then Ok()
            else Error "native context changed before completion effects")
        if not (valid request) then Error "invalid native completion effect plan"
        else unchanged() |> Result.bind request.BeforeEffects
             |> Result.bind unchanged |> Result.bind (fun () -> effects ports request)

    let commitLocked root request = commitLockedWith (create root) request
