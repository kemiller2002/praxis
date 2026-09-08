namespace Ros.Application.Work

open Ros.Domain.Work

[<RequireQualifiedAccess>]
module WorkOperations =
    let decideTransition request = WorkTransition.decide request
