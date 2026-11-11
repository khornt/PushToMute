using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using PushToMute.Logic;

namespace PushToMute.Wiring;

internal sealed class DiscordRpcClient : IDiscordVoiceGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly DiscordCredentialsStore _store = new();

    private Socket? _socket;
    private NetworkStream? _stream;
    private CancellationTokenSource? _lifetime;
    private Task? _reader;
    private string? _clientId;

    public bool Connected { get; private set; }
    public bool Authorized { get; private set; }

    public event Action<string>? Message;
    public event Action? StatusChanged;

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
                    await WriteFrameAsync(stream, 0, handshake, cancellationToken);

                    (int opcode, JsonElement packet) = await ReadFrameAsync(stream, cancellationToken);
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
    
    // Called after a fresh IPC handshake.
    public async Task<bool> TryRestoreAuthorizationAsync(CancellationToken ct)
    {
        string clientId = GetConnectedClientId();
        DiscordCredentials? credentials = await _store.ReadCredentialsAsync(clientId, ct);

        if (credentials is null || string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            return false;
        }

        if (credentials.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            credentials = await RefreshAsync(clientId, credentials, ct);
        }

        try
        {
            await AuthenticateTokenAsync(credentials.AccessToken, ct);
            return true;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Discord AUTHENTICATE error", StringComparison.Ordinal))
        {
            Message?.Invoke("Saved Discord access token rejected; trying refresh once...");
            credentials = await RefreshAsync(clientId, credentials, ct);
            await AuthenticateTokenAsync(credentials.AccessToken, ct);
            return true;
        }
    }
    
    // Interactive only on initial setup or after credential revocation.
    public async Task AuthorizeAsync(string clientSecret, string redirectUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new ArgumentException("Client Secret is required.");
        }

        string clientId = GetConnectedClientId();

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Redirect URI must match the Discord Developer Portal.");
        }

        Message?.Invoke("Requesting RPC authorization from Discord (check Discord for consent)...");

        DiscordAuthorizeArguments arguments = BuildAuthorizationArguments(clientId);
        JsonElement reply = await SendCommandAsync("AUTHORIZE", arguments, cancellationToken);
        string? authorizationCode = reply.GetProperty("data").GetProperty("code").GetString();

        if (string.IsNullOrEmpty(authorizationCode))
        {
            throw new InvalidDataException("AUTHORIZE did not return a code.");
        }

        Message?.Invoke("Exchanging authorization code for OAuth tokens...");

        Dictionary<string, string> fields = BuildAuthorizationCodeFields(
            clientId,
            clientSecret,
            authorizationCode,
            redirectUri);

        OAuthTokens token = await ExchangeTokensAsync(fields, cancellationToken);
        await AuthenticateTokenAsync(token.AccessToken, cancellationToken);

        // A keyring failure must not invalidate an otherwise working RPC session.
        try
        {
            DiscordSettings settings = new(clientId, redirectUri);
            DiscordCredentials credentials = new(
                clientSecret,
                token.AccessToken,
                token.RefreshToken,
                token.ExpiresAtUtc);

            await _store.SaveSettingsAsync(settings, cancellationToken);
            await _store.SaveCredentialsAsync(clientId, credentials, cancellationToken);
            Message?.Invoke("Discord credentials stored in the desktop keyring for automatic startup.");
        }
        catch (Exception exception)
        {
            Message?.Invoke("Authorized, but automatic sign-in could not be saved: " + exception.Message);
        }
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

    

    private string GetConnectedClientId()
    {
        if (!Connected || _clientId is null)
        {
            throw new InvalidOperationException("Connect first.");
        }

        return _clientId;
    }

    private static DiscordAuthorizeArguments BuildAuthorizationArguments(string clientId)
    {
        string[] scopes = BuildAuthorizationScopes();

        DiscordAuthorizeArguments arguments = new()
        {
            client_id = clientId,
            scopes = scopes
        };

        return arguments;
    }

    private static string[] BuildAuthorizationScopes()
    {
        string[] scopes =
        [
            "rpc",
            "identify"
        ];

        return scopes;
    }

    private static Dictionary<string, string> BuildAuthorizationCodeFields(
        string clientId,
        string clientSecret,
        string authorizationCode,
        string redirectUri)
    {
        Dictionary<string, string> fields = new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = redirectUri
        };

        return fields;
    }

    

    private async Task<DiscordCredentials> RefreshAsync(
        string clientId,
        DiscordCredentials credentials,
        CancellationToken ct)
    {
        Message?.Invoke("Refreshing Discord OAuth access token...");

        Dictionary<string, string> fields = BuildRefreshTokenFields(clientId, credentials);
        OAuthTokens token = await ExchangeTokensAsync(fields, ct);

        DiscordCredentials updated = credentials with
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAtUtc = token.ExpiresAtUtc
        };

        // Save the rotated refresh token before proceeding.
        await _store.SaveCredentialsAsync(clientId, updated, ct);
        Message?.Invoke("Discord OAuth token refreshed and saved.");
        return updated;
    }

    private static Dictionary<string, string> BuildRefreshTokenFields(
        string clientId,
        DiscordCredentials credentials)
    {
        Dictionary<string, string> fields = new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = credentials.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken
        };

        return fields;
    }

    private static async Task<OAuthTokens> ExchangeTokensAsync(
        Dictionary<string, string> fields,
        CancellationToken ct)
    {
        using HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        using FormUrlEncodedContent body = new(fields);
        using HttpResponseMessage response = await http.PostAsync(
            "https://discord.com/api/oauth2/token",
            body,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            string message = $"Discord OAuth token exchange failed (HTTP {(int)response.StatusCode}). " +
                             "Reauthorize if credentials have expired or been revoked.";
            throw new InvalidOperationException(message);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        JsonElement root = document.RootElement;
        string? accessToken = root.GetProperty("access_token").GetString();
        string? refreshToken = root.GetProperty("refresh_token").GetString();
        int expiresInSeconds = root.GetProperty("expires_in").GetInt32();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidDataException("Discord returned incomplete OAuth credentials.");
        }

        DateTimeOffset expiration = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
        OAuthTokens tokens = new(accessToken, refreshToken, expiration);
        return tokens;
    }

    private async Task AuthenticateTokenAsync(string accessToken, CancellationToken ct)
    {
        DiscordAuthenticateArguments arguments = BuildAuthenticateArguments(accessToken);
        JsonElement reply = await SendCommandAsync("AUTHENTICATE", arguments, ct);
        JsonElement data = reply.GetProperty("data");

        string scopes = ReadGrantedScopes(data);
        Authorized = true;
        StatusChanged?.Invoke();
        Message?.Invoke($"Discord authenticated. Granted scopes: {scopes}");
    }

    private static DiscordAuthenticateArguments BuildAuthenticateArguments(string accessToken)
    {
        DiscordAuthenticateArguments arguments = new()
        {
            access_token = accessToken
        };

        return arguments;
    }

    private static string ReadGrantedScopes(JsonElement data)
    {
        if (!data.TryGetProperty("scopes", out JsonElement scopeArray))
        {
            return "unknown";
        }

        if (scopeArray.ValueKind != JsonValueKind.Array)
        {
            return "unknown";
        }

        List<string> scopes = new();
        foreach (JsonElement scope in scopeArray.EnumerateArray())
        {
            string? value = scope.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                scopes.Add(value);
            }
        }

        return string.Join(", ", scopes);
    }

    // Changes only the local mute state, not a server-wide mute.
    public async Task<bool> SetUserMuteAsync(string userId, bool muted, CancellationToken ct)
    {
        EnsureUserId(userId);
        EnsureAuthorized();

        DiscordUserMuteArguments arguments = BuildUserMuteArguments(userId, muted);
        JsonElement reply = await SendCommandAsync("SET_USER_VOICE_SETTINGS", arguments, ct);
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

        JsonElement channel = await SendCommandAsync(
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
        if (!Authorized)
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

    private async Task<JsonElement> SendCommandAsync(string command, object arguments, CancellationToken ct)
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
                DiscordCommandPayload payload = BuildCommandPayload(command, arguments, nonce);
                await WriteFrameAsync(_stream, 1, payload, ct);
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

    private static DiscordCommandPayload BuildCommandPayload(string command, object arguments, string nonce)
    {
        DiscordCommandPayload payload = new()
        {
            cmd = command,
            args = arguments,
            nonce = nonce
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
                (int opcode, JsonElement packet) = await ReadFrameAsync(_stream, ct);

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
            await WriteFrameAsync(_stream, 4, packet, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void ProcessRpcPacket(JsonElement packet)
    {
        if (packet.TryGetProperty("nonce", out JsonElement nonceElement) &&
            nonceElement.ValueKind == JsonValueKind.String)
        {
            string? nonce = nonceElement.GetString();
            if (nonce is not null && _pending.TryRemove(nonce, out TaskCompletionSource<JsonElement>? pending))
            {
                pending.TrySetResult(packet.Clone());
                return;
            }
        }

        if (packet.TryGetProperty("evt", out JsonElement evt) && evt.GetString() == "ERROR")
        {
            Message?.Invoke("Discord RPC event error: " + packet.GetProperty("data"));
        }
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        int opcode,
        object data,
        CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(data);
        byte[] header = new byte[8];

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), payload.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static async Task<(int opcode, JsonElement payload)> ReadFrameAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (length < 0 || length > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid RPC packet size.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement packet = document.RootElement.Clone();
        return (opcode, packet);
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

    private sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc);

    // Explicit payload types keep RPC request construction independently debuggable.
    private sealed class DiscordHandshakePayload
    {
        public int v { get; init; }
        public string client_id { get; init; } = string.Empty;
    }

    private sealed class DiscordAuthorizeArguments
    {
        public string client_id { get; init; } = string.Empty;
        public string[] scopes { get; init; } = [];
    }

    private sealed class DiscordAuthenticateArguments
    {
        public string access_token { get; init; } = string.Empty;
    }

    private sealed class DiscordUserMuteArguments
    {
        public string user_id { get; init; } = string.Empty;
        public bool mute { get; init; }
    }

    private sealed class DiscordEmptyArguments
    {
    }

    private sealed class DiscordCommandPayload
    {
        public string cmd { get; init; } = string.Empty;
        public object args { get; init; } = new DiscordEmptyArguments();
        public string nonce { get; init; } = string.Empty;
    }
}
