using System.Text.Json;

namespace PushToMute.Wiring;

// Owns OAuth authorization, token exchange and refresh. Does not own the IPC socket.
internal sealed class DiscordAuthenticationService
{
    private readonly DiscordCredentialsStore _store;
    private readonly Func<string, object, CancellationToken, Task<JsonElement>> _sendCommand;
    private readonly Func<string, CancellationToken, Task> _onAuthenticated;
    private readonly Action<string> _status;

    public DiscordAuthenticationService(
        DiscordCredentialsStore store,
        Func<string, object, CancellationToken, Task<JsonElement>> sendCommand,
        Func<string, CancellationToken, Task> onAuthenticated,
        Action<string> status)
    {
        _store = store;
        _sendCommand = sendCommand;
        _onAuthenticated = onAuthenticated;
        _status = status;
    }

    // Called after a fresh IPC handshake.
    public async Task<bool> TryRestoreAuthorizationAsync(string clientId, CancellationToken ct)
    {
        DiscordCredentials? credentials = await _store.ReadCredentialsAsync(clientId, ct);

        if (credentials is null || string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            return false;
        }

        if (credentials.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            credentials = await RefreshAsync(clientId, credentials, ct);
        }

        try
        {
            await AuthenticateTokenAsync(credentials.AccessToken, ct);
            return true;
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Discord AUTHENTICATE error", StringComparison.Ordinal))
        {
            _status("Saved Discord access token rejected; trying refresh once...");
            credentials = await RefreshAsync(clientId, credentials, ct);
            await AuthenticateTokenAsync(credentials.AccessToken, ct);
            return true;
        }
    }
    
    // Interactive only on initial setup or after credential revocation.
    public async Task AuthorizeAsync(string clientId, string clientSecret, string redirectUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new ArgumentException("Client Secret is required.");
        }


        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Redirect URI must match the Discord Developer Portal.");
        }

        _status("Requesting RPC authorization from Discord (check Discord for consent)...");

        DiscordAuthorizeArguments arguments = BuildAuthorizationArguments(clientId);
        JsonElement reply = await _sendCommand("AUTHORIZE", arguments, cancellationToken);
        string? authorizationCode = reply.GetProperty("data").GetProperty("code").GetString();

        if (string.IsNullOrEmpty(authorizationCode))
        {
            throw new InvalidDataException("AUTHORIZE did not return a code.");
        }

        _status("Exchanging authorization code for OAuth tokens...");

        Dictionary<string, string> fields = BuildAuthorizationCodeFields(
            clientId,
            clientSecret,
            authorizationCode,
            redirectUri);

        OAuthTokens token = await ExchangeTokensAsync(fields, cancellationToken);
        await AuthenticateTokenAsync(token.AccessToken, cancellationToken);

        // A keyring failure must not invalidate an otherwise working RPC session.
        try
        {
            DiscordSettings settings = new(clientId, redirectUri);
            DiscordCredentials credentials = new(
                clientSecret,
                token.AccessToken,
                token.RefreshToken,
                token.ExpiresAtUtc);

            await _store.SaveSettingsAsync(settings, cancellationToken);
            await _store.SaveCredentialsAsync(clientId, credentials, cancellationToken);
            _status("Discord credentials stored in the desktop keyring for automatic startup.");
        }
        catch (Exception exception)
        {
            _status("Authorized, but automatic sign-in could not be saved: " + exception.Message);
        }
    }

    private static DiscordAuthorizeArguments BuildAuthorizationArguments(string clientId)
    {
        string[] scopes = BuildAuthorizationScopes();

        DiscordAuthorizeArguments arguments = new()
        {
            client_id = clientId,
            scopes = scopes
        };

        return arguments;
    }

    private static string[] BuildAuthorizationScopes()
    {
        string[] scopes =
        [
            "rpc",
            "identify"
        ];

        return scopes;
    }

    private static Dictionary<string, string> BuildAuthorizationCodeFields(
        string clientId,
        string clientSecret,
        string authorizationCode,
        string redirectUri)
    {
        Dictionary<string, string> fields = new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["redirect_uri"] = redirectUri
        };

        return fields;
    }

    

    private async Task<DiscordCredentials> RefreshAsync(
        string clientId,
        DiscordCredentials credentials,
        CancellationToken ct)
    {
        _status("Refreshing Discord OAuth access token...");

        Dictionary<string, string> fields = BuildRefreshTokenFields(clientId, credentials);
        OAuthTokens token = await ExchangeTokensAsync(fields, ct);

        DiscordCredentials updated = credentials with
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAtUtc = token.ExpiresAtUtc
        };

        // Save the rotated refresh token before proceeding.
        await _store.SaveCredentialsAsync(clientId, updated, ct);
        _status("Discord OAuth token refreshed and saved.");
        return updated;
    }

    private static Dictionary<string, string> BuildRefreshTokenFields(
        string clientId,
        DiscordCredentials credentials)
    {
        Dictionary<string, string> fields = new()
        {
            ["client_id"] = clientId,
            ["client_secret"] = credentials.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken
        };

        return fields;
    }

    private static async Task<OAuthTokens> ExchangeTokensAsync(
        Dictionary<string, string> fields,
        CancellationToken ct)
    {
        using HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        using FormUrlEncodedContent body = new(fields);
        using HttpResponseMessage response = await http.PostAsync(
            "https://discord.com/api/oauth2/token",
            body,
            ct);

        if (!response.IsSuccessStatusCode)
        {
            string message = $"Discord OAuth token exchange failed (HTTP {(int)response.StatusCode}). " +
                             "Reauthorize if credentials have expired or been revoked.";
            throw new InvalidOperationException(message);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        JsonElement root = document.RootElement;
        string? accessToken = root.GetProperty("access_token").GetString();
        string? refreshToken = root.GetProperty("refresh_token").GetString();
        int expiresInSeconds = root.GetProperty("expires_in").GetInt32();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidDataException("Discord returned incomplete OAuth credentials.");
        }

        DateTimeOffset expiration = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
        OAuthTokens tokens = new(accessToken, refreshToken, expiration);
        return tokens;
    }

    private async Task AuthenticateTokenAsync(string accessToken, CancellationToken ct)
    {
        DiscordAuthenticateArguments arguments = BuildAuthenticateArguments(accessToken);
        JsonElement reply = await _sendCommand("AUTHENTICATE", arguments, ct);
        JsonElement data = reply.GetProperty("data");

        string scopes = ReadGrantedScopes(data);
        await _onAuthenticated(scopes, ct);
    }

    private static DiscordAuthenticateArguments BuildAuthenticateArguments(string accessToken)
    {
        DiscordAuthenticateArguments arguments = new()
        {
            access_token = accessToken
        };

        return arguments;
    }

    private static string ReadGrantedScopes(JsonElement data)
    {
        if (!data.TryGetProperty("scopes", out JsonElement scopeArray))
        {
            return "unknown";
        }

        if (scopeArray.ValueKind != JsonValueKind.Array)
        {
            return "unknown";
        }

        List<string> scopes = new();
        foreach (JsonElement scope in scopeArray.EnumerateArray())
        {
            string? value = scope.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                scopes.Add(value);
            }
        }

        return string.Join(", ", scopes);
    }

}
