namespace Praxis.Infrastructure

[<RequireQualifiedAccess>]
module AssemblyInfo =
    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Praxis.Tests")>]
    do ()

    [<Literal>]
    let Name = "Praxis.Infrastructure"

    let ApplicationAssembly = Praxis.Application.AssemblyInfo.Name
