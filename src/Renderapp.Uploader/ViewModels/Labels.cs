using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Renderapp.Uploader.ViewModels;

// The words the window shows for each choice.
public sealed class Labels : IValueConverter
{
    public static readonly Labels Instance = new();

    public static readonly IValueConverter Weight =
        new FuncValueConverter<bool, FontWeight>(current => current ? FontWeight.SemiBold : FontWeight.Normal);

    public static readonly IValueConverter Any = new FuncValueConverter<int, bool>(count => count > 0);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FolderLayout.FileNames => "Parts of each file name",
        FolderLayout.Folders => "Folders and file names",
        ImportKind.Stills => "Stills",
        ImportKind.Spin => "Spin (numbered frames)",
        ImportKind.StillsSequence => "Stills sequence (numbered views)",
        _ => value?.ToString(),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
