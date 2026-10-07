namespace Ros.Infrastructure.Planning

open Ros.Application.Planning

/// The file-backed `WorkGroupPort`: members are read through the planner's
/// own queue and live-context readers; only the group store is written.
[<RequireQualifiedAccess>]
module FileWorkGroupRepository =
    let create (root: string) : WorkGroupPort =
        { ReadStore = fun () -> FileWorkGroupStore.read root
          Queue = fun () -> FilePlanningRepository.readQueue root
          Live = fun () -> FilePlanningRepository.readLive root
          WriteStore = FileWorkGroupStore.write root }
