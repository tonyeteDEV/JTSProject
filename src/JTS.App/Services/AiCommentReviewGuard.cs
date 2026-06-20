using System.Text.RegularExpressions;

namespace JTS_App.Services;

public static partial class AiCommentReviewGuard
{
    public const string ConservativeSystemPrompt =
        "You review short work-log comments. Be extremely conservative: only correct clear typos, accents, grammar, and punctuation, and keep the comment in its original language. " +
        "Do not add facts, dates, names, blockers, next steps, task details, bullets, summaries, or assumptions that are not literally present in the original comment. " +
        "If the comment is already understandable, return it exactly as written. Return only the final comment text.";

    public static string BuildUserPrompt(string taskTitle, string? projectName, string originalComment)
    {
        return
            "Use the task only to understand vocabulary. It is not a source for adding new information.\n" +
            $"Task: {taskTitle}\n" +
            $"Project: {projectName ?? "No project"}\n\n" +
            "Original comment:\n" +
            originalComment.Trim();
    }

    public static string KeepOriginalIfUnsafe(string originalComment, string? reviewedComment)
    {
        var original = originalComment.Trim();
        var reviewed = CleanModelResponse(reviewedComment);
        if (string.IsNullOrWhiteSpace(reviewed)) return original;

        if (reviewed.Length > Math.Max(original.Length * 2, original.Length + 90))
            return original;

        if (!original.Contains('\n') && reviewed.Count(c => c == '\n') > 1)
            return original;

        if (!LooksLikeList(original) && LooksLikeList(reviewed))
            return original;

        if (!ContainsDateLikeText(original) && ContainsDateLikeText(reviewed))
            return original;

        var originalWords = MeaningfulWords(original).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedWords = MeaningfulWords(reviewed)
            .Where(word => !originalWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (addedWords > Math.Max(3, originalWords.Count / 2))
            return original;

        return reviewed;
    }

    private static string CleanModelResponse(string? response)
    {
        var text = (response ?? string.Empty).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`').Trim();
            var firstNewLine = text.IndexOf('\n');
            if (firstNewLine >= 0)
                text = text[(firstNewLine + 1)..].Trim();
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        return text;
    }

    private static bool LooksLikeList(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) > 1;
    }

    private static bool ContainsDateLikeText(string text) =>
        DateLikeRegex().IsMatch(text);

    private static IEnumerable<string> MeaningfulWords(string text) =>
        WordRegex().Matches(text)
            .Select(match => match.Value.Trim().Trim('.', ',', ';', ':', '(', ')', '[', ']', '{', '}'))
            .Where(word => word.Length >= 4);

    [GeneratedRegex(@"\b\d{1,2}[/-]\d{1,2}([/-]\d{2,4})?\b|\b\d{4}-\d{2}-\d{2}\b")]
    private static partial Regex DateLikeRegex();

    [GeneratedRegex(@"[\p{L}\p{N}_#./-]+")]
    private static partial Regex WordRegex();
}
