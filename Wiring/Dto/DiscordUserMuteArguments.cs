namespace PushToMute.Wiring;

internal sealed class DiscordUserMuteArguments
{
    public string user_id { get; init; } = string.Empty;
    public bool mute { get; init; }
}
