namespace Praxis.Domain.Governance

/// What a repository has for verified completion, observed from its
/// protected branch and the local environment.
type CompletionAuthorityState =
    { RequireVerifiedCompletion: bool
      RequireAttestedEvidence: bool
      PinnedKeyIds: string list
      TrustRoot: VerifierTrustRoot option
      TrustRootError: string option
      ValidationPolicyPresent: bool
      MergeGateWorkflowPresent: bool
      VerifyWorkflowPresent: bool
      /// The `--authority` / `PRAXIS_COMPLETION_AUTHORITY` file, if named.
      HostAuthorityNamed: bool
      HostVerifierSocketNamed: bool }

/// The brief's §20 diagnostic: which authority is missing, why, whether an
/// existing one can be reused, who can resolve it, the exact procedure,
/// what may continue and what stays blocked.
type CompletionAuthorityDiagnosis =
    { Ready: bool
      Path: string
      Missing: string list
      Why: string
      Reusable: string list
      ResolvedBy: Principal
      Procedure: string list
      MayContinue: string list
      Blocked: string list }

[<RequireQualifiedAccess>]
module AuthorityDiagnostics =
    let private continueSafely =
        [ "implementation, commits and pushes on a work branch"
          "work validate-run and repository tests (unattested evidence)"
          "opening a draft pull request that states verified completion is pending"
          "durable checkpoints (work checkpoint) and blocking with the reason" ]

    let private blocked =
        [ "work complete-verified and work finish (no trusted verifier)"
          "describing the work as verified complete"
          "merging where the owner's direction requires verified completion" ]

    let diagnose (state: CompletionAuthorityState) =
        let workloadVerifiers =
            state.TrustRoot
            |> Option.map (fun root -> root.Verifiers |> List.filter (fun v -> v.RevokedAt.IsNone && (match v.Identity with VerifierIdentity.Workload _ -> true | _ -> false)))
            |> Option.defaultValue []

        let ciPathReady = state.TrustRoot.IsSome && state.TrustRootError.IsNone && not workloadVerifiers.IsEmpty && state.ValidationPolicyPresent && state.VerifyWorkflowPresent && state.MergeGateWorkflowPresent
        let hostPathReady = not state.PinnedKeyIds.IsEmpty && state.HostAuthorityNamed && state.HostVerifierSocketNamed

        if ciPathReady || hostPathReady then
            { Ready = true
              Path = (if ciPathReady then "ci-workload" else "host-verifier")
              Missing = []
              Why = "a trusted verifier outside the implementer's control is provisioned"
              Reusable = []
              ResolvedBy = Principal.VerificationService
              Procedure = (if ciPathReady then [ "push the work branch and open a pull request declaring 'Praxis-Work-Item: ID'; the protected workflow verifies and the merge gate decides" ] else [ "praxis work finish --id ID --token TOKEN" ])
              MayContinue = []
              Blocked = [] }
        else
            let missing =
                [ match state.TrustRoot, state.TrustRootError with
                  | _, Some error -> $"the protected trust root .praxis/trust/verifiers.json is unusable: {error}"
                  | None, None -> "a completion authority trust root (.praxis/trust/verifiers.json on the protected branch)"
                  | Some _, None when workloadVerifiers.IsEmpty && state.PinnedKeyIds.IsEmpty -> "an unrevoked verifier in the trust root"
                  | _ -> ()
                  if not state.ValidationPolicyPresent then "the protected validation policy (.praxis/validation-policy.json)"
                  if not (state.VerifyWorkflowPresent && state.MergeGateWorkflowPresent) then
                      "the protected verification and merge-gate workflow (.github/workflows/praxis-merge-gate.yml)" ]

            let reusable =
                [ if not state.PinnedKeyIds.IsEmpty then
                      let ids = String.concat ", " state.PinnedKeyIds
                      $"pinned host verifier key(s) {ids}: usable from a host that holds the key (praxis verifier serve), not from this session"
                  "no other repository's authority can be reused: a trust root binds one repository, and copying one would trust a verifier this repository never approved" ]

            { Ready = false
              Path = "none"
              Missing = missing
              Why =
                "verified completion needs evidence signed by a verifier the implementer does not control; an authority created from a key this session holds would be self-attestation, which Praxis refuses to treat as verification"
              Reusable = reusable
              ResolvedBy = Principal.OrganizationOwner
              Procedure =
                [ "an agent runs `praxis governance bootstrap --repository OWNER/NAME --approved-by LOGIN --validation NAME=COMMAND` on a branch and opens a pull request (it writes the trust root, validation policy, authorities and the merge-gate workflow; it creates no key)"
                  "the owner reviews and merges that pull request (the one human action that establishes the trust root; mobile is sufficient)"
                  "the owner imports the default-branch ruleset the bootstrap wrote (.praxis/governance/ruleset-main.json) so the gate is required and administrators cannot bypass it"
                  "`praxis governance inspect` then reports the readiness level reached" ]
              MayContinue = continueSafely
              Blocked = blocked }
