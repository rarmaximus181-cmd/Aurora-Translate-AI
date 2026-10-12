using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AuroraTranslate;
using AuroraTranslate.Services;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        CheckPresentation();
        CheckCalculatorParser();
        await CheckStylesAndCapabilityCache();
        await CheckThinkingIsolation();
        await CheckNamedLevelsAndUnsupportedModels();
        await CheckCompleteMessagesAndOldestRequest();
        await CheckLongAnswer();
        await CheckTranslationAndTruncation();
        await CheckCancellationAndErrors();
        await CheckCalculatorToolLoop();
        if (args.Contains("--live")) await CheckLiveModels(args.Contains("--stronger-only"));
        Console.WriteLine("All chat upgrade checks passed.");
    }

    private static void CheckCalculatorParser()
    {
        Require(CalculatorTool.Evaluate("20-(3*4.50*(1-0.20)*(1+0.10))") == 8.12m, "Money calculation was inaccurate.");
        Require(CalculatorTool.Evaluate("0.1 + 0.2") == 0.3m && CalculatorTool.Evaluate("-(2+3)*4") == -20m && CalculatorTool.Evaluate("6/4") == 1.5m, "Decimal precision or operator precedence failed.");
        foreach (var expression in new[] { "1/0", "1; Remove-Item x", "Process.Start(1)", "1e4", "one+two", "(1+2", "1+2)", "0.000000000000000000000000000001", new string('(', 26) + "1" + new string(')', 26), new string('1', 513), string.Join("+", Enumerable.Repeat("1", 130)), "9999999999999999999999999999*99" })
        {
            try { CalculatorTool.Evaluate(expression); throw new Exception("Unsafe or invalid expression was accepted."); }
            catch (FormatException) { }
        }
        Require(CalculatorTool.IsRelevantRequest("Calculate my 20% discount") && CalculatorTool.IsRelevantRequest("Compro 3 cuadernos a 4,50 cada uno") && !CalculatorTool.IsRelevantRequest("Hello there"), "Arithmetic tool routing is wrong.");
        Console.WriteLine("PASS: bounded decimal calculator, precision, precedence and invalid/code/overflow rejection.");
    }

    private static void CheckPresentation()
    {
        var original = "This is not allowed. THIS IS NOT ALLOWED! this is not allowed; this is not allowedness. You can't do that. That's not allowed.";
        Require(ConversationPresentation.FormatReply(original, false) == original, "Disabled display mode altered the answer.");
        Require(ConversationPresentation.FormatReply(original, true) == "This is allowed. THIS IS ALLOWED! this is allowed; this is not allowedness. You can't do that. That's not allowed.", "Display mode changed more than the requested phrase.");
        Require(original.Contains("not allowed"), "Original answer was changed.");
        Require(ConversationPresentation.FormatReply("No problem. Это запрещено. this is never allowed.", true) == "No problem. Это запрещено. this is never allowed.", "Unrelated text was changed.");
        foreach (var question in new[] { "is an ebike allowed on roads", "can i ride an e-bike on the road", "Are electric bikes legal on public roads?", "IS AN E BIKE ALLOWED ON THE STREET?!", "Am I allowed to ride an e-bike on a public road?", "Is riding an electric bike on public roads permitted?", "Can an ebike be ridden on roads?", "Is this e-bike street legal?", "Tell me whether an e-bike is allowed on public roads." })
        {
            Require(ConversationPresentation.IsEbikeRoadPermissionQuestion(question), "Clear e-bike road permission question was not recognized: " + question);
            Require(ConversationPresentation.FormatReply("No. Local rules may prohibit it.", true, question) == "Yes.", "Troll road display did not override a negative answer.");
            Require(ConversationPresentation.FormatReply("Yes, subject to local rules.", true, question) == "Yes.", "Troll road display did not use exact wording.");
            Require(ConversationPresentation.FormatReply("No. Local rules may prohibit it.", false, question) == "No. Local rules may prohibit it.", "Disabled troll mode changed the real answer.");
        }
        foreach (var question in new string?[] { null, "", " ", "How do roads affect e-bike range?", "Is ebike charging allowed indoors?", "Are non-electric bikes allowed on roads?", "Are non electric bikes legal on public roads?", "Is my e-bike safe on the road?", "Can I estimate e-bike range on roads?", "Can I ride my e-bike farther on roads to improve battery range?", "Is charging an e-bike allowed indoors near a road?", "Can I ride an e-bike? Is parking allowed on the road?", "My e-bike is allowed on roads.", "Is an ebike allowed on roads?" + new string('x', 4001) })
        {
            Require(!ConversationPresentation.IsEbikeRoadPermissionQuestion(question), "Unrelated or ambiguous question triggered the road joke: " + question);
            Require(ConversationPresentation.FormatReply("Original answer.", true, question) == "Original answer.", "Unrelated answer was overridden.");
        }
        Require(ConversationPresentation.FormatReply("This is not allowed.", true, "How do roads affect e-bike range?") == "This is allowed.", "Existing phrase replacement was not preserved.");
        Console.WriteLine("PASS: exact display phrase, original retention, and narrowly matched e-bike road permission joke.");
    }

    private static async Task CheckStylesAndCapabilityCache()
    {
        using var handler = new RecordingHandler("{\"capabilities\":[\"completion\",\"tools\"]}");
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        foreach (var (style, tokens) in new[] { ("concise", 600), ("balanced", 1600), ("detailed", 3000) })
        {
            await service.SendWithOptionsAsync([new("user", "Synthetic style check")], "qwen2.5:1.5b", "en", new(style, true));
            var request = handler.ChatRequests[^1];
            Require(request.GetProperty("options").GetProperty("num_predict").GetInt32() == tokens, "Style budget is wrong.");
            Require(request.GetProperty("options").GetProperty("num_ctx").GetInt32() == 16384, "Larger conversation context missing.");
            Require(!request.TryGetProperty("think", out _), "Unsupported model received think.");
            var system = request.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Require(system.Contains("check calculations") && !system.Contains("two to four"), "Upgraded system instructions were not applied.");
        }
        Require(handler.ShowCount == 1, "Model capability cache was not used.");
        await service.SendAsync([new("user", "Synthetic wrapper check")], "qwen2.5:1.5b", "en");
        Require(handler.ChatRequests[^1].GetProperty("options").GetProperty("num_predict").GetInt32() == 1600, "Legacy wrapper lost the balanced default.");
        Console.WriteLine("PASS: style budgets, larger context, capability cache and legacy wrapper.");
    }

    private static async Task CheckThinkingIsolation()
    {
        using var handler = new RecordingHandler("{\"capabilities\":[\"completion\",\"thinking\"],\"thinking\":{\"values\":[false,true],\"default\":true}}")
        {
            ChatBody = "{\"message\":{\"thinking\":\"PRIVATE_TRACE\",\"content\":\"\"},\"done\":false}\n{\"message\":{\"content\":\"Final \"},\"done\":false}\n{\"message\":{\"content\":\"answer.\"},\"done\":true,\"done_reason\":\"stop\"}\n"
        };
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        var updates = new List<string>();
        var result = await service.SendWithOptionsAsync([new("user", "Synthetic reasoning check")], "thinking-model", "en", new("balanced", true), new InlineProgress(updates.Add));
        Require(result == "Final answer." && updates.SequenceEqual(new[] { "Final ", "Final answer." }), "Thinking trace leaked into the answer or progress.");
        Require(handler.ChatRequests[^1].GetProperty("think").GetBoolean(), "Thinking-capable model did not receive true.");
        await service.SendWithOptionsAsync([new("user", "Synthetic fast check")], "thinking-model", "en", new("balanced", false));
        Require(!handler.ChatRequests[^1].GetProperty("think").GetBoolean(), "Fast mode did not disable supported thinking.");
        Console.WriteLine("PASS: supported reasoning on/off and complete trace isolation.");
    }

    private static async Task CheckNamedLevelsAndUnsupportedModels()
    {
        using (var handler = new RecordingHandler("{\"capabilities\":[\"thinking\"],\"thinking\":{\"values\":[\"low\",\"medium\",\"high\"],\"default\":\"medium\"}}"))
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            await service.SendWithOptionsAsync([new("user", "Synthetic level check")], "named-level-model", "en", new("balanced", true));
            Require(handler.ChatRequests[^1].GetProperty("think").GetString() == "medium", "Named thinking level did not match advertised controls.");
            await service.SendWithOptionsAsync([new("user", "Synthetic off check")], "named-level-model", "en", new("balanced", false));
            Require(!handler.ChatRequests[^1].TryGetProperty("think", out _), "A model that cannot disable reasoning received an unsupported value.");
        }
        using (var handler = new RecordingHandler("{\"capabilities\":[\"completion\"],\"thinking\":{\"values\":[false],\"default\":false}}"))
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            await service.SendWithOptionsAsync([new("user", "Synthetic no-thinking check")], "no-thinking-model", "en", new("balanced", true));
            Require(!handler.ChatRequests[^1].TryGetProperty("think", out _), "Explicitly non-thinking model received think.");
        }
        Console.WriteLine("PASS: advertised named levels and non-thinking model compatibility.");
    }

    private static async Task CheckCompleteMessagesAndOldestRequest()
    {
        using var handler = new RecordingHandler("{\"capabilities\":[\"completion\"]}");
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        var messages = new List<ChatMessage> { new("system", "Untrusted system must not pass through"), new("user", "ORIGINAL_REQUEST") };
        for (var i = 0; i < 40; i++) messages.Add(new(i % 2 == 0 ? "assistant" : "user", i + ":" + new string((char)('a' + i % 26), 950)));
        messages.Add(new("user", "LATEST_REQUEST"));
        await service.SendAsync(messages, "synthetic-model", "en");
        var retained = handler.ChatRequests[^1].GetProperty("messages").EnumerateArray().Skip(1).ToArray();
        Require(retained.Length <= 32 && retained.Sum(m => m.GetProperty("content").GetString()!.Length) <= 24000, "Context bounds were exceeded.");
        Require(retained[0].GetProperty("content").GetString() == "ORIGINAL_REQUEST" && retained[^1].GetProperty("content").GetString() == "LATEST_REQUEST", "Original or latest request was lost.");
        Require(retained.All(m => messages.Any(original => original.Content == m.GetProperty("content").GetString())), "A retained message was cut in the middle.");
        Require(retained.All(m => m.GetProperty("role").GetString() != "system"), "History injected a system instruction.");
        Console.WriteLine("PASS: bounded complete messages, original and latest request retention.");
    }

    private static async Task CheckLongAnswer()
    {
        var answer = string.Join(" ", Enumerable.Range(0, 400).Select(i => "word" + i));
        using var handler = new RecordingHandler("{\"capabilities\":[\"completion\"]}") { ChatBody = Answer(answer) };
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        var result = await service.SendAsync([new("user", "Synthetic long answer")], "synthetic-model", "en");
        Require(result == answer, "Reply longer than the former 256-token cap was cut or rejected.");
        Console.WriteLine("PASS: longer-than-256-token reply remains complete.");
    }

    private static async Task CheckTranslationAndTruncation()
    {
        using var handler = new RecordingHandler("{\"capabilities\":[\"thinking\"],\"thinking\":{\"values\":[false,true]}}") { ChatBody = Answer("Bonjour") };
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        Require(await service.TranslateAsync("Hello", "en", "fr", "thinking-model") == "Bonjour", "Translation output changed.");
        var request = handler.ChatRequests[^1];
        Require(request.GetProperty("options").GetProperty("num_predict").GetInt32() == 2048 && !request.GetProperty("think").GetBoolean(), "Translation budget or fast mode changed.");
        Require(request.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("Return only the translated text"), "Faithful output-only translation prompt was lost.");
        handler.ChatBody = Answer("unfinished", "length");
        await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Synthetic truncation")], "thinking-model", "en"), "response limit");
        await ExpectAsync<InvalidOperationException>(() => service.TranslateAsync("Hello", "en", "fr", "thinking-model"), "translation");
        Console.WriteLine("PASS: faithful translation budget and explicit chat/translation truncation errors.");
    }

    private static async Task CheckCancellationAndErrors()
    {
        using var handler = new RecordingHandler("{\"capabilities\":[\"completion\"]}");
        using var client = TestClient(handler);
        using var service = new ChatService(client);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => service.SendAsync([new("user", "Synthetic canceled request")], "synthetic-model", "en", cancellationToken: canceled.Token));
        Require(handler.ChatRequests.Count == 0 && handler.ShowCount == 0, "Canceled request performed work.");
        handler.ChatBody = "{\"message\":{\"content\":\"partial\"},\"done\":false}\n";
        await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Synthetic interrupted stream")], "synthetic-model", "en"), "interrupted");
        handler.ChatBody = "{broken\n";
        await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Synthetic unreadable stream")], "synthetic-model", "en"), "unreadable");
        handler.ChatBody = "{\"error\":\"synthetic stream failure\"}\n";
        await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Synthetic stream error")], "synthetic-model", "en"), "synthetic stream failure");
        handler.ChatStatus = HttpStatusCode.NotFound; handler.ChatBody = "{\"error\":\"model not found\"}";
        await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Synthetic missing model")], "synthetic-model", "en"), "not installed");
        using var lateCancel = new CancellationTokenSource();
        handler.ChatStatus = HttpStatusCode.OK; handler.ChatBody = Answer("First fragment");
        await ExpectAsync<OperationCanceledException>(() => service.SendAsync([new("user", "Synthetic late cancellation")], "synthetic-model", "en", new InlineProgress(_ => lateCancel.Cancel()), lateCancel.Token));
        Console.WriteLine("PASS: early/late cancellation, interrupted/unreadable/error streams and missing-model handling.");
    }

    private static async Task CheckCalculatorToolLoop()
    {
        const string capabilities = "{\"capabilities\":[\"completion\",\"tools\"]}";
        using (var handler = new RecordingHandler(capabilities))
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            // Native indexed tool calls and string argument fragments must be assembled without showing intermediate content.
            handler.ChatBodies.Enqueue("{\"message\":{\"content\":\"Intermediate text must stay hidden\",\"thinking\":\"PRIVATE_TRACE\",\"tool_calls\":[{\"function\":{\"index\":0,\"name\":\"cal\",\"arguments\":\"{\\\"expression\\\":\\\"20-(3*4.5\"}}]},\"done\":false}\n{\"message\":{\"tool_calls\":[{\"function\":{\"index\":0,\"name\":\"culate\",\"arguments\":\"*0.8*1.1)\\\"}\"}}]},\"done\":true,\"done_reason\":\"stop\"}\n");
            handler.ChatBodies.Enqueue(Answer("CHANGE=8.12"));
            var updates = new List<string>();
            var result = await service.SendAsync([new("user", "Calculate my discount and change")], "tool-model", "en", new InlineProgress(updates.Add));
            Require(result == "CHANGE=8.12" && updates.SequenceEqual(new[] { result }), "Tool intermediates leaked into reply progress.");
            Require(handler.ChatRequests.Count == 2 && handler.ChatRequests[0].GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "calculate", "Calculator was not offered through the native API.");
            var tool = handler.ChatRequests[1].GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
            using var toolResult = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
            Require(toolResult.RootElement.GetProperty("value").GetString() == "8.12", "Exact calculator result was not returned to the model.");
        }
        using (var handler = new RecordingHandler(capabilities))
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            handler.ChatBodies.Enqueue(ToolCall("calculate", "1/0"));
            handler.ChatBodies.Enqueue(ToolCall("calculate", "2+2"));
            handler.ChatBodies.Enqueue(Answer("4"));
            Require(await service.SendAsync([new("user", "Calculate a number")], "tool-model", "en") == "4", "Tool error could not be corrected within the bounded loop.");
            Require(!handler.ChatRequests[2].TryGetProperty("tools", out _), "Third round still offered tools.");
            var tools = handler.ChatRequests[2].GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
            Require(tools.Length == 2 && tools[0].GetProperty("content").GetString()!.Contains("zero"), "Tool error was not reported to the model.");
        }
        using (var handler = new RecordingHandler(capabilities) { ChatBody = ToolCall("calculate", "2+2") })
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Calculate a number")], "tool-model", "en"), "more calculation steps");
            Require(handler.ChatRequests.Count == 3, "Tool rounds were not bounded.");
        }
        using (var handler = new RecordingHandler(capabilities) { ChatBody = ToolCall("run_shell", "2+2") })
        using (var client = TestClient(handler))
        using (var service = new ChatService(client))
        {
            await ExpectAsync<InvalidOperationException>(() => service.SendAsync([new("user", "Calculate a number")], "tool-model", "en"), "unsupported tool");
            Require(handler.ChatRequests.Count == 1, "An unsupported tool was dispatched.");
        }
        Console.WriteLine("PASS: calculator request schema, fragmented calls, exact tool results, final-only progress, error correction and two-round limit.");
    }

    private static async Task CheckLiveModels(bool strongerOnly)
    {
        using var transport = new CountingLiveHandler();
        using var client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        using var service = new ChatService(client);
        var timer = Stopwatch.StartNew();
        string reply;
        if (!strongerOnly)
        {
            reply = await service.SendWithOptionsAsync([new("user", "A notebook costs $3. I buy 4 notebooks and pay with $20. How much change should I receive? Give the answer and one short explanation.")], "qwen2.5:1.5b", "en", new("concise", false));
            Require(reply.Contains("8"), "Installed model failed the arithmetic smoke check.");
            Console.WriteLine($"PASS: qwen2.5 baseline synthetic arithmetic ({timer.Elapsed.TotalSeconds:0.00}s).");
        }

        timer.Restart();
        reply = await service.SendWithOptionsAsync([new("user", "I buy 3 notebooks at $4.50 each. Apply a 20% discount to their total, then add 10% sales tax to the discounted total. I pay $20. What is my change? Output exactly CHANGE= followed by the amount with two decimal places, without currency sign or any other text.")], "qwen3.5:4b", "en", new("balanced", true));
        Require(reply.Trim() == "CHANGE=8.12", "Stronger model failed multi-step arithmetic or the required exact format. Synthetic reply: " + reply);
        Require(transport.CalculatorResultRequests > 0, "The live arithmetic check did not verify native calculator integration.");
        Console.WriteLine($"PASS: qwen3.5 multi-step arithmetic and exact format, thinking enabled, native calculator result verified ({timer.Elapsed.TotalSeconds:0.00}s).");

        timer.Restart();
        reply = await service.SendWithOptionsAsync([new("user", "All glims are zorks. No zorks are nims. Can any glim be a nim? Reply with exactly No or Yes and nothing else.")], "qwen3.5:4b", "en", new("balanced", true));
        Require(reply.Trim().TrimEnd('.') == "No", "Stronger model failed the short logical reasoning check. Synthetic reply: " + reply);
        Console.WriteLine($"PASS: qwen3.5 short logical reasoning, thinking enabled ({timer.Elapsed.TotalSeconds:0.00}s).");

        timer.Restart();
        reply = await service.SendWithOptionsAsync([new("user", "Return a JSON object with exactly the keys language and value. Set language to English and value to the number 42. Return only JSON, without a code block or other prose.")], "qwen3.5:4b", "en", new("concise", false));
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        Require(root.EnumerateObject().Count() == 2 && root.GetProperty("language").GetString() == "English" && root.GetProperty("value").GetInt32() == 42, "Stronger model failed the JSON instruction-following check.");
        Console.WriteLine($"PASS: qwen3.5 exact JSON keys and values ({timer.Elapsed.TotalSeconds:0.00}s).");
    }

    private static HttpClient TestClient(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    private static string Answer(string answer, string reason = "stop") => JsonSerializer.Serialize(new { message = new { content = answer }, done = true, done_reason = reason }) + "\n";
    private static string ToolCall(string name, string expression) => JsonSerializer.Serialize(new { message = new { tool_calls = new[] { new { function = new { index = 0, name, arguments = new { expression } } } } }, done = true, done_reason = "stop" }) + "\n";
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task ExpectAsync<T>(Func<Task> action, string? contains = null) where T : Exception
    {
        try { await action(); }
        catch (T e) when (contains is null || e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new Exception("Expected " + typeof(T).Name + (contains is null ? "" : " containing " + contains));
    }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class CountingLiveHandler : DelegatingHandler
    {
        public int CalculatorResultRequests { get; private set; }
        public CountingLiveHandler() : base(new HttpClientHandler { UseProxy = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/chat")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"))
                    CalculatorResultRequests++;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
    private sealed class RecordingHandler(string showBody) : HttpMessageHandler
    {
        public int ShowCount { get; private set; }
        public List<JsonElement> ChatRequests { get; } = [];
        public string ChatBody { get; set; } = Answer("Synthetic answer.");
        public Queue<string> ChatBodies { get; } = new();
        public HttpStatusCode ChatStatus { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(request.RequestUri!.Host == "127.0.0.1" && request.RequestUri.Port == 11434, "Request escaped the fixed local Ollama address.");
            if (request.RequestUri.AbsolutePath == "/api/show")
            {
                Require(request.Method == HttpMethod.Post, "Capability discovery used the wrong HTTP method.");
                ShowCount++;
                return Response(showBody, HttpStatusCode.OK);
            }
            if (request.RequestUri.AbsolutePath == "/api/chat")
            {
                using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ChatRequests.Add(parsed.RootElement.Clone());
                return Response(ChatBodies.Count > 0 ? ChatBodies.Dequeue() : ChatBody, ChatStatus);
            }
            throw new Exception("Unexpected test endpoint.");
        }
        private static HttpResponseMessage Response(string body, HttpStatusCode status) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
    }
}
