open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.KernelMemory
open Microsoft.KernelMemory.Postgres
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.KernelMemory.WebService
open Microsoft.KernelMemory.ContentStorage.AzureBlobs

let builder = WebApplication.CreateBuilder()

builder.AddServiceDefaults() |> ignore

let embeddingsConfig = AzureOpenAIConfig()
embeddingsConfig.APIKey <- builder.Configuration.["OpenAI:Key"]
embeddingsConfig.APIType <- AzureOpenAIConfig.APITypes.EmbeddingGeneration
embeddingsConfig.Auth <- AzureOpenAIConfig.AuthTypes.APIKey
embeddingsConfig.Endpoint <- builder.Configuration.["OpenAI:Endpoint"]
embeddingsConfig.Deployment <- builder.Configuration.["OpenAI:EmbeddingsDeployment"]

let textGenConfig = AzureOpenAIConfig()
textGenConfig.APIKey <- builder.Configuration.["OpenAI:Key"]
textGenConfig.APIType <- AzureOpenAIConfig.APITypes.ChatCompletion
textGenConfig.Auth <- AzureOpenAIConfig.AuthTypes.APIKey
textGenConfig.Endpoint <- builder.Configuration.["OpenAI:Endpoint"]
textGenConfig.Deployment <- builder.Configuration.["OpenAI:ChatDeployment"]

let postgresConfig = PostgresConfig()
postgresConfig.ConnectionString <- builder.Configuration.["ConnectionStrings:podcasts"]
postgresConfig.TableNamePrefix <- "km_"

let storageConfig = AzureBlobsConfig()
storageConfig.ConnectionString <- builder.Configuration.["ConnectionStrings:kernelmemory"]
storageConfig.Container <- "kernelmemory"
storageConfig.Auth <- AzureBlobsConfig.AuthTypes.ConnectionString

let memory =
    KernelMemoryBuilder(builder.Services)
        .WithPostgres(postgresConfig)
        .WithAzureBlobsStorage(storageConfig)
        //.WithSimpleFileStorage("kernel-memory")
        .WithAzureOpenAITextEmbeddingGeneration(embeddingsConfig)
        .WithAzureOpenAITextGeneration(textGenConfig)
        .Build()

builder.Services.AddSingleton memory |> ignore

let app = builder.Build()

// POST endpoint for KernelMemory so the client library can upload documents
type UploadRequest = Func<HttpRequest, IKernelMemory, ILogger<IKernelMemory>, CancellationToken, Task<IResult>>
app.MapPost(Constants.HttpUploadEndpoint, UploadRequest(fun request memory logger ct ->
    task {
        logger.LogTrace "New upload HTTP request"

        let! (input, isValid, errMessage) = HttpDocumentUploadRequest.BindHttpRequestAsync(request, ct)

        if not isValid then
            return TypedResults.BadRequest(errMessage) :> IResult
        else
            let! documentId = memory.ImportDocumentAsync(input.ToDocumentUploadRequest(), ct)

            let url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal)

            let uploadAccepted = UploadAccepted()
            uploadAccepted.DocumentId <- documentId
            uploadAccepted.Index <- input.Index
            uploadAccepted.Message <- "Document upload completed, ingestion pipeline started"
            return TypedResults.Accepted(url, uploadAccepted) :> IResult
    })) |> ignore

type GetIndexRequest = Func<HttpRequest, IKernelMemory, ILogger<IKernelMemory>, CancellationToken, Task<IResult>>
app.MapGet(Constants.HttpIndexesEndpoint, GetIndexRequest(fun _ memory logger ct ->
    task {
        logger.LogTrace "New index list HTTP request";

        let result = IndexCollection();
        let! list = memory.ListIndexesAsync ct;

        for index in list do
            result.Results.Add index |> ignore

        return TypedResults.Ok result :> IResult
    })) |> ignore

type AskRequest = Func<MemoryQuery, IKernelMemory, ILogger<IKernelMemory>, CancellationToken, Task<IResult>>
app.MapPost(Constants.HttpAskEndpoint, AskRequest(fun query memory logger ct ->
    task {
        logger.LogTrace "New search request"

        let! answer = memory.AskAsync(query.Question, query.Index, null, query.Filters, query.MinRelevance, ct)

        return TypedResults.Ok answer :> IResult
    })) |> ignore

app.MapDefaultEndpoints() |> ignore

app.Run()
