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

PushToMute runs a lightweight HTTP server that receives commands from a smart home hub and communicates with Discord to control the configured user's server mute state.

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

The local mute feature uses authenticated desktop RPC; no bot permission is required for it. The optional voice-event monitor uses a separate bot connection.

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

## Discord voice events → Homey (new)

This feature is **separate from the existing local Discord RPC connection**. The
RPC `ReadLoopAsync` and pending-command nonce matching remain unchanged. A second,
bot-authenticated Discord **Gateway WebSocket** observes server voice states.

Configure these environment variables before launching Rider or `dotnet run`:

```fish
set -x PUSHTOMUTE_DISCORD_BOT_TOKEN 'YOUR_BOT_TOKEN'
set -x PUSHTOMUTE_GUILD_ID 'YOUR_SERVER_ID'
set -x PUSHTOMUTE_HOMEY_URL 'http://192.168.50.110'
dotnet run
```

Avoid saving your bot token in project files or committing it to Git. For persistent
use, retrieve it from a secret store and inject it at launch. The bot must be
installed in the intended server. The Gateway uses the standard `GUILDS` and
`GUILD_VOICE_STATES` intents; it does **not** require Message Content intent or
reading chat messages.

Create three Homey Flows with these Logic webhook event names:

| Homey webhook event | Meaning | `tag` |
|---|---|---|
| `discord_voice_joined` | User entered a voice channel from no channel | Discord user ID |
| `discord_voice_left` | User left voice | Discord user ID |
| `discord_voice_moved` | User changed voice channels | Discord user ID |

For Homey Pro 2023+ the destination uses
`GET http://HOMEY-IP/webhook?event=...&tag=...`. The broker currently sends a
separate request for each detected event; user IDs are sent as the tag. It logs
delivery failures without blocking the Discord receive loop. The queue is bounded
(256 events); it can drop events under sustained overload. Events aren't persisted
or retried after application shutdown.

The bot's startup voice-state snapshot populates the current state without
emitting fake joins. After a reconnect the snapshot is refreshed; voice changes
that occurred while disconnected cannot be reconstructed. This first implementation
uses a fresh Gateway Identify after reconnect, rather than session Resume.

**Note:** Existing HTTP GET command endpoints on port 5050 are independent of this
outbound webhook feature. The bot listener starts automatically if configured;
Homey's outbound event deliveries do not require starting Kestrel.

**Known correction:** The older README text referring to a "server mute" is
inaccurate for the existing desktop IPC commands: those commands mute *locally*
for the logged-in desktop user, not the entire server.
