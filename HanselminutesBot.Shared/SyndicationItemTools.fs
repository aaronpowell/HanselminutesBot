namespace HanselminutesBot.Shared

open System
open System.Text.RegularExpressions

module SyndicationItemTools =
    let GenerateId (id: string) (date: DateTimeOffset) =
        [("F#", "FSharp"); ("C#", "CSharp"); (".NET", "dotnet")]
        |> Seq.fold (fun (acc: string) (from, to') -> acc.Replace(from, to')) id
        |> fun s -> Regex.Replace(s, "[^A-Za-z0-9-_]", "_")
        |> fun s -> $"""{date.ToString("yyyy-MM-dd")}_{s}"""
        |> fun s -> s.ToLowerInvariant()
