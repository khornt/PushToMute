namespace PushToMute.Wiring;

internal sealed class DiscordAuthorizeArguments
{
    public string client_id { get; init; } = string.Empty;
    public string[] scopes { get; init; } = [];
}
