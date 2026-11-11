using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PushToMute.Wiring;

namespace PushToMute.GUI;

// Presentation only: reads controls, dispatches user intent, updates status/log.
public partial class MainWindow : Window
{
    private readonly ApplicationCoordinator _app;
    private bool _httpBusy;
    private bool _rpcBusy;
    private string _selectedUserId = "";

    public MainWindow()
    {
        InitializeComponent();
        
        _app = new ApplicationCoordinator(
            GetSelectedUserId,
            ShowStatusMessage);
        
        _app.StatusChanged += () => Dispatcher.UIThread.Post(UpdateControls);
        DiscordTargetUserIdBox.TextChanged += (_, _) => _selectedUserId = DiscordTargetUserIdBox.Text?.Trim() ?? "";
        UpdateControls();
        AppendStatusMessage("Ready. Click Start to listen on port 5050.");
        Opened += OnOpened;

    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_rpcBusy)
        {
            return;
        } 
            
        _rpcBusy = true;
        UpdateControls();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var settings = await _app.LoadSettingsAsync(timeout.Token);
            if (settings is null)
            {
                AppendStatusMessage("First run: enter your Discord Application ID and Client Secret, connect, then authorize once.");
                return;
            }
            DiscordClientIdBox.Text = settings.ClientId;
            DiscordRedirectUriBox.Text = settings.RedirectUri;
            
            AppendStatusMessage("Restoring Discord connection and saved authorization...");
            await _app.AutoConnectDiscordAsync(settings, timeout.Token);
        }
        catch (Exception ex)
        {
            AppendStatusMessage("Automatic Discord sign-in failed: " + ex.Message);
            AppendStatusMessage("Use Authorize Discord to sign in again if necessary.");
        }
        finally { _rpcBusy = false; UpdateControls(); }
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_httpBusy || _app.HttpRunning) return;
        
        _httpBusy = true; UpdateControls();
        try
        {
            await _app.StartHttpAsync();
        }
        catch (Exception ex)
        {
            AppendStatusMessage($"Start failed: {ex.Message}");
        }
        finally
        {
            _httpBusy = false; 
            UpdateControls();
        }
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (_httpBusy || !_app.HttpRunning)
        {
            return;
        }
        _httpBusy = true; UpdateControls();
        try
        {
            await _app.StopHttpAsync();
        }
        catch (Exception ex)
        {
            AppendStatusMessage($"Stop failed: {ex.Message}");
        }
        finally
        {
            _httpBusy = false; 
            UpdateControls();
        }
    }

    private async void DiscordConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_rpcBusy || _app.DiscordConnected) return;
        _rpcBusy = true; UpdateControls();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.ConnectDiscordAsync(DiscordClientIdBox.Text?.Trim() ?? "", timeout.Token);
        }
        catch (Exception ex) { AppendStatusMessage($"Discord connection failed: {ex.Message}"); }
        finally { _rpcBusy = false; UpdateControls(); }
    }

    private async void DiscordAuthorize_Click(object? sender, RoutedEventArgs e)
    {
        if (_rpcBusy || !_app.DiscordConnected)
        {
            return;
        }
        _rpcBusy = true; 
        UpdateControls();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await _app.AuthorizeDiscordAsync(DiscordClientSecretBox.Text ?? "",
                DiscordRedirectUriBox.Text?.Trim() ?? "", timeout.Token);
            
            DiscordClientSecretBox.Text = "";
        }
        catch (OperationCanceledException)
        {
            AppendStatusMessage("Authorization timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            AppendStatusMessage($"Authorization failed: {ex.Message}");
        }
        finally
        {
            _rpcBusy = false; UpdateControls();
        }
    }

    private async void DiscordDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_rpcBusy)
        {
            return;
        }
        _rpcBusy = true; 
        UpdateControls();

        try
        {
            await _app.DisconnectDiscordAsync();
            AppendStatusMessage("Disconnected from Discord.");
        }
        catch (Exception ex)
        {
            AppendStatusMessage($"Disconnect failed: {ex.Message}");
        }
        finally
        {
            _rpcBusy = false; 
            UpdateControls();
        }
    }

    private async void Mute_Click(object? sender, RoutedEventArgs e) => await VoiceButtonAsync("mute");
    private async void Unmute_Click(object? sender, RoutedEventArgs e) => await VoiceButtonAsync("unmute");
    private async void Toggle_Click(object? sender, RoutedEventArgs e) => await VoiceButtonAsync("toggle");

    private async Task VoiceButtonAsync(string action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.RunVoiceAsync(action, _selectedUserId, timeout.Token);
        }
        catch (Exception ex) { AppendStatusMessage($"{action} failed: {ex.Message}"); }
    }

    private void UpdateControls()
    {
        StartButton.IsEnabled = !_httpBusy && !_app.HttpRunning;
        StopButton.IsEnabled = !_httpBusy && _app.HttpRunning;
        StatusText.Text = _app.HttpRunning ? "Listening on port 5050" : "HTTP stopped";
        DiscordConnectButton.IsEnabled = !_rpcBusy && !_app.DiscordConnected;
        DiscordAuthorizeButton.IsEnabled = !_rpcBusy && _app.DiscordConnected && !_app.DiscordAuthorized;
        DiscordDisconnectButton.IsEnabled = !_rpcBusy && _app.DiscordConnected;
        DiscordClientIdBox.IsEnabled = !_rpcBusy && !_app.DiscordConnected;
        MuteButton.IsEnabled = _app.DiscordAuthorized;
        UnmuteButton.IsEnabled = _app.DiscordAuthorized;
        ToggleButton.IsEnabled = _app.DiscordAuthorized;
        DiscordStatusText.Text = _app.DiscordAuthorized ? "Authorized"
            : _app.DiscordConnected ? "Connected (not authorized)" : "Disconnected";
    }
    
    private string GetSelectedUserId()
    {
        return _selectedUserId;
    }

    private void ShowStatusMessage(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AppendStatusMessage(message);
        });
    }
    
    private void AppendStatusMessage(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] {message}";
    
        StatusMessageBox.Text += formattedMessage + Environment.NewLine;
        StatusMessageBox.CaretIndex = StatusMessageBox.Text?.Length ?? 0;
        
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
