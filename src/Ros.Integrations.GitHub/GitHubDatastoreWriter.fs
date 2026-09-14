namespace Ros.Integrations.GitHub

open System.IO
open System.Text.Json

/// Writes a `TimeObservationCandidate` into a target repository's
/// working tree, keyed by `CandidateId` so a repeated delivery of the
/// same candidate overwrites the same file rather than creating a
/// second one -- the file-level half of the idempotency property
/// docs/migrations/central-integration/MIGRATION-PLAN.md Phase 9
/// requires ("repeat ROS delivery -> still one candidate").
///
/// This writes to a plain working tree, not a live GitHub commit: the
/// real adapter commits and pushes this same file via the GitHub API
/// once a target application's actual repository and a GitHub App
/// installation exist (see MIGRATION-PLAN.md Phase 8 -- "the ROS GitHub
/// App... infrastructure, not domain -- add GitHub App hosting only
/// after these boundaries are stable"). `LocalGitSimulation` proves the
/// commit half of the mechanism against a real, disposable local git
/// repository, standing in for that eventual GitHub commit until real
/// GitHub App credentials for a target application exist.
[<RequireQualifiedAccess>]
module GitHubDatastoreWriter =
    let private candidatePath (repoRoot: string) (candidateId: string) =
        Path.Combine(repoRoot, "chrona-data", "candidates", $"{candidateId}.json")

    let write (repoRoot: string) (candidate: TimeObservationCandidate) : string =
        let path = candidatePath repoRoot candidate.CandidateId
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let options = JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(path, JsonSerializer.Serialize(candidate, options) + "\n")
        path

    let tryRead (repoRoot: string) (candidateId: string) : TimeObservationCandidate option =
        let path = candidatePath repoRoot candidateId

        if not (File.Exists path) then
            None
        else
            try
                Some(JsonSerializer.Deserialize<TimeObservationCandidate> (File.ReadAllText path))
            with _ ->
                None

    let listCandidates (repoRoot: string) : string list =
        let directory = Path.Combine(repoRoot, "chrona-data", "candidates")

        if not (Directory.Exists directory) then
            []
        else
            Directory.GetFiles(directory, "*.json") |> Array.map Path.GetFileNameWithoutExtension |> Array.toList
