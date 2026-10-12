using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraTranslate.Services;

public enum UpdateStatus { Unconfigured, UpToDate, Available, Required, Unavailable }
public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Info, string Message);
public sealed record UpdateInfo(string Repository, string Version, string MinimumSupportedVersion, bool Mandatory,
    string ReleaseNotes, string AssetName, string Sha256, long SizeBytes, string DownloadUrl, string ReleaseUrl);

/// <summary>
/// Reads a public GitHub release and checks its manifest against the release assets.
/// HTTPS and SHA-256 protect transport and detect incorrect downloads; this is not
/// publisher code signing, and repository owners control which executable is trusted.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string ExecutableName = "AuroraTranslate.exe";
    private const string ManifestName = "update.json";
    private const long MaximumExecutableBytes = 200L * 1024 * 1024;
    private const int MaximumReleaseBytes = 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 12 };
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDirectory;
    private readonly string _currentVersion;
    private readonly string _currentExecutable;
    private readonly TimeSpan _checkTimeout;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Dictionary<string, UpdateInfo> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UpdateInfo> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public static string CurrentVersion => NormalizeVersion(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public UpdateService(HttpClient? http = null) : this(http, AppStore.DataDirectory, CurrentVersion,
        Environment.ProcessPath ?? "") { }

    internal UpdateService(HttpClient? http, string dataDirectory, string currentVersion, string currentExecutable, TimeSpan? checkTimeout = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _cacheDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "Updates");
        _currentVersion = NormalizeVersion(ParseVersion(currentVersion));
        _currentExecutable = string.IsNullOrEmpty(currentExecutable) ? "" : Path.GetFullPath(currentExecutable);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<UpdateCheckResult> CheckAsync(string repository, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(repository))
            return new(UpdateStatus.Unconfigured, null, "Updates are not configured yet.");
        if (!IsValidRepository(repository))
            return new(UpdateStatus.Unconfigured, null, "Choose a GitHub repository in owner/repository format.");
        repository = repository.Trim();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateInfo? cached = LoadCached(repository);
            if (_verified.TryGetValue(repository, out var remembered)
                && (cached is null || ParseVersion(remembered.Version) >= ParseVersion(cached.Version))) cached = remembered;
            if (cached is not null) Remember(cached);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_checkTimeout);
            var checkToken = deadline.Token;
            try
            {
                using var response = await SendGitHubAsync(new Uri($"https://api.github.com/repos/{repository}/releases/latest"),
                    "application/vnd.github+json", checkToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateUnavailableException(response.StatusCode == HttpStatusCode.NotFound
                        ? "The repository does not have a public update release yet."
                        : "GitHub could not check for updates. Try again later.");
                byte[] releaseBytes = await ReadBoundedAsync(response, MaximumReleaseBytes, checkToken).ConfigureAwait(false);
                using var release = JsonDocument.Parse(releaseBytes, new JsonDocumentOptions { MaxDepth = 16 });
                var releaseMetadata = ParseReleaseMetadata(repository, release.RootElement);
                using var manifestResponse = await SendGitHubAsync(new Uri(releaseMetadata.ManifestUrl), "application/octet-stream", checkToken).ConfigureAwait(false);
                if (!manifestResponse.IsSuccessStatusCode) throw new UpdateUnavailableException("The update manifest could not be downloaded.");
                byte[] manifestBytes = await ReadBoundedAsync(manifestResponse, MaximumManifestBytes, checkToken).ConfigureAwait(false);
                UpdateInfo latest = ParseManifest(repository, releaseMetadata, manifestBytes);
                // A removed/republished older release cannot erase a previously established requirement.
                UpdateInfo selected = cached is not null && ParseVersion(cached.Version) > ParseVersion(latest.Version) ? cached : latest;
                Remember(selected);
                try { SaveCached(selected); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return ResultFor(selected, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                or InvalidDataException or UpdateUnavailableException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (cached is not null && IsRequired(cached, _currentVersion)) return ResultFor(cached, true);
                string message = ex is UpdateUnavailableException ? ex.Message : "Updates could not be checked. You can use Aurora Translate offline and retry later.";
                return new(UpdateStatus.Unavailable, cached, message);
            }
        }
        finally { _operation.Release(); }
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(info);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var downloadToken = deadline.Token;
        try
        {
            if (!IsValidInfo(info) || !_verified.TryGetValue(info.Repository, out var verified) || verified != info)
                throw new InvalidOperationException("Check the GitHub release again before downloading this update.");
            if (ParseVersion(info.Version) <= ParseVersion(_currentVersion))
                throw new InvalidOperationException("This update is not newer than the installed version.");
            string folder = Path.Combine(_cacheDirectory, RepositoryCacheKey(info.Repository), info.Version);
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, ExecutableName);
            if (File.Exists(destination))
            {
                try
                {
                    await VerifyExecutableAsync(destination, info, downloadToken).ConfigureAwait(false);
                    _downloads[destination] = info;
                    progress?.Report("Update already downloaded and verified.");
                    return destination;
                }
                catch (InvalidDataException) { File.Delete(destination); }
            }
            temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".download");
            progress?.Report("Connecting to the official GitHub release…");
            using var response = await SendGitHubAsync(new Uri(info.DownloadUrl), "application/octet-stream", downloadToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The update download is unavailable. Select Retry and try again.");
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != info.SizeBytes)
                throw new InvalidDataException("The download size does not match its release manifest.");
            using var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long received = 0;
                int lastPercent = -1;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > info.SizeBytes || received > MaximumExecutableBytes)
                        throw new InvalidDataException("The download exceeded its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), downloadToken).ConfigureAwait(false);
                    int percent = (int)(received * 100 / info.SizeBytes);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report($"Downloading update… {percent.ToString(CultureInfo.InvariantCulture)}%");
                    }
                }
                if (received != info.SizeBytes) throw new InvalidDataException("The update download ended early. Retry the download.");
                await output.FlushAsync(downloadToken).ConfigureAwait(false);
            }
            progress?.Report("Checking the download's SHA-256 and app version…");
            await VerifyExecutableAsync(temporary, info, downloadToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
            temporary = null;
            _downloads[destination] = info;
            progress?.Report("Verified. Ready to install and restart.");
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The update download timed out. Select Retry to try again.");
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _operation.Release();
        }
    }

    public async Task ApplyAndRestartAsync(string downloadedPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedPath);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string source = Path.GetFullPath(downloadedPath);
            if (!_downloads.TryGetValue(source, out var info) || !IsPathWithin(source, _cacheDirectory))
                throw new InvalidOperationException("Download and verify the update in this app before installing it.");
            if (!File.Exists(_currentExecutable) || !string.Equals(Path.GetFileName(_currentExecutable), ExecutableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, _currentExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run AuroraTranslate.exe from its app folder to install an update.");
            string installationDirectory = Path.GetDirectoryName(_currentExecutable)!;
            await VerifyExecutableAsync(source, info, cancellationToken).ConfigureAwait(false);
            ProbeWritableDirectory(installationDirectory);
            string helperDirectory = Path.Combine(_cacheDirectory, "Helper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperDirectory);
            string scriptPath = Path.Combine(helperDirectory, "install-update.ps1");
            string settingsPath = Path.Combine(helperDirectory, "install-update.json");
            using var currentProcess = Process.GetCurrentProcess();
            var settings = new HelperSettings(source, _currentExecutable, info.Sha256, info.SizeBytes, info.Version,
                currentProcess.Id, currentProcess.StartTime.ToUniversalTime().Ticks,
                Path.Combine(helperDirectory, "update-install.log"), _cacheDirectory);
            await File.WriteAllTextAsync(scriptPath, HelperScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new InvalidOperationException("Windows PowerShell is unavailable. Download the release manually to update this app.");
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = helperDirectory
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-SettingsPath", settingsPath })
                start.ArgumentList.Add(argument);
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("The update installer could not be started.");
            // The caller closes the app only after the verified helper has successfully started.
        }
        finally { _operation.Release(); }
    }

    private UpdateCheckResult ResultFor(UpdateInfo info, bool offline)
    {
        if (IsRequired(info, _currentVersion))
            return new(UpdateStatus.Required, info, offline
                ? $"Version {info.Version} is required. The saved release is available; reconnect to download it."
                : $"Version {info.Version} is required before continuing.");
        if (ParseVersion(info.Version) > ParseVersion(_currentVersion))
            return new(UpdateStatus.Available, info, $"Version {info.Version} is available.");
        return new(UpdateStatus.UpToDate, info, $"Aurora Translate {_currentVersion} is up to date.");
    }

    internal static bool IsRequired(UpdateInfo info, string currentVersion) => ParseVersion(currentVersion) < ParseVersion(info.MinimumSupportedVersion)
        || (info.Mandatory && ParseVersion(currentVersion) < ParseVersion(info.Version));

    internal static ReleaseMetadata ParseReleaseMetadata(string repository, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("The update must be a published stable release.");
        string tag = GetString(root, "tag_name") ?? "";
        string version = NormalizeVersion(ParseVersion(tag.StartsWith('v') ? tag[1..] : tag));
        string releaseUrl = GetString(root, "html_url") ?? "";
        if (!IsReleasePageUrl(releaseUrl, repository, tag)) throw new InvalidDataException("The release page does not match the configured repository.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100)
            throw new InvalidDataException("The release assets are missing.");
        var matchingManifest = assets.EnumerateArray().Where(asset => GetString(asset, "name") == ManifestName).ToArray();
        var matchingExecutable = assets.EnumerateArray().Where(asset => GetString(asset, "name") == ExecutableName).ToArray();
        if (matchingManifest.Length != 1 || matchingExecutable.Length != 1) throw new InvalidDataException("The release needs one update.json and one AuroraTranslate.exe asset.");
        var manifest = matchingManifest[0];
        var executable = matchingExecutable[0];
        string manifestUrl = GetString(manifest, "browser_download_url") ?? "";
        string executableUrl = GetString(executable, "browser_download_url") ?? "";
        if (!IsAssetUrl(manifestUrl, repository, tag, ManifestName) || !IsAssetUrl(executableUrl, repository, tag, ExecutableName)
            || GetString(manifest, "state") != "uploaded" || GetString(executable, "state") != "uploaded"
            || !manifest.TryGetProperty("size", out var manifestSize) || !manifestSize.TryGetInt64(out long manifestBytes) || manifestBytes <= 0 || manifestBytes > MaximumManifestBytes
            || !executable.TryGetProperty("size", out var executableSize) || !executableSize.TryGetInt64(out long executableBytes) || executableBytes <= 0 || executableBytes > MaximumExecutableBytes)
            throw new InvalidDataException("The release contains invalid asset URLs or sizes.");
        string? digest = GetString(executable, "digest");
        if (digest is not null && !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The release executable has an invalid digest.");
        return new(tag, version, releaseUrl, manifestUrl, executableUrl, executableBytes, digest);
    }

    internal static UpdateInfo ParseManifest(string repository, ReleaseMetadata release, byte[] bytes)
    {
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("The update manifest is too large.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        string version = NormalizeVersion(ParseVersion(GetString(root, "version") ?? ""));
        string minimum = NormalizeVersion(ParseVersion(GetString(root, "minimumSupportedVersion") ?? ""));
        string name = GetString(root, "assetName") ?? "";
        string hash = GetString(root, "sha256") ?? "";
        string notes = GetString(root, "releaseNotes") ?? "";
        if (version != release.Version || ParseVersion(minimum) > ParseVersion(version) || name != ExecutableName
            || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant) || notes.Length > 12000
            || !root.TryGetProperty("mandatory", out var mandatory) || mandatory.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("sizeBytes", out var size) || !size.TryGetInt64(out long sizeBytes) || sizeBytes != release.ExecutableSizeBytes)
            throw new InvalidDataException("The update manifest does not match the GitHub release.");
        if (release.Digest is not null && !HashesMatch(hash, release.Digest[7..]))
            throw new InvalidDataException("The manifest digest does not match GitHub's release asset digest.");
        return new(repository, version, minimum, mandatory.GetBoolean(), notes, name, hash.ToLowerInvariant(), sizeBytes, release.ExecutableUrl, release.ReleaseUrl);
    }

    internal static bool IsValidRepository(string? value) => value is not null && value.Length <= 160
        && Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant)
        && !value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && !value.Split('/').Any(part => part is "." or "..");

    internal static Version ParseVersion(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 48 || !Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)
            || !Version.TryParse(value, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0)
            throw new InvalidDataException("The update has an invalid version.");
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    private static string NormalizeVersion(Version version) => version.Revision > 0
        ? version.ToString(4) : new Version(version.Major, version.Minor, Math.Max(0, version.Build)).ToString(3);

    private static bool IsAssetUrl(string value, string repository, string tag, string name)
    {
        if (!TryTrustedUri(value, out var uri) || uri!.Host != "github.com" || uri.Query.Length != 0) return false;
        return uri.AbsolutePath == $"/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
    }

    private static bool IsReleasePageUrl(string value, string repository, string tag)
    {
        if (!TryTrustedUri(value, out var uri) || uri!.Host != "github.com" || uri.Query.Length != 0) return false;
        return uri.AbsolutePath == $"/{repository}/releases/tag/{Uri.EscapeDataString(tag)}";
    }

    internal static bool TryTrustedUri(string? value, out Uri? uri)
    {
        uri = null;
        if (value is null || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || parsed.Port != 443 || parsed.UserInfo.Length != 0 || parsed.Fragment.Length != 0
            || parsed.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) return false;
        uri = parsed;
        return true;
    }

    private async Task<HttpResponseMessage> SendGitHubAsync(Uri initialUri, string accept, CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!TryTrustedUri(current.AbsoluteUri, out _)) throw new InvalidDataException("The update URL is outside GitHub's trusted release hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd($"AuroraTranslate/{_currentVersion}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (current.Host == "api.github.com") request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // Check the final location too when a caller supplies an auto-redirecting transport.
            if (response.RequestMessage?.RequestUri is Uri finalUri && !TryTrustedUri(finalUri.AbsoluteUri, out _))
            {
                response.Dispose();
                throw new InvalidDataException("The download was redirected outside GitHub's release hosts.");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("The update redirect was missing its location.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("The update download exceeded its redirect limit.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update response exceeded its size limit.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("The update response exceeded its size limit.");
            output.Write(buffer, 0, count);
        }
    }

    internal static async Task VerifyExecutableAsync(string path, UpdateInfo info, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != info.SizeBytes || file.Length <= 0 || file.Length > MaximumExecutableBytes)
            throw new InvalidDataException("The update file has an incorrect size.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(info.Sha256)))
            throw new InvalidDataException("The update SHA-256 did not match. The download was rejected.");
        if (stream.Length < 128) throw new InvalidDataException("The update is not a Windows executable.");
        stream.Position = 0;
        byte[] dos = new byte[64];
        await stream.ReadExactlyAsync(dos, cancellationToken).ConfigureAwait(false);
        int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60, 4));
        if (dos[0] != 'M' || dos[1] != 'Z' || offset < 64 || offset > Math.Min(stream.Length - 24, 1024 * 1024))
            throw new InvalidDataException("The update is not a valid Windows executable.");
        stream.Position = offset;
        byte[] pe = new byte[6];
        await stream.ReadExactlyAsync(pe, cancellationToken).ConfigureAwait(false);
        if (!pe.AsSpan(0, 4).SequenceEqual(new byte[] { 80, 69, 0, 0 }) || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4, 2)) != 0x8664)
            throw new InvalidDataException("The update is not the Windows x64 app.");
        string? fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (fileVersion is null || ParseVersion(fileVersion) != ParseVersion(info.Version))
            throw new InvalidDataException("The executable version does not match the release manifest.");
    }

    internal static bool HashesMatch(string left, string right) => Regex.IsMatch(left, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
        && Regex.IsMatch(right, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static bool IsValidInfo(UpdateInfo info)
    {
        try
        {
            string tag = new Uri(info.ReleaseUrl).Segments.Last();
            return IsValidRepository(info.Repository) && info.Version == NormalizeVersion(ParseVersion(info.Version))
                && info.MinimumSupportedVersion == NormalizeVersion(ParseVersion(info.MinimumSupportedVersion))
                && ParseVersion(info.MinimumSupportedVersion) <= ParseVersion(info.Version) && info.AssetName == ExecutableName
                && Regex.IsMatch(info.Sha256, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)
                && info.SizeBytes is > 0 and <= MaximumExecutableBytes && info.ReleaseNotes.Length <= 12000
                && IsReleasePageUrl(info.ReleaseUrl, info.Repository, tag) && IsAssetUrl(info.DownloadUrl, info.Repository, tag, ExecutableName)
                && NormalizeVersion(ParseVersion(tag.StartsWith('v') ? tag[1..] : tag)) == info.Version;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or UriFormatException or NullReferenceException) { return false; }
    }

    private void Remember(UpdateInfo info) => _verified[info.Repository] = info;
    private string CachePath(string repository) => Path.Combine(_cacheDirectory, RepositoryCacheKey(repository), "release-state.json");
    private static string RepositoryCacheKey(string repository) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToLowerInvariant()))).ToLowerInvariant();
    private UpdateInfo? LoadCached(string repository)
    {
        try
        {
            string path = CachePath(repository);
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes) return null;
            var cache = JsonSerializer.Deserialize<CachedRelease>(File.ReadAllBytes(path), JsonOptions);
            return cache?.SchemaVersion == 1 && cache.Info is not null && cache.Info.Repository == repository && IsValidInfo(cache.Info) ? cache.Info : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private void SaveCached(UpdateInfo info)
    {
        string path = CachePath(info.Repository);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new CachedRelease(1, info), JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsPathWithin(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void ProbeWritableDirectory(string directory)
    {
        string probe = Path.Combine(directory, ".aurora-update-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("This app folder cannot be updated by your Windows account. Move Aurora Translate to a folder you own, such as your Desktop, and try again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }

    private static string? GetString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal sealed record ReleaseMetadata(string Tag, string Version, string ReleaseUrl, string ManifestUrl, string ExecutableUrl, long ExecutableSizeBytes, string? Digest);
    private sealed record CachedRelease(int SchemaVersion, UpdateInfo Info);
    internal sealed record HelperSettings(string Source, string Target, string Sha256, long SizeBytes, string Version,
        int ProcessId, long ProcessStartUtcTicks, string LogPath, string CacheDirectory);
    private sealed class UpdateUnavailableException(string message) : Exception(message);

    // Paths and hashes are JSON data passed through ArgumentList, never interpolated into code.
    internal const string HelperScript = """
        param([Parameter(Mandatory=$true)][string]$SettingsPath)
        $ErrorActionPreference = 'Stop'
        $replaced = $false
        $stage = $null
        $settings = $null
        function Get-AuroraSha256([string]$Path) {
            $inputFile = [IO.File]::OpenRead($Path)
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try { return [BitConverter]::ToString($algorithm.ComputeHash($inputFile)).Replace('-', '') }
            finally { $inputFile.Dispose(); $algorithm.Dispose() }
        }
        try {
            $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
            $source = [IO.Path]::GetFullPath([string]$settings.Source)
            $target = [IO.Path]::GetFullPath([string]$settings.Target)
            $installDirectory = [IO.Path]::GetDirectoryName($target)
            $cacheRoot = [IO.Path]::GetFullPath([string]$settings.CacheDirectory).TrimEnd('\') + '\'
            if (-not $source.StartsWith($cacheRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($source) -cne 'AuroraTranslate.exe' -or
                [IO.Path]::GetFileName($target) -ine 'AuroraTranslate.exe' -or
                $source.Equals($target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update paths.' }
            if ([string]$settings.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$settings.SizeBytes -le 0 -or
                [long]$settings.SizeBytes -gt 209715200) { throw 'Invalid update verification data.' }
            $parent = Get-Process -Id ([int]$settings.ProcessId) -ErrorAction SilentlyContinue
            if ($null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks -eq [long]$settings.ProcessStartUtcTicks) {
                Wait-Process -Id $parent.Id -Timeout 60
            }
            $file = Get-Item -LiteralPath $source
            if ($file.Length -ne [long]$settings.SizeBytes -or
                (Get-AuroraSha256 $source) -ine [string]$settings.Sha256) { throw 'Downloaded update failed verification.' }
            $version = [Version]$file.VersionInfo.FileVersion
            $expectedVersion = [Version]([string]$settings.Version)
            if ($version.Major -ne $expectedVersion.Major -or $version.Minor -ne $expectedVersion.Minor -or
                $version.Build -ne $expectedVersion.Build -or [Math]::Max(0,$version.Revision) -ne [Math]::Max(0,$expectedVersion.Revision)) { throw 'Downloaded app version is incorrect.' }
            $stage = [IO.Path]::Combine($installDirectory, '.AuroraTranslate-update-' + [Guid]::NewGuid().ToString('N') + '.tmp')
            $backup = $target + '.previous'
            [IO.File]::Copy($source, $stage, $false)
            if ((Get-AuroraSha256 $stage) -ine [string]$settings.Sha256) { throw 'Staged update failed verification.' }
            [IO.File]::Replace($stage, $target, $backup, $true)
            $replaced = $true
            $stage = $null
            [IO.File]::WriteAllText([string]$settings.LogPath, 'Update installed and Aurora Translate restarted.')
            Start-Process -FilePath $target -WorkingDirectory $installDirectory
        } catch {
            $problem = $_.Exception.Message
            if ($replaced -and (Test-Path -LiteralPath $backup -PathType Leaf)) {
                try {
                    [IO.File]::Copy($backup, $target, $true)
                    Start-Process -FilePath $target -WorkingDirectory $installDirectory
                    $problem = $problem + ' The previous app was restored.'
                } catch { $problem = $problem + ' Restore failed: ' + $_.Exception.Message }
            } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
                try { Start-Process -FilePath $target -WorkingDirectory $installDirectory }
                catch { $problem = $problem + ' Restart failed: ' + $_.Exception.Message }
            }
            if ($null -ne $settings) { [IO.File]::WriteAllText([string]$settings.LogPath, $problem) }
            exit 1
        } finally {
            if ($null -ne $stage -and (Test-Path -LiteralPath $stage -PathType Leaf)) {
                Remove-Item -LiteralPath $stage -Force
            }
        }
        """;
}
