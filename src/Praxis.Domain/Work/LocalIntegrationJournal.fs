namespace Praxis.Domain.Work

open System

/// Proposed integration identity, never authority or native work completion.
type LocalIntegrationPlan =
    { SchemaVersion: string; RepositoryIdentity: string; DispatchId: string; AttemptId: string
      PacketDigest: string; SubmissionDigest: string; TargetRef: string
      BeforeCommit: string; CandidateCommit: string; PreparedAt: DateTimeOffset }
type LocalIntegrationJournal = { Plan: LocalIntegrationPlan; AppliedAt: DateTimeOffset option }
[<RequireQualifiedAccess>]
type LocalIntegrationPrepareOutcome = Created | Existing
[<RequireQualifiedAccess>]
type LocalIntegrationDisposition = TargetApplied | TargetAlreadyObserved

[<RequireQualifiedAccess>]
module LocalIntegrationJournal =
    let isTargetRef (value: string) =
        not (isNull value) && value.Length <= 1024 && value.StartsWith("refs/heads/", StringComparison.Ordinal)
        && not (value.Contains "..") && value |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || "_-/.".Contains c)
        && value.Split('/') |> Array.forall (fun part -> part <> "" && not (part.StartsWith '.') && not (part.EndsWith '.') && not (part.EndsWith(".lock", StringComparison.Ordinal)))
    let validate (journal: LocalIntegrationJournal) =
        let plan = journal.Plan
        let clean (value: string) = not (String.IsNullOrWhiteSpace value) && value.Length <= 1024 && value.Trim() = value && not (Seq.exists Char.IsControl value)
        if plan.SchemaVersion <> "praxis.local-integration-plan/1"
           || not ([ plan.RepositoryIdentity; plan.DispatchId; plan.AttemptId ] |> List.forall clean)
           || not (LocalAgentHandoff.isDigest plan.PacketDigest && LocalAgentHandoff.isDigest plan.SubmissionDigest)
           || not (isTargetRef plan.TargetRef)
           || not (LocalAgentHandoff.isCommit plan.BeforeCommit && LocalAgentHandoff.isCommit plan.CandidateCommit)
           || plan.BeforeCommit = plan.CandidateCommit || plan.PreparedAt.Offset <> TimeSpan.Zero
           || journal.AppliedAt |> Option.exists (fun at -> at.Offset <> TimeSpan.Zero || at < plan.PreparedAt) then Error "invalid local integration journal"
        else Ok()
