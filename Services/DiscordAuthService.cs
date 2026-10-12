using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraTranslate.Services;

/// <summary>
/// Optional identify-only Discord OAuth sign-in for a registered Public Client.
/// Authorization uses the system browser, PKCE and a loopback callback. No client
/// secret, Discord password, bot token or ordinary user token is accepted.
/// </summary>
public sealed class DiscordAuthService : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
    private const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
    private const string UserInfoEndpoint = "https://discord.com/api/v10/users/@me";
    private const string RevocationEndpoint = "https://discord.com/api/v10/oauth2/token/revoke";
    private const int MaximumJsonBytes = 64 * 1024;
    private const int MaximumHeaderBytes = 8 * 1024;
    private const int MaximumAvatarBytes = 2 * 1024 * 1024;
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("AuroraTranslate.DiscordOAuth.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 12 };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http;
    private readonly Action<string> _openBrowser;
    private readonly int _callbackPort;
    private readonly string _credentialsPath;
    private readonly string _sessionPath;
    private readonly string _avatarDirectory;
    private string? _clientId;
    private DiscordSession? _session;
    private CancellationTokenSource? _activeSignIn;
    private bool _disposed;

    public DiscordAuthService() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    }) { }

    // The injectable transport/browser make protocol checks possible without actual accounts.
    internal DiscordAuthService(HttpClient http, Action<string>? openBrowser = null, int callbackPort = 43821)
    {
        _http = http;
        _openBrowser = openBrowser ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        _callbackPort = callbackPort;
        var folder = Path.GetFullPath(AppStore.DataDirectory);
        _credentialsPath = Path.Combine(folder, "discord-client.bin");
        _sessionPath = Path.Combine(folder, "discord-session.bin");
        _avatarDirectory = Path.Combine(folder, "DiscordAvatars");
        var credentials = ReadProtected<DiscordCredentials>(_credentialsPath);
        if (credentials is not null && IsValidClientId(credentials.ClientId))
        {
            _clientId = credentials.ClientId;
            var session = ReadProtected<DiscordSession>(_sessionPath);
            if (session is not null && session.ClientId == _clientId && IsValidSession(session))
                _session = session with { Profile = SanitizeCachedAvatar(session.Profile) };
        }
    }

    public bool HasCredentials { get { lock (_sync) return !_disposed && _clientId is not null; } }
    public string? ClientId { get { lock (_sync) return _disposed ? null : _clientId; } }

    /// <summary>The ID is public application metadata, not an API key or account token.</summary>
    public void Configure(string clientId)
    {
        ThrowIfDisposed();
        clientId = clientId?.Trim() ?? "";
        if (!IsValidClientId(clientId))
            throw new InvalidOperationException("Paste the numeric Application ID from your Discord Developer Portal app. Do not paste a token or client secret.");
        if (!_operation.Wait(0))
            throw new InvalidOperationException("Finish or cancel the current Discord sign-in before changing its Application ID.");
        try
        {
            ThrowIfDisposed();
            WriteProtected(_credentialsPath, new DiscordCredentials(clientId));
            lock (_sync)
            {
                if (_clientId != clientId) ClearSession();
                _clientId = clientId;
            }
        }
        finally { _operation.Release(); }
    }

    /// <summary>Cached identity; SignInAsync verifies and refreshes its authorization online.</summary>
    public DiscordProfile? LoadProfile()
    {
        lock (_sync)
            return !_disposed && _session is not null && (!string.IsNullOrEmpty(_session.RefreshToken)
                || _session.AccessTokenExpiresUtc > DateTimeOffset.UtcNow) ? _session.Profile : null;
    }

    public async Task<DiscordProfile> SignInAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operation.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            string clientId;
            DiscordSession? session;
            lock (_sync)
            {
                clientId = _clientId ?? throw new InvalidOperationException("Save your Discord Application ID in Settings first. Enable Public Client and add the redirect URL using DISCORD-SIGN-IN.md.");
                session = _session;
                _activeSignIn = operationCancellation;
            }
            operationCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            var token = operationCancellation.Token;
            if (session is not null)
            {
                try
                {
                    var current = await VerifySavedSessionAsync(clientId, session, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    SaveSession(current);
                    return current.Profile;
                }
                catch (InvalidSessionException) { ClearSession(); }
            }
            var fresh = await AuthorizeInBrowserAsync(clientId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SaveSession(fresh);
            return fresh.Profile;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Discord sign-in timed out. Try again and approve the Discord page within three minutes.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Discord could not be reached. Check your internet connection and try again.");
        }
        catch (InvalidSessionException)
        {
            throw new InvalidOperationException("The Discord approval expired or was rejected. Sign in again for a new approval.");
        }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(_activeSignIn, operationCancellation)) _activeSignIn = null;
            _operation.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_sync) _activeSignIn?.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            string? clientId, tokenToRevoke;
            lock (_sync)
            {
                clientId = _clientId;
                tokenToRevoke = _session?.RefreshToken ?? _session?.AccessToken;
            }
            ClearSession();
            if (clientId is null || string.IsNullOrEmpty(tokenToRevoke) || cancellationToken.IsCancellationRequested) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, RevocationEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = clientId, ["token"] = tokenToRevoke
                    })
                };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                // Offline/rejected revocation cannot undo local sign-out. Discord's Authorized Apps
                // page remains available for manual revocation of the account's grant.
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally { _operation.Release(); }
    }

    private async Task<DiscordSession> AuthorizeInBrowserAsync(string clientId, CancellationToken cancellationToken)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, _callbackPort);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(8); }
        catch (SocketException)
        {
            throw new InvalidOperationException("Discord's local sign-in port is busy. Close another Aurora Translate window and try again.");
        }
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/callback";
            string authorizationUrl = BuildAuthorizationUrl(clientId, redirectUri, state, verifier);
            try { _openBrowser(authorizationUrl); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException("Your default browser could not be opened. Set a default browser in Windows and try again.");
            }
            string code = await WaitForAuthorizationCodeAsync(listener, port, state, cancellationToken).ConfigureAwait(false);
            var tokens = await ExchangeTokensAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier
            }, null, cancellationToken).ConfigureAwait(false);
            var profile = await GetProfileAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            return new DiscordSession(clientId, tokens.AccessToken, tokens.RefreshToken, tokens.AccessTokenExpiresUtc, profile);
        }
        finally { listener.Stop(); }
    }

    internal static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string verifier)
        => AuthorizationEndpoint + "?" + FormQuery(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirectUri, ["response_type"] = "code",
            ["scope"] = "identify", ["state"] = state, ["code_challenge"] = CreateCodeChallenge(verifier),
            ["code_challenge_method"] = "S256", ["prompt"] = "consent"
        });

    private async Task<DiscordSession> VerifySavedSessionAsync(string clientId, DiscordSession session, CancellationToken cancellationToken)
    {
        var current = session;
        if (current.AccessTokenExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(60))
            current = await RefreshAsync(clientId, current, cancellationToken).ConfigureAwait(false);
        DiscordProfile profile;
        try { profile = await GetProfileAsync(current.AccessToken, cancellationToken).ConfigureAwait(false); }
        catch (InvalidSessionException) when (current.AccessToken == session.AccessToken && !string.IsNullOrEmpty(session.RefreshToken))
        {
            current = await RefreshAsync(clientId, session, cancellationToken).ConfigureAwait(false);
            profile = await GetProfileAsync(current.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        if (profile.Id != session.Profile.Id) throw new InvalidSessionException();
        return current with { Profile = profile };
    }

    private async Task<DiscordSession> RefreshAsync(string clientId, DiscordSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.RefreshToken)) throw new InvalidSessionException();
        var tokens = await ExchangeTokensAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken
        }, session, cancellationToken).ConfigureAwait(false);
        return session with { AccessToken = tokens.AccessToken, RefreshToken = tokens.RefreshToken, AccessTokenExpiresUtc = tokens.AccessTokenExpiresUtc };
    }

    private async Task<TokenResult> ExchangeTokensAsync(Dictionary<string, string> parameters, DiscordSession? previous, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var json = await ReadResponseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? error = GetString(json.RootElement, "error");
            if (error == "invalid_grant") throw new InvalidSessionException();
            if (error is "invalid_client" or "unauthorized_client")
                throw new InvalidOperationException("Discord rejected this app. Enable Public Client in the app's OAuth2 page and check the Application ID and exact redirect URL in DISCORD-SIGN-IN.md.");
            throw new InvalidOperationException("Discord could not complete sign-in. Check the app's OAuth2 configuration or try again later.");
        }
        var root = json.RootElement;
        string? accessToken = GetString(root, "access_token");
        string? refreshToken = GetString(root, "refresh_token") ?? previous?.RefreshToken;
        string? scope = GetString(root, "scope");
        if (!IsValidToken(accessToken) || (refreshToken is not null && !IsValidToken(refreshToken))
            || !string.Equals(GetString(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase)
            || scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("identify", StringComparer.Ordinal) != true
            || !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt64(out long seconds)
            || seconds <= 0 || seconds > 31 * 24 * 60 * 60)
            throw new InvalidOperationException("Discord returned an incomplete sign-in response. Try again.");
        return new TokenResult(accessToken!, refreshToken, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private async Task<DiscordProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidSessionException();
        using var json = await ReadResponseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Discord could not load your profile. Try again later.");
        var profile = ParseProfile(json.RootElement);
        string? avatarPath = await CacheAvatarAsync(profile.AvatarUrl, cancellationToken).ConfigureAwait(false);
        return profile with { AvatarPath = avatarPath };
    }

    internal static DiscordProfile ParseProfile(JsonElement root)
    {
        string? id = GetString(root, "id");
        string? username = GetString(root, "username");
        if (!IsValidSnowflake(id) || !IsValidName(username)
            || (root.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True))
            throw new InvalidOperationException("Discord did not return a valid user profile. Try signing in again.");
        string? displayName = GetString(root, "global_name");
        if (!IsValidName(displayName)) displayName = username;
        string avatarUrl = CreateAvatarUrl(id!, GetString(root, "avatar"), GetString(root, "discriminator"));
        return new DiscordProfile(id!, username!, displayName!, avatarUrl);
    }

    internal static string CreateAvatarUrl(string id, string? avatarHash, string? discriminator)
    {
        if (!IsValidSnowflake(id)) throw new InvalidOperationException("Discord returned an invalid profile ID.");
        if (avatarHash is not null)
        {
            if (!Regex.IsMatch(avatarHash, @"\A(?:a_)?[0-9a-f]{32}\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Discord returned an invalid avatar.");
            return $"https://cdn.discordapp.com/avatars/{id}/{avatarHash}.png?size=128";
        }
        ulong userId = ulong.Parse(id, CultureInfo.InvariantCulture);
        int index = (int)((userId >> 22) % 6);
        if (!string.IsNullOrEmpty(discriminator) && discriminator != "0")
        {
            if (discriminator.Length != 4 || !int.TryParse(discriminator, NumberStyles.None, CultureInfo.InvariantCulture, out int legacy))
                throw new InvalidOperationException("Discord returned an invalid profile discriminator.");
            index = legacy % 5;
        }
        return $"https://cdn.discordapp.com/embed/avatars/{index}.png?size=128";
    }

    private async Task<string?> CacheAvatarAsync(string avatarUrl, CancellationToken cancellationToken)
    {
        if (!IsValidAvatarUrl(avatarUrl)) return null;
        string destination = Path.Combine(_avatarDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(avatarUrl))).ToLowerInvariant() + ".png");
        if (File.Exists(destination) && new FileInfo(destination).Length is > 0 and <= MaximumAvatarBytes) return destination;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, avatarUrl);
            // Never attach the account's bearer token to public CDN requests.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/png") return null;
            byte[] bytes = await ReadBoundedContentAsync(response, MaximumAvatarBytes, deadline.Token).ConfigureAwait(false);
            if (!IsSafePng(bytes)) return null;
            Directory.CreateDirectory(_avatarDirectory);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, deadline.Token).ConfigureAwait(false);
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    internal static bool IsSafePng(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width is > 0 and <= 512 && height is > 0 and <= 512;
    }

    private DiscordProfile SanitizeCachedAvatar(DiscordProfile profile)
    {
        if (profile.AvatarPath is not null)
        {
            string expected = Path.Combine(_avatarDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.AvatarUrl))).ToLowerInvariant() + ".png");
            if (string.Equals(profile.AvatarPath, expected, StringComparison.OrdinalIgnoreCase) && File.Exists(expected)
                && new FileInfo(expected).Length is > 0 and <= MaximumAvatarBytes) return profile;
        }
        return profile with { AvatarPath = null };
    }

    internal static async Task<string> WaitForAuthorizationCodeAsync(TcpListener listener, int port, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            using var stream = client.GetStream();
            try
            {
                string headers = await ReadHeadersAsync(stream, deadline.Token).ConfigureAwait(false);
                var callback = ParseCallbackRequest(headers, port, expectedState);
                if (!callback.Valid)
                {
                    await WriteBrowserResponseAsync(stream, callback.HttpStatus, "This request does not belong to the pending Discord sign-in. Return to Discord's approval page.", deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (callback.Error is not null)
                {
                    try { await WriteBrowserResponseAsync(stream, 200, "Discord sign-in was not completed. Return to Aurora Translate to try again.", deadline.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                    throw new InvalidOperationException(callback.Error == "access_denied"
                        ? "Discord sign-in was declined. Aurora Translate still works without an account."
                        : "Discord sign-in could not be completed. Check your app's OAuth2 settings and try again.");
                }
                try { await WriteBrowserResponseAsync(stream, 200, "Discord approval received. Return to Aurora Translate to finish signing in. You may close this tab.", deadline.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                return callback.Code!;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes];
        int used = 0;
        while (used < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Incomplete callback.");
            int previous = used;
            used += count;
            for (int i = Math.Max(0, previous - 3); i <= used - 4; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return Encoding.ASCII.GetString(buffer, 0, i + 4);
        }
        throw new InvalidDataException("Callback headers exceeded their limit.");
    }

    internal static CallbackResult ParseCallbackRequest(string headers, int port, string expectedState)
    {
        if (headers.Length > MaximumHeaderBytes) return new(false, 431, null, null);
        var lines = headers.Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ', StringSplitOptions.None);
        if (request.Length != 3 || request[0] != "GET") return new(false, 405, null, null);
        if (request[2] is not ("HTTP/1.1" or "HTTP/1.0")) return new(false, 400, null, null);
        string target = request[1];
        int queryOffset = target.IndexOf('?');
        string path = queryOffset < 0 ? target : target[..queryOffset];
        if (path != "/callback" || target.Contains('#')) return new(false, 404, null, null);
        string expectedHost = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        var hostHeaders = lines.Skip(1).Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hostHeaders.Length != 1 || hostHeaders[0][5..].Trim() != expectedHost) return new(false, 400, null, null);
        if (queryOffset < 0) return new(false, 400, null, null);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string part in target[(queryOffset + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                string key = DecodeQueryComponent(pair[0]);
                string value = pair.Length == 2 ? DecodeQueryComponent(pair[1]) : "";
                if (key is not ("state" or "code" or "error" or "iss")) continue;
                if (!query.TryAdd(key, value)) return new(false, 400, null, null);
            }
        }
        catch (UriFormatException) { return new(false, 400, null, null); }
        if (!query.TryGetValue("state", out string? state) || !StateMatches(expectedState, state)) return new(false, 400, null, null);
        if (query.TryGetValue("iss", out var issuer) && issuer != "https://discord.com") return new(false, 400, null, null);
        bool hasCode = query.TryGetValue("code", out string? code) && !string.IsNullOrWhiteSpace(code);
        bool hasError = query.TryGetValue("error", out string? error) && !string.IsNullOrWhiteSpace(error);
        if (hasCode == hasError || (code?.Length ?? 0) > 4096 || (error?.Length ?? 0) > 256
            || code?.Any(char.IsControl) == true || error?.Any(char.IsControl) == true) return new(false, 400, null, null);
        return new(true, 200, code, error);
    }

    internal static bool StateMatches(string expected, string actual) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
    internal static string CreateCodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    internal static bool IsValidClientId(string? value) => IsValidSnowflake(value);
    private static bool IsValidSnowflake(string? value) => value is not null && value.Length is >= 16 and <= 20
        && value.All(character => character is >= '0' and <= '9')
        && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id > 0;
    private static bool IsValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool IsValidToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    internal static bool IsValidAvatarUrl(string? value) => value is not null && Regex.IsMatch(value,
        @"\Ahttps://cdn\.discordapp\.com/(?:avatars/[0-9]{16,20}/(?:a_)?[0-9a-f]{32}|embed/avatars/[0-5])\.png\?size=128\z", RegexOptions.CultureInvariant);
    private static bool IsValidSession(DiscordSession session) => IsValidToken(session.AccessToken)
        && (session.RefreshToken is null || IsValidToken(session.RefreshToken)) && session.Profile is not null
        && IsValidSnowflake(session.Profile.Id) && IsValidName(session.Profile.Username) && IsValidName(session.Profile.DisplayName)
        && IsValidAvatarUrl(session.Profile.AvatarUrl);
    private static string? GetString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string FormQuery(Dictionary<string, string> parameters) => string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static string DecodeQueryComponent(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))) throw new UriFormatException();
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static async Task WriteBrowserResponseAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        string reason = status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", 431 => "Request Header Fields Too Large", _ => "Bad Request" };
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Aurora Translate</title><style>body{background:#101529;color:#eef4ff;font:18px system-ui;max-width:640px;margin:15vh auto;padding:32px}h1{color:#8be7de}p{line-height:1.7}</style><h1>Aurora Translate</h1><p>" + WebUtility.HtmlEncode(message) + "</p></html>");
        string headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedContentAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeded its size limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("Response exceeded its size limit.");
            output.Write(buffer, 0, count);
        }
    }

    private static async Task<JsonDocument> ReadResponseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await ReadBoundedContentAsync(response, MaximumJsonBytes, cancellationToken).ConfigureAwait(false);
            using var input = new MemoryStream(bytes, false);
            return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidOperationException("Discord returned an unexpected sign-in response. Try again.");
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    private static T? ReadProtected<T>(string path) where T : class
    {
        byte[]? plaintext = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumJsonBytes) return null;
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), ProtectionEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException) { return null; }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void WriteProtected<T>(string path, T value)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        byte[]? encrypted = null;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            encrypted = ProtectedData.Protect(plaintext, ProtectionEntropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(encrypted);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void SaveSession(DiscordSession session)
    {
        WriteProtected(_sessionPath, session);
        lock (_sync) _session = session;
    }

    private void ClearSession()
    {
        string? avatarPath;
        lock (_sync) { avatarPath = _session?.Profile.AvatarPath; _session = null; }
        if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
        if (avatarPath is not null && string.Equals(Path.GetDirectoryName(avatarPath), _avatarDirectory, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(avatarPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() { lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _clientId = null;
            _session = null;
        }
        _lifetime.Cancel();
        _http.Dispose();
        // Semaphore and cancellation source outlive operations unwinding cancellation.
    }

    private sealed record DiscordCredentials(string ClientId);
    internal sealed record CallbackResult(bool Valid, int HttpStatus, string? Code, string? Error);
    private sealed record TokenResult(string AccessToken, string? RefreshToken, DateTimeOffset AccessTokenExpiresUtc);
    private sealed record DiscordSession(string ClientId, string AccessToken, string? RefreshToken, DateTimeOffset AccessTokenExpiresUtc, DiscordProfile Profile);
    private sealed class InvalidSessionException : Exception { }
}
