namespace Ros.Contracts.Provenance

open System
open System.Text.RegularExpressions
open Ros.Domain.Provenance

/// The canonical front-matter serialization of artifact provenance:
///
/// ```yaml
/// provenance:
///   contributions:
///     EXE-20260925T194000000Z-ab12cd34:
///       operations: [created]
///       at: 2026-09-25T19:40:00.000Z
///       last: 2026-09-25T20:10:00.000Z      # optional: latest operation
///       actor:
///         kind: agent
///         id: anthropic/claude-code
///         provider: anthropic
///         model: unknown
///         runtime: claude-code
///       reason: "Initial capture"
///       evidence: [EV-ROS-2026-A049]
/// ```
///
/// Contributions are a mapping keyed by execution (or `CTB-...`) ID because
/// the repository's front-matter reader supports nested mappings but not
/// sequences of mappings; a keyed mapping also makes "one entry per
/// execution" structural. Edits are surgical text insertions: an existing
/// contributor's entry is never re-rendered, so fields this version does not
/// model (for example a future attestation) survive untouched.
[<RequireQualifiedAccess>]
module ProvenanceFrontMatter =
    let private plainToken = Regex("^[A-Za-z0-9._/@+:-]+$", RegexOptions.CultureInvariant)
    let private numberLike = Regex("^-?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)
    let private listItemToken = Regex("^[^\s,\[\]\"']+$", RegexOptions.CultureInvariant)

    /// Front matter is line-structured: a value may not span lines. Line
    /// breaks become spaces; everything else (including leading/trailing
    /// spaces, which a quoted scalar preserves) is kept.
    let normalize (value: string) =
        value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ")

    /// A scalar the front-matter reader will read back verbatim: plain
    /// tokens stay bare; anything that could be mistaken for a list,
    /// boolean, number, or comment is double-quoted.
    let scalar (value: string) =
        let text = normalize value

        if plainToken.IsMatch text
           && not (numberLike.IsMatch text)
           && not (String.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
           && not (String.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) then
            text
        else
            "\"" + text + "\""

    /// Whether a value can be written as an item of an inline `[a, b]` list
    /// and read back unchanged.
    let isListSafe (value: string) = listItemToken.IsMatch value

    let private inlineList (items: string list) = "[" + String.concat ", " items + "]"

    let private pad count = String(' ', count)

    let private operationCodes (contribution: Contribution) =
        contribution.Operations |> List.map ContributionOperation.code

    let entryLines (indent: int) (contribution: Contribution) : string list =
        let field depth (name: string) (value: string) = $"{pad (indent + depth)}{name}: {value}"
        let actor = contribution.Actor

        [ $"{pad indent}{contribution.Key}:"
          field 2 "operations" (inlineList (operationCodes contribution))
          field 2 "at" contribution.At
          yield! contribution.Last |> Option.map (field 2 "last") |> Option.toList
          $"{pad (indent + 2)}actor:"
          field 4 "kind" (ActorKind.code actor.Kind)
          field 4 "id" (scalar actor.Id)
          yield!
              [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ]
              |> List.choose (fun (name, value) -> value |> Option.map (fun text -> field 4 name (scalar text)))
          yield! contribution.Reason |> Option.map (fun reason -> field 2 "reason" ("\"" + normalize reason + "\"")) |> Option.toList
          if not contribution.Evidence.IsEmpty then
              field 2 "evidence" (inlineList contribution.Evidence) ]

    // ---- line-structured front matter ------------------------------------

    /// Lines are kept exactly as they appear, including any trailing `\r`,
    /// so untouched lines -- and the whole document body -- are preserved
    /// byte for byte. `Suffix` is the opening delimiter's line terminator
    /// style (`"\r"` for CRLF front matter), applied only to lines this
    /// module generates.
    type private Document =
        { Suffix: string
          Opening: string
          Body: string list
          Rest: string list }

    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    let private isContent (line: string) =
        let trimmed = line.Trim()
        trimmed.Length > 0 && not (trimmed.StartsWith("#", StringComparison.Ordinal))

    let private split (text: string) : Result<Document, string> =
        let lines = text.Split('\n') |> Array.toList

        match lines with
        | first :: rest when first.Trim() = "---" ->
            match rest |> List.tryFindIndex (fun line -> line.Trim() = "---") with
            | Some closing ->
                Ok
                    { Suffix = if first.EndsWith("\r", StringComparison.Ordinal) then "\r" else ""
                      Opening = first
                      Body = rest |> List.take closing
                      Rest = rest |> List.skip closing }
            | None -> Error "missing closing '---'"
        | _ -> Error "missing opening '---'"

    let private join (document: Document) =
        document.Opening :: document.Body @ document.Rest |> String.concat "\n"

    /// Everything after the closing front-matter delimiter, verbatim, used
    /// to prove an edit never touched the document body.
    let bodyOf (text: string) =
        match split text with
        | Ok document -> Some(document.Rest |> String.concat "\n")
        | Error _ -> None

    /// Index of the last line belonging to the block that starts at `start`
    /// (whose own indentation is `indent`): every following line that is
    /// blank, a comment, or indented deeper, up to the last content line.
    let private blockLast (body: string array) (start: int) (indent: int) =
        let rec scan index last =
            if index >= body.Length then last
            elif not (isContent body[index]) then scan (index + 1) last
            elif indentOf body[index] > indent then scan (index + 1) index
            else last

        scan (start + 1) start

    let private firstChildIndent (body: string array) (start: int) (last: int) =
        [ start + 1 .. last ]
        |> List.tryFind (fun index -> isContent body[index])
        |> Option.map (fun index -> indentOf body[index])

    let private findChild (body: string array) (start: int) (last: int) (indent: int) (key: string) =
        [ start + 1 .. last ]
        |> List.tryFind (fun index ->
            isContent body[index] && indentOf body[index] = indent && body[index].Trim() = key + ":")

    let private findField (body: string array) (start: int) (last: int) (indent: int) (name: string) =
        [ start + 1 .. last ]
        |> List.tryFind (fun index ->
            isContent body[index]
            && indentOf body[index] = indent
            && body[index].TrimStart().StartsWith(name + ":", StringComparison.Ordinal))

    let private insertAt (index: int) (additions: string list) (body: string array) =
        (body[.. index - 1] |> Array.toList) @ additions @ (body[index..] |> Array.toList) |> List.toArray

    /// Replaces the field line at `index` (and any deeper-indented block
    /// continuation beneath it) with one inline line.
    let private replaceField (index: int) (line: string) (body: string array) =
        let last = blockLast body index (indentOf body[index])
        (body[.. index - 1] |> Array.toList) @ [ line ] @ (body[last + 1 ..] |> Array.toList) |> List.toArray

    let private mergeIntoEntry (suffix: string) (body: string array) (entryIndex: int) (contribution: Contribution) =
        let entryIndent = indentOf body[entryIndex]
        let last = blockLast body entryIndex entryIndent
        let fieldIndent = firstChildIndent body entryIndex last |> Option.defaultValue (entryIndent + 2)
        let line name value = $"{pad fieldIndent}{name}: {value}{suffix}"

        let withOperations =
            match findField body entryIndex last fieldIndent "operations" with
            | Some index -> replaceField index (line "operations" (inlineList (operationCodes contribution))) body
            | None -> insertAt (last + 1) [ line "operations" (inlineList (operationCodes contribution)) ] body

        let last = blockLast withOperations entryIndex entryIndent

        let withEvidence =
            match contribution.Evidence, findField withOperations entryIndex last fieldIndent "evidence" with
            | [], _ -> withOperations
            | evidence, Some index -> replaceField index (line "evidence" (inlineList evidence)) withOperations
            | evidence, None -> insertAt (last + 1) [ line "evidence" (inlineList evidence) ] withOperations

        let last = blockLast withEvidence entryIndex entryIndent

        let withReason =
            match contribution.Reason, findField withEvidence entryIndex last fieldIndent "reason" with
            | Some reason, None -> insertAt (last + 1) [ line "reason" ("\"" + normalize reason + "\"") ] withEvidence
            | _ -> withEvidence

        let last = blockLast withReason entryIndex entryIndent

        match contribution.Last, findField withReason entryIndex last fieldIndent "last" with
        | None, _ -> withReason
        | Some value, Some index -> replaceField index (line "last" value) withReason
        | Some value, None ->
            match findField withReason entryIndex last fieldIndent "at" with
            | Some atIndex -> insertAt (atIndex + 1) [ line "last" value ] withReason
            | None -> insertAt (last + 1) [ line "last" value ] withReason

    /// Writes `contribution` -- already merged by `ArtifactProvenance.record`
    /// -- into the front matter of `text`, creating the `provenance` block
    /// and `contributions` mapping when absent. Only the touched entry's own
    /// `operations`/`last`/`evidence`/`reason` lines change; every other
    /// line, and the document body, is preserved byte for byte.
    let writeContribution (contribution: Contribution) (text: string) : Result<string, string> =
        match split text with
        | Error message -> Error message
        | Ok document ->
            let body = document.Body |> List.toArray
            let generated (lines: string list) = lines |> List.map (fun line -> line + document.Suffix)

            let provenanceIndex =
                body |> Array.tryFindIndex (fun line -> isContent line && indentOf line = 0 && line.Trim().StartsWith("provenance:", StringComparison.Ordinal))

            let updated =
                match provenanceIndex with
                | None ->
                    Ok(Array.append body ([ "provenance:"; "  contributions:" ] @ entryLines 4 contribution |> generated |> List.toArray))
                | Some index when body[index].Trim() <> "provenance:" ->
                    Error "'provenance' must be a block mapping to be updated automatically"
                | Some index ->
                    let last = blockLast body index 0
                    let childIndent = firstChildIndent body index last |> Option.defaultValue 2

                    match findChild body index last childIndent "contributions" with
                    | None ->
                        Ok(insertAt (last + 1) ($"{pad childIndent}contributions:" :: entryLines (childIndent + 2) contribution |> generated) body)
                    | Some contributionsIndex ->
                        let contributionsLast = blockLast body contributionsIndex childIndent

                        let entryIndent =
                            firstChildIndent body contributionsIndex contributionsLast |> Option.defaultValue (childIndent + 2)

                        match findChild body contributionsIndex contributionsLast entryIndent contribution.Key with
                        | Some entryIndex -> Ok(mergeIntoEntry document.Suffix body entryIndex contribution)
                        | None -> Ok(insertAt (contributionsLast + 1) (entryLines entryIndent contribution |> generated) body)

            updated |> Result.map (fun lines -> join { document with Body = Array.toList lines })

    /// Adds lineage references to the artifact's `derived_from` reference
    /// field. `existing` is the field's current value as the front-matter
    /// reader sees it (never re-parsed from raw text here, so quoting or
    /// commas inside an existing value cannot be misread); the field is
    /// rewritten as `existing @ additions`, in order. A value that cannot be
    /// written back as an inline list item unchanged is refused rather than
    /// risked.
    let addDerivedFrom (existing: string list) (references: string list) (text: string) : Result<string, string> =
        let additions =
            references |> List.distinct |> List.filter (fun reference -> not (List.contains reference existing))

        match additions with
        | [] -> Ok text
        | _ ->
            match existing |> List.filter (isListSafe >> not) with
            | unsafe when not unsafe.IsEmpty ->
                let values = String.concat "; " unsafe
                Error $"derived_from holds values that cannot be rewritten safely ({values}); add the lineage by hand"
            | _ ->
                match split text with
                | Error message -> Error message
                | Ok document ->
                    let body = document.Body |> List.toArray
                    let fieldName = Lineage.FieldName
                    let fieldLine = $"{fieldName}: {inlineList (existing @ additions)}{document.Suffix}"

                    let index =
                        body |> Array.tryFindIndex (fun line -> isContent line && indentOf line = 0 && line.StartsWith(fieldName + ":", StringComparison.Ordinal))

                    let updated =
                        match index with
                        | None -> Array.append body [| fieldLine |]
                        | Some fieldIndex -> replaceField fieldIndex fieldLine body

                    Ok(join { document with Body = Array.toList updated })
