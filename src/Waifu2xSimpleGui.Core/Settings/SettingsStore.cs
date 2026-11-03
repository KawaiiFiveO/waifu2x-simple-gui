using System.Text.Json;
using System.Text.Json.Nodes;

namespace Waifu2xSimpleGui.Core.Settings;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON in the per-user config folder
/// (%APPDATA%\Waifu2x-Simple-GUI on Windows, ~/.config/Waifu2x-Simple-GUI on Linux) —
/// never next to the executable, so no admin rights are needed.
/// </summary>
public sealed class SettingsStore(string filePath)
{
    public const string AppFolderName = "Waifu2x-Simple-GUI";

    public string FilePath { get; } = filePath;

    public static SettingsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        AppFolderName,
        "settings.json"));

    /// <summary>Returns defaults when the file is missing or unreadable (a corrupt file is kept as .bak).</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            JsonNode? saved;
            var stream = File.OpenRead(FilePath);
            await using (stream.ConfigureAwait(false))
            {
                saved = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (saved is not JsonObject savedObject)
            {
                throw new JsonException("Settings root is not an object.");
            }

            // The source generator fills every init-only property, so a key missing from an older file would
            // become 0/null instead of its default. Overlay the saved keys on a serialized default instead.
            var merged = JsonSerializer.SerializeToNode(new AppSettings(), SettingsJsonContext.Default.AppSettings)!.AsObject();
            foreach (var (key, value) in savedObject.ToList())
            {
                savedObject.Remove(key);
                merged[key] = value;
            }

            return merged.Deserialize(SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>Atomic write: temp file then replace.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        var stream = File.Create(tmp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SettingsJsonContext.Default.AppSettings, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tmp, FilePath, overwrite: true);
    }
}
