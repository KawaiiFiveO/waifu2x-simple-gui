using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Waifu2xSimpleGui.Core.Media;

namespace Waifu2xSimpleGui.App.Services;

public sealed class FileDialogs(TopLevel topLevel) : IFileDialogs
{
    private static readonly FilePickerFileType Media = new("Images, GIFs and videos")
    {
        Patterns = [.. MediaClassifier.Extensions.Select(e => "*" + e)],
    };

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files",
            AllowMultiple = true,
            FileTypeFilter = [Media, FilePickerFileTypes.All],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
