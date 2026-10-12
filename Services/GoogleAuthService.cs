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
/// Optional Google desktop sign-in. Profile and OAuth data stay on this Windows account;
/// this service does not upload translation history or provide cloud synchronization.
/// </summary>
public sealed class GoogleAuthService : IDisposable
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
    private const string RevocationEndpoint = "https://oauth2.googleapis.com/revoke";
    private const int MaximumJsonBytes = 64 * 1024;
    private const int MaximumHeaderBytes = 8 * 1024;
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("AuroraTranslate.GoogleOAuth.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 12 };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _http;
    private readonly string _credentialsPath;
    private readonly string _sessionPath;
    private OAuthCredentials? _credentials;
    private GoogleSession? _session;
    private CancellationTokenSource? _activeSignIn;
    private bool _disposed;

    public GoogleAuthService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuroraTranslate");
        _credentialsPath = Path.Combine(folder, "google-client.bin");
        _sessionPath = Path.Combine(folder, "google-session.bin");
        // Prevent an unexpected redirect from forwarding a bearer token to another origin.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaximumJsonBytes
        };

        var credentials = ReadProtected<OAuthCredentials>(_credentialsPath);
        if (credentials is not null && IsValidClientId(credentials.ClientId) && IsValidClientSecret(credentials.ClientSecret))
        {
            _credentials = credentials;
            var session = ReadProtected<GoogleSession>(_sessionPath);
            if (session is not null && session.ClientId == credentials.ClientId && IsValidSession(session))
                _session = session;
        }
    }

    public bool HasCredentials
    {
        get { lock (_sync) return !_disposed && _credentials is not null; }
    }

    /// <summary>Imports only the downloaded JSON for an OAuth client of type Desktop app.</summary>
    public void ImportCredentials(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaximumJsonBytes)
            throw new InvalidOperationException("Choose the small OAuth client JSON downloaded from Google Cloud.");

        OAuthCredentials credentials;
        byte[] json = File.ReadAllBytes(path);
        try { credentials = ParseCredentials(json); }
        catch (JsonException)
        {
            throw new InvalidOperationException("This file is not valid Google OAuth JSON. Download a Desktop app client JSON from Google Cloud.");
        }
        finally { CryptographicOperations.ZeroMemory(json); }

        if (!_operation.Wait(0))
            throw new InvalidOperationException("Finish or cancel the current Google sign-in before importing another client.");
        try
        {
            ThrowIfDisposed();
            WriteProtected(_credentialsPath, credentials);
            lock (_sync)
            {
                if (_credentials?.ClientId != credentials.ClientId)
                {
                    _session = null;
                    DeleteSessionFile();
                }
                _credentials = credentials;
            }
        }
        finally { _operation.Release(); }
    }

    /// <summary>
    /// Returns the locally cached identity, not proof of a currently valid Google session.
    /// SignInAsync verifies it online and refreshes expired access tokens when possible.
    /// </summary>
    public GoogleProfile? LoadProfile()
    {
        lock (_sync)
        {
            if (_disposed || _session is null) return null;
            bool usableRefresh = !string.IsNullOrEmpty(_session.RefreshToken)
                && (_session.RefreshTokenExpiresUtc is null || _session.RefreshTokenExpiresUtc > DateTimeOffset.UtcNow);
            return usableRefresh || _session.AccessTokenExpiresUtc > DateTimeOffset.UtcNow ? _session.Profile : null;
        }
    }

    public async Task<GoogleProfile> SignInAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _operation.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            OAuthCredentials credentials;
            GoogleSession? session;
            lock (_sync)
            {
                credentials = _credentials ?? throw new InvalidOperationException("Import a Google Desktop app OAuth client JSON in Settings before signing in. See GOOGLE-SIGN-IN.md for the one-time setup.");
                session = _session;
                _activeSignIn = operationCancellation;
            }
            operationCancellation.CancelAfter(TimeSpan.FromMinutes(3));
            var token = operationCancellation.Token;

            if (session is not null)
            {
                try
                {
                    var current = await VerifySavedSessionAsync(credentials, session, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    SaveSession(current);
                    return current.Profile;
                }
                catch (InvalidSessionException)
                {
                    ClearSession();
                    // Revoked/expired authorization requires fresh browser consent.
                }
            }

            var freshSession = await AuthorizeInBrowserAsync(credentials, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SaveSession(freshSession);
            return freshSession.Profile;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Google sign-in was canceled or timed out. Try again and complete the Google page within three minutes.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Google could not be reached. Check your internet connection and try again.");
        }
        catch (InvalidSessionException)
        {
            throw new InvalidOperationException("Google's sign-in approval expired or was rejected. Sign in again to request a new approval.");
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
        // Local sign-out must finish even if a network cancellation was requested.
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            string? tokenToRevoke;
            lock (_sync) tokenToRevoke = _session?.RefreshToken ?? _session?.AccessToken;
            ClearSession();
            if (string.IsNullOrEmpty(tokenToRevoke) || cancellationToken.IsCancellationRequested) return;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = tokenToRevoke });
                using var response = await _http.PostAsync(RevocationEndpoint, content, deadline.Token).ConfigureAwait(false);
                // Local data is already gone; offline or rejected revocation cannot undo sign-out.
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                // Google Account > Security > Third-party access can revoke an offline grant.
            }
        }
        finally { _operation.Release(); }
    }

    private async Task<GoogleSession> AuthorizeInBrowserAsync(OAuthCredentials credentials, CancellationToken cancellationToken)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = CreateCodeChallenge(verifier);
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(8);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirectUri = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";
            string authorizationUrl = AuthorizationEndpoint + "?" + FormQuery(new Dictionary<string, string>
            {
                ["client_id"] = credentials.ClientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = "openid email profile",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["access_type"] = "offline",
                ["prompt"] = "select_account consent"
            });
            try { Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException("Your default browser could not be opened. Set a default browser in Windows and try again.");
            }

            string code = await WaitForAuthorizationCodeAsync(listener, port, state, cancellationToken).ConfigureAwait(false);
            var parameters = ClientParameters(credentials);
            parameters["grant_type"] = "authorization_code";
            parameters["code"] = code;
            parameters["redirect_uri"] = redirectUri;
            parameters["code_verifier"] = verifier;
            var tokens = await ExchangeTokensAsync(parameters, null, cancellationToken).ConfigureAwait(false);
            GoogleProfile profile = await GetProfileAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            return new GoogleSession(credentials.ClientId, tokens.AccessToken, tokens.RefreshToken,
                tokens.AccessTokenExpiresUtc, tokens.RefreshTokenExpiresUtc, profile);
        }
        finally { listener.Stop(); }
    }

    private async Task<GoogleSession> VerifySavedSessionAsync(OAuthCredentials credentials, GoogleSession session, CancellationToken cancellationToken)
    {
        var current = session;
        if (current.AccessTokenExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(60))
            current = await RefreshAsync(credentials, current, cancellationToken).ConfigureAwait(false);
        GoogleProfile profile;
        try { profile = await GetProfileAsync(current.AccessToken, cancellationToken).ConfigureAwait(false); }
        catch (InvalidSessionException) when (current.AccessToken == session.AccessToken && !string.IsNullOrEmpty(session.RefreshToken))
        {
            current = await RefreshAsync(credentials, session, cancellationToken).ConfigureAwait(false);
            profile = await GetProfileAsync(current.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(profile.Id, session.Profile.Id, StringComparison.Ordinal))
            throw new InvalidSessionException();
        return current with { Profile = profile };
    }

    private async Task<GoogleSession> RefreshAsync(OAuthCredentials credentials, GoogleSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(session.RefreshToken) || session.RefreshTokenExpiresUtc <= DateTimeOffset.UtcNow)
            throw new InvalidSessionException();
        var parameters = ClientParameters(credentials);
        parameters["grant_type"] = "refresh_token";
        parameters["refresh_token"] = session.RefreshToken;
        var tokens = await ExchangeTokensAsync(parameters, session, cancellationToken).ConfigureAwait(false);
        return session with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            AccessTokenExpiresUtc = tokens.AccessTokenExpiresUtc,
            RefreshTokenExpiresUtc = tokens.RefreshTokenExpiresUtc
        };
    }

    private async Task<TokenResult> ExchangeTokensAsync(Dictionary<string, string> parameters, GoogleSession? previous, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        using var json = await ReadResponseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? error = GetString(json.RootElement, "error");
            if (error == "invalid_grant") throw new InvalidSessionException();
            if (error is "invalid_client" or "unauthorized_client")
                throw new InvalidOperationException("Google rejected this OAuth client. Import the downloaded Desktop app client JSON and check its Google Cloud configuration.");
            throw new InvalidOperationException("Google could not complete sign-in. Check the OAuth app configuration or try again later.");
        }
        var root = json.RootElement;
        string? accessToken = GetString(root, "access_token");
        string? refreshToken = GetString(root, "refresh_token") ?? previous?.RefreshToken;
        if (!IsValidToken(accessToken) || !string.Equals(GetString(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt64(out long seconds)
            || seconds <= 0 || seconds > 7 * 24 * 60 * 60 || (refreshToken is not null && !IsValidToken(refreshToken)))
            throw new InvalidOperationException("Google returned an incomplete sign-in response. Please try again.");

        DateTimeOffset? refreshExpires = previous?.RefreshTokenExpiresUtc;
        if (root.TryGetProperty("refresh_token_expires_in", out var refreshExpiry))
        {
            if (!refreshExpiry.TryGetInt64(out long refreshSeconds) || refreshSeconds <= 0 || refreshSeconds > 10L * 365 * 24 * 60 * 60)
                throw new InvalidOperationException("Google returned an invalid session expiry. Please try again.");
            refreshExpires = DateTimeOffset.UtcNow.AddSeconds(refreshSeconds);
        }
        // Never decode or trust the optional id_token; identity comes from Google's HTTPS userinfo endpoint.
        return new TokenResult(accessToken!, refreshToken, DateTimeOffset.UtcNow.AddSeconds(seconds), refreshExpires);
    }

    private async Task<GoogleProfile> GetProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidSessionException();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Google did not allow access to your basic profile. Allow the email and profile permissions and try again.");
        using var json = await ReadResponseJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        string? id = GetString(root, "sub");
        string? email = GetString(root, "email");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 255 || id.Any(character => character > 127 || char.IsControl(character)) || !IsValidEmail(email)
            || !root.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Google did not return a verified email and account identity. Please use an account with a verified email.");
        string name = GetString(root, "name")?.Trim() ?? "";
        if (name.Length == 0) name = email!;
        if (name.Length > 256) name = name[..256];
        return new GoogleProfile(id, name, email!);
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
                    await WriteBrowserResponseAsync(stream, callback.HttpStatus, "This request does not belong to the pending Google sign-in. Return to Google's sign-in page.", deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (callback.Error is not null)
                {
                    try
                    {
                        await WriteBrowserResponseAsync(stream, 200, "Google sign-in was not completed. Return to Aurora Translate to try again.", deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                    throw new InvalidOperationException(callback.Error == "access_denied"
                        ? "Google sign-in was declined. You can continue using Aurora Translate without an account."
                        : "Google sign-in could not be completed. Check your Google OAuth app configuration and try again.");
                }
                // A browser closing the TCP connection after sending the callback must not lose its valid code.
                try
                {
                    await WriteBrowserResponseAsync(stream, 200, "Google approval received. Return to Aurora Translate to finish signing in. You may close this tab.", deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                return callback.Code!;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                // Malformed/unrelated local connections cannot end a legitimate pending authorization.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Bound the time one partial local request can occupy the listener.
            }
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
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return Encoding.ASCII.GetString(buffer, 0, i + 4);
            }
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
        if (path != "/" || target.Contains('#')) return new(false, 404, null, null);

        string expectedHost = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        var hostHeaders = lines.Skip(1).Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hostHeaders.Length != 1 || !string.Equals(hostHeaders[0][5..].Trim(), expectedHost, StringComparison.OrdinalIgnoreCase))
            return new(false, 400, null, null);
        if (queryOffset < 0) return new(false, 400, null, null);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string part in target[(queryOffset + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                string key = DecodeQueryComponent(pair[0]);
                string value = pair.Length == 2 ? DecodeQueryComponent(pair[1]) : "";
                // Recognized parameters must not be ambiguous. Unknown parameters are ignored.
                if (key is not ("state" or "code" or "error" or "iss")) continue;
                if (!query.TryAdd(key, value)) return new(false, 400, null, null);
            }
        }
        catch (UriFormatException) { return new(false, 400, null, null); }
        if (!query.TryGetValue("state", out string? state) || !StateMatches(expectedState, state))
            return new(false, 400, null, null);
        if (query.TryGetValue("iss", out var issuer) && issuer != "https://accounts.google.com")
            return new(false, 400, null, null);
        bool hasCode = query.TryGetValue("code", out string? code) && !string.IsNullOrWhiteSpace(code);
        bool hasError = query.TryGetValue("error", out string? error) && !string.IsNullOrWhiteSpace(error);
        if (hasCode == hasError) return new(false, 400, null, null);
        if ((code?.Length ?? 0) > 4096 || (error?.Length ?? 0) > 256 || code?.Any(char.IsControl) == true)
            return new(false, 400, null, null);
        return new(true, 200, code, error);
    }

    internal static bool StateMatches(string expected, string actual)
    {
        // Fixed-size hash comparisons avoid variable-length string comparison of the secret state.
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(actual));
        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    internal static string CreateCodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string DecodeQueryComponent(string value)
    {
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])))
                throw new UriFormatException();
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static async Task WriteBrowserResponseAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        string reason = status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", 431 => "Request Header Fields Too Large", _ => "Bad Request" };
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Aurora Translate</title><style>body{background:#101529;color:#eef4ff;font:18px system-ui;max-width:640px;margin:15vh auto;padding:32px}h1{color:#8be7de}p{line-height:1.7}</style><h1>Aurora Translate</h1><p>" + WebUtility.HtmlEncode(message) + "</p></html>");
        string headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    internal static OAuthCredentials ParseCredentials(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaximumJsonBytes)
            throw new InvalidOperationException("The OAuth client JSON is too large.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("web", out _) || !root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Use an OAuth client of type Desktop app. Web application and service-account JSON files are not supported.");
        string? clientId = GetString(installed, "client_id");
        if (!IsValidClientId(clientId))
            throw new InvalidOperationException("This JSON does not contain a valid Google Desktop app client ID.");
        string? secret = null;
        if (installed.TryGetProperty("client_secret", out var secretProperty))
        {
            if (secretProperty.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidOperationException("The OAuth client secret must be a string.");
            secret = secretProperty.ValueKind == JsonValueKind.String ? secretProperty.GetString() : null;
            if (secret == "") secret = null;
        }
        if (!IsValidClientSecret(secret)) throw new InvalidOperationException("The OAuth client secret is not valid.");
        return new OAuthCredentials(clientId!, secret);
    }

    private static bool IsValidClientId(string? value) => value is not null && value.Length <= 256
        && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_-]{5,220}\.apps\.googleusercontent\.com\z", RegexOptions.CultureInvariant);
    private static bool IsValidClientSecret(string? value) => value is null || (value.Length is > 0 and <= 2048 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl));
    private static bool IsValidToken(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 8192 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl);
    private static bool IsValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 320 && !value.Any(char.IsWhiteSpace)
        && !value.Any(char.IsControl) && System.Net.Mail.MailAddress.TryCreate(value, out var address)
        && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
    private static bool IsValidSession(GoogleSession session) => IsValidToken(session.AccessToken)
        && (session.RefreshToken is null || IsValidToken(session.RefreshToken)) && session.Profile is not null
        && !string.IsNullOrWhiteSpace(session.Profile.Id) && session.Profile.Id.Length <= 255
        && !string.IsNullOrWhiteSpace(session.Profile.Name) && session.Profile.Name.Length <= 256
        && IsValidEmail(session.Profile.Email);
    private static string? GetString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string FormQuery(Dictionary<string, string> parameters) => string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static Dictionary<string, string> ClientParameters(OAuthCredentials credentials)
    {
        var parameters = new Dictionary<string, string> { ["client_id"] = credentials.ClientId };
        if (!string.IsNullOrEmpty(credentials.ClientSecret)) parameters["client_secret"] = credentials.ClientSecret;
        return parameters;
    }

    private static async Task<JsonDocument> ReadResponseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumJsonBytes)
            throw new InvalidOperationException("Google returned an unexpectedly large response.");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (bytes.Length > MaximumJsonBytes) throw new InvalidOperationException("Google returned an unexpectedly large response.");
            // The stream overload owns a separate backing buffer; the transport bytes can then be cleared.
            using var input = new MemoryStream(bytes, false);
            return JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 12 });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Google returned an unexpected sign-in response. Please try again.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private T? ReadProtected<T>(string path) where T : class
    {
        byte[]? plaintext = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumJsonBytes) return null;
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), ProtectionEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException)
        {
            return null;
        }
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

    private void SaveSession(GoogleSession session)
    {
        WriteProtected(_sessionPath, session);
        lock (_sync) _session = session;
    }

    private void ClearSession()
    {
        lock (_sync) _session = null;
        DeleteSessionFile();
    }

    private void DeleteSessionFile() { if (File.Exists(_sessionPath)) File.Delete(_sessionPath); }
    private void ThrowIfDisposed() { lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this); }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _credentials = null;
            _session = null;
        }
        _lifetime.Cancel();
        _http.Dispose();
        // The semaphore/CTS outlive any operation unwinding its cancellation callbacks.
    }

    internal sealed record OAuthCredentials(string ClientId, string? ClientSecret);
    internal sealed record CallbackResult(bool Valid, int HttpStatus, string? Code, string? Error);
    private sealed record TokenResult(string AccessToken, string? RefreshToken, DateTimeOffset AccessTokenExpiresUtc, DateTimeOffset? RefreshTokenExpiresUtc);
    private sealed record GoogleSession(string ClientId, string AccessToken, string? RefreshToken, DateTimeOffset AccessTokenExpiresUtc, DateTimeOffset? RefreshTokenExpiresUtc, GoogleProfile Profile);
    private sealed class InvalidSessionException : Exception { }
}
