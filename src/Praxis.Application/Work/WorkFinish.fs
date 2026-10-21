namespace Praxis.Application.Work

open System
open System.Text.RegularExpressions
open Praxis.Domain.Work

/// REL-TOKEN-003: a bounded excerpt of one not-passed receipt's output, so
/// the agent sees what failed without running the tests again.
type FailureExcerpt =
    { EvidenceId: string
      Validation: string
      Status: CheckStatus
      ExitCode: int option
      /// The full output beside the receipt (`.output.txt`, I8).
      OutputPath: string
      /// First shown line, 1-based (0 when the output is empty).
      FirstLine: int
      TotalLines: int
      Lines: string list }

/// Where `praxis work finish` stopped. `Stopped`: `work verify` refused,
/// failed or did not pass, and `complete-verified` was never run.
/// `Finished`: every receipt passed and `complete-verified` ran with its
/// own outcome (which may itself be a refusal). `Replayed`: the claim was
/// already completed, so `complete-verified` replayed it without verifying
/// again (the same idempotence `complete-verified` has).
[<RequireQualifiedAccess>]
type FinishOutcome<'completion> =
    | Stopped of verify: WorkVerifyOutcome
    | Finished of verify: WorkVerifyOutcome * completion: 'completion
    | Replayed of completion: 'completion

/// REL-TOKEN-003 `praxis work finish --id ID --token TOKEN`: `work verify`
/// at HEAD, then, only when every receipt passed, `work complete-verified`
/// with the same derivation. Both steps are passed in whole, so finish adds
/// no check and removes none (F1-F3, REL-GATE-003/004 apply unchanged).
[<RequireQualifiedAccess>]
module WorkFinish =
    /// Default bound on excerpt lines per failing receipt.
    let ExcerptLines = 40

    /// Bound on one excerpt line's length.
    let LineWidth = 240

    /// `current` is the claim as read before anything runs. A completed
    /// claim goes straight to `complete-verified`, which replays it (or
    /// refuses it) exactly as it would alone; anything else, including an
    /// unreadable store, is `work verify`'s to refuse first.
    let run
        (current: Result<WorkClaim option, string>)
        (verify: unit -> WorkVerifyOutcome)
        (complete: unit -> 'completion)
        : FinishOutcome<'completion> =
        match current with
        | Ok(Some claim) when claim.Status = ClaimStatus.Completed && claim.Completion.IsSome -> FinishOutcome.Replayed(complete ())
        | _ ->
            match verify () with
            | WorkVerifyOutcome.Verified(_, receipts, _) as verified when WorkVerification.allPassed receipts -> FinishOutcome.Finished(verified, complete ())
            | stopped -> FinishOutcome.Stopped stopped

    let private failure = Regex(@"(?i)(\bfail(ed|ure|s)?\b|\berror\b|\bexception\b|\bassert|\bexpected\b)", RegexOptions.CultureInvariant)

    let private summary =
        Regex(@"(?i)^\s*(failed!|passed!|total tests|test run (failed|aborted)|build failed|\d+ (tests? )?(failed|passed))", RegexOptions.CultureInvariant)

    let private clip (line: string) =
        if line.Length <= LineWidth then line else line.Substring(0, LineWidth - 3) + "..."

    /// Pure: the window of `output` worth showing, at most `maxLines` lines.
    /// Short output is shown whole. Otherwise the window starts two lines
    /// before the first failure marker (else it is the tail) and keeps the
    /// runner's last summary line when the window would cut it off.
    /// Returns (first shown line, 1-based; total lines; lines).
    let excerpt (maxLines: int) (output: string) =
        let lines =
            output.Replace("\r\n", "\n").Split('\n')
            |> Array.rev
            |> Array.skipWhile String.IsNullOrWhiteSpace
            |> Array.rev

        let total = lines.Length
        let bound = max 1 maxLines

        if total = 0 then
            0, 0, []
        elif total <= bound then
            1, total, lines |> Array.map clip |> Array.toList
        else
            match lines |> Array.tryFindIndex failure.IsMatch with
            | None -> total - bound + 1, total, lines[total - bound ..] |> Array.map clip |> Array.toList
            | Some first ->
                let start = min (max 0 (first - 2)) (total - bound)
                let window = lines[start .. start + bound - 1] |> Array.toList

                let shown =
                    match lines |> Array.tryFindIndexBack summary.IsMatch with
                    | Some last when last >= start + bound -> List.truncate (bound - 1) window @ [ lines[last] ]
                    | _ -> window

                start + 1, total, shown |> List.map clip

    let private outputPath (path: string) =
        if path.EndsWith(".json", StringComparison.Ordinal) then path.Substring(0, path.Length - 5) + ".output.txt" else path + ".output.txt"

    /// Pure: one excerpt per not-passed receipt of a verify outcome.
    let excerpts (maxLines: int) (outcome: WorkVerifyOutcome) : FailureExcerpt list =
        match outcome with
        | WorkVerifyOutcome.Verified(_, receipts, _) ->
            receipts
            |> List.filter (fun receipt -> receipt.Payload.Status <> CheckStatus.Passed)
            |> List.map (fun receipt ->
                let first, total, lines = excerpt maxLines receipt.Output

                { EvidenceId = receipt.Payload.EvidenceId
                  Validation = receipt.Payload.Validation
                  Status = receipt.Payload.Status
                  ExitCode = receipt.Payload.ExitCode
                  OutputPath = outputPath receipt.Path
                  FirstLine = first
                  TotalLines = total
                  Lines = lines })
        | _ -> []
