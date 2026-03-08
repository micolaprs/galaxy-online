using System.Text.RegularExpressions;

namespace GalaxyNG.Server.Services;

internal static partial class UiTextPolicy
{
    public static string Clean(string? text, int maxLen = 1200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text.ReplaceLineEndings("\n").Trim();
        cleaned = StripXmlThinkOpenCloseRegex().Replace(cleaned, ""); // remove only tags, keep content
        cleaned = StripMarkdownBoldRegex().Replace(cleaned, "$1");
        cleaned = StripMarkdownUnderlineRegex().Replace(cleaned, "$1");
        cleaned = StripMarkdownInlineCodeRegex().Replace(cleaned, "$1");
        cleaned = cleaned.Replace('\t', ' ');
        cleaned = MissingSpaceAfterSentenceRegex().Replace(cleaned, "$1 $2");
        cleaned = MissingSpaceBetweenLetterDigitRegex().Replace(cleaned, "$1 $2");
        cleaned = MissingSpaceBetweenDigitLetterRegex().Replace(cleaned, "$1 $2");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ");
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n");
        cleaned = cleaned.Trim();

        if (cleaned.Length > maxLen)
            cleaned = cleaned[..maxLen].Trim();

        return cleaned;
    }

    [GeneratedRegex(@"(?is)</?think>")]
    private static partial Regex StripXmlThinkOpenCloseRegex();

    [GeneratedRegex(@"\*\*([^*\n]+)\*\*")]
    private static partial Regex StripMarkdownBoldRegex();

    [GeneratedRegex(@"__([^_\n]+)__")]
    private static partial Regex StripMarkdownUnderlineRegex();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex StripMarkdownInlineCodeRegex();

    [GeneratedRegex(@"([.!?])([A-Za-zА-Яа-яЁё])")]
    private static partial Regex MissingSpaceAfterSentenceRegex();

    [GeneratedRegex(@"([A-Za-zА-Яа-яЁё])(\d)")]
    private static partial Regex MissingSpaceBetweenLetterDigitRegex();

    [GeneratedRegex(@"(\d)([A-Za-zА-Яа-яЁё])")]
    private static partial Regex MissingSpaceBetweenDigitLetterRegex();

    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
