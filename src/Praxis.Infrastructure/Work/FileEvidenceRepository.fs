namespace Praxis.Infrastructure.Work

open System
open System.IO
open Praxis.Application.Work
open Praxis.Domain.Work

[<RequireQualifiedAccess>]
module FileEvidenceRepository =
    let private observe root (evidence: WorkEvidence) =
        try
            let candidate = Path.GetFullPath(Path.Combine(root, evidence.Path))
            File.GetAttributes candidate |> ignore
            EvidencePathObservation.Present
        with
        | :? FileNotFoundException
        | :? DirectoryNotFoundException -> EvidencePathObservation.Missing
        | error -> EvidencePathObservation.Unavailable error.Message

    let create root : WorkEvidenceRepository =
        { Observe = observe (Path.GetFullPath root) }
