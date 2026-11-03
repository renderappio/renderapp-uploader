using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Renderapp.Uploader.Publish;
using Renderapp.Uploader.ViewModels;

namespace Renderapp.Uploader;

public partial class App : Application
{
    // RENDERAPP_URL points the app at another instance, a local server for one.
    private static readonly Uri Host = new(Environment.GetEnvironmentVariable("RENDERAPP_URL") ?? "https://beta.renderapp.io");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var publisher = new Publisher(http, Host, new RenderappSession(http, Host));
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(publisher) };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
