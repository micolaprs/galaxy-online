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
        cleaned = cleaned.Replace('\t', ' ');
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n");
        cleaned = cleaned.Trim();

        if (cleaned.Length > maxLen)
            cleaned = cleaned[..maxLen].Trim();

        return cleaned;
    }

    [GeneratedRegex(@"(?is)</?think>")]
    private static partial Regex StripXmlThinkOpenCloseRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
