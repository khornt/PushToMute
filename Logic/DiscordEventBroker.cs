using System.Threading.Channels;

namespace PushToMute.Logic;

// A bounded queue keeps slow HTTP receivers from blocking Discord's Gateway receive loop.
internal sealed class DiscordEventBroker : IAsyncDisposable
{
    private readonly Channel<DiscordVoiceEvent> _queue;
    private readonly IReadOnlyList<IDiscordEventDestination> _destinations;
    private readonly Action<string> _status;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;

    public DiscordEventBroker(
        IEnumerable<IDiscordEventDestination> destinations,
        Action<string> status)
    {
        _destinations = destinations.ToArray();
        _status = status;

        BoundedChannelOptions options = new(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _queue = Channel.CreateBounded<DiscordVoiceEvent>(options);
        _worker = ProcessAsync(_lifetime.Token);
    }

    public bool TryPublish(DiscordVoiceEvent discordEvent)
    {
        bool accepted = _queue.Writer.TryWrite(discordEvent);

        if (!accepted)
        {
            _status("Discord event queue full; an event was dropped.");
        }

        return accepted;
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (DiscordVoiceEvent discordEvent in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                foreach (IDiscordEventDestination destination in _destinations)
                {
                    try
                    {
                        await destination.DeliverAsync(discordEvent, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        _status($"Discord webhook delivery failed: {exception.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _lifetime.Cancel();

        try
        {
            await _worker;
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
