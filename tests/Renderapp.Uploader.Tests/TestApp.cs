using Avalonia;
using Avalonia.Headless;
using Renderapp.Uploader;
using Renderapp.Uploader.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Renderapp.Uploader.Tests;

// The real App on Avalonia's headless platform, drawing with Skia so frames can be captured.
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
