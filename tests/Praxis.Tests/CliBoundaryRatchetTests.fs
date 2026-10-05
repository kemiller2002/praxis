namespace Praxis.Tests

open System
open System.IO
open System.Text.Json

/// PRX-ARCH-001/002: a ratchet on the CLI adapter boundary. Project-reference
/// tests prove dependency direction but not that domain behaviour stays out of
/// the CLI; the usage-pacing incident grew an 821-line domain/infrastructure
/// module inside `Praxis.Cli` while every test stayed green. This ratchet makes
/// that growth fail: per-file line counts and effect-API usage may only fall,
/// improvements must be locked in, and any excess needs an owned, expiring
/// exception in `quality/cli-boundary-baseline.json`.
module CliBoundaryRatchetTests =
    type FileMeasure = { Lines: int; Effects: int }

    type RatchetException =
        { Id: string
          Rule: string
          Path: string
          Limit: int
          Rationale: string
          Owner: string
          Created: DateOnly option
          Expires: DateOnly option
          Evidence: string }

    type Baseline =
        { EffectMarkers: string list
          NewFileLineBudget: int
          LineTightenTolerance: int
          Files: Map<string, FileMeasure>
          Exceptions: RatchetException list }

    let private t name run = { Name = $"cli boundary ratchet: {name}"; Run = run }

    let measure (markers: string list) (source: string) =
        let lines = source.Split('\n').Length - (if source.EndsWith('\n') then 1 else 0)

        let count (marker: string) =
            let rec loop (from: int) total =
                match source.IndexOf(marker, from, StringComparison.Ordinal) with
                | -1 -> total
                | index -> loop (index + marker.Length) (total + 1)

            loop 0 0

        { Lines = lines; Effects = markers |> List.sumBy count }

    let private exceptionFindings (today: DateOnly) (exceptions: RatchetException list) =
        exceptions
        |> List.collect (fun entry ->
            [ if String.IsNullOrWhiteSpace entry.Owner then yield $"exception {entry.Id} has no owner"
              if String.IsNullOrWhiteSpace entry.Rationale then yield $"exception {entry.Id} has no rationale"
              if String.IsNullOrWhiteSpace entry.Evidence then yield $"exception {entry.Id} has no evidence"
              if entry.Created.IsNone then yield $"exception {entry.Id} has no creation date"

              match entry.Expires with
              | None -> yield $"exception {entry.Id} has no expiry"
              | Some expiry when expiry < today -> yield $"exception {entry.Id} expired on {expiry:``yyyy-MM-dd``}; remove it or fix the regression"
              | Some _ -> () ])

    let private allowed (today: DateOnly) (exceptions: RatchetException list) rule path baseline =
        exceptions
        |> List.filter (fun entry ->
            entry.Rule = rule
            && entry.Path = path
            && entry.Expires |> Option.exists (fun expiry -> expiry >= today))
        |> List.fold (fun limit entry -> max limit entry.Limit) baseline

    /// Pure: every finding the measured CLI produces against the baseline.
    let evaluate (today: DateOnly) (baseline: Baseline) (measured: Map<string, FileMeasure>) : string list =
        let fileFindings =
            measured
            |> Map.toList
            |> List.collect (fun (path, actual) ->
                match baseline.Files |> Map.tryFind path with
                | None ->
                    [ if actual.Lines > allowed today baseline.Exceptions "PRX-ARCH-002" path baseline.NewFileLineBudget then
                          yield $"PRX-ARCH-002 {path}: new CLI file has {actual.Lines} lines (budget {baseline.NewFileLineBudget}); move behaviour into Application/Infrastructure"
                      if actual.Effects > allowed today baseline.Exceptions "PRX-ARCH-001" path 0 then
                          yield $"PRX-ARCH-001 {path}: new CLI file uses {actual.Effects} effect API(s); effects belong in Praxis.Infrastructure behind an Application port" ]
                | Some expected ->
                    [ let lineLimit = allowed today baseline.Exceptions "PRX-ARCH-002" path expected.Lines
                      let effectLimit = allowed today baseline.Exceptions "PRX-ARCH-001" path expected.Effects

                      if actual.Lines > lineLimit then
                          yield $"PRX-ARCH-002 {path}: grew from {expected.Lines} to {actual.Lines} lines; extract behaviour out of the CLI or add an owned, expiring exception"
                      elif actual.Lines < expected.Lines - baseline.LineTightenTolerance then
                          yield $"PRX-ARCH-002 {path}: shrank from {expected.Lines} to {actual.Lines} lines; lock in the improvement by lowering its baseline entry to {actual.Lines}"

                      if actual.Effects > effectLimit then
                          yield $"PRX-ARCH-001 {path}: effect API uses grew from {expected.Effects} to {actual.Effects}; effects belong in Praxis.Infrastructure"
                      elif actual.Effects < expected.Effects then
                          yield $"PRX-ARCH-001 {path}: effect API uses fell from {expected.Effects} to {actual.Effects}; lock in the improvement by lowering its baseline entry" ])

        let removed =
            baseline.Files
            |> Map.toList
            |> List.filter (fun (path, _) -> not (measured.ContainsKey path))
            |> List.map (fun (path, _) -> $"PRX-ARCH-002 {path}: no longer exists; remove its baseline entry")

        exceptionFindings today baseline.Exceptions @ fileFindings @ removed

    let private parseDate (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            match DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd") with
            | true, date -> Some date
            | _ -> None
        | _ -> None

    let private text (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> ""

    let parseBaseline (json: string) : Baseline =
        use document = JsonDocument.Parse json
        let root = document.RootElement

        { EffectMarkers = root.GetProperty("effectMarkers").EnumerateArray() |> Seq.map _.GetString() |> Seq.toList
          NewFileLineBudget = root.GetProperty("newFileLineBudget").GetInt32()
          LineTightenTolerance = root.GetProperty("lineTightenTolerance").GetInt32()
          Files =
            root.GetProperty("files").EnumerateObject()
            |> Seq.map (fun file ->
                file.Name,
                { Lines = file.Value.GetProperty("lines").GetInt32()
                  Effects = file.Value.GetProperty("effects").GetInt32() })
            |> Map.ofSeq
          Exceptions =
            root.GetProperty("exceptions").EnumerateArray()
            |> Seq.map (fun entry ->
                { Id = text entry "id"
                  Rule = text entry "rule"
                  Path = text entry "path"
                  Limit =
                    match entry.TryGetProperty "limit" with
                    | true, value when value.ValueKind = JsonValueKind.Number -> value.GetInt32()
                    | _ -> 0
                  Rationale = text entry "rationale"
                  Owner = text entry "owner"
                  Created = parseDate entry "created"
                  Expires = parseDate entry "expires"
                  Evidence = text entry "evidence" })
            |> Seq.toList }

    let rec private repositoryRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "quality", "cli-boundary-baseline.json")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "Could not locate quality/cli-boundary-baseline.json"
        else
            repositoryRoot directory.Parent

    let private sample =
        { EffectMarkers = [ "File." ]
          NewFileLineBudget = 10
          LineTightenTolerance = 2
          Files = Map.ofList [ "Program.fs", { Lines = 100; Effects = 3 } ]
          Exceptions = [] }

    let private today = DateOnly(2026, 10, 5)

    let private validException =
        { Id = "QX-1"
          Rule = "PRX-ARCH-002"
          Path = "Program.fs"
          Limit = 120
          Rationale = "temporary while extracting"
          Owner = "praxis-maintainers"
          Created = Some(DateOnly(2026, 10, 1))
          Expires = Some(DateOnly(2026, 11, 1))
          Evidence = "kemiller2002/praxis#1" }

    let tests =
        [ t "the repository CLI is within its ratchet" (fun () ->
              let root = repositoryRoot (DirectoryInfo(Directory.GetCurrentDirectory()))
              let baseline = parseBaseline (File.ReadAllText(Path.Combine(root, "quality", "cli-boundary-baseline.json")))
              let cli = Path.Combine(root, "src", "Praxis.Cli")

              let measured =
                  Directory.GetFiles(cli, "*.fs")
                  |> Array.map (fun path -> Path.GetFileName path, measure baseline.EffectMarkers (File.ReadAllText path))
                  |> Map.ofArray

              evaluate (DateOnly.FromDateTime DateTime.UtcNow) baseline measured |> Assert.empty)

          t "growth of an existing CLI file fails" (fun () ->
              let findings = evaluate today sample (Map.ofList [ "Program.fs", { Lines = 101; Effects = 3 } ])
              Assert.isTrue (findings |> List.exists _.StartsWith("PRX-ARCH-002 Program.fs: grew")) $"{findings}")

          t "new effect usage in an existing CLI file fails" (fun () ->
              let findings = evaluate today sample (Map.ofList [ "Program.fs", { Lines = 100; Effects = 4 } ])
              Assert.isTrue (findings |> List.exists _.StartsWith("PRX-ARCH-001 Program.fs: effect")) $"{findings}")

          t "a new CLI file over budget or with effects fails" (fun () ->
              let findings =
                  evaluate today sample (Map.ofList [ "Program.fs", { Lines = 100; Effects = 3 }; "New.fs", { Lines = 11; Effects = 1 } ])

              Assert.equal 2 (findings |> List.filter _.Contains("New.fs")).Length)

          t "an unlocked improvement fails so the baseline cannot keep headroom" (fun () ->
              let findings = evaluate today sample (Map.ofList [ "Program.fs", { Lines = 90; Effects = 2 } ])
              Assert.equal 2 findings.Length)

          t "a valid exception permits a bounded regression" (fun () ->
              let baseline = { sample with Exceptions = [ validException ] }
              evaluate today baseline (Map.ofList [ "Program.fs", { Lines = 115; Effects = 3 } ]) |> Assert.empty)

          t "an expired exception fails verification" (fun () ->
              let baseline = { sample with Exceptions = [ { validException with Expires = Some(DateOnly(2026, 10, 4)) } ] }
              let findings = evaluate today baseline (Map.ofList [ "Program.fs", { Lines = 115; Effects = 3 } ])
              Assert.isTrue (findings |> List.exists _.Contains("expired")) $"{findings}"
              Assert.isTrue (findings |> List.exists _.StartsWith("PRX-ARCH-002 Program.fs: grew")) "an expired exception no longer permits the regression")

          t "an exception without an owner is invalid" (fun () ->
              let baseline = { sample with Exceptions = [ { validException with Owner = "" } ] }
              let findings = evaluate today baseline (Map.ofList [ "Program.fs", { Lines = 100; Effects = 3 } ])
              Assert.isTrue (findings |> List.exists _.Contains("has no owner")) $"{findings}")

          t "an exception does not redefine the baseline" (fun () ->
              let baseline = { sample with Exceptions = [ validException ] }
              let findings = evaluate today baseline (Map.ofList [ "Program.fs", { Lines = 121; Effects = 3 } ])
              Assert.isTrue (findings |> List.exists _.StartsWith("PRX-ARCH-002 Program.fs: grew from 100")) $"{findings}") ]
