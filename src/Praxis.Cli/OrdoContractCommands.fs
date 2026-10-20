namespace Praxis.Cli

open System.IO
open System.Text
open System.Text.Json
open Praxis.Domain.Ordo
open Praxis.Infrastructure.Ordo

/// `praxis ordo contract check`: admit an Ordo constructibility contract
/// before any agent execution (PRX-REL-008, interface I3). With
/// `--requirement`, also report the readiness `work complete-verified` will
/// require for those requirements under the `--authorized-decision` set the
/// host authority grants (REL-GATE-002). Exit 0 admitted (ready), 1 refused
/// (stable ORDO.CONTRACT.* codes), 2 usage. Read-only; neither admission nor
/// readiness is execution authority.
[<RequireQualifiedAccess>]
module OrdoContractCommands =
    let usage =
        "ordo contract check --contract FILE [--expected-digest sha256:HEX] [--supported-version VERSION]* [--requirement KEY]* [--authorized-decision ID]* [--json]"

    let rec private option (name: string) (arguments: string list) =
        match arguments with
        | flag :: value :: _ when flag = name -> Some value
        | _ :: rest -> option name rest
        | [] -> None

    let private options (name: string) (arguments: string list) =
        arguments |> List.pairwise |> List.choose (fun (flag, value) -> if flag = name then Some value else None)

    let private render (write: Utf8JsonWriter -> unit) =
        use buffer = new MemoryStream()
        do
            use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
            writer.WriteStartObject()
            writer.WriteString("schemaVersion", "praxis.ordo-contract-admission/1")
            write writer
            writer.WriteBoolean("executionAuthorized", false)
            writer.WriteEndObject()
        Encoding.UTF8.GetString(buffer.ToArray())

    let private strings (writer: Utf8JsonWriter) (name: string) (values: string list) =
        writer.WriteStartArray name
        values |> List.iter writer.WriteStringValue
        writer.WriteEndArray()

    let renderAdmitted (admitted: AdmittedOrdoContract) =
        render (fun writer ->
            writer.WriteString("status", "admitted")
            writer.WriteString("version", admitted.Version)
            writer.WriteString("digest", admitted.Digest)
            strings writer "requirementIds" admitted.RequirementIds
            strings writer "decisionIds" admitted.DecisionIds
            strings writer "conflictResolutionIds" admitted.ConflictResolutionIds)

    let renderReady (ready: OrdoContractAdmission.ReadyScope) =
        let view = OrdoContractAdmission.readyScopeView ready
        render (fun writer ->
            writer.WriteString("status", "ready")
            writer.WriteString("version", view.Contract.Version)
            writer.WriteString("digest", view.Contract.Digest)
            strings writer "requirementIds" view.RequirementKeys
            strings writer "requiredRequirementIds" view.RequiredRequirementKeys
            strings writer "requiredNodeIds" view.RequiredNodeIds
            strings writer "requiredDecisionIds" view.RequiredDecisionIds
            strings writer "requiredRationaleIds" view.RequiredRationaleIds
            strings writer "authorizedDecisionIds" view.AuthorizedDecisionIds)

    let renderRefused (diagnostics: OrdoDiagnostic list) =
        render (fun writer ->
            writer.WriteString("status", "refused")
            writer.WriteStartArray "diagnostics"
            for diagnostic in diagnostics do
                writer.WriteStartObject()
                writer.WriteString("code", diagnostic.Code)
                diagnostic.Subject |> Option.iter (fun subject -> writer.WriteString("subject", subject))
                writer.WriteString("message", diagnostic.Message)
                writer.WriteEndObject()
            writer.WriteEndArray())

    let private refuse json diagnostics =
        if json then printfn "%s" (renderRefused diagnostics)
        else
            for (diagnostic: OrdoDiagnostic) in diagnostics do
                eprintfn "ERROR %s%s %s" diagnostic.Code (diagnostic.Subject |> Option.map (sprintf " [%s]") |> Option.defaultValue "") diagnostic.Message
        1

    let run (root: string) (arguments: string list) =
        match arguments, option "--contract" arguments with
        | "check" :: rest, Some contract ->
            let json = List.contains "--json" rest
            let supported =
                match options "--supported-version" rest with
                | [] -> OrdoContractAdmission.implementedVersions
                | narrowed -> narrowed
            let expected = option "--expected-digest" rest
            match options "--requirement" rest, options "--authorized-decision" rest with
            | [], [] ->
                match OrdoContractSource.check root contract supported expected with
                | Ok admitted when json -> printfn "%s" (renderAdmitted admitted); 0
                | Ok admitted ->
                    printfn "Ordo contract admitted: %s %s; %d requirements, %d decisions, %d conflict resolutions. Execution authorization not granted."
                        admitted.Version admitted.Digest admitted.RequirementIds.Length admitted.DecisionIds.Length admitted.ConflictResolutionIds.Length
                    0
                | Error diagnostics -> refuse json diagnostics
            | [], _ ->
                eprintfn "Usage: praxis %s (--authorized-decision needs --requirement)" usage
                2
            | requirements, authorized ->
                match OrdoContractSource.checkReadiness root contract supported expected requirements authorized with
                | Ok ready when json -> printfn "%s" (renderReady ready); 0
                | Ok ready ->
                    let view = OrdoContractAdmission.readyScopeView ready
                    printfn "Ordo contract ready: %s %s; %d requirements executed, %d decisions authorized. Execution authorization not granted."
                        view.Contract.Version view.Contract.Digest view.RequirementKeys.Length view.AuthorizedDecisionIds.Length
                    0
                | Error diagnostics -> refuse json diagnostics
        | _ ->
            eprintfn "Usage: praxis %s" usage
            2
