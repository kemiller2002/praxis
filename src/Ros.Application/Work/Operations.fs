namespace Ros.Application.Work

open Ros.Domain.Work

[<RequireQualifiedAccess>]
type WorkPersistenceOutcome =
    | Failed
    | Indeterminate

type WorkPersistenceFailure =
    { Operation: string
      Path: string option
      Message: string
      Outcome: WorkPersistenceOutcome }

type WorkStateWrite =
    { Path: string
      Content: string }

[<RequireQualifiedAccess>]
module WorkOperations =
    let decideTransition request = WorkTransition.decide request
