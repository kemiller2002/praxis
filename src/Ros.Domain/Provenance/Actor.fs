namespace Ros.Domain.Provenance

open System.Text.RegularExpressions
open Ros.Domain.Telemetry

/// Who (or what) performed an action. `Agent` is an autonomous or
/// semi-autonomous AI system; `Automation` is a deterministic non-agent
/// process (CI, a scheduled job, the ROS installer); `Unknown` is recorded
/// honestly rather than guessed. Namespaced `x-...` extensions let a
/// consuming ecosystem add a category without changing this vocabulary.
[<RequireQualifiedAccess>]
type ActorKind =
    | Agent
    | Human
    | Automation
    | Unknown
    | Extension of string

[<RequireQualifiedAccess>]
module ActorKind =
    let private extensionPattern =
        Regex("^x-[a-z0-9][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    let code kind =
        match kind with
        | ActorKind.Agent -> "agent"
        | ActorKind.Human -> "human"
        | ActorKind.Automation -> "automation"
        | ActorKind.Unknown -> "unknown"
        | ActorKind.Extension value -> value

    let tryParse (value: string) =
        match value with
        | "agent" -> Some ActorKind.Agent
        | "human" -> Some ActorKind.Human
        | "automation" -> Some ActorKind.Automation
        | "unknown" -> Some ActorKind.Unknown
        | extension when extensionPattern.IsMatch extension -> Some(ActorKind.Extension extension)
        | _ -> None

    /// Maps the identity-discovery mechanism `Identity.discover` records to
    /// the kind of actor that mechanism reliably implies. Only mechanisms
    /// that identify a specific agent runtime or CI automation imply a
    /// kind; an explicit, unmapped, or merely "a local model server exists"
    /// environment implies nothing, so it resolves to `Unknown` instead of
    /// a guess.
    let fromDiscoveryMechanism (mechanism: string) =
        match mechanism with
        | "whitelisted-codex-environment"
        | "whitelisted-claude-environment"
        | "whitelisted-gemini-environment"
        | "whitelisted-copilot-environment" -> ActorKind.Agent
        | "whitelisted-github-actions-environment" -> ActorKind.Automation
        | _ -> ActorKind.Unknown

    /// An explicit declaration (`--actor-kind` / `ROS_ACTOR_KIND`) always
    /// wins; otherwise the discovery mechanism decides. An explicit value
    /// outside the vocabulary is rejected rather than silently coerced.
    let resolve (explicitKind: string option) (mechanism: string) : Result<ActorKind, string> =
        match explicitKind with
        | Some value ->
            match tryParse value with
            | Some kind -> Ok kind
            | None -> Error $"unknown actor kind '{value}'; expected agent, human, automation, unknown, or x-<extension>"
        | None -> Ok(fromDiscoveryMechanism mechanism)

/// The portable, denormalized "who" carried by events, backlog items, and
/// artifact contributions so that it survives an integration boundary
/// without the originating execution record. `Provider`/`Model`/`Runtime`
/// are `None` when not applicable (a human) and `Some "unknown"` when
/// applicable but not known -- the two are never conflated.
type Actor =
    { Kind: ActorKind
      Id: string
      Provider: string option
      Model: string option
      Runtime: string option }

[<RequireQualifiedAccess>]
module Actor =
    [<Literal>]
    let UnknownValue = "unknown"

    let private known (value: string) =
        value.Trim().Length > 0 && value.Trim() <> UnknownValue

    let unknown =
        { Kind = ActorKind.Unknown
          Id = UnknownValue
          Provider = Some UnknownValue
          Model = Some UnknownValue
          Runtime = Some UnknownValue }

    /// The stable identity of the actor, deliberately separate from any one
    /// execution. An explicit agent/actor identifier wins. Otherwise a
    /// non-human actor whose provider and runtime are both known is
    /// identified by `provider/runtime` (a deterministic function of
    /// recorded facts, not a guess). Anything else is `unknown`.
    let stableId (kind: ActorKind) (identity: Identity) =
        match identity.AgentId with
        | Some agentId when agentId.Trim().Length > 0 -> agentId.Trim()
        | _ ->
            match kind with
            | ActorKind.Human -> UnknownValue
            | _ when known identity.Provider && known identity.Runtime -> $"{identity.Provider}/{identity.Runtime}"
            | _ -> UnknownValue

    /// Projects a discovered execution identity onto the portable actor.
    let fromIdentity (kind: ActorKind) (identity: Identity) : Actor =
        let orUnknown (value: string) =
            if value.Trim().Length = 0 then UnknownValue else value

        match kind with
        | ActorKind.Human ->
            { Kind = kind
              Id = stableId kind identity
              Provider = None
              Model = None
              Runtime = None }
        | _ ->
            { Kind = kind
              Id = stableId kind identity
              Provider = Some(orUnknown identity.Provider)
              Model = Some(identity.Model |> Option.map orUnknown |> Option.defaultValue UnknownValue)
              Runtime = Some(orUnknown identity.Runtime) }

    /// Structural problems, each as `(field, message)`. An agent must state
    /// provider, model, and runtime -- possibly as `unknown` -- so absence
    /// can never be mistaken for "not applicable".
    let problems (actor: Actor) : (string * string) list =
        [ if actor.Id.Trim().Length = 0 then
              "id", "actor id must not be empty; use 'unknown' when it is not known"
          match actor.Kind with
          | ActorKind.Extension value when ActorKind.tryParse value <> Some actor.Kind ->
              "kind", $"invalid actor kind '{value}'"
          | ActorKind.Agent ->
              for field, value in [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ] do
                  match value with
                  | None -> field, $"agent actor must record {field} (use 'unknown' when it is not known)"
                  | Some text when text.Trim().Length = 0 -> field, $"agent actor {field} must not be empty"
                  | Some _ -> ()
          | _ -> () ]

    /// Two actor records describe the same actor when kind, stable id, and
    /// every applicable, known attribute agree. An `unknown` attribute on
    /// either side is not a contradiction.
    let agrees (left: Actor) (right: Actor) =
        let compatible (a: string option) (b: string option) =
            match a, b with
            | Some x, Some y when known x && known y -> x = y
            | _ -> true

        left.Kind = right.Kind
        && (left.Id = right.Id || not (known left.Id) || not (known right.Id))
        && compatible left.Provider right.Provider
        && compatible left.Model right.Model
        && compatible left.Runtime right.Runtime

    let describe (actor: Actor) =
        let detail =
            [ actor.Provider; actor.Model; actor.Runtime ]
            |> List.choose id
            |> function
                | [] -> ""
                | values -> " (" + String.concat ", " values + ")"

        $"{ActorKind.code actor.Kind}:{actor.Id}{detail}"

/// Resolves the acting identity for one process from the same explicit
/// overrides and whitelisted environment `Identity.discover` uses, so an
/// execution record and every event/record written alongside it always
/// agree. Resolution never consults previously stored state (such as the
/// last actor written to work context): inheriting another run's actor
/// would be impersonation.
[<RequireQualifiedAccess>]
module ActorResolution =
    let explicitKind (inputs: IdentityInputs) =
        inputs.ActorKind |> Option.orElse inputs.RosActorKind

    let resolveWith (inputs: IdentityInputs) : Result<Actor * Identity * IdentitySource, string> =
        let identity, source = Identity.discover inputs

        ActorKind.resolve (explicitKind inputs) source.Mechanism
        |> Result.map (fun kind -> Actor.fromIdentity kind identity, identity, source)

    let resolve (inputs: IdentityInputs) : Result<Actor, string> =
        resolveWith inputs |> Result.map (fun (actor, _, _) -> actor)

    /// Whether a process could be the same run as an execution: when both
    /// know a session ID (or a CI run ID) they must be equal. Two sessions of
    /// the same agent are different runs even though their actor agrees.
    let sameRun (current: Identity) (execution: Identity) =
        let compatible (left: string option) (right: string option) =
            match left, right with
            | Some a, Some b -> a = b
            | _ -> true

        compatible current.SessionId execution.SessionId
        && compatible current.ConversationId execution.ConversationId
        && compatible current.RunId execution.RunId

    /// Positive evidence that a process *is* a given execution's run, needed
    /// before identity is inherited implicitly: a run key (session,
    /// conversation, or CI run) known on both sides and equal, or the same
    /// known stable actor id. Agreement alone is not enough -- a process that
    /// declares only `ROS_ACTOR_KIND=agent` agrees with every agent.
    let evidentlySameRun (currentActor: Actor) (current: Identity) (executionActor: Actor) (execution: Identity) =
        let bothKnownAndEqual (left: string option) (right: string option) =
            match left, right with
            | Some a, Some b -> a = b
            | _ -> false

        sameRun current execution
        && (bothKnownAndEqual current.SessionId execution.SessionId
            || bothKnownAndEqual current.ConversationId execution.ConversationId
            || bothKnownAndEqual current.RunId execution.RunId
            || (currentActor.Id <> Actor.UnknownValue && currentActor.Id = executionActor.Id))

    /// Whether this process has any identity of its own. A process with none
    /// (a plain terminal with no identity environment) must not silently
    /// inherit someone else's execution.
    let isDeclared (actor: Actor) =
        actor.Kind <> ActorKind.Unknown || actor.Id <> Actor.UnknownValue

    /// Projects a stored execution record's identity. Records written before
    /// `actorKind` existed fall back to the discovery mechanism the record
    /// itself preserved (`provenance.sources`) -- recorded evidence, not a
    /// guess -- and otherwise to `unknown`.
    let ofExecutionRecord (storedKind: string option) (mechanism: string option) (identity: Identity) : Actor =
        let kind =
            match storedKind |> Option.bind ActorKind.tryParse with
            | Some kind -> kind
            | None -> mechanism |> Option.map ActorKind.fromDiscoveryMechanism |> Option.defaultValue ActorKind.Unknown

        Actor.fromIdentity kind identity
