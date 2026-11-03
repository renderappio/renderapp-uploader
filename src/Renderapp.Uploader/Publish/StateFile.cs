using System.Text.Json;

namespace renderappio_visualiser.Publish;

// The app's small JSON files under LocalApplicationData/Renderappio. Writes land whole or not at
// all, and a file that can't be read counts as absent rather than blocking every later publish.
internal static class StateFile
{
    public static string PathFor(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Renderappio", .. parts]);

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value, bool ownerOnly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (ownerOnly && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
            JsonSerializer.Serialize(stream, value);
        File.Move(temporary, path, overwrite: true);
    }
}
