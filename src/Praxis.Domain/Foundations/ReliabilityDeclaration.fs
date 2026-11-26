namespace Praxis.Domain.Foundations

open System
open System.Text.RegularExpressions

/// One tenet's declared applicability. Enumerated fields stay text: they are
/// what the declaration says, and `ReliabilityDeclaration.assess` decides
/// whether they are legal.
type ReliabilityTenet =
    { Applicability: string option
      Reason: string option }

/// One evidence reference of a requirement family: its class and reference.
type ReliabilityEvidence =
    { Class: string option
      Ref: string option }

/// One requirement family's declared compliance state and evidence.
type ReliabilityFamily =
    { Family: string option
      State: string option
      Evidence: ReliabilityEvidence list }

/// A documented exception to one requirement (PRX-REL-006).
type ReliabilityException =
    { Requirement: string option
      Rationale: string option
      CompensatingControls: string option
      Approver: string option
      Decision: string option
      Expires: string option }

/// A pinned shared reliability capability (PRX-REL-009).
type ReliabilityDependency =
    { Capability: string option
      Pin: string option }

/// Design evidence references for continuous availability (PRX-REL-007).
type ReliabilityDesignEvidence =
    { Decision: string option
      ServiceLevels: string option
      FailureModes: string option }

/// A repository's reliability applicability declaration
/// (`.echelon/reliability.json`, schema `echelon.reliability/v1`,
/// RQ-ROS-2026-A038). `ShapeProblems` are the structural problems the reader
/// met (wrong JSON types, unknown properties); the reader never guesses.
type ReliabilityDeclarationDocument =
    { Schema: string option
      SystemClass: string option
      /// Tenet key -> its declaration, or None when the key is absent.
      Tenets: (string * ReliabilityTenet option) list
      Families: ReliabilityFamily list
      Exceptions: ReliabilityException list
      DesignEvidence: ReliabilityDesignEvidence
      MigrationPlan: string option
      Dependencies: ReliabilityDependency list
      ShapeProblems: string list }

/// What the verifier found the declaration file to be.
type ReliabilityInput =
    | DeclarationAbsent
    /// The file exists but is not a JSON object; the text says why.
    | DeclarationUnreadable of string
    | DeclarationParsed of ReliabilityDeclarationDocument

/// Whether the declaration is present, absent, or present but invalid (PRX-REL-001).
type ReliabilityStatus =
    | Absent
    | Present
    | Invalid

/// One report-only finding: the PRX-REL requirement it reports against, its
/// code suffix (ECHELON-FND-RELIABILITY-<suffix>), message and remediation.
/// Every finding is informational: verification is report-only, as draft
/// decision DF-ROS-2026-A076 proposes.
type ReliabilityFinding =
    { Requirement: string
      Suffix: string
      Message: string
      Remediation: string }

/// A requirement family as reported: the declared state and the effective
/// state after evidence checks. A claim the evidence does not support is
/// reported as `unverified`, never as compliant (PRX-REL-004).
type ReliabilityFamilyResult =
    { Family: string
      Declared: string
      Effective: string
      Reasons: string list }

type ReliabilityAssessment =
    { Status: ReliabilityStatus
      SystemClass: string option
      Tenets: (string * string option) list
      Families: ReliabilityFamilyResult list
      Findings: ReliabilityFinding list }

/// The report-only rules of RQ-ROS-2026-A038. Pure: the caller reads the
/// file and supplies a repository-path existence predicate and today's date;
/// this module only decides.
[<RequireQualifiedAccess>]
module ReliabilityDeclaration =
    [<Literal>]
    let SchemaId = "echelon.reliability/v1"

    [<Literal>]
    let RelativePath = ".echelon/reliability.json"

    let systemClasses =
        [ "transactional-application"
          "interactive-application"
          "stateless-service"
          "background-processing-service"
          "developer-tooling"
          "shared-library"
          "static-website"
          "research-repository"
          "governance-repository"
          "other" ]

    let tenetKeys = [ "continuousAvailability"; "cacheReadiness"; "reliabilityByConstruction" ]
    let applicabilities = [ "applicable"; "partially-applicable"; "not-applicable" ]
    let families = [ "EAR-API"; "EAR-CMD"; "EAR-AVL"; "EAR-QRY"; "EAR-QCS"; "EAR-MSG"; "EAR-PRS"; "EAR-SEC"; "EAR-OBS" ]
    let states = [ "compliant"; "partially-compliant"; "noncompliant"; "not-applicable"; "unverified" ]
    let evidenceClasses = [ "automated"; "integration-test"; "operational"; "architectural-review" ]

    /// States that claim compliance and so need resolvable evidence (PRX-REL-004).
    let claimingStates = [ "compliant"; "partially-compliant" ]

    let private requirementPattern = Regex(@"^EAR-[A-Z]{3}-\d{3}$", RegexOptions.CultureInvariant)
    let private commitPattern = Regex(@"(^|[^0-9a-fA-F])[0-9a-fA-F]{40}([^0-9a-fA-F]|$)", RegexOptions.CultureInvariant)
    let private schemePattern = Regex(@"^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.CultureInvariant)

    let private show (allowed: string list) = String.Join("|", allowed)

    let private filled (value: string option) =
        value |> Option.exists (String.IsNullOrWhiteSpace >> not)

    let private finding requirement suffix message remediation =
        { Requirement = requirement
          Suffix = suffix
          Message = message
          Remediation = remediation }

    /// Suffixes that make the declaration invalid rather than merely incomplete.
    let private invalidSuffixes = set [ "002"; "003"; "004"; "005"; "008"; "009" ]

    /// How an evidence reference resolves: a repository path that exists, an
    /// immutable external reference (a URL naming a full commit or a sha256
    /// digest), or neither (with the reason).
    type EvidenceResolution =
        | RepositoryPath of string
        | ImmutableExternal of string
        | Unresolved of string

    /// A repository-relative path with any `#fragment` removed, or None when
    /// the reference is absolute or escapes the repository.
    let repositoryPath (reference: string) =
        let withoutFragment =
            match reference.IndexOf '#' with
            | -1 -> reference
            | index -> reference.Substring(0, index)

        let normalized = withoutFragment.Replace('\\', '/')

        let escapes =
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.fold (fun depth segment ->
                match segment with
                | _ when depth < 0 -> depth
                | "." -> depth
                | ".." -> depth - 1
                | _ -> depth + 1) 0
            |> (fun depth -> depth < 0)

        if String.IsNullOrWhiteSpace normalized
           || normalized.StartsWith("/", StringComparison.Ordinal)
           || (normalized.Length > 1 && normalized[1] = ':')
           || escapes then
            None
        else
            Some normalized

    let resolveEvidence (exists: string -> bool) (reference: string) =
        if schemePattern.IsMatch reference then
            if commitPattern.IsMatch reference || reference.Contains("sha256:", StringComparison.OrdinalIgnoreCase) then
                ImmutableExternal reference
            else
                Unresolved $"external reference '{reference}' does not name an immutable commit or digest"
        else
            match repositoryPath reference with
            | None -> Unresolved $"'{reference}' is not a repository-relative path"
            | Some path when exists path -> RepositoryPath path
            | Some path -> Unresolved $"repository path '{path}' does not exist"

    let private familyName (family: ReliabilityFamily) = family.Family |> Option.defaultValue "(unnamed family)"

    /// PRX-REL-004/005: a family's effective state and its findings.
    let private assessFamily (exists: string -> bool) (family: ReliabilityFamily) =
        let name = familyName family
        let declared = family.State |> Option.defaultValue "(none)"

        let shape =
            [ if not (family.Family |> Option.exists (fun value -> List.contains value families)) then
                  yield finding "PRX-REL-004" "008" $"family '{name}' is not one of {show families}." "Name a published requirement family."
              if not (family.State |> Option.exists (fun value -> List.contains value states)) then
                  yield finding "PRX-REL-004" "008" $"family '{name}' state '{declared}' is not one of {show states}." "Declare one of the five compliance states."
              for evidence in family.Evidence do
                  if not (evidence.Class |> Option.exists (fun value -> List.contains value evidenceClasses)) then
                      let shown = evidence.Class |> Option.defaultValue "(none)"
                      yield finding "PRX-REL-005" "008" $"family '{name}' evidence class '{shown}' is not one of {show evidenceClasses}." "State the evidence class."
                  if not (filled evidence.Ref) then
                      yield finding "PRX-REL-004" "008" $"family '{name}' has an evidence entry without a ref." "Give every evidence entry a ref." ]

        let claims = family.State |> Option.exists (fun value -> List.contains value claimingStates)

        let references = family.Evidence |> List.choose (fun evidence -> evidence.Ref |> Option.filter (String.IsNullOrWhiteSpace >> not))
        let resolutions = references |> List.map (resolveEvidence exists)
        let unresolved = resolutions |> List.choose (function Unresolved reason -> Some reason | _ -> None)

        let downgrade =
            if not claims then []
            elif references.IsEmpty then [ "no evidence reference" ]
            elif not unresolved.IsEmpty then unresolved
            else []

        let claimFindings =
            [ if claims && references.IsEmpty then
                  yield
                      finding
                          "PRX-REL-004"
                          "006"
                          $"family '{name}' declares '{declared}' without evidence; it is reported as unverified."
                          "Reference at least one evidence path in the repository, or an immutable external reference, or declare the family unverified."
              for reason in (if claims then unresolved else []) do
                  yield
                      finding
                          "PRX-REL-004"
                          "007"
                          $"family '{name}' declares '{declared}' but {reason}; it is reported as unverified."
                          "Point the evidence ref at a file committed in this repository, or at a URL naming a full commit or sha256 digest." ]

        let effective = if downgrade.IsEmpty then declared else "unverified"

        { Family = name
          Declared = declared
          Effective = effective
          Reasons = downgrade },
        shape @ claimFindings

    let private parseDate (text: string) =
        match DateOnly.TryParseExact(text, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, value -> Some value
        | _ -> None

    /// PRX-REL-006 (complete, unexpired exceptions) and PRX-REL-008 (the
    /// requirement identifier matches the published pattern).
    let private assessException (today: DateOnly) (index: int) (item: ReliabilityException) =
        let label = item.Requirement |> Option.defaultValue $"exception #{index + 1}"

        let missing =
            [ "requirement", item.Requirement
              "rationale", item.Rationale
              "compensatingControls", item.CompensatingControls
              "approver", item.Approver
              "decision", item.Decision
              "expires", item.Expires ]
            |> List.filter (snd >> filled >> not)
            |> List.map fst

        [ if not missing.IsEmpty then
              let fields = String.Join(", ", missing)
              yield finding "PRX-REL-006" "009" $"exception '{label}' is missing {fields}." "Exceptions name the requirement, rationale, compensating controls, approver, decision record and expiry date."
          match item.Requirement with
          | Some requirement when filled item.Requirement && not (requirementPattern.IsMatch requirement) ->
              yield finding "PRX-REL-008" "009" $"exception requirement '{requirement}' does not match EAR-[A-Z]{{3}}-\\d{{3}}." "Reference a published EAR requirement identifier."
          | _ -> ()
          match item.Expires |> Option.filter (String.IsNullOrWhiteSpace >> not) with
          | Some text ->
              match parseDate text with
              | None -> yield finding "PRX-REL-006" "009" $"exception '{label}' expiry '{text}' is not a yyyy-MM-dd date." "Write the expiry as an ISO date."
              | Some expires when expires < today ->
                  yield finding "PRX-REL-006" "010" $"exception '{label}' expired on {text}." "Renew the exception with a new decision, or remove it and address the requirement."
              | Some _ -> ()
          | None -> () ]

    /// PRX-REL-009: a consumed shared capability is pinned.
    let private assessDependency (dependency: ReliabilityDependency) =
        let label = dependency.Capability |> Option.defaultValue "(unnamed capability)"

        [ if not (filled dependency.Capability) || not (filled dependency.Pin) then
              yield finding "PRX-REL-009" "013" $"dependency '{label}' must name its capability and a version or immutable digest pin." "Pin the shared reliability capability to a version or digest." ]

    let private applicabilityOf (tenet: ReliabilityTenet option) = tenet |> Option.bind _.Applicability

    let private assessDeclaration (exists: string -> bool) (today: DateOnly) (document: ReliabilityDeclarationDocument) =
        let shape =
            [ for problem in document.ShapeProblems do
                  yield finding "PRX-REL-001" "002" $"{RelativePath} {problem}." $"Make the declaration conform to {SchemaId} (requirements/echelon-reliability-v1.schema.json)."
              if document.Schema <> Some SchemaId then
                  let shown = document.Schema |> Option.defaultValue "(none)"
                  yield finding "PRX-REL-001" "002" $"{RelativePath} schema is '{shown}', not '{SchemaId}'." $"Declare \"schema\": \"{SchemaId}\"."
              if not (document.SystemClass |> Option.exists (fun value -> List.contains value systemClasses)) then
                  let shown = document.SystemClass |> Option.defaultValue "(none)"
                  yield finding "PRX-REL-002" "003" $"systemClass '{shown}' is not one of {show systemClasses}." "Declare exactly one system class." ]

        let tenetFindings =
            [ for key, tenet in document.Tenets do
                  match tenet with
                  | None -> yield finding "PRX-REL-003" "004" $"tenet '{key}' is not declared." $"Declare '{key}' as {show applicabilities}."
                  | Some declared ->
                      match declared.Applicability with
                      | Some value when List.contains value applicabilities ->
                          if value <> "applicable" && not (filled declared.Reason) then
                              yield finding "PRX-REL-003" "005" $"tenet '{key}' is '{value}' without a reason." "Anything other than applicable carries a non-empty reason."
                      | other ->
                          let shown = other |> Option.defaultValue "(none)"
                          yield finding "PRX-REL-003" "004" $"tenet '{key}' applicability '{shown}' is not one of {show applicabilities}." $"Declare '{key}' as {show applicabilities}." ]

        let assessed = document.Families |> List.map (assessFamily exists)
        let familyResults = assessed |> List.map fst
        let familyFindings = assessed |> List.collect snd
        let exceptionFindings = document.Exceptions |> List.mapi (assessException today) |> List.concat
        let dependencyFindings = document.Dependencies |> List.collect assessDependency

        let tenet key = document.Tenets |> List.tryPick (fun (name, value) -> if name = key then Some value else None) |> Option.flatten

        let continuousApplicable = applicabilityOf (tenet "continuousAvailability") = Some "applicable"

        let designFindings =
            if not continuousApplicable then
                []
            else
                [ "decision", document.DesignEvidence.Decision, "an architecture decision covering request classification and topology"
                  "serviceLevels", document.DesignEvidence.ServiceLevels, "an SLO/RPO/RTO declaration"
                  "failureModes", document.DesignEvidence.FailureModes, "a failure-mode matrix" ]
                |> List.filter (fun (_, value, _) -> not (filled value))
                |> List.map (fun (field, _, what) ->
                    finding
                        "PRX-REL-007"
                        "011"
                        $"continuousAvailability is applicable but designEvidence.{field} ({what}) is not referenced."
                        $"Reference {what} in designEvidence.{field}; Praxis reports a missing reference and never supplies one.")

        let anyApplicable =
            document.Tenets |> List.exists (fun (_, value) -> applicabilityOf value = Some "applicable")

        // Not all-compliant: no family is declared, or some family's effective
        // state is neither compliant nor not-applicable.
        let allCompliant =
            not familyResults.IsEmpty
            && familyResults |> List.forall (fun family -> family.Effective = "compliant" || family.Effective = "not-applicable")

        let migrationFindings =
            if anyApplicable && not allCompliant && not (filled document.MigrationPlan) then
                [ finding
                      "PRX-REL-010"
                      "012"
                      "a tenet is applicable and not every requirement family is (effectively) compliant, but no migrationPlan is named."
                      "Name a migration plan reference (work item or document) in migrationPlan." ]
            else
                []

        let findings = shape @ tenetFindings @ familyFindings @ exceptionFindings @ designFindings @ migrationFindings @ dependencyFindings
        let invalid = findings |> List.exists (fun item -> invalidSuffixes.Contains item.Suffix)

        { Status = (if invalid then Invalid else Present)
          SystemClass = document.SystemClass
          Tenets = document.Tenets |> List.map (fun (key, value) -> key, applicabilityOf value)
          Families = familyResults
          Findings = findings }

    /// Assess the declaration. `exists` answers whether a repository-relative
    /// path exists; `today` dates exception expiry. Report-only: the caller
    /// must never fail verification on these findings.
    let assess (exists: string -> bool) (today: DateOnly) (input: ReliabilityInput) : ReliabilityAssessment =
        match input with
        | DeclarationAbsent ->
            { Status = Absent
              SystemClass = None
              Tenets = []
              Families = []
              Findings =
                [ finding
                      "PRX-REL-001"
                      "001"
                      $"{RelativePath} is absent; this repository has not declared its reliability applicability (report-only)."
                      $"Add {RelativePath} ({SchemaId}); requirements/APPLICATION-RELIABILITY-DECLARATION.md has a minimal example." ] }
        | DeclarationUnreadable reason ->
            { Status = Invalid
              SystemClass = None
              Tenets = []
              Families = []
              Findings = [ finding "PRX-REL-001" "002" $"{RelativePath} is invalid: {reason}." $"Write {RelativePath} as a JSON object conforming to {SchemaId}." ] }
        | DeclarationParsed document -> assessDeclaration exists today document
