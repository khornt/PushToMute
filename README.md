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
(Server Voice Mute)
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

The Discord bot must have permission to server-mute the selected user.

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
