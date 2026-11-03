using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Renderapp.Uploader.Publish;

// Encrypted rio files are decrypted by Renderapp (POST /api/v1/files/decrypt-rio-file), so the
// key never ships in the app.
internal static class RioDecryption
{
    public static async Task<string> DecryptAsync(HttpClient http, Uri host, RenderappSession session, string encrypted, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(host, "/api/v1/files/decrypt-rio-file"))
        {
            Content = JsonContent.Create(new { data = encrypted }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.AccessTokenAsync(cancellation));
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new PublishException($"Renderapp couldn't decrypt it ({(int)response.StatusCode}).", response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DecryptedRio>(cancellation);
        return body?.data ?? throw new PublishException("Renderapp sent back nothing to open.");
    }

    private sealed record DecryptedRio(string? data);
}
