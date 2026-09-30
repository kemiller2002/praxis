namespace Site.Tests

open System
open System.IO
open System.Text.RegularExpressions
open Praxis.Site

type TestCase =
    { Name: string
      Run: unit -> unit }

[<RequireQualifiedAccess>]
module Assert =
    let equal<'value when 'value: equality> (expected: 'value) (actual: 'value) =
        if actual <> expected then
            failwith $"Expected: {expected}\nActual:   {actual}"

    let equalMessage<'value when 'value: equality> (expected: 'value) (actual: 'value) (message: string) =
        if actual <> expected then
            failwith $"{message}\nExpected: {expected}\nActual:   {actual}"

    let empty (values: 'value list) =
        if not values.IsEmpty then
            failwith $"Expected no values but received {values.Length}: {values}"

    let isTrue condition message =
        if not condition then failwith message

    let isFalse condition message =
        if condition then failwith message

    /// `assert.match(text, pattern)`
    let matches (pattern: string) (text: string) (message: string) =
        if not (Regex.IsMatch(text, pattern)) then
            failwith $"{message}: expected to match /{pattern}/"

    let notMatches (pattern: string) (text: string) (message: string) =
        if Regex.IsMatch(text, pattern) then
            failwith $"{message}: expected not to match /{pattern}/"

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

/// The repository under test and its files; opened by every test module.
module Fixture =
    let root = Repository.locate ()
    let path (file: string) = Path.Combine(root, file)
    let read (file: string) = File.ReadAllText(path file)
    let exists (file: string) = File.Exists(path file) || Directory.Exists(path file)
    let siteRoot = path "site"
    let html () = read "site/index.html"
    let css () = read "site/assets/css/site.css"

    /// The markup from `id="<id>"` to the next `</section>`.
    let section (html: string) (id: string) =
        let start = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)
        Assert.isTrue (start >= 0) $"section #{id} exists"
        let finish = html.IndexOf("</section>", start, StringComparison.Ordinal)
        html.Substring(start, finish - start)

    let count (pattern: string) (text: string) = Regex.Matches(text, pattern).Count

    let test (name: string) (run: unit -> unit) = { Name = name; Run = run }
