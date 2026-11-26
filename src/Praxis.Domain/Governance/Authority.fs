namespace Praxis.Domain.Governance

open System
open Praxis.Domain.Autonomy

/// `praxis.governance-authorities/1`: who may authorize what, read only from
/// the protected branch. Logins are platform-authenticated account names.
type GovernanceAuthorities =
    { /// May create authorized work (a work item on the protected branch).
      WorkAuthorizers: string list
      /// May approve changes to protected classes (trust roots, verification
      /// policy, workflows, governance) for one exact candidate.
      ElevatedApprovers: string list
      /// May grant governance exceptions.
      ExceptionAuthorities: string list
      /// Platform identities that are agents or automation. They can never
      /// act as an elevated approver or exception authority, whatever list
      /// they also appear on.
      AgentIdentities: string list }

/// A bounded category of routine work an owner pre-authorizes once.
type StandingCategory =
    { Name: string
      /// Every changed path must match one of these.
      AllowedPaths: string list
      MaxChangedFiles: int }

/// `praxis.standing-authorization/1`: routine work agents may perform
/// without a per-change human approval. It never covers a protected class.
type StandingAuthorization =
    { Id: string
      Repositories: string list
      Categories: StandingCategory list
      ProhibitedPaths: string list
      ApprovedBy: string
      ValidFrom: DateTimeOffset
      ExpiresAt: DateTimeOffset
      RevokedAt: DateTimeOffset option }

/// An elevated approval bound to one exact candidate head, observed on the
/// platform (for example an owner's pull-request comment
/// `/praxis approve-elevated <sha>`).
type ElevatedApproval =
    { Approver: string
      CandidateHead: string
      ApprovedAt: DateTimeOffset
      /// The platform reports the action was made through an app on the
      /// approver's behalf (an agent acting as the owner). Never accepted.
      ViaApplication: string option
      /// The platform shows the comment was edited after it was posted.
      /// Any writer can edit another's comment, so an edited approval is
      /// never accepted.
      Edited: bool
      Reason: string }

/// A work authorizer's `/praxis authorize-work ID` on the pull request, or an
/// entry of the protected `.praxis/governance/work-authorizations.json`.
type WorkAuthorization =
    { Authorizer: string
      WorkItem: string
      AuthorizedAt: DateTimeOffset
      ViaApplication: string option
      Edited: bool }

/// `praxis.governance-exception/1`.
type GovernanceException =
    { Id: string
      Repository: string
      /// Exact candidate heads the exception covers; never open-ended.
      CandidateHeads: string list
      /// The refusal codes it waives.
      Waives: string list
      Reason: string
      AuthorizedBy: string
      /// The implementing principal of the covered work.
      Implementer: string
      CompensatingSafeguards: string list
      ResidualRisk: string
      FollowUp: string list
      GrantedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module Authority =
    /// Governance artifacts that sit outside the autonomy floor but decide
    /// what verification means. Changing any requires elevated approval.
    let governancePaths =
        [ { Pattern = ".praxis/trust/**"; Class = ProtectedClass.VerificationPolicy }
          { Pattern = ".praxis/governance/**"; Class = ProtectedClass.RepositoryRules }
          { Pattern = ".praxis/validation-policy.json"; Class = ProtectedClass.VerificationPolicy }
          { Pattern = ".praxis/standing-authorization.json"; Class = ProtectedClass.ExecutionPolicy }
          { Pattern = ".echelon/ros.json"; Class = ProtectedClass.VerificationPolicy } ]

    /// The protected classes a change touches, with the reason for each.
    let protectedChanges (ciReferenced: string list) (changes: ChangedPath list) =
        Classification.classify governancePaths ciReferenced changes

    let private isAgent (authorities: GovernanceAuthorities) (login: string) =
        authorities.AgentIdentities |> List.exists (fun agent -> String.Equals(agent, login, StringComparison.OrdinalIgnoreCase))

    let private listed (logins: string list) (login: string) =
        logins |> List.exists (fun candidate -> String.Equals(candidate, login, StringComparison.OrdinalIgnoreCase))

    /// An approval names the candidate by its full object ID. A short prefix
    /// is refused: an implementer can grind two commits sharing a 48-bit
    /// prefix in seconds and swap the approved one for the other.
    let namesHead (named: string) (candidateHead: string) =
        String.Equals(named, candidateHead, StringComparison.OrdinalIgnoreCase)

    /// Whether `approval` authorizes protected changes in `candidateHead`.
    let elevated (authorities: GovernanceAuthorities) (candidateHead: string) (approval: ElevatedApproval) =
        if not (namesHead approval.CandidateHead candidateHead) then Error $"the approval by {approval.Approver} names {approval.CandidateHead}, not the candidate"
        elif approval.Edited then Error $"the approval by {approval.Approver} was edited after it was posted"
        elif approval.ViaApplication.IsSome then Error $"the approval by {approval.Approver} was made through the application '{approval.ViaApplication.Value}', not by the person"
        elif isAgent authorities approval.Approver then Error $"{approval.Approver} is an agent identity"
        elif not (listed authorities.ElevatedApprovers approval.Approver) then Error $"{approval.Approver} is not an elevated approver"
        else Ok approval

    /// Whether a work authorization is acceptable: a listed work authorizer,
    /// not an agent identity, posted in person and not edited.
    let workAuthorized (authorities: GovernanceAuthorities) (authorization: WorkAuthorization) =
        not authorization.Edited
        && authorization.ViaApplication.IsNone
        && not (isAgent authorities authorization.Authorizer)
        && listed authorities.WorkAuthorizers authorization.Authorizer

    /// Which standing authorization, if any, covers a change.
    let standing (policies: StandingAuthorization list) (repository: string) (at: DateTimeOffset) (changedPaths: string list) =
        let active (policy: StandingAuthorization) =
            policy.RevokedAt.IsNone
            && Window.contains policy.ValidFrom (Some policy.ExpiresAt) at
            && listed policy.Repositories repository

        policies
        |> List.filter active
        |> List.tryPick (fun policy ->
            if changedPaths |> List.exists (GovernancePath.matchesAny policy.ProhibitedPaths) then
                None
            else
                policy.Categories
                |> List.tryFind (fun category ->
                    not changedPaths.IsEmpty
                    && changedPaths.Length <= category.MaxChangedFiles
                    && changedPaths |> List.forall (GovernancePath.matchesAny category.AllowedPaths))
                |> Option.map (fun category -> policy, category))

    /// Refusal codes an exception may never waive: identity, forgery and the
    /// exception mechanism itself. Receipt and validation codes may be
    /// waived, but the resulting merge is labelled authorized-by-exception,
    /// never verified.
    let nonWaivable =
        set
            [ "MRG.REPOSITORY_MISMATCH"
              "MRG.TARGET_MISMATCH"
              "MRG.RECEIPT_FORGED"
              "MRG.EXCEPTION_INVALID"
              "MRG.TARGET_HEAD_MISMATCH"
              "MRG.PROTECTED_CHANGE_UNAUTHORIZED" ]

    /// Validate an exception for one candidate. Fails closed on any missing
    /// element of the exception record.
    let validateException (authorities: GovernanceAuthorities) (repository: string) (candidateHead: string) (at: DateTimeOffset) (record: GovernanceException) =
        let problems =
            [ if record.Repository <> repository then "it names another repository"
              if not (List.contains candidateHead record.CandidateHeads) then "it does not name this exact candidate"
              if record.Waives.IsEmpty then "it waives nothing"
              for code in record.Waives do
                  if nonWaivable.Contains code then $"{code} cannot be waived"
              if String.IsNullOrWhiteSpace record.Reason then "it states no reason"
              if record.CompensatingSafeguards.IsEmpty then "it names no compensating safeguard"
              if String.IsNullOrWhiteSpace record.ResidualRisk then "it states no residual risk"
              if record.FollowUp.IsEmpty then "it records no follow-up obligation"
              if not (Window.contains record.GrantedAt (Some record.ExpiresAt) at) then "it is not in force now"
              if (record.ExpiresAt - record.GrantedAt) > TimeSpan.FromDays 30.0 then "its validity exceeds 30 days"
              if not (listed authorities.ExceptionAuthorities record.AuthorizedBy) then $"{record.AuthorizedBy} is not an exception authority"
              if isAgent authorities record.AuthorizedBy then $"{record.AuthorizedBy} is an agent identity"
              if String.Equals(record.AuthorizedBy, record.Implementer, StringComparison.OrdinalIgnoreCase) then
                  "the implementer cannot authorize its own exception" ]

        if problems.IsEmpty then Ok record else Error(String.concat "; " problems)
