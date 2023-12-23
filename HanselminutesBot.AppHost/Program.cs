using Projects;

var builder = DistributedApplication.CreateBuilder(args);

string endpoint = builder.Configuration["OpenAI:Endpoint"] ?? throw new ArgumentException("OpenAI:Endpoint must be provided in config.");
string key = builder.Configuration["OpenAI:Key"] ?? throw new ArgumentException("OpenAI:Key must be provided in config.");
string chatDeployment = builder.Configuration["OpenAI:ChatDeployment"] ?? throw new ArgumentException("OpenAI:ChatDeployment must be provided in config.");
string embeddingsDeployment = builder.Configuration["OpenAI:EmbeddingsDeployment"] ?? throw new ArgumentException("OpenAI:EmbeddingsDeployment must be provided in config.");

IResourceBuilder<PostgresDatabaseResource> postgres = builder.AddPostgresContainer("db")
    .WithEnvironment("POSTGRES_DB", "podcasts")
    .WithAnnotation(new ContainerImageAnnotation { Image = "ankane/pgvector", Tag = "latest" })
    .WithVolumeMount("./database", "/docker-entrypoint-initdb.d", VolumeMountType.Bind)
    .AddDatabase("podcasts");

IResourceBuilder<ProjectResource> memory = builder.AddProject<HanselminutesBot_Memory>("memory")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(postgres);

builder.AddProject<HanselminutesBot_Loader>("loader")
    .WithEnvironment("OpenAI__Endpoint", endpoint)
    .WithEnvironment("OpenAI__Key", key)
    .WithEnvironment("OpenAI__ChatDeployment", chatDeployment)
    .WithEnvironment("OpenAI__EmbeddingsDeployment", embeddingsDeployment)
    .WithReference(memory);

builder.Build().Run();
