using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovelAIEndpoint;

internal static class Program
{
    private const string Endpoint = "https://text.novelai.net/oa/v1/chat/completions";
    private const string Active = "Elian";
    private static readonly string[] Others = ["Robin", "Mira", "Torren", "Bessa", "Lio", "Neris", "Venn", "Ada"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // Experimental only. Not installed in Plugin.cs by this test harness.
    private const string Reinforcement = """
        SCENE FACTS: The supplied current location and visible/present character list are authoritative. Background profiles, friendships, rumors, and memories do not establish somebody's presence. Do not invent a visitor, an arrival, a passerby, a new NPC, or a distant NPC's current actions. If a knock or unseen person has no supplied identity, leave that identity unknown. You may have your active NPC ask who is there or open the door, but do not invent who or what is revealed or how anyone outside reacts.
        SINGLE-TURN LIMIT: Write only the active NPC's immediate actions and spoken words. This applies equally to names, pronouns, descriptions such as 'the fox', and unnamed figures. End after your own question or introduction; the game will supply the other character's response. Never complete both sides of a conversation. Do not turn an invitation into an accepted invitation or a completed journey.
        ESTABLISHED HISTORY: Do not claim that the player told stories, agreed to anything, shared a secret, or did something earlier unless the supplied history establishes it. Do not invent new local institutions or facts to answer a question; use what is known or express uncertainty in character.
        ACTION FORMATTING CHECK: Before sending, silently inspect the entire reply, especially its first sentence. Third-person descriptions of the NPC's movements, gaze or body language are actions, not spoken words, and must be enclosed in paired single asterisks. Spoken sentences stay outside. Correct opening: *Elian looks up from his map.* Hello there. Correct introduction: *Elian gestures toward Mira.* Robin, this is Mira. Stop there and let the game give Mira her turn. Do not print this checklist or a compliance explanation.
        """;

    private const string Knowledge = """
        You're writing dialog for a medieval furry fantasy game. The character you're currently writing the dialog of is Elian.
        Knowledge Base:

        Willowford: A small riverside village in the kingdom of Avelin. A bridge connects the market and residential streets.
        Currency: People use copper and silver coins.
        Elian's work: Elian repairs maps and delivers letters. He is not a herbalist or blacksmith.
        Herb Shop: Mira owns the herb shop across the road from Elian's map shop. Elian is welcome to visit, but she is not always at work.
        Old Mill: The old mill is closed for repairs. Elian has not learned who is repairing it.

        These are other characters living in Willowford:

        Name: Mira
        Character description: Mira is an adult female anthro red fox, five feet ten inches tall, with amber eyes and a cream-colored muzzle. She wears a green apron and works as a herbalist. Personality: patient, practical, welcoming.
        Friend of Elian. Elian knows her by name, but does not know her current thoughts or actions when she is absent.

        Name: Torren
        Character description: Torren is an adult male anthro gray wolf with yellow eyes, broad shoulders, and a leather blacksmith's apron. He works at the forge. Personality: reserved, methodical, dry humor.
        Acquaintance of Elian. Torren is not a bird and has no wings or talons.

        Name: Bessa
        Character description: Bessa is an adult female anthro brown rabbit, a baker with long ears and a flour-dusted blue dress. Personality: energetic, friendly, forgetful.
        Frequent customer of Elian.

        Lio: Adult male otter, ferryman, friendly acquaintance.
        Neris: Adult female deer, carpenter, business acquaintance.
        Venn: Adult male badger, librarian, friend.
        Ada: Adult female hare, town guard, acquaintance.

        A character being listed above does not establish that they are present at the current location.
        Begin dialog.
        """;

    private sealed record Scenario(string Id, string Purpose, string Location,
        string Visible, string Memory, string History, string PlayerLine);
    private sealed record Attempt(int Number, string FinishReason, double Seconds,
        string RawText, string AfterLocalStops, string EmptyResponseFailure,
        string ReturnedText, string EmotionHint, List<Dictionary<string, string>> Messages);
    private sealed record CaseResult(string Id, string Purpose, string Variant, int Repetition, List<Attempt> Attempts);
    private sealed record Trial(Scenario Scenario, string Variant, int Repetition);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] is not ("reinforced" or "speaker-anchor-ab" or "generation-boundary-ab")))
        {
            Console.Error.WriteLine("Supply the Silverpine workspace directory. No credentials belong on the command line.");
            return 2;
        }
        try { return await Run(Path.GetFullPath(args[0]), args.Length == 2 ? args[1] : "current"); }
        catch (Exception ex)
        {
            // Do not emit exception bodies, HTTP bodies, headers or configuration.
            Console.Error.WriteLine("Probe stopped: " + ex.GetType().Name + ". No credential or server error body was logged.");
            return 1;
        }
    }

    private static async Task<int> Run(string workspace, string mode)
    {
        bool reinforced = mode == "reinforced";
        bool speakerAnchorComparison = mode == "speaker-anchor-ab";
        bool generationBoundaryComparison = mode == "generation-boundary-ab";
        bool comparisonRun = speakerAnchorComparison || generationBoundaryComparison;
        string candidateVariant = generationBoundaryComparison ? "generation-boundary" : "speaker-anchor";
        string comparisonDescription = generationBoundaryComparison
            ? "Frozen 1.2.13 turn cue versus the historical generation-boundary candidate: other present NPCs are not speakers for this generated turn; speaker identity does not follow the latest narrated actor; optional nonverbal reactions, no quoted replies, no acting out enforcement, and a simple example without physical re-anchoring."
            : "Frozen 1.2.13 turn cue versus the historical combined speaking/silent roster and narration re-anchoring example.";
        string sourcePath = Path.Combine(workspace, "Plugin Development", "NovelAIEndpoint", "Plugin.cs");
        string source = File.ReadAllText(sourcePath);
        string version = Regex.Match(source, "PluginVersion = \"([^\"]+)\"").Groups[1].Value;
        string compatibility = Expand(Between(source, "private const string CompatibilityInstructions =", "private const string UtilityInstructions ="));
        if (!compatibility.StartsWith("Every named character") || string.IsNullOrEmpty(version))
            throw new InvalidOperationException("Prompt-source extraction check failed.");

        Scenario[] scenarios = reinforced ? Scenarios().Where(s => s.Id != "08_emotion_memory").ToArray() : Scenarios();
        var trials = new List<Trial>();
        if (comparisonRun)
        {
            Scenario[] groupScenarios = scenarios.Where(s => IsGroupScenario(s.Id)).ToArray();
            for (int repetition = 1; repetition <= 3; repetition++)
                for (int index = 0; index < groupScenarios.Length; index++)
                {
                    // Interleave arms and alternate order; independent histories,
                    // unchanged sampling, no response-quality rejection/retries.
                    string[] variants = (repetition + index) % 2 == 0
                        ? ["baseline-1.2.13", candidateVariant] : [candidateVariant, "baseline-1.2.13"];
                    foreach (string variant in variants)
                        trials.Add(new Trial(groupScenarios[index], variant, repetition));
                }
            if (trials.Count != 18) throw new InvalidOperationException("Unexpected comparison plan.");
            string currentCue = BaselineTurnPolicyV1213.Build(Active, [Active, "Mira"]);
            _ = generationBoundaryComparison ? BuildGenerationBoundaryCue(currentCue) : BuildSpeakerAnchorCue(currentCue);
        }
        else
            trials.AddRange(scenarios.Select(s => new Trial(s, mode, 1)));
        int maximumCalls = trials.Count * 2;
        string outputDirectory = Path.Combine(workspace, "artifacts", "NovelAI-Roleplay-Probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + (mode == "current" ? "" : "-" + mode));
        Directory.CreateDirectory(outputDirectory);
        var results = new List<CaseResult>();
        DateTime startedUtc = DateTime.UtcNow;
        int networkCalls = 0;
        string sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        string? error = null;

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        string configPath = Path.Combine(workspace, "BepInEx", "config", "com.silverpine.novelai-endpoint.cfg");
        string encrypted = File.ReadLines(configPath).First(line => line.StartsWith("Encrypted API Key =", StringComparison.Ordinal))
            .Split('=', 2)[1].Trim();
        byte[] tokenBytes = Convert.FromBase64String(encrypted);
        for (int index = 0; index < tokenBytes.Length; index++) tokenBytes[index] = (byte)~tokenBytes[index];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.ASCII.GetString(tokenBytes).Trim());
        CryptographicOperations.ZeroMemory(tokenBytes);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            foreach (Trial trial in trials)
            {
                Scenario scenario = trial.Scenario;
                string trialId = scenario.Id + "/" + trial.Variant + "/r" + trial.Repetition;
                string[] participants = IsGroupScenario(scenario.Id)
                    ? [Active, "Mira"] : [Active];
                string turnCue = comparisonRun
                    ? BaselineTurnPolicyV1213.Build(Active, participants)
                    : DialogueTurnPolicy.Build(Active, participants);
                if (reinforced) turnCue += "\n\n" + Reinforcement;
                if (trial.Variant == "speaker-anchor") turnCue = BuildSpeakerAnchorCue(turnCue);
                if (trial.Variant == "generation-boundary") turnCue = BuildGenerationBoundaryCue(turnCue);
                string retryCue = DialogueTurnPolicy.EmptyRetryCue(turnCue);
                var messages = new List<Dictionary<string, string>>
                {
                    Message("system", compatibility + "\n\n" + turnCue),
                    Message("system", EmotionMetadata.Strip(Knowledge)),
                    Message("user", EmotionMetadata.Strip(BuildContext(scenario)) + "\n\n" + turnCue)
                };
                var attempts = new List<Attempt>();
                results.Add(new CaseResult(scenario.Id, scenario.Purpose, trial.Variant, trial.Repetition, attempts));
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (networkCalls >= maximumCalls) throw new InvalidOperationException("Request bound reached.");
                    var payload = new
                    {
                        model = "xialong-v1", messages, max_tokens = 512,
                        temperature = 0.7f, top_k = 64, top_p = 0.95f,
                        n = 1, echo = false, enable_thinking = false, stream = true
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                    request.Headers.Add("x-correlation-id", Guid.NewGuid().ToString("N")[..6]);
                    request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    Console.WriteLine($"Sending {trialId}, attempt {attempt + 1} (request {networkCalls + 1}/{maximumCalls} maximum).");
                    var timer = Stopwatch.StartNew();
                    networkCalls++;
                    using var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        error = "HTTP " + (int)response.StatusCode + "; stopped without automatic network retries.";
                        throw new HttpRequestException();
                    }
                    string stream = await response.Content.ReadAsStringAsync();
                    (string raw, string finish) = ReadStream(stream);
                    timer.Stop();
                    string emotionHint = EmotionMetadata.ExtractHint(raw, Stops());
                    string stopped = ApplyLocalStops(EmotionMetadata.Strip(raw));
                    string returned = DialogueTextCleanup.Clean(stopped, Active);
                    string failure = string.IsNullOrWhiteSpace(returned) ? "empty response" : "";
                    attempts.Add(new Attempt(attempt + 1, finish, Math.Round(timer.Elapsed.TotalSeconds, 3),
                        raw, stopped, failure, returned, emotionHint,
                        messages.Select(message => new Dictionary<string, string>(message)).ToList()));
                    Console.WriteLine($"Completed {trialId}: finish={finish}, result={(failure.Length == 0 ? "nonempty" : failure)}. No actor/formatting validation.");
                    WriteResults();
                    // Serial calls and a full second after completion. No game configuration is changed.
                    await Task.Delay(1000);
                    if (failure.Length == 0) break;
                    if (attempt == 1)
                    {
                        error = "No usable text after one empty-response retry.";
                        throw new InvalidOperationException();
                    }
                    // Same retry placement as the plugin: append to the last user message.
                    // The rejected assistant response is not inserted into history.
                    messages[^1]["content"] += "\n\n" + retryCue;
                }
            }
        }
        catch (Exception ex)
        {
            error ??= ex.GetType().Name + "; stopped without automatic network retries.";
        }
        finally
        {
            client.DefaultRequestHeaders.Authorization = null;
            WriteResults();
        }
        Console.WriteLine($"Finished: {networkCalls} live request(s). Results: {outputDirectory}");
        if (error != null) Console.WriteLine(error);
        return error == null ? 0 : 1;

        void WriteResults()
        {
            var data = new
            {
                startedUtc, capturedUtc = DateTime.UtcNow, endpoint = Endpoint, model = "xialong-v1",
                pluginVersion = version, pluginSourceSha256 = sourceHash,
                setup = "Standalone simulation of Silverpine-style chat prompts, not Unity/game execution. All scenario content is synthetic. Compatibility text extracted from current Plugin.cs; compiled current DialogueTurnPolicy.cs, DialogueTextCleanup.cs and EmotionMetadata.cs. Scenario-specific conversation rosters. Independent scenarios; no game-save data used.",
                parameters = new { max_tokens = 512, temperature = 0.7, top_k = 64, top_p = 0.95, stream = true, enable_thinking = false },
                mode,
                baselineTurnPolicyVersion = comparisonRun ? "1.2.13" : "",
                comparison = comparisonRun ? "Three group scenarios, three independent repetitions per arm, alternating/interleaved order. " + comparisonDescription + " Same sampling and cleanup, no controlled seed." : "",
                experimentalReinforcement = reinforced ? Reinforcement : "",
                pacingSeconds = 1, maximumCalls, networkCalls, error,
                stopSequences = Stops(), results
            };
            File.WriteAllText(Path.Combine(outputDirectory, "results.json"), JsonSerializer.Serialize(data, JsonOptions));
            var report = new StringBuilder("# Xialong synthetic Silverpine-style roleplay probe\n\n");
            report.AppendLine($"Model: `xialong-v1`; plugin instructions: {version}; live API calls: {networkCalls}.");
            report.AppendLine("\nPrompt mode: " + mode + ". All experimental changes are test-only, not installed. Emotion metadata is separated before local stops using current EmotionMetadata.cs.");
            if (comparisonRun)
                report.AppendLine("\nComparison: three group scenarios, three independent repetitions per arm (nine frozen baseline, nine historical candidate). Arms alternate/interleave. " + comparisonDescription + " Shared compatibility instructions are read from the current Plugin.cs. No controlled seed; this is a small exploratory comparison, not a statistical reliability estimate.");
            report.AppendLine("\nAll character profiles, knowledge, memories and conversations are fictional test fixtures. This is not an in-game integration test. Sampling: temperature 0.7, top-k 64, top-p 0.95, 512-token output ceiling; serial calls with a one-second cooldown.");
            report.AppendLine("\nEach prompt uses the plugin's current system instructions, a Silverpine-style knowledge/character system message, and a user message containing memories, location, surroundings, player/active-NPC profiles, recent dialogue and the current-turn cue. Exact sent messages are in results.json (no authentication headers).");
            report.AppendLine("\nOnly empty responses are retried. There are no actor/formatting checks or quality scores. Review absent-participant inventions and any other NPC's speech manually; listed conversation NPCs may only make brief nonverbal reactions inside paired single asterisks. Only the active NPC may speak. Raw output and the post-stop text are recorded separately.");
            if (error != null) report.AppendLine("\nRun stopped: " + error);
            foreach (CaseResult result in results)
            {
                report.AppendLine("\n## " + result.Id + " / " + result.Variant + " / repetition " + result.Repetition + "\n\n" + result.Purpose);
                foreach (Attempt attempt in result.Attempts)
                {
                    report.AppendLine($"\n### Attempt {attempt.Number}\n\nFinish: {attempt.FinishReason}. Time: {attempt.Seconds}s. Result: {(attempt.EmptyResponseFailure.Length == 0 ? "nonempty (not quality-validated)" : attempt.EmptyResponseFailure)}.");
                    if (attempt.EmotionHint.Length > 0) report.AppendLine("\nSeparate emotion hint (not returned as dialogue): " + attempt.EmotionHint);
                    report.AppendLine("\nRaw model output:\n\n```text\n" + attempt.RawText + "\n```");
                    if (attempt.RawText != attempt.AfterLocalStops)
                        report.AppendLine("\nAfter local stops:\n\n```text\n" + attempt.AfterLocalStops + "\n```");
                    report.AppendLine("\nReturned to simulated game:\n\n```text\n" + attempt.ReturnedText + "\n```");
                }
            }
            File.WriteAllText(Path.Combine(outputDirectory, "report.md"), report.ToString());
        }
    }

    private static Dictionary<string, string> Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static bool IsGroupScenario(string id) => id is "03_present_mira" or "06_group_scene_pressure" or "07_contaminated_history";

    // Test-only candidate combining suggestions 1 and 2. No production changes.
    private static string BuildSpeakerAnchorCue(string currentCue)
    {
        const string originalRoster = "NPCs taking part in this conversation: Elian, Mira. This is the current conversation roster, not a list of all nearby or known NPCs. ";
        if (!currentCue.Contains(originalRoster, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate roster-source check failed.");
        return currentCue.Replace(originalRoster,
            "\nSpeaking NPC: Elian\nSilent participants: Mira\nThese are the current conversation NPCs, not all nearby or known NPCs. ", StringComparison.Ordinal) +
            "\n\nWhen a silent participant reacts and you continue with spoken dialogue, return the narration's " +
            "focus to Elian before that dialogue, or end the response after the reaction. Mentioning Mira's " +
            "action does not make her the next speaker. All spoken words still belong to Elian. " +
            "Consecutive actions and reactions may remain inside the same asterisk pair. " +
            "Example of the structure, not an event or wording to copy into this scene:\n" +
            "*Elian gestures toward Mira. She nods, and he turns back to you.* She's the person to ask about herbs. " +
            "Use this speaker-ownership pattern while responding naturally to the actual current request.";
    }

    // Revised test-only candidate. It does not call BuildSpeakerAnchorCue:
    // no 'silent participants' label or instruction to physically redirect focus.
    private static string BuildGenerationBoundaryCue(string currentCue)
    {
        const string originalRoster = "NPCs taking part in this conversation: Elian, Mira. This is the current conversation roster, not a list of all nearby or known NPCs. ";
        if (!currentCue.Contains(originalRoster, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate roster-source check failed.");
        return currentCue.Replace(originalRoster,
            "\nGenerate only Elian's contribution to this turn.\nOther conversation participants: Mira (not a speaker for this generated turn).\nThese are the current conversation NPCs, not all nearby or known NPCs. ", StringComparison.Ordinal) +
            "\n\nEvery spoken sentence belongs to Elian, regardless of which character was most recently " +
            "mentioned in narration. Other participants may make brief, optional nonverbal reactions " +
            "inside the narration. Do not generate their replies, including quotations inside asterisks. " +
            "These are text-generation boundaries, not events in the story. Characters should not " +
            "interrupt, silence, or redirect one another merely to satisfy these instructions. " +
            "Consecutive actions may share one asterisk enclosure. " +
            "Example of the structure, not an event or wording to copy into this scene:\n" +
            "*Elian gestures toward Mira, who nods in greeting.* She runs this shop. " +
            "Respond naturally to the actual current request.";
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        if (first < 0 || last < 0) throw new InvalidOperationException("Source boundary not found.");
        return source[first..last];
    }

    private static string Expand(string expression)
    {
        var output = new StringBuilder();
        foreach (Match match in Regex.Matches(expression, "\"(?:\\\\.|[^\"\\\\])*\""))
            output.Append(JsonSerializer.Deserialize<string>(match.Value));
        return output.ToString();
    }

    private static string[] Stops() => ["\n", "<api_user>", "<api_assistant>", "USER:", "ASSISTANT:", "SYSTEM:", .. Others.Select(name => name + ":")];

    private static string ApplyLocalStops(string output)
    {
        string trimmed = output.TrimStart();
        if (trimmed.StartsWith("ASSISTANT:", StringComparison.OrdinalIgnoreCase)) output = trimmed[10..].TrimStart();
        int stopAt = output.Length;
        foreach (string stop in Stops())
        {
            int index = output.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) stopAt = Math.Min(stopAt, index);
        }
        return output[..stopAt];
    }

    private static (string text, string finish) ReadStream(string stream)
    {
        var text = new StringBuilder();
        string finish = "";
        int events = 0;
        foreach (string line in Regex.Split(stream, "\\r?\\n"))
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            string data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;
            using JsonDocument document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            events++;
            JsonElement choice = choices[0];
            string chunk = GetString(choice, "text");
            if (chunk.Length == 0 && choice.TryGetProperty("message", out var message)) chunk = GetString(message, "content");
            if (chunk.Length == 0 && choice.TryGetProperty("delta", out var delta)) chunk = GetString(delta, "content");
            text.Append(chunk);
            string reason = GetString(choice, "finish_reason");
            if (reason.Length > 0) finish = reason;
        }
        if (events == 0) throw new InvalidOperationException("No readable SSE events.");
        return (text.ToString().TrimStart('\r', '\n'), finish);
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string BuildContext(Scenario scenario) => $"""
        SYSTEM: <memories>
        Yesterday:
        Elian repaired a torn map for Torren. Torren paid three silver coins and said his forge would be closed this afternoon.
        This morning:
        Elian spoke to Mira about a delivery of lavender. Mira planned to spend the afternoon sorting herbs at her own shop.
        Moments ago:
        {scenario.Memory}
        </memories>

        Current time: afternoon (3 PM).
        Current season: early autumn.
        Current weather: light rain.
        Current location: {scenario.Location}
        This is what Elian can see immediately around himself: {scenario.Visible}
        This is what Elian is currently doing: talking with Robin.

        This is the character description of Robin, the player's character:
        Robin is an adult human traveler, six feet tall, with brown eyes and short brown hair. They wear a blue wool cloak and carry an empty satchel. Robin is a new acquaintance of Elian.

        This is the character description of Elian:
        Elian is an adult male anthro black magpie with white shoulder patches, black wings, dark eyes, and taloned hands and feet. He is five feet seven inches tall. He wears a charcoal vest, a white linen shirt and a red scarf. Elian is a map restorer and courier. Personality: observant, friendly, cautious, quietly humorous. Elian owns neither a forge nor a herb shop. Elian knows Mira and Torren, but cannot decide what they say or do.

        Write a single paragraph. Narration and actions must ALWAYS be written *between asterisks* like this.
        POV: Write in second person present tense from Robin's point of view. Refrain from writing the dialog of anyone but Elian.

        {scenario.History}
        Robin: {scenario.PlayerLine}

        SYSTEM: (Elian's turn.)
        """;

    private static Scenario[] Scenarios()
    {
        const string alone = "Elian's Map Shop - Front Room. Only Elian and Robin are present. Mira is at her own herb shop; Torren is at the forge. The other listed villagers are elsewhere.";
        const string props = "a map counter, rolled charts, an ink bottle, a wooden stool, a closed front door, a rain-streaked window, Robin.";
        const string group = "Mira's Herb Shop - Work Room. Three people are present: Elian, Robin, and Mira. Torren and the other listed villagers are elsewhere. Nobody has changed rooms during this conversation.";
        const string groupProps = "a counter, shelves of herbs, a mortar and pestle, three stools, a closed front door, Robin, Mira. Mira has not yet spoken in this exchange.";
        return
        [
            new("01_greeting", "Control: ordinary single-NPC greeting, with many other NPC profiles in background knowledge.", alone, props,
                "Robin entered the front room and greeted Elian.", "", "Hello. Is this where I can have a map repaired?"),
            new("02_absent_mira_invitation", "Invitation to visit an absent named NPC: the reply should stop before moving the player, arriving, or scripting Mira's greeting.", alone, props,
                "Elian mentioned that Mira sells herbs across the road.", "Elian: Mira is a friend of mine. Her shop is just across the road.",
                "Could you take me over to Mira's shop and introduce me? I would like to meet her."),
            new("03_present_mira", "Multi-dialogue setup: participant Mira may only react nonverbally inside asterisks; only Elian may speak.", group, groupProps,
                "Robin and Elian entered Mira's work room. Mira joined their conversation. This is Robin's first meeting with her.", "",
                "Hello, Mira. Elian has told me you're good with herbs. Elian, would you introduce us?"),
            new("04_unknown_visitor", "An unidentified knock tempts the model to invent a new NPC or script a visitor. No visitor identity or actions beyond the knock are established.", alone,
                props + " A knock is heard at the closed door. The source is not visible and nobody has entered.",
                "Robin and Elian heard a knock while talking.", "", "Who's that at the door? Can you answer it?"),
            new("05_absent_torren", "A question about an absent named NPC tests remote speech, invented arrival, and species/appearance mixing.", alone, props,
                "Elian told Robin that Torren is working at the forge and is not at the map shop.", "Elian: Torren is the village blacksmith.",
                "What does Torren look like? Could you call him over and let him explain his work?"),
            new("06_group_scene_pressure", "Stress test: the player asks for a whole scene; the prompt permits only brief nonverbal Mira reactions inside asterisks, no Mira speech or player decisions.", group, groupProps,
                "Robin is visiting Mira's work room with Elian. Everyone remains in the same room.", "Elian: We could ask Mira about the lavender shipment.",
                "Please play out the whole scene, including Mira's reply and what we all do next. Start with your question to her."),
            new("07_contaminated_history", "Legacy-history stress test: do not copy prior Mira speech or player control; only nonverbal Mira reactions inside asterisks remain acceptable.", group, groupProps,
                "Mira is present in her work room, together with Elian and Robin.",
                "Elian: *He points at the jars. Mira smiles and says, 'The lavender is ready.' You nod and follow them to the counter.*",
                "What do they say next?"),
            new("08_emotion_memory", "A synthetic stored rumor contains Emotion: happy. Strip it from copied prompt context as production does, then inspect newly generated raw and cleaned output.", alone, props,
                "After hearing the rumor from Mira, Elian expressed this feeling about the matter: \"A new library? That's wonderful! Emotion: happy\".", "",
                "I heard there may be a new library. What do you think of that?")
        ];
    }
}
