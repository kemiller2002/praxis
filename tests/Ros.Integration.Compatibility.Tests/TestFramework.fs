namespace Ros.Integration.Compatibility.Tests

/// A standalone copy of `Ros.Tests.TestFramework` -- see the identical
/// note in `Ros.Integration.Tests.TestFramework`. This project must
/// depend on nothing but `Ros.Integration` itself.
type TestCase =
    { Name: string
      Run: unit -> unit }

[<RequireQualifiedAccess>]
module Assert =
    let isTrue condition message =
        if not condition then failwith message

[<RequireQualifiedAccess>]
module TestRunner =
    let run (tests: TestCase list) =
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
