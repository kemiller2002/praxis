namespace Ros.Domain.Provenance

open System

/// What an actor did to a record. Authorship (`Created`) is recorded once;
/// every later contribution is appended, never substituted, so the last
/// modifier is never mistaken for the original author.
[<RequireQualifiedAccess>]
type Operation =
    | Created
    | Modified
    | Reviewed
    | Approved

[<RequireQualifiedAccess>]
module Operation =
    let code operation =
        match operation with
        | Operation.Created -> "created"
        | Operation.Modified -> "modified"
        | Operation.Reviewed -> "reviewed"
        | Operation.Approved -> "approved"

    let tryParse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "created" -> Some Operation.Created
        | "modified" -> Some Operation.Modified
        | "reviewed" -> Some Operation.Reviewed
        | "approved" -> Some Operation.Approved
        | _ -> None

    let all = [ Operation.Created; Operation.Modified; Operation.Reviewed; Operation.Approved ]

/// One attributable contribution to a record (`praxis.contribution/1`).
/// `Basis` names the prior state the contribution operated on (a version,
/// Git blob/commit, or record revision) when the contributor knows it;
/// `Evidence` holds record references (`EV-...`, paths, URLs) supporting it.
type Contribution =
    { Operation: Operation
      At: string
      Actor: Actor
      WorkItem: string option
      Reason: string option
      Evidence: string list
      Basis: string option }

/// The accumulated provenance of one record: an append-only, ordered list
/// of contributions. Lineage (what a record was derived from) is kept
/// separately, in the record's existing `derived_from` references, so
/// authorship and derivation are never conflated.
type Provenance = { Contributions: Contribution list }

/// Which actors were involved and how -- the answer to "agent-created and
/// human-approved?" style questions without re-deriving it from strings.
type Involvement =
    { CreatorKind: ActorKind option
      ModifiedByAgent: bool
      ModifiedByHuman: bool
      ModifiedByAutomation: bool
      HumanApproved: bool
      HumanCorrectedAgentWork: bool
      AgentRevisedOtherAgentWork: bool }

[<RequireQualifiedAccess>]
module Provenance =
    [<Literal>]
    let SchemaVersion = "praxis.provenance/1"

    let empty = { Contributions = [] }

    let private sameContribution (left: Contribution) (right: Contribution) =
        left.Operation = right.Operation
        && left.At = right.At
        && Actor.stableKey left.Actor = Actor.stableKey right.Actor
        && left.Actor.ExecutionId = right.Actor.ExecutionId

    /// Appends a contribution, preserving every prior contribution. Appending
    /// the same contribution twice (same operation, time, actor, and
    /// execution -- a retried command) is idempotent.
    let append (contribution: Contribution) (provenance: Provenance) =
        if provenance.Contributions |> List.exists (sameContribution contribution) then
            provenance
        else
            { Contributions = provenance.Contributions @ [ contribution ] }

    /// The accumulation invariant: `after` keeps every contribution of
    /// `before`, in order, as its prefix. A rewrite that drops or alters a
    /// prior contributor violates it.
    let preserves (before: Provenance) (after: Provenance) =
        before.Contributions.Length <= after.Contributions.Length
        && List.forall2 (=) before.Contributions (after.Contributions |> List.take before.Contributions.Length)

    let originalCreator provenance =
        provenance.Contributions
        |> List.tryFind (fun contribution -> contribution.Operation = Operation.Created)

    let lastContribution provenance = provenance.Contributions |> List.tryLast

    /// Distinct stable participants in first-contribution order.
    let contributors provenance =
        provenance.Contributions
        |> List.map (fun contribution -> contribution.Actor)
        |> List.distinctBy Actor.stableKey

    /// Distinct executions that contributed, in first-contribution order.
    let executions provenance =
        provenance.Contributions
        |> List.choose (fun contribution -> Attribute.toOption contribution.Actor.ExecutionId)
        |> List.distinct

    /// Operation-dependent default for a newly observed change: a record
    /// that is new to the repository is `created`; an existing one --
    /// including a legacy record with no provenance at all -- is `modified`,
    /// so a later contributor never claims authorship of prior work.
    let defaultOperation (isNewRecord: bool) (provenance: Provenance) =
        if isNewRecord && provenance.Contributions.IsEmpty then Operation.Created else Operation.Modified

    let involvement provenance =
        let creator = originalCreator provenance
        let creatorKind = creator |> Option.map (fun contribution -> contribution.Actor.Kind)

        let later =
            provenance.Contributions
            |> List.filter (fun contribution -> contribution.Operation <> Operation.Created)

        let modifiers kind =
            later
            |> List.exists (fun contribution -> contribution.Operation = Operation.Modified && contribution.Actor.Kind = kind)

        let agentCreated = creatorKind = Some ActorKind.Agent

        let creatorKey = creator |> Option.map (fun contribution -> Actor.stableKey contribution.Actor)

        { CreatorKind = creatorKind
          ModifiedByAgent = modifiers ActorKind.Agent
          ModifiedByHuman = modifiers ActorKind.Human
          ModifiedByAutomation = modifiers ActorKind.Automation
          HumanApproved =
            later
            |> List.exists (fun contribution -> contribution.Operation = Operation.Approved && contribution.Actor.Kind = ActorKind.Human)
          HumanCorrectedAgentWork = agentCreated && modifiers ActorKind.Human
          AgentRevisedOtherAgentWork =
            agentCreated
            && later
               |> List.exists (fun contribution ->
                   contribution.Operation = Operation.Modified
                   && contribution.Actor.Kind = ActorKind.Agent
                   && Some(Actor.stableKey contribution.Actor) <> creatorKey) }

    /// Short labels such as `agent-created`, `human-approved`,
    /// `agent-modified`; `unattributed` when nothing was recorded and
    /// `creator-unknown` when contributions exist but none is `created`
    /// (the normal state of a legacy record later modified under Praxis).
    let involvementLabels provenance =
        if provenance.Contributions.IsEmpty then
            [ "unattributed" ]
        else
            let facts = involvement provenance

            [ match facts.CreatorKind with
              | Some kind -> yield $"{ActorKind.code kind}-created"
              | None -> yield "creator-unknown"
              if facts.ModifiedByAgent then yield "agent-modified"
              if facts.ModifiedByHuman then yield "human-modified"
              if facts.ModifiedByAutomation then yield "automation-modified"
              if facts.HumanApproved then yield "human-approved"
              if facts.HumanCorrectedAgentWork then yield "human-corrected-agent-work"
              if facts.AgentRevisedOtherAgentWork then yield "agent-to-agent-revision" ]

[<RequireQualifiedAccess>]
module Timestamp =
    /// Accepts an ISO-8601 date or UTC date-time, the two forms Praxis
    /// records carry (`created: 2026-09-25`, `occurredAt: ...Z`).
    let tryParse (value: string) =
        match
            DateTimeOffset.TryParse(
                value,
                Globalization.CultureInfo.InvariantCulture,
                Globalization.DateTimeStyles.AssumeUniversal ||| Globalization.DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed -> Some parsed
        | _ -> None
