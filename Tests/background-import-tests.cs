using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuroraTranslate;
using AuroraTranslate.Services;

internal static class BackgroundImportChecks
{
    [STAThread]
    private static void Main()
    {
        var previous = Environment.GetEnvironmentVariable("AURORA_DATA_DIR");
        var folder = Path.Combine(Path.GetTempPath(), "AuroraBackgroundChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Environment.SetEnvironmentVariable("AURORA_DATA_DIR", Path.Combine(folder, "data"));
            var pixels = new byte[] { 255, 100, 50, 255, 100, 255, 50, 255, 50, 100, 255, 255, 255, 50, 100, 255 };
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            bitmap.Freeze();
            var png = Path.Combine(folder, "fixture.png");
            Save(png, new PngBitmapEncoder(), bitmap);
            var gif = Path.Combine(folder, "fixture.gif");
            var animated = new GifBitmapEncoder();
            animated.Frames.Add(BitmapFrame.Create(bitmap));
            animated.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(gif)) animated.Save(output);
            using (var input = File.OpenRead(gif))
                Require(BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Count == 2, "GIF fixture lost animation frames.");

            foreach (var source in new[] { png, gif })
            {
                var imported = AppStore.ImportBackground(source);
                Require(Path.GetExtension(imported) == Path.GetExtension(source), "Import changed the image format.");
                Require(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(imported)), "Imported image changed content.");
                AppStore.SaveSettings(new AppSettings { Theme = "Custom", CustomBackground = imported, AnimateTheme = false });
                var reloaded = AppStore.LoadSettings();
                Require(reloaded.CustomBackground == imported && !reloaded.AnimateTheme, "Custom background settings did not persist.");
            }
            var disguised = Path.Combine(folder, "disguised.gif");
            File.Copy(png, disguised);
            Reject(disguised, "contents do not match");
            var unsupported = Path.Combine(folder, "unsupported.jpg");
            File.Copy(png, unsupported);
            Reject(unsupported, "PNG");
            var damaged = Path.Combine(folder, "damaged.png");
            File.WriteAllText(damaged, "not an image");
            Reject(damaged, "image");
            var oversized = Path.Combine(folder, "oversized.png");
            using (var output = File.Create(oversized)) output.SetLength(20 * 1024 * 1024 + 1);
            Reject(oversized, "20 MB");
            var tooWide = Path.Combine(folder, "too-wide.png");
            var widePixels = new byte[4097 * 4];
            var wide = BitmapSource.Create(4097, 1, 96, 96, PixelFormats.Bgra32, null, widePixels, widePixels.Length);
            Save(tooWide, new PngBitmapEncoder(), wide);
            Reject(tooWide, "too large");
            Console.WriteLine("PASS: PNG/GIF copy and settings round-trip; mismatched, unsupported, damaged, >20MB, and >4096px image rejection.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AURORA_DATA_DIR", previous);
            var target = Path.GetFullPath(folder);
            var allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected cleanup target.");
            Directory.Delete(target, true);
        }
    }

    private static void Save(string path, BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Reject(string path, string expectedMessage)
    {
        try { AppStore.ImportBackground(path); }
        catch (InvalidOperationException e) when (e.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Invalid image was accepted or had no useful validation message: " + Path.GetFileName(path));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
