
using System.Text.Json;
using PushToMute.Logic;

namespace PushToMute.Wiring;

internal static class DiscordIpcVoiceEventAdapter
{
    public static bool TryCreateUserJoinedEvent(
        DiscordIpcEvent ipcEvent,
        out DiscordVoiceEvent? voiceEvent)
    {
        voiceEvent = null;

        if (ipcEvent.EventName != "VOICE_STATE_CREATE")
        {
            return false;
        }

        JsonElement data = ipcEvent.Data;

        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? userId = ReadUserId(data);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        string? userName = ReadUserName(data);
        string? nickname = ReadNickname(data);

        string guildId = ReadString(data, "guild_id")
            ?? string.Empty;

        string? channelId = ReadString(data, "channel_id");

        voiceEvent = new DiscordVoiceEvent(
            DiscordVoiceEventType.Joined,
            guildId,
            userId,
            userName,
            nickname,
            null,
            channelId,
            DateTimeOffset.UtcNow);

        return true;
    }

    private static string? ReadUserId(JsonElement data)
    {
        string? userId = ReadString(data, "user_id");

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        if (!data.TryGetProperty("user", out JsonElement user))
        {
            return null;
        }

        return ReadString(user, "id");
    }

    private static string? ReadUserName(JsonElement data)
    {
        if (!data.TryGetProperty("user", out JsonElement user))
        {
            return null;
        }

        return ReadString(user, "username");
    }

    private static string? ReadNickname(JsonElement data)
    {
        return ReadString(data, "nick");
    }

    private static string? ReadString(
        JsonElement value,
        string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!value.TryGetProperty(
                propertyName,
                out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}
