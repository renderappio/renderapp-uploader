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

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
    }
}
