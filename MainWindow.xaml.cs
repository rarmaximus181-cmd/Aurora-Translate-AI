using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AuroraTranslate.Services;
using Microsoft.Win32;
using WpfAnimatedGif;

namespace AuroraTranslate;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TranslationService _translator = new();
    private readonly ChatService _chat = new();
    private readonly VoiceService _voice = new();
    private readonly DiscordAuthService _discord = new();
    private readonly VoiceLibraryService _voiceLibrary = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly List<TranslationHistory> _history;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? _operation;
    private bool _ready;
    private bool _closing;
    private bool _listening;
    private string? _loadedBackground;
    private IReadOnlyList<string> _recognitionLanguages = [];
    private IReadOnlyList<string> _availableModels = [];
    private bool _syncingModelSelection;
    private readonly Dictionary<TextBox, ReplyView> _replyViews = [];

    private sealed class ReplyView
    {
        public required TextBox Text { get; init; }
        public required TextBlock Notice { get; init; }
        public required Button OriginalButton { get; init; }
        public required StackPanel Actions { get; init; }
        public string UserQuestion { get; init; } = "";
        public string Original { get; set; } = "";
        public bool ShowOriginal { get; set; }
        public bool Complete { get; set; }
    }

    public Func<Task>? CheckUpdatesRequested { get; set; }

    public MainWindow()
    {
        _settings = AppStore.LoadSettings();
        _history = AppStore.LoadHistory();
        InitializeComponent();
        SourceLanguageBox.ItemsSource = Languages.All;
        TargetLanguageBox.ItemsSource = Languages.All;
        ConversationLanguageBox.ItemsSource = Languages.All;
        SourceLanguageBox.SelectedItem = Languages.Find(_settings.SourceLanguage);
        TargetLanguageBox.SelectedItem = Languages.Find(_settings.TargetLanguage);
        ConversationLanguageBox.SelectedItem = Languages.Find(_settings.ConversationLanguage);
        EngineBox.SelectedIndex = _settings.TranslationEngine == "local" ? 1 : 0;
        ModelTextBox.Text = _settings.OllamaModel;
        AnimateThemeCheck.IsChecked = _settings.AnimateTheme;
        BackgroundOpacitySlider.Value = _settings.BackgroundOpacity;
        SpeakTranslationsCheck.IsChecked = _settings.SpeakTranslations;
        SpeakChatCheck.IsChecked = _settings.SpeakChatReplies;
        SaveHistoryCheck.IsChecked = _settings.SaveHistory;
        SpeechRateSlider.Value = _settings.SpeechRate;
        VoiceCatalogBox.ItemsSource = VoiceLibraryService.Catalog;
        VoiceCatalogBox.SelectedIndex = 0;
        DiscordClientIdBox.Text = _discord.ClientId ?? "";
        ResponseStyleBox.SelectedIndex = _settings.ResponseStyle switch { "concise" => 0, "detailed" => 2, _ => 1 };
        ReasoningCheck.IsChecked = _settings.ReasoningEnabled;
        TrollModeCheck.IsChecked = _settings.TrollMode;
        TrollModeBanner.Visibility = _settings.TrollMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateRepositoryBox.Text = _settings.UpdateRepository;
        UpdateVersionText.Text = "v" + UpdateService.CurrentVersion;
        SyncChatModelSelector();
        VoiceBox.ItemsSource = new[] { new VoiceOption("", "Automatic · match selected language", "") };
        VoiceBox.SelectedIndex = 0;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistSettings(); };
        _ready = true;
        UpdateLabels();
        UpdateAccount();
        ApplyTheme();
        RenderHistory();
        ShowPage("translate");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { var enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); }
        catch (DllNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAnimations();
        _ = LoadVoicesAsync();
        try
        {
            var models = await _chat.GetModelsAsync();
            if (_closing) return;
            _availableModels = models;
            SyncChatModelSelector();
            if (models.Contains(_settings.OllamaModel, StringComparer.OrdinalIgnoreCase))
                ChatSetupHint.Text = "Your local model is ready. Type a message, or tap the microphone to speak.";
            else
                ChatSetupHint.Text = "Ollama is running. Download or choose an installed model in Settings to start.";
        }
        catch (Exception) { if (!_closing) ChatSetupHint.Text = "Install and open Ollama, then download a model in Settings to get started. No AI API key required."; }
    }

    public void ShowPage(string page)
    {
        TranslatePage.Visibility = page == "translate" ? Visibility.Visible : Visibility.Collapsed;
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { TranslateNav, ChatNav, HistoryNav, SettingsNav })
        {
            var selected = Equals(button.Tag, page);
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#24334E" : "#00000000"));
            button.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected ? "#C4D0FF" : "#A3B3C9"));
        }
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "chat" => ("Conversation", "A focused space for writing, ideas, and answers. Powered by your local model."),
            "history" => ("Translation history", "Return to your recent translations, saved on this computer."),
            "settings" => ("Settings", "Personalize your workspace, configure local AI, and manage your account."),
            _ => ("Translate", "Clear communication, in any language. Type, speak, or listen.")
        };
        PageEyebrow.Text = "WORKSPACE / " + (page == "chat" ? "CONVERSATION" : page.ToUpperInvariant());
        if (page == "history") RenderHistory();
    }

    public void SetPreviewTheme(string theme)
    {
        _settings.Theme = theme;
        ApplyTheme();
    }

    private void Nav_Click(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void Account_Click(object sender, RoutedEventArgs e) => ScrollSettingsTo("account");

    private void LoadVoices()
    {
        try
        {
            var voices = new List<VoiceOption> { new("", "Automatic · match selected language", "") };
            voices.AddRange(_voice.GetVoices());
            VoiceBox.ItemsSource = voices;
            VoiceBox.SelectedItem = voices.FirstOrDefault(v => v.Id == _settings.VoiceId) ?? voices[0];
            ImportedVoicesBox.ItemsSource = _voiceLibrary.GetInstalledVoices();
            ImportedVoicesBox.SelectedIndex = ImportedVoicesBox.Items.Count > 0 ? 0 : -1;
            _recognitionLanguages = _voice.GetRecognitionLanguages();
            RecognitionStatus.Text = _recognitionLanguages.Count == 0
                ? "No speech recognizer is installed. Add a Windows speech language to use microphone input."
                : $"Microphone languages: {string.Join(", ", _recognitionLanguages)}. Downloaded voices add speaking languages; microphone recognition still needs a Windows Speech language pack.";
        }
        catch (Exception ex) { RecognitionStatus.Text = ex.Message; }
    }

    private async Task LoadVoicesAsync()
    {
        try
        {
            var inventory = await Task.Run(() => (_voice.GetVoices(), _voiceLibrary.GetInstalledVoices(), _voice.GetRecognitionLanguages()));
            if (_closing) return;
            var voices = new List<VoiceOption> { new("", "Automatic · match selected language", "") };
            voices.AddRange(inventory.Item1);
            VoiceBox.ItemsSource = voices;
            VoiceBox.SelectedItem = voices.FirstOrDefault(v => v.Id == _settings.VoiceId) ?? voices[0];
            ImportedVoicesBox.ItemsSource = inventory.Item2;
            ImportedVoicesBox.SelectedIndex = ImportedVoicesBox.Items.Count > 0 ? 0 : -1;
            _recognitionLanguages = inventory.Item3;
            RecognitionStatus.Text = _recognitionLanguages.Count == 0
                ? "No speech recognizer is installed. Add a Windows speech language to use microphone input."
                : $"Microphone languages: {string.Join(", ", _recognitionLanguages)}. Downloaded voices add speaking languages; microphone recognition still needs a Windows Speech language pack.";
        }
        catch (Exception ex) { if (!_closing) RecognitionStatus.Text = ex.Message; }
    }

    private void UpdateLabels()
    {
        TranslationEngineLabel.Text = _settings.TranslationEngine == "local" ? $"Private local translation · {_settings.OllamaModel}" : "Free online translation · MyMemory";
        ChatModelLabel.Text = $"{_settings.OllamaModel} · local model";
        ThemeLabel.Text = $"Current theme: {_settings.Theme}";
        BackgroundFileLabel.Text = _settings.CustomBackground is null ? "No custom background imported" : "Imported: " + Path.GetFileName(_settings.CustomBackground);
    }

    private void Source_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SourceCount is null || SourcePlaceholder is null) return;
        SourceCount.Text = $"{SourceText.Text.Length:N0} / 10,000";
        SourcePlaceholder.Visibility = string.IsNullOrEmpty(SourceText.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_ready) SetResult("");
    }

    private void SetResult(string text)
    {
        ResultText.Text = text;
        ResultPlaceholder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
        SpeakResultButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
        CopyResultButton.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.SourceLanguage = ((LanguageOption)SourceLanguageBox.SelectedItem).Code;
        _settings.TargetLanguage = ((LanguageOption)TargetLanguageBox.SelectedItem).Code;
        SetResult("");
        ScheduleSave();
    }

    private void ConversationLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.ConversationLanguage = ((LanguageOption)ConversationLanguageBox.SelectedItem).Code;
        ScheduleSave();
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return; }
        var source = SourceLanguageBox.SelectedItem;
        var oldText = SourceText.Text;
        var oldResult = ResultText.Text;
        SourceLanguageBox.SelectedItem = TargetLanguageBox.SelectedItem;
        TargetLanguageBox.SelectedItem = source;
        SourceText.Text = oldResult;
        SetResult(oldText);
    }

    private void ClearSource_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        SourceText.Clear();
        SetResult("");
        SourceText.Focus();
    }

    private void Source_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; _ = TranslateAsync(); }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceText.Text)) { SetStatus("Write or speak something to translate first."); SourceText.Focus(); return; }
        var token = BeginOperation("Translating your words…");
        if (token is null) return;
        var source = (LanguageOption)SourceLanguageBox.SelectedItem;
        var target = (LanguageOption)TargetLanguageBox.SelectedItem;
        var input = SourceText.Text;
        SetResult("");
        TranslateButton.Content = "Translating…";
        CancelTranslateButton.Visibility = Visibility.Visible;
        try
        {
            var result = await _translator.TranslateAsync(input, source.Code, target.Code, _settings.TranslationEngine,
                _settings.OllamaModel, new Progress<string>(p => { if (IsOperationActive(token.Value)) SetStatus(p); }), token.Value);
            token.Value.ThrowIfCancellationRequested();
            SetResult(result.Text);
            string? historyWarning = null;
            if (_settings.SaveHistory)
            {
                _history.Insert(0, new(input, result.Text, source.Code, target.Code, DateTimeOffset.Now));
                if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
                try { AppStore.SaveHistory(_history); }
                catch (Exception ex) { historyWarning = "History could not be saved: " + ex.Message; }
            }
            SetStatus($"Translated to {target.Name} · {result.EngineDisplay}." + (historyWarning is null ? "" : " " + historyWarning), historyWarning is not null);
            if (_settings.SpeakTranslations)
            {
                try { await _voice.SpeakAsync(result.Text, target.Culture, _settings.VoiceId, _settings.SpeechRate, token.Value); }
                catch (OperationCanceledException) { SetStatus("Translation ready. Voice stopped."); }
                catch (Exception ex) { SetStatus("Translation ready. " + ex.Message, true); }
            }
        }
        catch (OperationCanceledException) { SetStatus("Translation cancelled."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private async void TranslateMic_Click(object sender, RoutedEventArgs e)
    {
        var token = BeginOperation("Listening… Say one phrase in your source language.");
        if (token is null) return;
        var heard = false;
        _listening = true;
        TranslateMicButton.Content = "● Listening";
        try
        {
            var language = (LanguageOption)SourceLanguageBox.SelectedItem;
            var result = await _voice.ListenAsync(language.Culture, TimeSpan.FromSeconds(15), token.Value);
            token.Value.ThrowIfCancellationRequested();
            SourceText.Text = result;
            heard = true;
        }
        catch (OperationCanceledException) { SetStatus("Listening stopped."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
        if (heard) await TranslateAsync();
    }

    private async void SpeakResult_Click(object sender, RoutedEventArgs e) => await SpeakTextAsync(ResultText.Text, ((LanguageOption)TargetLanguageBox.SelectedItem).Culture);

    private async Task SpeakTextAsync(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var token = BeginOperation("Reading aloud…");
        if (token is null) return;
        try
        {
            await _voice.SpeakAsync(text, language, _settings.VoiceId, _settings.SpeechRate, token.Value);
            token.Value.ThrowIfCancellationRequested();
            SetStatus("Finished reading aloud.");
        }
        catch (OperationCanceledException) { SetStatus("Voice stopped."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e) => CopyText(ResultText.Text);
    private void CopyText(string text)
    {
        try { Clipboard.SetText(text); SetStatus("Copied to clipboard."); }
        catch (ExternalException) { SetStatus("The clipboard is busy. Please try again.", true); }
    }

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; _ = SendChatAsync(); }
    }

    private void ChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ChatInputPlaceholder is not null) ChatInputPlaceholder.Visibility = string.IsNullOrEmpty(ChatInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        ChatInput.Text = ((Button)sender).Tag.ToString() ?? "";
        ChatInput.Focus(); ChatInput.CaretIndex = ChatInput.Text.Length;
    }

    private void ResponseStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ResponseStyleBox.SelectedItem is not ComboBoxItem item) return;
        _settings.ResponseStyle = item.Tag.ToString() ?? "balanced";
        ScheduleSave();
    }

    private void ConversationOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ReasoningEnabled = ReasoningCheck.IsChecked == true;
        _settings.TrollMode = TrollModeCheck.IsChecked == true;
        TrollModeBanner.Visibility = _settings.TrollMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var view in _replyViews.Values) RenderReply(view);
        ScheduleSave();
    }

    private void SyncChatModelSelector()
    {
        _syncingModelSelection = true;
        try
        {
            var models = _availableModels.Append(_settings.OllamaModel).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).ToArray();
            ChatModelBox.ItemsSource = models;
            ChatModelBox.SelectedItem = models.FirstOrDefault(m => m.Equals(_settings.OllamaModel, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingModelSelection = false; }
    }

    private void ChatModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingModelSelection || ChatModelBox.SelectedItem is not string model) return;
        ModelTextBox.Text = model;
    }

    public async Task RefreshChatModelsAsync(CancellationToken cancellationToken = default)
    {
        _availableModels = await _chat.GetModelsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_closing) SyncChatModelSelector();
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatInput.Text)) { SetStatus("Type a message or tap the microphone."); ChatInput.Focus(); return; }
        var token = BeginOperation("Aurora is thinking…");
        if (token is null) return;
        var text = ChatInput.Text.Trim();
        var language = (LanguageOption)ConversationLanguageBox.SelectedItem;
        var model = _settings.OllamaModel;
        ChatInput.Clear();
        _messages.Add(new("user", text));
        AddChatBubble("YOU", text, true, language.Culture);
        var reply = AddChatBubble("AURORA", "Thinking…", false, language.Culture, false);
        SendChatButton.Content = "Thinking…";
        try
        {
            var options = new ChatOptions(_settings.ResponseStyle, _settings.ReasoningEnabled);
            var result = await _chat.SendWithOptionsAsync(_messages, model, language.Code, options,
                new Progress<string>(partial => { if (IsOperationActive(token.Value)) { UpdateReply(reply, partial, false); ChatScroll.ScrollToEnd(); } }), token.Value);
            token.Value.ThrowIfCancellationRequested();
            UpdateReply(reply, result, true);
            _messages.Add(new("assistant", result));
            SetStatus($"Replied in {language.Name} · {_settings.ResponseStyle} response. Local AI can make mistakes.");
            if (_settings.SpeakChatReplies)
            {
                try { await _voice.SpeakAsync(result, language.Culture, _settings.VoiceId, _settings.SpeechRate, token.Value); }
                catch (OperationCanceledException) { SetStatus("Reply ready. Voice stopped."); }
                catch (Exception ex) { SetStatus("Reply ready. " + ex.Message, true); }
            }
        }
        catch (OperationCanceledException) { UpdateReply(reply, "Response cancelled.", false); RemovePendingUser(text); SetStatus("AI response cancelled."); }
        catch (Exception ex) { UpdateReply(reply, ex.Message, false); reply.Foreground = new SolidColorBrush(Color.FromRgb(255, 182, 182)); RemovePendingUser(text); SetStatus(ex.Message, true); }
        finally { EndOperation(); ChatScroll.ScrollToEnd(); }
    }

    private void RemovePendingUser(string text)
    {
        if (_messages.Count > 0 && _messages[^1].Role == "user" && _messages[^1].Content == text) _messages.RemoveAt(_messages.Count - 1);
        if (string.IsNullOrWhiteSpace(ChatInput.Text)) ChatInput.Text = text;
    }

    private TextBox AddChatBubble(string title, string text, bool user, string language, bool ready = true)
    {
        ChatWelcome.Visibility = Visibility.Collapsed;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(user ? Color.FromRgb(133, 161, 191) : Color.FromRgb(165, 185, 246)), Margin = new Thickness(0, 0, 0, 10) });
        var content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, FontSize = 15, Padding = new Thickness(0), AcceptsReturn = true, IsReadOnlyCaretVisible = false };
        panel.Children.Add(content);
        if (!user)
        {
            var notice = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(212, 190, 132)), Margin = new Thickness(0, 13, 0, 0), Visibility = Visibility.Collapsed };
            panel.Children.Add(notice);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 0) };
            var copy = new Button { Content = "Copy original", Style = (Style)FindResource("SmallButton"), Padding = new Thickness(9, 5, 9, 5), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ParseBrush("#8DA6C7") };
            var speak = new Button { Content = "Listen", Style = (Style)FindResource("SmallButton"), Padding = new Thickness(9, 5, 9, 5), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ParseBrush("#8DA6C7") };
            var original = new Button { Content = "Show original", Style = (Style)FindResource("SmallButton"), Padding = new Thickness(9, 5, 9, 5), Background = ParseBrush("#2A2B29"), BorderThickness = new Thickness(0), Foreground = ParseBrush("#D4C693"), Visibility = Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 0) };
            var view = new ReplyView { Text = content, Notice = notice, OriginalButton = original, Actions = actions,
                UserQuestion = _messages.LastOrDefault(message => message.Role == "user")?.Content ?? "",
                Original = ready ? text : "", Complete = ready };
            _replyViews.Add(content, view);
            copy.Click += (_, _) => CopyText(view.Original);
            speak.Click += async (_, _) => await SpeakTextAsync(view.Original, language);
            original.Click += (_, _) => { view.ShowOriginal = !view.ShowOriginal; RenderReply(view); };
            actions.Children.Add(speak); actions.Children.Add(copy); actions.Children.Add(original);
            panel.Children.Add(actions);
            RenderReply(view);
        }
        var bubble = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(20), Margin = new Thickness(user ? 80 : 0, 0, user ? 0 : 32, 18),
            Background = ParseBrush(user ? "#1B2B42" : "#141F30"), BorderBrush = ParseBrush(user ? "#344861" : "#26374E"), BorderThickness = new Thickness(1), Child = panel };
        ChatMessagesPanel.Children.Add(bubble);
        ChatScroll.ScrollToEnd();
        return content;
    }

    private static SolidColorBrush ParseBrush(string hex) => new(ParseColor(hex));

    private void UpdateReply(TextBox content, string original, bool complete)
    {
        if (!_replyViews.TryGetValue(content, out var view)) { content.Text = original; return; }
        view.Original = original; view.Complete = complete; RenderReply(view);
    }

    private void RenderReply(ReplyView view)
    {
        if (string.IsNullOrEmpty(view.Original)) { view.Actions.Visibility = Visibility.Collapsed; return; }
        var jokeEnabled = _settings.TrollMode && view.Complete;
        var ebikeJoke = jokeEnabled && ConversationPresentation.IsEbikeRoadPermissionQuestion(view.UserQuestion);
        var formatted = ConversationPresentation.FormatReply(view.Original, jokeEnabled, view.UserQuestion);
        var transformed = jokeEnabled && (ebikeJoke || !string.Equals(formatted, view.Original, StringComparison.Ordinal));
        view.Text.Text = view.ShowOriginal || !transformed ? view.Original : formatted;
        view.Notice.Visibility = transformed ? Visibility.Visible : Visibility.Collapsed;
        view.Notice.Text = view.ShowOriginal ? "Original model answer · joke display hidden for this reply"
            : ebikeJoke ? "Troll display · e-bike joke · not road-permission advice"
            : "Troll display · joke text only · original answer unchanged";
        view.OriginalButton.Visibility = transformed && view.Complete ? Visibility.Visible : Visibility.Collapsed;
        view.OriginalButton.Content = view.ShowOriginal ? "Show joke display" : "Show original";
        view.Actions.Visibility = view.Complete ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChatMic_Click(object sender, RoutedEventArgs e)
    {
        var token = BeginOperation("Listening… Say one message in your conversation language.");
        if (token is null) return;
        var heard = false;
        _listening = true;
        ChatMicButton.Content = "●";
        try
        {
            var language = (LanguageOption)ConversationLanguageBox.SelectedItem;
            var result = await _voice.ListenAsync(language.Culture, TimeSpan.FromSeconds(15), token.Value);
            token.Value.ThrowIfCancellationRequested();
            ChatInput.Text = result;
            heard = true;
        }
        catch (OperationCanceledException) { SetStatus("Listening stopped."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
        if (heard) await SendChatAsync();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return; }
        _voice.Stop();
        _messages.Clear();
        _replyViews.Clear();
        ChatMessagesPanel.Children.Clear();
        ChatWelcome.Visibility = Visibility.Visible;
        ChatInput.Clear();
        SetStatus("A fresh conversation. Say hello.");
    }

    private void RenderHistory()
    {
        HistoryPanel.Children.Clear();
        HistoryCount.Text = $"{_history.Count} recent translation{(_history.Count == 1 ? "" : "s")} · stored on this computer";
        if (_history.Count == 0)
        {
            HistoryPanel.Children.Add(new TextBlock { Text = "Your translations will appear here. Start with a few words.", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(4, 38, 4, 0), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var item in _history)
        {
            var card = new Border { Style = (Style)FindResource("CardStyle"), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12) };
            var content = new StackPanel();
            var title = new TextBlock { Text = $"{Languages.Find(item.SourceCode).Name} → {Languages.Find(item.TargetCode).Name} · {item.CreatedAt.LocalDateTime:g}", Foreground = (Brush)FindResource("AccentBright"), FontSize = 11, Margin = new Thickness(0, 0, 0, 11) };
            content.Children.Add(title);
            content.Children.Add(new TextBlock { Text = Preview(item.SourceText, 160), Foreground = (Brush)FindResource("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = Preview(item.TranslatedText, 260), FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0) });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            var use = new Button { Content = "Open translation", Style = (Style)FindResource("SmallButton") };
            use.Click += (_, _) =>
            {
                if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return; }
                SourceLanguageBox.SelectedItem = Languages.Find(item.SourceCode);
                TargetLanguageBox.SelectedItem = Languages.Find(item.TargetCode);
                SourceText.Text = item.SourceText; SetResult(item.TranslatedText); ShowPage("translate");
            };
            var remove = new Button { Content = "Delete", Style = (Style)FindResource("SmallButton"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(9, 0, 0, 0) };
            remove.Click += (_, _) => { _history.Remove(item); PersistHistory(); RenderHistory(); };
            actions.Children.Add(use); actions.Children.Add(remove); content.Children.Add(actions); card.Child = content; HistoryPanel.Children.Add(card);
        }
    }

    private static string Preview(string text, int maximum) => text.Length > maximum ? text[..maximum] + "…" : text;
    private bool PersistHistory()
    {
        try { AppStore.SaveHistory(_history); return true; }
        catch (Exception ex) { SetStatus("History could not be saved: " + ex.Message, true); return false; }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        if (MessageBox.Show(this, "Delete all saved translations from this computer?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _history.Clear(); var saved = PersistHistory(); RenderHistory(); if (saved) SetStatus("History cleared.");
    }

    private CancellationToken? BeginOperation(string status)
    {
        if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return null; }
        _voice.Stop();
        _operation = new CancellationTokenSource();
        TranslateButton.IsEnabled = SendChatButton.IsEnabled = TranslateMicButton.IsEnabled = ChatMicButton.IsEnabled = DownloadModelButton.IsEnabled = DiscordSignInButton.IsEnabled = DownloadVoiceButton.IsEnabled = ImportVoiceButton.IsEnabled = PreviewVoiceButton.IsEnabled = RemoveVoiceButton.IsEnabled = false;
        SourceLanguageBox.IsEnabled = TargetLanguageBox.IsEnabled = ConversationLanguageBox.IsEnabled = ChatModelBox.IsEnabled = ResponseStyleBox.IsEnabled = ReasoningCheck.IsEnabled = false;
        SourceText.IsReadOnly = true;
        GlobalCancelButton.Visibility = Visibility.Visible;
        SetStatus(status);
        return _operation.Token;
    }

    private void EndOperation()
    {
        _operation?.Dispose(); _operation = null;
        _listening = false;
        if (_closing) return;
        TranslateButton.IsEnabled = SendChatButton.IsEnabled = TranslateMicButton.IsEnabled = ChatMicButton.IsEnabled = DownloadModelButton.IsEnabled = DownloadVoiceButton.IsEnabled = ImportVoiceButton.IsEnabled = PreviewVoiceButton.IsEnabled = RemoveVoiceButton.IsEnabled = true;
        DiscordSignInButton.IsEnabled = _discord.HasCredentials;
        SourceLanguageBox.IsEnabled = TargetLanguageBox.IsEnabled = ConversationLanguageBox.IsEnabled = ChatModelBox.IsEnabled = ResponseStyleBox.IsEnabled = ReasoningCheck.IsEnabled = true;
        SourceText.IsReadOnly = false;
        TranslateButton.Content = "Translate  →"; SendChatButton.Content = "Send  ↑";
        TranslateMicButton.Content = "Speak"; ChatMicButton.Content = "Speak";
        CancelTranslateButton.Visibility = GlobalCancelButton.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); _voice.Stop(); SetStatus("Cancelling…"); }
    private void StopVoice_Click(object sender, RoutedEventArgs e) { if (_listening) _operation?.Cancel(); _voice.Stop(); SetStatus("Voice stopped."); }

    private bool IsOperationActive(CancellationToken token) => !_closing && !token.IsCancellationRequested && _operation is not null && _operation.Token == token;

    private void SetStatus(string message, bool error = false)
    {
        if (_closing) return;
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error ? Color.FromRgb(255, 181, 176) : Color.FromRgb(156, 179, 206));
    }

    private void ScheduleSave()
    {
        if (!_ready || _closing) return;
        UpdateLabels(); _saveTimer.Stop(); _saveTimer.Start();
    }

    private void PersistSettings()
    {
        try { AppStore.SaveSettings(_settings); }
        catch (Exception ex) { SetStatus("Settings could not be saved: " + ex.Message, true); }
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.AnimateTheme = AnimateThemeCheck.IsChecked == true;
        _settings.SpeakTranslations = SpeakTranslationsCheck.IsChecked == true;
        _settings.SpeakChatReplies = SpeakChatCheck.IsChecked == true;
        _settings.SaveHistory = SaveHistoryCheck.IsChecked == true;
        ApplyAnimations(); ScheduleSave();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value;
        CustomBackgroundImage.Opacity = _settings.BackgroundOpacity;
        OrbOne.Opacity = _settings.BackgroundOpacity * 0.45;
        OrbTwo.Opacity = _settings.BackgroundOpacity * 0.33;
        OrbThree.Opacity = _settings.BackgroundOpacity * 0.23;
        ScheduleSave();
    }

    private void SpeechRate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return; _settings.SpeechRate = (int)SpeechRateSlider.Value; ScheduleSave();
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.TranslationEngine = ((ComboBoxItem)EngineBox.SelectedItem).Tag.ToString()!; ScheduleSave();
    }

    private void Model_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return; _settings.OllamaModel = ModelTextBox.Text.Trim(); SyncChatModelSelector(); ScheduleSave();
    }

    private void Voice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || VoiceBox.SelectedItem is not VoiceOption voice) return;
        _settings.VoiceId = string.IsNullOrWhiteSpace(voice.Id) ? null : voice.Id; ScheduleSave();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        var token = BeginOperation("Checking installed local AI models…"); if (token is null) return;
        try { await RefreshModelsAsync(token.Value); SetStatus("Local AI model list refreshed."); }
        catch (OperationCanceledException) { SetStatus("Model refresh cancelled."); }
        catch (Exception ex) { ModelsStatus.Text = ex.Message; SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private async Task RefreshModelsAsync(CancellationToken token)
    {
        var models = await _chat.GetModelsAsync(token);
        token.ThrowIfCancellationRequested();
        _availableModels = models; SyncChatModelSelector();
        InstalledModelsBox.ItemsSource = models;
        InstalledModelsBox.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledModelsBox.SelectedItem = models.FirstOrDefault(m => m == _settings.OllamaModel);
        ModelsStatus.Text = models.Count == 0 ? "Ollama is running. Download a model to start conversation and local translation." : $"{models.Count} installed model{(models.Count == 1 ? "" : "s")}. Choose one above, or enter a model name to download.";
    }

    private void InstalledModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledModelsBox.SelectedItem is string name) ModelTextBox.Text = name;
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model)) { SetStatus("Enter a model name, such as qwen2.5:1.5b."); return; }
        var token = BeginOperation($"Downloading {model} through Ollama…"); if (token is null) return;
        try
        {
            await _chat.PullModelAsync(model, new Progress<string>(p => { if (IsOperationActive(token.Value)) { ModelsStatus.Text = p; SetStatus(p); } }), token.Value);
            token.Value.ThrowIfCancellationRequested();
            await RefreshModelsAsync(token.Value);
            SetStatus($"{model} is ready for local AI conversation and translation.");
        }
        catch (OperationCanceledException) { ModelsStatus.Text = "Download cancelled. Download again to resume."; SetStatus("Model download cancelled."); }
        catch (Exception ex) { ModelsStatus.Text = ex.Message; SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ((Button)sender).Tag.ToString()!;
        ApplyTheme(); ScheduleSave(); SetStatus($"{_settings.Theme} theme selected.");
    }

    private void ApplyTheme()
    {
        var theme = _settings.Theme;
        var colors = theme switch
        {
            "Nebula" => ("#160E22", "#31152E", "#E6A1D8", "#FFD0ED", "#D562C9", "#8279F9", "#E88CBE"),
            "Ocean" => ("#081624", "#0C2D36", "#69D6D5", "#B3F4EE", "#5AABFA", "#43DBCC", "#57BFDC"),
            "Sunset" => ("#1E1422", "#321F2F", "#E8B18C", "#FFE0BD", "#E6A263", "#C4729F", "#E58B80"),
            _ => ("#0A1220", "#121D30", "#8296FF", "#BEC9FF", "#5775D6", "#37BBAE", "#738EDD")
        };
        GradientA.Color = ParseColor(colors.Item1); GradientB.Color = ParseColor(colors.Item2);
        Resources["Accent"] = new SolidColorBrush(ParseColor(colors.Item3)); Resources["AccentBright"] = new SolidColorBrush(ParseColor(colors.Item4));
        SetOrbColor(OrbOne, colors.Item5); SetOrbColor(OrbTwo, colors.Item6); SetOrbColor(OrbThree, colors.Item7);
        OrbOne.Opacity = _settings.BackgroundOpacity * 0.45; OrbTwo.Opacity = _settings.BackgroundOpacity * 0.33; OrbThree.Opacity = _settings.BackgroundOpacity * 0.23;
        CustomBackgroundImage.Opacity = _settings.BackgroundOpacity;
        var custom = theme == "Custom" ? _settings.CustomBackground : null;
        if (custom != _loadedBackground)
        {
            ImageBehavior.SetAnimatedSource(CustomBackgroundImage, null); CustomBackgroundImage.Source = null;
            _loadedBackground = null;
            if (custom is not null && File.Exists(custom))
            {
                try
                {
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(custom, UriKind.Absolute); image.EndInit();
                    if (Path.GetExtension(custom).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        ImageBehavior.SetAutoStart(CustomBackgroundImage, _settings.AnimateTheme);
                        ImageBehavior.SetRepeatBehavior(CustomBackgroundImage, RepeatBehavior.Forever);
                        ImageBehavior.SetAnimatedSource(CustomBackgroundImage, image);
                    }
                    else CustomBackgroundImage.Source = image;
                    _loadedBackground = custom;
                }
                catch (Exception ex) { SetStatus("Background could not be opened: " + ex.Message, true); }
            }
        }
        UpdateLabels(); ApplyAnimations();
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static void SetOrbColor(System.Windows.Shapes.Ellipse orb, string hex)
    {
        var color = ParseColor(hex); orb.Fill = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
    }

    private void ApplyAnimations()
    {
        foreach (var transform in new[] { OrbOneMotion, OrbTwoMotion, OrbThreeMotion }) { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.BeginAnimation(TranslateTransform.YProperty, null); }
        if (_settings.AnimateTheme)
        {
            AnimateOrb(OrbOneMotion, 65, 60, 18); AnimateOrb(OrbTwoMotion, -90, -70, 25); AnimateOrb(OrbThreeMotion, -50, 100, 21);
        }
        ImageBehavior.SetAutoStart(CustomBackgroundImage, _settings.AnimateTheme);
        var controller = ImageBehavior.GetAnimationController(CustomBackgroundImage);
        if (controller is not null) { if (_settings.AnimateTheme) controller.Play(); else controller.Pause(); }
    }

    private static void AnimateOrb(TranslateTransform transform, double x, double y, int seconds)
    {
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, x, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = easing });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, y, TimeSpan.FromSeconds(seconds + 4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = easing });
    }

    private void ImportBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose your Aurora background", Filter = "PNG or animated GIF|*.png;*.gif", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _settings.CustomBackground = AppStore.ImportBackground(dialog.FileName); _settings.Theme = "Custom"; ApplyTheme(); ScheduleSave(); SetStatus("Your background is ready. Change its visibility above."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void UseCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.CustomBackground is null) { SetStatus("Import a PNG or GIF background first."); return; }
        _settings.Theme = "Custom"; ApplyTheme(); ScheduleSave(); SetStatus("Imported background selected.");
    }

    private void UpdateAccount()
    {
        try
        {
            var profile = _discord.LoadProfile();
            AccountName.Text = profile?.DisplayName ?? "Guest";
            AccountSubtitle.Text = profile is null ? "Discord account in Settings" : "@" + profile.Username;
            DiscordProfileName.Text = profile?.DisplayName ?? "Your Discord profile";
            DiscordProfileUsername.Text = profile is null ? "Your avatar appears here after sign-in." : "@" + profile.Username;
            DiscordStatus.Text = profile is null
                ? (_discord.HasCredentials ? "Application ID saved. Enable Public Client and add the exact redirect in Discord, then sign in." : "Set up a Discord application below to enable sign-in. Translation and AI work as a guest.")
                : $"Saved Discord profile on this PC. Verify sign-in to refresh your profile and avatar. Your content stays local.";
            DiscordSignInButton.IsEnabled = _operation is null && _discord.HasCredentials;
            DiscordSignInButton.Content = profile is null ? "Sign in with Discord" : "Refresh Discord profile";
            DiscordSignOutButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
            SetAccountAvatar(profile?.AvatarPath, profile?.DisplayName ?? "A");
        }
        catch (Exception ex) { DiscordStatus.Text = ex.Message; }
    }

    private void SetAccountAvatar(string? path, string name)
    {
        ImageSource? source = null;
        try
        {
            if (path is not null && File.Exists(path))
            {
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze(); source = image;
            }
        }
        catch (Exception) { }
        AccountAvatarImage.Source = DiscordAvatarImage.Source = source;
        AccountAvatarImage.Visibility = DiscordAvatarImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        AccountInitial.Text = DiscordInitial.Text = string.IsNullOrWhiteSpace(name) ? "A" : name.EnumerateRunes().First().ToString().ToUpperInvariant();
        AccountInitial.Visibility = DiscordInitial.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveDiscord_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return; }
        try { _discord.Configure(DiscordClientIdBox.Text.Trim()); UpdateAccount(); SetStatus("Application ID saved. Complete the two Discord settings above, then sign in."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void DiscordSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (!_discord.HasCredentials) { SetStatus("Save your Discord Application ID first. The setup steps are above."); return; }
        var token = BeginOperation("Complete Discord sign-in in your browser. You can cancel here."); if (token is null) return;
        try { var profile = await _discord.SignInAsync(token.Value); token.Value.ThrowIfCancellationRequested(); UpdateAccount(); SetStatus($"Welcome, {profile.DisplayName}."); }
        catch (OperationCanceledException) { SetStatus("Discord sign-in cancelled."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private async void DiscordSignOut_Click(object sender, RoutedEventArgs e)
    {
        var token = BeginOperation("Signing out…"); if (token is null) return;
        try { await _discord.SignOutAsync(token.Value); token.Value.ThrowIfCancellationRequested(); UpdateAccount(); SetStatus("Signed out. Aurora is ready in guest mode."); }
        catch (OperationCanceledException) { SetStatus("Sign-out cancelled."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private async void DownloadVoice_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceCatalogBox.SelectedItem is not VoiceDownloadOption option) return;
        var token = BeginOperation($"Downloading {option.Name}…"); if (token is null) return;
        try
        {
            var voice = await _voiceLibrary.DownloadVoiceAsync(option, new Progress<string>(p => { if (IsOperationActive(token.Value)) { VoiceLibraryStatus.Text = p; SetStatus(p); } }), token.Value);
            token.Value.ThrowIfCancellationRequested(); SelectInstalledVoice(voice);
            SetStatus($"{voice.Name} is installed and selected. It can speak {voice.Language} offline.");
        }
        catch (OperationCanceledException) { VoiceLibraryStatus.Text = "Voice download cancelled."; SetStatus("Voice download cancelled."); }
        catch (Exception ex) { VoiceLibraryStatus.Text = ex.Message; SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private async void ImportVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var modelDialog = new OpenFileDialog { Title = "Choose a Piper voice model", Filter = "Piper ONNX voice|*.onnx", CheckFileExists = true };
        if (modelDialog.ShowDialog(this) != true) return;
        var config = modelDialog.FileName + ".json";
        if (!File.Exists(config)) config = Path.ChangeExtension(modelDialog.FileName, ".json");
        if (!File.Exists(config))
        {
            var configDialog = new OpenFileDialog { Title = "Choose the matching .onnx.json voice configuration", Filter = "Piper configuration|*.json", CheckFileExists = true, InitialDirectory = Path.GetDirectoryName(modelDialog.FileName) };
            if (configDialog.ShowDialog(this) != true) return;
            config = configDialog.FileName;
        }
        var token = BeginOperation("Importing your voice model…"); if (token is null) return;
        try
        {
            var voice = await _voiceLibrary.ImportVoiceAsync(modelDialog.FileName, config, null, token.Value);
            token.Value.ThrowIfCancellationRequested(); SelectInstalledVoice(voice);
            SetStatus($"{voice.Name} imported and selected. Choose its language for AI conversation.");
        }
        catch (OperationCanceledException) { SetStatus("Voice import cancelled."); }
        catch (Exception ex) { VoiceLibraryStatus.Text = ex.Message; SetStatus(ex.Message, true); }
        finally { EndOperation(); }
    }

    private void SelectInstalledVoice(LocalVoiceInfo voice)
    {
        _settings.VoiceId = "piper:" + voice.Id;
        LoadVoices(); ScheduleSave();
        VoiceLibraryStatus.Text = $"Selected {voice.Name} · {voice.Language}. AI replies and translations will use it when the language matches.";
    }

    private void RemoveVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || ImportedVoicesBox.SelectedItem is not LocalVoiceInfo voice) return;
        try
        {
            _voiceLibrary.RemoveVoice(voice.Id);
            if (_settings.VoiceId == "piper:" + voice.Id) _settings.VoiceId = null;
            LoadVoices(); ScheduleSave(); VoiceLibraryStatus.Text = $"Removed {voice.Name}. You can download or import it again.";
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void PreviewVoice_Click(object sender, RoutedEventArgs e)
    {
        var voice = VoiceBox.SelectedItem as VoiceOption;
        var language = string.IsNullOrWhiteSpace(voice?.Language) ? Languages.Find(_settings.ConversationLanguage).Culture : voice.Language;
        var code = language.Split('-', '_')[0];
        var sample = code switch
        {
            "en" => "Hello! This is your Aurora voice.", "es" => "¡Hola! Esta es tu voz de Aurora.",
            "fr" => "Bonjour ! Voici votre voix Aurora.", "de" => "Hallo! Das ist deine Aurora Stimme.",
            "it" => "Ciao! Questa è la tua voce Aurora.", "pt" => "Olá! Esta é a sua voz Aurora.",
            "ru" => "Привет! Это ваш голос Аврора.", "zh" => "你好！这是你的语音助手。", _ => "1, 2, 3."
        };
        await SpeakTextAsync(sample, language);
    }

    private void VoiceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceCatalogBox.SelectedItem is VoiceDownloadOption voice) OpenLink(voice.InfoUrl);
    }

    public void ScrollSettingsTo(string section)
    {
        ShowPage("settings");
        var target = section switch { "account" => DiscordCard, "updates" => UpdatesCard, _ => VoiceCard };
        Dispatcher.BeginInvoke(() => target.BringIntoView(), DispatcherPriority.Loaded);
    }

    public void SetUpdateStatus(string message)
    {
        if (_closing) return;
        UpdateStatusText.Text = message;
        UpdateVersionText.Text = "v" + UpdateService.CurrentVersion;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { SetStatus("Finish or cancel the current task first."); return; }
        if (CheckUpdatesRequested is null) { SetUpdateStatus("The update check is available after application startup completes."); return; }
        CheckUpdatesButton.IsEnabled = false;
        SetUpdateStatus("Checking published GitHub releases…");
        try { await CheckUpdatesRequested(); }
        catch (Exception ex) { SetUpdateStatus("Could not check for updates: " + ex.Message); }
        finally { if (!_closing) CheckUpdatesButton.IsEnabled = true; }
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/" + _settings.UpdateRepository + "/releases");

    private void InstallOllama_Click(object sender, RoutedEventArgs e) => OpenLink("https://ollama.com/download/windows");
    private void DiscordHelp_Click(object sender, RoutedEventArgs e) => OpenLink("https://discord.com/developers/applications");
    private void WindowsSpeech_Click(object sender, RoutedEventArgs e) => OpenLink("ms-settings:speech");
    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open that page: " + ex.Message, true); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _saveTimer.Stop(); PersistSettings();
        _closing = true; _operation?.Cancel(); _voice.Stop();
        _translator.Dispose(); _chat.Dispose(); _voice.Dispose(); _voiceLibrary.Dispose(); _discord.Dispose();
        ImageBehavior.SetAnimatedSource(CustomBackgroundImage, null);
    }
}

