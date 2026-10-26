namespace Praxis.Application.Requirements

open System.Text
open Praxis.Contracts.Requirements
open Praxis.Domain.Requirements

/// Orchestrates `praxis requirements preservation verify` (REQ-PRES-001):
/// reads the inputs through a port, decodes them, and runs the pure gate.
/// No filesystem here; Infrastructure supplies the port.
[<RequireQualifiedAccess>]
module PreservationGate =
    /// What the gate needs from the outside world.
    type Inputs =
        { /// The bytes of a file named on the command line.
          ReadFile: string -> Result<byte array, string>
          /// The markdown files of the derived package directory, as
          /// (path relative to that directory, bytes), in ordinal path order.
          ReadDerived: string -> Result<(string * byte array) list, string>
          /// The text of a source document under the source root, or `None`
          /// when it does not exist there.
          ReadSource: string -> string -> Result<string option, string> }

    type Request =
        { Baseline: string
          Disposition: string
          Approvals: string option
          /// The approvals digest the approving authority published (REQ-PRES-001-R2).
          ApprovalsSha256: string option
          /// Identities whose approvals and reviews the gate honours.
          TrustedApprovers: string list
          Derived: string
          Source: string }

    type Outcome =
        { Inputs: PreservationJson.ReportInputs
          Report: PreservationReport }

    let private text (bytes: byte array) = Encoding.UTF8.GetString bytes

    let private file path (bytes: byte array) : PreservationJson.InputFile =
        { Path = path
          Sha256 = Preservation.sha256Bytes bytes }

    let private decode (label: string) path decoder (bytes: byte array) =
        decoder (text bytes) |> Result.mapError (fun reason -> $"{label} '{path}' is not a valid document: {reason}")

    type private ResultBuilder() =
        member _.Bind(value, next) = Result.bind next value
        member _.Return value = Ok value

    let private result = ResultBuilder()

    let private all (results: Result<'T, string> list) =
        results
        |> List.fold (fun acc r -> acc |> Result.bind (fun xs -> r |> Result.map (fun x -> x :: xs))) (Ok [])
        |> Result.map List.rev

    /// `Error reason` means the inputs could not be read or decoded: no
    /// verdict is possible, which the CLI reports as invalid input.
    let verify (inputs: Inputs) (request: Request) : Result<Outcome, string> =
        let readDecoded label path decoder =
            inputs.ReadFile path
            |> Result.bind (fun bytes -> decode label path decoder bytes |> Result.map (fun value -> file path bytes, value))

        result {
            let! baselineFile, baseline = readDecoded "baseline" request.Baseline PreservationJson.decodeBaseline
            let! dispositionFile, dispositions = readDecoded "disposition" request.Disposition PreservationJson.decodeDisposition

            let! approvalsFile, approvals =
                match request.Approvals with
                | None -> Ok(None, [])
                | Some path -> readDecoded "approvals" path PreservationJson.decodeApprovals |> Result.map (fun (f, a) -> Some f, a)

            let! derivedFiles = inputs.ReadDerived request.Derived

            let! observations =
                baseline.Documents
                |> List.map (fun d ->
                    inputs.ReadSource request.Source d.Path
                    |> Result.map (fun t -> ({ Path = d.Path; Text = t }: SourceObservation)))
                |> all

            let parsed = derivedFiles |> List.map (fun (path, bytes) -> path, bytes, Preservation.parseDerived path (text bytes))
            let criteria = parsed |> List.collect (fun (_, _, c) -> c)

            let derived =
                parsed
                |> List.map (fun (path, bytes, c) ->
                    ({ File = file path bytes
                       Requirement = c |> List.tryHead |> Option.map _.Requirement
                       Criteria = c.Length }: PreservationJson.DerivedFile))

            let trust: InputTrust =
                { BaselineSha256 = baselineFile.Sha256
                  ApprovalsSha256 = approvalsFile |> Option.map _.Sha256
                  PinnedApprovalsSha256 = request.ApprovalsSha256
                  TrustedApprovers = request.TrustedApprovers }

            return
                { Inputs =
                    { Baseline = baselineFile
                      BaselineValue = baseline
                      Disposition = dispositionFile
                      Package = dispositions.Package
                      Approvals = approvalsFile
                      Trust = trust
                      RecordedBaselineSha256 = dispositions.BaselineSha256
                      DerivedRoot = request.Derived
                      DerivedFiles = derived
                      SourceRoot = request.Source }
                  Report = Preservation.evaluate trust baseline observations dispositions approvals criteria }
        }

    /// `requirements preservation sections`: the heading sections of one
    /// source document with their hashes, to pin a baseline from.
    let sections (inputs: Inputs) (source: string) (document: string) (pattern: string option) =
        inputs.ReadSource source document
        |> Result.bind (fun found ->
            match found with
            | None -> Error $"source document '{document}' does not exist under '{source}'"
            | Some t -> Ok(Preservation.sha256Text t, Preservation.sections pattern t))
