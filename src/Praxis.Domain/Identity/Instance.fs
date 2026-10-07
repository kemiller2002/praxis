namespace Praxis.Domain.Identity

open System
open System.Text.RegularExpressions

// The Praxis instance identity (requirements/PRAXIS-DUAL-ENTRY-
// RECONCILIATION.md items 16, 17, 22-24; PRAXIS-ID-04). An instance is one
// installation of Praxis in one repository. Its identity is distinct from
// the repository, agent, session, work-item and transaction identities; it
// is generated and stored locally (`.praxis/instance.json`) before any
// registration, and the local record is authoritative. Pure: generation of
// the random value and the file are effects at the edge.

[<Struct>]
type InstanceId = private InstanceId of string

[<RequireQualifiedAccess>]
module InstanceId =
    let private pattern = Regex("^[A-Za-z0-9][A-Za-z0-9._:-]{2,127}\z", RegexOptions.CultureInvariant)

    let tryCreate (value: string) =
        if not (isNull value) && pattern.IsMatch value then Some(InstanceId value) else None

    let value (InstanceId id) = id

    /// A new identity from a random GUID supplied by the caller.
    let ofGuid (value: Guid) = InstanceId("pxi-" + value.ToString("N"))

/// An identity this instance replaced, and why (an explicit, auditable
/// migration; requirement 23).
type InstancePredecessor =
    { InstanceId: InstanceId
      Reason: string
      ReplacedAt: DateTimeOffset }

/// `.praxis/instance.json` (`praxis.instance/1`).
type InstanceRecord =
    { InstanceId: InstanceId
      /// `None` only for a record written before `praxis.instance/1`.
      CreatedAt: DateTimeOffset option
      /// The Praxis version that created it (informational; upgrades never
      /// change the identity).
      CreatedWith: string
      /// The repository the instance was created in, as then observed or
      /// configured. A copy into another repository is detected against it.
      Repository: RepositoryIdentity option
      Predecessors: InstancePredecessor list }

/// How the local instance record relates to the repository it is in now
/// (requirement 24).
[<RequireQualifiedAccess>]
type InstanceBinding =
    /// The record's repository is this repository (same stable ID, or the
    /// same locator when no stable ID is known). A clone of the same
    /// repository, a rename and a transfer are all bound.
    | Bound
    /// The record names another repository: it was copied by a template,
    /// fork or file copy. It is not this repository's identity.
    | Foreign of recorded: RepositoryIdentity * current: RepositoryIdentity
    /// Neither side has a stable ID and the locators differ (or one is
    /// unknown): a rename cannot be told from a copy.
    | Unverified of reason: string

/// The local instance as a consumer (telemetry, reconciliation, validate)
/// sees it.
[<RequireQualifiedAccess>]
type LocalInstance =
    | Missing
    | Unreadable of reason: string
    | Present of InstanceRecord * InstanceBinding

[<RequireQualifiedAccess>]
module InstanceBinding =
    let assess (record: InstanceRecord) (current: RepositoryIdentity option) =
        match record.Repository, current with
        | None, _ -> InstanceBinding.Unverified "the instance record names no repository"
        | Some _, None -> InstanceBinding.Unverified "this repository's identity cannot be observed"
        | Some recorded, Some current ->
            match RepositoryIdentity.compare recorded current with
            | RepositoryMatch.Same
            | RepositoryMatch.UnverifiedSameLocator -> InstanceBinding.Bound
            | RepositoryMatch.Different -> InstanceBinding.Foreign(recorded, current)
            | RepositoryMatch.Undetermined ->
                InstanceBinding.Unverified
                    $"the instance was created in {RepositoryIdentity.display recorded} and this repository appears as {RepositoryIdentity.display current}, without a stable ID to tell a rename from a copy"

    let code binding =
        match binding with
        | InstanceBinding.Bound -> "bound"
        | InstanceBinding.Foreign _ -> "foreign"
        | InstanceBinding.Unverified _ -> "unverified"

[<RequireQualifiedAccess>]
module LocalInstance =
    /// The instance ID this repository may stamp on new records: never a
    /// foreign one (requirement 24).
    let authoritativeId (local: LocalInstance) =
        match local with
        | LocalInstance.Present(record, (InstanceBinding.Bound | InstanceBinding.Unverified _)) -> Some record.InstanceId
        | _ -> None

    /// Findings for `validate`: `(isError, message)`.
    let findings (local: LocalInstance) : (bool * string) list =
        match local with
        | LocalInstance.Missing ->
            [ false, "this repository has no Praxis instance identity (.praxis/instance.json); create it with 'praxis instance init' (DER-16, DER-17)" ]
        | LocalInstance.Unreadable reason -> [ true, $".praxis/instance.json is unreadable: {reason}" ]
        | LocalInstance.Present(_, InstanceBinding.Foreign(recorded, current)) ->
            [ true,
              $"the Praxis instance identity was created in {RepositoryIdentity.display recorded}, not in {RepositoryIdentity.display current}; a template, fork or copy must not reuse it: run 'praxis instance init --reinitialize --reason TEXT' (DER-24)" ]
        | LocalInstance.Present(_, InstanceBinding.Unverified _)
        | LocalInstance.Present(_, InstanceBinding.Bound) -> []

/// What `praxis instance init` (and `init`/`upgrade`) decide.
[<RequireQualifiedAccess>]
type InstanceInitDecision =
    /// Write a new record.
    | Create of InstanceRecord
    /// Keep the existing record unchanged: an upgrade or a repeated init
    /// never replaces an identity.
    | Keep of InstanceRecord
    /// Refused, nothing written.
    | Refuse of reason: string

[<RequireQualifiedAccess>]
module InstanceInit =
    /// `existing` is what the store holds; `reinitialize` is the explicit,
    /// auditable migration with its reason.
    let decide
        (existing: LocalInstance)
        (newId: InstanceId)
        (now: DateTimeOffset)
        (version: string)
        (repository: RepositoryIdentity option)
        (reinitialize: string option)
        =
        let fresh predecessors =
            { InstanceId = newId
              CreatedAt = Some now
              CreatedWith = version
              Repository = repository
              Predecessors = predecessors }

        match existing, reinitialize with
        | LocalInstance.Missing, None -> InstanceInitDecision.Create(fresh [])
        | LocalInstance.Missing, Some _ -> InstanceInitDecision.Refuse "there is no instance identity to reinitialize; run 'praxis instance init' without --reinitialize"
        | LocalInstance.Unreadable reason, _ ->
            InstanceInitDecision.Refuse $".praxis/instance.json exists but is unreadable ({reason}); repair or remove it deliberately, Praxis never overwrites it"
        | LocalInstance.Present(record, _), None -> InstanceInitDecision.Keep record
        | LocalInstance.Present(_, _), Some reason when String.IsNullOrWhiteSpace reason ->
            InstanceInitDecision.Refuse "--reinitialize needs --reason TEXT stating why the identity is replaced"
        | LocalInstance.Present(record, _), Some reason ->
            InstanceInitDecision.Create(
                fresh (
                    { InstanceId = record.InstanceId
                      Reason = reason.Trim()
                      ReplacedAt = now }
                    :: record.Predecessors
                )
            )

/// Whether an optional Echelon integration is usable (requirement 20).
/// Unknown and unavailable are ordinary states, never failures.
[<RequireQualifiedAccess>]
type IntegrationAvailability =
    | Available
    | Unavailable of reason: string
    | NotConfigured
    | Unknown

[<RequireQualifiedAccess>]
module IntegrationAvailability =
    let code availability =
        match availability with
        | IntegrationAvailability.Available -> "available"
        | IntegrationAvailability.Unavailable _ -> "unavailable"
        | IntegrationAvailability.NotConfigured -> "not-configured"
        | IntegrationAvailability.Unknown -> "unknown"

/// The discoverable projection of an instance (requirements 19, 20, 25,
/// 26). It is built from an explicit allow-list of fields, so nothing
/// else -- credentials, environment, local paths, agent-private data --
/// can reach it. It is a projection: the local record stays authoritative.
type InstanceProjection =
    { InstanceId: InstanceId
      Repository: RepositoryIdentity option
      PraxisVersion: string
      ReconciliationProtocolVersion: string
      RemoteProtocolVersion: string
      Capabilities: string list
      Integrations: (string * IntegrationAvailability) list }

[<RequireQualifiedAccess>]
module InstanceProjection =
    let create (record: InstanceRecord) (repository: RepositoryIdentity option) praxisVersion reconciliation remote capabilities integrations =
        { InstanceId = record.InstanceId
          Repository = repository |> Option.orElse record.Repository
          PraxisVersion = praxisVersion
          ReconciliationProtocolVersion = reconciliation
          RemoteProtocolVersion = remote
          Capabilities = capabilities |> List.distinct |> List.sort
          Integrations = integrations |> List.sortBy fst }

    /// The registration operation ID: a digest of the projection, so a retry
    /// of the same projection is the same operation (idempotent; requirement
    /// 19) and a changed projection is a new one.
    let operationId (projection: InstanceProjection) =
        let material =
            String.concat
                "\n"
                [ yield InstanceId.value projection.InstanceId
                  yield projection.Repository |> Option.bind RepositoryIdentity.repositoryId |> Option.defaultValue ""
                  yield projection.Repository |> Option.bind _.Locator |> Option.map RepositoryLocator.value |> Option.defaultValue ""
                  yield projection.PraxisVersion
                  yield projection.ReconciliationProtocolVersion
                  yield projection.RemoteProtocolVersion
                  yield! projection.Capabilities
                  for name, availability in projection.Integrations do
                      yield name + "=" + IntegrationAvailability.code availability ]

        let digest =
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes material)
            |> Convert.ToHexString

        "praxis-instance-" + digest.ToLowerInvariant().Substring(0, 32)
