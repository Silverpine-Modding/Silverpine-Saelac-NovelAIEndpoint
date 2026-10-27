using System;
using System.Text.RegularExpressions;

namespace NovelAIEndpoint;

// Cosmetic cleanup only. Never classify actor behavior or guess missing
// asterisks; quality issues must not create extra generation requests.
internal static class DialogueTextCleanup
{
    internal static string Clean(string output, string activeSpeaker)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        Match label = Regex.Match(output,
            @"\A[ \t]*(?<style>\*{1,2})?(?<name>[^*:\r\n]{1,80}):");
        if (label.Success && !string.IsNullOrEmpty(activeSpeaker) &&
            label.Groups["name"].Value.Trim().Equals(activeSpeaker, StringComparison.OrdinalIgnoreCase))
        {
            int end = label.Length;
            string style = label.Groups["style"].Value;
            if (style.Length == 0)
                output = output.Substring(end).TrimStart();
            else if (output.Substring(end).StartsWith(style, StringComparison.Ordinal))
                output = output.Substring(end + style.Length).TrimStart();
            // Leave malformed label markup alone rather than guessing which
            // asterisk belongs to narration or forcing a retry.
        }
        // Consume mixed runs too, so cleanup cannot leave another removable
        // prefix to accumulate in the next turn. Single action stars survive.
        return Regex.Replace(output, @"\A[ \t]*(?:(?:\*\*[ \t]+)|(?:[-\u2013\u2014][ \t]*))+", "");
    }
}
