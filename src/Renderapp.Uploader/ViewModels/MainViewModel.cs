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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial bool IsDone { get; set; }

    // The window shows a step's state only as colour, so screen readers get it spelled out.
    public string Description => $"{Label}, {(IsDone ? "done" : IsCurrent ? "in progress" : "not started")}";
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
    private bool applying;
    private (FolderLayout Layout, ImportKind Kind) shown;
    private int generation;
    private CancellationTokenSource? loading;
    private Destination? published;

    internal MainViewModel(IPublisher publisher, TimeProvider? clock = null)
    {
        this.publisher = publisher;
        this.clock = clock ?? TimeProvider.System;
        Steps = [new("Sign in"), new("Choose where"), new("Upload")];
        IsSignedIn = publisher.IsSignedIn;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignOut))]
    public partial bool IsSignedIn { get; private set; }

    public bool CanSignOut => IsSignedIn && !IsPublishing;

    // Opening is only for the drop zone and the summary, never mid-publish or once done.
    public bool CanOpen => Screen is Screen.Start or Screen.Ready;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string BusyText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsReady), nameof(IsPublishing), nameof(IsPublished), nameof(CanOpen), nameof(CanSignOut))]
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
    public Task OpenAsync(string path)
    {
        if (!CanOpen) return Task.CompletedTask;
        if (Directory.Exists(path)) return LoadAsync("Reading the folder…", (mine, cancel) => OpenFolderAsync(path, mine, cancel));
        if (!File.Exists(path))
        {
            Error = $"Couldn't find {path}.";
            return Task.CompletedTask;
        }
        if (!path.EndsWith(".rio", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Drop the folder your renders are in, or a .rio file.";
            return Task.CompletedTask;
        }
        return LoadAsync("Opening the project…", (mine, cancel) => OpenRioAsync(path, mine, cancel));
    }

    // Opens and rebuilds run one at a time: each takes a generation, a newer one cancels the
    // last, and only the latest may change what the window shows.
    private async Task LoadAsync(string busyText, Func<int, CancellationToken, Task> work)
    {
        loading?.Cancel();
        var cancel = new CancellationTokenSource();
        loading = cancel;
        var mine = ++generation;
        Error = null;
        BusyText = busyText;
        IsBusy = true;
        try
        {
            await work(mine, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        // This runs from a click or a drop, where an unhandled error would close the app.
        catch (Exception error)
        {
            Trace.TraceError(error.ToString());
            if (mine == generation) Error = error is IOException or UnauthorizedAccessException
                ? $"Couldn't read that: {error.Message}"
                : $"Couldn't open that: {error.Message}";
        }
        finally
        {
            if (mine == generation)
            {
                IsBusy = false;
                BusyText = "";
                IsSignedIn = publisher.IsSignedIn;
            }
        }
    }

    // Work already running on the thread pool can't be stopped, so cancelling also retires its
    // generation and whatever it returns is dropped.
    [RelayCommand]
    private void CancelOpen()
    {
        loading?.Cancel();
        generation++;
        IsBusy = false;
        BusyText = "";
        // A cancelled rebuild leaves the pickers on a choice the shown build doesn't match.
        Choose(shown.Layout, shown.Kind);
    }

    private void Choose(FolderLayout layout, ImportKind kind)
    {
        applying = true;
        Layout = layout;
        Kind = kind;
        applying = false;
    }

    private async Task OpenFolderAsync(string path, int mine, CancellationToken cancel)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        var layout = await Task.Run(() => FolderImport.DetectLayout(path), cancel);
        var kind = await Task.Run(() => FolderImport.DetectKind(path, layout), cancel);
        var loaded = await Task.Run(() => FolderImport.Build(path, layout, kind), cancel);
        await ShowAsync(loaded, mine, cancel, () =>
        {
            Choose(layout, kind);
            shown = (layout, kind);
            folder = path;
            CanChooseLayout = true;
            ProjectName = Path.GetFileName(path);
        });
    }

    private async Task OpenRioAsync(string path, int mine, CancellationToken cancel)
    {
        var text = await File.ReadAllTextAsync(path, cancel);
        JObject loaded;
        try
        {
            loaded = JObject.Parse(text);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            // Not JSON: an encrypted rio, which Renderapp decrypts for a signed-in person.
            BusyText = "This project is encrypted. Sign in to Renderapp in your browser to open it.";
            loaded = JObject.Parse(await publisher.DecryptRioAsync(text, cancel));
        }
        await ShowAsync(loaded, mine, cancel, () =>
        {
            folder = null;
            CanChooseLayout = false;
            ProjectName = Path.GetFileNameWithoutExtension(path);
        });
    }

    // The folder chooser may also be needed for a rio whose files aren't where it says.
    public Task SetSourceFolderAsync(string path)
    {
        if (rio is not { } current || !CanOpen) return Task.CompletedTask;
        // A folder project has no file list of its own to re-point, so the chosen folder is opened afresh.
        if (folder != null) return OpenAsync(path);
        return LoadAsync("Reading the folder…", (mine, cancel) =>
        {
            var moved = (JObject)current.DeepClone();
            moved["CacheDir"] = Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
            return ShowAsync(moved, mine, cancel);
        });
    }

    partial void OnLayoutChanged(FolderLayout value) => Rebuild();

    partial void OnKindChanged(ImportKind value) => Rebuild();

    private void Rebuild()
    {
        if (applying || folder is not { } path || Screen != Screen.Ready) return;
        var (layout, kind) = (Layout, Kind);
        _ = LoadAsync("Reading the folder…", async (mine, cancel) =>
            await ShowAsync(await Task.Run(() => FolderImport.Build(path, layout, kind), cancel), mine, cancel, () => shown = (layout, kind)));
    }

    // Nothing is applied until the summary is in, so a superseded load changes nothing.
    private async Task ShowAsync(JObject loaded, int mine, CancellationToken cancel, Action? apply = null)
    {
        var summary = await Task.Run(() => FolderImport.Summarise(loaded), cancel);
        if (mine != generation) return;
        apply?.Invoke();
        rio = loaded;
        SourcePath = loaded["CacheDir"]?.Value<string>() ?? "";
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
        await LoadPreviewsAsync(loaded, mine, cancel);
    }

    private static string Detail(ProjectSummary summary)
    {
        var parts = new List<string>();
        if (summary.Options > 0) parts.Add(Plural(summary.Options, "option") + " in " + Plural(summary.Categories, "category", "categories"));
        if (summary.Frames > 0) parts.Add(Plural(summary.Frames, "frame"));
        return string.Join(" · ", parts);
    }

    private async Task LoadPreviewsAsync(JObject loaded, int mine, CancellationToken cancel)
    {
        ClearPreviews();
        foreach (var file in FolderImport.PreviewFiles(loaded, PreviewCount))
        {
            var bitmap = await Task.Run(() => DecodePreview(file), cancel);
            if (mine != generation) { bitmap?.Dispose(); return; }
            if (bitmap != null) Previews.Add(bitmap);
        }
    }

    private void ClearPreviews()
    {
        foreach (var preview in Previews) preview.Dispose();
        Previews.Clear();
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

    private bool CanPublish() => rio != null && Screen == Screen.Ready && !IsBusy && ProjectName.Trim().Length > 0 && Warning == null;

    [RelayCommand(CanExecute = nameof(CanPublish), IncludeCancelCommand = true)]
    private async Task PublishAsync(CancellationToken cancellation)
    {
        loading?.Cancel();
        generation++;
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
            StatusText = "";
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
        StatusText = "";
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
            _ => "Uploading the lossless files. Renderapp prepares them for viewing once they're in.",
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
        loading?.Cancel();
        generation++;
        IsBusy = false;
        ClearPreviews();
        StatusText = "";
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
