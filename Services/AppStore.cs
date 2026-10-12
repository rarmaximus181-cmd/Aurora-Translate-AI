using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace AuroraTranslate.Services;

public static class AppStore
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("AURORA_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuroraTranslate");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Sync = new();

    public static AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>("settings.json") ?? new AppSettings();
        settings.SourceLanguage = Languages.Find(settings.SourceLanguage).Code;
        settings.TargetLanguage = Languages.Find(settings.TargetLanguage).Code;
        settings.ConversationLanguage = Languages.Find(settings.ConversationLanguage).Code;
        settings.BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.1, 0.8);
        settings.SpeechRate = Math.Clamp(settings.SpeechRate, -5, 5);
        if (settings.TranslationEngine is not ("online" or "local")) settings.TranslationEngine = "online";
        if (string.IsNullOrWhiteSpace(settings.OllamaModel)) settings.OllamaModel = "qwen3.5:4b";
        if (settings.ResponseStyle is not ("concise" or "balanced" or "detailed")) settings.ResponseStyle = "balanced";
        if (string.IsNullOrWhiteSpace(settings.UpdateRepository)) settings.UpdateRepository = "rarmaximus181-cmd/Aurora-Translate-AI";
        if (settings.CustomBackground is not null && !File.Exists(settings.CustomBackground)) settings.CustomBackground = null;
        return settings;
    }

    public static void SaveSettings(AppSettings settings) => Write("settings.json", settings);
    public static List<TranslationHistory> LoadHistory() => (Read<List<TranslationHistory>>("history.json") ?? []).Take(50).ToList();
    public static void SaveHistory(IEnumerable<TranslationHistory> history) => Write("history.json", history.Take(50).ToList());

    public static string ImportBackground(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidOperationException("That image could not be found.");
        if (info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Choose a PNG or GIF smaller than 20 MB.");
        var extension = info.Extension.ToLowerInvariant();
        if (extension is not (".png" or ".gif")) throw new InvalidOperationException("Choose a PNG image or an animated GIF.");
        try
        {
            using var stream = info.OpenRead();
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if ((extension == ".png" && decoder is not PngBitmapDecoder) || (extension == ".gif" && decoder is not GifBitmapDecoder))
                throw new InvalidOperationException("The file's contents do not match its image type.");
            if (decoder.Frames.Count > 250 || decoder.Frames.Any(x => x.PixelWidth > 4096 || x.PixelHeight > 4096)
                || decoder.Frames.Sum(x => (long)x.PixelWidth * x.PixelHeight) > 100_000_000)
                throw new InvalidOperationException("This image is too large to animate smoothly. Choose a smaller image or shorter GIF.");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        { throw new InvalidOperationException("That image could not be opened. Try a different PNG or GIF.", ex); }
        var folder = Path.Combine(DataDirectory, "Themes");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
        File.Copy(info.FullName, destination);
        return destination;
    }

    private static T? Read<T>(string name)
    {
        lock (Sync)
        {
            var path = Path.Combine(DataDirectory, name);
            if (!File.Exists(path)) return default;
            try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
            catch (JsonException)
            {
                try { File.Copy(path, path + ".unreadable", true); } catch (IOException) { }
                return default;
            }
            catch (IOException) { return default; }
            catch (UnauthorizedAccessException) { return default; }
        }
    }

    private static void Write<T>(string name, T value)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, name);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, true);
        }
    }
}
