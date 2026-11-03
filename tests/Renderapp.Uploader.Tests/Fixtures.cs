using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Renderapp.Uploader.Tests;

// Folders of real, small images. Must run on Avalonia's thread, since it draws them.
internal sealed class Fixtures : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("renderapp-uploader-ui-").FullName;

    public string Folder(params string[] files)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        for (var i = 0; i < files.Length; i++)
        {
            var path = Path.Combine(folder, files[i]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Image(path, HsvColor.ToRgb(i * 360.0 / files.Length % 360, 0.55, 0.85));
        }
        return folder;
    }

    private static void Image(string path, Color color)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 40));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(color, 0), new GradientStop(Colors.Black, 1) },
            }, new Rect(0, 0, 64, 40));
        }
        using var file = File.Create(path);
        bitmap.Save(file, new PngBitmapEncoderOptions());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
