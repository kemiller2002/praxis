module Praxis.Site.Program

open System
open System.Diagnostics
open System.IO
open System.Reflection

let private usage =
    """usage: praxis-site <command>

  check                  structure, references, accessibility rules, boundary
  evidence               write site/data/gh-84.json from the .ros records
  evidence --render      regenerate the ledger and data-evidence values in site/index.html
  evidence --check       the snapshot matches the records; the page matches the snapshot
  assemble [OUT]         copy site/ to OUT (default _site) and check the copy
  serve [--port N]       preview site/ on http://127.0.0.1:N/ (default 4173)
  verify                 check, evidence --check, then the site test suite"""

let private report (problems: string list) (prefix: string) =
    problems |> List.iter (fun problem -> eprintfn "%s%s" prefix problem)

let private check (root: string) =
    let problems = Site.check (Path.Combine(root, "site"))
    report problems "ERROR "

    if problems.IsEmpty then
        printfn "site check passed"
        0
    else
        printfn "site check failed with %d problem(s)" problems.Length
        1

let private evidence (root: string) (args: string list) =
    if args |> List.contains "--render" then
        Evidence.render root
        printfn "rendered ledger and evidence values into site/index.html"
        0
    elif args |> List.contains "--check" then
        let problems = Evidence.check root
        report problems ""

        if problems.IsEmpty then
            printfn "evidence check passed"
            0
        else
            printfn "evidence check failed: %d problem(s)" problems.Length
            1
    else
        let written = Evidence.write root (Evidence.timestamp DateTimeOffset.UtcNow)
        printfn "wrote %s" written
        0

let private assemble (root: string) (out: string) =
    match Assemble.assemble root out with
    | Error message ->
        eprintfn "Error: %s" message
        1
    | Ok assembled ->
        report assembled.Problems ""

        if assembled.Problems.IsEmpty then
            printfn "assembled %s" (Repository.relative root assembled.Target)
            0
        else
            printfn "artifact check failed: %d problem(s)" assembled.Problems.Length
            1

let private serve (root: string) (args: string list) =
    let port =
        match args |> List.skipWhile (fun arg -> arg <> "--port") with
        | _ :: value :: _ when value <> "" -> value
        | _ -> "4173"

    match Int32.TryParse port with
    | true, number when number > 0 && number < 65536 ->
        Serve.run root number
        0
    | _ ->
        eprintfn "Error: invalid port %s" port
        1

/// The configuration this tool was built with, so the test suite runs in the same one.
let private configuration =
    Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()
    |> Option.ofObj
    |> Option.map (fun attribute -> attribute.Configuration)
    |> Option.filter (fun name -> name <> "")
    |> Option.defaultValue "Release"

/// Runs the site test suite (tests/Site.Tests) with `dotnet run`.
let private tests (root: string) =
    let start =
        ProcessStartInfo("dotnet", WorkingDirectory = root, UseShellExecute = false)

    [ "run"
      "--project"
      Path.Combine("tests", "Site.Tests", "Site.Tests.fsproj")
      "--configuration"
      configuration ]
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start
    child.WaitForExit()
    child.ExitCode

/// Every public-site check in order, stopping at the first failure.
let private verify (root: string) =
    let steps = [ (fun () -> check root); (fun () -> evidence root [ "--check" ]); (fun () -> tests root) ]
    let failed = steps |> List.exists (fun step -> step () <> 0)
    if failed then 1 else 0

[<EntryPoint>]
let main argv =
    try
        match List.ofArray argv with
        | [ "check" ] -> check (Repository.locate ())
        | "evidence" :: rest -> evidence (Repository.locate ()) rest
        | [ "assemble" ] -> assemble (Repository.locate ()) "_site"
        | [ "assemble"; out ] -> assemble (Repository.locate ()) out
        | "serve" :: rest -> serve (Repository.locate ()) rest
        | [ "verify" ] -> verify (Repository.locate ())
        | [ "--help" ]
        | [ "-h" ]
        | [ "help" ] ->
            printfn "%s" usage
            0
        | _ ->
            eprintfn "%s" usage
            2
    with error ->
        eprintfn "Error: %s" error.Message
        1
