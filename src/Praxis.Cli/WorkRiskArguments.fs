namespace Praxis.Cli

open Praxis.Domain.Work

/// Parses the `work update` risk options (PRX-QUAL-020) into a typed,
/// validated declaration. Pure: arguments in, `WorkRiskInput` out.
///
///   --change-class CLASS (repeatable) --risk-level low|medium|high|critical
///   [--state-impact] [--protocol-impact] [--security-impact]
///   [--failure-posture fail-open|fail-closed|indeterminate]
///   [--tier domain|contracts|application|infrastructure|cli] (repeatable)
///   [--live-proof TEXT]
[<RequireQualifiedAccess>]
module WorkRiskArguments =
    let private riskOptions =
        [ "--change-class"; "--risk-level"; "--state-impact"; "--protocol-impact"; "--security-impact"; "--failure-posture"; "--tier"; "--live-proof" ]

    let private values (name: string) (arguments: string list) =
        arguments
        |> List.pairwise
        |> List.choose (fun (option, value) -> if option = name then Some value else None)

    let private single name arguments = values name arguments |> List.tryLast

    let private parseAll (name: string) (parse: string -> 'T option) (allowed: string list) (arguments: string list) =
        values name arguments
        |> List.fold
            (fun acc code ->
                acc
                |> Result.bind (fun parsed ->
                    match parse code with
                    | Some value -> Ok(parsed @ [ value ])
                    | None ->
                        let choices = String.concat ", " allowed
                        Error $"{name} '{code}' is not one of {choices}"))
            (Ok [])

    let parse (arguments: string list) : WorkRiskInput =
        if not (arguments |> List.exists (fun argument -> List.contains argument riskOptions)) then
            WorkRiskInput.NotGiven
        else
            let classes =
                parseAll "--change-class" WorkRisk.tryParseChangeClass (WorkRisk.allChangeClasses |> List.map WorkRisk.changeClassCode) arguments

            let tiers = parseAll "--tier" WorkRisk.tryParseTier (WorkRisk.allTiers |> List.map WorkRisk.tierCode) arguments

            let level =
                match single "--risk-level" arguments with
                | None -> Error "--risk-level is required with risk metadata"
                | Some code ->
                    WorkRisk.tryParseLevel code
                    |> Option.map Ok
                    |> Option.defaultValue (Error $"--risk-level '{code}' is not one of low, medium, high, critical")

            let posture =
                match single "--failure-posture" arguments with
                | None -> Ok None
                | Some code ->
                    WorkRisk.tryParsePosture code
                    |> Option.map (Some >> Ok)
                    |> Option.defaultValue (Error $"--failure-posture '{code}' is not one of fail-open, fail-closed, indeterminate")

            match classes, tiers, level, posture with
            | Error reason, _, _, _
            | _, Error reason, _, _
            | _, _, Error reason, _
            | _, _, _, Error reason -> WorkRiskInput.Invalid reason
            | Ok classes, Ok tiers, Ok level, Ok posture ->
                match
                    WorkRisk.validate
                        { ChangeClasses = classes
                          Level = level
                          PersistentStateImpact = List.contains "--state-impact" arguments
                          ExternalProtocolImpact = List.contains "--protocol-impact" arguments
                          SecurityImpact = List.contains "--security-impact" arguments
                          FailurePosture = posture
                          Tiers = tiers
                          LiveProof = single "--live-proof" arguments }
                with
                | Ok risk -> WorkRiskInput.Given risk
                | Error reason -> WorkRiskInput.Invalid reason
