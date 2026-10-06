namespace Praxis.Tests

open System
open System.IO
open System.Text.RegularExpressions

/// Requirement records cite the tests that verify them. A citation of a
/// test file that no longer exists (for example the Node suites deleted by
/// RQ-ROS-2026-A024, or the pre-rename `tests/Ros.Tests` path) silently
/// overstates verification, so every cited test path in a record's body
/// must exist, unless the same line marks the citation historical
/// (PRAXIS-MISC-09). Front matter is provenance history and is never
/// rewritten, so it is not checked.
[<RequireQualifiedAccess>]
module RequirementCitationTests =
    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && File.Exists(Path.Combine(directory.FullName, "Praxis.slnx")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private citation =
        Regex(@"\btests/[A-Za-z0-9_./-]+\.(?:fs|fsx|mjs|cjs|js|ts|py|sh|json)\b", RegexOptions.CultureInvariant)

    /// The body lines of a Markdown record with YAML front matter.
    let bodyLines (text: string) =
        let lines = text.Replace("\r\n", "\n").Split('\n') |> List.ofArray

        match lines with
        | "---" :: rest ->
            match rest |> List.tryFindIndex ((=) "---") with
            | Some closing -> rest |> List.skip (closing + 1)
            | None -> lines
        | _ -> lines

    /// Cited test paths that do not exist under `root`, as (line, path).
    let staleCitations (root: string) (text: string) =
        bodyLines text
        |> List.filter (fun line -> not (line.Contains("historical", StringComparison.OrdinalIgnoreCase)))
        |> List.collect (fun line ->
            citation.Matches line
            |> Seq.map (fun found -> found.Value.TrimEnd('.'))
            |> Seq.filter (fun path -> not (File.Exists(Path.Combine(root, path))))
            |> Seq.map (fun path -> line.Trim(), path)
            |> List.ofSeq)

    let tests =
        [ { Name = "requirement citations: a cited test path must exist unless the line marks it historical"
            Run =
              fun () ->
                  let root = CliHarness.temporaryDirectory "praxis-requirement-citations"

                  try
                      CliHarness.write root "tests/Praxis.Tests/Present.fs" "module Present"

                      let record =
                          String.concat
                              "\n"
                              [ "---"
                                "id: RQ-X"
                                "provenance: [tests/deleted-in-front-matter.test.mjs]"
                                "---"
                                "- `tests/Praxis.Tests/Present.fs`"
                                "- `tests/Ros.Tests/Renamed.fs`"
                                "- `tests/gone.test.mjs` (historical; deleted with the Node suites)" ]

                      Assert.equal [ "tests/Ros.Tests/Renamed.fs" ] (staleCitations root record |> List.map snd)
                  finally
                      CliHarness.removeDirectory root }

          { Name = "requirement citations: every requirement record in this repository cites only existing tests"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo AppContext.BaseDirectory)

                  let stale =
                      Directory.GetFiles(Path.Combine(root, "research", "requirements"), "*.md")
                      |> Array.sort
                      |> List.ofArray
                      |> List.collect (fun file ->
                          staleCitations root (File.ReadAllText file)
                          |> List.map (fun (_, path) -> $"{Path.GetFileName file}: {path}"))

                  Assert.equal ([]: string list) stale } ]
