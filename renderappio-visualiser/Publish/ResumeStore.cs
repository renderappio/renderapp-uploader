namespace renderappio_visualiser.Publish;

internal sealed class MultipartUpload
{
    public required string UploadId { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public Dictionary<int, string> Parts { get; init; } = [];
}

// The multipart uploads still open for one version, kept on disk so a restarted publish sends
// only the parts S3 hasn't got. Whole files need no record: the version's file listing says
// which ones storage already holds.
internal sealed class ResumeStore(Destination destination)
{
    private readonly string path = StateFile.PathFor("uploads", $"{destination.ProjectId}-{destination.Version}.json");
    private readonly object gate = new();
    private Dictionary<string, MultipartUpload>? uploads;

    private Dictionary<string, MultipartUpload> Uploads => uploads ??= StateFile.Read<Dictionary<string, MultipartUpload>>(path) ?? [];

    // A file rewritten since its upload started, even at the same size, starts over: its stored
    // parts belong to the old bytes.
    public string? UploadIdFor(string key, FileInfo file)
    {
        lock (gate)
            return Uploads.TryGetValue(key, out var upload) && upload.Size == file.Length && upload.Modified == file.LastWriteTimeUtc
                ? upload.UploadId
                : null;
    }

    public void Start(string key, FileInfo file, string uploadId)
    {
        lock (gate)
        {
            Uploads[key] = new MultipartUpload { UploadId = uploadId, Size = file.Length, Modified = file.LastWriteTimeUtc };
            Save();
        }
    }

    public Dictionary<int, string> PartsOf(string key)
    {
        lock (gate) return new Dictionary<int, string>(Uploads[key].Parts);
    }

    public void CompletePart(string key, int number, string etag)
    {
        lock (gate)
        {
            Uploads[key].Parts[number] = etag;
            Save();
        }
    }

    public void Finish(string key)
    {
        lock (gate)
        {
            Uploads.Remove(key);
            Save();
        }
    }

    public void Clear()
    {
        lock (gate) File.Delete(path);
    }

    private void Save() => StateFile.Write(path, Uploads);
}
