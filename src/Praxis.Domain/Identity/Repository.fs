namespace Praxis.Domain.Identity

open System
open System.Text.RegularExpressions

// Repository and work-item identity (GitHub issue #90 PRX-REMOTE-045..050,
// adopted by RQ-ROS-2026-A021; PRAXIS-ID-01). A repository is identified by
// its hosting provider and the provider's stable repository ID; the
// `owner/repo` locator is a human-readable, changeable display value. A
// work item is identified by its repository plus its repository-local ID,
// and `owner/repo:LOCAL-ID` is only ever *rendered* from that pair: nothing
// here parses a display string to establish identity. Pure: no I/O.

/// A hosting provider (`github`, `gitlab`, ...). Praxis core knows no
/// provider's semantics; the name only scopes the provider ID.
[<Struct>]
type RepositoryProvider = private RepositoryProvider of string

[<RequireQualifiedAccess>]
module RepositoryProvider =
    let private pattern = Regex("^[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)

    let tryCreate (value: string) =
        if not (isNull value) && pattern.IsMatch value then Some(RepositoryProvider value) else None

    let value (RepositoryProvider provider) = provider

    let github = RepositoryProvider "github"

/// `owner/repo`: where a repository can be found today. It can change on a
/// rename or transfer, and different providers can reuse it.
[<Struct>]
type RepositoryLocator = private RepositoryLocator of string

[<RequireQualifiedAccess>]
module RepositoryLocator =
    let private segment = Regex("^[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)

    let isValid (value: string) =
        match (if isNull value then [||] else value.Split '/') with
        | [| owner; name |] ->
            segment.IsMatch owner
            && segment.IsMatch name
            && owner <> "."
            && owner <> ".."
            && name <> "."
            && name <> ".."
            && not (name.EndsWith(".git", StringComparison.Ordinal))
        | _ -> false

    let tryCreate (value: string) =
        if isValid value then Some(RepositoryLocator value) else None

    let value (RepositoryLocator locator) = locator

    /// `owner/repo` from a Git remote URL (`https://host/owner/repo(.git)`,
    /// `ssh://...`, `git@host:owner/repo(.git)`, or a proxied
    /// `http://host/git/owner/repo`). Only the remote's repository path is
    /// used; no local path is ever part of it.
    let ofRemoteUrl (remoteUrl: string) =
        let trimmed = if isNull remoteUrl then "" else remoteUrl.Trim()

        let path =
            if trimmed.StartsWith("git@", StringComparison.Ordinal) then
                match trimmed.IndexOf ':' with
                | -1 -> None
                | index -> Some(trimmed.Substring(index + 1))
            else
                match Uri.TryCreate(trimmed, UriKind.Absolute) with
                | true, uri when uri.Scheme = "https" || uri.Scheme = "http" || uri.Scheme = "ssh" -> Some(uri.AbsolutePath.TrimStart('/'))
                | _ -> None

        path
        |> Option.map (fun p -> if p.EndsWith(".git", StringComparison.Ordinal) then p.Substring(0, p.Length - 4) else p)
        |> Option.map (fun p ->
            let parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 2 then parts[parts.Length - 2] + "/" + parts[parts.Length - 1] else p)
        |> Option.bind tryCreate

/// A repository as Praxis knows it. `ProviderId` is the provider's stable
/// repository ID (for GitHub, the numeric repository ID); when it is absent
/// the identity is *legacy*: known only by its locator, which can be
/// renamed, transferred or reused, so it is never treated as proven.
type RepositoryIdentity =
    private
        { provider: RepositoryProvider
          providerId: string option
          locator: RepositoryLocator option }

    member this.Provider = this.provider
    member this.ProviderId = this.providerId
    member this.Locator = this.locator

/// How two repository identities relate.
[<RequireQualifiedAccess>]
type RepositoryMatch =
    /// The same provider and the same stable provider ID; locators may differ
    /// (a rename or transfer).
    | Same
    /// Different providers, or different stable provider IDs.
    | Different
    /// One side has no stable ID, and the locators are equal: the same
    /// repository as far as can be told, but not verified.
    | UnverifiedSameLocator
    /// One side has no stable ID and the locators differ or are unknown: a
    /// rename and a different repository cannot be told apart.
    | Undetermined

[<RequireQualifiedAccess>]
module RepositoryIdentity =
    let private providerIdPattern = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)

    let isValidProviderId (value: string) = not (isNull value) && providerIdPattern.IsMatch value

    /// A repository identity needs a provider and at least one of a stable
    /// provider ID and a locator.
    let create (provider: RepositoryProvider) (providerId: string option) (locator: RepositoryLocator option) : Result<RepositoryIdentity, string> =
        match providerId, locator with
        | None, None -> Error "a repository identity needs a stable provider ID or an owner/repo locator"
        | Some id, _ when not (isValidProviderId id) -> Error $"'{id}' is not a provider repository ID (letters, digits, '.', '_' or '-')"
        | _ ->
            Ok
                { provider = provider
                  providerId = providerId
                  locator = locator }

    /// A legacy identity: a locator without a stable provider ID
    /// (PRX-REMOTE-048). It is represented explicitly, never guessed into a
    /// stable one.
    let legacy (provider: RepositoryProvider) (locator: RepositoryLocator) =
        { provider = provider
          providerId = None
          locator = Some locator }

    let isLegacy (identity: RepositoryIdentity) = identity.providerId.IsNone

    /// The structural repository ID `provider:providerId` (for example
    /// `github:123456789`), or `None` for a legacy identity.
    let repositoryId (identity: RepositoryIdentity) =
        identity.providerId |> Option.map (fun id -> $"{RepositoryProvider.value identity.provider}:{id}")

    /// Parses a structural repository ID `provider:providerId`.
    let tryParseRepositoryId (value: string) =
        match (if isNull value then -1 else value.IndexOf ':') with
        | index when index > 0 ->
            match RepositoryProvider.tryCreate (value.Substring(0, index)) with
            | Some provider when isValidProviderId (value.Substring(index + 1)) -> Some(provider, value.Substring(index + 1))
            | _ -> None
        | _ -> None

    /// The human-readable form: the locator when known, else the repository ID.
    let display (identity: RepositoryIdentity) =
        match identity.locator, repositoryId identity with
        | Some locator, _ -> RepositoryLocator.value locator
        | None, Some id -> id
        | None, None -> "unknown"

    let compare (left: RepositoryIdentity) (right: RepositoryIdentity) =
        if left.provider <> right.provider then
            RepositoryMatch.Different
        else
            match left.providerId, right.providerId with
            | Some l, Some r -> if l = r then RepositoryMatch.Same else RepositoryMatch.Different
            | _ ->
                match left.locator, right.locator with
                | Some l, Some r when l = r -> RepositoryMatch.UnverifiedSameLocator
                | _ -> RepositoryMatch.Undetermined

    /// The stable identity with the current display locator: a rename or
    /// transfer changes only the locator (PRX-REMOTE-046).
    let withLocator (locator: RepositoryLocator) (identity: RepositoryIdentity) = { identity with locator = Some locator }

    /// Upgrades a legacy identity with verified repository evidence
    /// (PRX-REMOTE-048): only when the evidence carries a stable ID and does
    /// not contradict what the legacy record says.
    let upgrade (legacyIdentity: RepositoryIdentity) (verified: RepositoryIdentity) : Result<RepositoryIdentity, string> =
        match verified.providerId with
        | None -> Error "the evidence carries no stable provider ID, so the legacy identity cannot be upgraded"
        | Some _ ->
            match compare legacyIdentity verified with
            | RepositoryMatch.Same
            | RepositoryMatch.UnverifiedSameLocator -> Ok verified
            | RepositoryMatch.Different -> Error "the evidence names a different repository"
            | RepositoryMatch.Undetermined ->
                Error "the legacy locator does not match the evidence; a rename cannot be told apart from another repository without a stable ID"

    /// Conflicting identities within one observation fail closed
    /// (PRX-REMOTE-047): one stable ID seen with two locators, or one
    /// locator claimed by two stable IDs.
    let conflicts (identities: RepositoryIdentity list) : string list =
        let stable = identities |> List.filter (fun identity -> identity.providerId.IsSome) |> List.distinct

        let byId =
            stable
            |> List.filter (fun identity -> identity.locator.IsSome)
            |> List.groupBy repositoryId
            |> List.choose (fun (id, group) ->
                match group |> List.choose _.locator |> List.distinct with
                | _ :: _ :: _ as locators ->
                    let names = locators |> List.map RepositoryLocator.value |> String.concat ", "
                    let repository = Option.defaultValue "" id
                    Some $"repository {repository} is seen with more than one locator ({names})"
                | _ -> None)

        let byLocator =
            stable
            |> List.choose (fun identity -> identity.locator |> Option.map (fun locator -> (identity.provider, locator), identity))
            |> List.groupBy fst
            |> List.choose (fun ((_, locator), group) ->
                match group |> List.map (snd >> repositoryId) |> List.distinct with
                | _ :: _ :: _ as ids ->
                    let names = ids |> List.choose id |> String.concat ", "
                    Some $"locator {RepositoryLocator.value locator} is claimed by more than one repository ({names})"
                | _ -> None)

        byId @ byLocator

/// A repository-local work-item ID (`WI-0042`, `VIG-15`). Unique only
/// inside its repository (PRX-REMOTE-045).
[<Struct>]
type LocalWorkItemId = private LocalWorkItemId of string

[<RequireQualifiedAccess>]
module LocalWorkItemId =
    let private pattern = Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)

    let tryCreate (value: string) =
        if not (isNull value) && pattern.IsMatch value then Some(LocalWorkItemId value) else None

    let value (LocalWorkItemId id) = id

/// The canonical, globally unambiguous identity of a work item: its
/// repository plus its local ID (PRX-REMOTE-045, 049).
type WorkItemIdentity =
    { Repository: RepositoryIdentity
      LocalId: LocalWorkItemId }

[<RequireQualifiedAccess>]
module WorkItemIdentity =
    let create repository localId = { Repository = repository; LocalId = localId }

    /// `owner/repo:LOCAL-ID` for people, logs and CLI output. Derived, never
    /// parsed back into identity (PRX-REMOTE-047).
    let display (identity: WorkItemIdentity) =
        $"{RepositoryIdentity.display identity.Repository}:{LocalWorkItemId.value identity.LocalId}"

    /// The uniqueness key: stable repository ID plus local ID, or `None` for
    /// a work item in a legacy repository, whose uniqueness is not proven.
    let key (identity: WorkItemIdentity) =
        RepositoryIdentity.repositoryId identity.Repository
        |> Option.map (fun repository -> $"{repository}/{LocalWorkItemId.value identity.LocalId}")

    /// Duplicate or conflicting canonical identities fail closed: the same
    /// key recorded twice in one catalog, or repository conflicts.
    let conflicts (identities: WorkItemIdentity list) : string list =
        let duplicates =
            identities
            |> List.choose (fun identity -> key identity |> Option.map (fun key -> key, identity))
            |> List.groupBy fst
            |> List.filter (fun (_, group) -> group.Length > 1)
            |> List.map (fun (key, _) -> $"work item {key} is recorded more than once")

        duplicates @ RepositoryIdentity.conflicts (identities |> List.map _.Repository)

/// A reference to a work item as a caller wrote it.
[<RequireQualifiedAccess>]
type WorkItemReference =
    /// A bare local ID. It means a repository only in a context that makes
    /// it unambiguous.
    | Unqualified of LocalWorkItemId
    | Qualified of WorkItemIdentity

/// Why a reference does not denote exactly one work item. Every case fails
/// closed: Praxis never guesses a repository.
[<RequireQualifiedAccess>]
type IdentityFailure =
    | NoRepositoryContext of localId: string
    | UnknownWorkItem of localId: string
    | Ambiguous of localId: string * repositories: string list
    | ForeignRepository of reference: string * context: string
    | UnverifiedRepository of reference: string * context: string
    | Conflicting of reasons: string list

[<RequireQualifiedAccess>]
module IdentityFailure =
    let code failure =
        match failure with
        | IdentityFailure.NoRepositoryContext _ -> "no-repository-context"
        | IdentityFailure.UnknownWorkItem _ -> "unknown-work-item"
        | IdentityFailure.Ambiguous _ -> "ambiguous-unqualified-reference"
        | IdentityFailure.ForeignRepository _ -> "foreign-repository"
        | IdentityFailure.UnverifiedRepository _ -> "unverified-repository"
        | IdentityFailure.Conflicting _ -> "conflicting-identity"

    let message failure =
        match failure with
        | IdentityFailure.NoRepositoryContext id -> $"'{id}' is unqualified and no repository context makes it unambiguous"
        | IdentityFailure.UnknownWorkItem id -> $"no repository in context records '{id}'"
        | IdentityFailure.Ambiguous(id, repositories) ->
            let names = String.concat ", " repositories
            $"'{id}' is unqualified and exists in more than one repository ({names}); qualify it as owner/repo:{id}"
        | IdentityFailure.ForeignRepository(reference, context) -> $"'{reference}' names another repository than {context}"
        | IdentityFailure.UnverifiedRepository(reference, context) ->
            $"'{reference}' cannot be verified as {context}: one side has no stable repository ID and the locators differ"
        | IdentityFailure.Conflicting reasons -> "conflicting repository identity: " + String.concat "; " reasons

[<RequireQualifiedAccess>]
module WorkItemReference =
    let display (reference: WorkItemReference) =
        match reference with
        | WorkItemReference.Unqualified id -> LocalWorkItemId.value id
        | WorkItemReference.Qualified identity -> WorkItemIdentity.display identity

    /// Resolves a reference inside one repository context (PRX-REMOTE-045):
    /// a bare ID means the context; a qualified one must be the context.
    let resolveIn (context: RepositoryIdentity) (reference: WorkItemReference) : Result<WorkItemIdentity, IdentityFailure> =
        match reference with
        | WorkItemReference.Unqualified id -> Ok(WorkItemIdentity.create context id)
        | WorkItemReference.Qualified identity ->
            match RepositoryIdentity.compare identity.Repository context with
            | RepositoryMatch.Same
            | RepositoryMatch.UnverifiedSameLocator ->
                // The context is the richer, locally established identity.
                Ok(WorkItemIdentity.create context identity.LocalId)
            | RepositoryMatch.Different -> Error(IdentityFailure.ForeignRepository(display reference, RepositoryIdentity.display context))
            | RepositoryMatch.Undetermined -> Error(IdentityFailure.UnverifiedRepository(display reference, RepositoryIdentity.display context))

    /// Resolves a reference across several repositories, each with the local
    /// IDs it records (cross-repository lookup). An unqualified ID found in
    /// more than one repository is ambiguous and fails closed.
    let lookup (catalog: (RepositoryIdentity * LocalWorkItemId list) list) (reference: WorkItemReference) : Result<WorkItemIdentity, IdentityFailure> =
        match RepositoryIdentity.conflicts (catalog |> List.map fst) with
        | _ :: _ as reasons -> Error(IdentityFailure.Conflicting reasons)
        | [] ->
            match reference with
            | WorkItemReference.Unqualified id ->
                match catalog |> List.filter (fun (_, ids) -> List.contains id ids) with
                | [] -> Error(IdentityFailure.UnknownWorkItem(LocalWorkItemId.value id))
                | [ repository, _ ] -> Ok(WorkItemIdentity.create repository id)
                | several ->
                    Error(IdentityFailure.Ambiguous(LocalWorkItemId.value id, several |> List.map (fst >> RepositoryIdentity.display)))
            | WorkItemReference.Qualified identity ->
                let candidates =
                    catalog
                    |> List.filter (fun (repository, _) ->
                        match RepositoryIdentity.compare identity.Repository repository with
                        | RepositoryMatch.Same
                        | RepositoryMatch.UnverifiedSameLocator -> true
                        | _ -> false)

                match candidates with
                | [ repository, ids ] when List.contains identity.LocalId ids -> Ok(WorkItemIdentity.create repository identity.LocalId)
                | [ _ ] -> Error(IdentityFailure.UnknownWorkItem(display reference))
                | [] -> Error(IdentityFailure.UnknownWorkItem(display reference))
                | several -> Error(IdentityFailure.Ambiguous(display reference, several |> List.map (fst >> RepositoryIdentity.display)))
