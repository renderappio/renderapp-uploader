using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Renderapp.Uploader.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Opens_and_draws_with_publishing_waiting_for_a_rio()
    {
        var window = new MainWindow();
        window.Show();

        Assert.False(window.FindControl<MenuItem>("publishMenuItem")!.IsEnabled);
        Assert.False(window.FindControl<MenuItem>("cancelPublishMenuItem")!.IsEnabled);
        Assert.True(window.FindControl<MenuItem>("signOutMenuItem")!.IsEnabled);
        Assert.Equal("Ready", window.FindControl<TextBlock>("statusText")!.Text);

        // The theme painted: the status bar and the content area each got an opaque colour,
        // and not the same one.
        var frame = window.CaptureRenderedFrame()!;
        using var pixels = frame.Lock();
        int Pixel(int x, int y) => Marshal.ReadInt32(pixels.Address, y * pixels.RowBytes + x * 4);
        var statusBar = Pixel(frame.PixelSize.Width / 2, frame.PixelSize.Height - 4);
        var content = Pixel(frame.PixelSize.Width / 4, frame.PixelSize.Height / 4);
        Assert.NotEqual(0, statusBar >>> 24);
        Assert.NotEqual(0, content >>> 24);
        Assert.NotEqual(statusBar, content);
    }
}
