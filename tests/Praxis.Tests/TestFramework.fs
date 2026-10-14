namespace Praxis.Tests

open System

type TestCase =
    { Name: string
      Run: unit -> unit }

[<RequireQualifiedAccess>]
module Assert =
    let equal<'value when 'value: equality> (expected: 'value) (actual: 'value) =
        if actual <> expected then
            failwith $"Expected: {expected}\nActual:   {actual}"

    let empty (values: 'value list) =
        if not values.IsEmpty then
            failwith $"Expected no values but received {values.Length}: {values}"

    let single (values: 'value list) =
        match values with
        | [ value ] -> value
        | _ -> failwith $"Expected one value but received {values.Length}: {values}"

    let isTrue condition message =
        if not condition then failwith message

[<RequireQualifiedAccess>]
module TestRunner =
    /// `PRAXIS_TEST_FILTER=text` runs only tests whose name contains the
    /// text (a local convenience; CI never sets it).
    let private selected (tests: TestCase list) =
        match Environment.GetEnvironmentVariable "PRAXIS_TEST_FILTER" with
        | null
        | "" -> tests
        | filter -> tests |> List.filter (fun test -> test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))

    let run (allTests: TestCase list) =
        let tests = selected allTests
        let mutable failures = 0

        for test in tests do
            try
                test.Run()
                printfn "PASS %s" test.Name
            with error ->
                failures <- failures + 1
                eprintfn "FAIL %s\n  %s" test.Name error.Message

        printfn "%d test(s); %d passed; %d failed" tests.Length (tests.Length - failures) failures

        if failures = 0 then 0 else 1
