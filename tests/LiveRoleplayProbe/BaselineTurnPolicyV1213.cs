using System;
using System.Collections.Generic;
using System.Linq;

namespace NovelAIEndpoint;

// Frozen test-only turn cue; comparison overlays must not be applied twice
// after promotion into production.
internal static class BaselineTurnPolicyV1213
{
    internal static string Build(string activeSpeaker, IEnumerable<string> conversationNpcs)
    {
        string active = string.IsNullOrWhiteSpace(activeSpeaker) ? "the active NPC" : activeSpeaker.Trim();
        string[] participants = (conversationNpcs ?? Array.Empty<string>())
            .Concat(string.IsNullOrWhiteSpace(activeSpeaker) ? Array.Empty<string>() : new[] { active })
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string roster = participants.Length == 0
            ? "Use only NPCs explicitly established as participants in the current conversation. "
            : "NPCs taking part in this conversation: " + string.Join(", ", participants) + ". " +
              "This is the current conversation roster, not a list of all nearby or known NPCs. ";
        return "CURRENT TURN: Write " + active + "'s turn, without a speaker label. " + roster +
            "Keep the response centered on " + active + ". Only " + active + " may speak in this response. " +
            "Every spoken word must belong to " + active + ". Other listed conversation NPCs may only " +
            "make brief, observable, nonverbal reactions to the current words or actions, such as a nod " +
            "or glance. Keep these reactions inside the narration's paired single asterisks; " +
            "they do not need a separate pair from adjacent actions. Do not write, " +
            "quote, or paraphrase another NPC's speech, even inside asterisks or narration. " +
            "Do not expand a reaction into that NPC's independent turn, private thoughts, or major decisions. " +
            "An NPC appearing only in background knowledge, memories, relationships, or a location description " +
            "is not thereby a conversation participant. Do not invent an absent NPC's arrival or make an " +
            "unlisted character react. Leave an unidentified visitor's identity unknown until the game " +
            "establishes it; your NPC can ask who is there. Do not decide the player's actions, answers, " +
            "or acceptance of an invitation. Keep invitations open for the player to respond. " +
            "Write one paragraph. Open a single-asterisk narration span before an opening action. " +
            "Consecutive actions and permitted reactions may share one pair across sentences or a change " +
            "of actor. Do not close and reopen it between consecutive actions. Close the span before " +
            "spoken dialogue or the end of the response; open a new span if narration resumes. " +
            "Keep only " + active + "'s spoken words outside narration spans. End after this " +
            "turn's immediate exchange and let the game choose what happens next.";
    }
}
