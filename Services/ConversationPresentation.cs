using System.Text.RegularExpressions;

namespace AuroraTranslate.Services;

/// <summary>A small optional display joke. The model prompt, stored conversation and spoken answer remain original.</summary>
public static class ConversationPresentation
{
    private const RegexOptions QuestionOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex DisplayPhrase = new(@"\b(this is )not (allowed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex QuestionClauses = new(@"[^.!?\r\n]+(?:[.!?]|$)", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EbikeReference = new(@"\b(?:e[\s\-\u2010-\u2015]?bikes?|electric\s+bikes?)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex NonElectricReference = new(@"\bnon[\s\-\u2010-\u2015]+(?:electric\s+bikes?|e[\s\-\u2010-\u2015]?bikes?)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RoadUse = new(@"\b(?:on|in)\s+(?:(?:a|an|the|public|local|city|country|normal|regular)\s+){0,3}(?:roads?|streets?)\b|\b(?:street|road)[\s-]+legal\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Permission = new(@"\b(?:allowed|legal|illegal|permitted|permissible|authorized)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex QuestionLead = new(@"^\s*(?:is|are|am|do|does|may|can|would|should)\b|\b(?:tell\s+me|do\s+you\s+know)\s+(?:if|whether)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RideQuestion = new(@"\b(?:can|may)\s+(?:i|we|you|someone|one)\s+(?:legally\s+)?(?:ride|use|cycle|take|drive)\b|\bcan\s+(?:(?:an?|the)\s+)?(?:e[\s\-\u2010-\u2015]?bikes?|electric\s+bikes?)\s+(?:be\s+)?(?:ridden|used|driven)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PerformanceTopic = new(@"\b(?:range|battery|batteries|charging|chargers?|efficiency|comfort)\b", QuestionOptions, TimeSpan.FromMilliseconds(100));

    public static string FormatReply(string original, bool trollMode, string? userQuestion = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!trollMode) return original;
        if (IsEbikeRoadPermissionQuestion(userQuestion)) return "Yes.";
        return DisplayPhrase.Replace(original, "$1$2");
    }

    /// <summary>Recognizes a narrow English question for the visibly marked display joke, not for legal advice.</summary>
    public static bool IsEbikeRoadPermissionQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > 4000) return false;
        foreach (Match match in QuestionClauses.Matches(question))
        {
            var clause = match.Value.Trim();
            if (!EbikeReference.IsMatch(clause) || NonElectricReference.IsMatch(clause) || !RoadUse.IsMatch(clause)) continue;
            if (Permission.IsMatch(clause) && (QuestionLead.IsMatch(clause) || clause.EndsWith('?'))) return true;
            if (RideQuestion.IsMatch(clause) && !PerformanceTopic.IsMatch(clause)) return true;
        }
        return false;
    }
}
