using Avalonia;
using System.Threading;

namespace renderappio_visualiser
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "renderappio-visualiser", out createdNew))
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
}
