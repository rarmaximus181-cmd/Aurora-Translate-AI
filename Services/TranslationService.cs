using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AuroraTranslate.Services;

public sealed class TranslationService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ChatService _chat = new();
    public TranslationService(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<TranslationResult> TranslateAsync(string text, string sourceCode, string targetCode,
        string engine, string model, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Enter something to translate first.");
        if (text.Length > 10000) throw new InvalidOperationException("Translate up to 10,000 characters at a time.");
        if (!Languages.All.Any(x => x.Code == sourceCode) || !Languages.All.Any(x => x.Code == targetCode))
            throw new InvalidOperationException("Choose a source and destination language.");
        if (sourceCode == targetCode) return new(text, "Same language");
        if (engine is not ("online" or "local")) throw new InvalidOperationException("Choose a translation service in Settings.");
        var chunks = SplitText(text, engine == "local" ? 2000 : 480);
        var output = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            if (string.IsNullOrWhiteSpace(chunk)) { output.Append(chunk); continue; }
            var leadingLength = chunk.Length - chunk.TrimStart().Length;
            var trailingStart = chunk.TrimEnd().Length;
            var content = chunk[leadingLength..trailingStart];
            progress?.Report($"Translating part {i + 1} of {chunks.Count}…");
            var translation = engine == "local"
                ? await _chat.TranslateAsync(content, sourceCode, targetCode, model, cancellationToken)
                : await TranslateOnlineAsync(content, sourceCode, targetCode, cancellationToken);
            output.Append(chunk[..leadingLength]);
            output.Append(translation.Trim());
            output.Append(chunk[trailingStart..]);
        }
        return new(output.ToString(), engine == "local" ? "Local AI · Ollama" : "MyMemory · Online");
    }

    private async Task<string> TranslateOnlineAsync(string text, string source, string target, CancellationToken ct)
    {
        var uri = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text)
            + "&langpair=" + Uri.EscapeDataString(source + "|" + target);
        try
        {
            using var response = await _http.GetAsync(uri, ct);
            if ((int)response.StatusCode == 429) throw QuotaError();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var quota = root.TryGetProperty("quotaFinished", out var quotaElement) && quotaElement.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("responseStatus", out var statusElement) ? statusElement.ToString() : "200";
            if (quota || status == "429") throw QuotaError();
            if (status != "200")
            {
                var detail = root.TryGetProperty("responseDetails", out var d) ? d.GetString() : null;
                if (detail?.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) == true) throw QuotaError();
                throw new InvalidOperationException("The translation service could not translate this language pair. Try Local AI in Settings.");
            }
            if (!root.TryGetProperty("responseData", out var data) || !data.TryGetProperty("translatedText", out var translated)
                || string.IsNullOrWhiteSpace(translated.GetString()))
                throw new InvalidOperationException("The translation service returned an empty answer. Try again or switch to Local AI.");
            return WebUtility.HtmlDecode(translated.GetString()!);
        }
        catch (HttpRequestException ex)
        { throw new InvalidOperationException("Could not reach the online translator. Check your internet connection or choose Local AI in Settings.", ex); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("The online translator took too long. Try again or choose Local AI.", ex); }
        catch (JsonException ex)
        { throw new InvalidOperationException("The online translator returned an unreadable response. Please try again.", ex); }
    }

    private static InvalidOperationException QuotaError() => new("The free online translation allowance has been reached. MyMemory allows about 5,000 characters per day. Switch to Local AI in Settings to keep translating without an API key.");

    // Keep grapheme clusters intact, preserve all whitespace, and respect the provider's UTF-8 byte limit.
    public static IReadOnlyList<string> SplitText(string text, int maxBytes)
    {
        if (maxBytes < 16) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var result = new List<string>();
        var elements = StringInfo.ParseCombiningCharacters(text);
        var elementIndex = 0;
        while (elementIndex < elements.Length)
        {
            var startIndex = elementIndex;
            var bytes = 0;
            var lastBoundary = -1;
            while (elementIndex < elements.Length)
            {
                var start = elements[elementIndex];
                var end = elementIndex + 1 < elements.Length ? elements[elementIndex + 1] : text.Length;
                var element = text[start..end];
                var elementBytes = Encoding.UTF8.GetByteCount(element);
                if (elementBytes > maxBytes) throw new InvalidOperationException("A character sequence is too large to translate. Please remove unusually long combining characters.");
                if (bytes + elementBytes > maxBytes) break;
                bytes += elementBytes;
                elementIndex++;
                if (element.Contains('\n') || element.Contains('\r')) { lastBoundary = elementIndex; break; }
                if (char.IsWhiteSpace(element[0]) || element is "." or "!" or "?" or "。" or "！" or "？") lastBoundary = elementIndex;
            }
            if (elementIndex < elements.Length && lastBoundary > startIndex && lastBoundary > startIndex + (elementIndex - startIndex) / 2)
                elementIndex = lastBoundary;
            var chunkStart = elements[startIndex];
            var chunkEnd = elementIndex < elements.Length ? elements[elementIndex] : text.Length;
            result.Add(text[chunkStart..chunkEnd]);
        }
        return result;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); _chat.Dispose(); }
}
