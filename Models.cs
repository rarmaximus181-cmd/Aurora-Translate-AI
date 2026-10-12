namespace AuroraTranslate;

public record LanguageOption(string Code, string Name, string Culture)
{
    public override string ToString() => Name;
}
public record VoiceOption(string Id, string Name, string Language)
{
    public override string ToString() => Name;
}
public record ChatMessage(string Role, string Content);
public record ChatOptions(string ResponseStyle = "balanced", bool ReasoningEnabled = false);
public record TranslationResult(string Text, string EngineDisplay);
public record TranslationHistory(string SourceText, string TranslatedText, string SourceCode, string TargetCode, DateTimeOffset CreatedAt);
public record GoogleProfile(string Id, string Name, string Email);
public record DiscordProfile(string Id, string Username, string DisplayName, string AvatarUrl, string? AvatarPath = null);
public record LocalVoiceInfo(string Id, string Name, string Language, string ModelPath, string ConfigPath)
{
    public override string ToString() => Name;
}
public record VoiceDownloadOption(string Id, string Name, string Language, string SizeLabel, string ModelUrl, string ConfigUrl, string InfoUrl)
{
    public override string ToString() => $"{Name} · {Language} · {SizeLabel}";
}

public class AppSettings
{
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "es";
    public string Theme { get; set; } = "Aurora";
    public string? CustomBackground { get; set; }
    public bool AnimateTheme { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 0.4;
    public string TranslationEngine { get; set; } = "online";
    public string OllamaModel { get; set; } = "qwen3.5:4b";
    public string? VoiceId { get; set; }
    public int SpeechRate { get; set; } = 0;
    public bool SpeakTranslations { get; set; } = false;
    public bool SpeakChatReplies { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public string ConversationLanguage { get; set; } = "en";
    public string ResponseStyle { get; set; } = "balanced";
    public bool ReasoningEnabled { get; set; } = true;
    public bool TrollMode { get; set; } = false;
    public string UpdateRepository { get; set; } = "rarmaximus181-cmd/Aurora-Translate-AI";
}

public static class Languages
{
    public static readonly IReadOnlyList<LanguageOption> All = new List<LanguageOption>
    {
        new("en", "English", "en-US"), new("es", "Spanish", "es-ES"), new("fr", "French", "fr-FR"),
        new("de", "German", "de-DE"), new("it", "Italian", "it-IT"), new("pt", "Portuguese", "pt-BR"),
        new("ja", "Japanese", "ja-JP"), new("ko", "Korean", "ko-KR"), new("zh-CN", "Chinese (Simplified)", "zh-CN"),
        new("zh-TW", "Chinese (Traditional)", "zh-TW"), new("ar", "Arabic", "ar-SA"), new("hi", "Hindi", "hi-IN"),
        new("ru", "Russian", "ru-RU"), new("uk", "Ukrainian", "uk-UA"), new("nl", "Dutch", "nl-NL"),
        new("pl", "Polish", "pl-PL"), new("tr", "Turkish", "tr-TR"), new("sv", "Swedish", "sv-SE"),
        new("no", "Norwegian", "nb-NO"), new("da", "Danish", "da-DK"), new("fi", "Finnish", "fi-FI"),
        new("el", "Greek", "el-GR"), new("cs", "Czech", "cs-CZ"), new("ro", "Romanian", "ro-RO"),
        new("hu", "Hungarian", "hu-HU"), new("he", "Hebrew", "he-IL"), new("id", "Indonesian", "id-ID"),
        new("vi", "Vietnamese", "vi-VN"), new("th", "Thai", "th-TH"), new("ms", "Malay", "ms-MY"),
        new("bn", "Bengali", "bn-BD"), new("ta", "Tamil", "ta-IN"), new("te", "Telugu", "te-IN"),
        new("ur", "Urdu", "ur-PK"), new("fa", "Persian", "fa-IR"), new("sw", "Swahili", "sw-KE"),
        new("af", "Afrikaans", "af-ZA"), new("bg", "Bulgarian", "bg-BG"), new("hr", "Croatian", "hr-HR"),
        new("sk", "Slovak", "sk-SK"), new("sl", "Slovenian", "sl-SI"), new("sr", "Serbian", "sr-RS"),
        new("lt", "Lithuanian", "lt-LT"), new("lv", "Latvian", "lv-LV"), new("et", "Estonian", "et-EE"),
        new("fil", "Filipino", "fil-PH"), new("is", "Icelandic", "is-IS"), new("ga", "Irish", "ga-IE"),
        new("cy", "Welsh", "cy-GB"), new("mt", "Maltese", "mt-MT")
    };
    public static LanguageOption Find(string code) => All.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
