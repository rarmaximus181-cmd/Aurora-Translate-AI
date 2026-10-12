using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuroraTranslate.Services;

namespace AuroraTranslate;

internal static class UiSmokeRunner
{
    public static async Task<bool> RunAsync(MainWindow window, string reportPath)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURORA_DATA_DIR")))
            throw new InvalidOperationException("UI checks need an isolated AURORA_DATA_DIR test folder.");
        var results = new List<object>();
        var passed = true;
        T Find<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { passed = false; results.Add(new { name, passed = false, error = ex.Message }); }
        }
        Find<CheckBox>("SpeakTranslationsCheck").IsChecked = false;
        Find<CheckBox>("SpeakChatCheck").IsChecked = false;
        Find<CheckBox>("ReasoningCheck").IsChecked = false;
        await Check("Extra voice catalog and Discord setup controls are available", () =>
        {
            Require(Find<ComboBox>("VoiceCatalogBox").Items.Count >= 12, "Extra voices missing.");
            Require(!Find<Button>("DiscordSignInButton").IsEnabled, "Unconfigured Discord login appears ready.");
            Require(Find<Button>("ImportVoiceButton").IsEnabled, "Voice import unavailable.");
            return Task.CompletedTask;
        });
        await Check("Profile images render in both account locations with a guest fallback", () =>
        {
            var path = Path.Combine(AppStore.DataDirectory, "test-avatar.png");
            Directory.CreateDirectory(AppStore.DataDirectory);
            var pixels = Enumerable.Repeat((byte)180, 32 * 32 * 4).ToArray();
            var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            try
            {
                Invoke("SetAccountAvatar", path, "Test");
                Require(Find<Image>("AccountAvatarImage").Source is not null && Find<Image>("DiscordAvatarImage").Source is not null, "Avatar images did not load.");
                Require(Find<Image>("AccountAvatarImage").Visibility == Visibility.Visible, "Sidebar avatar remained hidden.");
                Invoke("SetAccountAvatar", null!, "A");
                Require(Find<TextBlock>("AccountInitial").Visibility == Visibility.Visible, "Guest fallback missing.");
            }
            finally { File.Delete(path); Invoke("UpdateAccount"); }
            return Task.CompletedTask;
        });
        await Check("Translate UI displays online result and saves the matching history", async () =>
        {
            Find<ComboBox>("SourceLanguageBox").SelectedItem = Languages.Find("en");
            Find<ComboBox>("TargetLanguageBox").SelectedItem = Languages.Find("es");
            Find<TextBox>("SourceText").Text = "  Good morning\n";
            var task = (Task)Invoke("TranslateAsync")!;
            Require(Find<TextBox>("SourceText").IsReadOnly, "Source edit was not locked while translating.");
            await task;
            var result = Find<TextBox>("ResultText").Text;
            Require(!string.IsNullOrWhiteSpace(result) && result.StartsWith("  ", StringComparison.Ordinal) && result.EndsWith("\n", StringComparison.Ordinal), "Result or preserved whitespace missing.");
            Require(AppStore.LoadHistory()[0].TranslatedText == result, "History does not match displayed translation.");
            Require(!Find<TextBox>("SourceText").IsReadOnly, "Input remained locked.");
        });
        await Check("Swap preserves both texts and changes both language selectors", () =>
        {
            var source = Find<TextBox>("SourceText").Text;
            var result = Find<TextBox>("ResultText").Text;
            Invoke("Swap_Click", new Button(), new RoutedEventArgs());
            Require(Find<TextBox>("SourceText").Text == result && Find<TextBox>("ResultText").Text == source, "Swap lost text.");
            Require(((LanguageOption)Find<ComboBox>("SourceLanguageBox").SelectedItem).Code == "es", "Source language did not swap.");
            Require(((LanguageOption)Find<ComboBox>("TargetLanguageBox").SelectedItem).Code == "en", "Destination language did not swap.");
            return Task.CompletedTask;
        });
        await Check("Editing source invalidates the previous translation", () =>
        {
            Find<TextBox>("SourceText").Text = "A new sentence";
            Require(Find<TextBox>("ResultText").Text.Length == 0, "Old translation was retained for new text.");
            Require(!Find<Button>("SpeakResultButton").IsEnabled, "Stale result remained speakable.");
            return Task.CompletedTask;
        });
        await Check("Cancel leaves no result or extra history and unlocks controls", async () =>
        {
            Find<ComboBox>("SourceLanguageBox").SelectedItem = Languages.Find("en");
            Find<ComboBox>("TargetLanguageBox").SelectedItem = Languages.Find("es");
            Find<TextBox>("SourceText").Text = "Cancel this translation immediately.";
            var count = AppStore.LoadHistory().Count;
            var task = (Task)Invoke("TranslateAsync")!;
            Invoke("Cancel_Click", new Button(), new RoutedEventArgs());
            await task;
            Require(Find<TextBox>("ResultText").Text.Length == 0, "Canceled translation displayed a result.");
            Require(AppStore.LoadHistory().Count == count, "Canceled translation was saved.");
            Require(!Find<TextBox>("SourceText").IsReadOnly && Find<Button>("TranslateButton").IsEnabled, "Controls remained locked.");
        });
        await Check("Conversation UI sends a real local message and receives a reply", async () =>
        {
            window.ShowPage("chat");
            Find<TextBox>("ChatInput").Text = "Say hello in one short sentence.";
            await (Task)Invoke("SendChatAsync")!;
            var messages = (List<ChatMessage>)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Require(messages.Count == 2 && messages[^1].Role == "assistant" && !string.IsNullOrWhiteSpace(messages[^1].Content), "No real assistant reply in conversation.");
            Require(Find<StackPanel>("ChatMessagesPanel").Children.Count == 2, "Conversation bubbles missing.");
        });
        await Check("Advanced troll display keeps original answers and reveals them on demand", () =>
        {
            var original = "This is not allowed. Here is the original answer.";
            var content = (TextBox)Invoke("AddChatBubble", "AURORA", original, false, "en-US", true)!;
            Find<CheckBox>("TrollModeCheck").IsChecked = true;
            Require(content.Text == "This is allowed. Here is the original answer.", "Troll display did not transform the phrase.");
            var panel = (StackPanel)content.Parent;
            var notice = panel.Children.OfType<TextBlock>().FirstOrDefault(x => x.Text.Contains("Troll display"));
            Require(notice is not null && notice.Visibility == Visibility.Visible, "Joke display was not labeled.");
            var actions = panel.Children.OfType<StackPanel>().Last();
            var originalButton = actions.Children.OfType<Button>().First(x => Equals(x.Content, "Show original"));
            originalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(content.Text == original, "Original answer could not be recovered.");
            Find<CheckBox>("TrollModeCheck").IsChecked = false;
            Require(content.Text == original, "Turning off troll display changed the original answer.");
            return Task.CompletedTask;
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed, results }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }
}
