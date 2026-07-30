using System.Text.RegularExpressions;

namespace ParseTiger.Generation;

public sealed record ProviderRequestPreparation(
    bool ResetProjectBaselineFirst,
    string CodeChangeRequest)
{
    private static readonly Regex BaselineResetSentence = new(
        @"(?ix)
          (?:^|(?<=[.!?\r\n]))\s*
          [^.!?\r\n]{0,160}
          \b(?:reset|restore|start|return|get)\w*\b
          [^.!?\r\n]{0,100}
          \b(?:project\s+)?baseline\b
          [^.!?\r\n]{0,100}
          (?:[.!?]+|(?=\r?\n)|$)",
        RegexOptions.Compiled);

    private static readonly Regex InlineBaselineClause = new(
        @"(?ix)
          \b(?:reset|restore|start|return|get)\w*\b
          \s+(?:the\s+)?(?:project\s+)?(?:to\s+|from\s+)?
          (?:the\s+)?(?:parsetiger\s+)?baseline\b
          \s*(?:,|;|and\s+then|then|and)?\s*",
        RegexOptions.Compiled);

    public static ProviderRequestPreparation Parse(string request)
    {
        string source = request?.Trim() ?? string.Empty;
        bool mentionsBaseline = Regex.IsMatch(
            source,
            @"(?i)\b(?:project\s+|parsetiger\s+)?baseline\b");
        bool requestsReset = Regex.IsMatch(
            source,
            @"(?i)\b(?:reset|restore|start|return|get)\w*\b");
        if (!mentionsBaseline || !requestsReset)
        {
            return new ProviderRequestPreparation(false, source);
        }

        string cleaned = BaselineResetSentence.Replace(source, " ");
        cleaned = InlineBaselineClause.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '.', ',', ';', ':');
        return new ProviderRequestPreparation(true, cleaned);
    }
}
