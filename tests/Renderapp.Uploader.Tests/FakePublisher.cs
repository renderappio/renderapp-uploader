using Newtonsoft.Json.Linq;
using Renderapp.Uploader.Publish;

namespace Renderapp.Uploader.Tests;

// Publishing without a network: each call reports the stages and the progress it's told to, then
// waits for the test to finish it.
internal sealed class FakePublisher : IPublisher
{
    private TaskCompletionSource<Destination>? pending;

    public Uri Host { get; } = new("https://renderapp.example");

    public bool IsSignedIn { get; set; }

    public IProgress<UploadProgress>? Progress { get; private set; }

    public HashSet<string> Unfinished { get; } = [];

    public List<(string Name, string? Note)> Published { get; } = [];

    public bool SignedOut { get; private set; }

    public bool HasUnfinished(string source) => Unfinished.Contains(source);

    public Task<Destination> PublishAsync(JObject rio, string name, string? note, Action<PublishStage> stage,
        IProgress<UploadProgress> progress, CancellationToken cancellation)
    {
        Published.Add((name, note));
        stage(PublishStage.SigningIn);
        stage(PublishStage.ChoosingDestination);
        stage(PublishStage.Uploading);
        Progress = progress;
        IsSignedIn = true;
        progress.Report(new UploadProgress(25, 100, 1, 4));
        pending = new TaskCompletionSource<Destination>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellation.Register(() => pending.TrySetCanceled(cancellation));
        return pending.Task;
    }

    public void Finish(Destination destination) => pending!.SetResult(destination);

    public void Fail(Exception error) => pending!.SetException(error);

    public Task<string> DecryptRioAsync(string encrypted, CancellationToken cancellation) =>
        Task.FromResult(encrypted.Replace("ENCRYPTED:", ""));

    public void SignOut()
    {
        SignedOut = true;
        IsSignedIn = false;
    }
}
