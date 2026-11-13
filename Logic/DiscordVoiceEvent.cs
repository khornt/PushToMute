namespace PushToMute.Logic;

internal enum DiscordVoiceEventType
{
    Joined,
    Left,
    ChangedChannel
}

internal sealed record DiscordVoiceEvent(
    DiscordVoiceEventType Type,
    string GuildId,
    string UserId,
    string? UserName,
    string? Nickname,
    string? PreviousChannelId,
    string? ChannelId,
    DateTimeOffset OccurredAtUtc)
{
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Nickname))
            {
                return Nickname;
            }

            if (!string.IsNullOrWhiteSpace(UserName))
            {
                return UserName;
            }

            return UserId;
        }
    }
}
internal interface IDiscordEventDestination
{
    Task DeliverAsync(DiscordVoiceEvent discordEvent, CancellationToken cancellationToken);
}
