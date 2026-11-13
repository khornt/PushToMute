namespace PushToMute.Wiring;

internal sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc);
