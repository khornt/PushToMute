using PushToMute.Logic;

namespace PushToMute.Wiring;

// Composition root: owns transports, passes business operations to GUI and HTTP adapter.
internal sealed class ApplicationCoordinator : IAsyncDisposable
{
    private readonly DiscordRpcClient _discord = new();
    private readonly DiscordCredentialsStore _credentials = new();
    private readonly VoiceControlService _voice;
    private readonly HomeyHttpServer _http;
    private readonly Action<string> _log;
    private readonly DiscordEventBroker? _eventBroker;
  //  private readonly DiscordGatewayClient? _gateway;
    private readonly HomeyWebhookDestination? _homeyDestination;
    public event Action? StatusChanged;
    public bool HttpRunning => _http.Running;
    public bool DiscordConnected => _discord.Connected;
    public bool DiscordAuthorized => _discord.Authorized;

    public ApplicationCoordinator(Func<string> selectedUserId, Action<string> log)
    {
        _log = log;
        _voice = new VoiceControlService(_discord);
        _http = new HomeyHttpServer(_voice, selectedUserId, log);
        
        _discord.Message += log;
        _discord.StatusChanged += OnDiscordStatusChanged;

        
        string homeyUrl = Environment.GetEnvironmentVariable(
            "PUSHTOMUTE_HOMEY_URL") ?? "http://192.168.50.110";

        _homeyDestination = new HomeyWebhookDestination(homeyUrl);

        _eventBroker = new DiscordEventBroker(
            [_homeyDestination],
            ShowStatusMessage);

        _discord.IpcEventReceived += OnDiscordIpcEvent;
        

        ShowStatusMessage("Discord IPC event monitoring configured.");
    }

    private void OnDiscordStatusChanged()
    {
        StatusChanged?.Invoke();
    }

    private void OnDiscordVoiceEvent(DiscordVoiceEvent discordEvent)
    {
        _eventBroker?.TryPublish(discordEvent);
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
        if (_eventBroker is not null)
        {
            await _eventBroker.DisposeAsync();
        }

        _homeyDestination?.Dispose();
        await _http.DisposeAsync();
        await _discord.DisposeAsync();
    }
    
    private void OnDiscordIpcEvent(DiscordIpcEvent discordEvent)
    {
        ShowStatusMessage(
            $"Discord IPC event received: {discordEvent.EventName}");
    }
}
