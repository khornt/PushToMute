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
        _discord.StatusChanged += () => StatusChanged?.Invoke();
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
        await _http.DisposeAsync();
        await _discord.DisposeAsync();
    }
}
