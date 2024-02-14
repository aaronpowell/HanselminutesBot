open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.KernelMemory
open System
open System.Net.Http
open HanselminutesBot.Shared

let builder = Host.CreateApplicationBuilder()

builder
    .Services
    .AddHttpClient(fun client -> client.BaseAddress <- Uri "http://memory")
    .AddStandardResilienceHandler(fun opts -> opts.TotalRequestTimeout.Timeout <- TimeSpan.FromMinutes 5)
    |> ignore

builder.Services.AddSingleton<IKernelMemory>(fun (sp: IServiceProvider) ->
    let httpClient = sp.GetRequiredService<HttpClient>()
    MemoryWebClient("http://memory", httpClient) :> IKernelMemory) |> ignore

builder.AddAzureOpenAI("AzureOpenAI", fun settings -> settings.Tracing <- true)

builder.Services.AddHostedService<Worker>() |> ignore
builder.AddServiceDefaults() |> ignore

builder.AddAzureQueueService ServiceConstants.BuildIndexQueueServiceName

let host = builder.Build()
host.Run()
