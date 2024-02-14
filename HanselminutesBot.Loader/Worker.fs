[<AutoOpenAttribute>]
module Worker

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open System.ServiceModel.Syndication
open Azure.AI.OpenAI
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.KernelMemory
open Azure.Storage.Queues
open HanselminutesBot.Shared
open Azure
open Microsoft.Extensions.Configuration

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

let systemMessage = """
You are an assistant that will extract information from Podcast episode descriptions.
The key bits of information you will extract is a summary, the speakers, and the topics.
The host of the Podcast is Scott Hanselman, and should be ignored from the speaker list.

## Example
Description: "Quincy Larson, the teacher who founded freeCodeCamp.org, shares his inspiring journey of creating one of the most beloved learn-to-code resources. In this episode, he discusses why he launched freeCodeCamp, the importance of making coding accessible to all, and how it will forever remain free. Quincy also dives into the exciting new C# Certification program in partnership with Microsoft and freeCodeCamp, empowering learners to master this powerful language and build their tech careers."
Speakers: "Quincy Larson"
Topics: "freeCodeCamp, C#, Microsoft, certification, coding, tech careers"

Description: "In this episode of Hanselminutes, Scott Hanselman talks to Jose Tejada (JOTEGO), a passionate retro gaming enthusiast and FPGA developer. Jose shares his journey of creating FPGA cores for classic arcade games such as Pac-Man, Galaga, and Out Run, and how he distributes them through the MiSTer and Analogue Pocket platforms. Jose also explains the benefits and challenges of FPGA development, and why he thinks FPGA is the future of retro gaming preservation and emulation."
Speakers: "Jose Tejada"
Topics: "retro gaming"

Description: "Open Telemetry plays a pivotal role in monitoring, tracing, and understanding complex distributed systems. From practical applications to real-world examples, Scott and Dr. Sally break down the how and why of Open Telemetry, offering a perspective to harnessing its power for perf and troubleshooting."
Speakers: "Dr. Sally"
Topics: "Open Telemetry, monitoring, tracing, distributed systems, perf, troubleshooting"
"""

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

let processItem (fd: FunctionDefinition) (client: OpenAIClient) (config: IConfiguration) (item: SyndicationItem) =
    let description = item.Summary.Text
    let opts = ChatCompletionsOptions(config.["OpenAI:ChatDeployment"], [ChatRequestSystemMessage systemMessage; ChatRequestUserMessage description])
    opts.Functions.Add(fd)
    opts.FunctionCall <- fd
    client.GetChatCompletionsAsync opts

let unpackResponse (item: SyndicationItem) (result: Response<ChatCompletions>) id =
    let response = result.Value
    let content = response.Choices.[0].Message.FunctionCall.Arguments

    let mr =
        { Title = item.Title.Text
          Date = item.PublishDate
          Speakers = []
          Summary = item.Summary.Text
          Uri = item.Links.[1].Uri
          Id = id
          Topics = [] }

    try
        let parsed = JsonSerializer.Deserialize<CompletionPayload> content

        { mr with Speakers = parsed.speakers |?? []; Summary = parsed.summary; Topics = parsed.topics |?? [] }
    with
    | _ ->
        mr

let importMemoryRecord (memoryClient: IKernelMemory) (logger: ILogger) cancellationToken (mr: MemoryRecord) =
    let tags = TagCollection()
    tags.Add("title", mr.Title)
    tags.Add("date", mr.Date.ToString("yyyy-MM-dd"))
    mr.Speakers
    // Sometimes the model just returns "Scott" as a speaker, which is not very useful
    // so we filter those out
    |> Seq.filter(fun speaker -> speaker <> "Scott")
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
    queueServiceClient: QueueServiceClient,
    config: IConfiguration) =
    inherit BackgroundService()

    override __.ExecuteAsync(cancellationToken) =
        let feed = PodcastSource.GetFeed()
        task {
            let queueClient = queueServiceClient.GetQueueClient ServiceConstants.BuildIndexQueueServiceName

            let! _ = queueClient.CreateIfNotExistsAsync(dict [], cancellationToken)

            while not cancellationToken.IsCancellationRequested do
                let! messages = queueClient.ReceiveMessagesAsync(32, TimeSpan.FromSeconds 30.0, cancellationToken)

                let importer' = importMemoryRecord memoryClient logger cancellationToken
                let processItem' = processItem (makeAOAIFunction()) client config

                let! _ =
                    messages.Value
                    |> Seq.map(fun m ->
                        task {
                            let item = feed.Items |> Seq.find(fun i -> i.Id = m.MessageText)
                            let id = SyndicationItemTools.GenerateId item.Title.Text item.PublishDate
                            let! status = memoryClient.GetDocumentStatusAsync id

                            match status with
                            | s when isNull s ->
                                let! completion = processItem' item
                                let memoryRecord = unpackResponse item completion id

                                try
                                    let! docId = importer' memoryRecord
                                    let! _ = queueClient.DeleteMessageAsync(m.MessageId, m.PopReceipt, cancellationToken)
                                    logger.LogInformation("Import requested for {0}", docId)
                                with ex ->
                                    logger.LogError(ex, "Failed to import memory record {0}", m.MessageText)
                            | s when s.Completed ->
                                logger.LogInformation ("Document {0} already imported", id)
                            | s when s.RemainingSteps.Count > 0 ->
                                logger.LogInformation ("Document {0} already in progress, {1} steps left", id, s.RemainingSteps.Count)
                            | s when s.Failed ->
                                logger.LogInformation ("Document {0} failed to import, ignoring", id)
                            | _ ->
                                let! completion = processItem' item
                                let memoryRecord = unpackResponse item completion id

                                try
                                    let! docId = importer' memoryRecord
                                    let! _ = queueClient.DeleteMessageAsync(m.MessageId, m.PopReceipt, cancellationToken)
                                    logger.LogInformation("Import requested for {0}", docId)
                                with ex ->
                                    logger.LogError(ex, "Failed to import memory record {0}", m.MessageText)
                        })
                    |> Task.WhenAll

                logger.LogInformation "Waiting for next load request"

                // Sleep for a bit so we don't hammer the queue
                do! Task.Delay(TimeSpan.FromMinutes 1.0, cancellationToken)
        }