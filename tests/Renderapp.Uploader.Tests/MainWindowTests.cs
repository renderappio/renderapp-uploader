using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Renderapp.Uploader.ViewModels;
using Xunit;

namespace Renderapp.Uploader.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Opens_on_the_drop_zone_and_draws_in_the_theme()
    {
        var window = new MainWindow { DataContext = new MainViewModel(new FakePublisher()) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<Border>("DropZone")!.IsVisible);
        Assert.False(window.FindControl<Grid>("ReadyView")!.IsVisible);
        Assert.False(window.FindControl<Border>("PublishingView")!.IsVisible);
        Assert.False(window.FindControl<Border>("PublishedView")!.IsVisible);

        // The theme painted: the drop zone and the window background each got an opaque colour,
        // and not the same one.
        var frame = window.CaptureRenderedFrame()!;
        using var pixels = frame.Lock();
        int Pixel(int x, int y) => Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4);
        var background = Pixel(6, frame.PixelSize.Height / 2);
        var dropZone = Pixel(frame.PixelSize.Width / 2, frame.PixelSize.Height / 2 + 160);
        Assert.NotEqual(0, background >>> 24);
        Assert.NotEqual(0, dropZone >>> 24);
        Assert.NotEqual(background, dropZone);
    }
}
