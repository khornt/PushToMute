using System.Text.Json;
using System.Threading.Channels;

namespace PushToMute.Wiring;

// The subscription worker must not await RPC responses on the receive thread.
internal sealed class DiscordVoiceSubscriptionService
{
    private readonly Func<string, object, CancellationToken, string?, Task<JsonElement>> _sendCommand;
    private readonly Func<bool> _isAvailable;
    private readonly Action<string> _status;
    private Channel<VoiceChannelSelection>? _voiceSelections;
    private Task? _subscriptionWorker;
    private string? _subscribedVoiceChannelId;

    public DiscordVoiceSubscriptionService(
        Func<string, object, CancellationToken, string?, Task<JsonElement>> sendCommand,
        Func<bool> isAvailable,
        Action<string> status)
    {
        _sendCommand = sendCommand;
        _isAvailable = isAvailable;
        _status = status;
    }

    public void StartSession(CancellationToken lifetimeToken)
    {
        UnboundedChannelOptions options = new()
        {
            SingleReader = true,
            SingleWriter = false
        };

        _voiceSelections = Channel.CreateUnbounded<VoiceChannelSelection>(options);
        _subscriptionWorker = ProcessVoiceSelectionsAsync(lifetimeToken);
    }

    public async Task StopSessionAsync()
    {
        if (_voiceSelections is not null)
        {
            _voiceSelections.Writer.TryComplete();
        }

        if (_subscriptionWorker is not null)
        {
            await _subscriptionWorker;
        }

        _voiceSelections = null;
        _subscriptionWorker = null;
        _subscribedVoiceChannelId = null;
    }

    public void OnVoiceChannelSelected(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? channelId = GetOptionalString(data, "channel_id");
        VoiceChannelSelection selection = new(channelId);
        QueueVoiceChannelSelection(selection);
    }

    public async Task StartVoiceEventSubscriptionsAsync(CancellationToken cancellationToken)
    {
        // Subscription failures must not invalidate a working OAuth session.
        try
        {
            DiscordEmptyArguments arguments = new();
            await SendSubscriptionCommandAsync(
                "SUBSCRIBE", "VOICE_CHANNEL_SELECT", arguments, cancellationToken);

            _status("Subscribed to VOICE_CHANNEL_SELECT.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _status("Could not subscribe to voice channel selections: " + exception.Message);
            return;
        }

        try
        {
            DiscordEmptyArguments arguments = new();
            JsonElement reply = await _sendCommand(
                "GET_SELECTED_VOICE_CHANNEL", arguments, cancellationToken, null);

            VoiceChannelSelection selection = ReadSelectedChannel(reply);
            QueueVoiceChannelSelection(selection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _status("Could not read current voice channel: " + exception.Message);
        }
    }

    private static VoiceChannelSelection ReadSelectedChannel(JsonElement response)
    {
        if (!response.TryGetProperty("data", out JsonElement data) ||
            data.ValueKind == JsonValueKind.Null)
        {
            return new VoiceChannelSelection(null);
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Unexpected selected voice channel response.");
        }

        string? channelId = GetOptionalString(data, "id");
        return new VoiceChannelSelection(channelId);
    }

    private static string? GetOptionalString(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private void QueueVoiceChannelSelection(VoiceChannelSelection selection)
    {
        bool accepted = _voiceSelections is not null && _voiceSelections.Writer.TryWrite(selection);

        if (!accepted)
        {
            _status("Could not queue Discord voice channel change.");
        }
    }

    // Runs independently of the IPC reader. Never await an RPC response on the reader.
    private async Task ProcessVoiceSelectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (VoiceChannelSelection selection in
                           _voiceSelections!.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await UpdateVoiceChannelSubscriptionsAsync(selection.ChannelId, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _status("Voice event subscription update failed: " + exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal disconnect.
        }
    }

    private async Task UpdateVoiceChannelSubscriptionsAsync(
        string? channelId,
        CancellationToken cancellationToken)
    {
        if (!_isAvailable())
        {
            return;
        }

        if (channelId == _subscribedVoiceChannelId)
        {
            return;
        }

        string? previousChannelId = _subscribedVoiceChannelId;
        if (previousChannelId is not null)
        {
            await ChangeVoiceStateSubscriptionsAsync("UNSUBSCRIBE", previousChannelId, cancellationToken);
            _subscribedVoiceChannelId = null;
        }

        if (channelId is null)
        {
            _status("Not in a voice channel; waiting for a selection.");
            return;
        }

        int subscriptions = await ChangeVoiceStateSubscriptionsAsync(
            "SUBSCRIBE", channelId, cancellationToken);

        if (subscriptions == 0)
        {
            _status($"No voice-state subscriptions accepted for channel {channelId}.");
            return;
        }

        _subscribedVoiceChannelId = channelId;
        _status($"Listening for voice-state events in channel {channelId} ({subscriptions}/3 subscribed).");
    }

    private async Task<int> ChangeVoiceStateSubscriptionsAsync(
        string command,
        string channelId,
        CancellationToken cancellationToken)
    {
        DiscordVoiceChannelArguments arguments = new()
        {
            channel_id = channelId
        };

        string[] eventNames =
        [
            "VOICE_STATE_CREATE",
            "VOICE_STATE_DELETE",
            "VOICE_STATE_UPDATE"
        ];

        int successful = 0;
        foreach (string eventName in eventNames)
        {
            try
            {
                await SendSubscriptionCommandAsync(
                    command, eventName, arguments, cancellationToken);
                successful++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _status($"{command} {eventName} failed for {channelId}: {exception.Message}");
            }
        }

        return successful;
    }

    private async Task<JsonElement> SendSubscriptionCommandAsync(
        string command,
        string eventName,
        object arguments,
        CancellationToken cancellationToken)
    {
        JsonElement reply = await _sendCommand(
            command, arguments, cancellationToken, eventName);
        return reply;
    }

}
