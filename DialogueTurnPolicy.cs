using System;
using System.Collections.Generic;
using System.Linq;

namespace NovelAIEndpoint;

internal static class DialogueTurnPolicy
{
    internal static string Build(string activeSpeaker, IEnumerable<string> conversationNpcs)
    {
        string active = string.IsNullOrWhiteSpace(activeSpeaker) ? "the active NPC" : activeSpeaker.Trim();
        string[] participants = (conversationNpcs ?? Array.Empty<string>())
            .Concat(string.IsNullOrWhiteSpace(activeSpeaker) ? Array.Empty<string>() : new[] { active })
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] others = participants.Where(name => !name.Equals(active, StringComparison.OrdinalIgnoreCase)).ToArray();
        string roster = "\nGenerate only " + active + "'s contribution to this turn.\nOther conversation participants: " +
            (others.Length == 0 ? "none established" : string.Join(", ", others) +
                (others.Length == 1 ? " (not a speaker for this generated turn)" : " (not speakers for this generated turn)")) +
            ".\nThese are the current conversation NPCs, not all nearby or known NPCs. ";
        // Demonstrate the tested structure using only actual conversation NPCs.
        // No synthetic names, genders, occupations or extra participants.
        string example = others.Length == 0
            ? "*" + active + " looks up and nods in greeting.* Hello there. "
            : "*" + active + " gestures toward " + others[0] + ", who nods in greeting.* This is " + others[0] + ". ";
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
            "turn's immediate exchange and let the game choose what happens next." +
            "\n\nEvery spoken sentence belongs to " + active + ", regardless of which character was most recently " +
            "mentioned in narration. Other participants may make brief, optional nonverbal reactions " +
            "inside the narration. Do not generate their replies, including quotations inside asterisks. " +
            "These are text-generation boundaries, not events in the story. Characters should not " +
            "interrupt, silence, or redirect one another merely to satisfy these instructions. " +
            "Consecutive actions may share one asterisk enclosure. " +
            "Example of the structure, not an event or wording to copy into this scene:\n" + example +
            "Respond naturally to the actual current request.";
    }

    internal static string EmptyRetryCue(string turnCue) =>
        "The previous response contained no usable text. Provide a nonempty reply now. " + turnCue;
}
