namespace PushToMute.Wiring;

internal sealed record DiscordSettings(
    string ClientId, 
    string RedirectUri
    );

internal sealed record DiscordCredentials(
    string ClientSecret, 
    string AccessToken, 
    string RefreshToken, 
    DateTimeOffset ExpiresAtUtc
    );
