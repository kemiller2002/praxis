namespace Praxis.Infrastructure.Ordo

open System
open System.IO
open Praxis.Domain.Ordo

/// File edge of Ordo contract admission (PRX-REL-008): reads the published
/// contract bytes and hands them to the pure domain admission. No state is
/// written; a refusal here happens before any agent work is claimed.
[<RequireQualifiedAccess>]
module OrdoContractSource =
    let private refusal code path message =
        Error [ { Code = code; Subject = Some path; Message = message } ]

    let read (root: string) (path: string) : Result<string, OrdoDiagnostic list> =
        let fullPath = Path.GetFullPath(Path.Combine(root, path))
        try
            if File.Exists fullPath then Ok(File.ReadAllText fullPath)
            else refusal "ORDO.CONTRACT.MISSING_FILE" path "contract file missing"
        with
        | :? IOException as ex -> refusal "ORDO.CONTRACT.UNREADABLE_FILE" path ex.Message
        | :? UnauthorizedAccessException as ex -> refusal "ORDO.CONTRACT.UNREADABLE_FILE" path ex.Message

    let check root path supportedVersions expectedDigest =
        read root path |> Result.bind (OrdoContractAdmission.admitPinned supportedVersions expectedDigest)

    /// Readiness as `work complete-verified` will require it (REL-GATE-002):
    /// admission, then the executed subset of `requirementIds` must be ready
    /// under `authorizedDecisionIds`, which the caller takes from its host
    /// authority. Reported before any agent work is claimed.
    let checkReadiness root path supportedVersions expectedDigest requirementIds authorizedDecisionIds =
        read root path
        |> Result.bind (fun json ->
            OrdoContractAdmission.assessCompletionReadiness supportedVersions expectedDigest json requirementIds authorizedDecisionIds)
