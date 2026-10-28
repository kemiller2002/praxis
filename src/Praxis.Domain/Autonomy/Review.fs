namespace Praxis.Domain.Autonomy

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type ReviewSeverity =
    /// A defect that must not merge (secret material, a disabled or removed
    /// test, an unresolved conflict).
    | Critical
    /// Not a defect in the code, but a missing obligation that blocks an
    /// unattended merge (no requirement trace, change outside scope).
    | Blocking
    /// Reported for the record; does not block.
    | Major
    /// The change needs the owner (classified protected); the evaluator, not
    /// the review, decides REQUIRES_OWNER.
    | Owner

[<RequireQualifiedAccess>]
module ReviewSeverity =
    let toWire value =
        match value with
        | ReviewSeverity.Critical -> "critical"
        | ReviewSeverity.Blocking -> "blocking"
        | ReviewSeverity.Major -> "major"
        | ReviewSeverity.Owner -> "owner"

type ReviewFinding =
    { Severity: ReviewSeverity
      Code: string
      Path: string option
      Detail: string }

type ReviewInput =
    { Changes: ChangedPath list
      /// Every added line, with its file.
      AddedLines: (string * string) list
      /// Commit messages, pull-request title and body: where a requirement
      /// trace is looked for.
      TraceText: string
      ProtectedPaths: ProtectedPath list
      CiReferencedPaths: string list
      /// The work item's declared scope, when one is given: every changed
      /// path must match one of these globs.
      AllowedPaths: string list option }

type ReviewReport =
    { Findings: ReviewFinding list
      CriticalDefects: int
      Passed: bool }

/// The deterministic part of the independent review: requirement trace,
/// security, test integrity, scope and regression-risk signals computed from
/// the diff alone. It runs in its own CI job, never in the implementer's
/// session, and it never approves anything: it reports, and its check
/// run's conclusion is the evidence the evaluator reads.
[<RequireQualifiedAccess>]
module Review =
    let private secretPatterns =
        [ "private-key", Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----", RegexOptions.Compiled)
          "github-token", Regex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{40,}", RegexOptions.Compiled)
          "aws-access-key", Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)
          "anthropic-key", Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled)
          "slack-token", Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled) ]

    let private disabledTestPatterns =
        [ Regex(@"\[<\s*(Ignore|Fact\s*\(\s*Skip|Theory\s*\(\s*Skip)", RegexOptions.Compiled)
          Regex(@"\[\s*(Ignore|Fact\s*\(\s*Skip|Theory\s*\(\s*Skip)", RegexOptions.Compiled)
          Regex(@"\b(it|test|describe)\.skip\s*\(", RegexOptions.Compiled)
          Regex(@"\b(xit|xdescribe|xtest)\s*\(", RegexOptions.Compiled)
          Regex(@"\bptestCase\b|\bptestList\b", RegexOptions.Compiled) ]

    let private tracePattern =
        Regex(@"\b(WI|GH|RQ|REQ|DEC|AR|ESO|DF|OD|VP)-[A-Za-z0-9][A-Za-z0-9.-]*|\bWork-Item:\s*\S+", RegexOptions.Compiled)

    let private sourceExtensions = set [ ".fs"; ".fsi"; ".fsx"; ".cs"; ".ts"; ".tsx"; ".js"; ".mjs"; ".jsx"; ".py"; ".go"; ".rs"; ".java" ]

    let private isTest (path: string) =
        [ "tests/**"; "**/tests/**"; "test/**"; "**/test/**"; "**/*Tests.fs"; "**/*Test.fs"; "**/*.spec.*"; "**/*.test.*" ]
        |> List.exists (fun p -> Classification.isMatch p path)

    let private isSource (path: string) =
        sourceExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant()) && not (isTest path)

    let private finding severity code path detail = { Severity = severity; Code = code; Path = path; Detail = detail }

    let review (input: ReviewInput) : ReviewReport =
        let secrets =
            input.AddedLines
            |> List.collect (fun (path, line) ->
                secretPatterns
                |> List.filter (fun (_, pattern) -> pattern.IsMatch line)
                |> List.map (fun (kind, _) -> finding ReviewSeverity.Critical "secret-material" (Some path) $"an added line matches {kind}"))
            |> List.distinct

        let conflicts =
            input.AddedLines
            |> List.filter (fun (_, line) -> line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith(">>>>>>> ", StringComparison.Ordinal))
            |> List.map (fun (path, _) -> finding ReviewSeverity.Critical "conflict-marker" (Some path) "an unresolved merge conflict marker was added")
            |> List.distinct

        let disabled =
            input.AddedLines
            |> List.filter (fun (_, line) -> disabledTestPatterns |> List.exists (fun p -> p.IsMatch line))
            |> List.map (fun (path, _) -> finding ReviewSeverity.Critical "disables-test" (Some path) "an added line skips or ignores a test")
            |> List.distinct

        let removed =
            input.Changes
            |> List.filter (fun c -> c.Kind = ChangeKind.Removed && isTest c.Path)
            |> List.map (fun c -> finding ReviewSeverity.Critical "removes-test" (Some c.Path) "a test file is deleted")

        let trace =
            if tracePattern.IsMatch input.TraceText then []
            else [ finding ReviewSeverity.Blocking "requirements-trace-missing" None "no work item, requirement or decision is referenced by the commits or the pull request" ]

        let scope =
            match input.AllowedPaths with
            | None -> []
            | Some globs ->
                input.Changes
                |> List.filter (fun c -> not (globs |> List.exists (fun g -> Classification.isMatch g c.Path)))
                |> List.map (fun c -> finding ReviewSeverity.Blocking "outside-scope" (Some c.Path) "the path is outside the work item's declared scope")

        let owner =
            Classification.classify input.ProtectedPaths input.CiReferencedPaths input.Changes
            |> List.map (fun (path, cls, reason) -> finding ReviewSeverity.Owner $"protected:{ProtectedClass.toWire cls}" (Some path) reason)

        let untested =
            let sourceChanged = input.Changes |> List.exists (fun c -> c.Kind <> ChangeKind.Removed && isSource c.Path)
            let testChanged = input.Changes |> List.exists (fun c -> c.Kind <> ChangeKind.Removed && isTest c.Path)
            if sourceChanged && not testChanged then
                [ finding ReviewSeverity.Major "untested-change" None "source changed with no test added or changed" ]
            else []

        let large =
            if input.AddedLines.Length > 1500 then
                [ finding ReviewSeverity.Major "large-change" None $"{input.AddedLines.Length} lines added; regression risk is high" ]
            else []

        let findings = secrets @ conflicts @ disabled @ removed @ trace @ scope @ owner @ untested @ large
        let critical = findings |> List.filter (fun f -> f.Severity = ReviewSeverity.Critical) |> List.length
        let blocking = findings |> List.exists (fun f -> f.Severity = ReviewSeverity.Blocking)

        { Findings = findings
          CriticalDefects = critical
          Passed = critical = 0 && not blocking }
