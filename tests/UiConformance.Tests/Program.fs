module Praxis.UiConformance.Program

// `ui:conformance` for Praxis: the native equivalent of `npm run
// ui:conformance` (Praxis owns no Node tooling, RQ-ROS-2026-A024).
//
//   dotnet tests/UiConformance.Tests/bin/Release/net10.0/UiConformance.Tests.dll [--evidence PATH]
//
// Drives headless Chrome (PRAXIS_TEST_BROWSER or the usual locations) over
// every Praxis UI surface -- `praxis web serve`, `praxis hub serve` and the
// public site -- against visual-engineering/UI-LAYOUT-INTERACTION@1.0.0, writes
// evidence that validates against ui-conformance.schema.json (standardVersion
// 1.0.0), and exits non-zero when anything FAILs or the evidence is invalid.
// UNKNOWN is reported, never converted to PASS, and does not fail the run.

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Praxis.Tests

let private revision () =
    try
        let start = ProcessStartInfo("git", "rev-parse HEAD", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory)
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEnd().Trim()
        child.WaitForExit()
        if child.ExitCode = 0 && output.Length >= 7 then output else "unknown-revision"
    with _ ->
        "unknown-revision"

let private notes =
    "Measured by tests/UiConformance.Tests (F#, Chrome DevTools Protocol; a native port of @echelon-foundry/forma-conformance 0.1.0 at "
    + "kemiller2002/forma 6c71725, because Praxis may own no Node/Playwright tooling: RQ-ROS-2026-A024). Scope: praxis web serve "
    + "(queue, item, validation, execution pages), praxis hub serve, and the public site. The praxis CLI's terminal output is not a "
    + "browser surface and is not-applicable to this standard. Actions run at 1280 CSS px; layout is measured at 320/390/768/1280 "
    + "and at 200% default text (390, 1280)."

let private line (surface: SurfaceRecord) =
    [ yield! surface.Evidence |> List.map (fun item -> $"  {item}")
      yield! surface.Groups |> List.map (fun group -> $"  GROUP {group.Status.ToUpperInvariant()} {group.Id}: " + String.concat " | " group.Evidence)
      yield! surface.Actions |> List.map (fun action -> $"  ACTION {action.Status.ToUpperInvariant()} {action.Id}: " + String.concat " | " action.Evidence) ]

[<EntryPoint>]
let main argv =
    let evidencePath =
        match argv |> List.ofArray with
        | [ "--evidence"; path ] -> Some path
        | [] -> None
        | other ->
            eprintfn "usage: UiConformance.Tests [--evidence PATH] (got %s)" (String.concat " " other)
            exit 2

    use browser = Browser.Launch()

    // PRAXIS_UI_SURFACES=web,hub,site narrows a local run (CI never sets it).
    let selected =
        match Environment.GetEnvironmentVariable "PRAXIS_UI_SURFACES" with
        | null
        | "" -> [ "web"; "hub"; "site" ]
        | value -> value.Split(',') |> List.ofArray

    let timed (name: string) (runSurface: Browser -> SurfaceRecord list) =
        if List.contains name selected then
            let clock = Stopwatch.StartNew()
            let result = runSurface browser
            printfn "measured %s in %.0f s" name clock.Elapsed.TotalSeconds
            result
        else
            []

    let applied =
        timed "web" WebSurfaces.run @ timed "hub" HubSurface.run @ timed "site" SiteSurface.run
        |> List.map Exceptions.apply

    let surfaces = applied |> List.map fst
    let exceptions = applied |> List.collect snd

    let document = Evidence.document "kemiller2002/praxis" (revision ()) "app-ui" surfaces exceptions notes
    let schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui-conformance.schema.json"))).AsObject()
    let violations = SchemaCheck.validate schema document

    for surface in surfaces do
        printfn "SURFACE %s %s (%s)" ((Evidence.surfaceStatus surface).ToUpperInvariant()) surface.Id surface.Path
        line surface |> List.iter (printfn "%s")

    match evidencePath with
    | Some path ->
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
        File.WriteAllText(path, Evidence.render document + "\n")
        printfn "evidence: %s" path
    | None -> ()

    let statuses = surfaces |> List.collect Evidence.statuses
    let count status = statuses |> List.filter ((=) status) |> List.length
    printfn "totals: %d pass, %d fail, %d unknown, %d not-applicable, %d exception (checks, groups and actions)" (count "pass") (count "fail") (count "unknown") (count "not-applicable") (count "exception")
    exceptions |> List.iter (fun record -> printfn "EXCEPTION %s %s: %s (owner: %s)" record.RuleId record.Surface record.Reason record.Owner)

    if not violations.IsEmpty then
        eprintfn "evidence does not validate against ui-conformance.schema.json:"
        violations |> List.iter (eprintfn "  %s")

    if violations.IsEmpty && count "fail" = 0 then 0 else 1
