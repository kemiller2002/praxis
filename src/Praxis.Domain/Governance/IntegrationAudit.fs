namespace Praxis.Domain.Governance

open System

/// How the merge gate's verdict for one candidate was observed.
[<RequireQualifiedAccess>]
type GateObservation =
    /// A gate check concluded at the candidate head, produced by `producer`.
    | Check of conclusion: string * producer: string
    /// A recorded `praxis.merge-decision/1` for the candidate.
    | Decision of outcome: string
    | NotObserved

/// One first-parent commit of the protected branch, as Git and (optionally)
/// the platform show it.
type IntegratedCommit =
    { Commit: string
      Parents: string list
      Committer: string
      Subject: string
      /// The pull request this commit integrated, from the platform or the
      /// merge subject; `None` for a commit that arrived without one.
      PullRequest: int option
      /// The candidate head that was integrated: the second parent of a merge
      /// commit, or the platform's recorded head for squash and rebase.
      Candidate: string option
      /// Work items whose state on the protected branch after this commit is
      /// not terminal, and which this commit introduced or changed.
      ActiveWorkItemsIntroduced: string list
      /// Whether the protected configuration at the parent required verified
      /// completion (read from the parent's `ros.json`).
      VerifiedCompletionRequired: bool
      Gate: GateObservation
      /// Commits pushed to the candidate's branch after it was integrated and
      /// never integrated themselves (platform observation; 0 when unknown).
      StrandedAfterMerge: int }

type AuditFinding =
    { Commit: string
      Code: string
      Severity: string
      Detail: string
      Remedy: string }

type IntegrationAuditReport =
    { Branch: string
      Head: string
      Examined: int
      Findings: AuditFinding list
      /// Integrations with verified gate evidence.
      VerifiedIntegrations: int
      HistoryRewritten: bool }

[<RequireQualifiedAccess>]
module IntegrationAudit =
    /// The committer GitHub records for merges and squashes made through its
    /// own merge button or API.
    let isPlatformMerge (commit: IntegratedCommit) = commit.Committer = "GitHub"

    let private finding (commit: IntegratedCommit) code severity detail remedy =
        { Commit = commit.Commit; Code = code; Severity = severity; Detail = detail; Remedy = remedy }

    let private gateFindings (gateCheck: string) (commit: IntegratedCommit) =
        let label = commit.PullRequest |> Option.map (sprintf "pull request #%d") |> Option.defaultValue "the integrated change"
        let severity = if commit.VerifiedCompletionRequired then "error" else "warning"

        match commit.Gate with
        | GateObservation.Check("success", "github-actions") -> []
        | GateObservation.Decision "verified" -> []
        | GateObservation.Decision "authorized-by-exception" ->
            [ finding commit "AUD.INTEGRATED_BY_EXCEPTION" "notice" $"{label} was integrated under a governance exception; it is not verified completion" "complete the exception's follow-up obligations" ]
        | GateObservation.Check(conclusion, producer) when producer <> "github-actions" ->
            [ finding commit "AUD.GATE_SOURCE_UNTRUSTED" "error" $"the '{gateCheck}' result for {label} ({conclusion}) was produced by '{producer}', not GitHub Actions" "pin the required check's source; treat the integration as unverified" ]
        | GateObservation.Check(conclusion, _)
        | GateObservation.Decision conclusion ->
            [ finding commit "AUD.GATE_REFUSED_BUT_MERGED" "error" $"{label} was integrated although the merge gate concluded '{conclusion}': an administrator bypass or an unenforced rule" "review the integration; record an exception or revert" ]
        | GateObservation.NotObserved ->
            [ finding
                  commit
                  "AUD.UNVERIFIED_INTEGRATION"
                  severity
                  $"{label} was integrated with no merge-gate verdict for its candidate {commit.Candidate |> Option.defaultValue commit.Commit}: nothing shows verified completion"
                  "re-verify the integrated state and record the result, or record the governing decision that accepted plain completion" ]

    let examine (gateCheck: string) (commit: IntegratedCommit) =
        let direct =
            if commit.PullRequest.IsNone && not (isPlatformMerge commit) then
                [ finding
                      commit
                      "AUD.DIRECT_PUSH"
                      "error"
                      $"'{commit.Subject}' reached the protected branch without a pull request (committer {commit.Committer})"
                      "require pull requests on the protected branch; review the pushed change" ]
            else []

        let gate = if commit.PullRequest.IsSome || commit.Candidate.IsSome then gateFindings gateCheck commit else []

        let active =
            commit.ActiveWorkItemsIntroduced
            |> List.map (fun item ->
                finding
                    commit
                    "AUD.INTEGRATED_WHILE_ACTIVE"
                    "error"
                    $"work item {item} was integrated while still active: its completion was never recorded"
                    $"complete {item} with verified evidence for the integrated state, or block it with the reason")

        let stranded =
            if commit.StrandedAfterMerge > 0 then
                [ finding
                      commit
                      "AUD.STRANDED_AFTER_MERGE"
                      "warning"
                      $"{commit.StrandedAfterMerge} commit(s) were pushed to the candidate branch after it was integrated and are not on the protected branch"
                      "integrate or discard them through a new pull request" ]
            else []

        direct @ gate @ active @ stranded

    let audit (branch: string) (head: string) (gateCheck: string) (previousHeadStillAncestor: bool option) (commits: IntegratedCommit list) =
        let findings = commits |> List.collect (examine gateCheck)

        let rewritten =
            match previousHeadStillAncestor with
            | Some false ->
                [ { Commit = head
                    Code = "AUD.HISTORY_REWRITTEN"
                    Severity = "error"
                    Detail = "the previously audited head is no longer in the protected branch's history (force push or deletion)"
                    Remedy = "block force pushes on the protected branch; restore and review the lost history" } ]
            | _ -> []

        let verified =
            commits
            |> List.filter (fun c ->
                match c.Gate with
                | GateObservation.Check("success", "github-actions")
                | GateObservation.Decision "verified" -> true
                | _ -> false)
            |> List.length

        { Branch = branch
          Head = head
          Examined = commits.Length
          Findings = rewritten @ findings
          VerifiedIntegrations = verified
          HistoryRewritten = not rewritten.IsEmpty }

    /// The operational-demonstration rung: at least one verified integration
    /// and no error finding in the examined range.
    let demonstrated (report: IntegrationAuditReport) =
        let errors = report.Findings |> List.filter (fun f -> f.Severity = "error")

        let blockers =
            [ if report.VerifiedIntegrations = 0 then "no integration in the audited range carries verified merge-gate evidence"
              for e in errors |> List.truncate 5 do
                  $"{e.Code} at {e.Commit.Substring(0, min 12 e.Commit.Length)}" ]

        blockers.IsEmpty, blockers

    /// The pull-request number GitHub writes into its merge subjects:
    /// "Merge pull request #N from ..." or a squash "Title (#N)".
    let pullRequestFromSubject (subject: string) =
        let digits (s: string) =
            let taken = s |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
            match Int32.TryParse taken with
            | true, n when taken.Length > 0 -> Some n
            | _ -> None

        if subject.StartsWith("Merge pull request #", StringComparison.Ordinal) then
            digits (subject.Substring "Merge pull request #".Length)
        else
            let marker = subject.LastIndexOf("(#", StringComparison.Ordinal)
            if marker >= 0 && subject.EndsWith(")", StringComparison.Ordinal) then digits (subject.Substring(marker + 2)) else None
