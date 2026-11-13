using System.Diagnostics;
using System.Text.Json;

namespace PushToMute.Wiring;

// Linux Secret Service (KWallet / another org.freedesktop.secrets provider).
// Never write OAuth tokens or the Client Secret into ordinary configuration files.
internal sealed class DiscordCredentialsStore
{
    private const string Service = "homey-discord-control";
    private const string ItemLabel = "Homey Discord Control OAuth credentials";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string SettingsFile
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, Service, "settings.json");
        }
    }

    public async Task<DiscordSettings?> ReadSettingsAsync(CancellationToken ct)
    {
        if (!File.Exists(SettingsFile)) return null;
        return JsonSerializer.Deserialize<DiscordSettings>(await File.ReadAllTextAsync(SettingsFile, ct), JsonOptions);
    }

    public async Task SaveSettingsAsync(DiscordSettings settings, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(SettingsFile)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions), ct);
    }

    public async Task<DiscordCredentials?> ReadCredentialsAsync(string clientId, CancellationToken ct)
    {
        var (exitCode, output) = await InvokeAsync(new[] { "lookup", "app", Service, "client_id", clientId }, null, ct);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;
        return JsonSerializer.Deserialize<DiscordCredentials>(output, JsonOptions);
    }

    public async Task SaveCredentialsAsync(string clientId, DiscordCredentials credentials, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var (exitCode, _) = await InvokeAsync(
            new[] { "store", "--label=" + ItemLabel, "app", Service, "client_id", clientId }, json, ct);
        if (exitCode != 0) throw new InvalidOperationException("Could not save Discord credentials to the Secret Service keyring.");
    }

    public async Task DeleteCredentialsAsync(string clientId, CancellationToken ct)
    {
        await InvokeAsync(new[] { "clear", "app", Service, "client_id", clientId }, null, ct);
    }

    private static async Task<(int exitCode, string stdout)> InvokeAsync(
        string[] args, string? stdin, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                "secret-tool is unavailable. On CachyOS install 'libsecret' and enable a Secret Service keyring (KWallet).", ex);
        }
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var errors = await errorTask;
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(errors))
        {
            throw new InvalidOperationException("Secret Service operation failed. Check that KWallet is unlocked and its Secret Service integration is available.");
        }
            
        return (process.ExitCode, output);
    }
}

