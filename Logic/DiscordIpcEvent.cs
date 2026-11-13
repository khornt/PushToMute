using System.Text.Json;

namespace PushToMute.Logic;

public sealed record DiscordIpcEvent
{
    public required string EventName { get; init; }

    public required JsonElement Data { get; init; }
}