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
open HanselminutesBot.ServiceDefaults
open Microsoft.KernelMemory.Pipeline.Queue.AzureQueues

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
storageConfig.ConnectionString <- builder.Configuration.[$"ConnectionStrings:{ServiceConstants.BlobServiceName}"]
storageConfig.Container <- "kernelmemory"
storageConfig.Auth <- AzureBlobsConfig.AuthTypes.ConnectionString

let queueConfig = AzureQueueConfig()
queueConfig.ConnectionString <- builder.Configuration.[$"ConnectionStrings:{ServiceConstants.MemoryPipelineQueueServiceName}"]
queueConfig.Auth <- AzureQueueConfig.AuthTypes.ConnectionString

let memory =
    KernelMemoryBuilder(builder.Services)
        .WithPostgres(postgresConfig)
        .WithAzureBlobsStorage(storageConfig)
        .WithAzureOpenAITextEmbeddingGeneration(embeddingsConfig)
        .WithAzureOpenAITextGeneration(textGenConfig)
        .WithAzurequeuePipeline(queueConfig)
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

type DocumentStatusRequest = Func<string, string, IKernelMemory, ILogger<IKernelMemory>, CancellationToken, Task<IResult>>
app.MapGet(Constants.HttpUploadStatusEndpoint, DocumentStatusRequest(fun index documentId memory logger ct ->
    task {
        logger.LogTrace "New document status request"

        let index' = IndexExtensions.CleanName index;

        if String.IsNullOrEmpty documentId then
            return TypedResults.BadRequest "Document ID is required" :> IResult
        else
            let! status = memory.GetDocumentStatusAsync(index', documentId, ct)

            return match status with
                   | null -> TypedResults.NotFound "Document not found" :> IResult
                   | pipeline when pipeline.Empty -> TypedResults.NotFound "Empty pipeline" :> IResult
                   | _ -> TypedResults.Ok status :> IResult
    })) |> ignore

app.MapDefaultEndpoints() |> ignore

app.Run()
