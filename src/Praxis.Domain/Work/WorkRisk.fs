namespace Praxis.Domain.Work

/// Engineering-risk metadata a work item declares (PRX-QUAL-020,
/// `praxis.work-risk/1`), and the completion obligations Praxis derives from
/// it (PRX-QUAL-021, PRX-QUAL-022). Pure: declaring, validating and deriving
/// obligations never touch files.
[<RequireQualifiedAccess>]
type ChangeClass =
    | StatefulControlPlane
    | Persistence
    | ReleaseBootstrap
    | Security
    | RemoteExecution
    | ExternalProtocol
    | Feature
    | Refactor
    | Documentation
    | Tooling

[<RequireQualifiedAccess>]
type RiskLevel =
    | Low
    | Medium
    | High
    | Critical

[<RequireQualifiedAccess>]
type FailurePosture =
    | FailOpen
    | FailClosed
    | Indeterminate

/// The architecture tiers a change is expected to own.
[<RequireQualifiedAccess>]
type Tier =
    | Domain
    | Contracts
    | Application
    | Infrastructure
    | Cli

type WorkRisk =
    { ChangeClasses: ChangeClass list
      Level: RiskLevel
      PersistentStateImpact: bool
      ExternalProtocolImpact: bool
      SecurityImpact: bool
      FailurePosture: FailurePosture option
      Tiers: Tier list
      /// The live or integration proof the work must show, when it needs one.
      LiveProof: string option }

/// The dimensions of a verification matrix (PRX-QUAL-022).
[<RequireQualifiedAccess>]
type VerificationDimension =
    | HappyPath
    | Negative
    | Corruption
    | Concurrency
    | Compatibility
    | Recovery
    | LiveEffect

/// What completing a risk-declaring item must show.
type CompletionObligations =
    { /// A design-debt declaration (none, or tracked debt items) is required.
      DesignDebtDeclaration: bool
      /// Dimensions a verification matrix must show as met; empty when no
      /// matrix is required.
      VerificationDimensions: Set<VerificationDimension> }

[<RequireQualifiedAccess>]
module WorkRisk =
    [<Literal>]
    let Schema = "praxis.work-risk/1"

    let private table (cases: ('T * string) list) =
        (fun value -> cases |> List.find (fun (candidate, _) -> candidate = value) |> snd),
        (fun (code: string) -> cases |> List.tryFind (fun (_, candidate) -> candidate = code) |> Option.map fst),
        (cases |> List.map fst)

    let changeClassCode, tryParseChangeClass, allChangeClasses =
        table
            [ ChangeClass.StatefulControlPlane, "stateful-control-plane"
              ChangeClass.Persistence, "persistence"
              ChangeClass.ReleaseBootstrap, "release-bootstrap"
              ChangeClass.Security, "security"
              ChangeClass.RemoteExecution, "remote-execution"
              ChangeClass.ExternalProtocol, "external-protocol"
              ChangeClass.Feature, "feature"
              ChangeClass.Refactor, "refactor"
              ChangeClass.Documentation, "documentation"
              ChangeClass.Tooling, "tooling" ]

    let levelCode, tryParseLevel, allLevels =
        table [ RiskLevel.Low, "low"; RiskLevel.Medium, "medium"; RiskLevel.High, "high"; RiskLevel.Critical, "critical" ]

    let postureCode, tryParsePosture, allPostures =
        table [ FailurePosture.FailOpen, "fail-open"; FailurePosture.FailClosed, "fail-closed"; FailurePosture.Indeterminate, "indeterminate" ]

    let tierCode, tryParseTier, allTiers =
        table
            [ Tier.Domain, "domain"
              Tier.Contracts, "contracts"
              Tier.Application, "application"
              Tier.Infrastructure, "infrastructure"
              Tier.Cli, "cli" ]

    let dimensionCode, tryParseDimension, allDimensions =
        table
            [ VerificationDimension.HappyPath, "happy-path"
              VerificationDimension.Negative, "negative"
              VerificationDimension.Corruption, "corruption"
              VerificationDimension.Concurrency, "concurrency"
              VerificationDimension.Compatibility, "compatibility"
              VerificationDimension.Recovery, "recovery"
              VerificationDimension.LiveEffect, "live-effect" ]

    /// Classes PRX-QUAL-022 names as needing a verification matrix.
    let matrixClasses =
        set
            [ ChangeClass.StatefulControlPlane
              ChangeClass.Persistence
              ChangeClass.ReleaseBootstrap
              ChangeClass.Security
              ChangeClass.RemoteExecution ]

    let isHighRisk (risk: WorkRisk) = risk.Level = RiskLevel.High || risk.Level = RiskLevel.Critical

    /// A declaration is coherent: at least one change class; a failure
    /// posture whenever state, protocol or security is affected; a non-empty
    /// live proof when one is named.
    let validate (risk: WorkRisk) : Result<WorkRisk, string> =
        let impacted = risk.PersistentStateImpact || risk.ExternalProtocolImpact || risk.SecurityImpact

        if risk.ChangeClasses.IsEmpty then
            Error "risk metadata needs at least one change class (--change-class)"
        elif impacted && risk.FailurePosture.IsNone then
            Error "risk metadata with state, protocol or security impact must declare a failure posture (--failure-posture fail-open|fail-closed|indeterminate)"
        elif risk.LiveProof |> Option.exists (fun proof -> proof.Trim() = "") then
            Error "a declared live proof cannot be empty"
        else
            Ok { risk with ChangeClasses = List.distinct risk.ChangeClasses; Tiers = List.distinct risk.Tiers }

    /// Derives completion obligations from risk metadata (PRX-QUAL-020):
    /// - high or critical work must declare its design debt (PRX-QUAL-021);
    /// - stateful control-plane, persistence, release/bootstrap, security or
    ///   remote-execution work, or work declaring state, protocol or security
    ///   impact, needs a verification matrix (PRX-QUAL-022): happy path,
    ///   negative, corruption/partial state, compatibility and recovery always;
    ///   concurrency when state is shared (persistence, control plane, remote
    ///   execution, persistent-state impact); a representative live effect
    ///   when a live proof is declared or the change crosses a release,
    ///   remote or external-protocol boundary.
    /// Work without risk metadata has no obligations (existing behaviour).
    let obligations (risk: WorkRisk option) : CompletionObligations =
        match risk with
        | None ->
            { DesignDebtDeclaration = false
              VerificationDimensions = Set.empty }
        | Some risk ->
            let classes = Set.ofList risk.ChangeClasses
            let has change = classes.Contains change

            let needsMatrix =
                not (Set.intersect classes matrixClasses).IsEmpty
                || risk.PersistentStateImpact
                || risk.ExternalProtocolImpact
                || risk.SecurityImpact

            let dimensions =
                if not needsMatrix then
                    Set.empty
                else
                    set
                        [ VerificationDimension.HappyPath
                          VerificationDimension.Negative
                          VerificationDimension.Corruption
                          VerificationDimension.Compatibility
                          VerificationDimension.Recovery
                          if has ChangeClass.Persistence
                             || has ChangeClass.StatefulControlPlane
                             || has ChangeClass.RemoteExecution
                             || risk.PersistentStateImpact then
                              VerificationDimension.Concurrency
                          if risk.LiveProof.IsSome
                             || has ChangeClass.ReleaseBootstrap
                             || has ChangeClass.RemoteExecution
                             || has ChangeClass.ExternalProtocol then
                              VerificationDimension.LiveEffect ]

            { DesignDebtDeclaration = isHighRisk risk
              VerificationDimensions = dimensions }

    let hasObligations (obligations: CompletionObligations) =
        obligations.DesignDebtDeclaration || not obligations.VerificationDimensions.IsEmpty
