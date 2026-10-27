using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NovelAIEndpoint;

internal static class EmotionMetadata
{
    // Explicit metadata only, not ordinary descriptions of feelings. Limit
    // the value to a word or a short emotion phrase so following dialogue
    // cannot be swallowed by a greedy "rest of line" replacement.
    private static readonly Regex Label = new Regex(
        @"(?<![\p{L}\p{N}_])Emotion[ \t]*:[ \t]*(?:(?<colonStars>\*{1,2})[ \t]*)?" +
        @"(?<quote>[""'])?(?<value>(?:(?:very|slightly|somewhat|physically)[ \t]+)?[\p{L}][\p{L}-]{0,39}|_+)(?(quote)\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Strip(string text) => Strip(text, out _);

    internal static string Strip(string text, out string hint)
    {
        hint = "";
        if (string.IsNullOrEmpty(text)) return text ?? "";
        MatchCollection matches = Label.Matches(text);
        if (matches.Count == 0) return text;
        string result = text;
        // Reverse order preserves source indices. The final label, rather
        // than one from earlier in a response, supplies the optional hint.
        for (int index = matches.Count - 1; index >= 0; index--)
        {
            Match match = matches[index];
            if (index == matches.Count - 1)
                hint = match.Groups["value"].Value.Trim();
            int start = match.Index;
            int end = match.Index + match.Length;
            string colonStars = match.Groups["colonStars"].Value;
            if (colonStars.Length > 0 && end + colonStars.Length <= text.Length &&
                text.Substring(end, colonStars.Length) == colonStars) end += colonStars.Length;
            int left = start;
            while (left > 0 && (text[left - 1] == ' ' || text[left - 1] == '\t')) left--;
            int right = end;
            while (right < text.Length && (text[right] == ' ' || text[right] == '\t')) right++;
            // Remove balanced wrappers around the metadata, never the star
            // that closes a real action beginning earlier in the sentence.
            if (left > 0 && right < text.Length &&
                ((text[left - 1] == '[' && text[right] == ']') ||
                 (text[left - 1] == '(' && text[right] == ')') ||
                 (text[left - 1] == '{' && text[right] == '}')))
            {
                start = left - 1;
                end = right + 1;
            }
            else
            {
                int stars = left;
                while (stars > 0 && text[stars - 1] == '*') stars--;
                int starCount = left - stars;
                if (starCount > 0 && starCount <= 2)
                {
                    // **Emotion:** happy already consumed the label's close.
                    bool closedAtColon = match.Value.IndexOf('*') >= 0;
                    bool closedAtEnd = right + starCount <= text.Length &&
                        text.Substring(right, starCount) == new string('*', starCount);
                    if (closedAtColon || closedAtEnd)
                    {
                        start = stars;
                        if (closedAtEnd) end = right + starCount;
                    }
                }
            }
            // A label's own sentence punctuation is not an empty extra sentence.
            if (end < text.Length && text[end] == '.') end++;
            while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t') &&
                (end == text.Length || "\r\n\"')]}*".IndexOf(text[end]) >= 0)) start--;
            result = result.Remove(start, end - start);
        }
        result = result.Trim();
        if (!result.Any(char.IsLetterOrDigit)) result = "";
        // Metadata is untrusted. Only a short plain phrase may enter the
        // separate expression prompt; never pass markup/instructions through.
        if (!Regex.IsMatch(hint, @"\A[A-Za-z][A-Za-z -]{0,63}\z")) hint = "";
        return result;
    }

    internal static string ExtractHint(string text, IEnumerable<string> roleBoundaries)
    {
        text = text ?? "";
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("ASSISTANT:", StringComparison.OrdinalIgnoreCase)) text = trimmed.Substring(10).TrimStart();
        int end = text.Length;
        // Newlines may separate a valid metadata annotation from its reply;
        // other-role boundaries must not let a foreign character donate a hint.
        foreach (string boundary in roleBoundaries ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(boundary)) continue;
            int index = text.IndexOf(boundary, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) end = Math.Min(end, index);
        }
        Strip(text.Substring(0, end), out string hint);
        return hint;
    }
}

// Ephemeral, per-owner and consumed once. No save serialization and no static
// dictionary retaining destroyed NPCs. Locking also protects queued calls.
internal sealed class EmotionHintCache
{
    private sealed class Entry
    {
        internal string Hint = "";
        internal DateTime CreatedUtc;
    }
    private readonly ConditionalWeakTable<object, Entry> entries = new();
    private readonly object gate = new();

    internal void Set(object owner, string hint, DateTime now)
    {
        if (owner == null) return;
        lock (gate)
        {
            entries.Remove(owner);
            if (!string.IsNullOrWhiteSpace(hint))
                entries.Add(owner, new Entry { Hint = hint, CreatedUtc = now });
        }
    }

    internal string Take(object owner, DateTime now)
    {
        if (owner == null) return "";
        lock (gate)
        {
            if (!entries.TryGetValue(owner, out var entry) || entry == null) return "";
            entries.Remove(owner);
            return now >= entry.CreatedUtc && now - entry.CreatedUtc <= TimeSpan.FromMinutes(3)
                ? entry.Hint : "";
        }
    }
}
