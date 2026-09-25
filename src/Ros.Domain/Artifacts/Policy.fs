namespace Ros.Domain.Artifacts

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module ArtifactPolicy =
    let private identifierPattern =
        Regex(
            "^(?:(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS|RQ)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-(?:[0-9]{4}|[A-F0-9]{4})|RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-[A-Z0-9]+(?:-[A-Z0-9]+)*|(?:RP|REP)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-[0-9]{3})$",
            RegexOptions.CultureInvariant
        )

    let private legacyResearchPackagePattern =
        Regex(
            "^(?:RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-[A-Z0-9]+(?:-[A-Z0-9]+)*|(?:RP|REP)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-[0-9]{3})$",
            RegexOptions.CultureInvariant
        )

    let private referenceFields =
        [ "contradicts"
          "contradicting_evidence"
          "depends_on"
          "derived_from"
          "evidence_ids"
          "hypothesis_ids"
          "related_documents"
          "related_mission"
          "related_package"
          "related_theories"
          "supporting_evidence"
          "supports"
          "superseded_by"
          "supersedes"
          "tests_hypotheses"
          "theory_ids" ]

    let private allowedStatuses =
        Map.ofList
            [ "DF", Set.ofList [ "draft"; "review"; "accepted"; "superseded"; "withdrawn" ]
              "EV", Set.ofList [ "draft"; "review"; "accepted"; "superseded"; "withdrawn" ]
              "EX", Set.ofList [ "proposed"; "active"; "blocked"; "completed"; "cancelled" ]
              "HY", Set.ofList [ "proposed"; "active"; "supported"; "rejected"; "superseded"; "withdrawn" ]
              "MS", Set.ofList [ "proposed"; "approved"; "active"; "blocked"; "completed"; "cancelled"; "archived" ]
              "RQ", Set.ofList [ "draft"; "proposed"; "accepted"; "implemented"; "deprecated"; "superseded"; "withdrawn" ]
              "RP", Set.ofList [ "draft"; "review"; "accepted"; "canonical"; "deprecated"; "archived"; "superseded"; "withdrawn" ]
              "TH", Set.ofList [ "candidate"; "supported"; "established"; "challenged"; "superseded"; "rejected" ] ]

    let private confidenceLabels =
        Set.ofList [ "very-low"; "low"; "medium"; "medium-high"; "high"; "very-high" ]

    let private finding path field message =
        { Path = path
          Field = field
          Message = message }

    let private artifactReferences field document =
        match ArtifactDocument.tryMetadata field document with
        | Some(ArtifactValue.Text value) when identifierPattern.IsMatch value -> [ value ]
        | Some(ArtifactValue.Sequence values) ->
            values
            |> List.choose (function
                | ArtifactValue.Text value when identifierPattern.IsMatch value -> Some value
                | _ -> None)
        | _ -> []

    let private compareFindings left right =
        let comparePath = StringComparer.Ordinal.Compare(left.Path, right.Path)

        if comparePath <> 0 then
            comparePath
        else
            let compareField = StringComparer.Ordinal.Compare(left.Field, right.Field)
            if compareField <> 0 then compareField else StringComparer.Ordinal.Compare(left.Message, right.Message)

    let isValidIdentifier (identifier: string) = identifierPattern.IsMatch identifier

    let validate (parseFindings: ArtifactFinding list) (documents: ArtifactDocument list) =
        let findings = ResizeArray<ArtifactFinding>(parseFindings)
        let documentsByIdentifier = Collections.Generic.Dictionary<string, ResizeArray<ArtifactDocument>>(StringComparer.Ordinal)

        for document in documents do
            let identifier = ArtifactDocument.identifier document

            if identifier.Length = 0 then
                findings.Add(finding document.RelativePath "id" "required field is missing")
            else
                if not (identifierPattern.IsMatch identifier) then
                    findings.Add(finding document.RelativePath "id" $"invalid identifier '{identifier}'")

                match documentsByIdentifier.TryGetValue identifier with
                | true, existing -> existing.Add document
                | false, _ ->
                    let existing = ResizeArray<ArtifactDocument>()
                    existing.Add document
                    documentsByIdentifier.Add(identifier, existing)

                let hasTitle =
                    ArtifactDocument.tryMetadata "title" document
                    |> Option.exists ArtifactValue.isJavaScriptTruthy

                if not hasTitle then
                    findings.Add(finding document.RelativePath "title" "required field is missing")

                let legacyName =
                    legacyResearchPackagePattern.IsMatch identifier
                    && document.FileName = $"{identifier}.md"

                if not (document.FileName.StartsWith($"{identifier}--", StringComparison.Ordinal) || legacyName) then
                    findings.Add(
                        finding document.RelativePath "id" $"filename must start with '{identifier}--'"
                    )

                let prefix = ArtifactKinds.identifierPrefix identifier

                match ArtifactDocument.tryMetadata "status" document, allowedStatuses |> Map.tryFind prefix with
                | Some status, Some allowed when ArtifactValue.isJavaScriptTruthy status ->
                    let displayed = ArtifactValue.display status
                    if not (allowed.Contains displayed) then
                        findings.Add(finding document.RelativePath "status" $"'{displayed}' is not allowed for {prefix}")
                | _ -> ()

                match ArtifactDocument.tryMetadata "confidence" document with
                | Some(ArtifactValue.Text confidence) when not (confidenceLabels.Contains confidence) ->
                    findings.Add(finding document.RelativePath "confidence" $"unknown label '{confidence}'")
                | _ -> ()

        for KeyValue(identifier, records) in documentsByIdentifier do
            if records.Count > 1 then
                let paths = records |> Seq.map _.RelativePath |> String.concat ", "

                for document in records do
                    findings.Add(
                        finding document.RelativePath "id" $"duplicate '{identifier}' also in {paths}"
                    )

        let knownIdentifiers = documentsByIdentifier.Keys |> Set.ofSeq

        for document in documents do
            let identifier = ArtifactDocument.identifier document

            for field in referenceFields do
                for target in artifactReferences field document do
                    if not (knownIdentifiers.Contains target) then
                        findings.Add(finding document.RelativePath field $"broken reference '{target}'")

                    if target = identifier && (field = "supersedes" || field = "superseded_by") then
                        findings.Add(finding document.RelativePath field "artifact cannot supersede itself")

            for target in artifactReferences "supersedes" document do
                match documentsByIdentifier.TryGetValue target with
                | true, targetRecords when targetRecords.Count > 0 ->
                    let reciprocal = targetRecords[0]
                    if not (artifactReferences "superseded_by" reciprocal |> List.contains identifier) then
                        findings.Add(finding document.RelativePath "supersedes" $"'{target}' is not reciprocal")
                | _ -> ()

            for target in artifactReferences "superseded_by" document do
                match documentsByIdentifier.TryGetValue target with
                | true, targetRecords when targetRecords.Count > 0 ->
                    let reciprocal = targetRecords[0]
                    if not (artifactReferences "supersedes" reciprocal |> List.contains identifier) then
                        findings.Add(finding document.RelativePath "superseded_by" $"'{target}' is not reciprocal")
                | _ -> ()

        findings |> Seq.toList |> List.sortWith compareFindings
