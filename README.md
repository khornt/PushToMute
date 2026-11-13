# PushToMute

**Control Discord voice mute directly with a physical button.**

Let's face it, every Discord channel has *that one friend*. The one who's a little too chatty, a little too loud, or simply doesn't know when to stop talking. Sometimes, a quick press of a button to mute them — and another to let them back into the conversation — is exactly what you need.

PushToMute lets you mute, unmute, or toggle the voice mute state of a **preselected Discord user** using a physical button, such as a Zigbee smart button.

The idea is simple: connect a Zigbee button to any compatible smart home hub capable of sending HTTP GET requests. When the button is pressed, the hub sends a command to PushToMute, which controls the configured user's mute state in Discord.

No need to open Discord or manually mute someone.

## Features

- **Physical Button Control** — Mute or unmute a configured Discord user with a button press.
- **Preconfigured Target** — Select which Discord user the application controls.
- **Mute / Unmute / Toggle** — Control the selected user's voice mute state.
- **Hub Independent** — Works with any smart home hub or automation system capable of sending HTTP GET requests.
- **Desktop Controls** — Manually control mute state from the application.
- **Status Output** — View connection status and activity.

## How It Works

```text
Zigbee Button
     |
     v
Smart Home Hub
(Homey, Home Assistant, etc.)
     |
     | HTTP GET
     v
PushToMute
     |
     | Discord API
     v
Preconfigured Discord User
(Local Voice Mute)
```

PushToMute runs a lightweight HTTP server that receives commands from a smart home hub and communicates with Discord to control the configured user's local mute state.

## HTTP Integration

PushToMute exposes HTTP endpoints for controlling the selected user's mute state.

| Action | Endpoint |
|--------|----------|
| Mute | `/discord/mute` |
| Unmute | `/discord/unmute` |
| Toggle | `/discord/toggle` |

Default port: `5050`

Example:

```http
GET http://<host-ip>:5050/discord/toggle
```

Any compatible hub or automation platform can trigger these endpoints.

## Discord Configuration

1. Create an application in the Discord Developer Portal.
2. Configure the required Discord permissions.
3. Configure the Application ID and authentication credentials.
4. Start PushToMute.
5. Select the Discord user to control.

The local mute feature uses authenticated desktop RPC; no bot permission is required for it. The IPC event monitor uses the same desktop RPC connection.

## Technology

- C# / .NET 10
- Avalonia UI
- ASP.NET Core
- Discord API integration

## Development

Clone the repository and restore dependencies:

```bash
dotnet restore
```

Run the application:

```bash
dotnet run
```

## Security

The HTTP endpoint is intended for controlled environments.

HTTPS and request authentication are recommended when communicating across untrusted networks. IP filtering alone is not sufficient authentication.

## License

License information has not yet been specified.

## Local Discord IPC event listener (current baseline)

This build uses **only local Discord IPC**. No bot token or Discord Gateway connection
is required. Bot-era `DiscordEventBroker`, `DiscordVoiceEvent` and
`HomeyWebhookDestination` source types remain for possible later work, but are **not
instantiated** and do not send any outgoing webhooks in this version.

After a successful interactive or restored OAuth `AUTHENTICATE`, the RPC client:

1. Sends `SUBSCRIBE` with the top-level `evt: VOICE_CHANNEL_SELECT`.
2. Calls `GET_SELECTED_VOICE_CHANNEL` to find the initially selected channel.
3. For a selected channel, subscribes to `VOICE_STATE_CREATE`,
   `VOICE_STATE_DELETE`, and `VOICE_STATE_UPDATE` with `args.channel_id`.
4. On `VOICE_CHANNEL_SELECT`, queues a change, unsubscribes from the old voice
   channel and subscribes to the new one. It does **not** await subscriptions from
   within the receive loop; that would deadlock its nonce-based response reader.

The existing nonce/`TaskCompletionSource` command-response path is preserved.
Unsolicited `DISPATCH` packets are forwarded through `IpcEventReceived` and
shown in the Avalonia status window. Subscription errors are shown there too.

**Test:** connect/authenticate Discord, join or change a voice channel, and
check for `Subscribed to VOICE_CHANNEL_SELECT`, `Listening for voice-state events
in channel ...`, and `Discord IPC VOICE_CHANNEL_SELECT`. Then ask another user
to join/leave the same voice channel and look for `VOICE_STATE_CREATE/DELETE`.

**Scope:** This monitors your own selected channel, not every channel in a
server. Some RPC subscription commands may be rejected depending on client
support or granted scopes (`rpc`, `identify` are already used by this project).
There is no automatic reauthorization or webhook delivery added here.


## Layered refactor (2026-11-13)

This baseline preserves the proven local IPC voice events and command response reader.
No Discord bot or Gateway connection has been added.

- `GUI/` — Avalonia views and status output.
- `Logic/` — voice use case and interfaces.
- `Wiring/ApplicationCoordinator.cs` — composition root; creates shared credentials store and RPC client.
- `Wiring/DiscordRpcClient.cs` — IPC connection, nonce/TaskCompletionSource correlation, incoming dispatch, session lifetime.
- `Wiring/DiscordAuthenticationService.cs` — OAuth code exchange, refresh, credential persistence and RPC authentication.
- `Wiring/DiscordCredentialsStore.cs` — Linux Secret Service and non-secret application settings.
- `Wiring/DiscordVoiceSubscriptionService.cs` — channel-selection worker and voice-state subscriptions.
- `Wiring/DiscordVoiceCommandService.cs` — mute/unmute/toggle and response interpretation.
- `Wiring/DiscordIpcFraming.cs` — frame serialization and parsing.
- `Wiring/Dto/` — request DTOs and internal credential/token models.

`DiscordRpcClient` keeps the same externally used methods and events. The coordinator
injects the same `DiscordCredentialsStore` instance used for its settings and by the OAuth service.
The authentication/subscription/voice classes receive narrow delegates for sending RPC commands,
which preserves the single command dispatcher and persistent IPC reader.

**Smoke test on CachyOS:** `dotnet build`, start the GUI, verify token restore, mute/unmute/toggle,
change Discord voice channels, and confirm both subscription status and `VOICE_STATE_*` events.

This refactor was statically reviewed but not compiled here: the environment has no .NET SDK.

## User Joined → Homey webhook dry run

The local IPC listener now converts `VOICE_STATE_CREATE` into a `DiscordVoiceEventType.Joined`
event. `ApplicationCoordinator` publishes this to the existing bounded `DiscordEventBroker`.
The broker invokes `HomeyWebhookDestination`, which **only writes the intended URL to the
Avalonia status output**. The actual HTTP GET and response check are commented out in
`Wiring/HomeyWebhookDestination.cs`.

For a participant joining the selected voice channel, look for status lines like:

```
Queued User Joined event for 123456789012345678.
Homey webhook (dry run): http://192.168.50.110/webhook?event=discord_voice_joined&tag=123456789012345678
```

Optionally set `PUSHTOMUTE_HOMEY_URL` to override the Homey base URL. No bot token
is required. This is *selected-channel IPC monitoring*, not server-wide monitoring.

Once ready, uncomment the HTTP request lines in `HomeyWebhookDestination.DeliverAsync`.
