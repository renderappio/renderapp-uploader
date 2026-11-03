using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Retry;

namespace Renderapp.Uploader.Publish;

internal sealed record UploadProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

// Uploads a rio's base files into a version Renderapp has reserved, then finishes the version;
// the server makes every rendition. Each S3 request is signed by the API for one key inside the
// version's upload prefix, so no storage credential reaches this machine.
internal sealed class BaseFileUploader
{
    // src/shared/uploadLimits.ts: the API refuses a single PUT above this.
    private const long MultipartThreshold = 50L * 1024 * 1024;
    // S3 limits, and the same part sizing as the web uploader (features/upload/lib/etag.ts), so
    // either can recognise a copy the other stored.
    private const long MinPart = 5L * 1024 * 1024;
    private const int MaxParts = 10_000;
    private const int Concurrency = 6;

    private readonly HttpClient http;
    private readonly Uri api;
    private readonly RenderappSession session;
    private readonly SemaphoreSlim slots = new(Concurrency);
    private readonly ResiliencePipeline retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            // HttpClient's own timeout surfaces as a TaskCanceledException over a TimeoutException.
            ShouldHandle = new PredicateBuilder()
                .Handle<HttpRequestException>()
                .Handle<TransientException>()
                .Handle<TaskCanceledException>(error => error.InnerException is TimeoutException),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            MaxRetryAttempts = 5,
            Delay = TimeSpan.FromSeconds(1),
        })
        .Build();

    public BaseFileUploader(HttpClient http, Uri host, RenderappSession session)
    {
        this.http = http;
        api = new Uri(host, "/api/v1/");
        this.session = session;
    }

    public async Task PublishAsync(JObject rio, Destination destination, string? description,
        IProgress<UploadProgress> progress, CancellationToken cancellation)
    {
        const string carryOn = "Publish again to carry on where it stopped.";
        try
        {
            await UploadAndFinishAsync(rio, destination, description, progress, cancellation);
        }
        catch (Exception error) when (error is TransientException or HttpRequestException
            || error is TaskCanceledException { InnerException: TimeoutException })
        {
            throw new PublishException($"Renderapp or its storage kept failing. {carryOn}");
        }
        catch (UploadGoneException)
        {
            throw new PublishException($"Storage lost part of an upload. {carryOn}");
        }
        catch (IOException error)
        {
            throw new PublishException($"Couldn't read a file to upload: {error.Message}");
        }
    }

    private async Task UploadAndFinishAsync(JObject rio, Destination destination, string? description,
        IProgress<UploadProgress> progress, CancellationToken cancellation)
    {
        var cacheDir = rio["CacheDir"]?.Value<string>() ?? throw new PublishException("The rio names no CacheDir.");
        var paths = (rio["Maps"] as JArray ?? [])
            .Select(map => map["filename"]?.Value<string>())
            .OfType<string>()
            .Select(path => path.Replace('\\', '/'))
            .Distinct()
            .ToList();
        var files = paths.Select(path => new FileInfo(Path.Combine(cacheDir, path))).ToList();
        var missing = paths.Where((_, i) => !files[i].Exists).ToList();
        if (missing.Count > 0)
            throw new PublishException($"{missing.Count} files are missing from {cacheDir}, starting with {missing[0]}.");

        var stored = await StoredCopiesAsync(destination, cancellation);
        var resume = new ResumeStore(destination);
        long total = files.Sum(file => file.Length);
        long done = 0;
        int filesDone = 0;
        var uploaded = new List<string>();
        void Advance(long bytes) => progress.Report(new UploadProgress(Interlocked.Add(ref done, bytes), total, filesDone, files.Count));

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellation },
            async (i, token) =>
            {
                var (path, file) = (paths[i], files[i]);
                if (stored.TryGetValue(path, out var copy) && copy.Size == file.Length && copy.ETag == await ETagAsync(file, copy.ETag, token))
                {
                    Advance(file.Length);
                }
                else
                {
                    var key = $"{destination.ProjectId}/{destination.Version}/{destination.ProjectId}_{destination.Version}/{path}";
                    if (file.Length > MultipartThreshold)
                    {
                        long sent = 0;
                        void Count(long bytes) { Interlocked.Add(ref sent, bytes); Advance(bytes); }
                        var resuming = resume.UploadIdFor(key, file) != null;
                        try
                        {
                            await MultipartAsync(key, file, resume, Count, token);
                        }
                        catch (UploadGoneException) when (resuming)
                        {
                            resume.Finish(key);
                            Advance(-Interlocked.Exchange(ref sent, 0));
                            await MultipartAsync(key, file, resume, Advance, token);
                        }
                    }
                    else await PutAsync(key, file, Advance, token);
                    lock (uploaded) uploaded.Add(path);
                }
                Interlocked.Increment(ref filesDone);
                Advance(0);
            });

        var body = new JObject
        {
            ["rio"] = WithView(rio),
            ["version_size"] = total,
            ["cache_dir"] = cacheDir,
            ["uploaded_paths"] = new JArray(uploaded),
        };
        body["version_description"] = description ?? "";
        await ApiAsync(HttpMethod.Put, $"projects/{destination.ProjectId}/{destination.Version}/upload-complete", body, cancellation);
        resume.Clear();
    }

    // The API refuses a rio whose options are all NotAView. Like the web uploader's
    // ensureStillView (src/shared/RioFile.ts), give such a rio a hidden Still view.
    private static JObject WithView(JObject rio)
    {
        var copy = (JObject)rio.DeepClone();
        if (copy["Inputs"] is not JArray inputs || inputs.Count == 0) return copy;
        var hasView = inputs.SelectMany(input => input["Options"] as JArray ?? [])
            .Any(option => option["Type"]?.Value<string>() != "NotAView");
        if (hasView) return copy;
        inputs.Add(new JObject
        {
            ["Type"] = "Radio",
            ["Name"] = "View",
            ["Group"] = "+VEW",
            ["Visibility"] = false,
            ["Effect"] = "configuration",
            ["Options"] = new JArray(new JObject
            {
                ["Name"] = "Still",
                ["Type"] = "Still",
                ["FrontEndRule"] = "+STL",
                ["BackEndRule"] = "+STL",
                ["Defaults"] = new JArray(),
                ["Conditions"] = new JArray(),
                ["Visibility"] = false,
            }),
        });
        return copy;
    }

    private async Task PutAsync(string key, FileInfo file, Action<long> advance, CancellationToken cancellation)
    {
        var type = ContentType(file.Name);
        await retry.ExecuteAsync(async token =>
        {
            var url = await SignAsync(new JObject { ["operation"] = "put-object", ["key"] = key, ["content_type"] = type, ["content_length"] = file.Length }, token);
            await using var stream = file.OpenRead();
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            content.Headers.ContentLength = file.Length;
            await SendAsync(new HttpRequestMessage(HttpMethod.Put, url) { Content = content }, token);
        }, cancellation);
        advance(file.Length);
    }

    private async Task MultipartAsync(string key, FileInfo file, ResumeStore resume, Action<long> advance, CancellationToken cancellation)
    {
        var partSize = Math.Max(MinPart, (file.Length + MaxParts - 1) / MaxParts);
        var partCount = (int)((file.Length + partSize - 1) / partSize);
        var uploadId = resume.UploadIdFor(key, file);
        if (uploadId == null)
        {
            var type = ContentType(file.Name);
            uploadId = await retry.ExecuteAsync(async token =>
            {
                var url = await SignAsync(new JObject { ["operation"] = "create-multipart-upload", ["key"] = key, ["content_type"] = type }, token);
                var reply = await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = Typed(type) }, token);
                return ParseXml(reply).Descendants().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value
                    ?? throw new TransientException();
            }, cancellation);
            resume.Start(key, file, uploadId);
        }
        var done = resume.PartsOf(key);
        foreach (var part in done.Keys) advance(Math.Min(partSize, file.Length - (part - 1) * partSize));

        await Parallel.ForEachAsync(Enumerable.Range(1, partCount).Where(n => !done.ContainsKey(n)).ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellation },
            async (number, token) =>
            {
                var offset = (number - 1) * partSize;
                var length = Math.Min(partSize, file.Length - offset);
                var etag = await retry.ExecuteAsync(async inner =>
                {
                    var url = await SignAsync(new JObject { ["operation"] = "upload-part", ["key"] = key, ["upload_id"] = uploadId, ["part_number"] = number }, inner);
                    return await SendPartAsync(url, file, offset, length, inner);
                }, token);
                resume.CompletePart(key, number, etag);
                advance(length);
            });

        var complete = new XElement("CompleteMultipartUpload",
            resume.PartsOf(key).OrderBy(p => p.Key).Select(p => new XElement("Part", new XElement("PartNumber", p.Key), new XElement("ETag", p.Value))));
        await retry.ExecuteAsync(async token =>
        {
            var url = await SignAsync(new JObject { ["operation"] = "complete-multipart-upload", ["key"] = key, ["upload_id"] = uploadId }, token);
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(complete.ToString(), Encoding.UTF8, "application/xml") }, token);
        }, cancellation);
        resume.Finish(key);
    }

    private async Task<Dictionary<string, (long Size, string? ETag)>> StoredCopiesAsync(Destination destination, CancellationToken cancellation)
    {
        var listing = await ApiAsync(HttpMethod.Get, $"files/{destination.ProjectId}/{destination.Version}", null, cancellation);
        return (listing["files"] as JArray ?? [])
            .Where(f => f["path"] != null)
            .ToDictionary(f => f["path"]!.Value<string>()!, f => (f["size"]!.Value<long>(), f["etag"]?.Value<string>()));
    }

    // An S3 ETag: the MD5 of the bytes, or for a multipart object the MD5 of the part MD5s and
    // the part count. Hashing only happens for a stored copy of the same size.
    private static async Task<string?> ETagAsync(FileInfo file, string? stored, CancellationToken cancellation)
    {
        if (stored == null) return null;
        await using var stream = file.OpenRead();
        var dash = stored.IndexOf('-');
        if (dash < 0) return Convert.ToHexString(await MD5.HashDataAsync(stream, cancellation)).ToLowerInvariant();
        if (!int.TryParse(stored[(dash + 1)..], out var parts)) return null;
        var partSize = Math.Max(MinPart, (file.Length + MaxParts - 1) / MaxParts);
        if ((file.Length + partSize - 1) / partSize != parts) return null;
        var digests = new byte[parts * 16];
        var buffer = new byte[partSize];
        for (var i = 0; i < parts; i++)
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellation);
            MD5.HashData(buffer.AsSpan(0, read)).CopyTo(digests, i * 16);
        }
        return $"{Convert.ToHexString(MD5.HashData(digests)).ToLowerInvariant()}-{parts}";
    }

    private async Task<string> SignAsync(JObject request, CancellationToken cancellation)
    {
        var (projectId, version) = KeyOwner(request["key"]!.Value<string>()!);
        var reply = await ApiAsync(HttpMethod.Post, $"files/{projectId}/{version}/sign-s3-request", request, cancellation);
        return reply["url"]!.Value<string>()!;
    }

    private static (string, string) KeyOwner(string key)
    {
        var segments = key.Split('/');
        return (segments[0], segments[1]);
    }

    private async Task<JObject> ApiAsync(HttpMethod method, string path, JObject? body, CancellationToken cancellation)
    {
        await slots.WaitAsync(cancellation);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(api, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.AccessTokenAsync(cancellation));
            if (body != null) request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellation);
            var text = await response.Content.ReadAsStringAsync(cancellation);
            if (IsTransient(response.StatusCode)) throw new TransientException();
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.StartsWith('{') ? JObject.Parse(text)["detail"]?.Value<string>() : null;
                throw new PublishException(detail ?? $"Renderapp answered {(int)response.StatusCode} to {path}.", response.StatusCode);
            }
            return text.Length == 0 ? [] : JObject.Parse(text);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        await slots.WaitAsync(cancellation);
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, cancellation))
            {
                var text = await response.Content.ReadAsStringAsync(cancellation);
                if (IsTransient(response.StatusCode)) throw new TransientException();
                if (response.StatusCode == HttpStatusCode.NotFound) throw new UploadGoneException();
                if (!response.IsSuccessStatusCode) throw new PublishException($"Storage refused an upload ({(int)response.StatusCode}).");
                // S3 can report a failed CompleteMultipartUpload in a 200 body.
                if (text.Contains("<Error>")) throw new TransientException();
                return text;
            }
        }
        finally
        {
            slots.Release();
        }
    }

    // The part is read only once its slot is free, so memory holds at most one part per slot.
    private async Task<string> SendPartAsync(string url, FileInfo file, long offset, long length, CancellationToken cancellation)
    {
        await slots.WaitAsync(cancellation);
        try
        {
            var bytes = new byte[length];
            await using (var stream = file.OpenRead())
            {
                stream.Position = offset;
                await stream.ReadExactlyAsync(bytes, cancellation);
            }
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
            using var response = await http.SendAsync(request, cancellation);
            if (IsTransient(response.StatusCode)) throw new TransientException();
            if (response.StatusCode == HttpStatusCode.NotFound) throw new UploadGoneException();
            if (!response.IsSuccessStatusCode) throw new PublishException($"Storage refused an upload part ({(int)response.StatusCode}).");
            return response.Headers.ETag?.Tag ?? throw new PublishException("Storage returned no ETag for a part.");
        }
        finally
        {
            slots.Release();
        }
    }

    private static XDocument ParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            throw new TransientException();
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static ByteArrayContent Typed(string type)
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".exr" => "image/x-exr",
        ".tif" or ".tiff" => "image/tiff",
        ".mp4" => "video/mp4",
        ".glb" => "model/gltf-binary",
        _ => "application/octet-stream",
    };

    private sealed class TransientException : Exception;

    // S3 no longer knows a multipart upload a resume recorded (it was aborted or expired).
    private sealed class UploadGoneException : Exception;
}
