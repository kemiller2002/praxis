namespace Ros.Infrastructure.Artifacts

open System
open System.Text.RegularExpressions
open Ros.Domain.Artifacts
open Ros.Domain.Provenance

/// Append-only editing of a canonical artifact's front-matter `provenance`
/// block. The edit is a line insertion: every other byte of the document --
/// fields Praxis does not model, comments, the body -- is preserved, and
/// existing contributions are never rewritten.
[<RequireQualifiedAccess>]
module FrontMatterProvenance =
    let private indentation (line: string) = line.Length - line.TrimStart().Length

    /// Free text is always double-quoted so the front-matter scalar reader
    /// never reinterprets it as a list, number, or boolean; line breaks are
    /// flattened because front matter scalars are single-line.
    let private quote (value: string) =
        let flattened = Regex.Replace(value, "\\s*\\r?\\n\\s*", " ").Trim()
        $"\"{flattened}\""

    let rec private renderFields (indent: int) (fields: (string * OrderedValue) list) : string list =
        let pad = String(' ', indent)

        fields
        |> List.collect (fun (name, value) ->
            match value with
            | OrderedValue.Text text -> [ $"{pad}{name}: {quote text}" ]
            | OrderedValue.List items ->
                $"{pad}{name}:" :: (items |> List.collect (renderItem (indent + 2)))
            | OrderedValue.Fields nested -> $"{pad}{name}:" :: renderFields (indent + 2) nested)

    and private renderItem (indent: int) (value: OrderedValue) : string list =
        let pad = String(' ', indent)

        match value with
        | OrderedValue.Text text -> [ $"{pad}- {quote text}" ]
        | OrderedValue.Fields((firstName, firstValue) :: rest) ->
            let firstLines = renderFields (indent + 2) [ firstName, firstValue ]
            let head = $"{pad}- {firstLines.Head.TrimStart()}"
            head :: firstLines.Tail @ renderFields (indent + 2) rest
        | OrderedValue.Fields [] -> [ $"{pad}- {{}}" ]
        | OrderedValue.List items -> items |> List.collect (renderItem (indent + 2))

    /// The YAML lines of one contribution list item at a given indentation.
    let contributionLines (indent: int) (contribution: Contribution) =
        renderItem indent (ProvenanceCodec.contributionValue contribution)

    let private frontMatterBounds (lines: string array) =
        if lines.Length = 0 || lines[0].Trim() <> "---" then
            Error "missing opening '---'"
        else
            lines
            |> Array.indexed
            |> Array.tryPick (fun (index, line) -> if index > 0 && line.Trim() = "---" then Some index else None)
            |> Option.map Ok
            |> Option.defaultValue (Error "missing closing '---'")

    /// End (exclusive) of the block opened at `start`: the first following
    /// non-blank line indented at or below `indent`, or `limit`.
    let private blockEnd (lines: string array) (start: int) (indent: int) (limit: int) =
        seq { start + 1 .. limit - 1 }
        |> Seq.tryFind (fun index -> lines[index].Trim().Length > 0 && indentation lines[index] <= indent)
        |> Option.defaultValue limit

    let private lastContentLine (lines: string array) (start: int) (finish: int) =
        seq { finish - 1 .. -1 .. start }
        |> Seq.tryFind (fun index -> lines[index].Trim().Length > 0)
        |> Option.defaultValue start

    let private findKey (lines: string array) (from: int) (until: int) (indent: int) (key: string) =
        seq { from .. until - 1 }
        |> Seq.tryFind (fun index ->
            indentation lines[index] = indent
            && Regex.IsMatch(lines[index].Trim(), $"^{Regex.Escape key}:(\\s|$)"))

    /// Reads the current provenance (empty when absent) so the caller can
    /// decide the operation and check preservation.
    let read (relativePath: string) (text: string) : Result<Provenance * ProvenanceIssue list, string> =
        FrontMatter.parse relativePath text
        |> Result.map (fun document ->
            match document.Metadata |> Map.tryFind "provenance" with
            | Some value -> ProvenanceCodec.readProvenance value
            | None -> Provenance.empty, [])

    /// Appends `contribution` to the document's provenance, creating the
    /// block when it is absent. Refuses a document whose existing provenance
    /// is malformed instead of guessing, and is idempotent for a retried
    /// identical contribution.
    let append (relativePath: string) (text: string) (contribution: Contribution) : Result<string, string> =
        match read relativePath text with
        | Error message -> Error message
        | Ok(_, issues) when issues |> List.exists (fun issue -> issue.Code <> "empty-provenance") ->
            let issue = issues |> List.find (fun issue -> issue.Code <> "empty-provenance")
            Error $"existing provenance is malformed ({issue.Field}: {issue.Message}); repair it before recording a new contribution"
        | Ok(existing, _) when (Provenance.append contribution existing).Contributions.Length = existing.Contributions.Length ->
            Ok text
        | Ok _ ->
            let newline = if text.Contains "\r\n" then "\r\n" else "\n"
            let lines = Regex.Split(text, "\r?\n")

            frontMatterBounds lines
            |> Result.map (fun closing ->
                let insertAt, inserted =
                    match findKey lines 1 closing 0 "provenance" with
                    | None ->
                        closing,
                        [ "provenance:"; "  contributions:" ] @ contributionLines 4 contribution
                    | Some provenanceLine ->
                        let provenanceEnd = blockEnd lines provenanceLine 0 closing

                        match findKey lines (provenanceLine + 1) provenanceEnd 2 "contributions" with
                        | None ->
                            lastContentLine lines provenanceLine provenanceEnd + 1,
                            "  contributions:" :: contributionLines 4 contribution
                        | Some contributionsLine when Regex.IsMatch(lines[contributionsLine].Trim(), "^contributions:\\s*\\[\\s*\\]\\s*$") ->
                            contributionsLine, "  contributions:" :: contributionLines 4 contribution
                        | Some contributionsLine ->
                            let contributionsEnd = blockEnd lines contributionsLine 2 provenanceEnd
                            lastContentLine lines contributionsLine contributionsEnd + 1, contributionLines 4 contribution

                // An inline empty list is replaced; every other edit only inserts.
                let replaced =
                    insertAt < closing
                    && Regex.IsMatch(lines[insertAt].Trim(), "^contributions:\\s*\\[\\s*\\]\\s*$")

                let before = lines[.. insertAt - 1]
                let after = if replaced then lines[insertAt + 1 ..] else lines[insertAt..]
                Array.concat [ before; List.toArray inserted; after ] |> String.concat newline)
