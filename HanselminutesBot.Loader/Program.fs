open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.KernelMemory
open System
open System.Net.Http
open Azure.AI.OpenAI
open Azure
open HanselminutesBot.ServiceDefaults

let builder = Host.CreateApplicationBuilder()

builder
    .Services
    .AddHttpClient(fun client -> client.BaseAddress <- Uri "http://memory")
    .AddStandardResilienceHandler(fun opts -> opts.TotalRequestTimeout.Timeout <- TimeSpan.FromMinutes 5)
    |> ignore

builder.Services.AddSingleton<IKernelMemory>(fun (sp: IServiceProvider) ->
    let httpClient = sp.GetRequiredService<HttpClient>()
    MemoryWebClient("http://memory", httpClient) :> IKernelMemory) |> ignore

builder.Services.AddSingleton<OpenAIClient>(fun (_) ->
    let configuration = builder.Configuration
    let endpoint =
        match configuration.["OpenAI:Endpoint"] with
        | null -> failwith "OpenAI:Endpoint not found in configuration"
        | value -> value

    let key =
        match configuration.["OpenAI:Key"] with
        | null -> failwith "OpenAI:Key not found in configuration"
        | value -> value

    OpenAIClient(Uri endpoint, AzureKeyCredential key)) |> ignore

builder.Services.AddHostedService<Worker>() |> ignore
builder.AddServiceDefaults() |> ignore

builder.AddAzureQueueService ServiceConstants.QueueServiceName

let host = builder.Build()
host.Run()
