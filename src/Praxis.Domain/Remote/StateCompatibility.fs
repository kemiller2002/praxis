namespace Praxis.Domain.Remote

/// Declared compatibility between a Praxis executor and the repository state
/// it reads (PRX-QUAL-010). The authority is the state-schema versions a
/// release reads, not equality of release and pin versions: an executor may
/// mutate a repository only when it reads every state document's version.
type StateCompatibility =
    { /// The remote protocol this release speaks (`praxis.remote` MAJOR.MINOR).
      RemoteProtocol: string
      /// Per state document, the schema versions this release reads.
      Reads: Map<string, string list>
      /// Per state document, the schema version this release writes.
      Writes: Map<string, string> }

/// One state document whose version an executor does not read.
type StateIncompatibility =
    { Document: string
      Version: string
      Supported: string list }

[<RequireQualifiedAccess>]
module StateCompatibility =
    [<Literal>]
    let WorkQueue = "work-queue"

    [<Literal>]
    let WorkContext = "work-context"

    [<Literal>]
    let WorkGroups = "work-groups"

    [<Literal>]
    let WorkEvents = "work-events"

    /// The state documents whose schema version gates remote mutation, and
    /// the repository-relative file each lives in.
    let documents =
        [ WorkQueue, ".ros/work/queue.json"
          WorkContext, ".ros/context/current.json"
          WorkGroups, ".ros/work/groups.json"
          WorkEvents, ".ros/events/events.jsonl" ]

    /// This build's compatibility. `release.json` declares the same table
    /// (a premerge fence keeps them equal), so a published release states
    /// what it can read without anyone running it.
    let current =
        { RemoteProtocol = ProtocolVersion.code ProtocolVersion.current
          Reads =
            Map.ofList
                [ WorkQueue, [ "1.0.0" ]
                  WorkContext, [ "1.0.0" ]
                  WorkGroups, [ "1"; "2" ]
                  WorkEvents, [ "1.0.0" ] ]
          Writes =
            Map.ofList
                [ WorkQueue, "1.0.0"
                  WorkContext, "1.0.0"
                  WorkGroups, "2"
                  WorkEvents, "1.0.0" ] }

    /// Every observed `(document, version)` the compatibility does not read.
    /// A document the compatibility does not know is incompatible too: an
    /// executor never guesses that it can read state it has no rule for.
    let check (compatibility: StateCompatibility) (observed: (string * string) list) : StateIncompatibility list =
        observed
        |> List.distinct
        |> List.choose (fun (document, version) ->
            let supported = compatibility.Reads |> Map.tryFind document |> Option.defaultValue []

            if List.contains version supported then
                None
            else
                Some
                    { Document = document
                      Version = version
                      Supported = supported })

    let describe (incompatibility: StateIncompatibility) =
        let supported =
            match incompatibility.Supported with
            | [] -> "none"
            | versions -> String.concat ", " versions

        $"{incompatibility.Document} schema {incompatibility.Version} (this executor reads: {supported})"
