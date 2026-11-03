using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json.Linq;
using Renderapp.Uploader.Publish;

namespace Renderapp.Uploader.ViewModels;

public enum Screen { Start, Ready, Publishing, Published }

public sealed partial class StepViewModel(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty] public partial bool IsCurrent { get; set; }

    [ObservableProperty] public partial bool IsDone { get; set; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int PreviewCount = 8;
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(5);

    private readonly IPublisher publisher;
    private readonly TimeProvider clock;
    private readonly Queue<(long At, long Bytes)> samples = new();
    private JObject? rio;
    private string? folder;
    private bool rebuilding;
    private Destination? published;

    internal MainViewModel(IPublisher publisher, TimeProvider? clock = null)
    {
        this.publisher = publisher;
        this.clock = clock ?? TimeProvider.System;
        Steps = [new("Sign in"), new("Choose where"), new("Upload")];
        IsSignedIn = publisher.IsSignedIn;
    }

    [ObservableProperty] public partial bool IsSignedIn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsReady), nameof(IsPublishing), nameof(IsPublished))]
    public partial Screen Screen { get; private set; } = Screen.Start;

    public bool IsStart => Screen == Screen.Start;
    public bool IsReady => Screen == Screen.Ready;
    public bool IsPublishing => Screen == Screen.Publishing;
    public bool IsPublished => Screen == Screen.Published;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    public partial string ProjectName { get; set; } = "";

    [ObservableProperty] public partial string VersionNote { get; set; } = "";

    [ObservableProperty] public partial string SourcePath { get; private set; } = "";

    [ObservableProperty] public partial string SummaryText { get; private set; } = "";

    [ObservableProperty] public partial string DetailText { get; private set; } = "";

    [ObservableProperty] public partial string? Warning { get; private set; }

    [ObservableProperty] public partial string? Error { get; private set; }

    [ObservableProperty] public partial bool CanChooseLayout { get; private set; }

    [ObservableProperty] public partial FolderLayout Layout { get; set; }

    [ObservableProperty] public partial ImportKind Kind { get; set; }

    [ObservableProperty] public partial bool Resumes { get; private set; }

    [ObservableProperty] public partial double Progress { get; private set; }

    [ObservableProperty] public partial string ProgressText { get; private set; } = "";

    [ObservableProperty] public partial string SpeedText { get; private set; } = "";

    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    [ObservableProperty] public partial string PublishedText { get; private set; } = "";

    public ObservableCollection<Bitmap> Previews { get; } = [];

    public ObservableCollection<StepViewModel> Steps { get; }

    public IReadOnlyList<FolderLayout> Layouts { get; } = [FolderLayout.FileNames, FolderLayout.Folders];

    public IReadOnlyList<ImportKind> Kinds { get; } = [ImportKind.Stills, ImportKind.Spin, ImportKind.StillsSequence];

    public Uri? PublishedUri => published is { } at ? new Uri(publisher.Host, $"/viewer/{at.ProjectId}/{at.Version}") : null;

    // A folder of images, or a .rio file.
    public async Task OpenAsync(string path)
    {
        Error = null;
        try
        {
            if (File.Exists(path)) await OpenRioAsync(path);
            else if (Directory.Exists(path)) await OpenFolderAsync(path);
            else Error = $"Couldn't find {path}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't read {Path.GetFileName(path)}: {error.Message}";
        }
    }

    private async Task OpenFolderAsync(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        rebuilding = true;
        Layout = await Task.Run(() => FolderImport.DetectLayout(path));
        Kind = await Task.Run(() => FolderImport.DetectKind(path, Layout));
        rebuilding = false;
        folder = path;
        CanChooseLayout = true;
        ProjectName = Path.GetFileName(path);
        await BuildAsync();
    }

    private async Task OpenRioAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        JObject loaded;
        try
        {
            loaded = JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            // Not JSON: an encrypted rio, which Renderapp decrypts for a signed-in person.
            StatusText = "This rio is encrypted. Sign in to Renderapp in your browser to open it.";
            try
            {
                loaded = JObject.Parse(await publisher.DecryptRioAsync(text, CancellationToken.None));
            }
            catch (Exception error) when (error is PublishException or HttpRequestException or Newtonsoft.Json.JsonReaderException)
            {
                StatusText = "";
                Error = $"Couldn't open {Path.GetFileName(path)}: {error.Message}";
                return;
            }
            finally
            {
                IsSignedIn = publisher.IsSignedIn;
            }
            StatusText = "";
        }
        folder = null;
        CanChooseLayout = false;
        ProjectName = Path.GetFileNameWithoutExtension(path);
        await ShowAsync(loaded);
    }

    // The folder chooser may also be needed for a rio whose files aren't where it says.
    public async Task SetSourceFolderAsync(string path)
    {
        if (rio == null) return;
        rio["CacheDir"] = Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
        await ShowAsync(rio);
    }

    partial void OnLayoutChanged(FolderLayout value) => Rebuild();

    partial void OnKindChanged(ImportKind value) => Rebuild();

    private void Rebuild()
    {
        if (!rebuilding && folder != null && Screen == Screen.Ready) _ = BuildAsync();
    }

    private async Task BuildAsync()
    {
        var (path, layout, kind) = (folder!, Layout, Kind);
        await ShowAsync(await Task.Run(() => FolderImport.Build(path, layout, kind)));
    }

    private async Task ShowAsync(JObject loaded)
    {
        rio = loaded;
        SourcePath = loaded["CacheDir"]?.Value<string>() ?? "";
        var summary = await Task.Run(() => FolderImport.Summarise(loaded));
        SummaryText = summary.Images == 1 ? $"1 image · {Bytes(summary.Bytes)}" : $"{summary.Images:N0} images · {Bytes(summary.Bytes)}";
        DetailText = Detail(summary);
        Warning = summary switch
        {
            { Images: 0 } => "No PNG or JPEG images here to publish.",
            { Missing: > 0 } => $"{summary.Missing:N0} of the files this project lists aren't in this folder.",
            _ => null,
        };
        Resumes = SourcePath.Length > 0 && publisher.HasUnfinished(SourcePath);
        Screen = Screen.Ready;
        PublishCommand.NotifyCanExecuteChanged();
        await LoadPreviewsAsync(loaded);
    }

    private static string Detail(ProjectSummary summary)
    {
        var parts = new List<string>();
        if (summary.Options > 0) parts.Add(Plural(summary.Options, "option") + " in " + Plural(summary.Categories, "category", "categories"));
        if (summary.Frames > 0) parts.Add(Plural(summary.Frames, "frame"));
        return string.Join(" · ", parts);
    }

    private async Task LoadPreviewsAsync(JObject loaded)
    {
        foreach (var preview in Previews) preview.Dispose();
        Previews.Clear();
        foreach (var file in FolderImport.PreviewFiles(loaded, PreviewCount))
        {
            var bitmap = await Task.Run(() => DecodePreview(file));
            if (!ReferenceEquals(rio, loaded)) { bitmap?.Dispose(); return; }
            if (bitmap != null) Previews.Add(bitmap);
        }
    }

    // A preview that can't be decoded is left out rather than failing the screen.
    private static Bitmap? DecodePreview(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 320);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool CanPublish() => rio != null && Screen == Screen.Ready && ProjectName.Trim().Length > 0 && Warning == null;

    [RelayCommand(CanExecute = nameof(CanPublish), IncludeCancelCommand = true)]
    private async Task PublishAsync(CancellationToken cancellation)
    {
        Error = null;
        samples.Clear();
        Progress = 0;
        ProgressText = "";
        SpeedText = "";
        foreach (var step in Steps) step.IsCurrent = step.IsDone = false;
        Screen = Screen.Publishing;
        var progress = new Progress<UploadProgress>(Report);
        try
        {
            published = await publisher.PublishAsync(rio!, ProjectName.Trim(), VersionNote.Trim(), Enter, progress, cancellation);
            foreach (var step in Steps) { step.IsDone = true; step.IsCurrent = false; }
            PublishedText = $"{ProjectName.Trim()}, version {published.Version}";
            OnPropertyChanged(nameof(PublishedUri));
            Screen = Screen.Published;
        }
        catch (OperationCanceledException)
        {
            Stop("Publishing stopped. Publish again to carry on where it left off.");
        }
        catch (PublishException error)
        {
            Stop(error.Message);
        }
        catch (HttpRequestException)
        {
            Stop("Couldn't reach Renderapp. Publish again to carry on where it left off.");
        }
        // This runs from a button, where an unhandled error would close the app.
        catch (Exception error)
        {
            Trace.TraceError(error.ToString());
            Stop($"Publishing failed: {error.Message}");
        }
        finally
        {
            IsSignedIn = publisher.IsSignedIn;
        }
    }

    private void Stop(string message)
    {
        Error = message;
        Resumes = SourcePath.Length > 0 && publisher.HasUnfinished(SourcePath);
        Screen = Screen.Ready;
        PublishCommand.NotifyCanExecuteChanged();
    }

    private void Enter(PublishStage stage)
    {
        var index = (int)stage;
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsDone = i < index;
            Steps[i].IsCurrent = i == index;
        }
        StatusText = stage switch
        {
            PublishStage.SigningIn => "Signing in. If your browser opens, sign in to Renderapp there.",
            PublishStage.ChoosingDestination => "Choose where this goes in your browser.",
            _ => "Uploading the original files. Renderapp prepares them for viewing once they're in.",
        };
    }

    private void Report(UploadProgress p)
    {
        Progress = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
        ProgressText = $"{p.FilesDone:N0} of {Plural(p.FilesTotal, "file")} · {Bytes(p.BytesDone)} of {Bytes(p.BytesTotal)}";

        var now = clock.GetTimestamp();
        samples.Enqueue((now, p.BytesDone));
        while (samples.Count > 2 && clock.GetElapsedTime(samples.Peek().At, now) > SpeedWindow) samples.Dequeue();
        var (firstAt, firstBytes) = samples.Peek();
        var seconds = clock.GetElapsedTime(firstAt, now).TotalSeconds;
        if (seconds < 1 || p.BytesDone <= firstBytes) return;
        var perSecond = (p.BytesDone - firstBytes) / seconds;
        var left = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / perSecond);
        SpeedText = $"{Bytes((long)perSecond)}/s · {Remaining(left)}";
    }

    [RelayCommand]
    private void StartOver()
    {
        foreach (var preview in Previews) preview.Dispose();
        Previews.Clear();
        rio = null;
        folder = null;
        published = null;
        Error = null;
        Warning = null;
        VersionNote = "";
        Screen = Screen.Start;
    }

    [RelayCommand]
    private void SignOut()
    {
        publisher.SignOut();
        IsSignedIn = false;
        StatusText = "Signed out of Renderapp. Publishing asks you to sign in again.";
    }

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    internal static string Remaining(TimeSpan left) => left switch
    {
        { TotalSeconds: < 60 } => "less than a minute left",
        { TotalMinutes: < 90 } => $"about {Math.Ceiling(left.TotalMinutes):0} min left",
        _ => $"about {left.TotalHours:0.#} h left",
    };

    private static string Plural(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count:N0} {many ?? one + "s"}";
}
