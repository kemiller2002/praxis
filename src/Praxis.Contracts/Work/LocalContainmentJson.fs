namespace Praxis.Contracts.Work

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Praxis.Contracts.Execution
open Praxis.Domain.Execution
open Praxis.Domain.Work

/// A containment qualification as a `praxis.containment-evidence/1` record.
/// The existing fields (`schema`, `host`, `restrictions`) are exactly what
/// `ExecutionJson.readContainmentEvidence` already consumes, so the record can
/// be passed to `execution start --containment-evidence`. The additive fields
/// carry the profile, mechanism, platform, tool digests and every probe.
[<RequireQualifiedAccess>]
module LocalContainmentJson =
    [<Literal>]
    let Schema = "praxis.containment-evidence/1"

    let private maxBytes = 262144

    let private str (value: string) : JsonNode = JsonValue.Create value
    let private arr (items: JsonNode list) : JsonNode = JsonArray(items |> List.toArray)

    let private obj (fields: (string * JsonNode) list) : JsonNode =
        let node = JsonObject()
        for name, value in fields do node[name] <- value
        node

    let private stamp (at: DateTimeOffset) = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)

    let render (qualification: ContainmentQualification) =
        let restrictions = LocalContainment.restrictions qualification
        let node =
            obj
                [ "schema", str Schema
                  "host", str qualification.Host
                  "restrictions",
                  arr (
                      restrictions
                      |> List.map (fun r ->
                          obj
                              [ "dimension", str r.Dimension
                                "status", str (RestrictionStatus.toWire r.Status)
                                if r.Mechanism.IsSome then "mechanism", str r.Mechanism.Value
                                if r.Evidence.IsSome then "evidence", str r.Evidence.Value ])
                  )
                  "qualified", JsonValue.Create(LocalContainment.isQualified qualification)
                  "profile", str qualification.Profile
                  "profileDigest", str qualification.ProfileDigest
                  "mechanism", str qualification.Mechanism
                  "platform",
                  obj
                      [ "operatingSystem", str qualification.OperatingSystem
                        "kernel", str qualification.Kernel
                        "architecture", str qualification.Architecture ]
                  "tools", arr (qualification.Tools |> List.map (fun t -> obj [ "name", str t.Name; "path", str t.Path; "sha256", str t.Digest ]))
                  "probes",
                  arr (
                      qualification.Probes
                      |> List.map (fun p ->
                          obj
                              [ "id", str p.Id
                                "dimension", str p.Dimension
                                "expected", str p.Expected
                                "observed", str p.Observed
                                "result", str (ContainmentProbeOutcome.toWire p.Outcome)
                                "detail", str p.Detail ])
                  )
                  "unsupportedControls", arr (qualification.Unsupported |> List.map str)
                  "qualifiedAt", str (stamp qualification.QualifiedAt) ]
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

    let private fail message : 'a = raise (InvalidOperationException message)

    let private text (node: JsonNode) (name: string) =
        match node[name] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, content when not (isNull content) && content.Length <= 4096 -> content
            | _ -> fail ("invalid text field: " + name)
        | _ -> fail ("missing text field: " + name)

    let private items (node: JsonNode) (name: string) =
        match node[name] with
        | :? JsonArray as values when values.Count <= 512 -> values |> Seq.map (fun item -> if isNull item then fail ("null entry in " + name) else item) |> Seq.toList
        | _ -> fail ("invalid array field: " + name)

    /// Reads a record this module rendered. The stated restrictions must be
    /// exactly what the probes derive, and must also satisfy the existing
    /// `praxis.containment-evidence/1` reader; any disagreement refuses.
    let read (json: string) : Result<ContainmentQualification, string> =
        try
            if isNull json || UTF8Encoding(false, true).GetByteCount json > maxBytes then fail "containment evidence exceeds 256 KiB"
            let node = JsonNode.Parse(json, documentOptions = JsonDocumentOptions(MaxDepth = 8))
            if isNull node then fail "containment evidence is empty"
            let stated = ExecutionJson.readContainmentEvidence node |> Result.defaultWith (fun error -> fail error)
            let platform = node["platform"]
            if isNull platform then fail "missing platform"
            let qualifiedAt =
                match DateTimeOffset.TryParse(text node "qualifiedAt", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal) with
                | true, at -> at
                | _ -> fail "invalid qualifiedAt"
            let qualification =
                { Host = text node "host"
                  Profile = text node "profile"
                  ProfileDigest = text node "profileDigest"
                  Mechanism = text node "mechanism"
                  OperatingSystem = text platform "operatingSystem"
                  Kernel = text platform "kernel"
                  Architecture = text platform "architecture"
                  Tools = items node "tools" |> List.map (fun t -> { Name = text t "name"; Path = text t "path"; Digest = text t "sha256" })
                  Probes =
                    items node "probes"
                    |> List.map (fun p ->
                        { Id = text p "id"
                          Dimension = text p "dimension"
                          Expected = text p "expected"
                          Observed = text p "observed"
                          Outcome = text p "result" |> ContainmentProbeOutcome.tryParse |> Option.defaultWith (fun () -> fail "invalid probe result")
                          Detail = text p "detail" })
                  Unsupported = items node "unsupportedControls" |> List.map (fun item -> item.GetValue<string>())
                  QualifiedAt = qualifiedAt }
            match LocalContainment.profile qualification with
            | Error error -> Error("containment evidence refused: " + error)
            | Ok derived when derived <> stated -> Error "containment evidence refused: stated restrictions differ from the probes"
            | Ok _ ->
                match node["qualified"] with
                | :? JsonValue as flag when (match flag.TryGetValue<bool>() with | true, value -> value = LocalContainment.isQualified qualification | _ -> false) ->
                    Ok qualification
                | _ -> Error "containment evidence refused: qualified flag differs from the probes"
        with
        | :? InvalidOperationException as error -> Error("containment evidence refused: " + error.Message)
        | :? JsonException | :? FormatException | :? ArgumentException | :? DecoderFallbackException as error ->
            Error("containment evidence refused: " + error.GetType().Name)
