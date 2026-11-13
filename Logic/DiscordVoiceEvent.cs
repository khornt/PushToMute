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
    string? PreviousChannelId,
    string? ChannelId,
    DateTimeOffset OccurredAtUtc);

internal interface IDiscordEventDestination
{
    Task DeliverAsync(DiscordVoiceEvent discordEvent, CancellationToken cancellationToken);
}
