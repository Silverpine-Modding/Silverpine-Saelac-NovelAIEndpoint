using System;
using NovelAIEndpoint;

internal static class Program
{
    private static readonly string[] Others = { "Rosalyn", "Edgar", "Gareth" };
    private static int Passed;

    private static void Main()
    {
        // Historical validator regressions are kept for comparison only;
        // DialogueGuard is excluded from the production plugin assembly.
        // Valid initial speech must never be guessed to be narration.
        Accept("Hello there. *Kay smiles.*", "Hello there. *Kay smiles.*");
        Accept("*Kay smiles.* Hello there.", "*Kay smiles.* Hello there.");
        Accept("Kay:*Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("Kay: *Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("*Kay:* *Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("**Kay:** *Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("Kay: ** ** *Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("Kay: - - - *Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Accept("Kay: - Hello!", "Hello!");
        Accept("Rosalyn's shop is across the road.", "Rosalyn's shop is across the road.");
        Accept("Rosalyn says hello whenever I visit.", "Rosalyn says hello whenever I visit.");
        Accept("*Kay points at Rosalyn's shop.* Shall we go?",
            "*Kay points at Rosalyn's shop.* Shall we go?");
        Accept("*Kay waves to Rosalyn.*", "*Kay waves to Rosalyn.*");
        Accept("*Kay shows you the sign. You see a flower on it.*",
            "*Kay shows you the sign. You see a flower on it.*");
        Accept("You follow the road north to find her.", "You follow the road north to find her.");
        Accept("*Kay wonders if you follow their meaning.*",
            "*Kay wonders if you follow their meaning.*");
        Accept("*Kay recalls the phrase \"you follow them\".*",
            "*Kay recalls the phrase \"you follow them\".*");
        Accept("*Kay smiles.* *They point across the road.* Shall we?",
            "*Kay smiles.* *They point across the road.* Shall we?");
        Accept("Day to you. *Kay turns away.*", "Day to you. *Kay turns away.*");

        Reject("Hello! *Kay waves.", "unbalanced action markers");
        Reject("Kay smiles.* Hello!", "unbalanced action markers");
        Reject("*Kay smiles.* Hello! *", "unbalanced action markers");
        Reject(@"*Kay smiles.\* Hello!", "escaped action marker");
        Reject("*Kay smiles.* Hello!**", "empty action span");
        Reject("* * Hello!", "empty action span");
        Reject("**Kay: *Kay smiles.* Hello!", "unbalanced speaker-label markup");
        Reject("Rosalyn: Hello!", "non-active speaker label");
        Reject("Hello! **Rosalyn:** Who's this?", "non-active speaker label");
        Reject("*Kay waves.*\nRosalyn : Hello!", "non-active speaker label");
        Reject("*Kay starts walking, and you follow them.*", "invented player action");
        Reject("*You enter the shop.*", "invented player action");
        Reject("*Kay points. You then nod.*", "invented player action");
        Reject("*Rosalyn looks up and smiles.* Hello!", "invented other-character action");
        Reject("*Kay waves, and Rosalyn replies.*", "invented other-character action");
        Reject("*Edgar follows Kay.*", "invented other-character action");
        Reject("", "empty response");
        Reject("Kay:", "empty response");

        // Exact continuation mode owns its opening star in the assistant prefill.
        Accept("Kay smiles.* Hello!", "Kay smiles.* Hello!", true);
        Reject("Kay smiles. Hello!", "unbalanced action markers", true);

        const string reported = @"Kay: *Kay smiles, and nods. Great! Come on, then. Rosalyn's shop is just across the road. She's the herbalist in town, and a good friend of mine. She'll be happy to meet you. They start walking, and you follow them. They lead you to a wooden building with a sign that says* Rosalyn's Herbs and Remedies. They open the door, and a bell rings. Inside, you see a cozy room with shelves full of jars, bottles, and bundles of dried plants. There's a counter with a register, and a door that leads to the back. A female fox with red fur and green eyes is standing behind the counter, arranging some flowers in a vase. She looks up as you enter, and smiles.\* Oh, Kay! Hello! And who is this? *She looks at you with curiosity and kindness.*\*";
        Reject(reported, "escaped action marker");
        Reject(reported.Replace(@"\*", "*"), "invented player action");
        // Fixing the punctuation alone cannot make a multi-character turn safe.
        Reject("*Kay smiles.* Great! Come on, then. *They start walking, and you follow them.*",
            "invented player action");
        Accept("*Kay smiles and points across the road.* Rosalyn's shop is over there. Would you like to meet her?",
            "*Kay smiles and points across the road.* Rosalyn's shop is over there. Would you like to meet her?");

        // First-attempt checks above remain strict. Only the final response
        // gets the permissive policy, retaining the reason for its warning.
        FinalAttempt("Kay: - Hello! *Kay waves.", "Hello! *Kay waves.",
            "unbalanced action markers");
        FinalAttempt(@"Kay: *Kay smiles.\* Hello!", @"*Kay smiles.\* Hello!",
            "escaped action marker");
        FinalAttempt("*You follow Kay.*", "*You follow Kay.*", "invented player action");
        FinalAttempt("*Rosalyn smiles.*", "*Rosalyn smiles.*", "invented other-character action");
        FinalAttempt("Rosalyn: Hello!", "Rosalyn: Hello!", "non-active speaker label");
        FinalAttempt("**Kay: Hello!", "**Kay: Hello!", "unbalanced speaker-label markup");
        FinalAttempt("Hello!**", "Hello!**", "empty action span");
        FinalAttempt("Kay smiles. Hello!", "Kay smiles. Hello!", "unbalanced action markers", true);
        FinalAttempt("Kay smiles.* Hello!", "Kay smiles.* Hello!", "", true);
        FinalAttempt("Kay: *Kay waves.* Hello!", "*Kay waves.* Hello!", "");
        // No fallback to the first attempt or to role-boundary-only text:
        // this API receives the current response after local stops.
        FinalAttempt("", "", "empty response");
        FinalAttempt(" \t", "", "empty response");
        FinalAttempt("Kay:", "", "empty response");
        FinalAttempt("Kay: - - ", "", "empty response");

        Metadata("Hello!\n  Emotion: happy", "Hello!", "happy");
        Metadata("Hello! Emotion: thinking", "Hello!", "thinking");
        Metadata("Emotion: happy", "", "happy");
        Metadata("Emotion: ___", "", "");
        Metadata("Hello. [Emotion: worried]", "Hello.", "worried");
        Metadata("Hello. (Emotion: neutral)", "Hello.", "neutral");
        Metadata("Hello. *Emotion: happy*", "Hello.", "happy");
        Metadata("Hello. **Emotion:** happy", "Hello.", "happy");
        Metadata("Hello. **Emotion: happy**", "Hello.", "happy");
        Metadata("*Kay smiles. Emotion: happy* Hello!", "*Kay smiles.* Hello!", "happy");
        Metadata("Hello! Emotion: happy. See you later.", "Hello!  See you later.", "happy");
        Metadata("After hearing the rumor, Kay said: \"Wonderful! Emotion: happy\".",
            "After hearing the rumor, Kay said: \"Wonderful!\".", "happy");
        Metadata("*Kay smiles.* I feel happy.", "*Kay smiles.* I feel happy.", "");
        Metadata("Emotion: physically pleasured", "", "physically pleasured");
        Metadata("Emotion: very worried", "", "very worried");
        Metadata("First. Emotion: sad\nSecond. Emotion: happy", "First.\nSecond.", "happy");
        Metadata("{\"emotion\":\"happy\"}", "{\"emotion\":\"happy\"}", "");
        Metadata("Emotion: happy.", "", "happy");
        Metadata("Hello. Emotion: \"happy\"", "Hello.", "happy");
        Metadata("Hello. Emotion: 'worried'", "Hello.", "worried");
        Metadata("Hello. Emotion: **happy**", "Hello.", "happy");
        Metadata("Hello. Emotion: *happy*", "Hello.", "happy");
        CacheResult(EmotionMetadata.ExtractHint("Hello.\nEmotion: happy", new[] { "\n", "Mira:" }), "happy");
        CacheResult(EmotionMetadata.ExtractHint("Hello.\nMira: Bye.\nEmotion: sad", new[] { "\n", "Mira:" }), "");
        CacheResult(EmotionMetadata.ExtractHint("Hello.\nEmotion: happy\nMira: Bye.\nEmotion: sad", new[] { "\n", "Mira:" }), "happy");
        CacheResult(EmotionMetadata.ExtractHint("ASSISTANT: Hello.\nEmotion: happy", new[] { "\n", "ASSISTANT:" }), "happy");

        var cache = new EmotionHintCache();
        var kay = new object();
        var mira = new object();
        DateTime now = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);
        cache.Set(kay, "happy", now);
        CacheResult(cache.Take(mira, now), "");
        CacheResult(cache.Take(kay, now), "happy");
        CacheResult(cache.Take(kay, now), "");
        cache.Set(kay, "sad", now);
        cache.Set(kay, "", now);
        CacheResult(cache.Take(kay, now), "");
        cache.Set(kay, "sad", now);
        cache.Set(kay, "happy", now);
        CacheResult(cache.Take(kay, now), "happy");
        cache.Set(kay, "happy", now);
        CacheResult(cache.Take(kay, now.AddMinutes(4)), "");

        // Production no longer runs the legacy actor/formatting classifier.
        // Every nonempty reply passes cosmetic cleanup without semantic tests.
        Clean("Kay: *Kay smiles.* *Mira nods back.*", "*Kay smiles.* *Mira nods back.*");
        Clean("*Mira dips her head in greeting.*", "*Mira dips her head in greeting.*");
        Clean("*A visitor arrives.*", "*A visitor arrives.*");
        Clean("*You follow Kay.*", "*You follow Kay.*");
        Clean("Hello! *Kay smiles.", "Hello! *Kay smiles.");
        Clean("Kay smiles.* Hello!", "Kay smiles.* Hello!");
        Clean("Kay:*Kay smiles.* Hello!", "*Kay smiles.* Hello!");
        Clean("Kay: - - ** ** *Kay smiles.*", "*Kay smiles.*");
        Clean("Kay: ** ** *Kay smiles.*", "*Kay smiles.*");
        Clean("Kay: ** - ** — *Kay smiles.*", "*Kay smiles.*");
        Clean("*Kay pauses—then smiles.* Hello!", "*Kay pauses—then smiles.* Hello!");
        Clean("Hello there.", "Hello there.");
        Clean("Kay: - - ", "");
        Clean("**Kay:** *Kay smiles.*", "*Kay smiles.*");
        Clean("**Kay: *Kay smiles.*", "**Kay: *Kay smiles.*");
        Clean("Kay: Emotion: happy", "Emotion: happy"); // Metadata handled separately.
        Clean("", "");
        Clean("Kay:", "");
        Clean("*Kay waits for your reply. Mira glances between you and Kay.*",
            "*Kay waits for your reply. Mira glances between you and Kay.*");
        Clean("*Kay looks up. Mira nods in response.* Hello. *Kay waits. Mira glances at the door.*",
            "*Kay looks up. Mira nods in response.* Hello. *Kay waits. Mira glances at the door.*");

        string groupPolicy = DialogueTurnPolicy.Build("Kay", new[] { "Kay", "Mira", " mira ", "" });
        Check(groupPolicy.Contains("Generate only Kay's contribution to this turn."));
        Check(groupPolicy.Contains("Other conversation participants: Mira (not a speaker for this generated turn)."));
        Check(groupPolicy.Contains("Other listed conversation NPCs may only make brief, observable, nonverbal reactions"));
        Check(groupPolicy.Contains("Only Kay may speak in this response."));
        Check(groupPolicy.Contains("Every spoken word must belong to Kay."));
        Check(groupPolicy.Contains("they do not need a separate pair from adjacent actions."));
        Check(groupPolicy.Contains("Do not write, quote, or paraphrase another NPC's speech, even inside asterisks or narration."));
        Check(groupPolicy.Contains("Keep only Kay's spoken words outside narration spans."));
        Check(groupPolicy.Contains("Consecutive actions and permitted reactions may share one pair across sentences or a change of actor."));
        Check(groupPolicy.Contains("Do not close and reopen it between consecutive actions."));
        Check(groupPolicy.Contains("Close the span before spoken dialogue or the end of the response; open a new span if narration resumes."));
        Check(!groupPolicy.Contains("Enclose each entire reaction"));
        Check(!groupPolicy.Contains("Torren"));
        Check(groupPolicy.Contains("unlisted character react"));
        Check(DialogueTurnPolicy.Build("Kay", null).Contains("Other conversation participants: none established."));
        Check(DialogueTurnPolicy.Build("Kay", null).Contains("Only Kay may speak in this response."));
        Check(DialogueTurnPolicy.EmptyRetryCue(groupPolicy).Contains(groupPolicy));
        Check(groupPolicy.Contains("Every spoken sentence belongs to Kay, regardless of which character was most recently mentioned in narration."));
        Check(groupPolicy.Contains("brief, optional nonverbal reactions"));
        Check(groupPolicy.Contains("Do not generate their replies, including quotations inside asterisks."));
        Check(groupPolicy.Contains("These are text-generation boundaries, not events in the story."));
        Check(groupPolicy.Contains("Characters should not interrupt, silence, or redirect one another merely to satisfy these instructions."));
        Check(groupPolicy.Contains("*Kay gestures toward Mira, who nods in greeting.* This is Mira."));
        Check(!groupPolicy.Contains("Elian") && !groupPolicy.Contains("shop") && !groupPolicy.Contains("Silent participants"));
        string soloPolicy = DialogueTurnPolicy.Build("Kay", new[] { "kay", " Kay ", "" });
        Check(soloPolicy.Contains("Other conversation participants: none established."));
        Check(soloPolicy.Contains("*Kay looks up and nods in greeting.* Hello there."));
        Check(!soloPolicy.Contains("Mira") && !soloPolicy.Contains("gestures toward"));
        string multiPolicy = DialogueTurnPolicy.Build("Kay", new[] { "Kay", "Mira", "Gareth", "mira" });
        Check(multiPolicy.Contains("Other conversation participants: Mira, Gareth (not speakers for this generated turn)."));
        Check(!multiPolicy.Contains("Other conversation participants: Kay"));
        Check(DialogueTurnPolicy.Build("", null).Contains("Generate only the active NPC's contribution to this turn."));
        Console.WriteLine($"Passed {Passed} dialogue/metadata/policy regression checks.");
    }

    private static void Accept(string input, string expected, bool prefill = false)
    {
        string output = DialogueGuard.Prepare(input, "Kay", Others, prefill, out string failure);
        if (output != expected || failure.Length != 0)
            throw new Exception($"Valid dialogue check {Passed + 1} failed: {failure}");
        Passed++;
    }

    private static void Reject(string input, string expectedReason, bool prefill = false)
    {
        string output = DialogueGuard.Prepare(input, "Kay", Others, prefill, out string failure);
        if (output.Length != 0 || failure != expectedReason)
            throw new Exception($"Invalid dialogue check {Passed + 1} failed: {failure}");
        Passed++;
    }

    private static void FinalAttempt(string input, string expected, string expectedReason,
        bool prefill = false)
    {
        string output = DialogueGuard.Prepare(input, "Kay", Others, prefill,
            out string failure, allowInvalid: true);
        if (output != expected || failure != expectedReason)
            throw new Exception($"Final-attempt check {Passed + 1} failed: {failure}");
        Passed++;
    }

    private static void Metadata(string input, string expected, string expectedHint)
    {
        string output = EmotionMetadata.Strip(input, out string hint);
        if (output != expected || hint != expectedHint)
            throw new Exception($"Metadata check {Passed + 1} failed: output={output}, hint={hint}");
        Passed++;
    }

    private static void CacheResult(string actual, string expected)
    {
        if (actual != expected) throw new Exception($"Hint-cache check {Passed + 1} failed.");
        Passed++;
    }

    private static void Clean(string input, string expected) => Check(DialogueTextCleanup.Clean(input, "Kay") == expected);

    private static void Check(bool result)
    {
        if (!result) throw new Exception($"Cleanup/policy check {Passed + 1} failed.");
        Passed++;
    }
}
