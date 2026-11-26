namespace Praxis.Domain.Governance

open System

// Enforceable governance (WI-0117, docs/governance/). Every module in this
// namespace is pure: Git, GitHub and the clock are the infrastructure's.
// The distinction this namespace exists to keep is between a governance
// *requirement* (a Markdown policy, a `ros.json` flag) and the platform or
// cryptographic *mechanism* that makes it hold. A requirement with no
// observed mechanism is reported as not enforced, never as satisfied.

/// The principals of the Praxis trust model (docs/governance/trust-model.md).
/// A finding names the principal who can resolve it, so a diagnostic tells
/// the owner whether an agent can continue or a person must act.
[<RequireQualifiedAccess>]
type Principal =
    | OrganizationOwner
    | RepositoryAdministrator
    | WorkAuthorizer
    | AgentImplementer
    | AutomatedImplementer
    | WorkCoordinator
    | VerificationService
    | ReleaseAuthority
    | ExceptionAuthority
    | HostingProvider
    | CiEnvironment
    | EvidenceStore

[<RequireQualifiedAccess>]
module Principal =
    let toWire principal =
        match principal with
        | Principal.OrganizationOwner -> "organization-owner"
        | Principal.RepositoryAdministrator -> "repository-administrator"
        | Principal.WorkAuthorizer -> "work-authorizer"
        | Principal.AgentImplementer -> "agent-implementer"
        | Principal.AutomatedImplementer -> "automated-implementer"
        | Principal.WorkCoordinator -> "work-coordinator"
        | Principal.VerificationService -> "verification-service"
        | Principal.ReleaseAuthority -> "release-authority"
        | Principal.ExceptionAuthority -> "exception-authority"
        | Principal.HostingProvider -> "hosting-provider"
        | Principal.CiEnvironment -> "ci-environment"
        | Principal.EvidenceStore -> "evidence-store"

    /// Whether an ordinary implementing agent may resolve a finding itself.
    /// Anything a person or a protected service must do is not agent work.
    let isAgentResolvable principal =
        match principal with
        | Principal.AgentImplementer
        | Principal.AutomatedImplementer
        | Principal.WorkCoordinator -> true
        | _ -> false

/// How strongly an identity is established. Only the first three are
/// authentication; `SelfReported` is provenance (docs/agent-provenance.md).
[<RequireQualifiedAccess>]
type AuthenticationBasis =
    /// The hosting provider authenticated the account that acted.
    | PlatformAccount
    /// A signature verified under a key the protected trust root pins.
    | PinnedKey
    /// A CI workload identity token verified against its issuer.
    | WorkloadIdentity
    /// Declared by the actor, cross-checked but not proven.
    | SelfReported

[<RequireQualifiedAccess>]
module AuthenticationBasis =
    let toWire basis =
        match basis with
        | AuthenticationBasis.PlatformAccount -> "platform-account"
        | AuthenticationBasis.PinnedKey -> "pinned-key"
        | AuthenticationBasis.WorkloadIdentity -> "workload-identity"
        | AuthenticationBasis.SelfReported -> "self-reported"

    let isAuthenticated basis = basis <> AuthenticationBasis.SelfReported

/// One reasoned governance finding: a stable code, what was observed, the
/// smallest supported remedy and who can apply it.
type GovernanceFinding =
    { Code: string
      Detail: string
      Remedy: string
      ResolvedBy: Principal }

[<RequireQualifiedAccess>]
module GovernanceFinding =
    let create code detail remedy resolvedBy =
        { Code = code; Detail = detail; Remedy = remedy; ResolvedBy = resolvedBy }

/// A fact read from a provider. `Unknown` is never treated as the safe value:
/// an unreadable rule is a rule Praxis cannot show is enforced.
[<RequireQualifiedAccess>]
type Observed<'value> =
    | Known of 'value
    | Unknown of reason: string

[<RequireQualifiedAccess>]
module Observed =
    let map f observed =
        match observed with
        | Observed.Known value -> Observed.Known(f value)
        | Observed.Unknown reason -> Observed.Unknown reason

    let defaultWith fallback observed =
        match observed with
        | Observed.Known value -> value
        | Observed.Unknown _ -> fallback

/// Path matching shared by governance modules: case-insensitive globs where
/// over-matching only ever makes a change more protected.
[<RequireQualifiedAccess>]
module GovernancePath =
    let isMatch (pattern: string) (path: string) =
        Praxis.Domain.Autonomy.Classification.isMatch pattern path

    let matchesAny (patterns: string list) (path: string) = patterns |> List.exists (fun pattern -> isMatch pattern path)

    /// Paths whose change after verification does not change the verified
    /// artifact: Praxis's own bookkeeping and committed receipts. Nothing
    /// else is exempt, so editing a test after verification invalidates it.
    let postVerificationExempt =
        [ ".ros/**"; ".praxis/receipts/**"; ".praxis/evidence/**" ]

    let isPostVerificationExempt (path: string) = matchesAny postVerificationExempt path

/// The instant arithmetic every validity window uses: `[from, until)`.
[<RequireQualifiedAccess>]
module Window =
    let contains (from: DateTimeOffset) (until: DateTimeOffset option) (instant: DateTimeOffset) =
        instant >= from
        && (match until with
            | Some limit -> instant < limit
            | None -> true)
