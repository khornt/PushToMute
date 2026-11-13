using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using PushToMute.Logic;

namespace PushToMute.Wiring;

internal sealed class DiscordRpcClient : IDiscordVoiceGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly DiscordAuthenticationService _authentication;
    private readonly DiscordVoiceSubscriptionService _subscriptions;
    private readonly DiscordVoiceCommandService _voiceCommands;

    private Socket? _socket;
    private NetworkStream? _stream;
    private CancellationTokenSource? _lifetime;
    private Task? _reader;
    private string? _clientId;
    public bool Connected { get; private set; }
    public bool Authorized { get; private set; }

    public event Action<string>? Message;
    public event Action? StatusChanged;
    
    public event Action<DiscordIpcEvent>? IpcEventReceived;

    public DiscordRpcClient(DiscordCredentialsStore credentialsStore)
    {
        _authentication = new DiscordAuthenticationService(
            credentialsStore,
            SendAuthenticationCommandAsync,
            OnAuthenticatedAsync,
            ReportStatus);

        _subscriptions = new DiscordVoiceSubscriptionService(
            SendCommandAsync,
            IsAvailableForSubscriptions,
            ReportStatus);

        _voiceCommands = new DiscordVoiceCommandService(
            SendAuthenticationCommandAsync,
            IsAuthorized);
    }

    private void ReportStatus(string message)
    {
        Message?.Invoke(message);
    }

    private bool IsAuthorized()
    {
        return Authorized;
    }

    private bool IsAvailableForSubscriptions()
    {
        return Connected && Authorized;
    }

    private Task<JsonElement> SendAuthenticationCommandAsync(
        string command,
        object arguments,
        CancellationToken cancellationToken)
    {
        return SendCommandAsync(command, arguments, cancellationToken);
    }

    private async Task OnAuthenticatedAsync(string scopes, CancellationToken cancellationToken)
    {
        Authorized = true;
        StatusChanged?.Invoke();
        ReportStatus($"Discord authenticated. Granted scopes: {scopes}");
        await _subscriptions.StartVoiceEventSubscriptionsAsync(cancellationToken);
    }

    public Task<bool> TryRestoreAuthorizationAsync(CancellationToken cancellationToken)
    {
        string clientId = GetConnectedClientId();
        return _authentication.TryRestoreAuthorizationAsync(clientId, cancellationToken);
    }

    public Task AuthorizeAsync(
        string clientSecret,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        string clientId = GetConnectedClientId();
        return _authentication.AuthorizeAsync(clientId, clientSecret, redirectUri, cancellationToken);
    }

    private string GetConnectedClientId()
    {
        if (!Connected || _clientId is null)
        {
            throw new InvalidOperationException("Connect first.");
        }

        return _clientId;
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        if (Connected)
        {
            throw new InvalidOperationException("Already connected.");
        }

        if (string.IsNullOrWhiteSpace(clientId) || !clientId.All(char.IsDigit))
        {
            throw new ArgumentException("Application ID must contain only digits.");
        }

        string[] directories = GetCandidateSocketDirectories();
        Exception? lastException = null;

        foreach (string directory in directories)
        {
            for (int index = 0; index < 10; index++)
            {
                string path = Path.Combine(directory, $"discord-ipc-{index}");
                if (!File.Exists(path))
                {
                    continue;
                }

                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    NetworkStream stream = new(socket, ownsSocket: false);

                    DiscordHandshakePayload handshake = BuildHandshakePayload(clientId);
                    await DiscordIpcFraming.WriteFrameAsync(stream, 0, handshake, cancellationToken);

                    (int opcode, JsonElement packet) = await DiscordIpcFraming.ReadFrameAsync(stream, cancellationToken);
                    ValidateHandshakeResponse(opcode, packet);

                    _socket = socket;
                    _stream = stream;
                    _clientId = clientId;
                    _lifetime = new CancellationTokenSource();
                    Connected = true;
                    Authorized = false;

                    Message?.Invoke($"Discord READY via {path}. IPC connected; not authorized.");
                    StatusChanged?.Invoke();
                    _reader = ReadLoopAsync(_lifetime.Token);
                    _subscriptions.StartSession(_lifetime.Token);
                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastException = exception;
                    socket.Dispose();
                }
            }
        }

        throw new IOException("Could not connect to a Discord IPC socket.", lastException);
    }
    
    private static string[] GetCandidateSocketDirectories()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TEMP"),
            "/tmp"
        ];

        IEnumerable<string> populated = candidates
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => directory!);

        return populated.Distinct().ToArray();
    }

    private static DiscordHandshakePayload BuildHandshakePayload(string clientId)
    {
        DiscordHandshakePayload payload = new()
        {
            v = 1,
            client_id = clientId
        };

        return payload;
    }

    private static void ValidateHandshakeResponse(int opcode, JsonElement packet)
    {
        bool isReady = opcode == 1;
        if (isReady && packet.TryGetProperty("evt", out JsonElement evt))
        {
            isReady = evt.GetString() == "READY";
        }
        else
        {
            isReady = false;
        }

        if (!isReady)
        {
            throw new InvalidDataException("Discord did not return READY: " + packet);
        }
    }

    

    public Task<bool> SetUserMuteAsync(string userId, bool muted, CancellationToken ct)
    {
        return _voiceCommands.SetUserMuteAsync(userId, muted, ct);
    }

    public Task<bool> ToggleUserMuteAsync(string userId, CancellationToken ct)
    {
        return _voiceCommands.ToggleUserMuteAsync(userId, ct);
    }

    private async Task<JsonElement> SendCommandAsync(
        string command,
        object arguments,
        CancellationToken ct,
        string? eventName = null)
    {
        if (!Connected || _stream is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        string nonce = Guid.NewGuid().ToString("N");
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = completion;

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                DiscordCommandPayload payload = BuildCommandPayload(command, arguments, nonce, eventName);
                await DiscordIpcFraming.WriteFrameAsync(_stream, 1, payload, ct);
            }
            finally
            {
                _sendLock.Release();
            }

            JsonElement reply = await completion.Task.WaitAsync(ct);
            ThrowIfRpcError(command, reply);
            return reply;
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private static DiscordCommandPayload BuildCommandPayload(
        string command,
        object arguments,
        string nonce,
        string? eventName)
    {
        DiscordCommandPayload payload = new()
        {
            cmd = command,
            args = arguments,
            nonce = nonce,
            evt = eventName
        };

        return payload;
    }

    private static void ThrowIfRpcError(string command, JsonElement reply)
    {
        if (!reply.TryGetProperty("evt", out JsonElement evt))
        {
            return;
        }

        if (evt.GetString() != "ERROR")
        {
            return;
        }

        JsonElement error = reply;
        if (reply.TryGetProperty("data", out JsonElement data))
        {
            error = data;
        }

        throw new InvalidOperationException($"Discord {command} error: {error}");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                (int opcode, JsonElement packet) = await DiscordIpcFraming.ReadFrameAsync(_stream, ct);

                if (opcode == 2)
                {
                    Message?.Invoke("Discord closed the RPC connection.");
                    break;
                }

                if (opcode == 3)
                {
                    await SendPongAsync(packet, ct);
                    continue;
                }

                if (opcode != 1)
                {
                    continue;
                }

                ProcessRpcPacket(packet);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            Message?.Invoke("Discord IPC connection lost: " + exception.Message);
        }
        finally
        {
            Connected = false;
            Authorized = false;

            foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
            {
                pending.TrySetException(new IOException("Discord IPC disconnected."));
            }

            _pending.Clear();
            StatusChanged?.Invoke();
        }
    }

    private async Task SendPongAsync(JsonElement packet, CancellationToken ct)
    {
        if (_stream is null)
        {
            return;
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            await DiscordIpcFraming.WriteFrameAsync(_stream, 4, packet, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
    
    private void ProcessRpcPacket(JsonElement packet)
    {
        if (TryCompletePendingCommand(packet))
        {
            return;
        }

        if (TryHandleRpcError(packet))
        {
            return;
        }

        HandleDiscordEvent(packet);
    }
    
    private bool TryCompletePendingCommand(JsonElement packet)
    {
        if (!packet.TryGetProperty("nonce", out JsonElement nonceElement))
        {
            return false;
        }

        if (nonceElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? nonce = nonceElement.GetString();

        if (string.IsNullOrEmpty(nonce))
        {
            return false;
        }

        if (!_pending.TryRemove(
                nonce,
                out TaskCompletionSource<JsonElement>? pending))
        {
            return false;
        }

        pending.TrySetResult(packet.Clone());

        return true;
    }
    
    private bool TryHandleRpcError(JsonElement packet)
    {
        if (!packet.TryGetProperty("evt", out JsonElement eventElement))
        {
            return false;
        }

        if (eventElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? eventName = eventElement.GetString();

        if (eventName != "ERROR")
        {
            return false;
        }

        string errorMessage = packet.ToString();

        if (packet.TryGetProperty("data", out JsonElement dataElement))
        {
            errorMessage = dataElement.ToString();
        }

        Message?.Invoke($"Discord RPC event error: {errorMessage}");

        return true;
    }
    
    private void HandleDiscordEvent(JsonElement packet)
    {
        if (!packet.TryGetProperty("cmd", out JsonElement commandElement))
        {
            return;
        }

        if (commandElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? command = commandElement.GetString();

        if (command != "DISPATCH")
        {
            return;
        }

        if (!packet.TryGetProperty("evt", out JsonElement eventElement))
        {
            return;
        }

        if (eventElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? eventName = eventElement.GetString();

        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        if (!packet.TryGetProperty("data", out JsonElement dataElement))
        {
            return;
        }

        DiscordIpcEvent discordEvent = new()
        {
            EventName = eventName,
            Data = dataElement.Clone()
        };

        if (eventName == "VOICE_CHANNEL_SELECT" &&
            dataElement.ValueKind == JsonValueKind.Object)
        {
            _subscriptions.OnVoiceChannelSelected(dataElement);
        }

        IpcEventReceived?.Invoke(discordEvent);
    }

    public async Task DisconnectAsync()
    {
        _lifetime?.Cancel();
        _stream?.Dispose();
        _socket?.Dispose();

        if (_reader is not null)
        {
            try
            {
                await _reader;
            }
            catch (OperationCanceledException)
            {
                // Expected while disconnecting.
            }
        }

        await _subscriptions.StopSessionAsync();

        _lifetime?.Dispose();
        _lifetime = null;
        _stream = null;
        _socket = null;
        _reader = null;
        _clientId = null;
        Connected = false;
        Authorized = false;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

}
