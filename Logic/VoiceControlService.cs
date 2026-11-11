namespace PushToMute.Logic;

// Business operations are independent of Avalonia and ASP.NET Core.
internal sealed class VoiceControlService
{
    private readonly IDiscordVoiceGateway _discord;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public VoiceControlService(IDiscordVoiceGateway discord) => _discord = discord;

    public async Task<bool> ExecuteAsync(string action, string userId, CancellationToken ct)
    {
        if (action is not ("mute" or "unmute" or "toggle"))
            throw new ArgumentException("Unsupported voice action.", nameof(action));
        await _gate.WaitAsync(ct);
        try
        {
            return action switch
            {
                "mute" => await _discord.SetUserMuteAsync(userId, true, ct),
                "unmute" => await _discord.SetUserMuteAsync(userId, false, ct),
                _ => await _discord.ToggleUserMuteAsync(userId, ct)
            };
        }
        finally { _gate.Release(); }
    }
}

internal interface IDiscordVoiceGateway
{
    Task<bool> SetUserMuteAsync(string userId, bool muted, CancellationToken ct);
    Task<bool> ToggleUserMuteAsync(string userId, CancellationToken ct);
}
