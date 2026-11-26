namespace Praxis.Domain.Governance

open System
open System.Text.RegularExpressions

/// A validation's declared proof that it executed tests. A suite that exits
/// 0 after running nothing (a filter that matches nothing, a discovery
/// failure, an emptied project) must not pass a mandatory validation.
type TestExecutionExpectation =
    { /// A regular expression with a named group `count` that the output
      /// must contain, for example `(?<count>\d+) test\(s\); \d+ passed`.
      CountPattern: string
      MinimumTests: int }

[<RequireQualifiedAccess>]
type TestExecutionAssessment =
    | Executed of count: int
    | NoCount
    | TooFew of count: int * minimum: int
    | InvalidPattern of reason: string

[<RequireQualifiedAccess>]
module TestExecution =
    /// The largest count the pattern finds: summary lines repeat per project,
    /// and the total is the largest, never their sum (which would double
    /// count a total line).
    let assess (expectation: TestExecutionExpectation) (output: string) =
        try
            let regex = Regex(expectation.CountPattern, RegexOptions.Multiline, TimeSpan.FromSeconds 2.0)

            if regex.GetGroupNumbers() |> Array.exists (fun n -> regex.GroupNameFromNumber n = "count") |> not then
                TestExecutionAssessment.InvalidPattern "the pattern has no named group 'count'"
            else
                let counts =
                    regex.Matches(output)
                    |> Seq.choose (fun m ->
                        match Int32.TryParse m.Groups["count"].Value with
                        | true, n -> Some n
                        | _ -> None)
                    |> Seq.toList

                match counts with
                | [] -> TestExecutionAssessment.NoCount
                | _ ->
                    let count = List.max counts
                    if count < expectation.MinimumTests then TestExecutionAssessment.TooFew(count, expectation.MinimumTests)
                    else TestExecutionAssessment.Executed count
        with :? ArgumentException as error ->
            TestExecutionAssessment.InvalidPattern error.Message

    /// Whether a passing exit code may stand. Any assessment other than
    /// `Executed` turns a pass into a failure, with the reason.
    let gate (expectation: TestExecutionExpectation option) (output: string) =
        match expectation with
        | None -> Ok None
        | Some expectation ->
            match assess expectation output with
            | TestExecutionAssessment.Executed count -> Ok(Some count)
            | TestExecutionAssessment.NoCount -> Error "PRX.VALIDATION.NO_TESTS_EXECUTED: the output reports no executed-test count; an empty or undiscovered suite cannot pass"
            | TestExecutionAssessment.TooFew(count, minimum) -> Error $"PRX.VALIDATION.NO_TESTS_EXECUTED: {count} test(s) executed, below the required minimum of {minimum}"
            | TestExecutionAssessment.InvalidPattern reason -> Error $"PRX.VALIDATION.NO_TESTS_EXECUTED: the policy's test-count pattern is unusable ({reason})"
