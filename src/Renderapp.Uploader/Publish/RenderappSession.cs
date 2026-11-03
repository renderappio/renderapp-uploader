using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Renderapp.Uploader.Publish;

// OAuth 2.0 authorization code + PKCE (RFC 7636) for the public native client the server seeds as
// `renderapp-visualiser`. The token must name the server as its audience (RFC 8707 `resource`),
// or the API refuses it.
internal sealed class RenderappSession
{
    public const string ClientId = "renderapp-visualiser";
    private const string Scope = "projects:read projects:write workspaces:read offline_access";

    private readonly HttpClient http;
    private readonly Uri host;
    private readonly string storePath;
    private string? accessToken;
    private DateTimeOffset accessTokenExpires;
    // Uploads ask for the token from several requests at once. A refresh token is spent on use,
    // so only one of them may refresh, or sign in, and the rest take what it got.
    private readonly SemaphoreSlim renewing = new(1);

    public RenderappSession(HttpClient http, Uri host)
    {
        this.http = http;
        this.host = host;
        storePath = StateFile.PathFor($"session-{host.Host}-{host.Port}.json");
    }

    private string Resource => host.GetLeftPart(UriPartial.Authority);

    public async Task<string> AccessTokenAsync(CancellationToken cancellation)
    {
        if (Current() is { } token) return token;
        await renewing.WaitAsync(cancellation);
        try
        {
            if (Current() is { } renewed) return renewed;
            var refresh = StateFile.Read<StoredSession>(storePath)?.RefreshToken;
            if (refresh == null || !await RefreshAsync(refresh, cancellation))
                await SignInAsync(cancellation);
            return accessToken!;
        }
        finally
        {
            renewing.Release();
        }
    }

    private string? Current() => accessToken != null && DateTimeOffset.UtcNow < accessTokenExpires ? accessToken : null;

    public void SignOut()
    {
        accessToken = null;
        File.Delete(storePath);
    }

    private async Task SignInAsync(CancellationToken cancellation)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        using var loopback = new Loopback("callback");

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = loopback.RedirectUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = Resource,
        };
        Loopback.OpenBrowser(new Uri(host, "/api/auth/oauth2/authorize?" + Encode(query)).AbsoluteUri);

        var reply = await loopback.WaitAsync(cancellation);
        if (reply["state"] != state) throw new PublishException("The sign-in reply didn't match this request.");
        if (reply["error"] is { } error)
            throw new PublishException(error == "access_denied" ? "Sign-in was cancelled." : $"Sign-in failed: {error}");
        var code = reply["code"] ?? throw new PublishException("Sign-in returned no code.");

        await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = loopback.RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["resource"] = Resource,
        }, cancellation, throwOnFailure: true);
    }

    private Task<bool> RefreshAsync(string refreshToken, CancellationToken cancellation) =>
        ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
            ["resource"] = Resource,
        }, cancellation, throwOnFailure: false);

    private async Task<bool> ExchangeAsync(Dictionary<string, string> form, CancellationToken cancellation, bool throwOnFailure)
    {
        using var response = await http.PostAsync(new Uri(host, "/api/auth/oauth2/token"), new FormUrlEncodedContent(form), cancellation);
        if (!response.IsSuccessStatusCode)
        {
            if (!throwOnFailure) return false;
            throw new PublishException($"Renderapp refused the sign-in ({(int)response.StatusCode}).");
        }
        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellation)
            ?? throw new PublishException("Renderapp sent an empty token response.");
        accessToken = tokens.AccessToken;
        accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(tokens.ExpiresIn - 60, 30));
        if (tokens.RefreshToken != null) StateFile.Write(storePath, new StoredSession(tokens.RefreshToken), ownerOnly: true);
        return true;
    }

    internal static string Encode(Dictionary<string, string> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record StoredSession(string RefreshToken);
}

internal sealed class PublishException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
