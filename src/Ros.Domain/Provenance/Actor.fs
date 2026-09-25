namespace Ros.Domain.Provenance

open System

/// One actor attribute that is either known or explicitly unknown. Praxis
/// never fabricates a value an actor cannot know: an unknown attribute is
/// recorded as the literal token `unknown`, which is distinct from the
/// attribute being absent from a record (a validation concern, not a value).
[<RequireQualifiedAccess>]
type Attribute =
    | Known of string
    | Unknown

[<RequireQualifiedAccess>]
module Attribute =
    [<Literal>]
    let UnknownToken = "unknown"

    let private meaningful (value: string) =
        not (String.IsNullOrWhiteSpace value)
        && not (String.Equals(value.Trim(), UnknownToken, StringComparison.OrdinalIgnoreCase))

    let ofOption (value: string option) =
        match value with
        | Some text when meaningful text -> Attribute.Known(text.Trim())
        | _ -> Attribute.Unknown

    let ofString (value: string) = ofOption (Some value)

    let toOption attribute =
        match attribute with
        | Attribute.Known value -> Some value
        | Attribute.Unknown -> None

    let render attribute =
        match attribute with
        | Attribute.Known value -> value
        | Attribute.Unknown -> UnknownToken

    let isKnown attribute =
        match attribute with
        | Attribute.Known _ -> true
        | Attribute.Unknown -> false

    let orElse (fallback: Attribute) (attribute: Attribute) =
        match attribute with
        | Attribute.Known _ -> attribute
        | Attribute.Unknown -> fallback

/// What kind of participant performed an action. The four kinds are
/// deliberately separate so validation, metrics, and human-agency questions
/// ("was this agent work approved by a human?") never have to parse an
/// identity string.
[<RequireQualifiedAccess>]
type ActorKind =
    | Agent
    | Human
    | Automation
    | Unknown

[<RequireQualifiedAccess>]
module ActorKind =
    let code kind =
        match kind with
        | ActorKind.Agent -> "agent"
        | ActorKind.Human -> "human"
        | ActorKind.Automation -> "automation"
        | ActorKind.Unknown -> "unknown"

    let tryParse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "agent" -> Some ActorKind.Agent
        | "human" -> Some ActorKind.Human
        | "automation" -> Some ActorKind.Automation
        | "unknown" -> Some ActorKind.Unknown
        | _ -> None

    let all = [ ActorKind.Agent; ActorKind.Human; ActorKind.Automation; ActorKind.Unknown ]

/// How strongly an actor record is backed. Every identity Praxis records
/// today is self-reported provenance, not proof. Other levels (a CI OIDC
/// attestation, a signed execution receipt) are preserved verbatim when an
/// integration supplies them so a later verifier can be added without
/// changing the record shape.
[<RequireQualifiedAccess>]
type Assurance =
    | SelfReported
    | Declared of level: string

[<RequireQualifiedAccess>]
module Assurance =
    [<Literal>]
    let SelfReportedCode = "self-reported"

    let code assurance =
        match assurance with
        | Assurance.SelfReported -> SelfReportedCode
        | Assurance.Declared level -> level

    let parse (value: string) =
        if String.Equals(value.Trim(), SelfReportedCode, StringComparison.OrdinalIgnoreCase) then
            Assurance.SelfReported
        else
            Assurance.Declared(value.Trim())

/// The canonical Praxis actor (`praxis.actor/1`).
///
/// Stable identity: `Kind`, `Id`, `Provider`.
/// Execution-scoped identity: `ExecutionId` (the Praxis telemetry execution,
/// `EXE-...`), `Model`, `ModelVersion`, `Runtime`, `RuntimeVersion`,
/// `SessionId`. Two runs of the same agent share `Kind`/`Id`/`Provider` and
/// never share an `ExecutionId`.
type Actor =
    { Kind: ActorKind
      Id: Attribute
      Provider: Attribute
      Model: Attribute
      ModelVersion: string option
      Runtime: Attribute
      RuntimeVersion: string option
      ExecutionId: Attribute
      SessionId: string option
      Assurance: Assurance }

/// Which attributes a record MUST carry (as a value or as explicit
/// `unknown`) for an actor of a given kind. Everything else is optional and
/// rendered only when known.
[<RequireQualifiedAccess>]
type ActorField =
    | Kind
    | Id
    | Provider
    | Model
    | ModelVersion
    | Runtime
    | RuntimeVersion
    | ExecutionId
    | SessionId
    | Assurance

[<RequireQualifiedAccess>]
module ActorField =
    let name field =
        match field with
        | ActorField.Kind -> "kind"
        | ActorField.Id -> "id"
        | ActorField.Provider -> "provider"
        | ActorField.Model -> "model"
        | ActorField.ModelVersion -> "modelVersion"
        | ActorField.Runtime -> "runtime"
        | ActorField.RuntimeVersion -> "runtimeVersion"
        | ActorField.ExecutionId -> "executionId"
        | ActorField.SessionId -> "sessionId"
        | ActorField.Assurance -> "assurance"

    /// Canonical serialization order.
    let ordered =
        [ ActorField.Kind
          ActorField.Id
          ActorField.Provider
          ActorField.Model
          ActorField.ModelVersion
          ActorField.Runtime
          ActorField.RuntimeVersion
          ActorField.ExecutionId
          ActorField.SessionId
          ActorField.Assurance ]

    let requiredFor kind =
        match kind with
        | ActorKind.Agent ->
            [ ActorField.Kind
              ActorField.Id
              ActorField.Provider
              ActorField.Model
              ActorField.Runtime
              ActorField.ExecutionId ]
        | ActorKind.Automation -> [ ActorField.Kind; ActorField.Id; ActorField.Runtime; ActorField.ExecutionId ]
        | ActorKind.Human
        | ActorKind.Unknown -> [ ActorField.Kind; ActorField.Id ]

[<RequireQualifiedAccess>]
module Actor =
    [<Literal>]
    let SchemaVersion = "praxis.actor/1"

    /// A fully unknown actor: the honest value for a legacy record or for a
    /// process that cannot identify itself.
    let unknown =
        { Kind = ActorKind.Unknown
          Id = Attribute.Unknown
          Provider = Attribute.Unknown
          Model = Attribute.Unknown
          ModelVersion = None
          Runtime = Attribute.Unknown
          RuntimeVersion = None
          ExecutionId = Attribute.Unknown
          SessionId = None
          Assurance = Assurance.SelfReported }

    let human (id: string) =
        { unknown with
            Kind = ActorKind.Human
            Id = Attribute.ofString id }

    /// The (kind, id) pair that names a stable participant across runs.
    let stableKey actor = ActorKind.code actor.Kind, Attribute.render actor.Id

    /// The rendered (field, value) pairs of an actor, in canonical order:
    /// required attributes are always present (explicit `unknown` when not
    /// known), optional attributes only when known.
    let fields actor =
        let required = ActorField.requiredFor actor.Kind |> Set.ofList

        let value field =
            match field with
            | ActorField.Kind -> Some(ActorKind.code actor.Kind)
            | ActorField.Id -> Some(Attribute.render actor.Id)
            | ActorField.Provider -> Some(Attribute.render actor.Provider)
            | ActorField.Model -> Some(Attribute.render actor.Model)
            | ActorField.ModelVersion -> actor.ModelVersion
            | ActorField.Runtime -> Some(Attribute.render actor.Runtime)
            | ActorField.RuntimeVersion -> actor.RuntimeVersion
            | ActorField.ExecutionId -> Some(Attribute.render actor.ExecutionId)
            | ActorField.SessionId -> actor.SessionId
            | ActorField.Assurance -> Some(Assurance.code actor.Assurance)

        let known field =
            match field with
            | ActorField.Provider -> Attribute.isKnown actor.Provider
            | ActorField.Model -> Attribute.isKnown actor.Model
            | ActorField.Runtime -> Attribute.isKnown actor.Runtime
            | ActorField.ExecutionId -> Attribute.isKnown actor.ExecutionId
            | _ -> true

        ActorField.ordered
        |> List.filter (fun field -> required.Contains field || field = ActorField.Assurance || known field)
        |> List.choose (fun field -> value field |> Option.map (fun rendered -> ActorField.name field, rendered))

    /// Human-readable one-line label, e.g. `agent claude-code (anthropic,
    /// model unknown) execution EXE-...`.
    let describe actor =
        let kind = ActorKind.code actor.Kind
        let id = Attribute.render actor.Id

        match actor.Kind with
        | ActorKind.Agent ->
            $"{kind} {id} (provider {Attribute.render actor.Provider}, model {Attribute.render actor.Model}, runtime {Attribute.render actor.Runtime}) execution {Attribute.render actor.ExecutionId}"
        | ActorKind.Automation ->
            $"{kind} {id} (runtime {Attribute.render actor.Runtime}) execution {Attribute.render actor.ExecutionId}"
        | ActorKind.Human
        | ActorKind.Unknown -> $"{kind} {id}"
