using System.Security.Cryptography;

namespace renderappio_visualiser.Publish;

internal sealed record Destination(string ProjectId, int Version, string WorkspaceId);

// Renderapp's own /upload/destination page picks the project, or makes a new one, reserves the
// next version and sends the browser back here with it.
internal static class DestinationPicker
{
    public static async Task<Destination> PickAsync(Uri host, string suggestedName, CancellationToken cancellation)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var loopback = new Loopback("destination");
        var query = new Dictionary<string, string>
        {
            ["return_to"] = loopback.RedirectUri,
            ["state"] = state,
            ["name"] = suggestedName,
        };
        Loopback.OpenBrowser(new Uri(host, "/upload/destination?" + RenderappSession.Encode(query)).AbsoluteUri);

        var reply = await loopback.WaitAsync(cancellation);
        if (reply["state"] != state) throw new PublishException("The destination reply didn't match this request.");
        if (reply["error"] != null) throw new PublishException("Publishing was cancelled.");
        if (reply["project_id"] is not { } projectId
            || !int.TryParse(reply["version"], out var version)
            || reply["workspace_id"] is not { } workspaceId)
            throw new PublishException("Renderapp didn't say where to publish.");
        return new Destination(projectId, version, workspaceId);
    }
}
