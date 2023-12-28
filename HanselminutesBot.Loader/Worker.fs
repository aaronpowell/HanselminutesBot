[<AutoOpenAttribute>]
module Worker

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open System.IO
open System.ServiceModel.Syndication
open System.Xml
open Azure.AI.OpenAI
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.KernelMemory

type CompletionPayload =
  { [<JsonConverter(typeof<CommaListJsonParser.CommaListJsonConverter>)>] speakers: string list
    summary: string
    [<JsonConverter(typeof<CommaListJsonParser.CommaListJsonConverter>)>] topics: string list }

type MemoryRecord =
  { Title: string
    Date: DateTimeOffset
    Speakers: string list
    Summary: string
    Uri: Uri
    Id: string
    Topics: string list }

let makeAOAIFunction () =
    let jsonOptions = JsonSerializerOptions()
    jsonOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

    let fd = FunctionDefinition("extract_info")
    fd.Description <- "Extracts information from a podcast episode description"
    let d =
        {| Type = "object"
           Properties =
             {| Summary =
                    {| Type = "string"
                       Description = "A summary of the podcast" |}
                Topics =
                    {| Type = "string"
                       Description = "A comman separated list of topics from the description" |}
                Speakers =
                    {| Type = "string"
                       Description = "A comman separated list of speakers from the description" |} |}
           Required = [| "summary"; "topics"; "speakers" |] |}

    fd.Parameters <- BinaryData.FromObjectAsJson(d, jsonOptions)
    fd

let buildMemoryRecord (feed: SyndicationFeed) (client: OpenAIClient) =
    let fd = makeAOAIFunction()

    let payloads =
        feed.Items
        |> Seq.take 10 // just to speed up local dev, don't need all 900+ episodes yet
        |> Seq.map(fun item ->
            let description = item.Summary.Text
            let opts = ChatCompletionsOptions("gpt-35-turbo", [ChatRequestUserMessage description])
            opts.Functions.Add(fd)
            opts.FunctionCall <- fd
            (item, client.GetChatCompletions opts))

    payloads
    |> Seq.map(fun (item, result) ->
        let response = result.Value
        let content = response.Choices.[0].Message.FunctionCall.Arguments

        let mr =
            { Title = item.Title.Text
              Date = item.PublishDate
              Speakers = []
              Summary = item.Summary.Text
              Uri = item.Links.[1].Uri
              Id = item.Id
              Topics = [] }

        try
            let parsed = JsonSerializer.Deserialize<CompletionPayload> content

            { mr with Speakers = parsed.speakers; Summary = parsed.summary; Topics = parsed.topics }
        with
        | _ -> mr)

type Worker(logger: ILogger<Worker>, client: OpenAIClient, memoryClient: IKernelMemory) =
    inherit BackgroundService()

    override __.ExecuteAsync(cancellationToken) =
        task {
            let file = File.OpenRead(Path.Join(Directory.GetCurrentDirectory(), "Data", "hanselminutes.rss"))
            let feed = SyndicationFeed.Load(XmlReader.Create file)

            let memoryRecords = buildMemoryRecord feed client

            let importTasks = memoryRecords
                              |> Seq.map(fun mr ->
                                     let tags = TagCollection()
                                     tags.Add("title", mr.Title)
                                     tags.Add("date", mr.Date.ToString("yyyy-MM-dd"))
                                     mr.Speakers |> Seq.iter(fun speaker -> tags.Add("speaker", speaker))
                                     mr.Topics |> Seq.iter(fun topic -> tags.Add("topic", topic))
                                     tags.Add("uri", mr.Uri.ToString())
                                     sprintf "Importing %s" mr.Title |> logger.LogInformation
                                     memoryClient.ImportTextAsync(mr.Summary, mr.Id, tags, "summaries", Seq.empty, cancellationToken))

            let! _ = Task.WhenAll importTasks

            sprintf "Imported %d records" (Seq.length memoryRecords) |> logger.LogInformation
}