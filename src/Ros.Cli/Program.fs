module Ros.Cli.Program

[<Literal>]
let Version = "0.1.0-shadow"

let private usage =
    "Usage: ros-fs version | --version | --help"

[<EntryPoint>]
let main arguments =
    match arguments |> Array.toList with
    | [ "version" ]
    | [ "--version" ] ->
        printfn "ros-fs %s" Version
        0
    | []
    | [ "help" ]
    | [ "--help" ]
    | [ "-h" ] ->
        printfn "%s" usage
        0
    | _ ->
        eprintfn "%s" usage
        2
