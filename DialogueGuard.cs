using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NovelAIEndpoint;

// Deliberately limited to structural errors and explicit actor/verb patterns.
// This is not a semantic classifier: ambiguous pronouns and ordinary mentions
// must not be rewritten into invented dialogue or different character actions.
internal static class DialogueGuard
{
    internal static string Prepare(
        string output,
        string activeSpeaker,
        IEnumerable<string> otherSpeakers,
        bool hasActionPrefill,
        out string failure,
        bool allowInvalid = false)
    {
        string prepared = PrepareCore(ref output, activeSpeaker, otherSpeakers,
            hasActionPrefill, out failure);
        // The final attempt may keep its nonempty text despite validation.
        // Preserve any harmless label/separator cleanup already performed,
        // and retain the diagnostic so the caller can log the soft failure.
        if (allowInvalid && failure.Length > 0 && failure != "empty response" &&
            !string.IsNullOrWhiteSpace(output))
            return output;
        return prepared;
    }

    private static string PrepareCore(
        ref string output,
        string activeSpeaker,
        IEnumerable<string> otherSpeakers,
        bool hasActionPrefill,
        out string failure)
    {
        failure = "";
        if (string.IsNullOrWhiteSpace(output))
        {
            failure = "empty response";
            return "";
        }

        string[] others = (otherSpeakers ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                !name.Equals(activeSpeaker, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Match label = Regex.Match(output,
            @"\A[ \t]*(?<style>\*{1,2})?(?<name>[^*:\r\n]{1,80}):");
        if (label.Success)
        {
            string name = label.Groups["name"].Value.Trim();
            if (others.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                failure = "non-active speaker label";
                return "";
            }
            if (!string.IsNullOrEmpty(activeSpeaker) &&
                name.Equals(activeSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                int end = label.Length;
                string style = label.Groups["style"].Value;
                if (style.Length > 0)
                {
                    // Only remove balanced markup around the label itself.
                    // In Kay:*Kay waves.* the first star belongs to the action.
                    if (!output.Substring(end).StartsWith(style,
                            StringComparison.Ordinal))
                    {
                        failure = "unbalanced speaker-label markup";
                        return "";
                    }
                    end += style.Length;
                }
                output = output.Substring(end).TrimStart();
            }
        }

        // Keep the existing leading separator cleanup, after recognizing a
        // styled speaker label so its closing stars are not mistaken for an
        // empty action. Never change stars around actual text.
        output = Regex.Replace(output, @"\A(?:[ \t]*\*\*[ \t]+)+", "");
        output = Regex.Replace(output, @"\A[ \t]*(?:[-\u2013\u2014][ \t]*)+", "");

        // Completion-mode dialogue can already have an opening action star in
        // its exact assistant prefill. Validate with it but do not return it;
        // Silverpine supplies that same star when displaying the continuation.
        string fullText = hasActionPrefill ? "*" + output : output;
        if (fullText.Contains(@"\*"))
            failure = "escaped action marker";
        else if (fullText.Count(character => character == '*') % 2 != 0)
            failure = "unbalanced action markers";
        if (failure.Length > 0)
            return "";

        foreach (string other in others)
        {
            // Check labels before action spans: a styled foreign label also
            // contains empty star pairs, but the actor violation is clearer.
            if (Regex.IsMatch(fullText,
                    @"(?:^|[\r\n]|[.!?]\s+|\*\s*)[ \t]*\*{0,2}" +
                    Regex.Escape(other) + @"[ \t]*:",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                failure = "non-active speaker label";
                return "";
            }
        }

        string[] segments = fullText.Split('*');
        for (int index = 1; index < segments.Length; index += 2)
        {
            string action = segments[index];
            if (string.IsNullOrWhiteSpace(action))
            {
                failure = "empty action span";
                return "";
            }

            // Quoted recollections inside narration are not a new actor turn.
            string narration = Regex.Replace(action,
                "\"[^\"\r\n]*\"|\u201c[^\u201d\r\n]*\u201d", "");
            const string clauseStart = @"(?:^|[.!?;]\s+|,\s*(?:and\s+)?|\band\s+)";
            const string adverbs = @"(?:(?:then|now|slowly|quickly|quietly|happily|finally)\s+){0,2}";
            if (Regex.IsMatch(narration,
                    clauseStart + @"you\s+" + adverbs +
                    @"(?:follow|enter|walk|nod|say|reply|answer|agree|decide|accept|step|sit|stand|smile|laugh|reach|take|open|leave|turn|start\s+walking|begin\s+walking)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                failure = "invented player action";
                return "";
            }

            foreach (string other in others)
            {
                if (Regex.IsMatch(narration,
                        clauseStart + Regex.Escape(other) + @"\s+" + adverbs +
                        @"(?:says|replies|answers|asks|nods|smiles|laughs|walks|follows|enters|leaves|opens|turns|steps|reaches|takes|decides|agrees|looks|waves|greets|whispers|shouts)\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    failure = "invented other-character action";
                    return "";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(output))
            failure = "empty response";
        return failure.Length == 0 ? output : "";
    }
}
