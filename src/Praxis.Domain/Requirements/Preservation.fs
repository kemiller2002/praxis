namespace Praxis.Domain.Requirements

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// The requirements-preservation model (REQ-PRES-001, DC-008): an
/// authoritative baseline of source requirements and their mandatory
/// obligations, a derived package's disposition of each one, and approval
/// records that only an approving authority supplies. Everything here is
/// pure: text and typed values in, a deterministic report out. Nothing in
/// this module creates an approval or an equivalence review.

/// What an obligation demands of the derived criterion that carries it.
[<RequireQualifiedAccess>]
type ObligationKind =
    /// Exact tokens (hex values, thresholds) that must appear literally.
    | Value
    /// A verification obligation; it must map to a derived acceptance criterion.
    | Acceptance
    /// Any other mandatory constraint.
    | Constraint

type Obligation =
    { Id: string
      Kind: ObligationKind
      /// Verbatim source text; it must occur in the requirement's section.
      Text: string
      /// Exact tokens the mapped derived criteria must contain
      /// (case-insensitive). Required for `Value`; optional otherwise, and an
      /// obligation without tokens can only be accepted by a recorded review.
      Tokens: string list }

type SourceRequirement =
    { Id: string
      Title: string
      Priority: string
      /// Path of the source document (relative to the source root).
      Document: string
      /// The verbatim heading line that opens the requirement's section.
      Heading: string
      SectionSha256: string
      Obligations: Obligation list }

type SourceDocument =
    { Path: string
      Sha256: string
      UpstreamPath: string option
      /// A regex whose first group captures a requirement id on a heading
      /// line; every id it finds must be inventoried.
      RequirementPattern: string option }

type DeclaredConflict =
    { Id: string
      Requirements: string list
      Description: string
      /// The approval record that decides the conflict, when one exists.
      Decision: string option }

type Baseline =
    { Id: string
      Repository: string
      Commit: string
      Documents: SourceDocument list
      Requirements: SourceRequirement list
      Conflicts: DeclaredConflict list }

/// An approval decided by an approving authority outside the derivation
/// (for example an operator, later an Ordo decision). Praxis reads these;
/// it never writes them.
type ApprovalRecord =
    { DecisionId: string
      Authority: string
      Scope: string
      /// Requirement, obligation or conflict ids the approval covers.
      Covers: string list
      Date: string
      OrdoDecision: string option }

/// A human attestation that a derived criterion carries an obligation that
/// no token can decide. Never produced by Praxis.
type EquivalenceReview =
    { Reviewer: string
      Date: string
      Basis: string }

type CriterionRef =
    { Requirement: string
      Criterion: string }

[<RequireQualifiedAccess>]
module CriterionRef =
    let render (reference: CriterionRef) = $"{reference.Requirement}#{reference.Criterion}"

    let parse (text: string) =
        match text.Split('#') with
        | [| requirement; criterion |] when requirement.Trim() <> "" && criterion.Trim() <> "" ->
            Ok
                { Requirement = requirement.Trim()
                  Criterion = criterion.Trim() }
        | _ -> Error $"'{text}' is not a criterion reference of the form REQUIREMENT-ID#CRITERION-ID"

[<RequireQualifiedAccess>]
type ObligationCoverage =
    | Mapped of criteria: CriterionRef list * review: EquivalenceReview option
    | Excluded of approval: string * rationale: string

type ObligationMapping =
    { Obligation: string
      Coverage: ObligationCoverage }

[<RequireQualifiedAccess>]
type Disposition =
    | Preserved of ObligationMapping list
    | Strengthened of mappings: ObligationMapping list * rationale: string
    | ExcludedWithApproval of approval: string * rationale: string
    | Superseded of replacement: CriterionRef list * approval: string
    | NotApplicable of justification: string * approval: string option

[<RequireQualifiedAccess>]
module Disposition =
    let code disposition =
        match disposition with
        | Disposition.Preserved _ -> "preserved"
        | Disposition.Strengthened _ -> "strengthened"
        | Disposition.ExcludedWithApproval _ -> "excluded-with-approval"
        | Disposition.Superseded _ -> "superseded"
        | Disposition.NotApplicable _ -> "not-applicable"

type RequirementDisposition =
    { Requirement: string
      Disposition: Disposition }

type DispositionSet =
    { BaselineId: string
      BaselineCommit: string
      Package: string
      Entries: RequirementDisposition list
      Conflicts: DeclaredConflict list }

[<RequireQualifiedAccess>]
type CriterionKind =
    | Acceptance
    | Statement

type DerivedCriterion =
    { Requirement: string
      Id: string
      Text: string
      Kind: CriterionKind }

/// The current text of one baseline source document, or `None` when the
/// document is absent from the source root.
type SourceObservation = { Path: string; Text: string option }

[<RequireQualifiedAccess>]
type FindingStatus =
    /// A deterministic violation.
    | Fail
    /// The source changed against the pinned baseline; reconcile first.
    | Drift
    /// Pending a decision (a declared conflict).
    | Blocked
    /// Only semantic judgement can decide; blocks acceptance until reviewed.
    | NeedsReview
    /// Accepted, and reported so it stays visible (approvals, reviews).
    | Info

[<RequireQualifiedAccess>]
type FindingCode =
    | BaselineInvalid
    | DispositionInvalid
    | BaselineMismatch
    | SourceDocumentMissing
    | SourceDocumentChanged
    | SourceSectionMissing
    | SourceSectionChanged
    | UninventoriedRequirement
    | DispositionMissing
    | DispositionDuplicate
    | DispositionUnknownRequirement
    | ObligationUnmapped
    | ObligationUnknown
    | ObligationDuplicate
    | CriterionNotFound
    | DerivedCriterionDuplicate
    | ValueTokenMissing
    | TokenMissing
    | AcceptanceCriterionMissing
    | EquivalenceUnverified
    | EquivalenceReviewed
    | ApprovalMissing
    | ApprovalInvalid
    | ExclusionApproved
    | SupersessionApproved
    | NotApplicableUnjustified
    | NotApplicableUnapproved
    | NotApplicableApproved
    | ConflictUnresolved
    | ConflictResolved
    | ConflictInvalid

[<RequireQualifiedAccess>]
module FindingCode =
    let render code =
        match code with
        | FindingCode.BaselineInvalid -> "baseline-invalid"
        | FindingCode.DispositionInvalid -> "disposition-invalid"
        | FindingCode.BaselineMismatch -> "baseline-mismatch"
        | FindingCode.SourceDocumentMissing -> "source-document-missing"
        | FindingCode.SourceDocumentChanged -> "source-document-changed"
        | FindingCode.SourceSectionMissing -> "source-section-missing"
        | FindingCode.SourceSectionChanged -> "source-section-changed"
        | FindingCode.UninventoriedRequirement -> "uninventoried-requirement"
        | FindingCode.DispositionMissing -> "disposition-missing"
        | FindingCode.DispositionDuplicate -> "disposition-duplicate"
        | FindingCode.DispositionUnknownRequirement -> "disposition-unknown-requirement"
        | FindingCode.ObligationUnmapped -> "obligation-unmapped"
        | FindingCode.ObligationUnknown -> "obligation-unknown"
        | FindingCode.ObligationDuplicate -> "obligation-duplicate"
        | FindingCode.CriterionNotFound -> "criterion-not-found"
        | FindingCode.DerivedCriterionDuplicate -> "derived-criterion-duplicate"
        | FindingCode.ValueTokenMissing -> "value-token-missing"
        | FindingCode.TokenMissing -> "token-missing"
        | FindingCode.AcceptanceCriterionMissing -> "acceptance-criterion-missing"
        | FindingCode.EquivalenceUnverified -> "equivalence-unverified"
        | FindingCode.EquivalenceReviewed -> "equivalence-reviewed"
        | FindingCode.ApprovalMissing -> "approval-missing"
        | FindingCode.ApprovalInvalid -> "approval-invalid"
        | FindingCode.ExclusionApproved -> "exclusion-approved"
        | FindingCode.SupersessionApproved -> "supersession-approved"
        | FindingCode.NotApplicableUnjustified -> "not-applicable-unjustified"
        | FindingCode.NotApplicableUnapproved -> "not-applicable-unapproved"
        | FindingCode.NotApplicableApproved -> "not-applicable-approved"
        | FindingCode.ConflictUnresolved -> "conflict-unresolved"
        | FindingCode.ConflictResolved -> "conflict-resolved"
        | FindingCode.ConflictInvalid -> "conflict-invalid"

type Finding =
    { Code: FindingCode
      Status: FindingStatus
      Requirement: string option
      Obligation: string option
      Message: string }

[<RequireQualifiedAccess>]
type Verdict =
    | Pass
    | Fail
    | Blocked

/// How an obligation's coverage was decided, for the report.
[<RequireQualifiedAccess>]
type Decision =
    | Tokens
    /// The obligation's verbatim text (whitespace collapsed) appears in a
    /// mapped criterion: deterministic, no review needed.
    | Verbatim
    | Review of EquivalenceReview
    | Exclusion of approval: string
    | Undecided

type TokenResult = { Token: string; Found: bool }

type ObligationOutcome =
    { Obligation: Obligation
      Criteria: CriterionRef list
      Tokens: TokenResult list
      Decision: Decision
      Status: FindingStatus option }

type RequirementOutcome =
    { Requirement: SourceRequirement
      Disposition: Disposition option
      Obligations: ObligationOutcome list
      Verdict: Verdict }

type ExclusionRecord =
    { Requirement: string
      Obligation: string option
      Approval: ApprovalRecord }

type ConflictOutcome =
    { Conflict: DeclaredConflict
      Resolved: bool }

type DocumentOutcome =
    { Document: SourceDocument
      ObservedSha256: string option }

type PreservationReport =
    { Verdict: Verdict
      Documents: DocumentOutcome list
      Requirements: RequirementOutcome list
      Exclusions: ExclusionRecord list
      Conflicts: ConflictOutcome list
      Findings: Finding list }

[<RequireQualifiedAccess>]
module Preservation =
    let private ordinal = StringComparison.Ordinal
    let private caseless = StringComparison.OrdinalIgnoreCase

    /// Line endings are normalized to LF before any hash or match, so a
    /// checkout's autocrlf setting cannot manufacture drift.
    let normalize (text: string) = text.Replace("\r\n", "\n").Replace('\r', '\n')

    let sha256Bytes (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    let sha256Text (text: string) = sha256Bytes (Encoding.UTF8.GetBytes(normalize text))

    let private headingLevel (line: string) =
        let level = line |> Seq.takeWhile ((=) '#') |> Seq.length
        if level > 0 && line.Length > level && line[level] = ' ' then Some level else None

    let private regexOf (pattern: string option) =
        pattern |> Option.map (fun p -> Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds 1.0))

    let private captured (regex: Regex option) (line: string) =
        regex
        |> Option.bind (fun r ->
            let m = r.Match line
            if m.Success && m.Groups.Count > 1 then Some m.Groups[1].Value else None)

    let private sectionIn (regex: Regex option) (lines: string array) (start: int) =
        let bounds (line: string) =
            captured regex line |> Option.isSome
            || match headingLevel lines[start], headingLevel line with
               | Some level, Some other -> other <= level
               | None, Some _ -> true
               | _, None -> false

        let finish =
            lines
            |> Array.skip (start + 1)
            |> Array.tryFindIndex bounds
            |> Option.map (fun offset -> start + 1 + offset)
            |> Option.defaultValue lines.Length

        lines[start .. finish - 1]
        |> Array.rev
        |> Array.skipWhile String.IsNullOrWhiteSpace
        |> Array.rev
        |> String.concat "\n"

    let private linesOf (text: string) = (normalize text).Split('\n') |> Array.map _.TrimEnd()

    /// The section an anchor line opens, trailing blank lines removed; `None`
    /// when no line equals the anchor exactly. A markdown heading anchor runs
    /// to the next heading of the same or a higher level; any other anchor
    /// (a bold paragraph or list item that opens a requirement) runs to the
    /// next heading. Either ends early at the next line `pattern` captures a
    /// requirement id from.
    let section (pattern: string option) (heading: string) (text: string) =
        let lines = linesOf text
        let wanted = heading.Trim()

        lines
        |> Array.tryFindIndex (fun line -> line = wanted)
        |> Option.map (sectionIn (regexOf pattern) lines)

    type SectionEntry =
        { Id: string option
          Heading: string
          Sha256: string }

    /// Every section anchor in a document (each heading, and each line the
    /// pattern captures an id from), with its id and section hash; the
    /// inventory helper behind `requirements preservation sections`.
    let sections (pattern: string option) (text: string) =
        let lines = linesOf text
        let regex = regexOf pattern

        lines
        |> Array.indexed
        |> Array.filter (fun (_, line) -> (headingLevel line).IsSome || (captured regex line).IsSome)
        |> Array.map (fun (index, line) ->
            { Id = captured regex line
              Heading = line
              Sha256 = sectionIn regex lines index |> sha256Text })
        |> Array.toList

    /// Table cells split on unescaped pipes; `\|` is a literal pipe.
    let private cells (line: string) =
        let trimmed = line.Trim()
        let inner = trimmed.Substring(1, (if trimmed.EndsWith "|" && not (trimmed.EndsWith "\\|") && trimmed.Length > 1 then trimmed.Length - 2 else trimmed.Length - 1))

        let folded =
            inner
            |> Seq.fold
                (fun (cellsSoFar: string list, current: StringBuilder, escaped: bool) c ->
                    match c with
                    | '|' when escaped ->
                        current.Length <- current.Length - 1
                        current.Append('|') |> ignore
                        cellsSoFar, current, false
                    | '|' -> current.ToString() :: cellsSoFar, StringBuilder(), false
                    | '\\' -> cellsSoFar, current.Append(c), true
                    | _ -> cellsSoFar, current.Append(c), false)
                ([], StringBuilder(), false)

        let cellsSoFar, last, _ = folded
        last.ToString() :: cellsSoFar |> List.rev |> List.map _.Trim()

    let private isSeparator (line: string) =
        let t = line.Trim()
        t.StartsWith "|" && t |> Seq.forall (fun c -> c = '|' || c = '-' || c = ':' || c = ' ')

    let private frontMatterId (lines: string array) =
        if lines.Length > 0 && lines[0].Trim() = "---" then
            lines
            |> Array.skip 1
            |> Array.takeWhile (fun line -> line.Trim() <> "---")
            |> Array.tryPick (fun line ->
                let t = line.Trim()
                if t.StartsWith("id:", ordinal) then Some(t.Substring(3).Trim().Trim('"', '\'')) else None)
        else
            None

    /// Criteria of one derived requirement document: every markdown table
    /// whose first header cell is `ID`. A table whose second header cell
    /// names "acceptance" holds acceptance criteria. The requirement id is
    /// the front-matter `id`, else the file name before `--`.
    let parseDerived (fileName: string) (text: string) : DerivedCriterion list =
        let lines = (normalize text).Split('\n')

        let requirement =
            frontMatterId lines
            |> Option.defaultWith (fun () ->
                let stem = IO.Path.GetFileNameWithoutExtension fileName
                match stem.IndexOf("--", ordinal) with
                | -1 -> stem
                | index -> stem.Substring(0, index))

        let rec scan index (acc: DerivedCriterion list) =
            if index + 1 >= lines.Length then
                List.rev acc
            else
                let line = lines[index].Trim()

                match (if line.StartsWith "|" then cells line else []) with
                | first :: rest when first.Equals("ID", caseless) && isSeparator lines[index + 1] ->
                    let kind =
                        match rest with
                        | header :: _ when header.Contains("acceptance", caseless) -> CriterionKind.Acceptance
                        | _ -> CriterionKind.Statement

                    let rows =
                        lines
                        |> Array.skip (index + 2)
                        |> Array.takeWhile (fun row -> row.Trim().StartsWith "|")

                    let criteria =
                        rows
                        |> Array.choose (fun row ->
                            match cells row with
                            | id :: textCells when id <> "" ->
                                Some
                                    { Requirement = requirement
                                      Id = id
                                      Text = String.Join(" | ", textCells)
                                      Kind = kind }
                            | _ -> None)
                        |> Array.toList

                    scan (index + 2 + rows.Length) (List.rev criteria @ acc)
                | _ -> scan (index + 1) acc

        scan 0 []

    let private finding code status requirement obligation message =
        { Code = code
          Status = status
          Requirement = requirement
          Obligation = obligation
          Message = message }

    let private isIsoDate (text: string) =
        fst (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None))

    let private blank (text: string) = String.IsNullOrWhiteSpace text

    let private joined (items: string list) = String.Join(", ", items)

    /// Resolves an approval reference for `target` (a requirement,
    /// obligation or conflict id): it must exist, be complete, carry an ISO
    /// date, and cover the target. Findings explain every refusal.
    let private approve (approvals: ApprovalRecord list) (requirement: string option) (obligation: string option) (targets: string list) (reference: string) =
        match approvals |> List.tryFind (fun a -> a.DecisionId = reference) with
        | None ->
            Error [ finding FindingCode.ApprovalMissing FindingStatus.Fail requirement obligation $"approval record '{reference}' does not exist; approvals are supplied by the approving authority and never generated" ]
        | Some approval ->
            let problems =
                [ if blank approval.Authority then "it names no approving authority"
                  if blank approval.Scope then "it states no scope"
                  if not (isIsoDate approval.Date) then $"its date '{approval.Date}' is not an ISO date (yyyy-MM-dd)"
                  if not (targets |> List.exists (fun t -> approval.Covers |> List.contains t)) then
                      let wanted = String.Join(" or ", targets)
                      $"it does not cover {wanted}" ]

            match problems with
            | [] -> Ok approval
            | _ ->
                let reasons = String.concat "; " problems
                Error [ finding FindingCode.ApprovalInvalid FindingStatus.Fail requirement obligation $"approval record '{reference}' is not valid here: {reasons}" ]

    /// The requirement pattern of the document a requirement lives in.
    let private patternOf (baseline: Baseline) (requirement: SourceRequirement) =
        baseline.Documents
        |> List.tryFind (fun d -> d.Path = requirement.Document)
        |> Option.bind _.RequirementPattern

    /// Self-consistency of the baseline: unique ids, verbatim obligation
    /// text, tokens drawn from the source, value obligations with tokens.
    let private validateBaseline (baseline: Baseline) (observations: SourceObservation list) =
        let duplicates ids =
            ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

        let invalid requirement obligation message =
            finding FindingCode.BaselineInvalid FindingStatus.Fail requirement obligation message

        let pinnedText (requirement: SourceRequirement) =
            observations
            |> List.tryFind (fun o -> o.Path = requirement.Document)
            |> Option.bind _.Text
            |> Option.bind (section (patternOf baseline requirement) requirement.Heading)
            |> Option.filter (fun s -> sha256Text s = requirement.SectionSha256)

        [ for id in duplicates (baseline.Requirements |> List.map _.Id) do
              yield invalid (Some id) None $"requirement '{id}' is inventoried more than once"
          for id in duplicates (baseline.Requirements |> List.collect (fun r -> r.Obligations |> List.map _.Id)) do
              yield invalid None (Some id) $"obligation '{id}' is inventoried more than once"
          for requirement in baseline.Requirements do
              if not (baseline.Documents |> List.exists (fun d -> d.Path = requirement.Document)) then
                  yield invalid (Some requirement.Id) None $"requirement '{requirement.Id}' names document '{requirement.Document}', which the baseline does not pin"
              if requirement.Obligations.IsEmpty then
                  yield invalid (Some requirement.Id) None $"requirement '{requirement.Id}' lists no mandatory obligations"
              for obligation in requirement.Obligations do
                  let at = Some requirement.Id, Some obligation.Id
                  if obligation.Kind = ObligationKind.Value && obligation.Tokens.IsEmpty then
                      yield invalid (fst at) (snd at) $"value obligation '{obligation.Id}' lists no exact tokens"
                  for token in obligation.Tokens do
                      if blank token then yield invalid (fst at) (snd at) $"obligation '{obligation.Id}' has an empty token"
                      elif not (obligation.Text.Contains(token, caseless)) then
                          yield invalid (fst at) (snd at) $"token '{token}' of obligation '{obligation.Id}' does not occur in its verbatim text"
                  // Verbatim text is checked against the pinned section only;
                  // a changed section is reported as drift instead.
                  match pinnedText requirement with
                  | Some text when not (text.Contains(normalize obligation.Text, ordinal)) ->
                      yield invalid (fst at) (snd at) $"obligation '{obligation.Id}' text is not verbatim in the source section '{requirement.Heading}'"
                  | _ -> () ]

    let private drift (baseline: Baseline) (observations: SourceObservation list) =
        let observed path = observations |> List.tryFind (fun o -> o.Path = path) |> Option.bind _.Text

        let documents =
            baseline.Documents
            |> List.map (fun document ->
                { Document = document
                  ObservedSha256 = observed document.Path |> Option.map sha256Text })

        let findings =
            [ for outcome in documents do
                  let document = outcome.Document
                  match outcome.ObservedSha256 with
                  | None ->
                      yield finding FindingCode.SourceDocumentMissing FindingStatus.Drift None None $"source document '{document.Path}' is missing from the source root"
                  | Some sha when sha <> document.Sha256 ->
                      yield finding FindingCode.SourceDocumentChanged FindingStatus.Drift None None $"source document '{document.Path}' hashes to {sha}, not the pinned {document.Sha256}; reconcile the baseline before deriving"
                  | Some _ -> ()

                  match observed document.Path, document.RequirementPattern with
                  | Some text, Some pattern ->
                      let inventoried =
                          baseline.Requirements |> List.filter (fun r -> r.Document = document.Path) |> List.map _.Id |> Set.ofList

                      for entry in sections (Some pattern) text do
                          match entry.Id with
                          | Some id when not (inventoried.Contains id) ->
                              yield finding FindingCode.UninventoriedRequirement FindingStatus.Drift (Some id) None $"source requirement '{id}' ({entry.Heading}) in '{document.Path}' is not in the baseline inventory"
                          | _ -> ()
                  | _ -> ()

              for requirement in baseline.Requirements do
                  match observed requirement.Document with
                  | None -> ()
                  | Some text ->
                      match section (patternOf baseline requirement) requirement.Heading text with
                      | None ->
                          yield finding FindingCode.SourceSectionMissing FindingStatus.Drift (Some requirement.Id) None $"source section '{requirement.Heading}' no longer exists in '{requirement.Document}'"
                      | Some s when sha256Text s <> requirement.SectionSha256 ->
                          yield finding FindingCode.SourceSectionChanged FindingStatus.Drift (Some requirement.Id) None $"source section '{requirement.Heading}' changed since the pinned baseline ({sha256Text s}, pinned {requirement.SectionSha256})"
                      | Some _ -> () ]

        documents, findings

    let private evaluateObligation
        (criteria: Map<CriterionRef, DerivedCriterion>)
        (approvals: ApprovalRecord list)
        (requirement: SourceRequirement)
        (obligation: Obligation)
        (mapping: ObligationMapping option)
        =
        let at code status message = finding code status (Some requirement.Id) (Some obligation.Id) message

        match mapping with
        | None ->
            { Obligation = obligation
              Criteria = []
              Tokens = []
              Decision = Decision.Undecided
              Status = Some FindingStatus.Fail },
            [ at FindingCode.ObligationUnmapped FindingStatus.Fail $"obligation '{obligation.Id}' ({obligation.Text}) is not mapped to any derived criterion" ],
            []
        | Some { Coverage = ObligationCoverage.Excluded(reference, rationale) } ->
            match approve approvals (Some requirement.Id) (Some obligation.Id) [ obligation.Id; requirement.Id ] reference with
            | Error findings ->
                { Obligation = obligation
                  Criteria = []
                  Tokens = []
                  Decision = Decision.Exclusion reference
                  Status = Some FindingStatus.Fail },
                findings,
                []
            | Ok approval ->
                { Obligation = obligation
                  Criteria = []
                  Tokens = []
                  Decision = Decision.Exclusion reference
                  Status = Some FindingStatus.Info },
                [ at FindingCode.ExclusionApproved FindingStatus.Info $"obligation '{obligation.Id}' is excluded under approval '{approval.DecisionId}' by {approval.Authority} ({approval.Scope}): {rationale}" ],
                [ { Requirement = requirement.Id
                    Obligation = Some obligation.Id
                    Approval = approval } ]
        | Some { Coverage = ObligationCoverage.Mapped(references, review) } ->
            let resolved = references |> List.map (fun r -> r, criteria |> Map.tryFind r)

            let missingFindings =
                resolved
                |> List.choose (fun (r, found) ->
                    match found with
                    | None -> Some(at FindingCode.CriterionNotFound FindingStatus.Fail $"obligation '{obligation.Id}' maps to '{CriterionRef.render r}', which is not a criterion of the derived package")
                    | Some _ -> None)

            let found = resolved |> List.choose snd

            let carriers =
                match obligation.Kind with
                | ObligationKind.Acceptance -> found |> List.filter (fun c -> c.Kind = CriterionKind.Acceptance)
                | _ -> found

            let acceptanceFindings =
                if obligation.Kind = ObligationKind.Acceptance && not found.IsEmpty && carriers.IsEmpty then
                    [ at FindingCode.AcceptanceCriterionMissing FindingStatus.Fail $"acceptance obligation '{obligation.Id}' is mapped only to non-acceptance criteria" ]
                else
                    []

            let emptyFindings =
                if references.IsEmpty then
                    [ at FindingCode.ObligationUnmapped FindingStatus.Fail $"obligation '{obligation.Id}' has a mapping with no derived criteria" ]
                else
                    []

            let tokens =
                obligation.Tokens
                |> List.map (fun token ->
                    { Token = token
                      Found = carriers |> List.exists (fun c -> c.Text.Contains(token, caseless)) })

            let missingTokens = tokens |> List.filter (fun t -> not t.Found) |> List.map _.Token

            let tokenFindings =
                match missingTokens with
                | [] -> []
                | missing ->
                    let listed = String.Join(", ", missing |> List.map (sprintf "'%s'"))
                    let mapped = String.Join(", ", references |> List.map CriterionRef.render)

                    match obligation.Kind with
                    | ObligationKind.Value ->
                        [ at FindingCode.ValueTokenMissing FindingStatus.Fail $"value obligation '{obligation.Id}' is weakened or falsely equated: {listed} missing from {mapped}" ]
                    | _ -> [ at FindingCode.TokenMissing FindingStatus.Fail $"obligation '{obligation.Id}' is not carried: {listed} missing from {mapped}" ]

            let structural = emptyFindings @ missingFindings @ acceptanceFindings @ tokenFindings

            let collapse (text: string) = Regex.Replace(text, @"\s+", " ").Trim()
            // Short fragments ("hypotheses") occur everywhere, so verbatim
            // carriage decides only an obligation of at least five words.
            let verbatim =
                (collapse obligation.Text).Split(' ').Length >= 5
                && carriers |> List.exists (fun c -> (collapse c.Text).Contains(collapse obligation.Text, ordinal))

            let decision, reviewFindings =
                match obligation.Tokens, review with
                | [], _ when verbatim -> Decision.Verbatim, []
                | [], Some r when not (blank r.Reviewer) && not (blank r.Basis) && isIsoDate r.Date ->
                    Decision.Review r,
                    [ at FindingCode.EquivalenceReviewed FindingStatus.Info $"obligation '{obligation.Id}' has no deterministic tokens; accepted by the equivalence review of {r.Reviewer} on {r.Date}: {r.Basis}" ]
                | [], Some r ->
                    Decision.Undecided,
                    [ at FindingCode.EquivalenceUnverified FindingStatus.NeedsReview $"obligation '{obligation.Id}' has no deterministic tokens and its review is incomplete (reviewer '{r.Reviewer}', date '{r.Date}'); a reviewer must record equivalence" ]
                | [], None ->
                    Decision.Undecided,
                    [ at FindingCode.EquivalenceUnverified FindingStatus.NeedsReview $"obligation '{obligation.Id}' has no deterministic tokens; whether its mapped criteria carry it needs a recorded review" ]
                | _ -> Decision.Tokens, []

            let findings = if structural.IsEmpty then reviewFindings else structural

            let status =
                if not structural.IsEmpty then Some FindingStatus.Fail
                else reviewFindings |> List.tryHead |> Option.map _.Status

            { Obligation = obligation
              Criteria = references
              Tokens = tokens
              Decision = decision
              Status = status },
            findings,
            []

    let private mappingFindings (requirement: SourceRequirement) (mappings: ObligationMapping list) =
        let known = requirement.Obligations |> List.map _.Id |> Set.ofList

        [ for m in mappings do
              if not (known.Contains m.Obligation) then
                  yield finding FindingCode.ObligationUnknown FindingStatus.Fail (Some requirement.Id) (Some m.Obligation) $"mapping names obligation '{m.Obligation}', which requirement '{requirement.Id}' does not inventory"
          for id, count in mappings |> List.countBy _.Obligation do
              if count > 1 then
                  yield finding FindingCode.ObligationDuplicate FindingStatus.Fail (Some requirement.Id) (Some id) $"obligation '{id}' is mapped more than once; list every carrying criterion in one mapping" ]

    let private verdictOf (findings: Finding list) =
        if findings |> List.exists (fun f -> f.Status = FindingStatus.Fail || f.Status = FindingStatus.Drift) then Verdict.Fail
        elif findings |> List.exists (fun f -> f.Status = FindingStatus.Blocked || f.Status = FindingStatus.NeedsReview) then Verdict.Blocked
        else Verdict.Pass

    let private evaluateRequirement criteria approvals (entry: RequirementDisposition option) (requirement: SourceRequirement) =
        let at code status message = finding code status (Some requirement.Id) None message

        let obligations mappings =
            let results =
                requirement.Obligations
                |> List.map (fun o -> evaluateObligation criteria approvals requirement o (mappings |> List.tryFind (fun m -> m.Obligation = o.Id)))

            results |> List.map (fun (o, _, _) -> o),
            mappingFindings requirement mappings @ (results |> List.collect (fun (_, f, _) -> f)),
            results |> List.collect (fun (_, _, e) -> e)

        let unmapped () =
            requirement.Obligations
            |> List.map (fun o ->
                { Obligation = o
                  Criteria = []
                  Tokens = []
                  Decision = Decision.Undecided
                  Status = None })

        let outcomes, findings, exclusions =
            match entry |> Option.map _.Disposition with
            | None ->
                unmapped (),
                [ at FindingCode.DispositionMissing FindingStatus.Fail $"mandatory source requirement '{requirement.Id}' ({requirement.Title}, {requirement.Priority}) has no disposition in the derived package" ],
                []
            | Some(Disposition.Preserved mappings) -> obligations mappings
            | Some(Disposition.Strengthened(mappings, rationale)) ->
                let o, f, e = obligations mappings

                o,
                (if blank rationale then [ at FindingCode.DispositionInvalid FindingStatus.Fail $"strengthened disposition of '{requirement.Id}' states no rationale" ] else []) @ f,
                e
            | Some(Disposition.ExcludedWithApproval(reference, rationale)) ->
                match approve approvals (Some requirement.Id) None [ requirement.Id ] reference with
                | Error f -> unmapped (), f, []
                | Ok approval ->
                    unmapped (),
                    [ at FindingCode.ExclusionApproved FindingStatus.Info $"requirement '{requirement.Id}' is excluded under approval '{approval.DecisionId}' by {approval.Authority} ({approval.Scope}): {rationale}" ],
                    [ { Requirement = requirement.Id
                        Obligation = None
                        Approval = approval } ]
            | Some(Disposition.Superseded(replacement, reference)) ->
                let missing =
                    replacement
                    |> List.filter (fun r -> not (criteria.ContainsKey r))
                    |> List.map (fun r -> at FindingCode.CriterionNotFound FindingStatus.Fail $"replacement '{CriterionRef.render r}' for '{requirement.Id}' is not a criterion of the derived package")

                let empty =
                    if replacement.IsEmpty then [ at FindingCode.DispositionInvalid FindingStatus.Fail $"superseded disposition of '{requirement.Id}' names no replacement" ] else []

                match approve approvals (Some requirement.Id) None [ requirement.Id ] reference with
                | Error f -> unmapped (), empty @ missing @ f, []
                | Ok approval ->
                    unmapped (),
                    empty
                    @ missing
                    @ [ at FindingCode.SupersessionApproved FindingStatus.Info $"requirement '{requirement.Id}' is superseded by {joined (replacement |> List.map CriterionRef.render)} under approval '{approval.DecisionId}' by {approval.Authority}" ],
                    []
            | Some(Disposition.NotApplicable(justification, reference)) ->
                if blank justification then
                    unmapped (), [ at FindingCode.NotApplicableUnjustified FindingStatus.Fail $"not-applicable disposition of '{requirement.Id}' gives no justification" ], []
                else
                    match reference with
                    | None ->
                        unmapped (),
                        [ at FindingCode.NotApplicableUnapproved FindingStatus.NeedsReview $"requirement '{requirement.Id}' is declared not applicable ({justification}); applicability is a judgement, so it needs an approval record" ],
                        []
                    | Some r ->
                        match approve approvals (Some requirement.Id) None [ requirement.Id ] r with
                        | Error f -> unmapped (), f, []
                        | Ok approval ->
                            unmapped (),
                            [ at FindingCode.NotApplicableApproved FindingStatus.Info $"requirement '{requirement.Id}' is not applicable ({justification}) under approval '{approval.DecisionId}' by {approval.Authority}" ],
                            [ { Requirement = requirement.Id
                                Obligation = None
                                Approval = approval } ]

        { Requirement = requirement
          Disposition = entry |> Option.map _.Disposition
          Obligations = outcomes
          Verdict = verdictOf findings },
        findings,
        exclusions

    let private evaluateConflicts (baseline: Baseline) (approvals: ApprovalRecord list) (conflicts: DeclaredConflict list) =
        let known = baseline.Requirements |> List.map _.Id |> Set.ofList

        conflicts
        |> List.map (fun conflict ->
            let unknown = conflict.Requirements |> List.filter (known.Contains >> not)

            let invalid =
                [ if conflict.Requirements.Length < 2 then
                      finding FindingCode.ConflictInvalid FindingStatus.Fail None None $"conflict '{conflict.Id}' names fewer than two requirements"
                  for id in unknown do
                      finding FindingCode.ConflictInvalid FindingStatus.Fail (Some id) None $"conflict '{conflict.Id}' names '{id}', which the baseline does not inventory" ]

            let resolved, findings =
                match conflict.Decision with
                | None ->
                    false,
                    [ finding FindingCode.ConflictUnresolved FindingStatus.Blocked None None $"conflict '{conflict.Id}' between {joined conflict.Requirements} is pending a decision: {conflict.Description}" ]
                | Some reference ->
                    match approve approvals None None [ conflict.Id ] reference with
                    | Error f -> false, f
                    | Ok approval ->
                        true,
                        [ finding FindingCode.ConflictResolved FindingStatus.Info None None $"conflict '{conflict.Id}' is decided by '{approval.DecisionId}' ({approval.Authority}, {approval.Date})" ]

            { Conflict = conflict; Resolved = resolved }, invalid @ findings)

    /// The gate. Deterministic: the same inputs always give the same report.
    let evaluate
        (baseline: Baseline)
        (observations: SourceObservation list)
        (dispositions: DispositionSet)
        (approvals: ApprovalRecord list)
        (derived: DerivedCriterion list)
        : PreservationReport =
        let documents, driftFindings = drift baseline observations
        let baselineFindings = validateBaseline baseline observations

        let identityFindings =
            [ if dispositions.BaselineId <> baseline.Id || dispositions.BaselineCommit <> baseline.Commit then
                  finding
                      FindingCode.BaselineMismatch
                      FindingStatus.Fail
                      None
                      None
                      $"the disposition reconciles baseline '{dispositions.BaselineId}' at {dispositions.BaselineCommit}, not '{baseline.Id}' at {baseline.Commit}" ]

        let derivedFindings =
            derived
            |> List.countBy (fun c -> c.Requirement, c.Id)
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun ((requirement, id), _) ->
                finding FindingCode.DerivedCriterionDuplicate FindingStatus.Fail None None $"derived criterion '{requirement}#{id}' is defined more than once")

        let criteria =
            derived
            |> List.map (fun c -> { Requirement = c.Requirement; Criterion = c.Id }, c)
            |> Map.ofList

        let known = baseline.Requirements |> List.map _.Id |> Set.ofList

        let entryFindings =
            [ for id, count in dispositions.Entries |> List.countBy _.Requirement do
                  if count > 1 then
                      yield finding FindingCode.DispositionDuplicate FindingStatus.Fail (Some id) None $"requirement '{id}' has more than one disposition"
                  if not (known.Contains id) then
                      yield finding FindingCode.DispositionUnknownRequirement FindingStatus.Fail (Some id) None $"the disposition names '{id}', which the baseline does not inventory" ]

        let requirementResults =
            baseline.Requirements
            |> List.map (fun r -> evaluateRequirement criteria approvals (dispositions.Entries |> List.tryFind (fun e -> e.Requirement = r.Id)) r)

        let conflictResults = evaluateConflicts baseline approvals (baseline.Conflicts @ dispositions.Conflicts)

        let findings =
            baselineFindings
            @ identityFindings
            @ driftFindings
            @ derivedFindings
            @ entryFindings
            @ (requirementResults |> List.collect (fun (_, f, _) -> f))
            @ (conflictResults |> List.collect snd)

        { Verdict = verdictOf findings
          Documents = documents
          // A requirement's verdict counts every finding about it, including
          // drift and conflicts found outside its disposition.
          Requirements =
            requirementResults
            |> List.map (fun (r, _, _) ->
                { r with Verdict = findings |> List.filter (fun f -> f.Requirement = Some r.Requirement.Id) |> verdictOf })
          Exclusions = requirementResults |> List.collect (fun (_, _, e) -> e)
          Conflicts = conflictResults |> List.map fst
          Findings = findings }
