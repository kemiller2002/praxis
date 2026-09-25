namespace Ros.Domain.Provenance

open System
open Ros.Domain.Telemetry

/// Inputs that establish who the current process is, on top of the
/// telemetry identity discovery Praxis already performs at `work begin`.
/// Everything here is supplied once per execution (environment or a
/// command-line override); no downstream command asks an agent to repeat it.
type ActorInputs =
    { /// `ROS_ACTOR_KIND`: agent, human, automation, or unknown.
      ExplicitKind: string option
      /// `ROS_EXECUTION_ID`: pins the Praxis execution this process belongs to.
      ExplicitExecutionId: string option }

/// A previously recorded execution a process may be continuing.
type ExecutionCandidate =
    { ExecutionId: string
      /// The work item the execution was started for.
      WorkItemId: string option
      Status: string
      Identity: Identity
      /// The actor kind the execution record declares, when it has one.
      ActorKind: ActorKind option }

[<RequireQualifiedAccess>]
type ExecutionBinding =
    /// `ROS_EXECUTION_ID` named the execution explicitly.
    | Explicit of executionId: string
    /// An active execution recorded with a compatible identity.
    | Matched of executionId: string
    /// No compatible execution is recorded; the execution is unknown.
    | Unbound

[<RequireQualifiedAccess>]
module ActorResolution =
    let emptyInputs =
        { ExplicitKind = None
          ExplicitExecutionId = None }

    /// Kind inference is deliberately conservative: only an environment that
    /// unambiguously belongs to an agent runtime (a whitelisted agent session
    /// variable) implies `agent`, and only a CI runner without one implies
    /// `automation`. Anything else stays `unknown` unless declared.
    let inferKind (inputs: ActorInputs) (source: IdentitySource) =
        match inputs.ExplicitKind |> Option.bind ActorKind.tryParse with
        | Some kind -> kind
        | None ->
            match source.Mechanism with
            | "whitelisted-codex-environment"
            | "whitelisted-claude-environment"
            | "whitelisted-gemini-environment"
            | "whitelisted-copilot-environment" -> ActorKind.Agent
            | "whitelisted-github-actions-environment" -> ActorKind.Automation
            | _ -> ActorKind.Unknown

    /// The stable id: an explicit `ROS_ACTOR`/agent id when declared;
    /// otherwise, for an agent or automation whose runtime is known, the
    /// runtime's own name (it names the agent system, e.g. `claude-code`,
    /// `codex`); otherwise explicitly unknown. Never inferred from style,
    /// Git authorship, or file contents.
    let stableId (kind: ActorKind) (identity: Identity) =
        match Attribute.ofOption identity.AgentId with
        | Attribute.Known id -> Attribute.Known id
        | Attribute.Unknown ->
            match kind with
            | ActorKind.Agent
            | ActorKind.Automation -> Attribute.ofString identity.Runtime
            | ActorKind.Human
            | ActorKind.Unknown -> Attribute.Unknown

    let private agreeWhenBothKnown (left: string option) (right: string option) =
        match Attribute.ofOption left, Attribute.ofOption right with
        | Attribute.Known a, Attribute.Known b -> String.Equals(a, b, StringComparison.Ordinal)
        | _ -> true

    /// An execution record is compatible with the current process when every
    /// attribute both sides know agrees: provider, runtime, session, and
    /// agent id. A different agent (or a different session of the same
    /// agent) therefore never inherits another execution's identity.
    let compatible (current: Identity) (candidate: Identity) =
        agreeWhenBothKnown (Some current.Provider) (Some candidate.Provider)
        && agreeWhenBothKnown (Some current.Runtime) (Some candidate.Runtime)
        && agreeWhenBothKnown current.SessionId candidate.SessionId
        && agreeWhenBothKnown current.AgentId candidate.AgentId
        && (Attribute.isKnown (Attribute.ofString current.Provider) || Attribute.isKnown (Attribute.ofString current.Runtime) || current.SessionId.IsSome || current.AgentId.IsSome)

    /// Binds the current process to an execution: an explicit id wins;
    /// otherwise the most recently started compatible *active* execution
    /// (execution ids embed their start time, so ordinal order is start
    /// order); otherwise unbound.
    let bindExecution (inputs: ActorInputs) (current: Identity) (candidates: ExecutionCandidate list) =
        match inputs.ExplicitExecutionId |> Option.filter (fun id -> id.Trim().Length > 0) with
        | Some explicitId -> ExecutionBinding.Explicit(explicitId.Trim())
        | None ->
            candidates
            |> List.filter (fun candidate -> candidate.Status = "active" && compatible current candidate.Identity)
            |> List.sortWith (fun left right -> String.CompareOrdinal(right.ExecutionId, left.ExecutionId))
            |> List.tryHead
            |> Option.map (fun candidate -> ExecutionBinding.Matched candidate.ExecutionId)
            |> Option.defaultValue ExecutionBinding.Unbound

    let executionAttribute binding =
        match binding with
        | ExecutionBinding.Explicit id
        | ExecutionBinding.Matched id -> Attribute.Known id
        | ExecutionBinding.Unbound -> Attribute.Unknown

    /// The canonical actor for a discovered identity bound to an execution.
    let resolve (inputs: ActorInputs) (identity: Identity, source: IdentitySource) (binding: ExecutionBinding) : Actor =
        let kind = inferKind inputs source

        { Kind = kind
          Id = stableId kind identity
          Provider = Attribute.ofString identity.Provider
          Model = Attribute.ofOption identity.Model
          ModelVersion = identity.ModelVersion |> Option.filter (fun value -> value.Trim().Length > 0)
          Runtime = Attribute.ofString identity.Runtime
          RuntimeVersion = identity.RuntimeVersion |> Option.filter (fun value -> value.Trim().Length > 0)
          ExecutionId = executionAttribute binding
          SessionId = identity.SessionId |> Option.filter (fun value -> value.Trim().Length > 0)
          Assurance = Assurance.SelfReported }

    /// The actor an execution record describes, for records created before
    /// the current process (a handoff reads the originating execution).
    let ofExecution (candidate: ExecutionCandidate) : Actor =
        let kind = candidate.ActorKind |> Option.defaultValue ActorKind.Unknown

        { Kind = kind
          Id = stableId kind candidate.Identity
          Provider = Attribute.ofString candidate.Identity.Provider
          Model = Attribute.ofOption candidate.Identity.Model
          ModelVersion = candidate.Identity.ModelVersion
          Runtime = Attribute.ofString candidate.Identity.Runtime
          RuntimeVersion = candidate.Identity.RuntimeVersion
          ExecutionId = Attribute.Known candidate.ExecutionId
          SessionId = candidate.Identity.SessionId
          Assurance = Assurance.SelfReported }
