module CommaListJsonParser

open System.Text.Json.Serialization

type CommaListJsonConverter() =
    inherit JsonConverter<string list>()

    override __.Read(reader, t, _) =
        let s = reader.GetString()
        s.Split ','
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray

    override __.Write(writer, value, _) =
        value
        |> String.concat ","
        |> writer.WriteStringValue
        |> ignore