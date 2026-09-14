namespace Ros.Persistence.Tests

type TestCase =
    { Name: string
      Run: unit -> unit }

[<RequireQualifiedAccess>]
module Assert =
    let equal<'value when 'value: equality> (expected: 'value) (actual: 'value) =
        if actual <> expected then
            failwith $"Expected: {expected}\nActual:   {actual}"

    let isTrue condition message =
        if not condition then failwith message

    let isOk (result: Result<'ok, 'error>) : 'ok =
        match result with
        | Ok value -> value
        | Error error -> failwith $"expected Ok, got Error {error}"

    let isError (result: Result<'ok, 'error>) : 'error =
        match result with
        | Error error -> error
        | Ok value -> failwith $"expected Error, got Ok {value}"

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
