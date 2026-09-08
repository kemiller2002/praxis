namespace Ros.Application.Git

open Ros.Domain.Git

type GitRepository =
    { ObserveStatus: unit -> GitStatusObservation }

[<RequireQualifiedAccess>]
module GitOperations =
    let observe repository = repository.ObserveStatus()
