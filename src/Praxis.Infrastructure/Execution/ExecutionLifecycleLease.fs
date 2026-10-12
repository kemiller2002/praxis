namespace Praxis.Infrastructure.Execution

open Praxis.Infrastructure.Artifacts

/// Raised by serialized execution ports when a write cannot obtain the
/// lifecycle lease. Callers report it as a refusal; nothing was written.
exception ExecutionLifecycleLeaseUnavailable of string

/// Execution-envelope writers share the native lock order used by work
/// transitions, group membership changes and the local worker host:
/// `work-protocol`, then `work-groups` (WI-0087). While a host dispatch or
/// acceptance scope holds these leases no envelope, step ledger or
/// verification record changes underneath its observations. The registry
/// lock is not reentrant, so callers already holding `work-protocol` must use
/// unserialized ports instead of nesting this lease.
[<RequireQualifiedAccess>]
module ExecutionLifecycleLease =
    let resources = [ "work-protocol"; "work-groups" ]

    let within (root: string) (action: unit -> 'a) : Result<'a, string> =
        let rec hold remaining =
            match remaining with
            | [] -> Ok(action ())
            | resource :: rest ->
                match RegistryLock.acquire root resource RegistryLock.defaultSettings with
                | Error failure -> Error("execution lifecycle lease unavailable: " + failure.Message)
                | Ok lease ->
                    let result =
                        try hold rest
                        with _ ->
                            lease.Release() |> ignore
                            reraise ()
                    match lease.Release(), result with
                    | Error failure, Ok _ -> Error("execution lifecycle lease release uncertain: " + failure.Message)
                    | _, value -> value
        hold resources

    /// For port operations that cannot return a Result: throw a typed refusal.
    let required root action =
        match within root action with
        | Ok value -> value
        | Error reason -> raise (ExecutionLifecycleLeaseUnavailable reason)
