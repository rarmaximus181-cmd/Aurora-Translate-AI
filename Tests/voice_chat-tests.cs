// Integration checks for the real local Ollama service and Windows speech inventory.
// Link this file, Models.cs and the two service files into a net10.0-windows console project.
using AuroraTranslate;
using AuroraTranslate.Services;

internal static class VoiceChatChecks
{
    private static async Task Main()
    {
        using var voice = new VoiceService();
        var voices = voice.GetVoices();
        var recognizers = voice.GetRecognitionLanguages();
        Console.WriteLine($"Installed voices: {string.Join(", ", voices.Select(v => $"{v.Name} ({v.Language})"))}");
        Console.WriteLine($"Installed recognizers: {string.Join(", ", recognizers)}");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => voice.SpeakAsync("This must not play.", "en", cancellationToken: canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => voice.ListenAsync("en", cancellationToken: canceled.Token));
        if (!voices.Any(v => v.Language.StartsWith("es", StringComparison.OrdinalIgnoreCase)))
            await ExpectAsync<InvalidOperationException>(() => voice.SpeakAsync("Hola", "es"));
        if (!recognizers.Any(v => v.StartsWith("es", StringComparison.OrdinalIgnoreCase)))
            await ExpectAsync<InvalidOperationException>(() => voice.ListenAsync("es"));
        Console.WriteLine("Voice language failures and cancellation: PASS (no audio or microphone recording).");

        using var chat = new ChatService();
        var models = await chat.GetModelsAsync();
        if (!models.Contains("qwen2.5:1.5b")) throw new Exception("The integration model qwen2.5:1.5b is missing.");
        var updates = new List<string>();
        var reply = await chat.SendAsync([new ChatMessage("user", "Say hello in one short sentence.")], "qwen2.5:1.5b", "en", new InlineProgress(updates.Add));
        if (updates.Count < 2 || updates[^1].Trim() != reply) throw new Exception("Stream did not deliver the full response.");
        for (var i = 1; i < updates.Count; i++)
            if (!updates[i].StartsWith(updates[i - 1], StringComparison.Ordinal)) throw new Exception("Stream fragments were out of order.");
        Console.WriteLine($"Streamed chat ({updates.Count} updates): {reply}");
        var translated = await chat.TranslateAsync("Hello, how are you?", "en", "es", "qwen2.5:1.5b");
        if (!translated.Contains("Hola", StringComparison.OrdinalIgnoreCase)) throw new Exception("Spanish translation did not contain the expected greeting.");
        Console.WriteLine($"Local Spanish translation: {translated}");
        await ExpectAsync<InvalidOperationException>(() => chat.SendAsync([new ChatMessage("user", "Hello")], "aurora-not-installed-test:latest", "en"));
        await ExpectAsync<OperationCanceledException>(() => chat.SendAsync([new ChatMessage("user", "Hello")], "qwen2.5:1.5b", "en", cancellationToken: canceled.Token));
        await ExpectAsync<OperationCanceledException>(() => chat.PullModelAsync("qwen2.5:1.5b", cancellationToken: canceled.Token));
        Console.WriteLine("Model error and chat/download cancellation: PASS. All service checks passed.");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private sealed class InlineProgress(Action<string> update) : IProgress<string>
    {
        public void Report(string value) => update(value);
    }
}
