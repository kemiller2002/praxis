namespace Praxis.Application.Work

open System

/// Transient controller tool observation. It cannot be submitted to worker
/// journals, which require a separately observed process incarnation.
type LocalControllerToolObservation =
    { ProcessStarted: bool option
      RootExitObserved: bool
      Outcome: LocalWorkerProcessOutcome
      StandardOutput: string
      StandardError: string
      CapturedBytes: int }

/// Provisioned by the controller, not decoded from worker output. Dependencies
/// include managed assemblies, scripts, configuration and tool closure files.
type LocalValidatorRequest =
    { ValidatorId: string
      CandidateCommit: string
      GitExecutable: string
      GitExecutableDigest: string
      Dependencies: Map<string, string>
      Process: LocalWorkerProcessSpec
      TrxPath: string option
      EvidenceRoot: string }

/// Fresh input observation for checklist generation; never a passing result.
type LocalValidatorInputs =
    { ValidatorId: string
      CandidateCommit: string
      CandidateManifestDigest: string
      ValidatorInputDigest: string }

/// A process observation, not authentication, acceptance or write authority.
type LocalValidatorReport =
    { ValidatorId: string
      CandidateCommit: string
      CandidateManifestDigest: string
      ValidatorInputDigest: string
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset
      TestsExecuted: int option
      Verdict: Result<unit, string>
      EvidencePath: string
      EvidenceDigest: string }
