using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuroraTranslate.Services;

/// <summary>Installs data-only Piper voices and runs the bundled offline speech engine.</summary>
public sealed class VoiceLibraryService : IDisposable
{
    private const long MaximumModelBytes = 500L * 1024 * 1024;
    private const int MaximumConfigBytes = 2 * 1024 * 1024;
    private const string VoiceRevision = "c10ece1aade47bb51c153c893d14e5bf8e5b7117";
    private const string EngineVersion = "piper-2023.11.14-2";
    private static readonly object LibraryLock = new();
    private static readonly SemaphoreSlim EngineGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _dataDirectory = Path.GetFullPath(AppStore.DataDirectory);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private static VoiceDownloadOption Choice(string id, string name, string language)
    {
        var family = language.Split('-')[0];
        var locale = language.Replace('-', '_');
        var model = $"{locale}-{id}-medium";
        var directory = $"https://huggingface.co/rhasspy/piper-voices/resolve/{VoiceRevision}/{family}/{locale}/{id}/medium";
        var info = $"https://huggingface.co/rhasspy/piper-voices/blob/{VoiceRevision}/{family}/{locale}/{id}/medium/MODEL_CARD";
        return new(model, name, language, "~61 MB", $"{directory}/{model}.onnx", $"{directory}/{model}.onnx.json", info);
    }

    public static IReadOnlyList<VoiceDownloadOption> Catalog { get; } = Array.AsReadOnly(new[]
    {
        Choice("amy", "Amy · English (US)", "en-US"),
        Choice("lessac", "Lessac · English (US)", "en-US"),
        Choice("ryan", "Ryan · English (US)", "en-US"),
        Choice("alba", "Alba · English (UK)", "en-GB"),
        Choice("alan", "Alan · English (UK)", "en-GB"),
        Choice("davefx", "Davefx · Spanish", "es-ES"),
        Choice("siwis", "Siwis · French", "fr-FR"),
        Choice("thorsten", "Thorsten · German", "de-DE"),
        Choice("faber", "Faber · Portuguese (Brazil)", "pt-BR"),
        Choice("paola", "Paola · Italian", "it-IT"),
        Choice("irina", "Irina · Russian", "ru-RU"),
        Choice("huayan", "Huayan · Chinese", "zh-CN")
    });

    // Published LFS SHA-256 values at the pinned repository revision.
    private static readonly IReadOnlyDictionary<string, string> ModelHashes = new Dictionary<string, string>
    {
        ["en_US-amy-medium"] = "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
        ["en_US-lessac-medium"] = "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f",
        ["en_US-ryan-medium"] = "abf4c274862564ed647ba0d2c47f8ee7c9b717d27bdad9219100eb310db4047a",
        ["en_GB-alba-medium"] = "401369c4a81d09fdd86c32c5c864440811dbdcc66466cde2d64f7133a66ad03b",
        ["en_GB-alan-medium"] = "0a309668932205e762801f1efc2736cd4b0120329622adf62be09e56339d3330",
        ["es_ES-davefx-medium"] = "6658b03b1a6c316ee4c265a9896abc1393353c2d9e1bca7d66c2c442e222a917",
        ["fr_FR-siwis-medium"] = "641d1ab097da2b81128c076810edb052b385decc8be3381814802a64a73baf99",
        ["de_DE-thorsten-medium"] = "7e64762d8e5118bb578f2eea6207e1a35a8e0c30595010b666f983fc87bb7819",
        ["pt_BR-faber-medium"] = "858555e3a064209c57088fe6bd70c4c3dc54d03eaa00c45d5ecaf43a33f95aa7",
        ["it_IT-paola-medium"] = "6fc918b5a0ea6137382833dddfa567bffbe6a5060c02043c87192ee59c04210c",
        ["ru_RU-irina-medium"] = "8ff38212d23da300bbe3705c645e6e5b9475f0bfde01558eb17813e22acaaaaa",
        ["zh_CN-huayan-medium"] = "9929917bf8cabb26fd528ea44d3a6699c11e87317a14765312420be230be0f3d"
    };

    private string LibraryDirectory => Path.Combine(_dataDirectory, "Voices");
    private string ManifestPath => Path.Combine(LibraryDirectory, "library.json");

    public IReadOnlyList<LocalVoiceInfo> GetInstalledVoices()
    {
        ThrowIfDisposed();
        lock (LibraryLock)
        {
            return ReadManifest().Where(IsAvailable).Select(ToInfo)
                .OrderBy(v => v.Language).ThenBy(v => v.Name).ToArray();
        }
    }

    public async Task<LocalVoiceInfo> ImportVoiceAsync(string modelPath, string configPath,
        string? displayName = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        ValidateFile(modelPath, ".onnx", MaximumModelBytes, "voice model");
        ValidateFile(configPath, ".json", MaximumConfigBytes, "voice configuration");
        Directory.CreateDirectory(LibraryDirectory);
        var staging = Path.Combine(LibraryDirectory, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await CopyBoundedAsync(modelPath, Path.Combine(staging, "voice.onnx"), MaximumModelBytes, token).ConfigureAwait(false);
            await CopyBoundedAsync(configPath, Path.Combine(staging, "voice.onnx.json"), MaximumConfigBytes, token).ConfigureAwait(false);
            var fallback = Path.GetFileNameWithoutExtension(modelPath);
            return await FinishInstallAsync(staging, displayName, fallback, "Imported model", null, token).ConfigureAwait(false);
        }
        finally { DeleteOwnedDirectory(staging); }
    }

    public async Task<LocalVoiceInfo> DownloadVoiceAsync(VoiceDownloadOption option,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        // Callers cannot substitute arbitrary URLs into an official catalog entry.
        var selected = Catalog.FirstOrDefault(v => v == option)
            ?? throw new ArgumentException("Choose a voice from the built-in voice library.", nameof(option));
        lock (LibraryLock)
        {
            var installed = ReadManifest().FirstOrDefault(v => v.CatalogId == selected.Id && IsAvailable(v));
            if (installed is not null) return ToInfo(installed);
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(10));
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(LibraryDirectory);
        var staging = Path.Combine(LibraryDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var modelPath = Path.Combine(staging, "voice.onnx");
            progress?.Report("Downloading " + selected.Name + "…");
            await DownloadFileAsync(selected.ModelUrl, modelPath, MaximumModelBytes, progress, token).ConfigureAwait(false);
            progress?.Report("Checking voice download…");
            if (!string.Equals(await ComputeHashAsync(modelPath, token).ConfigureAwait(false), ModelHashes[selected.Id], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded voice did not pass its integrity check. Please try again.");
            await DownloadFileAsync(selected.ConfigUrl, Path.Combine(staging, "voice.onnx.json"), MaximumConfigBytes, null, token).ConfigureAwait(false);
            var cardUrl = selected.ModelUrl[..(selected.ModelUrl.LastIndexOf('/') + 1)] + "MODEL_CARD";
            await DownloadFileAsync(cardUrl, Path.Combine(staging, "MODEL_CARD"), 256 * 1024, null, token).ConfigureAwait(false);
            var voice = await FinishInstallAsync(staging, selected.Name, selected.Id, selected.InfoUrl, selected.Id, token).ConfigureAwait(false);
            progress?.Report(selected.Name + " is ready offline.");
            return voice;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The voice download timed out. Check your connection and try again.");
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException("The voice download could not finish. Check your connection and try again.", e);
        }
        finally { DeleteOwnedDirectory(staging); }
    }

    public void RemoveVoice(string id)
    {
        ThrowIfDisposed();
        var normalized = NormalizeId(id);
        lock (LibraryLock)
        {
            var voices = ReadManifest();
            if (voices.RemoveAll(v => v.Id == normalized) == 0) return;
            // Save first so a failed deletion cannot leave a broken selection in the library.
            SaveManifest(voices);
            DeleteOwnedDirectory(Path.Combine(LibraryDirectory, normalized));
        }
    }

    /// <summary>Creates a WAV without playing it. The caller deletes the returned file.</summary>
    public async Task<string> SynthesizeToFileAsync(string text, LocalVoiceInfo voice, int rate = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter something for the voice to say.", nameof(text));
        if (text.Length > 30_000) throw new ArgumentException("Please read aloud no more than 30,000 characters at a time.", nameof(text));
        var id = NormalizeId(voice.Id);
        InstalledVoice entry;
        lock (LibraryLock)
        {
            entry = ReadManifest().FirstOrDefault(v => v.Id == id && IsAvailable(v))
                ?? throw new InvalidOperationException("This voice is no longer installed. Choose or import another voice in Settings.");
        }
        var installed = ToInfo(entry);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(60));
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        var engine = await EnsureEngineAsync(token).ConfigureAwait(false);
        var audioDirectory = Path.Combine(_dataDirectory, "VoiceAudio");
        Directory.CreateDirectory(audioDirectory);
        var output = Path.Combine(audioDirectory, Guid.NewGuid().ToString("N") + ".wav");
        var succeeded = false;
        try
        {
            ValidateOnnx(installed.ModelPath);
            ReadConfig(installed.ConfigPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = engine, WorkingDirectory = Path.GetDirectoryName(engine)!,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(installed.ModelPath);
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(installed.ConfigPath);
            startInfo.ArgumentList.Add("--output_file");
            startInfo.ArgumentList.Add(output);
            startInfo.ArgumentList.Add("--length_scale");
            startInfo.ArgumentList.Add(Math.Pow(1.08, -Math.Clamp(rate, -10, 10)).ToString("0.000", CultureInfo.InvariantCulture));
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("The offline voice engine could not start.");
            using var cancel = token.Register(() => Kill(process));
            var stdout = ReadBoundedOutputAsync(process.StandardOutput);
            var stderr = ReadBoundedOutputAsync(process.StandardError);
            // Piper treats each input line as a separate utterance; one line preserves the whole reply in one WAV.
            var utterance = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Replace('\0', ' ');
            await process.StandardInput.WriteLineAsync(utterance.AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("This voice could not generate speech. Make sure its ONNX model and matching JSON configuration are a compatible Piper voice." +
                    (string.IsNullOrWhiteSpace(error) ? "" : " Engine details: " + error[..Math.Min(500, error.Length)]));
            ValidateWave(output);
            token.ThrowIfCancellationRequested();
            succeeded = true;
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("The offline voice took too long. Try a shorter message or another voice."); }
        catch (System.ComponentModel.Win32Exception e)
        { throw new InvalidOperationException("The bundled offline voice engine could not start. Reinstall Aurora Translate and try again.", e); }
        finally { if (!succeeded) DeleteFile(output); }
    }

    private async Task<LocalVoiceInfo> FinishInstallAsync(string staging, string? displayName, string fallback,
        string source, string? catalogId, CancellationToken token)
    {
        var modelPath = Path.Combine(staging, "voice.onnx");
        var configPath = Path.Combine(staging, "voice.onnx.json");
        var metadata = ReadConfig(configPath);
        ValidateOnnx(modelPath);
        var name = CleanName(displayName ?? metadata.Name ?? fallback);
        var modelHash = await ComputeHashAsync(modelPath, token).ConfigureAwait(false);
        var configHash = await ComputeHashAsync(configPath, token).ConfigureAwait(false);
        var entry = new InstalledVoice(Guid.NewGuid().ToString("N"), name, metadata.Language,
            new FileInfo(modelPath).Length, new FileInfo(configPath).Length, modelHash, configHash, catalogId, source);
        await File.WriteAllTextAsync(Path.Combine(staging, "voice-info.json"), JsonSerializer.Serialize(entry, JsonOptions), token).ConfigureAwait(false);
        lock (LibraryLock)
        {
            token.ThrowIfCancellationRequested();
            var voices = ReadManifest();
            if (catalogId is not null)
            {
                var existing = voices.FirstOrDefault(v => v.CatalogId == catalogId && IsAvailable(v));
                if (existing is not null) return ToInfo(existing);
            }
            if (voices.Count >= 500) throw new InvalidOperationException("Remove an unused voice before importing another one.");
            var finalDirectory = Path.Combine(LibraryDirectory, entry.Id);
            Directory.Move(staging, finalDirectory);
            try
            {
                voices.Add(entry);
                SaveManifest(voices);
                return ToInfo(entry);
            }
            catch { DeleteOwnedDirectory(finalDirectory); throw; }
        }
    }

    private async Task DownloadFileAsync(string url, string destination, long maximum,
        IProgress<string>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co")
            throw new InvalidOperationException("The voice download address is not valid.");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Voice downloads must use a secure connection.");
        var length = response.Content.Headers.ContentLength;
        if (length > maximum) throw new InvalidOperationException("That voice file is larger than the supported limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        var lastReport = Stopwatch.StartNew();
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximum) throw new InvalidOperationException("That voice file is larger than the supported limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (progress is not null && lastReport.ElapsedMilliseconds >= 200)
            {
                progress.Report(length > 0 ? $"Downloading voice… {total * 100 / length}%" : $"Downloading voice… {total / 1024 / 1024} MB");
                lastReport.Restart();
            }
        }
        if (total == 0 || (length.HasValue && total != length.Value)) throw new IOException("The voice download was incomplete.");
    }

    private async Task<string> EnsureEngineAsync(CancellationToken token)
    {
        var engines = Path.Combine(_dataDirectory, "Engines");
        var target = Path.Combine(engines, EngineVersion);
        var executable = Path.Combine(target, "piper", "piper.exe");
        await EngineGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(executable) && File.Exists(Path.Combine(target, ".complete"))) return executable;
            Directory.CreateDirectory(engines);
            var staging = Path.Combine(engines, ".extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using var stream = typeof(VoiceLibraryService).Assembly.GetManifestResourceStream("AuroraTranslate.Assets.piper_windows_amd64.zip")
                    ?? throw new InvalidOperationException("The bundled offline voice engine is missing. Reinstall the latest Aurora Translate.");
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                long totalBytes = 0;
                if (archive.Entries.Count > 10_000) throw new InvalidOperationException("The offline voice engine archive is invalid.");
                var prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var item in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    totalBytes += item.Length;
                    if (item.Length > 150L * 1024 * 1024 || totalBytes > 300L * 1024 * 1024)
                        throw new InvalidOperationException("The offline voice engine archive is too large.");
                    var path = Path.GetFullPath(Path.Combine(staging, item.FullName));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || item.FullName.Contains(':'))
                        throw new InvalidOperationException("The offline voice engine archive contains an invalid path.");
                    if (string.IsNullOrEmpty(item.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var input = item.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
                if (!File.Exists(Path.Combine(staging, "piper", "piper.exe")))
                    throw new InvalidOperationException("The offline voice engine archive is incomplete.");
                await File.WriteAllTextAsync(Path.Combine(staging, ".complete"), EngineVersion, token).ConfigureAwait(false);
                // This version directory is generated and owned by this service.
                if (Directory.Exists(target)) DeleteOwnedDirectory(target);
                Directory.Move(staging, target);
                return executable;
            }
            finally { DeleteOwnedDirectory(staging); }
        }
        finally { EngineGate.Release(); }
    }

    private List<InstalledVoice> ReadManifest()
    {
        if (!File.Exists(ManifestPath)) return [];
        if (new FileInfo(ManifestPath).Length > MaximumConfigBytes) throw new InvalidOperationException("The saved voice library is too large to open.");
        try
        {
            var voices = JsonSerializer.Deserialize<List<InstalledVoice>>(File.ReadAllText(ManifestPath)) ?? [];
            return voices.Where(v => v is not null && Guid.TryParseExact(v.Id, "N", out _)
                && !string.IsNullOrWhiteSpace(v.Name) && v.Name.Length <= 120
                && IsCulture(v.Language) && v.ModelBytes > 0 && v.ModelBytes <= MaximumModelBytes
                && v.ConfigBytes > 0 && v.ConfigBytes <= MaximumConfigBytes).Take(500).ToList();
        }
        catch (JsonException e) { throw new InvalidOperationException("The saved voice library could not be read. Its model folders have been preserved.", e); }
    }

    private void SaveManifest(List<InstalledVoice> entries)
    {
        Directory.CreateDirectory(LibraryDirectory);
        var temporary = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporary, ManifestPath, true);
        }
        finally { DeleteFile(temporary); }
    }

    private LocalVoiceInfo ToInfo(InstalledVoice voice) => new(voice.Id, voice.Name, voice.Language,
        Path.Combine(LibraryDirectory, voice.Id, "voice.onnx"), Path.Combine(LibraryDirectory, voice.Id, "voice.onnx.json"));

    private bool IsAvailable(InstalledVoice voice)
    {
        var info = ToInfo(voice);
        return File.Exists(info.ModelPath) && File.Exists(info.ConfigPath)
            && new FileInfo(info.ModelPath).Length == voice.ModelBytes && new FileInfo(info.ConfigPath).Length == voice.ConfigBytes;
    }

    private static void ValidateFile(string path, string extension, long maximum, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Choose a {extension} {description} file.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidOperationException($"That {description} could not be found.");
        if (info.Length == 0 || info.Length > maximum)
            throw new InvalidOperationException($"Choose a {description} between 1 byte and {maximum / 1024 / 1024} MB.");
    }

    private static VoiceMetadata ReadConfig(string path)
    {
        ValidateFile(path, ".json", MaximumConfigBytes, "voice configuration");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("audio", out var audio) || audio.ValueKind != JsonValueKind.Object
                || !audio.TryGetProperty("sample_rate", out var sample) || sample.ValueKind != JsonValueKind.Number || !sample.TryGetInt32(out var sampleRate)
                || sampleRate is < 8000 or > 96000
                || !root.TryGetProperty("phoneme_type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("espeak" or "text")
                || !root.TryGetProperty("phoneme_id_map", out var map) || map.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("This JSON is not a supported Piper voice configuration.");
            var phonemeCount = 0;
            foreach (var property in map.EnumerateObject())
            {
                if (++phonemeCount > 20_000 || property.Value.ValueKind != JsonValueKind.Array
                    || property.Value.GetArrayLength() is < 1 or > 32)
                    throw new InvalidOperationException("The voice's phoneme configuration is invalid.");
                foreach (var symbol in property.Value.EnumerateArray())
                    if (symbol.ValueKind != JsonValueKind.Number || !symbol.TryGetInt32(out var value) || value is < 0 or > 100_000)
                        throw new InvalidOperationException("The voice's phoneme configuration is invalid.");
            }
            if (phonemeCount == 0) throw new InvalidOperationException("The voice's phoneme configuration is empty.");
            if (type.GetString() == "espeak")
            {
                if (!root.TryGetProperty("espeak", out var espeak) || espeak.ValueKind != JsonValueKind.Object
                    || !espeak.TryGetProperty("voice", out var espeakVoice) || espeakVoice.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("The voice's speech language is missing.");
                var value = espeakVoice.GetString()!;
                if (value.Length is < 1 or > 64 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new InvalidOperationException("The voice's speech language is invalid.");
            }
            if (!root.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.Object
                || !language.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("This voice needs a language.code entry in its Piper JSON configuration.");
            var cultureCode = code.GetString()!.Replace('_', '-');
            if (!IsCulture(cultureCode)) throw new InvalidOperationException("The voice's language code is not supported.");
            if (root.TryGetProperty("num_speakers", out var speakers)
                && (speakers.ValueKind != JsonValueKind.Number || !speakers.TryGetInt32(out var count) || count is < 1 or > 10_000))
                throw new InvalidOperationException("The voice's speaker count is invalid.");
            var name = root.TryGetProperty("dataset", out var dataset) && dataset.ValueKind == JsonValueKind.String
                ? dataset.GetString() : null;
            return new(CultureInfo.GetCultureInfo(cultureCode).Name, name);
        }
        catch (JsonException e)
        { throw new InvalidOperationException("The voice configuration could not be read. Choose the matching .onnx.json file that came with the Piper model.", e); }
    }

    private static bool IsCulture(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 48 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        try { return !string.IsNullOrEmpty(CultureInfo.GetCultureInfo(code).TwoLetterISOLanguageName); }
        catch (CultureNotFoundException) { return false; }
    }

    // ONNX is a protobuf ModelProto. Check the envelope before handing a data file to ONNX Runtime.
    private static void ValidateOnnx(string path)
    {
        ValidateFile(path, ".onnx", MaximumModelBytes, "voice model");
        using var stream = File.OpenRead(path);
        var versionFound = false;
        var graphFound = false;
        var opsetFound = false;
        try
        {
            var fields = 0;
            while (stream.Position < stream.Length)
            {
                if (++fields > 10_000) throw new InvalidDataException();
                var tag = ReadVarInt(stream);
                var number = tag >> 3;
                var wire = tag & 7;
                if (number == 0) throw new InvalidDataException();
                if (wire == 0)
                {
                    var value = ReadVarInt(stream);
                    if (number == 1) versionFound = value is > 0 and < 1000;
                }
                else if (wire == 2)
                {
                    var size = ReadVarInt(stream);
                    if (size > (ulong)(stream.Length - stream.Position)) throw new InvalidDataException();
                    if (number == 7) graphFound = size > 0;
                    if (number == 8) opsetFound = size > 0;
                    stream.Seek((long)size, SeekOrigin.Current);
                }
                else if (wire is 1 or 5)
                {
                    var size = wire == 1 ? 8 : 4;
                    if (stream.Length - stream.Position < size) throw new InvalidDataException();
                    stream.Seek(size, SeekOrigin.Current);
                }
                else throw new InvalidDataException();
            }
            if (!versionFound || !graphFound || !opsetFound) throw new InvalidDataException();
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or OverflowException)
        { throw new InvalidOperationException("That file is not a valid ONNX voice model. Download a Piper .onnx model and its matching .onnx.json configuration.", e); }
    }

    private static ulong ReadVarInt(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var next = stream.ReadByte();
            if (next < 0) throw new EndOfStreamException();
            if (shift == 63 && next > 1) throw new InvalidDataException();
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new InvalidDataException();
    }

    private static void ValidateWave(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 44) throw new InvalidOperationException("The voice did not produce any speech.");
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidOperationException("The voice did not produce a playable WAV file.");
    }

    private static async Task CopyBoundedAsync(string source, string destination, long maximum, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximum) throw new InvalidOperationException("That voice file is larger than the supported limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(read, 8192 - text.Length));
        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static string CleanName(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).Take(120).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Imported voice" : clean;
    }

    private static string NormalizeId(string id)
    {
        if (id.StartsWith("piper:", StringComparison.OrdinalIgnoreCase)) id = id[6..];
        if (!Guid.TryParseExact(id, "N", out var parsed)) throw new ArgumentException("Choose an installed voice.", nameof(id));
        return parsed.ToString("N");
    }

    private void DeleteOwnedDirectory(string path)
    {
        var absolute = Path.GetFullPath(path);
        var prefix = _dataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The voice folder is outside the application data folder.");
        try { if (Directory.Exists(absolute)) Directory.Delete(absolute, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private sealed record InstalledVoice(string Id, string Name, string Language, long ModelBytes,
        long ConfigBytes, string ModelSha256, string ConfigSha256, string? CatalogId, string Source);
    private sealed record VoiceMetadata(string Language, string? Name);
}
