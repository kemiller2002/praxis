namespace Ros.Domain.Provenance

open System
open System.Text.RegularExpressions
open Ros.Domain.Artifacts

/// What a contribution did to an artifact. `Created` establishes the
/// originator; every other operation adds to, and never replaces, the
/// accumulated history.
///
/// The role operations (`Discovered` .. `Resolved`, RQ-ROS-2026-A014) let
/// other Echelon systems record *what part* an actor played in a record's
/// life -- finding something, measuring it, carrying it across a boundary,
/// fixing it, confirming the fix, closing it -- without inventing a second
/// vocabulary. None of them transfers authorship: the originator is always
/// the single `Created` contribution.
[<RequireQualifiedAccess>]
type ContributionOperation =
    | Created
    | Modified
    | Reviewed
    | Approved
    | Superseded
    | Migrated
    /// Found the fact, defect, or risk the record reports.
    | Discovered
    /// Measured the record's subject; says nothing about who authored it.
    | Measured
    /// Converted the record into another system's representation without
    /// changing its meaning (an integration executable).
    | Transformed
    /// Changed the subject to address what the record reports.
    | Remediated
    /// Independently confirmed an outcome (for example that a remediation
    /// worked).
    | Validated
    /// Closed or dispositioned the record.
    | Resolved
    | Extension of string

[<RequireQualifiedAccess>]
module ContributionOperation =
    let private extensionPattern =
        Regex("^x-[a-z0-9][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    let code operation =
        match operation with
        | ContributionOperation.Created -> "created"
        | ContributionOperation.Modified -> "modified"
        | ContributionOperation.Reviewed -> "reviewed"
        | ContributionOperation.Approved -> "approved"
        | ContributionOperation.Superseded -> "superseded"
        | ContributionOperation.Migrated -> "migrated"
        | ContributionOperation.Discovered -> "discovered"
        | ContributionOperation.Measured -> "measured"
        | ContributionOperation.Transformed -> "transformed"
        | ContributionOperation.Remediated -> "remediated"
        | ContributionOperation.Validated -> "validated"
        | ContributionOperation.Resolved -> "resolved"
        | ContributionOperation.Extension value -> value

    let tryParse (value: string) =
        match value with
        | "created" -> Some ContributionOperation.Created
        | "modified" -> Some ContributionOperation.Modified
        | "reviewed" -> Some ContributionOperation.Reviewed
        | "approved" -> Some ContributionOperation.Approved
        | "superseded" -> Some ContributionOperation.Superseded
        | "migrated" -> Some ContributionOperation.Migrated
        | "discovered" -> Some ContributionOperation.Discovered
        | "measured" -> Some ContributionOperation.Measured
        | "transformed" -> Some ContributionOperation.Transformed
        | "remediated" -> Some ContributionOperation.Remediated
        | "validated" -> Some ContributionOperation.Validated
        | "resolved" -> Some ContributionOperation.Resolved
        | extension when extensionPattern.IsMatch extension -> Some(ContributionOperation.Extension extension)
        | _ -> None

    let private operationGrammar =
        Regex("^[a-z][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    /// The operation grammar every contract-1 reader accepts.
    let isGrammarValid (value: string) = not (isNull value) && operationGrammar.IsMatch value

    /// Whether a code is known to this version (a core operation or an
    /// `x-...` extension), as opposed to one from a later 1.x release.
    let isKnown (operation: ContributionOperation) =
        match operation with
        | ContributionOperation.Extension value -> extensionPattern.IsMatch value
        | _ -> true

    /// Whether the operation changed the artifact's content or standing (and
    /// so counts toward "modified by"). Observing, measuring, reviewing,
    /// approving, and validating do not; an unrecognized `x-...` extension is
    /// conservatively treated as a modification.
    let isModification operation =
        match operation with
        | ContributionOperation.Created
        | ContributionOperation.Reviewed
        | ContributionOperation.Approved
        | ContributionOperation.Discovered
        | ContributionOperation.Measured
        | ContributionOperation.Validated -> false
        | ContributionOperation.Modified
        | ContributionOperation.Superseded
        | ContributionOperation.Migrated
        | ContributionOperation.Transformed
        | ContributionOperation.Remediated
        | ContributionOperation.Resolved
        | ContributionOperation.Extension _ -> true

/// One actor's contribution to one artifact. Keyed by the execution that
/// produced it (`EXE-...`) so every contribution made during one execution
/// is traceable to that execution, and two executions of the same agent can
/// never collapse into one entry. A contribution made outside any ROS
/// execution (typically a human editing directly) uses a generated
/// `CTB-...` key and records no execution. A contribution imported from
/// another Echelon system is keyed by that system's own run as
/// `EXT-<system>.<run-id>` (RQ-ROS-2026-A013): a foreign execution this
/// repository carries verbatim but cannot cross-check.
type Contribution =
    { Key: string
      Operations: ContributionOperation list
      /// When this execution first contributed to the artifact.
      At: string
      /// When this execution last recorded an operation, if later than `At`:
      /// every recorded modification leaves a visible trace even when the
      /// execution had already recorded the same operation.
      Last: string option
      Actor: Actor
      Reason: string option
      Evidence: string list }

[<RequireQualifiedAccess>]
module Contribution =
    let private executionPattern =
        Regex("^EXE-[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)

    let private contributionKeyPattern =
        Regex("^CTB-[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)

    /// `EXT-<system>.<run-id>`: `<system>` is an Echelon registry system id
    /// (no dots), so the first dot always separates it from the run id.
    let private foreignExecutionPattern =
        Regex("^EXT-([a-z][a-z0-9-]*)\\.([A-Za-z0-9._-]+)\\z", RegexOptions.CultureInvariant)

    let private timestampPattern =
        Regex("^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.([0-9]{1,9}))?Z\\z", RegexOptions.CultureInvariant)

    let isExecutionId (value: string) = executionPattern.IsMatch value

    let isForeignExecutionId (value: string) = foreignExecutionPattern.IsMatch value

    let isValidKey (value: string) =
        executionPattern.IsMatch value
        || contributionKeyPattern.IsMatch value
        || foreignExecutionPattern.IsMatch value

    /// The owning system of a foreign execution key (`EXT-dokimos.run-7` ->
    /// `dokimos`).
    let foreignSystem (value: string) =
        let matched = foreignExecutionPattern.Match value
        if matched.Success then Some matched.Groups[1].Value else None

    /// A calendar-valid UTC instant (year 0001-9999, no rollover such as
    /// Feb 30 or 24:00), truncated to millisecond precision: contract 1.1
    /// compares contribution times in milliseconds everywhere, so every
    /// implementation orders a history identically.
    let parseTimestamp (value: string) : DateTimeOffset option =
        match value with
        | null -> None
        | text ->
            let matched = timestampPattern.Match text

            if not matched.Success then
                None
            else
                let number (index: int) = int matched.Groups[index].Value
                let fraction = matched.Groups[7].Value.PadRight(3, '0').Substring(0, 3)

                try
                    Some(DateTimeOffset(number 1, number 2, number 3, number 4, number 5, number 6, int fraction, TimeSpan.Zero))
                with :? ArgumentOutOfRangeException ->
                    None

    let isTimestamp (value: string) = (parseTimestamp value).IsSome

    /// The local Praxis execution (`EXE-...`) that produced the
    /// contribution, which this repository can cross-check.
    let execution (contribution: Contribution) =
        if isExecutionId contribution.Key then Some contribution.Key else None

    /// Another Echelon system's execution (`EXT-...`), carried verbatim.
    let foreignExecution (contribution: Contribution) =
        if isForeignExecutionId contribution.Key then Some contribution.Key else None

    /// Any execution, local or foreign: a run, as opposed to a `CTB-...`
    /// contribution made outside any execution.
    let anyExecution (contribution: Contribution) =
        execution contribution |> Option.orElse (foreignExecution contribution)

    let has operation (contribution: Contribution) =
        contribution.Operations |> List.contains operation

    let isCreation contribution = has ContributionOperation.Created contribution

    /// `at` as a comparable instant; invalid timestamps sort last so they
    /// never masquerade as the earliest (originating) contribution.
    let instant (contribution: Contribution) =
        parseTimestamp contribution.At |> Option.defaultValue DateTimeOffset.MaxValue

    /// The calendar date (`yyyy-MM-dd`) of the contribution's latest recorded
    /// operation, comparable with the date-granular `created`/`updated`
    /// front-matter fields.
    let date (contribution: Contribution) =
        let latest = contribution.Last |> Option.defaultValue contribution.At
        if latest.Length >= 10 then latest[..9] else latest

    let problems (contribution: Contribution) : (string * string) list =
        [ if not (isValidKey contribution.Key) then
              "key", $"contribution key '{contribution.Key}' must be an execution ID (EXE-...), a foreign execution ID (EXT-<system>.<run-id>), or a contribution ID (CTB-...)"
          if contribution.Operations.IsEmpty then
              "operations", "contribution must record at least one operation"
          if not (isTimestamp contribution.At) then
              "at", $"'{contribution.At}' is not an ISO-8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss[.fff]Z)"
          match contribution.Last with
          | Some last when not (isTimestamp last) -> "last", $"'{last}' is not an ISO-8601 UTC timestamp"
          | Some last when instant { contribution with At = last } < instant contribution -> "last", "last must not precede at"
          | _ -> ()
          yield! Actor.problems contribution.Actor |> List.map (fun (field, message) -> $"actor.{field}", message)
          if contribution.Actor.Kind = ActorKind.Agent && anyExecution contribution |> Option.isNone then
              "key", "an agent contribution must be keyed by the execution (EXE-... or EXT-<system>.<run-id>) that produced it"
          if contribution.Evidence |> List.exists (fun item -> item.Trim().Length = 0) then
              "evidence", "evidence references must not be empty" ]

/// The accumulated, append-only provenance of one artifact. Contributions
/// are ordered by time; the history is never rewritten, only extended.
type ArtifactProvenance = { Contributions: Contribution list }

/// A structural problem found while reading a front-matter provenance
/// block, attributed to a dotted field path within it.
type ProvenanceProblem = { Field: string; Message: string }

[<RequireQualifiedAccess>]
module ArtifactProvenance =
    [<Literal>]
    let FieldName = "provenance"

    let empty = { Contributions = [] }

    let private order (contributions: Contribution list) =
        contributions
        |> List.sortWith (fun left right ->
            let byTime = compare (Contribution.instant left) (Contribution.instant right)
            if byTime <> 0 then byTime else StringComparer.Ordinal.Compare(left.Key, right.Key))

    let ofContributions contributions = { Contributions = order contributions }

    let originator (provenance: ArtifactProvenance) =
        provenance.Contributions |> List.tryFind Contribution.isCreation

    let latest (provenance: ArtifactProvenance) =
        provenance.Contributions |> List.tryLast

    let contributors (provenance: ArtifactProvenance) =
        provenance.Contributions |> List.map _.Actor |> List.distinct

    let executions (provenance: ArtifactProvenance) =
        provenance.Contributions |> List.choose Contribution.execution

    /// Appends a contribution without disturbing any existing one. A second
    /// recording by the same execution merges its new operation(s) into
    /// that execution's single entry (so the history stays one-entry-per-
    /// execution and re-recording is idempotent) but only when the actor
    /// agrees: one execution can never be attributed to two actors.
    let record (contribution: Contribution) (provenance: ArtifactProvenance) : Result<ArtifactProvenance, string> =
        match provenance.Contributions |> List.tryFind (fun existing -> existing.Key = contribution.Key) with
        | Some existing when not (Actor.agrees existing.Actor contribution.Actor) ->
            Error
                $"contribution '{contribution.Key}' is already attributed to {Actor.describe existing.Actor}; refusing to re-attribute it to {Actor.describe contribution.Actor}"
        | Some existing when
            Contribution.isCreation contribution
            && not (Contribution.isCreation existing)
            && ((originator provenance).IsSome
                || provenance.Contributions |> List.exists (fun item -> item.Key <> existing.Key && Contribution.instant item < Contribution.instant existing))
            ->
            // Merging 'created' into a later entry would give the artifact a
            // second or out-of-order originator (DF-ROS-2026-A037 review).
            Error "the artifact's originator is already recorded or precedes this contribution; record 'modified' instead of 'created'"
        | Some existing ->
            let operations =
                existing.Operations
                @ (contribution.Operations |> List.filter (fun operation -> not (List.contains operation existing.Operations)))

            let evidence =
                existing.Evidence
                @ (contribution.Evidence |> List.filter (fun item -> not (List.contains item existing.Evidence)))

            let latestSoFar = { existing with At = existing.Last |> Option.defaultValue existing.At }

            let last =
                if Contribution.instant contribution > Contribution.instant latestSoFar then
                    Some contribution.At
                else
                    existing.Last

            let merged =
                { existing with
                    Operations = operations
                    Evidence = evidence
                    Last = last
                    Reason = existing.Reason |> Option.orElse contribution.Reason }

            provenance.Contributions
            |> List.map (fun item -> if item.Key = existing.Key then merged else item)
            |> ofContributions
            |> Ok
        | None when Contribution.isCreation contribution && (originator provenance).IsSome ->
            Error "artifact already has a recorded originator; record this contribution as 'modified' instead of 'created'"
        | None when
            Contribution.isCreation contribution
            && provenance.Contributions |> List.exists (fun item -> Contribution.instant item < Contribution.instant contribution)
            ->
            Error "a 'created' contribution cannot follow existing contributions; record it as 'modified'"
        | None -> provenance.Contributions @ [ contribution ] |> ofContributions |> Ok

    /// Artifact-level invariants beyond each contribution's own structure.
    let problems (provenance: ArtifactProvenance) : ProvenanceProblem list =
        let perContribution =
            provenance.Contributions
            |> List.collect (fun contribution ->
                Contribution.problems contribution
                |> List.map (fun (field, message) ->
                    { Field = $"provenance.contributions.{contribution.Key}.{field}"
                      Message = message }))

        let creations = provenance.Contributions |> List.filter Contribution.isCreation

        let creationProblems =
            match creations with
            | [] -> []
            | [ creation ] ->
                provenance.Contributions
                |> List.filter (fun item -> Contribution.instant item < Contribution.instant creation)
                |> List.map (fun item ->
                    { Field = $"provenance.contributions.{item.Key}.at"
                      Message = $"contribution precedes the artifact's recorded creation ({creation.Key} at {creation.At})" })
            | many ->
                [ { Field = "provenance.contributions"
                    Message =
                      "more than one contribution claims 'created': "
                      + (many |> List.map _.Key |> String.concat ", ") } ]

        perContribution @ creationProblems

    // ---- reading the front-matter representation -------------------------

    let private text (value: ArtifactValue) =
        match value with
        | ArtifactValue.Text item -> Some item
        | ArtifactValue.Number number -> Some(string number)
        | ArtifactValue.Boolean flag -> Some(if flag then "true" else "false")
        | _ -> None

    let private textList (value: ArtifactValue) =
        match value with
        | ArtifactValue.Sequence items -> items |> List.choose text
        | ArtifactValue.Text item when item.Trim().Length > 0 -> [ item ]
        | _ -> []

    let private field name (mapping: Map<string, ArtifactValue>) = mapping |> Map.tryFind name

    let private parseActor (prefix: string) (value: ArtifactValue option) : Result<Actor, ProvenanceProblem list> =
        match value with
        | Some(ArtifactValue.Mapping actor) ->
            let kindText = field "kind" actor |> Option.bind text

            match kindText |> Option.bind ActorKind.tryParse with
            | None ->
                Error
                    [ { Field = $"{prefix}.kind"
                        Message =
                          match kindText with
                          | Some value -> $"unknown actor kind '{value}'"
                          | None -> "actor kind is required" } ]
            | Some kind ->
                Ok
                    { Kind = kind
                      Id = field "id" actor |> Option.bind text |> Option.defaultValue ""
                      Provider = field "provider" actor |> Option.bind text
                      Model = field "model" actor |> Option.bind text
                      Runtime = field "runtime" actor |> Option.bind text }
        | _ -> Error [ { Field = prefix; Message = "actor must be a mapping with at least 'kind' and 'id'" } ]

    let private parseContribution (key: string) (value: ArtifactValue) : Result<Contribution, ProvenanceProblem list> =
        let prefix = $"provenance.contributions.{key}"

        match value with
        | ArtifactValue.Mapping entry ->
            let operationTexts = field "operations" entry |> Option.map textList |> Option.defaultValue []

            // Contract 1.1: a grammar-valid operation this version does not
            // know (added by a later 1.x) is carried as an extension and
            // reported by validation, not rejected; anything else is malformed.
            let parseOperation (operation: string) =
                ContributionOperation.tryParse operation
                |> Option.orElse (
                    if ContributionOperation.isGrammarValid operation then
                        Some(ContributionOperation.Extension operation)
                    else
                        None
                )

            let unknownOperations =
                operationTexts
                |> List.filter (fun operation -> parseOperation operation |> Option.isNone)
                |> List.map (fun operation ->
                    { Field = $"{prefix}.operations"
                      Message = $"unknown operation '{operation}'" })

            match parseActor $"{prefix}.actor" (field "actor" entry) with
            | Error problems -> Error(unknownOperations @ problems)
            | Ok _ when not unknownOperations.IsEmpty -> Error unknownOperations
            | Ok actor ->
                Ok
                    { Key = key
                      Operations = operationTexts |> List.choose parseOperation |> List.distinct
                      At = field "at" entry |> Option.bind text |> Option.defaultValue ""
                      Last = field "last" entry |> Option.bind text
                      Actor = actor
                      Reason = field "reason" entry |> Option.bind text |> Option.filter (fun item -> item.Trim().Length > 0)
                      Evidence = field "evidence" entry |> Option.map textList |> Option.defaultValue [] }
        | _ -> Error [ { Field = prefix; Message = "contribution must be a mapping" } ]

    /// Reads an artifact's `provenance` front-matter block. `Ok None` means
    /// the artifact carries no provenance at all (legacy or not yet
    /// attributed); a present-but-malformed block is an error, never
    /// silently treated as absent.
    let parse (metadata: Map<string, ArtifactValue>) : Result<ArtifactProvenance option, ProvenanceProblem list> =
        match metadata |> Map.tryFind FieldName with
        | None -> Ok None
        | Some(ArtifactValue.Mapping block) ->
            match block |> Map.tryFind "contributions" with
            | None -> Error [ { Field = "provenance.contributions"; Message = "provenance must contain a 'contributions' mapping" } ]
            | Some(ArtifactValue.Mapping entries) ->
                let parsed = entries |> Map.toList |> List.map (fun (key, value) -> parseContribution key value)
                let problems = parsed |> List.collect (function Error problems -> problems | Ok _ -> [])

                if problems.IsEmpty then
                    parsed |> List.choose (function Ok item -> Some item | Error _ -> None) |> ofContributions |> Some |> Ok
                else
                    Error problems
            | Some _ ->
                Error
                    [ { Field = "provenance.contributions"
                        Message = "'contributions' must be a mapping keyed by execution (EXE-... or EXT-...) or contribution (CTB-...) ID" } ]
        | Some _ -> Error [ { Field = FieldName; Message = "'provenance' must be a mapping" } ]

/// How humans and agents were involved in an artifact, derived purely from
/// its recorded contributions. The originator is never inferred from the
/// last modifier.
type Involvement =
    { Origin: Actor option
      Modifiers: Actor list
      Reviewers: Actor list
      Approvers: Actor list
      AgentToAgentRevision: bool
      HumanCorrectionOfAgentWork: bool
      Label: string }

[<RequireQualifiedAccess>]
module Involvement =
    let private kinds (actors: Actor list) =
        actors |> List.map (fun actor -> ActorKind.code actor.Kind) |> List.distinct |> List.sort

    let describe (provenance: ArtifactProvenance) : Involvement =
        let origin = ArtifactProvenance.originator provenance
        let originKey = origin |> Option.map _.Key

        let others =
            provenance.Contributions |> List.filter (fun item -> Some item.Key <> originKey)

        let actorsWith predicate =
            others |> List.filter predicate |> List.map _.Actor |> List.distinct

        let modifiers =
            actorsWith (fun item -> item.Operations |> List.exists ContributionOperation.isModification)

        let reviewers = actorsWith (Contribution.has ContributionOperation.Reviewed)
        let approvers = actorsWith (Contribution.has ContributionOperation.Approved)

        let originActor = origin |> Option.map _.Actor

        let agentToAgent =
            match originActor with
            | Some creator when creator.Kind = ActorKind.Agent ->
                modifiers |> List.exists (fun actor -> actor.Kind = ActorKind.Agent && actor.Id <> creator.Id)
            | _ -> false

        let humanCorrection =
            match originActor with
            | Some creator when creator.Kind = ActorKind.Agent -> modifiers |> List.exists (fun actor -> actor.Kind = ActorKind.Human)
            | _ -> false

        let originPart =
            match originActor with
            | Some actor -> $"{ActorKind.code actor.Kind}-created"
            | None when provenance.Contributions.IsEmpty -> "unattributed"
            | None -> "origin-unknown"

        let suffix label actors =
            match kinds actors with
            | [] -> []
            | values -> [ (String.concat "+" values) + label ]

        { Origin = originActor
          Modifiers = modifiers
          Reviewers = reviewers
          Approvers = approvers
          AgentToAgentRevision = agentToAgent
          HumanCorrectionOfAgentWork = humanCorrection
          Label = originPart :: (suffix "-modified" modifiers @ suffix "-reviewed" reviewers @ suffix "-approved" approvers) |> String.concat ", " }

/// Authorship and lineage are different relationships: an artifact's
/// contributors wrote it; the artifacts named in its existing
/// `derived_from` reference field are what it was derived from. Lineage
/// references are artifact IDs (or foreign, namespaced references from
/// another system), never actors.
[<RequireQualifiedAccess>]
module Lineage =
    [<Literal>]
    let FieldName = "derived_from"

    let sources (document: ArtifactDocument) =
        match ArtifactDocument.tryMetadata FieldName document with
        | Some(ArtifactValue.Text value) when value.Trim().Length > 0 -> [ value.Trim() ]
        | Some(ArtifactValue.Sequence values) ->
            values
            |> List.choose (function
                | ArtifactValue.Text value when value.Trim().Length > 0 -> Some(value.Trim())
                | _ -> None)
        | _ -> []

    let derivatives (identifier: string) (documents: ArtifactDocument list) =
        documents
        |> List.filter (fun document -> sources document |> List.contains identifier)
        |> List.map ArtifactDocument.identifier
        |> List.sort
