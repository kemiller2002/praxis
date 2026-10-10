namespace Praxis.Infrastructure.Work

open System
open System.IO
open System.Text
open Praxis.Application.Work
open Praxis.Contracts.Work

[<RequireQualifiedAccess>]
module FileLocalHandoffInspection =
    let private readPacket (root: string) (path: string) =
        try
            let fullPath = if Path.IsPathRooted path then path else Path.Combine(root, path)
            use stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            if stream.Length > int64 LocalAgentHandoffJson.MaxDocumentBytes then Error "packet exceeds 65536 UTF-8 bytes"
            else
                use reader = new StreamReader(stream, UTF8Encoding(false, true), false)
                let buffer = Array.zeroCreate<char> (LocalAgentHandoffJson.MaxDocumentBytes + 1)
                let count = reader.ReadBlock(buffer, 0, buffer.Length)
                if count > LocalAgentHandoffJson.MaxDocumentBytes then Error "packet exceeds bounded input size"
                else Ok(String(buffer, 0, count))
        with
        | :? IOException as error -> Error("cannot read packet: " + error.Message)
        | :? UnauthorizedAccessException as error -> Error("cannot read packet: " + error.Message)
        | :? ArgumentException as error -> Error("invalid packet path or encoding: " + error.Message)

    let create root : LocalHandoffInspectionPorts =
        { ReadPacket = readPacket root; Now = fun () -> DateTimeOffset.UtcNow }
