using PushToMute.Logic;

namespace PushToMute.Wiring;

// Composition root: owns transports, passes business operations to GUI and HTTP adapter.
internal sealed class ApplicationCoordinator : IAsyncDisposable
{
    private readonly DiscordCredentialsStore _credentials;
    private readonly DiscordRpcClient _discord;
    private readonly VoiceControlService _voice;
    private readonly HomeyHttpServer _http;
    private readonly Action<string> _log;
    private readonly DiscordEventBroker _eventBroker;
    private readonly HomeyWebhookDestination _homeyDestination;
    public event Action? StatusChanged;
    public bool HttpRunning => _http.Running;
    public bool DiscordConnected => _discord.Connected;
    public bool DiscordAuthorized => _discord.Authorized;

    public ApplicationCoordinator(Func<string> selectedUserId, Action<string> log)
    {
        _log = log;
        _credentials = new DiscordCredentialsStore();
        _discord = new DiscordRpcClient(_credentials);
        _voice = new VoiceControlService(_discord);
        _http = new HomeyHttpServer(_voice, selectedUserId, log);
        
        _discord.Message += log;
        _discord.StatusChanged += OnDiscordStatusChanged;

        
        string homeyUrl = Environment.GetEnvironmentVariable("PUSHTOMUTE_HOMEY_URL")
            ?? "http://192.168.50.110";

        _homeyDestination = new HomeyWebhookDestination(homeyUrl, ShowStatusMessage);
        _eventBroker = new DiscordEventBroker(
            new IDiscordEventDestination[] { _homeyDestination },
            ShowStatusMessage);

        _discord.IpcEventReceived += OnDiscordIpcEvent;
        ShowStatusMessage("Discord IPC event listener configured.");
    }

    private void OnDiscordStatusChanged()
    {
        StatusChanged?.Invoke();
    }

    private void ShowStatusMessage(string message)
    {
        _log(message);
    }

    public async Task StartHttpAsync() { 
        await _http.StartAsync(); 
        StatusChanged?.Invoke(); 
    }

    public async Task StopHttpAsync()
    {
        await _http.StopAsync(); 
        StatusChanged?.Invoke();
    }
    
    public Task ConnectDiscordAsync(string clientId, CancellationToken ct) => _discord.ConnectAsync(clientId, ct);
    
    public Task AuthorizeDiscordAsync(string secret, string uri, CancellationToken ct) => _discord.AuthorizeAsync(secret, uri, ct);
    
    public Task DisconnectDiscordAsync() => _discord.DisconnectAsync();

    public async Task<DiscordSettings?> LoadSettingsAsync(CancellationToken ct) =>
        await _credentials.ReadSettingsAsync(ct);

    public async Task<bool> AutoConnectDiscordAsync(DiscordSettings settings, CancellationToken ct)
    {
        await _discord.ConnectAsync(settings.ClientId, ct);
        var restored = await _discord.TryRestoreAuthorizationAsync(ct);
        
        if (!restored)
        {
            _log("No saved Discord authorization. Use Authorize Discord once.");
        }
        return restored;
    }

    public async Task RunVoiceAsync(string action, string userId, CancellationToken ct)
    {
        var muted = await _voice.ExecuteAsync(action, userId, ct);
        _log($"Discord local mute: user {userId}, muted={muted} ({action}, GUI).");
    }
    public async ValueTask DisposeAsync()
    {
        _discord.IpcEventReceived -= OnDiscordIpcEvent;
        _discord.StatusChanged -= OnDiscordStatusChanged;
        await _http.DisposeAsync();
        await _discord.DisposeAsync();
        await _eventBroker.DisposeAsync();
        _homeyDestination.Dispose();
    }
    
    private void OnDiscordIpcEvent(DiscordIpcEvent discordEvent)
    {
        string eventName = discordEvent.EventName;
        string description = DescribeDiscordIpcEvent(discordEvent);
        ShowStatusMessage($"Discord IPC {eventName}: {description}");

        PublishUserJoinedEvent(discordEvent);
    }

    
    private void PublishUserJoinedEvent(DiscordIpcEvent ipcEvent)
    {
        bool recognized =
            DiscordIpcVoiceEventAdapter.TryCreateUserJoinedEvent(
                ipcEvent,
                out DiscordVoiceEvent? voiceEvent);

        if (!recognized || voiceEvent is null)
        {
            return;
        }

        bool accepted = _eventBroker.TryPublish(voiceEvent);

        if (!accepted)
        {
            ShowStatusMessage(
                $"Could not queue User Joined event for {voiceEvent.DisplayName}.");

            return;
        }

        string message =
            $"Queued User Joined event: {voiceEvent.DisplayName} " +
            $"({voiceEvent.UserId}).";

        ShowStatusMessage(message);
    }
    
    private static string DescribeDiscordIpcEvent(
        DiscordIpcEvent discordEvent)
    {
        System.Text.Json.JsonElement data = discordEvent.Data;

        if (discordEvent.EventName == "VOICE_CHANNEL_SELECT")
        {
            string? channelId = ReadStringProperty(
                data,
                "channel_id");

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return "Disconnected from voice";
            }

            return $"Selected channel {channelId}";
        }

        if (discordEvent.EventName.StartsWith(
                "VOICE_STATE_",
                StringComparison.Ordinal))
        {
            string? userId = ReadStringProperty(data, "user_id");
            string? userName = null;
            string? nickname = ReadStringProperty(data, "nick");

            if (data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                data.TryGetProperty("user", out System.Text.Json.JsonElement user))
            {
                userId ??= ReadStringProperty(user, "id");
                userName = ReadStringProperty(user, "username");
            }

            string displayName = nickname
                                 ?? userName
                                 ?? userId
                                 ?? "unknown";

            return $"User {displayName} ({userId ?? "unknown"})";
        }

        return data.ToString();
    }


    private static string? ReadStringProperty(
        System.Text.Json.JsonElement element,
        string name)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out System.Text.Json.JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
