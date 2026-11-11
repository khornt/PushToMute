using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using PushToMute.Logic;

namespace PushToMute.Wiring;

// Transport adapter: maps HTTP requests onto the same domain operations used by the GUI.
internal sealed class HomeyHttpServer : IAsyncDisposable
{
    private const string HomeyIp = "192.168.50.110";
    private const int Port = 5050;
    private readonly VoiceControlService _voice;
    private readonly Func<string> _getSelectedUserId;
    private readonly Action<string> _log;
    private WebApplication? _app;
    public bool Running => _app is not null;

    public HomeyHttpServer(VoiceControlService voice, Func<string> getSelectedUserId, Action<string> log)
    {
        _voice = voice;
        _getSelectedUserId = getSelectedUserId;
        _log = log;
    }

    public async Task StartAsync()
    {
        if (Running) return;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{Port}");
        var app = builder.Build();
        app.MapGet("/hello", (HttpContext ctx) =>
        {
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            _log($"Hello World! Request from {remote}");
            return Results.Text("Hello World!");
        });
        app.MapGet("/discord/{action}", (string action, HttpContext ctx) => ExecuteAsync(action, null, ctx));
        app.MapGet("/discord/{action}/{userId}", (string action, string userId, HttpContext ctx) => ExecuteAsync(action, userId, ctx));
        try { await app.StartAsync(); _app = app; _log("HTTP server started."); }
        catch { await app.DisposeAsync(); throw; }
    }

    private async Task<IResult> ExecuteAsync(string action, string? suppliedUserId, HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !(IPAddress.IsLoopback(remote) || remote.MapToIPv4().ToString() == HomeyIp))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (action is not ("mute" or "unmute" or "toggle"))
        {
            return Results.NotFound(new { error = "Unknown action. Use mute, unmute or toggle." });
        }
            
        string userId = suppliedUserId ?? _getSelectedUserId();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var muted = await _voice.ExecuteAsync(action, userId, timeout.Token);
            _log($"Discord local mute: user {userId}, muted={muted} ({action}, HTTP).");

            return Results.Ok(new { userId, action, muted });
        }
        catch (ArgumentException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        catch (InvalidOperationException e)
        {
            return Results.Json(new { error = e.Message }, statusCode: 409);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { error = "Request cancelled or timed out." }, statusCode: 504);
        }
        catch (Exception e)
        {
            _log($"Homey {action} failed: {e.Message}");
            return Results.Json(new { error = e.Message }, statusCode: 502);
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync(); _log("HTTP server stopped."); }
        finally { await app.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
