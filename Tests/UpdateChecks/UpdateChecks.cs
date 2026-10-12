using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuroraTranslate.Services;

internal static class UpdateChecks
{
    private const string Repository = "rarmaximus181-cmd/Aurora-Translate-AI";
    private static int _checks;
    private static byte[] _fixture = [];
    private static string _hash = "";

    public static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "AuroraUpdateChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            _fixture = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixture", "UpdateFixture.exe"));
            _hash = Convert.ToHexString(SHA256.HashData(_fixture)).ToLowerInvariant();
            CheckValidation();
            await CheckStatesAndCacheAsync(Path.Combine(root, "state"));
            await CheckDownloadsAsync(Path.Combine(root, "download"));
            await CheckHelperAsync(Path.Combine(root, "helper"));
            Console.WriteLine($"PASS: {_checks} update checks. Mock GitHub only; replacement/restart used a disposable local fixture.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            string canonical = Path.GetFullPath(root);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(canonical).StartsWith("AuroraUpdateChecks-", StringComparison.Ordinal) && Directory.Exists(canonical))
            {
                // The restarted no-UI fixture can still be exiting for a fraction of a second.
                for (int attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(canonical, true); break; }
                    catch (Exception ex) when (attempt < 100 && ex is IOException or UnauthorizedAccessException)
                    { await Task.Delay(100); }
                }
            }
        }
    }

    private static void CheckValidation()
    {
        Check(UpdateService.IsValidRepository(Repository), "owner/repository accepted");
        foreach (string? repository in new string?[] { null, "", "https://github.com/a/b", "a/b/../../c", "a/b.git", "a /b", "a/b?token=x", "-owner/repo", "a/.." })
            Check(!UpdateService.IsValidRepository(repository), "unsafe repository rejected");
        Check(UpdateService.ParseVersion("2.1.0") == UpdateService.ParseVersion("2.1.0.0"), "three/four-part version normalization");
        Check(UpdateService.ParseVersion("2.0.10") > UpdateService.ParseVersion("2.0.2"), "numeric version comparison");
        foreach (string version in new[] { "", "2", "2.1", "v2.1.0", "2.1.0-beta", "-2.1.0", "2.1.0/../", "99999999999999999.1.0" })
            Expect<InvalidDataException>(() => UpdateService.ParseVersion(version), "unsafe version rejected");
        Check(UpdateService.TryTrustedUri("https://release-assets.githubusercontent.com/github-production-release-asset/1?signature=abc", out _), "release CDN permitted");
        foreach (string url in new[] { "http://github.com/a/b", "https://github.com.evil.example/a", "https://github.com:444/a", "https://user@github.com/a", "https://github.com/a#x", "https://raw.githubusercontent.com/a" })
            Check(!UpdateService.TryTrustedUri(url, out _), "untrusted download origin rejected");
        Check(UpdateService.HashesMatch(_hash, _hash.ToUpperInvariant()) && !UpdateService.HashesMatch(_hash, new string('0', 64)), "SHA comparison");
        Check(UpdateService.IsPathWithin(@"C:\updates\v\AuroraTranslate.exe", @"C:\updates")
            && !UpdateService.IsPathWithin(@"C:\updates-evil\AuroraTranslate.exe", @"C:\updates")
            && !UpdateService.IsPathWithin(@"C:\updates\..\outside.exe", @"C:\updates"), "canonical update directory containment");
        var handler = new GitHubMock();
        using var release = JsonDocument.Parse(handler.ReleaseJson());
        var metadata = UpdateService.ParseReleaseMetadata(Repository, release.RootElement);
        var info = UpdateService.ParseManifest(Repository, metadata, handler.ManifestBytes());
        Check(info.Version == "2.1.0" && info.Mandatory && info.AssetName == "AuroraTranslate.exe", "manifest matches release");
        Check(UpdateService.IsRequired(info, "2.0.0") && !UpdateService.IsRequired(info, "2.1.0") && !UpdateService.IsRequired(info, "3.0.0"), "mandatory update never downgrades");
        foreach (string malformed in new[]
        {
            handler.ManifestText().Replace("2.1.0", "2.2.0", StringComparison.Ordinal),
            handler.ManifestText().Replace("AuroraTranslate.exe", "evil.exe", StringComparison.Ordinal),
            handler.ManifestText().Replace(_hash, new string('0', 64), StringComparison.Ordinal),
            handler.ManifestText().Replace("\"mandatory\":true", "\"mandatory\":\"true\"", StringComparison.Ordinal),
            handler.ManifestText().Replace("\"minimumSupportedVersion\":\"2.1.0\"", "\"minimumSupportedVersion\":\"3.0.0\"", StringComparison.Ordinal),
            handler.ManifestText().Replace("\"sizeBytes\":" + _fixture.Length, "\"sizeBytes\":1", StringComparison.Ordinal)
        }) Expect<InvalidDataException>(() => UpdateService.ParseManifest(Repository, metadata, Encoding.UTF8.GetBytes(malformed)), "manifest inconsistency rejected");
        foreach (string malformed in new[]
        {
            handler.ReleaseJson().Replace("\"draft\":false", "\"draft\":true", StringComparison.Ordinal),
            handler.ReleaseJson().Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal),
            handler.ReleaseJson().Replace("https://github.com/", "https://evil.example/", StringComparison.Ordinal),
            handler.ReleaseJson().Replace("\"state\":\"uploaded\"", "\"state\":\"new\"", StringComparison.Ordinal)
        })
        {
            using var invalid = JsonDocument.Parse(malformed);
            Expect<InvalidDataException>(() => UpdateService.ParseReleaseMetadata(Repository, invalid.RootElement), "unsafe release rejected");
        }
    }

    private static async Task CheckStatesAndCacheAsync(string folder)
    {
        var mock = new GitHubMock();
        using var service = new UpdateService(new HttpClient(mock), folder, "2.0.0", "");
        Check((await service.CheckAsync("")).Status == UpdateStatus.Unconfigured, "missing repository is unconfigured");
        Check((await service.CheckAsync("https://evil.example")).Status == UpdateStatus.Unconfigured && mock.Requests == 0, "arbitrary URL causes no network request");
        mock.Offline = true;
        Check((await service.CheckAsync(Repository)).Status == UpdateStatus.Unavailable, "first offline check allows app without false up-to-date claim");
        mock.Offline = false;
        var required = await service.CheckAsync(Repository);
        Check(required.Status == UpdateStatus.Required && required.Info?.Version == "2.1.0", "new mandatory release gates old app");
        mock.Offline = true;
        Check((await service.CheckAsync(Repository)).Status == UpdateStatus.Required, "known required update remains required offline");
        using (var restarted = new UpdateService(new HttpClient(mock), folder, "2.0.0", ""))
            Check((await restarted.CheckAsync(Repository)).Status == UpdateStatus.Required, "offline requirement persists across app restart");
        mock.Offline = false;
        mock.Version = "2.0.0";
        Check((await service.CheckAsync(Repository)).Info?.Version == "2.1.0", "older republished release cannot erase requirement");
        using (var current = new UpdateService(new HttpClient(mock), Path.Combine(folder, "current"), "2.1.0", ""))
            Check((await current.CheckAsync(Repository)).Status == UpdateStatus.UpToDate, "newer installed app stays current");
        mock.Version = "2.1.0";
        mock.Mandatory = false;
        mock.MinimumVersion = "2.0.0";
        using (var optional = new UpdateService(new HttpClient(mock), Path.Combine(folder, "optional"), "2.0.0", ""))
            Check((await optional.CheckAsync(Repository)).Status == UpdateStatus.Available, "optional release available");
        mock.MinimumVersion = "2.1.0";
        using (var minimum = new UpdateService(new HttpClient(mock), Path.Combine(folder, "minimum"), "2.0.0", ""))
            Check((await minimum.CheckAsync(Repository)).Status == UpdateStatus.Required, "minimum supported version enforces gate");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => service.CheckAsync(Repository, canceled.Token), "update check caller cancellation");
        await ExpectAsync<InvalidOperationException>(() => service.ApplyAndRestartAsync(Path.Combine(folder, "not-verified.exe")), "installer rejects unverified path");
        mock.Offline = true;
        string badCacheFolder = Path.Combine(folder, "bad-cache");
        string cachePath = Path.Combine(badCacheFolder, "Updates", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Repository.ToLowerInvariant()))).ToLowerInvariant(), "release-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, "{\"schemaVersion\":1,\"info\":{\"version\":\"../\"}}");
        using var corrupted = new UpdateService(new HttpClient(mock), badCacheFolder, "2.0.0", "");
        Check((await corrupted.CheckAsync(Repository)).Status == UpdateStatus.Unavailable, "invalid cached manifest cannot create a gate");
        var timeoutMock = new GitHubMock();
        using var timed = new UpdateService(new HttpClient(timeoutMock), Path.Combine(folder, "timeout"), "2.0.0", "", TimeSpan.FromMilliseconds(150));
        await timed.CheckAsync(Repository);
        timeoutMock.DelayRequests = true;
        var stopwatch = Stopwatch.StartNew();
        Check((await timed.CheckAsync(Repository)).Status == UpdateStatus.Required && stopwatch.Elapsed < TimeSpan.FromSeconds(2), "internal check timeout keeps cached requirement and returns promptly");
        string blockedFolder = Path.Combine(folder, "blocked-cache");
        Directory.CreateDirectory(blockedFolder);
        await File.WriteAllTextAsync(Path.Combine(blockedFolder, "Updates"), "file blocks directory creation");
        var persistenceMock = new GitHubMock();
        using var noPersistence = new UpdateService(new HttpClient(persistenceMock), blockedFolder, "2.0.0", "");
        Check((await noPersistence.CheckAsync(Repository)).Status == UpdateStatus.Required, "cache write failure cannot ignore verified mandatory release");
        persistenceMock.Offline = true;
        Check((await noPersistence.CheckAsync(Repository)).Status == UpdateStatus.Required, "in-memory requirement survives failed cache write and network loss");
    }

    private static async Task CheckDownloadsAsync(string folder)
    {
        var mock = new GitHubMock { RedirectExecutable = true };
        using var service = new UpdateService(new HttpClient(mock), folder, "2.0.0", "");
        UpdateInfo info = (await service.CheckAsync(Repository)).Info!;
        string path = await service.DownloadAsync(info);
        Check(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(_fixture), "bounded executable download verified and retained");
        Check(mock.CdnRequests == 1, "trusted GitHub release CDN redirect followed");
        await UpdateService.VerifyExecutableAsync(path, info, CancellationToken.None);
        Check(true, "actual Windows x64 PE and file version accepted");
        int priorRequests = mock.Requests;
        Check(await service.DownloadAsync(info) == path && mock.Requests == priorRequests, "verified download reused without network");
        await ExpectAsync<InvalidOperationException>(() => service.DownloadAsync(info with { Repository = "evil/repo" }), "download must follow verified release");
        byte[] changed = _fixture.ToArray();
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        Check(await service.DownloadAsync(info) == path && (await File.ReadAllBytesAsync(path)).SequenceEqual(_fixture), "tampered cache is rejected and redownloaded");
        using var hashFailure = new UpdateService(new HttpClient(new GitHubMock { DownloadBytes = changed }), Path.Combine(folder, "hash-fail"), "2.0.0", "");
        var hashInfo = (await hashFailure.CheckAsync(Repository)).Info!;
        await ExpectAsync<InvalidDataException>(() => hashFailure.DownloadAsync(hashInfo), "incorrect digest rejected");
        Check(!Directory.EnumerateFiles(Path.Combine(folder, "hash-fail"), "*.download", SearchOption.AllDirectories).Any(), "failed partial download cleaned");
        using var shortFailure = new UpdateService(new HttpClient(new GitHubMock { DownloadBytes = _fixture[..^8], OmitLength = true }), Path.Combine(folder, "short-fail"), "2.0.0", "");
        var shortInfo = (await shortFailure.CheckAsync(Repository)).Info!;
        await ExpectAsync<InvalidDataException>(() => shortFailure.DownloadAsync(shortInfo), "truncated streaming download rejected");
        using var versionFailure = new UpdateService(new HttpClient(new GitHubMock { Version = "2.2.0" }), Path.Combine(folder, "version-fail"), "2.0.0", "");
        var versionInfo = (await versionFailure.CheckAsync(Repository)).Info!;
        await ExpectAsync<InvalidDataException>(() => versionFailure.DownloadAsync(versionInfo), "executable/version mismatch rejected");
        var badRedirectMock = new GitHubMock { ExternalRedirect = true };
        using var redirectFailure = new UpdateService(new HttpClient(badRedirectMock), Path.Combine(folder, "redirect-fail"), "2.0.0", "");
        var redirectInfo = (await redirectFailure.CheckAsync(Repository)).Info!;
        await ExpectAsync<InvalidDataException>(() => redirectFailure.DownloadAsync(redirectInfo), "external redirect rejected");
        Check(badRedirectMock.ExternalRequests == 0, "untrusted host never requested");
    }

    private static async Task CheckHelperAsync(string folder)
    {
        // Tests run the same helper script only against a GUID-named disposable app.
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixture");
        string installDirectory = Path.Combine(folder, "App's folder with spaces");
        string cache = Path.Combine(folder, "Updates");
        string source = Path.Combine(cache, "2.1.0", "AuroraTranslate.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(installDirectory);
        foreach (string file in Directory.EnumerateFiles(fixtureDirectory)) File.Copy(file, Path.Combine(installDirectory, Path.GetFileName(file)), true);
        string target = Path.Combine(installDirectory, "AuroraTranslate.exe");
        byte[] oldBytes = _fixture.Concat(Encoding.UTF8.GetBytes("previous disposable test version")).ToArray();
        await File.WriteAllBytesAsync(target, oldBytes);
        await File.WriteAllBytesAsync(source, _fixture);
        string script = Path.Combine(folder, "helper.ps1");
        string settingsPath = Path.Combine(folder, "helper.json");
        string log = Path.Combine(folder, "helper.log");
        await File.WriteAllTextAsync(script, UpdateService.HelperScript, new UTF8Encoding(false));
        var settings = new UpdateService.HelperSettings(source, target, _hash, _fixture.Length, "2.1.0", int.MaxValue, 0, log, cache);
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings));
        Check(!UpdateService.HelperScript.Contains("Invoke-Expression", StringComparison.Ordinal)
            && !UpdateService.HelperScript.Contains("Remove-Item -Recurse", StringComparison.Ordinal), "helper treats paths as data and uses no recursive deletes");
        using var process = StartHelper(script, settingsPath, folder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        Check(process.ExitCode == 0, "helper completed against disposable fixture: " + (File.Exists(log) ? await File.ReadAllTextAsync(log) : "no log"));
        Check((await File.ReadAllBytesAsync(target)).SequenceEqual(_fixture), "helper replaced exact target atomically");
        Check((await File.ReadAllBytesAsync(target + ".previous")).SequenceEqual(oldBytes), "helper preserves previous executable backup");
        string marker = Path.Combine(installDirectory, "restart-marker.txt");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        Check(File.Exists(marker) && await File.ReadAllTextAsync(marker) == "2.1.0", "helper restarted updated fixture from same installation folder");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings with { Sha256 = new string('0', 64) }));
        using var invalid = StartHelper(script, settingsPath, folder);
        await invalid.WaitForExitAsync(timeout.Token);
        Check(invalid.ExitCode != 0 && (await File.ReadAllBytesAsync(target)).SequenceEqual(_fixture), "helper rechecks digest before touching installed app");
    }

    private static Process StartHelper(string script, string settings, string folder)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = folder };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-SettingsPath", settings }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
    }
    private static void Expect<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(label);
    }
    private static async Task ExpectAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(label);
    }

    private sealed class GitHubMock : HttpMessageHandler
    {
        public string Version = "2.1.0";
        public string? MinimumVersion;
        public bool Mandatory = true, Offline, RedirectExecutable, ExternalRedirect, OmitLength, DelayRequests;
        public byte[]? DownloadBytes;
        public int Requests, CdnRequests, ExternalRequests;
        private string ReleaseUrl => $"https://github.com/{Repository}/releases/tag/v{Version}";
        private string ManifestUrl => $"https://github.com/{Repository}/releases/download/v{Version}/update.json";
        private string ExecutableUrl => $"https://github.com/{Repository}/releases/download/v{Version}/AuroraTranslate.exe";
        public string ManifestText() => JsonSerializer.Serialize(new
        {
            version = Version, minimumSupportedVersion = MinimumVersion ?? Version, mandatory = Mandatory,
            releaseNotes = "Improved conversation and automatic updates.", assetName = "AuroraTranslate.exe", sha256 = _hash, sizeBytes = _fixture.Length
        });
        public byte[] ManifestBytes() => Encoding.UTF8.GetBytes(ManifestText());
        public string ReleaseJson() => JsonSerializer.Serialize(new
        {
            tag_name = "v" + Version, html_url = ReleaseUrl, draft = false, prerelease = false,
            assets = new[]
            {
                new { name = "update.json", browser_download_url = ManifestUrl, state = "uploaded", size = ManifestBytes().Length, digest = (string?)null },
                new { name = "AuroraTranslate.exe", browser_download_url = ExecutableUrl, state = "uploaded", size = _fixture.Length, digest = (string?)("sha256:" + _hash) }
            }
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (DelayRequests) return WaitForCancellationAsync(cancellationToken);
            if (Offline) throw new HttpRequestException("Offline test.");
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null
                && request.Headers.UserAgent.Any(), "public GitHub GET with user agent and no auth secret");
            string url = request.RequestUri!.AbsoluteUri;
            HttpResponseMessage response;
            if (url == $"https://api.github.com/repos/{Repository}/releases/latest")
            {
                Check(request.Headers.Contains("X-GitHub-Api-Version"), "GitHub API version header");
                response = new(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson(), Encoding.UTF8, "application/json") };
            }
            else if (url == ManifestUrl) response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(ManifestBytes()) };
            else if (url == ExecutableUrl && (RedirectExecutable || ExternalRedirect))
            {
                response = new(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(ExternalRedirect ? "https://evil.example/update.exe" : "https://release-assets.githubusercontent.com/github-production-release-asset/test-fixture?signature=abc");
            }
            else if (url == ExecutableUrl || request.RequestUri.Host == "release-assets.githubusercontent.com")
            {
                if (request.RequestUri.Host == "release-assets.githubusercontent.com") CdnRequests++;
                response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes ?? _fixture) };
                if (OmitLength) response.Content.Headers.ContentLength = null;
            }
            else
            {
                ExternalRequests++;
                throw new InvalidOperationException("Mock update attempted an unrecognized network origin.");
            }
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { }
        private static async Task<HttpResponseMessage> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The timeout test should have been canceled.");
        }
    }
}
