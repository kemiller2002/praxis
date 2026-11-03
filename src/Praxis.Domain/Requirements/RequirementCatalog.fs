namespace Praxis.Domain.Requirements

open System
open System.Text.RegularExpressions

/// Field-level requirement identity and its compilation into a verification
/// manifest (REQ-RVM-001). A catalog (`praxis.requirement-catalog/1`) names
/// every individually verifiable field, control, action, navigation, rule,
/// contract, transition, outcome and accessibility obligation of an
/// application under a stable, authored ID. The compiler is pure: the same
/// catalog and source commit always give the same manifest, and it refuses a
/// catalog with any error rather than emitting a partial manifest.
///
/// IDs are authored, never derived from labels, so renaming a field keeps
/// its ID. An ID leaves a catalog only through a `retired` record, and an
/// ID dropped without one is reported against the previous manifest.

/// What kind of thing a requirement obliges the application to provide.
type RequirementKind =
    | UiField
    | UiControl
    | Action
    | Navigation
    | ValidationRule
    | DataContract
    | StateTransition
    | WorkflowOutcome
    | Accessibility

/// The five verification levels, in increasing strength.
type VerificationLevel =
    /// The required implementation mapping exists and the identity is valid.
    | V1
    /// The element is rendered, visible and accessible in the expected state.
    | V2
    /// A person can enter, select or activate the expected value.
    | V3
    /// Validation and outcomes behave as the requirement states.
    | V4
    /// Values survive save and reload, stay with the right entity, and stay isolated.
    | V5

/// One verification check a requirement can oblige. Each belongs to exactly one level.
type VerificationCheck =
    | Mapped
    | Exists
    | Visible
    | Accessible
    | Editable
    | Enter
    | Select
    | Activate
    | Validation
    | Rejects
    | Outcome
    | Save
    | Reload
    | Update
    | Isolation

/// Machine-checkable rule deciding whether a requirement applies. NOT_APPLICABLE
/// is only ever the result of a declared fact having the declared value; it is
/// never inferred from missing implementation.
type Applicability =
    | Always
    /// Applies only when every named fact has the stated value.
    | When of (string * bool) list

/// How many rendered elements may carry the ID in one active scope.
type Instancing =
    /// Exactly one element per active scope.
    | Single
    /// Repeated controls: each carries the ID plus a distinct instance key
    /// (`data-requirement-instance`), and the key's meaning is named here.
    | Repeated of key: string

type FieldRules =
    { MinLength: int option
      MaxLength: int option
      Pattern: string option
      /// For selections: the complete, ordered set of valid values.
      Options: string list }

/// The authoritative requirement this one decomposes (e.g. a SCREEN-CONTRACTS
/// section). Every catalog requirement must trace to at least one.
type SourceReference = { Document: string; Requirement: string }

/// A catalog requirement as written, before validation: every enumerated
/// member is still text, so an unsupported value is a finding, not a crash.
type CatalogRequirement =
    { Id: string
      Kind: string
      Screen: string
      Name: string
      Component: string option
      Required: bool
      Visible: bool
      Editable: bool
      Rules: FieldRules
      Verification: string list
      Sources: SourceReference list
      DependsOn: string list
      AppliesWhen: (string * bool) list
      Instances: string option }

type Retirement =
    { Id: string
      Reason: string
      ReplacedBy: string option
      /// The governance record that authorised the retirement.
      Authority: string }

type Screen = { Id: string; Route: string option }

type Catalog =
    { CatalogId: string
      /// Every requirement ID must match this pattern (e.g. `^INV-\d{3}$`).
      IdPattern: string
      Status: string
      Screens: Screen list
      /// The applicability facts a requirement may name.
      Facts: string list
      Requirements: CatalogRequirement list
      Retired: Retirement list }

/// An implementation mapping: where the application claims to implement an ID.
type ImplementationMapping = { RequirementId: string; Location: string }

type FindingSeverity =
    | CatalogError
    | CatalogWarning

type CatalogFinding =
    { Code: string
      Severity: FindingSeverity
      Requirement: string option
      Message: string }

/// One verification obligation of the manifest: requirement, level and check.
type ManifestObligation =
    { Id: string
      Level: VerificationLevel
      Check: VerificationCheck
      Mandatory: bool }

type ManifestRequirement =
    { Id: string
      Kind: RequirementKind
      Screen: string
      Name: string
      Component: string option
      Required: bool
      Visible: bool
      Editable: bool
      Rules: FieldRules
      Sources: SourceReference list
      DependsOn: string list
      Applicability: Applicability
      Instancing: Instancing
      Levels: VerificationLevel list
      Obligations: ManifestObligation list
      Mappings: string list }

type VerificationManifest =
    { CatalogId: string
      CatalogStatus: string
      SourceCommit: string
      CatalogSha256: string
      Screens: Screen list
      Facts: string list
      Requirements: ManifestRequirement list
      Retired: Retirement list }

[<RequireQualifiedAccess>]
module RequirementKind =
    let all =
        [ UiField; UiControl; Action; Navigation; ValidationRule; DataContract; StateTransition; WorkflowOutcome; Accessibility ]

    let text kind =
        match kind with
        | UiField -> "ui-field"
        | UiControl -> "ui-control"
        | Action -> "action"
        | Navigation -> "navigation"
        | ValidationRule -> "validation-rule"
        | DataContract -> "data-contract"
        | StateTransition -> "state-transition"
        | WorkflowOutcome -> "workflow-outcome"
        | Accessibility -> "accessibility"

    let tryParse (value: string) = all |> List.tryFind (fun kind -> text kind = value)

[<RequireQualifiedAccess>]
module VerificationLevel =
    let all = [ V1; V2; V3; V4; V5 ]

    let text level =
        match level with
        | V1 -> "V1"
        | V2 -> "V2"
        | V3 -> "V3"
        | V4 -> "V4"
        | V5 -> "V5"

    let tryParse (value: string) = all |> List.tryFind (fun level -> text level = value)

[<RequireQualifiedAccess>]
module VerificationCheck =
    let all =
        [ Mapped; Exists; Visible; Accessible; Editable; Enter; Select; Activate; Validation; Rejects; Outcome; Save; Reload; Update; Isolation ]

    let text check =
        match check with
        | Mapped -> "mapped"
        | Exists -> "exists"
        | Visible -> "visible"
        | Accessible -> "accessible"
        | Editable -> "editable"
        | Enter -> "enter"
        | Select -> "select"
        | Activate -> "activate"
        | Validation -> "validation"
        | Rejects -> "rejects"
        | Outcome -> "outcome"
        | Save -> "save"
        | Reload -> "reload"
        | Update -> "update"
        | Isolation -> "isolation"

    let tryParse (value: string) = all |> List.tryFind (fun check -> text check = value)

    let level check =
        match check with
        | Mapped
        | Exists -> V1
        | Visible
        | Accessible -> V2
        | Editable
        | Enter
        | Select
        | Activate -> V3
        | Validation
        | Rejects
        | Outcome -> V4
        | Save
        | Reload
        | Update
        | Isolation -> V5

    /// The checks a kind of requirement can meaningfully oblige. Anything else
    /// is a catalog error: an action cannot be "entered", a data contract has no
    /// visible element, and so on.
    let allowedFor kind =
        match kind with
        | UiField -> [ Mapped; Exists; Visible; Accessible; Editable; Enter; Select; Validation; Rejects; Save; Reload; Update; Isolation ]
        | UiControl -> [ Mapped; Exists; Visible; Accessible; Editable; Enter; Select; Activate; Outcome; Save; Reload ]
        | Action -> [ Mapped; Exists; Visible; Accessible; Activate; Outcome; Save; Reload; Update; Isolation ]
        | Navigation -> [ Mapped; Exists; Visible; Accessible; Activate; Outcome; Reload ]
        | ValidationRule -> [ Mapped; Validation; Rejects ]
        | DataContract -> [ Mapped; Validation; Rejects; Save; Reload; Update; Isolation ]
        | StateTransition -> [ Mapped; Activate; Outcome; Rejects; Save; Reload ]
        | WorkflowOutcome -> [ Mapped; Outcome; Save; Reload; Update; Isolation ]
        | Accessibility -> [ Mapped; Exists; Visible; Accessible ]

[<RequireQualifiedAccess>]
module RequirementCatalog =
    let private error code requirement message =
        { Code = code
          Severity = CatalogError
          Requirement = requirement
          Message = message }

    let private warning code requirement message =
        { Code = code
          Severity = CatalogWarning
          Requirement = requirement
          Message = message }

    let private ordinal (a: string) (b: string) = String.CompareOrdinal(a, b)

    let private duplicates (values: string list) =
        values |> List.countBy id |> List.filter (fun (_, count) -> count > 1) |> List.map fst |> List.sortWith ordinal

    let private tryRegex (pattern: string) =
        try
            Some(Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds 1.0))
        with :? ArgumentException ->
            None

    let private catalogFindings (catalog: Catalog) =
        [ if String.IsNullOrWhiteSpace catalog.CatalogId then
              error "missing-catalog-id" None "the catalog has no catalogId"
          if catalog.Requirements.IsEmpty then
              error "empty-catalog" None "the catalog lists no requirements"
          match tryRegex catalog.IdPattern with
          | None -> error "invalid-id-pattern" None $"idPattern '{catalog.IdPattern}' is not a valid regular expression"
          | Some _ -> ()
          for id in duplicates (catalog.Screens |> List.map _.Id) do
              error "duplicate-screen" None $"screen '{id}' is declared more than once"
          for id in duplicates catalog.Facts do
              error "duplicate-fact" None $"fact '{id}' is declared more than once"
          for id in duplicates (catalog.Requirements |> List.map _.Id |> List.filter (String.IsNullOrWhiteSpace >> not)) do
              error "duplicate-id" (Some id) $"requirement ID '{id}' is used by more than one requirement"
          for id in duplicates (catalog.Retired |> List.map _.Id) do
              error "duplicate-retirement" (Some id) $"requirement ID '{id}' is retired more than once" ]

    let private requirementFindings (catalog: Catalog) (pattern: Regex option) (requirement: CatalogRequirement) =
        let id = requirement.Id
        let subject = if String.IsNullOrWhiteSpace id then None else Some id
        let screens = catalog.Screens |> List.map _.Id |> Set.ofList
        let activeIds = catalog.Requirements |> List.map _.Id |> Set.ofList
        let retiredIds = catalog.Retired |> List.map _.Id |> Set.ofList
        let kind = RequirementKind.tryParse requirement.Kind
        let checks = requirement.Verification |> List.map (fun text -> text, VerificationCheck.tryParse text)

        [ if String.IsNullOrWhiteSpace id then
              error "missing-id" None $"the requirement named '{requirement.Name}' has no ID"
          else
              match pattern with
              | Some regex when not (regex.IsMatch id) ->
                  error "invalid-id" subject $"ID '{id}' does not match the catalog idPattern '{catalog.IdPattern}'"
              | _ -> ()
          if retiredIds.Contains id then
              error "retired-id-reused" subject $"ID '{id}' is retired and cannot name an active requirement"
          match kind with
          | None -> error "unsupported-requirement-type" subject $"type '{requirement.Kind}' is not a supported requirement type"
          | Some _ -> ()
          if String.IsNullOrWhiteSpace requirement.Name then
              error "missing-name" subject "the requirement has no name"
          if not (screens.Contains requirement.Screen) then
              error "invalid-reference" subject $"screen '{requirement.Screen}' is not declared in the catalog"
          for dependency in requirement.DependsOn do
              if not (activeIds.Contains dependency) then
                  error "invalid-reference" subject $"dependency '{dependency}' is not an active requirement of the catalog"
              elif dependency = id then
                  error "invalid-reference" subject "a requirement cannot depend on itself"
          if requirement.Sources.IsEmpty then
              error "missing-source" subject "the requirement does not trace to any authoritative requirement"
          if requirement.Verification.IsEmpty then
              error "missing-verification" subject "the requirement defines no verification obligation"
          for text in duplicates requirement.Verification do
              error "duplicate-verification" subject $"verification '{text}' is listed more than once"
          for text, parsed in checks do
              match parsed, kind with
              | None, _ -> error "unsupported-verification" subject $"verification '{text}' is not a supported verification type"
              | Some check, Some k when not (List.contains check (VerificationCheck.allowedFor k)) ->
                  error "unsupported-verification" subject $"verification '{text}' does not apply to a {RequirementKind.text k} requirement"
              | _ -> ()
          for fact, _ in requirement.AppliesWhen do
              if not (List.contains fact catalog.Facts) then
                  error "invalid-reference" subject $"applicability fact '{fact}' is not declared in the catalog facts"
          match requirement.Instances with
          | Some key when String.IsNullOrWhiteSpace key ->
              error "invalid-instancing" subject "a repeated requirement must name its instance key"
          | _ -> ()
          // Conflicts within one requirement.
          if requirement.Required && not requirement.Visible && List.exists (fun (_, c) -> c = Some Visible) checks then
              error "conflicting-requirement" subject "the requirement is not visible but obliges a visibility check"
          if not requirement.Editable && List.exists (fun (_, c) -> c = Some Editable || c = Some Enter) checks then
              error "conflicting-requirement" subject "the requirement is not editable but obliges an editability or entry check"
          match requirement.Rules.MinLength, requirement.Rules.MaxLength with
          | Some minimum, Some maximum when minimum > maximum ->
              error "conflicting-requirement" subject $"minLength {minimum} is greater than maxLength {maximum}"
          | Some minimum, _ when minimum < 0 -> error "conflicting-requirement" subject "minLength must not be negative"
          | _ -> ()
          match requirement.Rules.Pattern with
          | Some text when (tryRegex text).IsNone -> error "conflicting-requirement" subject $"validation pattern '{text}' is not a valid regular expression"
          | _ -> ()
          for value in duplicates requirement.Rules.Options do
              error "conflicting-requirement" subject $"option '{value}' is listed more than once"
          if List.exists (fun (_, c) -> c = Some Select) checks && requirement.Rules.Options.IsEmpty then
              error "conflicting-requirement" subject "a selection check needs the complete list of valid options"
          if List.exists (fun (_, c) -> c = Some Validation || c = Some Rejects) checks
             && requirement.Rules = { MinLength = None; MaxLength = None; Pattern = None; Options = [] } then
              error "conflicting-requirement" subject "a validation check needs a value rule: minLength, maxLength, pattern or options (required makes the requirement mandatory, not the value non-empty)"
          if requirement.Required && kind <> None && not (List.exists (fun (_, c) -> c = Some Mapped) checks) then
              warning "no-structural-obligation" subject "a required requirement without a 'mapped' check is never reconciled against an implementation mapping" ]

    let private crossFindings (catalog: Catalog) =
        let active = catalog.Requirements |> List.map _.Id |> Set.ofList
        let retired = catalog.Retired |> List.map _.Id |> Set.ofList

        [ // Two IDs naming the same thing on the same screen is a conflict, not two requirements.
          for (screen, name), group in catalog.Requirements |> List.groupBy (fun r -> r.Screen, r.Name.Trim().ToLowerInvariant()) do
              if group.Length > 1 then
                  let ids = group |> List.map _.Id |> List.sortWith ordinal |> String.concat ", "
                  error "conflicting-requirement" None $"requirements {ids} all name '{name}' on screen '{screen}'"
          for retirement in catalog.Retired do
              if String.IsNullOrWhiteSpace retirement.Reason || String.IsNullOrWhiteSpace retirement.Authority then
                  error "unauthorised-retirement" (Some retirement.Id) "a retirement must state its reason and the governance record that authorised it"
              match retirement.ReplacedBy with
              | Some replacement when not (active.Contains replacement) && not (retired.Contains replacement) ->
                  error "invalid-reference" (Some retirement.Id) $"replacement '{replacement}' is not a requirement of the catalog"
              | _ -> () ]

    /// Every finding for a catalog and an optional set of implementation mappings,
    /// in a deterministic order (errors first, then by code, requirement, message).
    let validate (catalog: Catalog) (mappings: ImplementationMapping list) : CatalogFinding list =
        let pattern = tryRegex catalog.IdPattern
        let active = catalog.Requirements |> List.map _.Id |> Set.ofList

        let mappingFindings =
            [ for mapping in mappings do
                  if not (active.Contains mapping.RequirementId) then
                      error "orphaned-mapping" (Some mapping.RequirementId) $"implementation mapping at '{mapping.Location}' names no active requirement" ]

        catalogFindings catalog
        @ (catalog.Requirements |> List.collect (requirementFindings catalog pattern))
        @ crossFindings catalog
        @ mappingFindings
        |> List.distinct
        |> List.sortWith (fun a b ->
            let severity f = if f.Severity = CatalogError then 0 else 1
            compare (severity a, a.Code, Option.defaultValue "" a.Requirement, a.Message) (severity b, b.Code, Option.defaultValue "" b.Requirement, b.Message))

    let hasErrors (findings: CatalogFinding list) =
        findings |> List.exists (fun f -> f.Severity = CatalogError)

    /// The obligation ID: `<requirement>/<level>/<check>`, stable while the requirement keeps its ID.
    let obligationId (requirement: string) (check: VerificationCheck) =
        $"{requirement}/{VerificationLevel.text (VerificationCheck.level check)}/{VerificationCheck.text check}"

    let private compileRequirement (mappings: ImplementationMapping list) (requirement: CatalogRequirement) : ManifestRequirement =
        let kind = RequirementKind.tryParse requirement.Kind |> Option.get

        let checks =
            requirement.Verification
            |> List.choose VerificationCheck.tryParse
            |> List.sortBy (fun check -> List.findIndex ((=) check) VerificationCheck.all)

        { Id = requirement.Id
          Kind = kind
          Screen = requirement.Screen
          Name = requirement.Name
          Component = requirement.Component
          Required = requirement.Required
          Visible = requirement.Visible
          Editable = requirement.Editable
          Rules = requirement.Rules
          Sources = requirement.Sources |> List.sortWith (fun a b -> ordinal (a.Document + "#" + a.Requirement) (b.Document + "#" + b.Requirement))
          DependsOn = requirement.DependsOn |> List.distinct |> List.sortWith ordinal
          Applicability =
            match requirement.AppliesWhen with
            | [] -> Always
            | facts -> When(facts |> List.sortWith (fun (a, _) (b, _) -> ordinal a b))
          Instancing =
            match requirement.Instances with
            | None -> Single
            | Some key -> Repeated key
          Levels = checks |> List.map VerificationCheck.level |> List.distinct
          Obligations =
            checks
            |> List.map (fun check ->
                { Id = obligationId requirement.Id check
                  Level = VerificationCheck.level check
                  Check = check
                  Mandatory = requirement.Required })
          Mappings =
            mappings
            |> List.filter (fun m -> m.RequirementId = requirement.Id)
            |> List.map _.Location
            |> List.distinct
            |> List.sortWith ordinal }

    /// Compile a valid catalog into its manifest, or return every finding when
    /// the catalog has any error. The manifest's requirement set is the
    /// catalog's, whatever the implementation maps: an unmapped requirement
    /// keeps every obligation (and fails its `mapped` check at verification).
    let compile
        (catalog: Catalog)
        (catalogSha256: string)
        (sourceCommit: string)
        (mappings: ImplementationMapping list)
        : Result<VerificationManifest * CatalogFinding list, CatalogFinding list> =
        let findings =
            validate catalog mappings
            @ [ if not (Regex.IsMatch(sourceCommit, "^[0-9a-f]{40}$")) then
                    error "invalid-source-commit" None $"source commit '{sourceCommit}' is not a full 40-character lowercase Git SHA" ]

        if hasErrors findings then
            Error findings
        else
            Ok(
                { CatalogId = catalog.CatalogId
                  CatalogStatus = catalog.Status
                  SourceCommit = sourceCommit
                  CatalogSha256 = catalogSha256
                  Screens = catalog.Screens |> List.sortWith (fun a b -> ordinal a.Id b.Id)
                  Facts = catalog.Facts |> List.sortWith ordinal
                  Requirements =
                    catalog.Requirements
                    |> List.map (compileRequirement mappings)
                    |> List.sortWith (fun a b -> ordinal a.Id b.Id)
                  Retired = catalog.Retired |> List.sortWith (fun a b -> ordinal a.Id b.Id) },
                findings
            )

    /// IDs a previous manifest named that the catalog neither keeps nor retires:
    /// a requirement deleted without an auditable record.
    let removedWithoutRetirement (previousIds: string list) (catalog: Catalog) : CatalogFinding list =
        let kept =
            (catalog.Requirements |> List.map _.Id) @ (catalog.Retired |> List.map _.Id) |> Set.ofList

        previousIds
        |> List.distinct
        |> List.sortWith ordinal
        |> List.filter (kept.Contains >> not)
        |> List.map (fun id ->
            error "removed-without-retirement" (Some id) $"requirement '{id}' was in the previous manifest but is neither kept nor retired")
