using System.Text.Json;

namespace PushToMute.Wiring;

// Implements local voice operations, independent of IPC framing and OAuth.
internal sealed class DiscordVoiceCommandService
{
    private readonly Func<string, object, CancellationToken, Task<JsonElement>> _sendCommand;
    private readonly Func<bool> _isAuthorized;

    public DiscordVoiceCommandService(
        Func<string, object, CancellationToken, Task<JsonElement>> sendCommand,
        Func<bool> isAuthorized)
    {
        _sendCommand = sendCommand;
        _isAuthorized = isAuthorized;
    }

    // Changes only the local mute state, not a server-wide mute.
    public async Task<bool> SetUserMuteAsync(string userId, bool muted, CancellationToken ct)
    {
        EnsureUserId(userId);
        EnsureAuthorized();

        DiscordUserMuteArguments arguments = BuildUserMuteArguments(userId, muted);
        JsonElement reply = await _sendCommand("SET_USER_VOICE_SETTINGS", arguments, ct);
        JsonElement data = reply.GetProperty("data");

        if (!data.TryGetProperty("mute", out JsonElement muteState))
        {
            throw new InvalidDataException("Discord returned no mute state.");
        }

        if (muteState.ValueKind != JsonValueKind.True && muteState.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException("Discord returned no mute state.");
        }

        return muteState.GetBoolean();
    }

    private static DiscordUserMuteArguments BuildUserMuteArguments(string userId, bool muted)
    {
        DiscordUserMuteArguments arguments = new()
        {
            user_id = userId,
            mute = muted
        };

        return arguments;
    }

    public async Task<bool> ToggleUserMuteAsync(string userId, CancellationToken ct)
    {
        EnsureUserId(userId);
        EnsureAuthorized();

        JsonElement channel = await _sendCommand(
            "GET_SELECTED_VOICE_CHANNEL",
            new DiscordEmptyArguments(),
            ct);

        JsonElement data = channel.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException("Join a Discord voice channel first.");
        }

        if (!data.TryGetProperty("voice_states", out JsonElement states) ||
            states.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Discord did not provide voice states for the selected channel.");
        }

        foreach (JsonElement member in states.EnumerateArray())
        {
            if (!IsTargetUser(member, userId))
            {
                continue;
            }

            bool currentlyMuted = GetMemberMuteState(member);
            return await SetUserMuteAsync(userId, !currentlyMuted, ct);
        }

        throw new InvalidOperationException("Target user is not in your currently selected voice channel.");
    }

    private static bool IsTargetUser(JsonElement member, string userId)
    {
        if (!member.TryGetProperty("user", out JsonElement user))
        {
            return false;
        }

        if (!user.TryGetProperty("id", out JsonElement id))
        {
            return false;
        }

        return id.GetString() == userId;
    }

    private static bool GetMemberMuteState(JsonElement member)
    {
        if (!member.TryGetProperty("mute", out JsonElement mute))
        {
            throw new InvalidDataException("Discord did not provide this user's local mute state.");
        }

        if (mute.ValueKind != JsonValueKind.True && mute.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException("Discord did not provide this user's local mute state.");
        }

        return mute.GetBoolean();
    }

    private void EnsureAuthorized()
    {
        if (!_isAuthorized())
        {
            throw new InvalidOperationException("Authorize Discord first.");
        }
    }

    private static void EnsureUserId(string userId)
    {
        bool invalidLength = string.IsNullOrWhiteSpace(userId) || userId.Length < 17 || userId.Length > 20;
        bool nonNumeric = !string.IsNullOrWhiteSpace(userId) && !userId.All(char.IsAsciiDigit);

        if (invalidLength || nonNumeric)
        {
            throw new ArgumentException("Discord User ID must be a 17–20 digit snowflake.");
        }
    }

}
