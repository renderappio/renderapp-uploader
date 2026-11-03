using Newtonsoft.Json.Linq;

namespace Renderapp.Uploader;

// How a folder's images become options: from the parts of each file name, or from the folders
// they sit in.
public enum FolderLayout { FileNames, Folders }

// The kind of view the images make. The numbers are LoadFromFolder's types.
public enum ImportKind { Stills = 0, Spin = 1, StillsSequence = 2 }

internal sealed record ProjectSummary(int Images, long Bytes, int Categories, int Options, int Frames, int Missing);

internal static class FolderImport
{
    // The extensions LoadFromFolder picks up, matched the same way.
    private static bool IsImage(string path) => path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg");

    public static FolderLayout DetectLayout(string folder) =>
        Directory.EnumerateDirectories(folder).Any(sub => Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Any(IsImage))
            ? FolderLayout.Folders
            : FolderLayout.FileNames;

    // A sequence numbers its frames in the last part of each name: car_red_001.png, or
    // paint/red/001.png.
    public static ImportKind DetectKind(string folder, FolderLayout layout)
    {
        var search = layout == FolderLayout.Folders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var lastParts = Directory.EnumerateFiles(folder, "*", search)
            .Where(IsImage)
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Select(name => layout == FolderLayout.Folders ? name : name.Split('_')[^1])
            .ToList();
        var numbered = lastParts.Count >= 2 && lastParts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
        return numbered && lastParts.Distinct().Count() >= 2 ? ImportKind.Spin : ImportKind.Stills;
    }

    // LoadFromFolder keeps its working state in statics, and a cancelled build can still be
    // running when the next one starts.
    private static readonly Lock Building = new();

    public static JObject Build(string folder, FolderLayout layout, ImportKind kind)
    {
        lock (Building)
        {
            return layout == FolderLayout.Folders
                ? LoadFromFolder.LoadDirectory(folder, (int)kind)
                : LoadFromFolder.LoadFilenames(folder, (int)kind);
        }
    }

    public static ProjectSummary Summarise(JObject rio)
    {
        var cacheDir = rio["CacheDir"]?.Value<string>() ?? "";
        var files = (rio["Maps"] as JArray ?? [])
            .Select(map => map["filename"]?.Value<string>())
            .OfType<string>()
            .Distinct()
            .Select(name => new FileInfo(Path.Combine(cacheDir, name)))
            .ToList();
        var inputs = (rio["Inputs"] as JArray ?? []).OfType<JObject>().ToList();
        var views = inputs.Where(input => (string?)input["Group"] == "+VEW").ToList();
        var categories = inputs.Except(views).ToList();
        return new ProjectSummary(
            Images: files.Count,
            Bytes: files.Where(file => file.Exists).Sum(file => file.Length),
            Categories: categories.Count,
            Options: categories.Sum(input => (input["Options"] as JArray)?.Count ?? 0),
            Frames: views.SelectMany(view => view["Options"] as JArray ?? []).Sum(option => (option["Frames"] as JArray)?.Count ?? 0),
            Missing: files.Count(file => !file.Exists));
    }

    public static IEnumerable<string> PreviewFiles(JObject rio, int count)
    {
        var cacheDir = rio["CacheDir"]?.Value<string>() ?? "";
        return (rio["Maps"] as JArray ?? [])
            .Select(map => map["filename"]?.Value<string>())
            .OfType<string>()
            .Distinct()
            .Where(IsImage)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(name => Path.Combine(cacheDir, name));
    }
}
