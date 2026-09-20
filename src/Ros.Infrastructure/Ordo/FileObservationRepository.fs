namespace Ros.Infrastructure.Ordo

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Ros.Application.Ordo
open Ros.Contracts.Ordo
open Ros.Domain.Ordo

[<RequireQualifiedAccess>]
module FileObservationRepository =
    let private hashId (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    let private directory root kind = Path.Combine(root, ".ros", "ordo", kind)

    let private pathFor root kind id =
        Path.Combine(directory root kind, $"{hashId id}.json")

    let private writeAtomic (path: string) (content: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let temporary = path + ".tmp"
        File.WriteAllText(temporary, content, UTF8Encoding(false))
        File.Move(temporary, path, true)

    let private readParsed path parser =
        if not (File.Exists path) then None
        else
            let raw = File.ReadAllText path
            match parser raw with
            | Ok value -> Some value
            | Error error -> failwith $"Corrupt ROS observation record '{path}': {error}"

    let private listParsed root kind parser =
        let folder = directory root kind
        if not (Directory.Exists folder) then []
        else
            Directory.GetFiles(folder, "*.json")
            |> Array.sort
            |> Array.map (fun path ->
                match parser (File.ReadAllText path) with
                | Ok value -> value
                | Error error -> failwith $"Corrupt ROS observation record '{path}': {error}")
            |> Array.toList

    let create (root: string) : ObservationRepository =
        { TryResolution =
            fun id ->
                pathFor root "resolutions" id
                |> fun path -> readParsed path ObservationJson.parseResolutionObservation
          SaveResolution =
            fun observation rawJson ->
                writeAtomic
                    (pathFor root "raw/resolutions" observation.ResolutionId)
                    rawJson
                writeAtomic
                    (pathFor root "resolutions" observation.ResolutionId)
                    (ObservationJson.renderResolutionObservation observation)
          ListResolutions =
            fun () -> listParsed root "resolutions" ObservationJson.parseResolutionObservation
          TryAssessment =
            fun id ->
                pathFor root "assessments" id
                |> fun path -> readParsed path ObservationJson.parseAssessment
          SaveAssessment =
            fun assessment ->
                writeAtomic
                    (pathFor root "assessments" assessment.AssessmentId)
                    (ObservationJson.renderAssessment assessment)
          ListAssessments =
            fun () -> listParsed root "assessments" ObservationJson.parseAssessment
          TrySearchObservation =
            fun id ->
                pathFor root "search-observations" id
                |> fun path -> readParsed path ObservationJson.parseSearchObservation
          SaveSearchObservation =
            fun observation ->
                writeAtomic
                    (pathFor root "search-observations" observation.ObservationId)
                    (ObservationJson.renderSearchObservation observation)
          TryEffectObservation =
            fun id ->
                pathFor root "effect-observations" id
                |> fun path -> readParsed path ObservationJson.parseEffectObservation
          SaveEffectObservation =
            fun observation ->
                writeAtomic
                    (pathFor root "effect-observations" observation.ObservationId)
                    (ObservationJson.renderEffectObservation observation) }
