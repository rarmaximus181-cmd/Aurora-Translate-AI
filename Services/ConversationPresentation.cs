using System.Text.RegularExpressions;

namespace AuroraTranslate.Services;

/// <summary>A small optional display joke. The model prompt, stored conversation and spoken answer remain original.</summary>
public static class ConversationPresentation
{
    private static readonly Regex DisplayPhrase = new(@"\b(this is )not (allowed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string FormatReply(string original, bool trollMode)
    {
        ArgumentNullException.ThrowIfNull(original);
        return trollMode ? DisplayPhrase.Replace(original, "$1$2") : original;
    }
}
