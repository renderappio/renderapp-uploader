using Avalonia;
using System.Threading;

namespace Renderapp.Uploader;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "renderapp-uploader", out createdNew))
        {
            if (createdNew)
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
