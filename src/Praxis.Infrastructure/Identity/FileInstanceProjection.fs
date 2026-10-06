namespace Praxis.Infrastructure.Identity

open System.IO
open Praxis.Domain.Identity
open Praxis.Domain.Remote
open Praxis.Infrastructure.Installation
open Praxis.Infrastructure.Remote

/// Gathers the facts of an instance projection (DER-19, 20, 26) from local
/// state only: no Echelon component, network or credential is needed, and
/// an integration that cannot be confirmed is `unknown`, not a failure.
[<RequireQualifiedAccess>]
module FileInstanceProjection =
    /// The runtime-free envelope schema version reconciliation accepts.
    [<Literal>]
    let ReconciliationProtocolVersion = "1.0"

    let private integrations (root: string) =
        let administration =
            match AdministrationClient.loadConfig root None with
            | Ok None -> IntegrationAvailability.NotConfigured
            | Ok(Some _) -> IntegrationAvailability.Unknown
            | Error reason -> IntegrationAvailability.Unavailable reason

        [ "project-administration", administration
          "echelon-registry", IntegrationAvailability.Unknown
          "vigila", IntegrationAvailability.Unknown ]

    let private capabilities (root: string) =
        [ yield "native-execution"
          yield "envelope-reconciliation"
          if File.Exists(Path.Combine(root, ".github", "workflows", "praxis-remote.yml")) then
              yield "remote-execution"
          for capability in FileRemoteRepository.readRepositoryCapabilities root do
              yield "remote:" + Capability.code capability ]

    /// The projection of the local instance, or why there is none to project.
    let build (root: string) (praxisVersion: string) : Result<InstanceProjection, string> =
        match FileInstanceIdentityStore.local root with
        | LocalInstance.Missing -> Error "this repository has no Praxis instance identity; run 'praxis instance init' first"
        | LocalInstance.Unreadable reason -> Error $".praxis/instance.json is unreadable: {reason}"
        | LocalInstance.Present(_, InstanceBinding.Foreign _) ->
            Error "the local instance identity belongs to another repository; run 'praxis instance init --reinitialize --reason TEXT' first"
        | LocalInstance.Present(record, _) ->
            Ok(
                InstanceProjection.create
                    record
                    (FileRepositoryIdentityRepository.current RepositoryObserver.environmentVariable root)
                    praxisVersion
                    ReconciliationProtocolVersion
                    (ProtocolVersion.code ProtocolVersion.current)
                    (capabilities root)
                    (integrations root)
            )
