namespace Praxis.Domain.Foundations

/// What `.echelon/limen.json` records about an installation, as far as pin
/// evidence is concerned. Either field may be absent in an older manifest.
type LimenManifest =
    { Package: string option
      InstalledVersion: string option }

/// Whether a repository's Limen installation is pinned to the version an
/// application's foundations declaration expects. Pure: the caller supplies
/// the evidence it read, and this module only decides.
[<RequireQualifiedAccess>]
module LimenPin =
    /// Limen's npm package names, current first. 0.7.0 renamed
    /// `@echelon-foundry/typescript-wasm-kernel` (deprecated; 0.6.2 is its last
    /// version) to `@echelon-foundry/limen`. Both are recognized; nothing else is.
    let packageNames = [ "@echelon-foundry/limen"; "@echelon-foundry/typescript-wasm-kernel" ]

    let isLimenPackage (name: string) = packageNames |> List.contains name

    /// The pin evidence the manifest gives, or None when it gives none.
    ///
    /// Since 0.7.0 the installed verify workflow runs exactly the
    /// `installedVersion` recorded in `.echelon/limen.json`, so that field is
    /// the pin for a repository with no npm dependency on Limen. A manifest that
    /// records a different version, or a package that is not Limen, is
    /// evidence against the pin. With no expected version, any Limen
    /// configuration present counts, as it always has.
    let manifestEvidence (expectedVersion: string option) (configurationPresent: bool) (manifest: LimenManifest option) =
        match expectedVersion, manifest with
        | None, _ -> if configurationPresent || manifest.IsSome then Some true else None
        | Some expected, Some recorded ->
            Some(
                recorded.InstalledVersion = Some expected
                && recorded.Package |> Option.forall isLimenPackage
            )
        | Some _, None -> None

    /// Pinned when there is at least one piece of evidence and every piece
    /// agrees. A pinned npm dependency that contradicts the manifest is not a
    /// pin: CI would verify with a different version than the one shipped.
    let isPinned (npmEvidence: bool option) (manifestEvidence: bool option) =
        match List.choose id [ npmEvidence; manifestEvidence ] with
        | [] -> false
        | evidence -> List.forall id evidence
