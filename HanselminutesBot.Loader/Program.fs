open Microsoft.Extensions.Configuration
open System
open System.IO
open System.ServiceModel.Syndication
open System.Xml
open Azure.AI.OpenAI
open Azure
open System.Text.Json

type CompletionPayload =
    { speakers: string list
      summary: string }

let env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")

let builder = 
    ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile($"appsettings.{env}.json", true, true)

let configuration = builder.Build()

let file = File.OpenRead(Path.Join(Directory.GetCurrentDirectory(), "Data", "hanselminutes.rss"))
let feed = SyndicationFeed.Load(XmlReader.Create file)

let promptTemplate = sprintf "
Create a JSON object that contains the name of the speakers in the podcast episode, extracted from the description, along with a summarisation of the podcast.
The host is Scott Hanselman, or just Scott, and does not need to be listed in the speaker names.

Use the following template:

{
    \"speakers\": [\"Speaker 1\", \"Speaker 2\"],
    \"summary\": \"A summarisation of the podcast\"
}

The episode decsription is:
%s
"

let endpoint =
    match configuration.["OpenAI:Endpoint"] with
    | null -> failwith "OpenAI:Endpoint not found in configuration"
    | value -> value

let key =
    match configuration.["OpenAI:Key"] with
    | null -> failwith "OpenAI:Key not found in configuration"
    | value -> value

let client = OpenAIClient(Uri(endpoint), AzureKeyCredential(key))

let payloads =
    feed.Items
    |> Seq.map(fun item ->
        let description = item.Summary.Text
        let prompt = promptTemplate description
        let opts = ChatCompletionsOptions("gpt-35-turbo", [ChatRequestUserMessage prompt])
        (item, client.GetChatCompletions opts))

type MemoryRecord =
    { Title: string
      Date: DateTimeOffset
      Speakers: string list
      Summary: string
      Uri: Uri}

let memoryRecords = 
    payloads
    |> Seq.map(fun (item, result) ->
        let response = result.Value
        let content = response.Choices.[0].Message.Content

        let mr =
            { Title = item.Title.Text
              Date = item.PublishDate
              Speakers = []
              Summary = item.Summary.Text
              Uri = item.Links.[1].Uri }

        try
            let parsed = JsonSerializer.Deserialize<CompletionPayload> content

            { mr with Speakers = parsed.speakers; Summary = parsed.summary }
        with
        | _ -> mr)

let json = JsonSerializer.Serialize memoryRecords
File.WriteAllText(Path.Join(Directory.GetCurrentDirectory(), "Data", "memory.json"), json)
