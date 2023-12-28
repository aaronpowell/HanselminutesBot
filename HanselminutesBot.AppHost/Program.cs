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
    storage.UseEmulator();
}

IResourceBuilder<AzureBlobStorageResource> blob = storage
    .AddBlobs("kernelmemory");

IResourceBuilder<PostgresDatabaseResource> postgres = builder.AddPostgresContainer("db")
    .WithEnvironment("POSTGRES_DB", "podcasts")
    // Use a custom container image that has pgvector installed
    .WithAnnotation(new ContainerImageAnnotation { Image = "ankane/pgvector", Tag = "latest" })
    // Mount the database scripts into the container that will configure pgvector
    .WithVolumeMount("./database", "/docker-entrypoint-initdb.d", VolumeMountType.Bind)
    .AddDatabase("podcasts");

IResourceBuilder<ProjectResource> memory = builder.AddProject<HanselminutesBot_Memory>("memory")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(blob)
    .WithReference(postgres);

builder.AddProject<HanselminutesBot_Loader>("loader")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(memory);

builder.Build().Run();
