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
open Azure.Storage.Queues
open HanselminutesBot.ServiceDefaults
open System.Text.RegularExpressions

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
                       Description = "A comma separated list of topics from the description" |}
                Speakers =
                    {| Type = "string"
                       Description = "A comma separated list of speakers from the description" |} |}
           Required = [| "summary"; "topics"; "speakers" |] |}

    fd.Parameters <- BinaryData.FromObjectAsJson(d, jsonOptions)
    fd

let idGenerator (id: string) (date: DateTimeOffset) =
    [("F#", "FSharp"); ("C#", "CSharp"); (".NET", "dotnet")]
    |> Seq.fold (fun (acc: string) (from, to') -> acc.Replace(from, to')) id
    |> fun s -> Regex.Replace(s, "[^A-Za-z0-9-_]", "_")
    |> fun s -> $"""{date.ToString("yyyy-MM-dd")}_{s}"""
    |> fun s -> s.ToLowerInvariant()

let buildMemoryRecord (feed: SyndicationFeed) (client: OpenAIClient) (logger: ILogger) =
    let fd = makeAOAIFunction()

    let payloads =
        feed.Items
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
              Id = idGenerator item.Title.Text item.PublishDate
              Topics = [] }

        try
            let parsed = JsonSerializer.Deserialize<CompletionPayload> content

            logger.LogInformation("Parsed function definition for {0}. {1}", item.Title.Text, parsed)

            { mr with Speakers = parsed.speakers |?? []; Summary = parsed.summary; Topics = parsed.topics |?? [] }
        with
        | _ ->
            logger.LogWarning("Failed to parse function definition for {0}", item.Title.Text)
            mr)

let importMemoryRecord (memoryClient: IKernelMemory) (logger: ILogger) cancellationToken (mr: MemoryRecord) =
    let tags = TagCollection()
    tags.Add("title", mr.Title)
    tags.Add("date", mr.Date.ToString("yyyy-MM-dd"))
    mr.Speakers
    // Sometimes the model just returns "Scott" as a speaker, which is not very useful
    // so we filter those out
    |> Seq.filter(fun speaker -> speaker = "Scott")
    |> Seq.iter(fun speaker -> tags.Add("speaker", speaker))
    mr.Topics |> Seq.iter(fun topic -> tags.Add("topic", topic))
    tags.Add("uri", mr.Uri.ToString())
    logger.LogInformation ("Importing {0}", mr.Id)
    try
        memoryClient.ImportTextAsync(mr.Summary, mr.Id, tags, null, Seq.empty, cancellationToken)
    with ex ->
        logger.LogError(ex, "Failed to import {0}", mr.Id)
        task { return mr.Id }

type Worker(
    logger: ILogger<Worker>,
    client: OpenAIClient,
    memoryClient: IKernelMemory,
    queueServiceClient: QueueServiceClient) =
    inherit BackgroundService()

    override __.ExecuteAsync(cancellationToken) =
        task {
            let queueClient = queueServiceClient.GetQueueClient ServiceConstants.BuildIndexQueueServiceName

            let! _ = queueClient.CreateIfNotExistsAsync(dict [], cancellationToken)

            while not cancellationToken.IsCancellationRequested do
                let! messages = queueClient.ReceiveMessagesAsync(32, TimeSpan.FromSeconds 30.0, cancellationToken)

                let importer' = importMemoryRecord memoryClient logger cancellationToken

                let! _ =
                    messages.Value
                    |> Seq.map(fun m ->
                        task {
                            let file = File.OpenRead(Path.Join(Directory.GetCurrentDirectory(), "Data", "hanselminutes.rss"))
                            let feed = SyndicationFeed.Load(XmlReader.Create file)

                            let memoryRecords = buildMemoryRecord feed client logger

                            let importTasks = memoryRecords |> Seq.map importer'

                            for t in importTasks do
                                try
                                    let! id = t
                                    logger.LogInformation("Import requested for {0}", id)
                                with ex ->
                                    logger.LogError(ex, "Failed to import memory record {0}", id)

                            let! _ = queueClient.DeleteMessageAsync(m.MessageId, m.PopReceipt, cancellationToken)
                            logger.LogInformation ("Imported {0} records", Seq.length memoryRecords)
                        })
                    |> Task.WhenAll

                logger.LogInformation "Waiting for next load request"

                // Sleep for a bit so we don't hammer the queue
                do! Task.Delay(TimeSpan.FromMinutes 1.0, cancellationToken)
        }