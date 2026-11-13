using PushToMute.Logic;

namespace PushToMute.Wiring;

internal sealed class HomeyWebhookDestination : IDiscordEventDestination, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    public HomeyWebhookDestination(string homeyBaseUrl)
    {
        _baseUri = new Uri(homeyBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task DeliverAsync(DiscordVoiceEvent discordEvent, CancellationToken cancellationToken)
    {
        string eventName = GetEventName(discordEvent.Type);
        string tag = discordEvent.UserId;
        string requestPath = "webhook?event=" + Uri.EscapeDataString(eventName)
            + "&tag=" + Uri.EscapeDataString(tag);

        Uri requestUri = new(_baseUri, requestPath);
        using HttpResponseMessage response = await _client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
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
