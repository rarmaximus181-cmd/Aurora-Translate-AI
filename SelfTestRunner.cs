using System.IO;
using System.Net;
using System.Net.Http;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using AuroraTranslate.Services;

namespace AuroraTranslate;

public static class SelfTestRunner
{
    public static async Task<bool> RunAsync(string reportPath)
    {
        var results = new List<object>();
        var passed = true;
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { passed = false; results.Add(new { name, passed = false, error = ex.Message }); }
        }
        void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

        await Check("Unicode chunks preserve emoji, combining marks, whitespace and provider byte limit", () =>
        {
            var samples = new[]
            {
                string.Concat(Enumerable.Repeat("Hello 日本語 👩‍💻 e\u0301!\r\n مرحبا world. ", 70)),
                string.Concat(Enumerable.Repeat("Hello 日本語 👩‍💻 e\u0301! مرحبا world. ", 70)),
                string.Concat(Enumerable.Repeat("日本語👩‍💻e\u0301", 160))
            };
            foreach (var text in samples)
            {
                var chunks = TranslationService.SplitText(text, 480);
                Require(string.Concat(chunks) == text, "Text lost or duplicated during chunking.");
                Require(chunks.All(c => Encoding.UTF8.GetByteCount(c) <= 480), "Provider byte limit exceeded.");
                Require(chunks.All(c => !c.StartsWith("\u0301", StringComparison.Ordinal) && !c.StartsWith("\u200d", StringComparison.Ordinal)), "A grapheme was split.");
            }
            return Task.CompletedTask;
        });
        await Check("Same-language translation does not send a request", async () =>
        {
            using var client = new HttpClient(new StubHandler(_ => throw new Exception("Unexpected request.")));
            using var service = new TranslationService(client);
            var result = await service.TranslateAsync("Hello", "en", "en", "online", "none");
            Require(result.Text == "Hello", "Same-language result changed.");
        });
        await Check("Online translation encodes requests and decodes text safely", async () =>
        {
            string? uri = null;
            using var client = new HttpClient(new StubHandler(r =>
            {
                uri = r.RequestUri!.OriginalString;
                return JsonResponse("{\"responseStatus\":200,\"quotaFinished\":false,\"responseData\":{\"translatedText\":\"Hola &amp; adiós\"}}");
            }));
            using var service = new TranslationService(client);
            var result = await service.TranslateAsync("  Hello & goodbye\n", "en", "es", "online", "none");
            Require(result.Text == "  Hola & adiós\n", "Whitespace or HTML decoding failed.");
            Require(uri!.Contains("%26") && uri.Contains("en%7Ces"), "Query parameters not escaped.");
        });
        await Check("Quota exhaustion is reported without displaying an error as a translation", async () =>
        {
            using var client = new HttpClient(new StubHandler(_ => JsonResponse("{\"responseStatus\":429,\"quotaFinished\":true}")));
            using var service = new TranslationService(client);
            try { await service.TranslateAsync("hello", "en", "es", "online", "none"); throw new Exception("Quota error ignored."); }
            catch (InvalidOperationException ex) { Require(ex.Message.Contains("Local AI"), "No useful quota guidance."); }
        });
        await Check("Canceling translation stops before sending text", async () =>
        {
            using var service = new TranslationService();
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await service.TranslateAsync("hello", "en", "es", "online", "none", cancellationToken: cts.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
        });
        await Check("Settings persist and damaged files recover without changing other user data", () =>
        {
            var previous = Environment.GetEnvironmentVariable("AURORA_DATA_DIR");
            var folder = Path.Combine(Path.GetTempPath(), "AuroraTranslate-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("AURORA_DATA_DIR", folder);
                AppStore.SaveSettings(new AppSettings { Theme = "Ocean", TargetLanguage = "ja", AnimateTheme = false });
                Require(AppStore.LoadSettings().TargetLanguage == "ja" && !AppStore.LoadSettings().AnimateTheme, "Settings did not round-trip.");
                AppStore.SaveHistory(Enumerable.Range(0, 80).Select(i => new TranslationHistory(i.ToString(), "example", "en", "es", DateTimeOffset.UtcNow)));
                Require(AppStore.LoadHistory().Count == 50, "History cap failed.");
                File.WriteAllText(Path.Combine(folder, "settings.json"), "{broken");
                Require(AppStore.LoadSettings().Theme == "Aurora", "Damaged settings did not recover.");
                Require(File.Exists(Path.Combine(folder, "settings.json.unreadable")), "Damaged settings not preserved.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("AURORA_DATA_DIR", previous);
                // This exclusively owned test directory is removed after verifying its absolute target.
                if (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(folder, true);
            }
            return Task.CompletedTask;
        });
        await Check("Installed Windows voice can synthesize a playable WAV", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "Aurora-voice-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                using var voice = new SpeechSynthesizer();
                Require(voice.GetInstalledVoices().Any(v => v.Enabled), "No installed voices.");
                voice.SetOutputToWaveFile(path);
                voice.Speak("Aurora Translate is ready.");
                voice.SetOutputToNull();
                Require(new FileInfo(path).Length > 1000, "Voice WAV is empty.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            return Task.CompletedTask;
        });
        await Check("Online translation works against the live free service", async () =>
        {
            using var translator = new TranslationService();
            var result = await translator.TranslateAsync("Good morning", "en", "es", "online", "none");
            Require(result.Text.Length > 0 && result.Text != "Good morning", "Live service did not translate.");
            results.Add(new { evidence = "online translation", output = result.Text });
        });
        await Check("Local AI conversation works with an installed model", async () =>
        {
            using var chat = new ChatService();
            var models = await chat.GetModelsAsync();
            Require(models.Count > 0, "No local model available.");
            var model = models.Contains("qwen2.5:1.5b") ? "qwen2.5:1.5b" : models[0];
            var response = await chat.SendAsync([new("user", "Say hello in one short sentence.")], model, "en");
            Require(!string.IsNullOrWhiteSpace(response), "Local conversation returned no reply.");
            results.Add(new { evidence = "local conversation", model, output = response });
        });
        await Check("Local AI translation works without internet translation credentials", async () =>
        {
            using var translator = new TranslationService();
            var result = await translator.TranslateAsync("Hello, my friend.", "en", "fr", "local", "qwen2.5:1.5b");
            Require(!string.IsNullOrWhiteSpace(result.Text) && result.Text != "Hello, my friend.", "Local model did not translate.");
            results.Add(new { evidence = "local translation", output = result.Text });
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed, testedAt = DateTimeOffset.Now, results }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
