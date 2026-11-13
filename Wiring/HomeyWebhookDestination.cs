using PushToMute.Logic;

namespace PushToMute.Wiring;

internal sealed class HomeyWebhookDestination : IDiscordEventDestination, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly Action<string> _status;

    public HomeyWebhookDestination(string homeyBaseUrl, Action<string> status)
    {
        _baseUri = new Uri(homeyBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromSeconds(5);
        _status = status;
    }

    public async Task DeliverAsync(
        DiscordVoiceEvent discordEvent,
        CancellationToken cancellationToken)
    {
        string eventName = GetEventName(discordEvent.Type);
        string tag = discordEvent.UserId;
        string requestPath = BuildRequestPath(eventName, tag);
        Uri requestUri = new(_baseUri, requestPath);

        _status($"Homey webhook (dry run): {requestUri}");

        // Enable this when we are ready to send the real HTTP request:
        // using HttpResponseMessage response = await _client.GetAsync(
        //     requestUri,
        //     cancellationToken);
        // response.EnsureSuccessStatusCode();

        await Task.CompletedTask;
    }

    private static string BuildRequestPath(string eventName, string tag)
    {
        string encodedEvent = Uri.EscapeDataString(eventName);
        string encodedTag = Uri.EscapeDataString(tag);

        string requestPath = $"webhook?event={encodedEvent}&tag={encodedTag}";
        return requestPath;
    }

    private static string GetEventName(DiscordVoiceEventType type)
    {
        switch (type)
        {
            case DiscordVoiceEventType.Joined:
                return "discord_voice_joined";
            case DiscordVoiceEventType.Left:
                return "discord_voice_left";
            case DiscordVoiceEventType.ChangedChannel:
                return "discord_voice_moved";
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
