namespace Ros.Contracts

open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

[<RequireQualifiedAccess>]
module JsonRendering =
    let renderIndented (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()

        let options =
            JsonWriterOptions(
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            )

        use writer = new Utf8JsonWriter(stream, options)
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"
