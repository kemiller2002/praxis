namespace Praxis.Tests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// PRAXIS-HYG-04: requirement status tables (Markdown tables under a heading
/// that contains "status", in docs/ and requirements/) may cite, in
/// backticks, only research records, tests/ and src/ paths and work items
/// that exist. This catches a status row that drifts from the repository by
/// citing something deleted, renamed or never captured. It does not judge a
/// row's prose (for example "not implemented" beside a completed item): rows
/// legitimately mix complete and open items, so that check would be unsound.
[<RequireQualifiedAccess>]
module StatusTableCitationTests =
    type Known =
        { Records: Set<string>
          Paths: string -> bool
          WorkItems: Set<string> }

    let private recordPattern = Regex(@"^(EV|DF|EX|HY|TH|RQ)-[A-Z]+-\d{4}-[A-Z]?\d+$", RegexOptions.Compiled)
    let private workItemPattern = Regex(@"^[A-Z][A-Z0-9]*(-[A-Z0-9]+)+$", RegexOptions.Compiled)
    let private citation = Regex(@"`([^`]+)`", RegexOptions.Compiled)

    let private family (id: string) = id.Substring(0, id.IndexOf '-')

    /// Pure: every dangling citation in one Markdown document's status tables.
    /// A backticked token is a work-item citation only when its first segment
    /// is the family of an existing work item (`PRAXIS-`, `WI-`, `GH-`, ...),
    /// so requirement IDs (`PRX-GRP-...`), commands and other code spans are
    /// never mistaken for work items.
    let danglingCitations (known: Known) (path: string) (content: string) : string list =
        let families = known.WorkItems |> Set.map family

        let check (line: string) =
            [ for matched in citation.Matches line do
                  let token = matched.Groups[1].Value

                  if recordPattern.IsMatch token then
                      if not (known.Records.Contains token) then
                          yield $"{path}: status row cites record {token}, which does not exist"
                  elif token.StartsWith("tests/", StringComparison.Ordinal) || token.StartsWith("src/", StringComparison.Ordinal) then
                      let target = token.Split(':').[0]

                      if not (known.Paths target) then
                          yield $"{path}: status row cites {target}, which does not exist"
                  elif workItemPattern.IsMatch token && families.Contains(family token) && not (known.WorkItems.Contains token) then
                      yield $"{path}: status row cites work item {token}, which is not in the backlog or live context" ]

        content.Split('\n')
        |> Array.fold
            (fun (inStatus, found) (raw: string) ->
                let line = raw.TrimEnd '\r'

                if line.StartsWith "#" then
                    line.Contains("status", StringComparison.OrdinalIgnoreCase), found
                elif inStatus && line.StartsWith "|" then
                    inStatus, found @ check line
                else
                    inStatus, found)
            (false, [])
        |> snd

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "release.json"))
           && Directory.Exists(Path.Combine(directory.FullName, "src", "Praxis.Domain")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate repository root"
        else
            repositoryRoot directory.Parent

    let private markdownUnder root (directory: string) =
        let full = Path.Combine(root, directory)

        if Directory.Exists full then
            Directory.GetFiles(full, "*.md", SearchOption.AllDirectories) |> Array.toList
        else
            []

    let private frontMatterId (file: string) =
        File.ReadLines file
        |> Seq.truncate 40
        |> Seq.tryPick (fun line -> if line.StartsWith "id:" then Some(line.Substring(3).Trim().Trim('"')) else None)

    let private workItemIds root =
        let idsOf (relative: string) (arrayName: string) =
            let path = Path.Combine(root, relative)

            if File.Exists path then
                use document = JsonDocument.Parse(File.ReadAllText path)

                [ for item in document.RootElement.GetProperty(arrayName).EnumerateArray() -> item.GetProperty("id").GetString() ]
            else
                []

        Set.ofList (idsOf ".ros/work/queue.json" "items" @ idsOf ".ros/context/current.json" "workItems")

    let private repositoryKnown root =
        { Records =
            [ "research"; "requirements"; "docs" ]
            |> List.collect (markdownUnder root)
            |> List.choose frontMatterId
            |> Set.ofList
          Paths = fun relative -> File.Exists(Path.Combine(root, relative)) || Directory.Exists(Path.Combine(root, relative))
          WorkItems = workItemIds root }

    let private sample =
        { Records = set [ "EV-ROS-2026-A070" ]
          Paths = fun path -> path = "tests/Praxis.Tests/GroupingTests.fs"
          WorkItems = set [ "PRAXIS-GROUP-07"; "WI-0001" ] }

    let tests =
        [ { Name = "status table citations: existing records, paths and work items pass"
            Run =
              fun () ->
                  Assert.empty (
                      danglingCitations
                          sample
                          "doc.md"
                          "## Requirement status\n\n| R | S |\n| --- | --- |\n| 080 | `EV-ROS-2026-A070`, `tests/Praxis.Tests/GroupingTests.fs:12`, `PRAXIS-GROUP-07`, `PRX-GRP-133`, `plan execute-group`, `UTF-8` |\n"
                  ) }
          { Name = "status table citations: a missing record, path or work item is reported"
            Run =
              fun () ->
                  Assert.equal
                      [ "doc.md: status row cites record EV-ROS-2026-A999, which does not exist"
                        "doc.md: status row cites tests/Gone.fs, which does not exist"
                        "doc.md: status row cites work item PRAXIS-GROUP-99, which is not in the backlog or live context" ]
                      (danglingCitations
                          sample
                          "doc.md"
                          "## Work-group requirement status\n| R | S |\n| 1 | `EV-ROS-2026-A999` `tests/Gone.fs` `PRAXIS-GROUP-99` |\n") }
          { Name = "status table citations: tables outside a status section are not checked"
            Run =
              fun () ->
                  Assert.empty (
                      danglingCitations sample "doc.md" "## Status\n\nText.\n\n## History\n| 1 | `EV-ROS-2026-A999` `tests/Gone.fs` |\n"
                  ) }
          { Name = "status table citations: every status table in docs/ and requirements/ cites only what exists"
            Run =
              fun () ->
                  let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
                  let known = repositoryKnown root

                  [ "docs"; "requirements" ]
                  |> List.collect (markdownUnder root)
                  |> List.collect (fun file ->
                      danglingCitations known (Path.GetRelativePath(root, file).Replace('\\', '/')) (File.ReadAllText file))
                  |> Assert.empty } ]
