namespace Praxis.Domain.Work

open System
open Praxis.Domain.Planning
open Praxis.Domain.Provenance

/// `plan execute-group` (PRX-GRP-117, 130..132, 136, 137): the only `plan`
/// verb that mutates, and only Praxis state, only through existing
/// transitions. These decisions are pure; the edge begins the member through
/// the existing `work begin` and records the group execution. Praxis never
/// launches an agent, creates a branch, selects a provider or model, or
/// changes priorities or dependencies.

type ExecuteGroupRequest =
    { GroupId: string
      OccurredAt: string
      Actor: Actor
      /// `--member ID`: begin this member instead of the next in order.
      Member: string option
      /// `--mode grouped|independent --reason TEXT` (PRX-GRP-130, 132).
      Mode: (ExecutionMode * string) option
      /// `--independent-member ID --reason TEXT` (PRX-GRP-132).
      IndependentMembers: (string * string) list
      /// The identifier a new group execution receives, chosen at the edge.
      NewExecutionId: string
      /// The repository this checkout is, as the planner names it.
      Repository: string }

/// The facts a decision reads, gathered read-only at the edge.
type ExecuteGroupFacts =
    { Group: StoredWorkGroup option
      /// The planner's view of the group, when it formed one.
      Planned: WorkGroup option
      /// Dependency cycles the planner found (PRX-GRP-050).
      Cycles: string list list
      /// Every group execution in this repository's store.
      AllExecutions: GroupExecutionRecord list
      /// The unsatisfied prerequisites of each member (PRX-GRP-105): a
      /// member waiting on one does not begin yet.
      WaitsOn: string -> string list
      /// Executions that took a begun member over (`work continue`), as
      /// (member, successor execution) for this group's open execution.
      Successors: (string * string) list
      /// A context-pressure signal for this group's open execution
      /// (PRX-GRP-136), detected deterministically from recorded data.
      Fallback: GroupFallback option }

[<RequireQualifiedAccess>]
type ExecuteGroupRejection =
    | UnknownGroup of groupId: string
    | DependencyCycle of members: string list
    | NothingRunnableHere of reasons: string list
    | OwnsOtherGroupExecution of executionId: string * groupId: string
    | NotMember of workItemId: string
    | MemberNotRunnable of workItemId: string * reason: string
    | ReasonRequired of what: string

[<RequireQualifiedAccess>]
module ExecuteGroupRejection =
    let code rejection =
        match rejection with
        | ExecuteGroupRejection.UnknownGroup _ -> "unknown-group"
        | ExecuteGroupRejection.DependencyCycle _ -> "dependency-cycle"
        | ExecuteGroupRejection.NothingRunnableHere _ -> "nothing-runnable-here"
        | ExecuteGroupRejection.OwnsOtherGroupExecution _ -> "owns-other-group-execution"
        | ExecuteGroupRejection.NotMember _ -> "not-member"
        | ExecuteGroupRejection.MemberNotRunnable _ -> "member-not-runnable"
        | ExecuteGroupRejection.ReasonRequired _ -> "reason-required"

    let message rejection =
        match rejection with
        | ExecuteGroupRejection.UnknownGroup id -> $"group {id} is not recorded"
        | ExecuteGroupRejection.DependencyCycle members -> $"""members depend on each other in a cycle ({String.concat " -> " members}); resolve it before executing the group"""
        | ExecuteGroupRejection.NothingRunnableHere reasons -> $"""no member is runnable in this checkout ({String.concat "; " reasons}) (PRX-GRP-108)"""
        | ExecuteGroupRejection.OwnsOtherGroupExecution(execution, group) -> $"you already own open group execution {execution} of {group}; finish, block or hand it over first"
        | ExecuteGroupRejection.NotMember id -> $"{id} is not a member of the group"
        | ExecuteGroupRejection.MemberNotRunnable(id, reason) -> $"{id} cannot begin here: {reason}"
        | ExecuteGroupRejection.ReasonRequired what -> $"{what} needs --reason TEXT (PRX-GRP-132)"

    let isArgumentError rejection =
        match rejection with
        | ExecuteGroupRejection.ReasonRequired _ -> true
        | _ -> false

/// What `execute-group` will do.
[<RequireQualifiedAccess>]
type ExecuteGroupPlan =
    /// Begin `member` in `mode` through `work begin`, inside `execution`
    /// (new when `isNew`), then record it.
    | Begin of execution: GroupExecutionRecord * isNew: bool * memberId: string * mode: ExecutionMode
    /// The member was already begun in this group execution (PRX-GRP-114).
    | AlreadyBegun of execution: GroupExecutionRecord * memberId: string
    /// A context-pressure signal stopped grouped mode (PRX-GRP-136): the
    /// execution is ended with the fallback; the rest run independently.
    | FellBack of execution: GroupExecutionRecord * recommendations: string list
    /// Every runnable member is terminal or removed: the execution ends.
    | Finished of execution: GroupExecutionRecord

[<RequireQualifiedAccess>]
module GroupExecutions =
    let isOpen (execution: GroupExecutionRecord) = execution.EndedAt.IsNone

    let ownedBy (actor: Actor) (execution: GroupExecutionRecord) =
        execution.Actor.Kind = actor.Kind && execution.Actor.Id = actor.Id

    /// `GEX-<timestamp>-<suffix>` from a timestamp and a digest suffix.
    let identifier (at: DateTimeOffset) (suffix: string) =
        $"""GEX-{at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'")}-{suffix}"""

    /// PRX-GRP-136: the first context-pressure signal among members begun in
    /// grouped mode, from recorded data only. `metrics` gives a member
    /// execution's `context.compactions` and `context.repeated_file_reads`
    /// (unknown when unrecorded, never zero); `elapsed` its elapsed and upper
    /// estimated milliseconds; `pressure` the explicit context-pressure
    /// observations (members, indicators, reference).
    let fallbackSignal
        (settings: GroupedExecutionConfiguration)
        (execution: GroupExecutionRecord)
        (metrics: string -> int option * int option)
        (elapsed: string -> int64 option * int64 option)
        (pressure: (string list * (string * int) list * string) list)
        (at: string)
        : GroupFallback option =
        let grouped = execution.Members |> List.filter (fun begun -> begun.Mode = ExecutionMode.Grouped)
        let ids = grouped |> List.map (fun begun -> begun.WorkItemId)

        let signals =
            [ for begun in grouped do
                  let compactions, reads = metrics begun.ExecutionId

                  match compactions with
                  | Some count when count >= settings.CompactionLimit ->
                      yield "context.compactions", [ $"{begun.WorkItemId} ({begun.ExecutionId}): context.compactions {count} >= {settings.CompactionLimit}" ]
                  | _ -> ()

                  match reads with
                  | Some count when count > settings.RepeatedReadLimit ->
                      yield "context.repeated_file_reads", [ $"{begun.WorkItemId} ({begun.ExecutionId}): context.repeated_file_reads {count} > {settings.RepeatedReadLimit}" ]
                  | _ -> ()

                  match elapsed begun.WorkItemId with
                  | Some taken, Some upper when decimal taken > settings.ElapsedFactor * decimal upper ->
                      yield "elapsed-over-estimate", [ $"{begun.WorkItemId}: {taken / 60_000L} min elapsed > {settings.ElapsedFactor} x upper estimate {upper / 60_000L} min" ]
                  | _ -> ()
              for observed, indicators, reference in pressure do
                  if observed |> List.exists (fun id -> List.contains id ids) && indicators |> List.sumBy snd > 0 then
                      let listed = indicators |> List.map (fun (name, count) -> $"{name} {count}") |> String.concat ", "
                      let who = String.concat ", " observed
                      yield "context-pressure", [ $"observed for {who}: {listed} ({reference})" ] ]

        match signals with
        | [] -> None
        | (signal, _) :: _ -> Some { Signal = signal; Evidence = signals |> List.collect snd; At = at }

    let private entry operation memberId (request: ExecuteGroupRequest) reason executionId =
        { Operation = operation
          Member = memberId
          At = request.OccurredAt
          Actor = request.Actor
          Reason = reason
          ExplicitEmpty = false
          ExecutionId = executionId
          MemberState = None }

    /// Decide (pure): which member to begin, in which mode, inside which
    /// group execution; or why not.
    let decide (request: ExecuteGroupRequest) (facts: ExecuteGroupFacts) : Result<ExecuteGroupPlan, ExecuteGroupRejection list> =
        let argumentRejections =
            [ match request.Mode with
              | Some(_, reason) when String.IsNullOrWhiteSpace reason -> yield ExecuteGroupRejection.ReasonRequired "--mode"
              | _ -> ()
              for memberId, reason in request.IndependentMembers do
                  if String.IsNullOrWhiteSpace reason then yield ExecuteGroupRejection.ReasonRequired $"--independent-member {memberId}" ]

        match facts.Group, argumentRejections with
        | _, (_ :: _ as rejections) -> Error rejections
        | None, _ -> Error [ ExecuteGroupRejection.UnknownGroup request.GroupId ]
        | Some group, [] ->
            let members = group.Declaration.Members

            let others =
                facts.AllExecutions
                |> List.filter (fun execution -> isOpen execution && ownedBy request.Actor execution && execution.GroupId <> request.GroupId)

            let withSuccessors (execution: GroupExecutionRecord) =
                let fresh = facts.Successors |> List.filter (fun successor -> not (List.contains successor execution.Successors))
                { execution with Successors = execution.Successors @ fresh }

            let mine =
                group.Executions
                |> List.filter (fun execution -> isOpen execution && ownedBy request.Actor execution)
                |> List.tryLast
                |> Option.map withSuccessors

            let cycles = facts.Cycles |> List.filter (fun cycle -> cycle |> List.exists (fun id -> List.contains id members))

            let plannedMembers =
                facts.Planned |> Option.map (fun planned -> planned.Members) |> Option.defaultValue []

            let order =
                facts.Planned
                |> Option.map (fun planned -> planned.RequiredSequence)
                |> Option.defaultValue members
                |> List.filter (fun id -> List.contains id members)

            /// Why a member cannot begin in this checkout, if it cannot.
            let blocker (id: string) =
                if MemberReference.isQualified id then
                    let elsewhere = MemberReference.repository group.HomeRepository (MemberReference.parse id) |> Option.defaultValue "another repository"
                    Some $"it executes in {elsewhere}; begin it there"
                else
                    match plannedMembers |> List.tryFind (fun row -> row.WorkItemId = id) with
                    | None -> Some "the planner does not see it in this checkout"
                    | Some row when row.Status = MemberStatus.Runnable ->
                        match facts.WaitsOn id with
                        | [] -> None
                        | prerequisites -> Some $"""it waits on {String.concat ", " prerequisites}"""
                    | Some row -> Some $"it is {PlanningWorkState.code row.PlanningState} ({MemberStatus.code row.Status})"

            let begunIn (execution: GroupExecutionRecord option) (id: string) =
                execution |> Option.exists (fun execution -> execution.Members |> List.exists (fun begun -> begun.WorkItemId = id))

            match others, cycles, mine, facts.Fallback with
            | other :: _, _, _, _ -> Error [ ExecuteGroupRejection.OwnsOtherGroupExecution(other.Id, other.GroupId) ]
            | [], cycle :: _, _, _ -> Error [ ExecuteGroupRejection.DependencyCycle cycle ]
            | [], [], Some openExecution, Some fallback when openExecution.Mode = ExecutionMode.Grouped && openExecution.Fallback.IsNone ->
                let remaining = order |> List.filter (fun id -> not (begunIn (Some openExecution) id) && (blocker id).IsNone)

                Ok(
                    ExecuteGroupPlan.FellBack(
                        { openExecution with Fallback = Some fallback; EndedAt = Some request.OccurredAt },
                        remaining |> List.map (fun id -> $"{id}: begin it in a fresh, independent execution (work begin --id {id}), or split the group (PRX-GRP-074)")
                    )
                )
            | [], [], _, _ ->
                match request.Member with
                | Some id when not (List.contains (MemberReference.normalize group.HomeRepository id) members) -> Error [ ExecuteGroupRejection.NotMember id ]
                | Some id when begunIn mine id -> Ok(ExecuteGroupPlan.AlreadyBegun(mine.Value, id))
                | Some id when (blocker id).IsSome -> Error [ ExecuteGroupRejection.MemberNotRunnable(id, (blocker id).Value) ]
                | requested ->
                    let next = requested |> Option.orElse (order |> List.tryFind (fun id -> not (begunIn mine id) && (blocker id).IsNone))

                    match next, mine with
                    | None, Some openExecution ->
                        Ok(ExecuteGroupPlan.Finished { openExecution with EndedAt = Some request.OccurredAt })
                    | None, None ->
                        let why id = blocker id |> Option.defaultValue "already begun"
                        Error [ ExecuteGroupRejection.NothingRunnableHere(members |> List.map (fun id -> $"{id}: {why id}")) ]
                    | Some memberId, _ ->
                        let previous = group.Executions |> List.tryLast
                        let qualification = facts.Planned |> Option.map (fun planned -> planned.GroupedExecution)

                        let mode, basis =
                            match request.Mode, previous, group.Declaration.IndependentReason, qualification with
                            | Some(mode, reason), _, _, _ -> mode, [ $"requested {ExecutionMode.code mode}: {reason}" ]
                            | None, Some prior, _, _ when prior.Fallback.IsSome ->
                                ExecutionMode.Independent, [ $"fallback in {prior.Id} ({prior.Fallback.Value.Signal}); remaining members run independently (PRX-GRP-136)" ]
                            | None, _, Some reason, _ -> ExecutionMode.Independent, [ $"group opted out: {reason} (PRX-GRP-132)" ]
                            | None, _, None, Some qualified when qualified.ExecuteGroupDefault = "grouped" -> ExecutionMode.Grouped, [ qualified.Statement ]
                            | None, _, None, Some qualified -> ExecutionMode.Independent, [ qualified.Statement ]
                            | None, _, None, None -> ExecutionMode.Independent, [ "the planner formed no group from these members; independent by default" ]

                        let declaredIndependent = group.Declaration.IndependentMembers @ request.IndependentMembers

                        let execution, isNew =
                            match mine with
                            | Some openExecution -> openExecution, false
                            | None ->
                                { Id = request.NewExecutionId
                                  GroupId = group.Declaration.Id
                                  Actor = request.Actor
                                  StartedAt = request.OccurredAt
                                  Repository = request.Repository
                                  Order = order
                                  Mode = mode
                                  Basis = basis
                                  OptOuts = []
                                  Members = []
                                  Fallback = None
                                  EndedAt = None
                                  Successors = [] },
                                true

                        let newOptOuts =
                            request.IndependentMembers
                            |> List.filter (fun (id, _) -> not (execution.OptOuts |> List.exists (fun optOut -> optOut.WorkItemId = id)))
                            |> List.map (fun (id, reason) -> { WorkItemId = id; Reason = reason; Actor = request.Actor; At = request.OccurredAt })

                        let memberMode =
                            if declaredIndependent |> List.exists (fun (id, _) -> id = memberId) then ExecutionMode.Independent else execution.Mode

                        Ok(ExecuteGroupPlan.Begin({ execution with OptOuts = execution.OptOuts @ newOptOuts }, isNew, memberId, memberMode))

    /// Record (pure) the outcome in the group: a begun member linked to its
    /// own execution, an ended or fallen-back execution, opt-outs and the
    /// history entries that audit them (PRX-GRP-113, 132).
    let record (request: ExecuteGroupRequest) (group: StoredWorkGroup) (plan: ExecuteGroupPlan) (memberExecution: string option) : StoredWorkGroup =
        let replace (execution: GroupExecutionRecord) =
            if group.Executions |> List.exists (fun existing -> existing.Id = execution.Id) then
                group.Executions |> List.map (fun existing -> if existing.Id = execution.Id then execution else existing)
            else
                group.Executions @ [ execution ]

        match plan with
        | ExecuteGroupPlan.AlreadyBegun _ -> group
        | ExecuteGroupPlan.Finished execution -> { group with Executions = replace execution }
        | ExecuteGroupPlan.FellBack(execution, _) ->
            let fallback = execution.Fallback.Value

            { group with
                Executions = replace execution
                History = group.History @ [ entry GroupOperation.FellBack None request (Some $"{execution.Id}: {fallback.Signal}") None ] }
        | ExecuteGroupPlan.Begin(execution, isNew, memberId, mode) ->
            let begun =
                memberExecution
                |> Option.map (fun executionId -> { WorkItemId = memberId; ExecutionId = executionId; BegunAt = request.OccurredAt; Mode = mode })
                |> Option.toList

            let started =
                if isNew then
                    [ entry GroupOperation.ExecutionStarted None request (Some $"""{execution.Id} {ExecutionMode.code execution.Mode}: {String.concat "; " execution.Basis}""") None ]
                else
                    []

            let optOuts =
                execution.OptOuts
                |> List.filter (fun optOut -> optOut.At = request.OccurredAt && optOut.Actor = request.Actor)
                |> List.filter (fun optOut -> not (group.History |> List.exists (fun existing -> existing.Operation = GroupOperation.OptedOut && existing.Member = Some optOut.WorkItemId && existing.At = optOut.At)))
                |> List.map (fun optOut -> entry GroupOperation.OptedOut (Some optOut.WorkItemId) request (Some optOut.Reason) None)

            let groupOptOut =
                match request.Mode with
                | Some(ExecutionMode.Independent, reason) when isNew -> [ entry GroupOperation.OptedOut None request (Some reason) None ]
                | _ -> []

            let beganEntry =
                begun |> List.map (fun begunMember -> entry GroupOperation.MemberBegun (Some memberId) request (Some $"{execution.Id} {ExecutionMode.code mode}") (Some begunMember.ExecutionId))

            { group with
                Executions = replace { execution with Members = execution.Members @ begun }
                History = group.History @ started @ groupOptOut @ optOuts @ beganEntry }
