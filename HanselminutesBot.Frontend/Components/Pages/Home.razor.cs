using Microsoft.AspNetCore.Components;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.KernelMemory;
using MudBlazor;

namespace HanselminutesBot.Frontend.Components.Pages;

public partial class Home
{
    [Inject]
    public required IConfiguration Configuration { get; set; }

    private string UserInput = "";

    private string? answer = null;
    private IEnumerable<Source>? sources = null;

    private MudChip[] selectedSpeakers = [];
    private MudChip[] selectedTopics = [];

    private bool loading = false;

    private string? answerSpokenFile = null;
    private bool playAudio = false;

    private async Task DoAsk()
    {
        if (string.IsNullOrWhiteSpace(UserInput))
        {
            return;
        }

        answer = null;
        sources = null;
        loading = true;

        answerSpokenFile = null;
        playAudio = false;

        MemoryFilter filter = [];

        foreach (var speaker in selectedSpeakers)
        {
            filter.Add("speaker", speaker.Text);
        }

        foreach (var topic in selectedTopics)
        {
            filter.Add("topic", topic.Text);
        }

        var response = await Memory.AskAsync(UserInput, filter: filter, minRelevance: 0.8);

        answer = response.Result;

        sources = response.RelevantSources.Select(s =>
        {
            List<Citation.Partition> partitions = s.Partitions;
            var title = partitions.First(p => p.Tags.ContainsKey("title")).Tags["title"].First();
            var uri = partitions.First(p => p.Tags.ContainsKey("uri")).Tags["uri"].First();
            return new Source(title!, uri!, partitions.Where(p => p.Tags.ContainsKey("speaker")).SelectMany(p => p.Tags["speaker"]), partitions.Where(p => p.Tags.ContainsKey("topic")).SelectMany(p => p.Tags["topic"]));
        });
        loading = false;

        await Speak();
    }

    private async Task Speak()
    {
        if (string.IsNullOrEmpty(answer))
        {
            return;
        }

        string subscriptionKey = Configuration["Speech:Key"] ?? throw new ArgumentException("Speech:Key is not set.");
        string subscriptionRegion = Configuration["Speech:Region"] ?? throw new ArgumentException("Speech:Region is not set.");
        string endpointId = Configuration["Speech:EndpointId"] ?? throw new ArgumentException("Speech:EndpointId is not set.");

        SpeechConfig config = SpeechConfig.FromSubscription(subscriptionKey, subscriptionRegion);
        config.EndpointId = endpointId;
        config.SpeechSynthesisVoiceName = Configuration["Speech:VoiceName"] ?? throw new ArgumentException("Speech:VoiceName is not set.");
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz160KBitRateMonoMp3);

        string fileName = Path.Join("tts", Guid.NewGuid().ToString() + ".wav");

        // using the default speaker as audio output.
        using var fileOutput = AudioConfig.FromWavFileOutput(fileName);
        using var synthesizer = new SpeechSynthesizer(config, fileOutput);
        using var result = await synthesizer.SpeakTextAsync(answer);
        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
        {
            Console.WriteLine($"Speech synthesized for text [{answer}], and the audio was saved to [{fileName}]");
            answerSpokenFile = fileName;
        }
        else if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
            Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

            if (cancellation.Reason == CancellationReason.Error)
            {
                Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                Console.WriteLine($"CANCELED: Did you update the subscription info?");
            }
        }
    }

    private record Source(string Title, string Uri, IEnumerable<string?> Speakers, IEnumerable<string?> Topics);
}