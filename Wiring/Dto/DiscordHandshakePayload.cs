namespace PushToMute.Wiring;

internal sealed class DiscordHandshakePayload
{
    public int v { get; init; }
    public string client_id { get; init; } = string.Empty;
}
