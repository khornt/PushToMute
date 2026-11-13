using System.Text.Json.Serialization;

namespace PushToMute.Wiring;

internal sealed class DiscordCommandPayload
{
    public string cmd { get; init; } = string.Empty;
    public object args { get; init; } = new DiscordEmptyArguments();
    public string nonce { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? evt { get; init; }
}
