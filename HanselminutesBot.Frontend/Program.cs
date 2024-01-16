using HanselminutesBot.Frontend.Components;
using HanselminutesBot.ServiceDefaults;
using Microsoft.KernelMemory;
using MudBlazor.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
.AddHttpClient("memory", client =>
{
    client.BaseAddress = new("http://memory");
});

builder.Services.AddTransient<IKernelMemory>(sp =>
{
    HttpClient httpClient = sp.GetRequiredService<HttpClient>();
    return new MemoryWebClient("http://memory", httpClient);
});

builder.AddAzureQueueService(ServiceConstants.BuildIndexQueueServiceName);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.MapGet("/tts/{filename}.wav", (string filename) =>
{
    return Results.Stream(File.OpenRead(Path.Join("tts", filename + ".wav")));
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
