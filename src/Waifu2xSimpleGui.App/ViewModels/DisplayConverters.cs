using Avalonia.Data.Converters;

namespace Waifu2xSimpleGui.App.ViewModels;

public static class DisplayConverters
{
    /// <summary>File-type enums shown like extensions: <c>Png</c> → "png", <c>Apng</c> → "apng".</summary>
    public static FuncValueConverter<object?, string> Lowercase { get; } = new(v => v?.ToString()?.ToLowerInvariant() ?? string.Empty);
}
