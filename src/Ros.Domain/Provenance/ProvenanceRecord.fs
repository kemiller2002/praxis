namespace Ros.Domain.Provenance

open System
open System.Text.RegularExpressions

/// The semantic version of the provenance interchange contract
/// (`praxis.provenance-record`, RQ-ROS-2026-A013). A consumer supports a
/// major version: any minor or patch of a supported major is read (fields it
/// does not model are preserved by the codec), and an unsupported major is
/// carried verbatim, never modified or appended to.
type ContractVersion = { Major: int; Minor: int; Patch: int }

[<RequireQualifiedAccess>]
module ContractVersion =
    let private pattern =
        Regex("^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)

    /// The version this implementation writes.
    let current = { Major = 1; Minor = 0; Patch = 0 }

    let code (version: ContractVersion) = $"{version.Major}.{version.Minor}.{version.Patch}"

    let tryParse (value: string) =
        match pattern.Match value with
        | matched when matched.Success ->
            let part (index: int) = Int32.TryParse matched.Groups[index].Value

            match part 1, part 2, part 3 with
            | (true, major), (true, minor), (true, patch) -> Some { Major = major; Minor = minor; Patch = patch }
            | _ -> None
        | _ -> None

    let isSupported (version: ContractVersion) = version.Major = current.Major

/// A lineage snapshot: the source's own provenance, carried verbatim so a
/// consumer that cannot resolve the source still keeps its originating
/// actors and executions. A snapshot in an unsupported major version is
/// opaque: preserved, never interpreted.
[<RequireQualifiedAccess>]
type SourceSnapshot =
    | Known of ProvenanceRecord
    | Opaque of version: string

/// The versioned form in which artifact provenance crosses a system
/// boundary: the same execution-keyed contributions and actor as
/// `ArtifactProvenance` (not a second identity model), plus lineage kept
/// separate from authorship.
and ProvenanceRecord =
    { Version: ContractVersion
      Subject: string option
      Provenance: ArtifactProvenance
      DerivedFrom: string list
      Sources: (string * SourceSnapshot) list }

/// One contribution anywhere in a record's lineage, flattened so the chain
/// "who did what, in which run, to which subject" can be reconstructed
/// without attributing any one actor with the whole chain.
type ChainLink =
    { Subject: string option
      Depth: int
      Contribution: Contribution }

[<RequireQualifiedAccess>]
module ProvenanceRecord =
    [<Literal>]
    let ContractName = "praxis.provenance-record"

    /// Lineage snapshots nest; this bounds the nesting a reader will follow.
    [<Literal>]
    let MaxSourceDepth = 16

    let private systemPattern = Regex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)
    let private runPattern = Regex("^[A-Za-z0-9_-][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)
    let private foreignExecutionPattern = Regex("^EXE-[a-z][a-z0-9-]*\.", RegexOptions.CultureInvariant)
    let private referencePattern = Regex("^\S+$", RegexOptions.CultureInvariant)

    /// A system other than Praxis that records a contribution with no Praxis
    /// execution propagated to it (RQ-ROS-2026-A014) keys the contribution
    /// by its own run, namespaced so it can never collide with, or be
    /// mistaken for, a Praxis execution: `EXE-<system>.<run>`.
    let foreignExecutionKey (system: string) (run: string) : Result<string, string> =
        if not (systemPattern.IsMatch system) then
            Error $"system '{system}' must match ^[a-z][a-z0-9-]*$"
        elif not (runPattern.IsMatch run) then
            Error $"run '{run}' must match ^[A-Za-z0-9_-][A-Za-z0-9._-]*$"
        else
            Ok $"EXE-{system}.{run}"

    /// Whether an execution key was minted by another system, not by a
    /// Praxis `work begin`.
    let isForeignExecution (key: string) = foreignExecutionPattern.IsMatch key

    let empty =
        { Version = ContractVersion.current
          Subject = None
          Provenance = ArtifactProvenance.empty
          DerivedFrom = []
          Sources = [] }

    let private problem field message : ProvenanceProblem = { Field = field; Message = message }

    let rec private problemsAt (prefix: string) (depth: int) (record: ProvenanceRecord) : ProvenanceProblem list =
        let at field = if prefix = "" then field else $"{prefix}.{field}"

        let provenanceProblems =
            ArtifactProvenance.problems record.Provenance
            |> List.map (fun item ->
                // ArtifactProvenance reports `provenance.contributions...`; in
                // a record the mapping sits at the top level.
                let field =
                    if item.Field.StartsWith("provenance.", StringComparison.Ordinal) then
                        item.Field.Substring("provenance.".Length)
                    else
                        item.Field

                { item with Field = at field })

        let referenceProblems name (values: string list) =
            values
            |> List.filter (referencePattern.IsMatch >> not)
            |> List.map (fun value -> problem (at name) $"reference '{value}' must be a non-empty token without whitespace")

        let credentialProblems =
            [ yield! record.Subject |> Option.toList |> List.map (fun value -> "subject", value)
              yield! record.DerivedFrom |> List.map (fun value -> "derivedFrom", value) ]
            |> List.filter (snd >> Credentials.looksLikeCredential)
            |> List.map (fun (field, _) -> problem (at field) "value looks like a credential; provenance must never carry secrets")

        let lineageProblems =
            [ match record.Subject with
              | Some subject when List.contains subject record.DerivedFrom ->
                  problem (at "derivedFrom") $"'{subject}' cannot be derived from itself"
              | _ -> ()
              if List.length (List.distinct record.DerivedFrom) <> List.length record.DerivedFrom then
                  problem (at "derivedFrom") "lineage references must be unique"
              for reference, snapshot in record.Sources do
                  if not (List.contains reference record.DerivedFrom) then
                      problem (at $"sources.{reference}") "a lineage snapshot must name a reference listed in derivedFrom"

                  match snapshot with
                  | SourceSnapshot.Known source when source.Subject.IsSome && source.Subject <> Some reference ->
                      problem
                          (at $"sources.{reference}.subject")
                          $"the snapshot describes '{source.Subject.Value}', not '{reference}'; a lineage snapshot must be the named source's own provenance"
                  | _ -> () ]

        let sourceProblems =
            record.Sources
            |> List.collect (fun (reference, snapshot) ->
                match snapshot with
                | SourceSnapshot.Opaque _ -> []
                | SourceSnapshot.Known _ when depth >= MaxSourceDepth ->
                    [ problem (at $"sources.{reference}") $"lineage snapshots nest deeper than {MaxSourceDepth} levels" ]
                | SourceSnapshot.Known source -> problemsAt (at $"sources.{reference}") (depth + 1) source)

        provenanceProblems
        @ referenceProblems "subject" (Option.toList record.Subject)
        @ referenceProblems "derivedFrom" record.DerivedFrom
        @ credentialProblems
        @ lineageProblems
        @ sourceProblems

    /// Structural problems of a record and, recursively, its lineage
    /// snapshots. An empty list means the record is well-formed; it says
    /// nothing about whether the recorded identities are true.
    let problems (record: ProvenanceRecord) = problemsAt "" 0 record

    /// Appends a contribution under the same rules as artifact provenance:
    /// only adds entries or extends the contributing execution's own entry,
    /// refuses re-attribution, a second `created`, and a late `created`.
    let record (contribution: Contribution) (record: ProvenanceRecord) : Result<ProvenanceRecord, string> =
        ArtifactProvenance.record contribution record.Provenance
        |> Result.map (fun provenance -> { record with Provenance = provenance })

    /// Starts the provenance of a new subject derived from other subjects.
    /// The creator authors the new subject; the sources are lineage, carried
    /// as snapshots and never merged into the new subject's contributions.
    let derive (subject: string) (creator: Contribution) (sources: (string * SourceSnapshot) list) : Result<ProvenanceRecord, string> =
        if not (Contribution.isCreation creator) then
            Error "the first contribution to a derived subject must be 'created'"
        else
            let references = sources |> List.map fst |> List.distinct

            { empty with
                Subject = Some subject
                Provenance = ArtifactProvenance.ofContributions [ creator ]
                DerivedFrom = references
                Sources = sources |> List.distinctBy fst }
            |> Ok

    /// Every contribution in the record and its lineage snapshots, the
    /// record's own first. Each link keeps the subject it belongs to, so a
    /// source's contributors are never presented as the derivative's.
    let chain (record: ProvenanceRecord) : ChainLink list =
        let rec walk depth (current: ProvenanceRecord) =
            let own =
                current.Provenance.Contributions
                |> List.map (fun contribution ->
                    { Subject = current.Subject
                      Depth = depth
                      Contribution = contribution })

            let lineage =
                if depth >= MaxSourceDepth then
                    []
                else
                    current.Sources
                    |> List.collect (fun (reference, snapshot) ->
                        match snapshot with
                        | SourceSnapshot.Known source ->
                            walk (depth + 1) { source with Subject = source.Subject |> Option.orElse (Some reference) }
                        | SourceSnapshot.Opaque _ -> [])

            own @ lineage

        walk 0 record

    /// Forgery detection where it is possible: a contribution keyed by an
    /// execution this repository has a record of must agree with that
    /// execution's actor. `executions` maps execution IDs to their recorded
    /// actors; keys with no record (foreign, imported, pruned) are not
    /// judged here -- Praxis reports those as warnings elsewhere.
    let impersonationProblems (executions: Map<string, Actor>) (record: ProvenanceRecord) : ProvenanceProblem list =
        chain record
        |> List.choose (fun link ->
            match executions |> Map.tryFind link.Contribution.Key with
            | Some recorded when not (Actor.agrees recorded link.Contribution.Actor) ->
                let subject = link.Subject |> Option.defaultValue "record"

                Some(
                    problem
                        $"contributions.{link.Contribution.Key}.actor"
                        $"{subject}: attributed to {Actor.describe link.Contribution.Actor}, but execution {link.Contribution.Key} is recorded as {Actor.describe recorded}"
                )
            | _ -> None)
        |> List.distinct

    /// Destructive-transformation check for one hop: `after` must keep
    /// every contribution of `before` (same key, same actor, same `at`, no
    /// operation or evidence removed), every lineage reference, and every
    /// lineage snapshot. This catches provenance that was dropped, an
    /// original actor that was overwritten, contribution history that was
    /// replaced, and execution identity that was lost.
    let successorProblems (before: ProvenanceRecord) (after: ProvenanceRecord) : ProvenanceProblem list =
        let afterByKey =
            after.Provenance.Contributions |> List.map (fun item -> item.Key, item) |> Map.ofList

        let contributionProblems =
            before.Provenance.Contributions
            |> List.collect (fun previous ->
                let field name = $"contributions.{previous.Key}{name}"

                match afterByKey |> Map.tryFind previous.Key with
                | None -> [ problem (field "") "contribution was removed; provenance history is append-only" ]
                | Some current ->
                    [ if current.Actor <> previous.Actor then
                          problem (field ".actor") $"actor changed from {Actor.describe previous.Actor} to {Actor.describe current.Actor}"
                      if current.At <> previous.At then
                          problem (field ".at") "the time of the first recorded operation changed"
                      for operation in previous.Operations do
                          if not (List.contains operation current.Operations) then
                              problem (field ".operations") $"operation '{ContributionOperation.code operation}' was removed"
                      for item in previous.Evidence do
                          if not (List.contains item current.Evidence) then
                              problem (field ".evidence") $"evidence '{item}' was removed"
                      match previous.Reason, current.Reason with
                      | Some reason, other when other <> Some reason -> problem (field ".reason") "reason was rewritten"
                      | _ -> () ])

        let originProblems =
            match ArtifactProvenance.originator before.Provenance, ArtifactProvenance.originator after.Provenance with
            | Some previous, Some current when previous.Key <> current.Key ->
                [ problem "contributions" $"originator changed from {previous.Key} to {current.Key}" ]
            | _ -> []

        let lineageProblems =
            [ for reference in before.DerivedFrom do
                  if not (List.contains reference after.DerivedFrom) then
                      problem "derivedFrom" $"lineage reference '{reference}' was removed"
              for reference, _ in before.Sources do
                  if not (after.Sources |> List.exists (fun (key, _) -> key = reference)) then
                      problem $"sources.{reference}" "lineage snapshot was removed"
              if before.Subject.IsSome && after.Subject <> before.Subject then
                  problem "subject" "subject changed; a different subject needs its own record that derives from this one"
              if after.Version.Major <> before.Version.Major then
                  problem "version" "major version changed in place; an unsupported major must be carried verbatim"
              elif compare (after.Version.Minor, after.Version.Patch) (before.Version.Minor, before.Version.Patch) < 0 then
                  problem
                      "version"
                      $"version was lowered from {ContractVersion.code before.Version} to {ContractVersion.code after.Version}; a consumer must not relabel a newer record as an older one" ]

        contributionProblems @ originProblems @ lineageProblems
