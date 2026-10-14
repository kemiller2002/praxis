namespace Praxis.Domain.Identity

// Whether the repository identity a repository is configured with
// (`ros.json` `repository.identity`) is the repository Praxis is running in
// (PRX-REMOTE-046, 048). The configuration travels with the files, so a
// template or fork copies it: only an observation of the hosting
// environment can tell a copy from the original. Pure.

/// What the hosting environment shows about the repository. In CI the
/// provider's stable ID is observable (`GITHUB_REPOSITORY_ID`); locally
/// usually only a locator from the Git remote is.
type RepositoryObservation =
    { Provider: RepositoryProvider option
      ProviderId: string option
      Locator: RepositoryLocator option
      /// Where the observation came from (`github-actions`, `git-remote`, `none`).
      Source: string }

[<RequireQualifiedAccess>]
module RepositoryObservation =
    let none =
        { Provider = None
          ProviderId = None
          Locator = None
          Source = "none" }

    /// The observation as an identity, when it carries enough to be one.
    let identity (observation: RepositoryObservation) =
        observation.Provider
        |> Option.bind (fun provider -> RepositoryIdentity.create provider observation.ProviderId observation.Locator |> Result.toOption)

[<RequireQualifiedAccess>]
type RepositoryIdentityStatus =
    /// No `repository.identity` is configured. The legacy identity derived
    /// from the observation, if any, is explicitly legacy (PRX-REMOTE-048).
    | NotEstablished of legacy: RepositoryIdentity option
    /// The configured stable ID is the observed one. The identity carries
    /// the observed locator, so a rename or transfer is followed.
    | Verified of RepositoryIdentity
    /// Nothing observed contradicts the configured identity, but its stable
    /// ID was not observed (normal outside CI).
    | Unverified of RepositoryIdentity
    /// The configured identity has no stable ID (legacy configuration).
    | Legacy of RepositoryIdentity
    /// The observed locator differs from the configured one and no stable ID
    /// was observed: a rename, a transfer, or a copy into another repository.
    | LocatorChanged of configured: RepositoryIdentity * observed: RepositoryLocator
    /// The observed stable ID differs from the configured one: the
    /// configuration was copied from another repository (template or fork).
    | Contradicted of configured: RepositoryIdentity * observed: RepositoryIdentity

[<RequireQualifiedAccess>]
module RepositoryIdentityStatus =
    let assess (configured: RepositoryIdentity option) (observation: RepositoryObservation) =
        match configured with
        | None ->
            RepositoryIdentityStatus.NotEstablished(
                match observation.Provider, observation.Locator with
                | Some provider, Some locator -> Some(RepositoryIdentity.legacy provider locator)
                | _ -> None
            )
        | Some configured ->
            let observedIdentity = RepositoryObservation.identity observation

            match observedIdentity with
            | Some observed when observed.ProviderId.IsSome && configured.ProviderId.IsSome ->
                match RepositoryIdentity.compare configured observed with
                | RepositoryMatch.Same ->
                    RepositoryIdentityStatus.Verified(
                        match observed.Locator with
                        | Some locator -> RepositoryIdentity.withLocator locator configured
                        | None -> configured
                    )
                | _ -> RepositoryIdentityStatus.Contradicted(configured, observed)
            | Some observed when observed.Provider <> configured.Provider && observed.ProviderId.IsSome ->
                RepositoryIdentityStatus.Contradicted(configured, observed)
            | _ when configured.ProviderId.IsNone -> RepositoryIdentityStatus.Legacy configured
            | _ ->
                match configured.Locator, observation.Locator with
                | Some expected, Some actual when expected <> actual -> RepositoryIdentityStatus.LocatorChanged(configured, actual)
                | _ -> RepositoryIdentityStatus.Unverified configured

    /// The repository identity canonical work-item identities are derived
    /// from, or `None` when there is none to rely on. A contradicted
    /// configuration yields none: it fails closed.
    let current status =
        match status with
        | RepositoryIdentityStatus.Verified identity
        | RepositoryIdentityStatus.Unverified identity
        | RepositoryIdentityStatus.Legacy identity
        | RepositoryIdentityStatus.LocatorChanged(identity, _) -> Some identity
        | RepositoryIdentityStatus.NotEstablished legacy -> legacy
        | RepositoryIdentityStatus.Contradicted _ -> None

    let code status =
        match status with
        | RepositoryIdentityStatus.NotEstablished _ -> "not-established"
        | RepositoryIdentityStatus.Verified _ -> "verified"
        | RepositoryIdentityStatus.Unverified _ -> "unverified"
        | RepositoryIdentityStatus.Legacy _ -> "legacy"
        | RepositoryIdentityStatus.LocatorChanged _ -> "locator-changed"
        | RepositoryIdentityStatus.Contradicted _ -> "contradicted"

    /// Findings for `validate`: `(isError, message)`.
    let findings status : (bool * string) list =
        match status with
        | RepositoryIdentityStatus.NotEstablished _ ->
            [ false,
              "repository identity is not established, so canonical work-item identities are incomplete (PRX-REMOTE-049); record it with 'praxis repository identity set'" ]
        | RepositoryIdentityStatus.Legacy _ ->
            [ false, "repository identity has no stable provider ID (legacy); add one with 'praxis repository identity set --provider-id ID' (PRX-REMOTE-048)" ]
        | RepositoryIdentityStatus.LocatorChanged(configured, observed) ->
            [ false,
              $"the Git remote names {RepositoryLocator.value observed}, but repository.identity names {RepositoryIdentity.display configured}; after a rename or transfer update the locator, after a copy set a new identity" ]
        | RepositoryIdentityStatus.Contradicted(configured, observed) ->
            let configuredId = RepositoryIdentity.repositoryId configured |> Option.defaultValue (RepositoryIdentity.display configured)
            let observedId = RepositoryIdentity.repositoryId observed |> Option.defaultValue (RepositoryIdentity.display observed)

            [ true,
              $"repository.identity is {configuredId} but this repository is {observedId}: the configuration was copied from another repository; set this repository's identity with 'praxis repository identity set'" ]
        | RepositoryIdentityStatus.Verified _
        | RepositoryIdentityStatus.Unverified _ -> []
