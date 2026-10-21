namespace Praxis.Application.Work

open System
open Praxis.Domain.Work

/// The completion-policy members of `ros.json` `workProtocol` (REL-GATE-004).
type CompletionPolicy =
    { RequireVerifiedCompletion: bool
      RequireAttestedEvidence: bool
      AllowUnattestedEvidence: bool
      /// `workProtocol.trustedVerifierKeyIds`: the verifier keys (I4 `keyid`,
      /// `sha256:` over the key's SPKI) an attested authority may trust.
      /// `None` when absent.
      TrustedVerifierKeyIds: string list option
      /// `workProtocol.upstreamSync` remote and branch: where the protected
      /// base is looked up.
      Upstream: string * string }

/// Where the protected policy was read from (REL-GATE-004).
[<RequireQualifiedAccess>]
type ProtectedPolicySource =
    /// `ros.json` at the merge base of HEAD and the configured upstream branch.
    | Base of commit: string * policy: Result<CompletionPolicy, string>
    /// No upstream branch to compare with: every readable committed version of
    /// `ros.json` in HEAD's history, newest first.
    | History of versions: (string * CompletionPolicy) list
    /// Git could not answer at all.
    | Unavailable of reason: string

/// REL-GATE-004: the completion policy is decided against a protected source,
/// not the worktree being completed, and an attested authority may trust only
/// verifier keys that protected policy pins. Pure: every input is already read.
[<RequireQualifiedAccess>]
module ProtectedCompletionPolicy =
    let AttestationRequiredCode = "PRX.COMPLETION.ATTESTATION_REQUIRED"
    let PolicyChangedCode = "PRX.COMPLETION.POLICY_CHANGED"
    let PolicyUnavailableCode = "PRX.COMPLETION.POLICY_UNAVAILABLE"
    let UntrustedAuthorityCode = "PRX.COMPLETION.UNTRUSTED_AUTHORITY"

    let empty =
        { RequireVerifiedCompletion = false
          RequireAttestedEvidence = false
          AllowUnattestedEvidence = false
          TrustedVerifierKeyIds = None
          Upstream = "origin", "main" }

    let private finding code detail : CompletionFinding = { Code = code; Detail = detail }

    /// The fixed locator the protected base is first looked up with
    /// (REL-GATE-004): the remote's default branch when `origin/HEAD`
    /// names one, else origin/main. Never the worktree's or the candidate
    /// commit's `upstreamSync`, which the completing process controls.
    let bootstrapLocator (originHead: (string * string) option) : string * string =
        originHead |> Option.defaultValue empty.Upstream

    /// `refs/remotes/origin/HEAD`'s symbolic target as a locator, if it is one.
    let parseOriginHead (target: string) : (string * string) option =
        let prefix = "refs/remotes/origin/"
        let target = target.Trim()

        if target.StartsWith(prefix, StringComparison.Ordinal) && target.Length > prefix.Length then
            Some("origin", target.Substring prefix.Length)
        else
            None

    /// The one re-location allowed: when the protected policy found at the
    /// bootstrap base configures another upstream, look there exactly once.
    let relocation (bootstrap: string * string) (protectedPolicy: CompletionPolicy) : (string * string) option =
        if protectedPolicy.Upstream = bootstrap then None else Some protectedPolicy.Upstream

    /// Why the policy requires attested evidence, if it does (REL-GATE-003):
    /// `requireAttestedEvidence`, or `requireVerifiedCompletion` without the
    /// `allowUnattestedEvidence` opt-out.
    let attestationRequirement (requireAttested: bool) (requireVerified: bool) (allowUnattested: bool) : string option =
        if requireAttested then
            Some "ros.json workProtocol.requireAttestedEvidence"
        elif requireVerified && not allowUnattested then
            Some "ros.json workProtocol.requireVerifiedCompletion (which implies requireAttestedEvidence unless workProtocol.allowUnattestedEvidence is true)"
        else
            None

    let requirement (policy: CompletionPolicy) =
        attestationRequirement policy.RequireAttestedEvidence policy.RequireVerifiedCompletion policy.AllowUnattestedEvidence

    /// Whether `policy` constrains which verifier keys may be trusted: it pins
    /// keys, or it requires attestation (then no pin means no key is trusted).
    let private constrainsTrust (policy: CompletionPolicy) = policy.TrustedVerifierKeyIds.IsSome || (requirement policy).IsSome

    let private trusted (policy: CompletionPolicy) = policy.TrustedVerifierKeyIds |> Option.defaultValue []

    /// Every way `candidate` is weaker than `protectedPolicy`: a requirement
    /// dropped, the opt-out added, or a verifier key trusted that the protected
    /// policy does not trust. Strengthening is never a weakening.
    let weakenings (protectedPolicy: CompletionPolicy) (candidate: CompletionPolicy) : string list =
        [ if protectedPolicy.RequireVerifiedCompletion && not candidate.RequireVerifiedCompletion then
              "workProtocol.requireVerifiedCompletion was removed"
          if protectedPolicy.RequireAttestedEvidence && not candidate.RequireAttestedEvidence then
              "workProtocol.requireAttestedEvidence was removed"
          if not protectedPolicy.AllowUnattestedEvidence && candidate.AllowUnattestedEvidence then
              "workProtocol.allowUnattestedEvidence was added"
          if constrainsTrust protectedPolicy then
              if not (constrainsTrust candidate) then
                  "workProtocol.trustedVerifierKeyIds was removed"
              else
                  match trusted candidate |> List.filter (fun key -> not (List.contains key (trusted protectedPolicy))) with
                  | [] -> ()
                  | added -> $"""workProtocol.trustedVerifierKeyIds trusts {String.Join(", ", added)}, which protected policy does not""" ]

    /// The effective policy: the worktree's, once it is shown to be no weaker
    /// than the protected source. A malformed worktree policy keeps the
    /// REL-GATE-003 behaviour (fails closed as ATTESTATION_REQUIRED); an
    /// unreadable protected source fails closed; any weakening, or a moved
    /// upstream locator, is a policy change.
    let decide (worktree: Result<CompletionPolicy, string>) (source: ProtectedPolicySource) : Result<CompletionPolicy, CompletionFinding> =
        let changed where reasons =
            Error(
                finding
                    PolicyChangedCode
                    $"""ros.json changes the completion policy relative to {where}: {String.Join("; ", (reasons: string list))}; a completion policy change must land on the protected branch through review before it applies"""
            )

        match worktree with
        | Error message -> Error(finding AttestationRequiredCode message)
        | Ok candidate ->
            match source with
            | ProtectedPolicySource.Unavailable reason ->
                Error(finding PolicyUnavailableCode $"the protected completion policy could not be read ({reason}); completion fails closed")
            | ProtectedPolicySource.Base(commit, Error message) ->
                Error(finding PolicyUnavailableCode $"ros.json at the protected base {commit} is unreadable ({message}); completion fails closed")
            | ProtectedPolicySource.Base(commit, Ok protectedPolicy) ->
                let remote, branch = protectedPolicy.Upstream

                match weakenings protectedPolicy candidate with
                | [] when protectedPolicy.Upstream = candidate.Upstream -> Ok candidate
                | [] -> changed $"the protected base {commit}" [ $"workProtocol.upstreamSync moved from {remote}/{branch}" ]
                | reasons -> changed $"the protected base {commit} ({remote}/{branch})" reasons
            | ProtectedPolicySource.History versions ->
                versions
                |> List.tryPick (fun (commit, earlier) ->
                    match weakenings earlier candidate with
                    | [] -> None
                    | reasons -> Some(commit, reasons))
                |> function
                    | None -> Ok candidate
                    | Some(commit, reasons) -> changed $"the version committed in {commit} (no upstream branch to compare with, so every committed version binds)" reasons

    /// Whether the bound authority may be trusted under the effective policy.
    /// When attestation is required the authority must pin verifier keys
    /// (ATTESTATION_REQUIRED), live outside the repository and its Git
    /// directory, and trust only keys the policy pins (UNTRUSTED_AUTHORITY).
    /// A pin applies whenever it is present.
    let trust (policy: CompletionPolicy) (authorityKeyIds: string list) (authorityInside: string option) : Result<unit, CompletionFinding> =
        let pinned = trusted policy
        let unpinned = authorityKeyIds |> List.filter (fun key -> not (List.contains key pinned)) |> List.distinct

        match requirement policy with
        | Some source when authorityKeyIds.IsEmpty ->
            Error(finding AttestationRequiredCode $"{source} requires a host authority that pins TrustedVerifierKeys; unattested evidence cannot complete this item")
        | Some source ->
            match authorityInside with
            | Some location ->
                Error(
                    finding
                        UntrustedAuthorityCode
                        $"{source} requires a host-held authority, but the completion authority resolves inside {location}, which the completing process can write"
                )
            | None when pinned.IsEmpty ->
                Error(
                    finding
                        UntrustedAuthorityCode
                        $"{source} requires attested evidence, but protected policy pins no verifier key (workProtocol.trustedVerifierKeyIds); a caller-supplied authority cannot be the trust root"
                )
            | None when not unpinned.IsEmpty ->
                Error(finding UntrustedAuthorityCode $"""the completion authority trusts verifier key(s) {String.Join(", ", unpinned)} that workProtocol.trustedVerifierKeyIds does not pin""")
            | None -> Ok()
        | None when policy.TrustedVerifierKeyIds.IsSome && not unpinned.IsEmpty ->
            Error(finding UntrustedAuthorityCode $"""the completion authority trusts verifier key(s) {String.Join(", ", unpinned)} that workProtocol.trustedVerifierKeyIds does not pin""")
        | None -> Ok()
