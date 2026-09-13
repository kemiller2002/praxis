namespace EchelonFoundry.Ros.Integration

/// A pointer to something outside this contract that substantiates an
/// `ActivityObservation` -- a commit SHA, a pull request URL, a CI run
/// id. `Kind` names what `Reference` is; this package does not
/// interpret either value.
type Evidence = { Kind: string; Reference: string }
