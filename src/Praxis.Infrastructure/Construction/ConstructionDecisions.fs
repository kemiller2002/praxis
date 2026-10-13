namespace Praxis.Infrastructure.Construction

open System
open System.IO
open System.Text
open System.Text.Json
open Praxis.Infrastructure.Planning
open Praxis.Infrastructure.Work

/// Content-addressed, write-once records under the host construction root
/// (outside the repository, never followed through a link): approved
/// decision records and validated prepared artifacts (CONSTRUCT-008).
[<RequireQualifiedAccess>]
module ConstructionHostStore =
    let private maxBytes = 4194304

    let private hex (value: string) =
        if value.StartsWith "sha256:" && value.Length = 71 && value.Substring 7 |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f')) then Some(value.Substring 7)
        else None

    let private located (configuration: LocalHostConfiguration) (folder: string) (key: string) =
        match configuration.ConstructionRoot, hex key with
        | None, _ -> Error "construction is disabled: the host configuration names no construction root"
        | _, None -> Error "record key is not a SHA-256 digest"
        | Some store, Some name ->
            let directory = Path.Combine(LocalHostJournalFiles.rootOutside configuration.RepositoryRoot store, folder)
            Ok(directory, Path.Combine(directory, name + ".json"))

    /// Exact text stored under `key`, or None.
    let read (configuration: LocalHostConfiguration) (folder: string) (key: string) : Result<string option, string> =
        try
            located configuration folder key |> Result.bind (fun (_, path) ->
                if not (LocalHostJournalFiles.noLinks path) then Error "host record path contains a link"
                elif not (File.Exists path) then Ok None
                elif FileInfo(path).Length > int64 maxBytes then Error "host record exceeds 4 MiB"
                else Ok(Some(File.ReadAllText(path, UTF8Encoding(false, true)))))
        with error -> Error("host record unreadable: " + error.Message)

    /// Create `key`'s record with exactly `content`; an identical existing
    /// record is accepted, a different one refused.
    let writeOnce (configuration: LocalHostConfiguration) (folder: string) (key: string) (content: string) : Result<string, string> =
        try
            located configuration folder key |> Result.bind (fun (directory, path) ->
                let bytes = UTF8Encoding(false).GetBytes content
                if bytes.Length > maxBytes then Error "host record exceeds 4 MiB"
                elif not (LocalHostJournalFiles.noLinks directory) then Error "host record directory contains a link"
                else
                    Directory.CreateDirectory directory |> ignore
                    if not (LocalHostJournalFiles.noLinks path) then Error "host record path contains a link"
                    elif File.Exists path then
                        if File.ReadAllBytes path = bytes then Ok path else Error "a different host record already exists under this key"
                    else
                        do
                            use output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            output.Write(bytes, 0, bytes.Length)
                            output.Flush true
                        Ok path)
        with error -> Error(error.Message)

/// The envelope of an `ordo.construction-decisions/1` record set as Praxis
/// reads it. Praxis never interprets the records' semantics; Ordo does. The
/// host only checks identity (schema, cohort, blueprint) so a record set
/// approved for another cohort or blueprint is refused before generation.
type ConstructionDecisionsEnvelope =
    { CohortId: string
      BlueprintDigest: string
      NodeIds: string list }

/// Exact operator-approved construction decision records (CONSTRUCT-008).
/// Their SHA-256 over the exact bytes is bound into the signed grant
/// (`grant-draft --decisions F`); every later use compares the presented
/// bytes with that signed digest and refuses any difference.
[<RequireQualifiedAccess>]
module ConstructionDecisions =
    [<Literal>]
    let Schema = "ordo.construction-decisions/1"

    let private maxBytes = 1048576
    let private strict = UTF8Encoding(false, true)

    /// SHA-256 of the exact UTF-8 bytes, as `sha256:HEX`.
    let digest (text: string) = ConstructionTree.digest text

    /// Read a decision-record file without following links. The text must be
    /// strict UTF-8 without a byte-order mark, so its digest is the digest of
    /// the file's exact bytes.
    let readFile (path: string) : Result<string * string, string> =
        try
            let full = Path.GetFullPath path
            let info = FileInfo full
            if not info.Exists then Error "construction decisions file does not exist"
            elif not (isNull info.LinkTarget) || not (LocalHostJournalFiles.noLinks full) then Error "construction decisions file is or lies under a symbolic link"
            elif info.Length > int64 maxBytes then Error "construction decisions file exceeds 1 MiB"
            else
                let bytes = File.ReadAllBytes full
                if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then Error "construction decisions file starts with a byte-order mark"
                else
                    match (try Ok(strict.GetString bytes) with _ -> Error "construction decisions file is not UTF-8 text") with
                    | Error reason -> Error reason
                    | Ok text -> Ok(text, digest text)
        with error -> Error("construction decisions file unreadable: " + error.Message)

    /// Identity of the record set. Strict about the envelope only.
    let envelope (text: string) : Result<ConstructionDecisionsEnvelope, string> =
        try
            use document = JsonDocument.Parse(text, JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow))
            let root = document.RootElement
            let field (name: string) (element: JsonElement) =
                match element.TryGetProperty name with
                | true, value when value.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(value.GetString())) -> Ok(value.GetString())
                | _ -> Error(sprintf "construction decisions: %s must be a non-empty string" name)
            if root.ValueKind <> JsonValueKind.Object then Error "construction decisions must be a JSON object"
            else
                field "schemaVersion" root |> Result.bind (fun schema ->
                    if schema <> Schema then Error("construction decisions: unsupported schemaVersion " + schema)
                    else
                        field "cohortId" root |> Result.bind (fun cohort ->
                            field "blueprintDigest" root |> Result.bind (fun blueprint ->
                                match root.TryGetProperty "records" with
                                | true, records when records.ValueKind = JsonValueKind.Array ->
                                    records.EnumerateArray() |> Seq.toList
                                    |> List.fold (fun acc record ->
                                        acc |> Result.bind (fun ids ->
                                            if record.ValueKind <> JsonValueKind.Object then Error "construction decisions: every record must be an object"
                                            else field "nodeId" record |> Result.map (fun id -> ids @ [ id ]))) (Ok [])
                                    |> Result.bind (fun ids ->
                                        if ids.Length <> (Set.ofList ids).Count then Error "construction decisions: a node has more than one record"
                                        else Ok { CohortId = cohort; BlueprintDigest = blueprint; NodeIds = ids })
                                | _ -> Error "construction decisions: records must be an array")))
        with :? JsonException as error -> Error("construction decisions are not strict JSON: " + error.Message)

    /// Keep the approved bytes in the protected host store, write-once under
    /// their digest, so `construct apply` re-reads exactly what was signed.
    let store (configuration: LocalHostConfiguration) (text: string) : Result<string, string> =
        ConstructionHostStore.writeOnce configuration "decisions" (digest text) text
        |> Result.mapError ((+) "construction decisions store refused: ")

    /// The stored record whose bytes have exactly `expected` as digest.
    let load (configuration: LocalHostConfiguration) (expected: string) : Result<string, string> =
        ConstructionHostStore.read configuration "decisions" expected |> Result.bind (function
            | None -> Error "the host holds no copy of the signed construction decisions; run construct prepare with --decisions"
            | Some text when digest text <> expected -> Error "stored construction decisions differ from the signed digest"
            | Some text -> Ok text)
