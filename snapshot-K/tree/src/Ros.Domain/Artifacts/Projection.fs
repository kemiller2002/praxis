namespace Ros.Domain.Artifacts

open System

[<RequireQualifiedAccess>]
module ArtifactProjection =
    let private compareDocuments left right =
        StringComparer.Ordinal.Compare(ArtifactDocument.identifier left, ArtifactDocument.identifier right)

    let plan documents =
        ArtifactKinds.configurations
        |> List.map (fun configuration ->
            let matching =
                documents
                |> List.filter (fun document ->
                    ArtifactKinds.identifierPrefix (ArtifactDocument.identifier document) = configuration.IdentifierPrefix)
                |> List.sortWith compareDocuments

            { RegistryPath = configuration.RegistryPath
              Documents = matching })
