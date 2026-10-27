namespace Praxis.Domain.Verification

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Praxis.Domain.Provenance
open Praxis.Domain.Work

/// What admission established about a compiled verification plan
/// (`indy-init.verification-plan/1`): its identity, the input it came from,
/// and what it covers. Praxis owns this admission (intake, identity, digest,
/// provenance); Ordo owns scheduling the plan (`sde verification schedule`),
/// which the handoff names but Praxis never performs.
type VerificationPlanIdentity =
    { PlanId: string
      /// Lowercase hex SHA-256 recomputed by Praxis, equal to the plan's
      /// declared `planIdentity.digest`.
      Digest: string
      SourceRepository: string
      SourceCommit: string
      BaselineId: string
      BaselineSha256: string
      Requirements: int
      Obligations: int
      MissingTestObligations: int }

/// The record Praxis hands to Ordo: which admitted bytes to schedule, the
/// digest they must carry, and the input claim and executor they came from.
type VerificationPlanHandoff =
    { ClaimId: string
      Input: InputSource
      Plan: VerificationPlanIdentity
      /// Repository-relative path of the admitted plan bytes (identical to
      /// the claimed input; SHA-256 `Input.Sha256`).
      AdmittedPath: string
      AdmittedAt: string
      AdmittedBy: Actor
      ExecutionId: string option }

/// Admission of a verification plan received through the input-document
/// lifecycle (`praxis inbox claim`). Pure: the caller supplies the claimed
/// bytes and the claim; nothing here reads a file or a clock.
[<RequireQualifiedAccess>]
module VerificationPlanIntake =
    [<Literal>]
    let PlanSchema = "indy-init.verification-plan/1"

    [<Literal>]
    let HandoffSchema = "praxis.verification-plan-handoff/1"

    [<Literal>]
    let ScheduleSchema = "ordo.verification-schedule/1"

    let private hex (bytes: byte array) = Convert.ToHexString(bytes).ToLowerInvariant()
    let sha256 (bytes: byte array) = SHA256.HashData bytes |> hex

    let private escape (value: string) =
        let builder = StringBuilder(value.Length + 2)
        builder.Append('"') |> ignore

        for character in value do
            match character with
            | '"' -> builder.Append("\\\"") |> ignore
            | '\\' -> builder.Append("\\\\") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | '\b' -> builder.Append("\\b") |> ignore
            | '\f' -> builder.Append("\\f") |> ignore
            | c when c < ' ' || c > '~' -> builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
            | c -> builder.Append(c) |> ignore

        builder.Append('"').ToString()

    /// The plan producer's canonical writer (indy-init
    /// `tools/requirements-compiler`): document member order, ASCII-only
    /// strings with lowercase `\uXXXX` escapes, two-space indentation and a
    /// trailing newline. Only the plan identity is taken over this writer.
    let rec private write (builder: StringBuilder) (indent: int) (skip: string option) (element: JsonElement) =
        let pad n = String(' ', n)

        match element.ValueKind with
        | JsonValueKind.Null -> builder.Append("null") |> ignore
        | JsonValueKind.True -> builder.Append("true") |> ignore
        | JsonValueKind.False -> builder.Append("false") |> ignore
        | JsonValueKind.String -> builder.Append(escape (element.GetString())) |> ignore
        | JsonValueKind.Number ->
            match element.TryGetInt64() with
            | true, value -> builder.Append(value.ToString(CultureInfo.InvariantCulture)) |> ignore
            | _ -> builder.Append(element.GetDecimal().ToString("0.###", CultureInfo.InvariantCulture)) |> ignore
        | JsonValueKind.Array ->
            let items = element.EnumerateArray() |> List.ofSeq

            if items.IsEmpty then builder.Append("[]") |> ignore
            else
                builder.Append("[\n") |> ignore

                items
                |> List.iteri (fun index item ->
                    builder.Append(pad (indent + 2)) |> ignore
                    write builder (indent + 2) None item
                    builder.Append(if index < items.Length - 1 then ",\n" else "\n") |> ignore)

                builder.Append(pad indent).Append("]") |> ignore
        | JsonValueKind.Object ->
            let members = element.EnumerateObject() |> Seq.filter (fun p -> Some p.Name <> skip) |> List.ofSeq

            if members.IsEmpty then builder.Append("{}") |> ignore
            else
                builder.Append("{\n") |> ignore

                members
                |> List.iteri (fun index property ->
                    builder.Append(pad (indent + 2)).Append(escape property.Name).Append(": ") |> ignore
                    write builder (indent + 2) None property.Value
                    builder.Append(if index < members.Length - 1 then ",\n" else "\n") |> ignore)

                builder.Append(pad indent).Append("}") |> ignore
        | kind -> failwithf "unsupported JSON value kind %A" kind

    /// SHA-256 of the plan's canonical serialization without `planIdentity`.
    let recomputeDigest (root: JsonElement) =
        let builder = StringBuilder()
        write builder 0 (Some "planIdentity") root
        builder.Append('\n') |> ignore
        Encoding.UTF8.GetBytes(builder.ToString()) |> sha256

    let private isHex length (value: string) =
        not (isNull value) && value.Length = length && value |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))

    let private property (name: string) (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Object then
            match element.TryGetProperty name with
            | true, value -> Some value
            | _ -> None
        else None

    let private text name element =
        property name element
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map (fun v -> v.GetString())

    let private integer name element =
        property name element
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
        |> Option.bind (fun v -> match v.TryGetInt32() with | true, n -> Some n | _ -> None)

    let private array name element =
        property name element
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array)
        |> Option.map (fun v -> v.EnumerateArray() |> List.ofSeq)

    /// Every reason the claimed bytes are not an admissible plan. Empty
    /// means admitted: the bytes are the claimed input, the plan identity
    /// recomputes, ids are stable-format and unique, and the declared scope
    /// counts match the content (nothing was silently dropped).
    let problems (source: InputSource) (bytes: byte array) : string list * VerificationPlanIdentity option =
        let actual = sha256 bytes

        if actual <> source.Sha256 then
            [ $"the claimed copy hashes to {actual}, the claim recorded {source.Sha256}" ], None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory bytes)
                let root = document.RootElement
                let identity = property "planIdentity" root
                let declared = identity |> Option.bind (text "digest")
                let planId = identity |> Option.bind (text "planId")
                let recomputed = recomputeDigest root
                let source' = property "source" root
                let scope = property "scope" root
                let requirements = array "requirements" root |> Option.defaultValue []
                let requirementIds = requirements |> List.map (fun r -> text "requirement" r |> Option.defaultValue "")
                let obligations = requirements |> List.collect (fun r -> array "obligations" r |> Option.defaultValue [])
                let obligationIds = obligations |> List.map (fun o -> text "id" o |> Option.defaultValue "")

                let missing =
                    obligations
                    |> List.filter (fun o -> property "testIdentity" o |> Option.bind (text "status") = Some "missing")
                    |> List.length

                let duplicates values = values |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

                let found =
                    [ if text "schema" root <> Some PlanSchema then yield $"schema is not {PlanSchema}"
                      if identity.IsNone then yield "planIdentity is missing"
                      if identity |> Option.bind (text "algorithm") <> Some "sha256" then yield "planIdentity.algorithm is not sha256"
                      if not (declared |> Option.exists (isHex 64)) then yield "planIdentity.digest is not a lowercase SHA-256"
                      elif declared <> Some recomputed then yield $"planIdentity.digest {declared.Value} does not match the recomputed {recomputed}"
                      if planId <> Some("VP-" + recomputed.Substring(0, 12)) then yield "planIdentity.planId does not derive from the digest"
                      if source'.IsNone then yield "source is missing"
                      if not (source' |> Option.bind (text "commit") |> Option.exists (isHex 40)) then yield "source.commit is not a 40-hex commit"
                      if not (source' |> Option.bind (text "baselineSha256") |> Option.exists (isHex 64)) then yield "source.baselineSha256 is not a SHA-256"
                      if requirementIds |> List.exists String.IsNullOrWhiteSpace then yield "a requirement has no id"
                      for id in duplicates requirementIds do yield $"requirement {id} appears more than once"
                      if requirementIds <> (requirementIds |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))) then yield "requirements are not sorted ordinally by id"
                      for id in obligationIds |> List.filter (fun id -> not (id.StartsWith("VPO-", StringComparison.Ordinal) && isHex 16 (id.Substring(min 4 id.Length)))) do
                          yield $"obligation id '{id}' is not VPO- plus 16 hex digits"
                      for id in duplicates obligationIds do yield $"obligation {id} appears more than once"
                      for requirement in requirements do
                          for dependency in property "dependencies" requirement |> Option.bind (array "requirements") |> Option.defaultValue [] do
                              if dependency.ValueKind <> JsonValueKind.String then yield "a dependency is not a requirement id"
                      if scope |> Option.bind (integer "requirements") <> Some requirements.Length then yield $"scope.requirements does not equal the {requirements.Length} requirements present"
                      if scope |> Option.bind (integer "obligations") <> Some obligations.Length then yield $"scope.obligations does not equal the {obligations.Length} obligations present"
                      if scope |> Option.bind (integer "missingTestObligations") <> Some missing then yield "scope.missingTestObligations does not match the obligations marked missing" ]

                match found, source' with
                | [], Some plannedSource ->
                    [],
                    Some
                        { PlanId = planId.Value
                          Digest = recomputed
                          SourceRepository = text "repository" plannedSource |> Option.defaultValue ""
                          SourceCommit = text "commit" plannedSource |> Option.defaultValue ""
                          BaselineId = text "baselineId" plannedSource |> Option.defaultValue ""
                          BaselineSha256 = text "baselineSha256" plannedSource |> Option.defaultValue ""
                          Requirements = requirements.Length
                          Obligations = obligations.Length
                          MissingTestObligations = missing }
                | found, _ -> found, None
            with :? JsonException as ex ->
                [ $"the claimed input is not JSON: {ex.Message}" ], None

    /// Admits the claimed plan, or refuses with every problem found.
    let admit
        (claim: InputClaim)
        (bytes: byte array)
        (admittedPath: string)
        (at: string)
        (actor: Actor)
        (execution: string option)
        : Result<VerificationPlanHandoff, InputInboxError> =
        if claim.State <> InputClaimState.Claimed then
            Error(InputInboxError.WrongState(claim.ClaimId, claim.State, "admit-plan"))
        else
            match problems claim.Source bytes with
            | [], Some identity ->
                Ok
                    { ClaimId = claim.ClaimId
                      Input = claim.Source
                      Plan = identity
                      AdmittedPath = admittedPath
                      AdmittedAt = at
                      AdmittedBy = actor
                      ExecutionId = execution }
            | found, _ -> Error(InputInboxError.InvalidRequest("verification plan refused: " + String.concat "; " found))

    /// The command Ordo runs over the admitted plan. Praxis records it; it
    /// does not run it and does not interpret the schedule it produces.
    let orderingCommand (handoff: VerificationPlanHandoff) =
        $"sde verification schedule --plan {handoff.AdmittedPath} --expected-digest {handoff.Plan.Digest} --json"
