using HanselminutesBot.Frontend.Components;
using HanselminutesBot.ServiceDefaults;
using Microsoft.KernelMemory;

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

builder.AddAzureQueueService(ServiceConstants.QueueServiceName);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
