using HanselminutesBot.ServiceDefaults;
using Microsoft.Extensions.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

string endpoint = builder.Configuration["OpenAI:Endpoint"] ?? throw new ArgumentException("OpenAI:Endpoint must be provided in config.");
string key = builder.Configuration["OpenAI:Key"] ?? throw new ArgumentException("OpenAI:Key must be provided in config.");
string chatDeployment = builder.Configuration["OpenAI:ChatDeployment"] ?? throw new ArgumentException("OpenAI:ChatDeployment must be provided in config.");
string embeddingsDeployment = builder.Configuration["OpenAI:EmbeddingsDeployment"] ?? throw new ArgumentException("OpenAI:EmbeddingsDeployment must be provided in config.");

IResourceBuilder<AzureStorageResource> storage = builder.AddAzureStorage("hanselminutesbot");

if (builder.Environment.IsDevelopment())
{
    storage.UseEmulator(blobPort: builder.Configuration["Aspire:Azure:Storage:Blobs:Port"] switch
    {
        null => null,
        string port => int.Parse(port)
    },
    queuePort: builder.Configuration["Aspire:Azure:Storage:Queues:Port"] switch
    {
        null => null,
        string port => int.Parse(port)
    })
    .WithAnnotation(new VolumeMountAnnotation("./data/azurite", "/data", VolumeMountType.Bind));
}

IResourceBuilder<AzureBlobStorageResource> blob = storage
    .AddBlobs(ServiceConstants.BlobServiceName);

IResourceBuilder<AzureQueueStorageResource> buildIndexQueue = storage.AddQueues(ServiceConstants.BuildIndexQueueServiceName);
IResourceBuilder<AzureQueueStorageResource> memoryPipelineQueue = storage.AddQueues(ServiceConstants.MemoryPipelineQueueServiceName);

IResourceBuilder<PostgresContainerResource> postgresContainerDefinition = builder
    .AddPostgresContainer("db", port: builder.Configuration["Aspire:Postgres:Port"] switch
    {
        null => null,
        string port => int.Parse(port)
    }, password: builder.Configuration["Aspire:Postgres:Password"])
    .WithEnvironment("POSTGRES_DB", "podcasts")
    // Use a custom container image that has pgvector installed
    .WithAnnotation(new ContainerImageAnnotation { Image = "ankane/pgvector", Tag = "latest" })
    // Mount the database scripts into the container that will configure pgvector
    .WithVolumeMount("./database", "/docker-entrypoint-initdb.d", VolumeMountType.Bind)
    // Enable logging of all SQL statements
    .WithArgs("-c", "log_statement=all");

if (builder.Environment.IsDevelopment())
{
    postgresContainerDefinition
    // Mount the Postgres data directory into the container so that the database is persisted
    .WithVolumeMount("./data/postgres", "/var/lib/postgresql/data", VolumeMountType.Bind);
}

IResourceBuilder<PostgresDatabaseResource> postgres = postgresContainerDefinition
    .AddDatabase("podcasts");

IResourceBuilder<ProjectResource> memory = builder.AddProject<HanselminutesBot_Memory>("memory")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(blob)
    .WithReference(postgres)
    .WithReference(memoryPipelineQueue);

builder.AddProject<HanselminutesBot_Loader>("loader")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(memory)
    .WithReference(buildIndexQueue);

builder.AddProject<HanselminutesBot_Frontend>("frontend")
    .WithReference(memory)
    .WithReference(buildIndexQueue);

builder.Build().Run();
