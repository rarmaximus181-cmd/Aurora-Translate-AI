using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuroraTranslate;
using AuroraTranslate.Services;

internal static class DiscordChecks
{
    private static int _checks;
    private const string ClientId = "123456789012345678";
    private const string UserId = "333333333333333333";
    private const string AvatarHash = "a_0123456789abcdef0123456789abcdef";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static async Task<int> Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AuroraDiscordChecks-" + Guid.NewGuid().ToString("N"));
        string? priorDataDirectory = Environment.GetEnvironmentVariable("AURORA_DATA_DIR");
        Environment.SetEnvironmentVariable("AURORA_DATA_DIR", folder);
        try
        {
            CheckValidation();
            await CheckActualLoopbackAsync();
            await CheckSignInAndPersistenceAsync(folder);
            await CheckBusyPortAsync();
            Console.WriteLine($"PASS: {_checks} Discord protocol and storage checks; no live Discord account or credentials used.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AURORA_DATA_DIR", priorDataDirectory);
            // Only the fresh GUID-named test directory is removed.
            string canonical = Path.GetFullPath(folder);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (canonical.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(canonical).StartsWith("AuroraDiscordChecks-", StringComparison.Ordinal) && Directory.Exists(canonical))
                Directory.Delete(canonical, true);
        }
    }

    private static void CheckValidation()
    {
        Check(DiscordAuthService.IsValidClientId(ClientId), "numeric client ID");
        foreach (string? id in new string?[] { null, "", "token-abc", "-123456789012345678", "123456789012345678 ", "99999999999999999999", "00000000000000000000" })
            Check(!DiscordAuthService.IsValidClientId(id), "reject invalid client ID");
        Check(DiscordAuthService.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "RFC 7636 PKCE S256 vector");
        Check(DiscordAuthService.StateMatches("secret", "secret") && !DiscordAuthService.StateMatches("secret", "other"), "state comparison");
        const string state = "known-state";
        string Request(string query, string method = "GET", string path = "/callback", string host = "127.0.0.1:43821")
            => $"{method} {path}?{query} HTTP/1.1\r\nHost: {host}\r\n\r\n";
        var valid = DiscordAuthService.ParseCallbackRequest(Request("state=known-state&code=good%2Bcode"), 43821, state);
        Check(valid.Valid && valid.Code == "good+code", "valid callback decoding");
        var declined = DiscordAuthService.ParseCallbackRequest(Request("state=known-state&error=access_denied"), 43821, state);
        Check(declined.Valid && declined.Error == "access_denied", "declined callback");
        foreach (string invalid in new[]
        {
            Request("state=wrong&code=good"), Request("code=good"), Request("state=known-state"),
            Request("state=known-state&state=known-state&code=good"), Request("state=known-state&%73tate=known-state&code=good"),
            Request("state=known-state&code=one&code=two"), Request("state=known-state&code=one&error=access_denied"),
            Request("state=known-state&code=%xx"), Request("state=known-state&code=bad%0Dcode"),
            Request("state=known-state&code=good&iss=https%3A%2F%2Fevil.example"),
            Request("state=known-state&code=good", method: "POST"), Request("state=known-state&code=good", path: "/"),
            Request("state=known-state&code=good", path: "http://127.0.0.1:43821/callback"),
            Request("state=known-state&code=good", host: "evil.example"),
            "GET /callback?state=known-state&code=good HTTP/1.1\r\nHost: 127.0.0.1:43821\r\nHost: evil.example\r\n\r\n",
            new string('x', 8193)
        }) Check(!DiscordAuthService.ParseCallbackRequest(invalid, 43821, state).Valid, "reject unsafe callback");
        Check(DiscordAuthService.CreateAvatarUrl(UserId, AvatarHash, "0")
            == $"https://cdn.discordapp.com/avatars/{UserId}/{AvatarHash}.png?size=128", "animated avatar requested as PNG");
        Check(DiscordAuthService.CreateAvatarUrl(UserId, null, "0007") == "https://cdn.discordapp.com/embed/avatars/2.png?size=128", "legacy default avatar");
        int migratedIndex = (int)((ulong.Parse(UserId) >> 22) % 6);
        Check(DiscordAuthService.CreateAvatarUrl(UserId, null, "0") == $"https://cdn.discordapp.com/embed/avatars/{migratedIndex}.png?size=128", "migrated default avatar");
        Expect<InvalidOperationException>(() => DiscordAuthService.CreateAvatarUrl(UserId, "../../evil", "0"), "reject avatar traversal");
        Check(!DiscordAuthService.IsValidAvatarUrl("https://evil.example/a.png?size=128")
            && !DiscordAuthService.IsValidAvatarUrl("http://cdn.discordapp.com/embed/avatars/1.png?size=128")
            && !DiscordAuthService.IsValidAvatarUrl("https://cdn.discordapp.com/avatars/123456789012345678/0123456789abcdef0123456789abcdef.png?size=128&redirect=evil"), "CDN URL allowlist");
        Check(DiscordAuthService.IsSafePng(Png), "valid small PNG bounds");
        byte[] huge = Png.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16, 4), 100_000);
        Check(!DiscordAuthService.IsSafePng(huge) && !DiscordAuthService.IsSafePng(new byte[24]), "reject invalid and oversized PNG");
        using var malformedProfile = JsonDocument.Parse("{\"id\":\"bad\",\"username\":\"x\"}");
        Expect<InvalidOperationException>(() => DiscordAuthService.ParseProfile(malformedProfile.RootElement), "reject malformed profile");
    }

    private static async Task CheckActualLoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task<string> pending = DiscordAuthService.WaitForAuthorizationCodeAsync(listener, port, "socket-state", deadline.Token);
            string invalid = await SendCallbackAsync(port, "state=wrong&code=untrusted", deadline.Token);
            Check(invalid.StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "invalid callback receives 400");
            Check(!pending.IsCompleted, "invalid callback does not end real login");
            string valid = await SendCallbackAsync(port, "state=socket-state&code=socket-code", deadline.Token);
            Check(await pending == "socket-code", "real loopback delivers matching code");
            Check(valid.Contains("Cache-Control: no-store", StringComparison.Ordinal)
                && valid.Contains("Referrer-Policy: no-referrer", StringComparison.Ordinal)
                && valid.Contains("Content-Security-Policy:", StringComparison.Ordinal), "callback response does not leak code");
        }
        finally { listener.Stop(); }
    }

    private static async Task CheckSignInAndPersistenceAsync(string folder)
    {
        var protocol = new MockProtocol();
        Action<string> browser = url => protocol.AuthorizeInTestBrowser(url);
        using (var service = new DiscordAuthService(new HttpClient(protocol), browser, 0))
        {
            Check(!service.HasCredentials && service.LoadProfile() is null, "unconfigured service");
            service.Configure(ClientId);
            Check(service.HasCredentials && service.ClientId == ClientId, "save public app ID");
            Expect<InvalidOperationException>(() => service.Configure("a-token"), "reject token pasted as app ID");
            var profile = await service.SignInAsync();
            Check(profile.Id == UserId && profile.Username == "aurora_user" && profile.DisplayName == "Aurora User", "verified basic profile");
            Check(profile.AvatarPath is not null && File.Exists(profile.AvatarPath), "cache downloaded avatar locally");
            Check(File.ReadAllBytes(profile.AvatarPath!).SequenceEqual(Png), "cache contains fetched PNG");
            Check(protocol.BrowserLaunches == 1 && protocol.CodeExchanges == 1 && protocol.RefreshExchanges == 0, "browser/code flow once");
            Check(service.LoadProfile()?.Id == UserId, "cached profile available");
            byte[] encrypted = File.ReadAllBytes(Path.Combine(folder, "discord-session.bin"));
            Check(!Encoding.UTF8.GetString(encrypted).Contains("test-access-", StringComparison.Ordinal), "session is not plaintext");
            byte[] decrypted = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("AuroraTranslate.DiscordOAuth.v1"), DataProtectionScope.CurrentUser);
            try { Check(Encoding.UTF8.GetString(decrypted).Contains("test-refresh-", StringComparison.Ordinal), "Windows-user DPAPI decrypts stored session"); }
            finally { CryptographicOperations.ZeroMemory(decrypted); }
        }
        using (var restored = new DiscordAuthService(new HttpClient(protocol), browser, 0))
        {
            Check(restored.HasCredentials && restored.ClientId == ClientId && restored.LoadProfile()?.Id == UserId, "restore protected app ID and session");
            var profile = await restored.SignInAsync();
            Check(profile.Id == UserId && protocol.RefreshExchanges == 1 && protocol.BrowserLaunches == 1, "expired-soon token refreshes without browser");
            protocol.FailNextRefresh = true;
            await restored.SignInAsync();
            Check(protocol.RefreshExchanges == 2 && protocol.BrowserLaunches == 2 && protocol.CodeExchanges == 2, "invalid refresh recovers through fresh consent");
            profile = restored.LoadProfile()!;
            await restored.SignOutAsync();
            Check(restored.LoadProfile() is null && !File.Exists(Path.Combine(folder, "discord-session.bin")), "sign-out removes protected session");
            Check(profile.AvatarPath is null || !File.Exists(profile.AvatarPath), "sign-out removes current cached avatar");
            Check(restored.HasCredentials && protocol.Revocations == 1, "public app setting retained and token revoked");
        }
        using (var restored = new DiscordAuthService(new HttpClient(protocol), browser, 0))
            Check(restored.HasCredentials && restored.LoadProfile() is null, "sign-out persists after restart");
        using (var denied = new DiscordAuthService(new HttpClient(new MockProtocol()), url =>
        {
            var query = ParseForm(new Uri(url).Query.TrimStart('?'));
            var redirect = new Uri(query["redirect_uri"]);
            _ = SendCallbackAsync(redirect.Port, "state=" + Uri.EscapeDataString(query["state"]) + "&error=access_denied", CancellationToken.None);
        }, 0))
        {
            await ExpectAsync<InvalidOperationException>(() => denied.SignInAsync(), "browser denial handled");
            Check(denied.LoadProfile() is null, "denial does not create identity");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => denied.SignInAsync(canceled.Token), "caller cancellation honored");
        }
    }

    private static async Task CheckBusyPortAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var service = new DiscordAuthService(new HttpClient(new MockProtocol()), _ => throw new Exception("Browser must not open for busy port"), port);
            await ExpectAsync<InvalidOperationException>(() => service.SignInAsync(), "exclusive callback port rejects conflicts");
        }
        finally { listener.Stop(); }
    }

    private static async Task<string> SendCallbackAsync(int port, string query, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        using var stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes($"GET /callback?{query} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response, cancellationToken);
        return Encoding.UTF8.GetString(response.ToArray());
    }

    private static Dictionary<string, string> ParseForm(string text) => text.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace('+', ' ')), StringComparer.Ordinal);
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

    private sealed class MockProtocol : HttpMessageHandler
    {
        public int BrowserLaunches, CodeExchanges, RefreshExchanges, Revocations;
        public bool FailNextRefresh;
        private string? _challenge, _redirectUri;
        private string _latestAccessToken = "";

        public void AuthorizeInTestBrowser(string url)
        {
            var uri = new Uri(url);
            var query = ParseForm(uri.Query.TrimStart('?'));
            Check(uri.GetLeftPart(UriPartial.Path) == "https://discord.com/oauth2/authorize", "browser opens official HTTPS origin");
            Check(query["scope"] == "identify" && query["response_type"] == "code" && query["code_challenge_method"] == "S256", "identify-only PKCE authorization");
            Check(query["state"].Length == 43 && query["code_challenge"].Length == 43 && query["client_id"] == ClientId, "random state and challenge");
            _challenge = query["code_challenge"];
            _redirectUri = query["redirect_uri"];
            Check(!query.ContainsKey("client_secret") && !query.ContainsKey("code_verifier"), "browser receives no secret/verifier");
            BrowserLaunches++;
            var redirect = new Uri(_redirectUri);
            _ = Task.Run(async () => await SendCallbackAsync(redirect.Port,
                "state=" + Uri.EscapeDataString(query["state"]) + "&code=test-code", CancellationToken.None));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://discord.com/api/v10/oauth2/token")
            {
                var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Check(request.Method == HttpMethod.Post && request.Headers.Authorization is null && form["client_id"] == ClientId
                    && !form.ContainsKey("client_secret"), "public client token POST without secret");
                if (form["grant_type"] == "authorization_code")
                {
                    Check(form["code"] == "test-code" && form["redirect_uri"] == _redirectUri
                        && DiscordAuthService.CreateCodeChallenge(form["code_verifier"]) == _challenge, "exchange bound to callback and PKCE verifier");
                    CodeExchanges++;
                }
                else
                {
                    Check(form["grant_type"] == "refresh_token" && form["refresh_token"].StartsWith("test-refresh-", StringComparison.Ordinal), "refresh token flow");
                    RefreshExchanges++;
                    if (FailNextRefresh)
                    {
                        FailNextRefresh = false;
                        return JsonResponse("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest);
                    }
                }
                _latestAccessToken = "test-access-" + (CodeExchanges + RefreshExchanges);
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    access_token = _latestAccessToken, refresh_token = "test-refresh-" + (CodeExchanges + RefreshExchanges),
                    token_type = "Bearer", expires_in = 10, scope = "identify"
                }));
            }
            if (url == "https://discord.com/api/v10/users/@me")
            {
                Check(request.Method == HttpMethod.Get && request.Headers.Authorization?.Scheme == "Bearer"
                    && request.Headers.Authorization.Parameter == _latestAccessToken, "profile fetched with OAuth bearer");
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    id = UserId, username = "aurora_user", global_name = "Aurora User", avatar = AvatarHash, discriminator = "0"
                }));
            }
            if (url.StartsWith("https://cdn.discordapp.com/", StringComparison.Ordinal))
            {
                Check(request.Headers.Authorization is null && DiscordAuthService.IsValidAvatarUrl(url), "avatar CDN receives no bearer token");
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return response;
            }
            if (url == "https://discord.com/api/v10/oauth2/token/revoke")
            {
                var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                Check(form["client_id"] == ClientId && form["token"].StartsWith("test-refresh-", StringComparison.Ordinal)
                    && !form.ContainsKey("client_secret"), "public revocation request");
                Revocations++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            throw new InvalidOperationException("Unexpected external request in isolated Discord checks.");
        }

        // Multiple simulated app restarts share the server-side protocol state.
        protected override void Dispose(bool disposing) { }
        private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
