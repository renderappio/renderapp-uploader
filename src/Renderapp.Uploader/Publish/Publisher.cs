using Newtonsoft.Json.Linq;

namespace renderappio_visualiser.Publish;

internal enum PublishStage { SigningIn, ChoosingDestination, Uploading }

// Sign in, choose where to publish, upload. The picker reserves a new version every time it's
// used, so a publish that stopped part-way keeps its destination here, per source folder, and the
// next publish of that folder finishes the same version instead of reserving another.
internal sealed class Publisher(HttpClient http, Uri host, RenderappSession session)
{
    private readonly string pendingPath = StateFile.PathFor($"pending-{host.Host}-{host.Port}.json");

    public async Task<Destination> PublishAsync(JObject rio, string name, Action<PublishStage> stage,
        IProgress<UploadProgress> progress, CancellationToken cancellation)
    {
        var source = rio["CacheDir"]?.Value<string>() ?? throw new PublishException("The rio names no CacheDir.");
        stage(PublishStage.SigningIn);
        await session.AccessTokenAsync(cancellation);

        var pending = StateFile.Read<Dictionary<string, Destination>>(pendingPath) ?? [];
        if (!pending.TryGetValue(source, out var destination))
        {
            stage(PublishStage.ChoosingDestination);
            destination = await DestinationPicker.PickAsync(host, name, cancellation);
            pending[source] = destination;
            StateFile.Write(pendingPath, pending);
        }

        stage(PublishStage.Uploading);
        try
        {
            await new BaseFileUploader(http, host, session).PublishAsync(rio, destination, null, progress, cancellation);
        }
        // Renderapp refused this version itself (gone, no longer allowed, the rio rejected), so
        // trying it again can't help: forget it and let the next publish choose afresh.
        catch (PublishException error) when (error.Status is { } status && (int)status is >= 400 and < 500)
        {
            pending.Remove(source);
            StateFile.Write(pendingPath, pending);
            throw new PublishException($"{error.Message} Publish again to choose where it goes.");
        }
        pending.Remove(source);
        StateFile.Write(pendingPath, pending);
        return destination;
    }
}
