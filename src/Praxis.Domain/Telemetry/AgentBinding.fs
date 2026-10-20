namespace Praxis.Domain.Telemetry

open System

/// One entry of `praxis.agent-binding/1` (PRAXIS-REL-010, interface I7): a
/// provider agent instance bound to the scope (trial or execution) and the
/// work items it worked on. The operator or orchestrator writes it from what
/// the runtime reported (for Claude Code, the Agent tool result's `agentId`).
type AgentBindingEntry =
    { AgentId: string
      /// The provider session the agent ran in; `None` binds the agent ID in
      /// any session.
      SessionId: string option
      Scope: string option
      WorkItems: string list }

type AgentBinding =
    { Bindings: AgentBindingEntry list
      /// SHA-256 of the binding file, for the report's inputs.
      Digest: string option }

/// Who an observation belongs to under agent attribution.
[<RequireQualifiedAccess>]
type AgentTarget =
    /// A bound agent instance.
    | Agent of string
    /// The main thread of a provider session (Claude Code `isSidechain:
    /// false` without `agentId`): the orchestrator, never an agent.
    | Orchestrator of string

[<RequireQualifiedAccess>]
type AgentResolution =
    | Attributed of target: AgentTarget * method: string
    | Unattributed of reason: string

/// I7 attribution order. Pure: the caller supplies, for an observation, the
/// agent instance IDs of the executions it belongs to (`None` when it
/// belongs to no execution).
[<RequireQualifiedAccess>]
module AgentAttribution =
    let schema = "praxis.agent-binding/1"

    let rules =
        [ "A1 agent ID: a transcript line whose agentId matches a binding (and the binding's sessionId, when stated) belongs to that agent. A line with an agentId that no binding names is unattributed (agent-not-bound): its identity is known, so no later rule may reassign it."
          "A2 main thread: a Claude Code line with isSidechain false and no agentId is the session's orchestrator; reported as orchestrator usage, never as an agent and never as a requirement's cost."
          "A3 execution agent instance: otherwise, when every execution the observation belongs to (its ledger's execution, else the R3 session-window candidates) records the same provider.agentInstanceId and a binding names it, the observation belongs to that agent."
          "A4 single candidate: otherwise, when exactly one bound agent ran in the observation's session, the observation belongs to it. No candidate or several leave it unattributed (no-agent-for-session, ambiguous-agents); unattributed usage is reported, never split."
          "A5 identity and conservation: every observation is deduplicated once across all files (provider message ID, else request ID; per-bucket maximum over streamed entries) before attribution, so agents + orchestrators + unattributed = the global deduplicated total."
          "A6 agent to requirements: an agent's usage is split evenly over its bound work items, and a work item's share evenly over the requirement IDs of its work.completion.verified events (R4-R6 integer split); a work item without verified requirements keeps its share at work-item level." ]

    let private bindingMatches (observation: UsageObservation) (agentId: string) (binding: AgentBindingEntry) =
        binding.AgentId = agentId && binding.SessionId |> Option.forall (fun session -> observation.SessionId = Some session)

    let resolve (binding: AgentBinding) (executionAgents: UsageObservation -> string option list option) (observation: UsageObservation) : AgentResolution =
        let bound agentId = binding.Bindings |> List.exists (bindingMatches observation agentId)

        match observation.AgentId with
        | Some agentId when bound agentId -> AgentResolution.Attributed(AgentTarget.Agent agentId, "agent-id")
        | Some _ -> AgentResolution.Unattributed "agent-not-bound"
        | None ->
            match observation.Sidechain, observation.SessionId with
            | Some false, Some session -> AgentResolution.Attributed(AgentTarget.Orchestrator session, "main-thread")
            | _ ->
                let viaExecution =
                    match executionAgents observation with
                    | Some((_ :: _) as agents) when agents |> List.forall Option.isSome ->
                        match agents |> List.choose id |> List.distinct with
                        | [ agentId ] when bound agentId -> Some agentId
                        | _ -> None
                    | _ -> None

                match viaExecution with
                | Some agentId -> AgentResolution.Attributed(AgentTarget.Agent agentId, "execution-agent-instance")
                | None ->
                    match observation.SessionId with
                    | None -> AgentResolution.Unattributed "no-session-id"
                    | Some session ->
                        match binding.Bindings |> List.filter (fun entry -> entry.SessionId = Some session) |> List.map _.AgentId |> List.distinct with
                        | [ single ] -> AgentResolution.Attributed(AgentTarget.Agent single, "session-single-agent")
                        | [] -> AgentResolution.Unattributed "no-agent-for-session"
                        | _ -> AgentResolution.Unattributed "ambiguous-agents"

    /// The work items an agent is bound to (all its bindings), ordinal.
    let workItems (binding: AgentBinding) (agentId: string) =
        binding.Bindings
        |> List.filter (fun entry -> entry.AgentId = agentId)
        |> List.collect _.WorkItems
        |> List.distinct
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
