namespace Praxis.Infrastructure.Work

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Praxis.Application.Work

/// OS/process effects, separate from the shared supervisor. These are trusted
/// host observations, never decoded from worker output or capability flags.
type LocalWorkerProcessPlatform =
    { ObserveIdentity: Process -> Result<string, string>
      StopAndObserveRootExit: Process -> CancellationToken -> Task<Result<unit, string>> }

[<RequireQualifiedAccess>]
module LocalWorkerProcessPlatform =
    let private bounded (text: string) = if text.Length > 1024 then text.Substring(0, 1024) else text

    let operatingSystem () =
        if OperatingSystem.IsLinux() then Ok LocalWorkerOperatingSystem.Linux
        elif OperatingSystem.IsMacOS() then Ok LocalWorkerOperatingSystem.MacOS
        elif OperatingSystem.IsWindows() then Ok LocalWorkerOperatingSystem.Windows
        else Error "local worker runtime operating system is unsupported"

    let selectCurrent catalog admitted =
        operatingSystem() |> Result.bind (fun os -> LocalWorkerAdapters.select os catalog admitted)

    /// Functional .NET mechanism on supported OSes. Root observation and a
    /// best-effort tree kill do not establish equivalent descendant containment.
    let dotNet : LocalWorkerProcessPlatform =
        { ObserveIdentity = fun child ->
              try Ok(sprintf "pid:%d/start-utc-ticks:%d" child.Id (child.StartTime.ToUniversalTime().Ticks))
              with e -> Error(sprintf "process incarnation unavailable (pid:%d): %s" child.Id (bounded e.Message))
          StopAndObserveRootExit = fun child cancellation -> task {
              try
                  if not child.HasExited then child.Kill(true)
                  do! child.WaitForExitAsync(cancellation)
                  return Ok()
              with e -> return Error(bounded e.Message) } }
