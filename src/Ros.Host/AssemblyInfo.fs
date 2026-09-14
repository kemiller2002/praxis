namespace Ros.Host

[<RequireQualifiedAccess>]
module AssemblyInfo =
    [<Literal>]
    let Name = "Ros.Host"

    /// The Central host's own version -- independent of both the
    /// EchelonFoundry.Ros.Integration package's semver and this repo's
    /// own npm package version. Reported by GET /version.
    [<Literal>]
    let Version = "0.1.0"
