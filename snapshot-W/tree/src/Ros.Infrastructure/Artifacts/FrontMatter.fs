namespace Ros.Infrastructure.Artifacts

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open Ros.Domain.Artifacts

type private MutableArtifactValue =
    | MutableScalar of ArtifactValue
    | MutableSequence of ResizeArray<MutableArtifactValue>
    | MutableMapping of Dictionary<string, MutableArtifactValue>

[<RequireQualifiedAccess>]
module FrontMatter =
    let private numberPattern =
        Regex("^-?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)

    let private stripBoundaryQuotes (value: string) =
        let withoutLeading =
            if value.Length > 0 && (value[0] = '\'' || value[0] = '"') then
                value[1..]
            else
                value

        if withoutLeading.Length > 0
           && (withoutLeading[withoutLeading.Length - 1] = '\''
               || withoutLeading[withoutLeading.Length - 1] = '"') then
            withoutLeading[.. withoutLeading.Length - 2]
        else
            withoutLeading

    let private scalar (raw: string) =
        let value = raw.Trim()

        if value.Length = 0 then
            ArtifactValue.Text ""
        elif value = "[]" then
            ArtifactValue.Sequence []
        elif value = "{}" then
            ArtifactValue.Mapping Map.empty
        elif value.StartsWith("[", StringComparison.Ordinal)
             && value.EndsWith("]", StringComparison.Ordinal) then
            let inner = value[1 .. value.Length - 2].Trim()

            if inner.Length = 0 then
                ArtifactValue.Sequence []
            else
                inner.Split(',')
                |> Array.map (fun item -> item.Trim() |> stripBoundaryQuotes |> ArtifactValue.Text)
                |> Array.toList
                |> ArtifactValue.Sequence
        elif String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) then
            ArtifactValue.Boolean true
        elif String.Equals(value, "false", StringComparison.OrdinalIgnoreCase) then
            ArtifactValue.Boolean false
        elif numberPattern.IsMatch value then
            ArtifactValue.Number(Double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture))
        else
            ArtifactValue.Text(stripBoundaryQuotes value)

    let rec private freeze value =
        match value with
        | MutableScalar item -> item
        | MutableSequence items ->
            items |> Seq.map freeze |> Seq.toList |> ArtifactValue.Sequence
        | MutableMapping items ->
            items
            |> Seq.map (fun pair -> pair.Key, freeze pair.Value)
            |> Map.ofSeq
            |> ArtifactValue.Mapping

    let private indentation (line: string) =
        line.Length - line.TrimStart().Length

    let private popToParent indent stack =
        let rec pop remaining =
            match remaining with
            | (currentIndent, _) :: rest when currentIndent >= indent -> pop rest
            | _ -> remaining

        pop stack

    let parse (relativePath: string) (text: string) =
        let lines = Regex.Split(text, "\r?\n")

        if lines.Length = 0 || lines[0].Trim() <> "---" then
            Error "missing opening '---'"
        else
            let closing =
                lines
                |> Array.indexed
                |> Array.tryPick (fun (index, line) ->
                    if index > 0 && line.Trim() = "---" then Some index else None)

            match closing with
            | None -> Error "missing closing '---'"
            | Some closingIndex ->
                let root = MutableMapping(Dictionary<string, MutableArtifactValue>(StringComparer.Ordinal))

                let rec parseLines index stack =
                    if index >= closingIndex then
                        Ok()
                    else
                        let raw = lines[index]
                        let stripped = raw.Trim()

                        if stripped.Length = 0 || raw.TrimStart().StartsWith("#", StringComparison.Ordinal) then
                            parseLines (index + 1) stack
                        else
                            let indent = indentation raw
                            let parentStack = popToParent indent stack

                            match parentStack with
                            | [] -> Error $"line {index + 1}: expected 'field: value'"
                            | (_, parent) :: _ when stripped.StartsWith("- ", StringComparison.Ordinal) ->
                                match parent with
                                | MutableSequence values ->
                                    values.Add(MutableScalar(scalar stripped[2..]))
                                    parseLines (index + 1) parentStack
                                | _ -> Error $"line {index + 1}: list item has no list field"
                            | (_, MutableSequence _) :: _ ->
                                Error $"line {index + 1}: expected 'field: value'"
                            | (_, MutableScalar _) :: _ ->
                                Error $"line {index + 1}: expected 'field: value'"
                            | (_, MutableMapping mapping) :: _ ->
                                let separator = stripped.IndexOf(':')

                                if separator < 1 then
                                    Error $"line {index + 1}: expected 'field: value'"
                                else
                                    let key = stripped[.. separator - 1].Trim()
                                    let rawValue = stripped[separator + 1 ..]

                                    if rawValue.Trim().Length > 0 then
                                        mapping[key] <- MutableScalar(scalar rawValue)
                                        parseLines (index + 1) parentStack
                                    else
                                        let next =
                                            if index + 1 < lines.Length then lines[index + 1] else ""

                                        let nextIndent = indentation next

                                        let child =
                                            if nextIndent > indent
                                               && next.Trim().StartsWith("- ", StringComparison.Ordinal) then
                                                MutableSequence(ResizeArray<MutableArtifactValue>())
                                            else
                                                MutableMapping(Dictionary<string, MutableArtifactValue>(StringComparer.Ordinal))

                                        mapping[key] <- child
                                        parseLines (index + 1) ((indent, child) :: parentStack)

                match parseLines 1 [ (-1, root) ] with
                | Error message -> Error message
                | Ok() ->
                    match freeze root with
                    | ArtifactValue.Mapping metadata ->
                        Ok
                            { RelativePath = relativePath
                              FileName = relativePath.Replace('\\', '/').Split('/') |> Array.last
                              Metadata = metadata }
                    | _ -> Error "front matter root must be a mapping"
