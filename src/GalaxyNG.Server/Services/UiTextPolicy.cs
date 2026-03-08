using System.Text.RegularExpressions;

namespace GalaxyNG.Server.Services;

internal static partial class UiTextPolicy
{
    public static string Clean(string? text, int maxLen = 1200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text.ReplaceLineEndings("\n").Trim();
        cleaned = StripXmlThinkTagsRegex().Replace(cleaned, "");   // <think>...</think>
        cleaned = StripThinkingBlocksRegex().Replace(cleaned, "");
        cleaned = StripReasoningInlineRegex().Replace(cleaned, "");
        cleaned = cleaned.Replace('\t', ' ');
        cleaned = MultiSpaceRegex().Replace(cleaned, " ");
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n");
        cleaned = cleaned.Trim();

        if (cleaned.Length > maxLen)
            cleaned = cleaned[..maxLen].Trim();

        return cleaned;
    }

    [GeneratedRegex(@"(?is)<think>.*?</think>\s*")]
    private static partial Regex StripXmlThinkTagsRegex();

    [GeneratedRegex(@"(?is)(^|\n)\s*(thinking process|analysis|reasoning)\s*:.*?(?=\n\s*(final|answer|итог|summary)\s*:|\z)")]
    private static partial Regex StripThinkingBlocksRegex();

    [GeneratedRegex(@"(?im)\b(thinking process|analysis|reasoning)\s*:.*")]
    private static partial Regex StripReasoningInlineRegex();

    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
