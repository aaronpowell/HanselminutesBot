using Azure.Storage.Queues;
using HanselminutesBot.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.KernelMemory;
using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Xml;

namespace HanselminutesBot.Frontend.Components.Pages;

public partial class Management
{
    [Inject]
    public required IKernelMemory Memory { get; set; }

    [Inject]
    public required QueueServiceClient QueueServiceClient { get; set; }

    private record PodcastItem(string Title, string Id, DateTimeOffset PublishDate, DataPipelineStatus? Status);

    private List<PodcastItem> Episodes = [];

    private HashSet<PodcastItem> ItemsToIndex = [];

    private bool FetchingStatuses = false;

    protected override Task OnInitializedAsync()
    {
        SyndicationFeed feed = PodcastSource.GetFeed();

        foreach (SyndicationItem? item in feed.Items)
        {
            Episodes.Add(new PodcastItem(item.Title.Text, item.Id, item.PublishDate, null));
        }

        return Task.CompletedTask;
    }

    private async Task IndexDocuments()
    {
        var queueClient = QueueServiceClient.GetQueueClient(ServiceConstants.BuildIndexQueueServiceName);

        await queueClient.CreateIfNotExistsAsync();

        foreach (PodcastItem item in ItemsToIndex)
        {
            await queueClient.SendMessageAsync(item.Id);
        }
    }

    private async Task RetrieveStatuses()
    {
        FetchingStatuses = true;
        List<PodcastItem> updatedEps = [];
        foreach (PodcastItem item in Episodes)
        {
            string id = SyndicationItemTools.GenerateId(item.Title, item.PublishDate);
            DataPipelineStatus? status = await Memory.GetDocumentStatusAsync(id);

            PodcastItem updatedItem = item with { Status = status };
            updatedEps.Add(updatedItem);
        }
        Episodes = updatedEps;
        FetchingStatuses = false;
    }
}