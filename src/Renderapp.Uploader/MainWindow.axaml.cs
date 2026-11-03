using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Renderapp.Uploader.ViewModels;

namespace Renderapp.Uploader;

// The window's own jobs: the platform pickers, drag and drop, and opening links. Everything else
// is the view model's.
public partial class MainWindow : Window
{
    private static readonly Uri Help = new("https://discord.gg/s8BSKwuw");

    public MainWindow()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragEnterEvent, (_, e) => SetDropping(e, true));
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) => SetDropping(e, true));
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, e) => SetDropping(e, false));
        DropZone.AddHandler(DragDrop.DropEvent, Dropped);
    }

    private MainViewModel Model => (MainViewModel)DataContext!;

    private void SetDropping(DragEventArgs e, bool over)
    {
        var files = over && e.DataTransfer.TryGetFiles() is { Length: > 0 };
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("over", files);
    }

    private async void Dropped(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("over", false);
        if (e.DataTransfer.TryGetFiles() is [var first, ..] && first.TryGetLocalPath() is { } path)
            await Model.OpenAsync(path);
    }

    private async void ChooseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder of renders" });
        if (folders is [var folder, ..] && folder.TryGetLocalPath() is { } path) await Model.OpenAsync(path);
    }

    private async void OpenRio(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a .rio file",
            FileTypeFilter = [new FilePickerFileType("Renderapp project") { Patterns = ["*.rio"] }, FilePickerFileTypes.All],
        });
        if (files is [var file, ..] && file.TryGetLocalPath() is { } path) await Model.OpenAsync(path);
    }

    private async void FindFiles(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the folder holding this project's files" });
        if (folders is [var folder, ..] && folder.TryGetLocalPath() is { } path) await Model.SetSourceFolderAsync(path);
    }

    private async void OpenInRenderapp(object? sender, RoutedEventArgs e)
    {
        if (Model.PublishedUri is { } uri) await Launcher.LaunchUriAsync(uri);
    }

    private async void GetHelp(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(Help);
}
