namespace Waifu2xSimpleGui.App.Services;

/// <summary>Native pickers, abstracted so view models stay UI-framework free and testable.</summary>
public interface IFileDialogs
{
    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickFolderAsync(string title);
}
