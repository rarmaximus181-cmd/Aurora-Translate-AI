using System.IO;
using System.Text;
using AuroraTranslate;
using AuroraTranslate.Services;

if (args.Contains("--install-defaults", StringComparer.Ordinal))
{
    var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuroraTranslate");
    Environment.SetEnvironmentVariable("AURORA_DATA_DIR", appData);
    using var production = new VoiceLibraryService();
    foreach (var id in new[] { "en_GB-alba-medium", "en_US-ryan-medium" })
    {
        var voice = await production.DownloadVoiceAsync(VoiceLibraryService.Catalog.Single(v => v.Id == id));
        var path = await production.SynthesizeToFileAsync("Hello! I am ready to speak in Aurora Translate.", voice);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20_000 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || !bytes.Skip(44).Any(b => b != 0))
                throw new InvalidOperationException("Voice did not synthesize non-silent WAV audio.");
            Console.WriteLine($"INSTALLED: {voice.Name}; Language={voice.Language}; Id={voice.Id}; WAV={bytes.Length} bytes; no audio played.");
        }
        finally { File.Delete(path); }
    }
    Console.WriteLine("Production voice library: " + appData);
    return;
}

var testRoot = Path.Combine(Path.GetTempPath(), "AuroraNeuralChecks-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("AURORA_DATA_DIR", testRoot);
Directory.CreateDirectory(testRoot);
var passed = new List<string>();
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    passed.Add(label);
    Console.WriteLine("PASS: " + label);
}
async Task Reject(Func<Task> operation, string label)
{
    try { await operation(); throw new InvalidOperationException("FAIL: accepted " + label); }
    catch (InvalidOperationException ex) when (!ex.Message.StartsWith("FAIL:")) { Check(true, label); }
    catch (ArgumentException) { Check(true, label); }
}
async Task Cancel(Func<Task> operation, string label)
{
    try { await operation(); throw new InvalidOperationException("FAIL: did not cancel " + label); }
    catch (OperationCanceledException) { Check(true, label); }
}

try
{
    using var library = new VoiceLibraryService();
    Check(VoiceLibraryService.Catalog.Count == 12, "12 catalog voices in nine languages");
    Check(library.GetInstalledVoices().Count == 0, "new data directory starts with no imported voices");
    var amy = VoiceLibraryService.Catalog.Single(v => v.Id == "en_US-amy-medium");
    await Reject(() => library.DownloadVoiceAsync(amy with { ModelUrl = "https://example.com/not-a-voice" }), "catalog URL substitution is rejected");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await Cancel(() => library.DownloadVoiceAsync(amy, cancellationToken: canceled.Token), "download respects prior cancellation");
    }
    Console.WriteLine("Downloading official Amy model for file-only speech verification…");
    var downloaded = await library.DownloadVoiceAsync(amy);
    Check(downloaded.Language == "en-US" && File.Exists(downloaded.ModelPath), "downloaded voice has parsed language and owned model files");
    Check(File.Exists(Path.Combine(Path.GetDirectoryName(downloaded.ModelPath)!, "MODEL_CARD")), "voice license/model card is preserved");
    var repeated = await library.DownloadVoiceAsync(amy);
    Check(repeated.Id == downloaded.Id && library.GetInstalledVoices().Count == 1, "repeat catalog download reuses stable voice ID");
    var inputDirectory = Path.Combine(testRoot, "Inputs");
    Directory.CreateDirectory(inputDirectory);
    var invalidModel = Path.Combine(inputDirectory, "fake.onnx");
    File.WriteAllText(invalidModel, "MZ this is not an ONNX model");
    await Reject(() => library.ImportVoiceAsync(invalidModel, downloaded.ConfigPath), "a renamed executable/non-ONNX model is rejected");
    var invalidConfig = Path.Combine(inputDirectory, "broken.json");
    File.WriteAllText(invalidConfig, "{ broken");
    await Reject(() => library.ImportVoiceAsync(downloaded.ModelPath, invalidConfig), "invalid JSON is rejected");
    File.WriteAllText(invalidConfig, "{\"language\":{\"code\":\"en_US\"}}");
    await Reject(() => library.ImportVoiceAsync(downloaded.ModelPath, invalidConfig), "non-Piper configuration is rejected");
    Check(library.GetInstalledVoices().Count == 1 && !Directory.EnumerateDirectories(Path.Combine(testRoot, "Voices"), ".*-*").Any(), "failed imports leave no partial installs or changed library");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        await Cancel(() => library.ImportVoiceAsync(downloaded.ModelPath, downloaded.ConfigPath, cancellationToken: canceled.Token), "import respects prior cancellation");
    }
    var imported = await library.ImportVoiceAsync(downloaded.ModelPath, downloaded.ConfigPath, "My downloaded voice");
    Check(imported.Id != downloaded.Id && imported.Name == "My downloaded voice" && imported.Language == "en-US", "import creates named, independent voice with language metadata");
    using (var reopened = new VoiceLibraryService())
        Check(reopened.GetInstalledVoices().Any(v => v.Id == imported.Id), "library persists imported IDs across service instances");
    using (var speech = new VoiceService())
    {
        Check(speech.GetVoices().Any(v => v.Id == "piper:" + imported.Id && v.Name.Contains("Piper")), "speech selector includes imported Piper voice with stable prefix");
        await Reject(() => speech.SpeakAsync("Hola", "es", "piper:" + imported.Id), "different-language selected voice is rejected before playback");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Cancel(() => speech.SpeakAsync("This must never play.", "en", "piper:" + imported.Id, cancellationToken: canceled.Token), "canceled read-aloud never plays audio");
    }
    var wavePath = await library.SynthesizeToFileAsync("Hello from Aurora Translate.\nThis second paragraph should also be spoken.", imported);
    var wave = File.ReadAllBytes(wavePath);
    Check(wave.Length > 20_000 && Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 4) == "WAVE", "bundled Piper engine synthesizes a real WAV without playing sound");
    Check(wave.Skip(44).Any(b => b != 0), "generated speech contains non-silent audio samples");
    File.Delete(wavePath);
    var audioDirectory = Path.Combine(testRoot, "VoiceAudio");
    using (var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        await Cancel(() => library.SynthesizeToFileAsync(string.Join(" ", Enumerable.Repeat("This is a longer cancellation check.", 100)), imported, cancellationToken: canceled.Token), "running offline synthesis can be canceled");
    Check(!Directory.EnumerateFiles(audioDirectory, "*.wav").Any(), "canceled synthesis removes its temporary WAV");
    library.RemoveVoice("piper:" + imported.Id);
    Check(library.GetInstalledVoices().Count == 1 && !File.Exists(imported.ModelPath) && File.Exists(downloaded.ModelPath), "removal deletes only the selected owned voice and keeps source model");
    Check(passed.Count >= 18, "all neural voice checks completed");
    Console.WriteLine($"Completed {passed.Count} checks; no audio was played.");
}
finally
{
    var resolved = Path.GetFullPath(testRoot);
    var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("AuroraNeuralChecks-"))
        throw new InvalidOperationException("Unexpected test cleanup folder.");
    Directory.Delete(resolved, true);
}
