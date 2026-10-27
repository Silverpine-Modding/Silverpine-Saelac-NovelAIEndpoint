using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KoboldSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

namespace NovelAIEndpoint;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.silverpine.novelai-endpoint";
    public const string PluginName = "NovelAI Endpoint";
    public const string PluginVersion = "1.2.14";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> UseNovelAI = null!;
    internal static ConfigEntry<string> Model = null!;
    internal static ConfigEntry<float> RequestDelaySeconds = null!;
    internal static ConfigEntry<bool> SendStopSequences = null!;
    internal static ConfigEntry<string> EncryptedApiKey = null!;
    internal static ConfigEntry<bool> SeparateKeyMigrationComplete = null!;
    private static ConfigFile PluginConfig = null!;

    private void Awake()
    {
        Log = Logger;
        PluginConfig = Config;
        UseNovelAI = Config.Bind(
            "Endpoint",
            "Use NovelAI",
            false,
            "Selects NovelAI instead of OpenRouter for Silverpine's hosted API mode.");
        Model = Config.Bind(
            "Endpoint",
            "Model",
            "xialong-v1",
            new ConfigDescription(
                "NovelAI text-generation model. Xialong is NovelAI's " +
                "roleplay-oriented GLM fine-tune; GLM 4.6 is the base model.",
                new AcceptableValueList<string>(
                    "xialong-v1",
                    "glm-4-6")));
        if (Model.Value == "kayra-v1")
            Model.Value = "xialong-v1";
        RequestDelaySeconds = Config.Bind(
            "Endpoint",
            "Delay Between Requests",
            1f,
            "Minimum cooldown in seconds after one NovelAI request completes " +
            "before the next queued request begins.");
        SendStopSequences = Config.Bind(
            "Endpoint",
            "Send Stop Sequences",
            true,
            "Sends a bounded set of Silverpine stop sequences to NovelAI " +
            "text-completion requests. Streaming chat requests enforce " +
            "their stops locally.");
        EncryptedApiKey = Config.Bind(
            "Authentication",
            "Encrypted API Key",
            "",
            "NovelAI access token encrypted with Silverpine's " +
            "SimpleEncryption format. Enter the token through the game's " +
            "API key field while NovelAI is selected; do not enter plaintext " +
            "in this configuration value.");
        SeparateKeyMigrationComplete = Config.Bind(
            "Authentication",
            "Separate Key Migration Complete",
            false,
            "Internal migration marker that prevents an OpenRouter key from " +
            "being mistaken for a NovelAI token.");

        new Harmony(PluginGuid).PatchAll();
        Log.LogInfo("NovelAI endpoint loaded.");
    }

    internal static string GetNovelAIApiKey()
    {
        string encrypted = EncryptedApiKey.Value?.Trim() ?? "";
        if (encrypted.Length == 0)
            return "";

        try
        {
            return SimpleEncryption.SimpleDecrypt(encrypted).Trim();
        }
        catch (Exception exception)
        {
            Log.LogError(
                "The encrypted NovelAI API key could not be decrypted. " +
                $"Re-enter it in the game settings. {exception.Message}");
            return "";
        }
    }

    internal static void SaveNovelAIApiKey(string token)
    {
        string trimmed = token?.Trim() ?? "";
        string encrypted = trimmed.Length == 0
            ? ""
            : SimpleEncryption.SimpleEncrypt(trimmed);
        if (EncryptedApiKey.Value == encrypted)
            return;

        EncryptedApiKey.Value = encrypted;
        PluginConfig.Save();
    }
}

internal static class ProviderApiKeyManager
{
    private static TMP_InputField ApiKeyInputField = null!;
    private static string OpenRouterApiKey = "";

    internal static void Attach(SettingsUI settings)
    {
        ApiKeyInputField = Traverse.Create(settings)
            .Field("apiKeyInputField")
            .GetValue<TMP_InputField>();
        ApiKeyInputField.onValueChanged.AddListener(value =>
        {
            if (!Plugin.UseNovelAI.Value)
            {
                // Silverpine's listener runs first and has already updated its
                // own plaintext-in-memory OpenRouter value.
                OpenRouterApiKey = settings.apiKey ?? "";
                return;
            }

            Plugin.SaveNovelAIApiKey(value);

            // Silverpine's original listener temporarily copied the visible
            // field into apiKey and saved it. Restore the OpenRouter value and
            // immediately rewrite settings.json so the providers stay separate.
            settings.apiKey = OpenRouterApiKey;
            SaveSilverpineConfig(settings);
        });
    }

    internal static void AfterSettingsLoaded(SettingsUI settings)
    {
        OpenRouterApiKey = settings.apiKey ?? "";

        if (!Plugin.SeparateKeyMigrationComplete.Value)
        {
            // Versions through 1.2.5 reused Silverpine's OpenRouter field. If
            // NovelAI was the saved provider, move that value once, then clear
            // it from settings.json so it cannot be sent to OpenRouter later.
            if (Plugin.UseNovelAI.Value &&
                string.IsNullOrEmpty(Plugin.GetNovelAIApiKey()) &&
                !string.IsNullOrWhiteSpace(settings.apiKey))
            {
                Plugin.SaveNovelAIApiKey(settings.apiKey);
                settings.apiKey = "";
                OpenRouterApiKey = "";
                SaveSilverpineConfig(settings);
                Plugin.Log.LogInfo(
                    "Migrated the existing NovelAI token into the plugin's " +
                    "encrypted provider-specific configuration.");
            }

            Plugin.SeparateKeyMigrationComplete.Value = true;
        }

        ShowSelectedProviderKey(Plugin.UseNovelAI.Value);
    }

    internal static void OnProviderChanged(
        SettingsUI settings,
        bool novelAISelected)
    {
        if (novelAISelected)
        {
            OpenRouterApiKey = settings.apiKey ?? "";
        }
        else
        {
            settings.apiKey = OpenRouterApiKey;
        }

        ShowSelectedProviderKey(novelAISelected);
    }

    internal static void PrepareNovelAI(SettingsUI settings, string token)
    {
        OpenRouterApiKey = settings.apiKey ?? "";
        Plugin.SaveNovelAIApiKey(token);
        ShowSelectedProviderKey(novelAISelected: true);
    }

    private static void ShowSelectedProviderKey(bool novelAISelected)
    {
        if (ApiKeyInputField == null)
            return;

        ApiKeyInputField.SetTextWithoutNotify(
            novelAISelected
                ? Plugin.GetNovelAIApiKey()
                : OpenRouterApiKey);
    }

    private static void SaveSilverpineConfig(SettingsUI settings)
    {
        Traverse.Create(settings).Method("SaveConfig").GetValue();
    }
}

[HarmonyPatch(typeof(SettingsUI), "Awake")]
internal static class SettingsUIAwakePatch
{
    private const string NovelAILabel = "NovelAI";

    private static void Postfix(SettingsUI __instance)
    {
        try
        {
            MineButton selector = Traverse.Create(__instance)
                .Field("aiProcessingMineButton")
                .GetValue<MineButton>();

            AddNovelAIOption(selector);
            ProviderApiKeyManager.Attach(__instance);
            selector.OnValueChanged += value =>
            {
                bool selected = value == NovelAILabel;
                Plugin.UseNovelAI.Value = selected;
                if (selected)
                {
                    __instance.useAIServer = false;
                    __instance.useAPIModel = true;
                    __instance.apiUrl = NovelAIClient.BaseUrl;
                }
                else
                {
                    __instance.apiUrl = "https://openrouter.ai/api/v1";
                }
                ProviderApiKeyManager.OnProviderChanged(
                    __instance,
                    selected);
            };

        }
        catch (Exception exception)
        {
            Plugin.Log.LogError($"Could not add NovelAI endpoint: {exception}");
        }
    }

    private static void AddNovelAIOption(MineButton selector)
    {
        Traverse selectorFields = Traverse.Create(selector);
        IList options = selectorFields.Field("options").GetValue<IList>();
        Type optionType = typeof(MineButton).GetNestedType(
            "Option",
            BindingFlags.NonPublic)!;
        FieldInfo labelField = AccessTools.Field(optionType, "label");

        foreach (object option in options)
        {
            if ((string)labelField.GetValue(option) == NovelAILabel)
                return;
        }

        object novelAIOption = Activator.CreateInstance(optionType)!;
        labelField.SetValue(novelAIOption, NovelAILabel);

        // Reuse the OpenRouter option's visibility rules so the API-key control
        // appears for NovelAI as well.
        FieldInfo enableField = AccessTools.Field(optionType, "toEnable");
        foreach (object option in options)
        {
            if ((string)labelField.GetValue(option) == "OpenRouter")
            {
                var enabledObjects =
                    (List<GameObject>)enableField.GetValue(option);
                enableField.SetValue(
                    novelAIOption,
                    new List<GameObject>(enabledObjects));
                break;
            }
        }

        options.Add(novelAIOption);
    }
}

[HarmonyPatch(typeof(SettingsUI), "Start")]
internal static class SettingsUIStartPatch
{
    private static void Postfix(SettingsUI __instance)
    {
        try
        {
            ProviderApiKeyManager.AfterSettingsLoaded(__instance);
            if (!Plugin.UseNovelAI.Value)
                return;

            __instance.useAIServer = false;
            __instance.useAPIModel = true;
            __instance.apiUrl = NovelAIClient.BaseUrl;

            MineButton selector = Traverse.Create(__instance)
                .Field("aiProcessingMineButton")
                .GetValue<MineButton>();

            // Silverpine loads and redraws its settings in Start. Restore the
            // provider only after that load, without firing SaveConfig.
            selector.SetValueWithoutNotify("NovelAI");
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Could not restore the NovelAI selection: {exception}");
        }
    }
}

[HarmonyPatch(typeof(APIModelHandler), nameof(APIModelHandler.GetChatCompletion))]
internal static class ChatCompletionPatch
{
    private static bool Prefix(
        GenParams genParams,
        ref Task<APIModelHandler.ChatCompletionsReponse> __result)
    {
        if (!Plugin.UseNovelAI.Value)
            return true;

        __result = NovelAIClient.Generate(genParams);
        return false;
    }
}

[HarmonyPatch(
    typeof(DialogBox),
    "AnimateText",
    new[] { typeof(string), typeof(bool), typeof(Action), typeof(bool) })]
internal static class NovelAIDialogFormattingPatch
{
    private static void Prefix(ref string text)
    {
        if (!Plugin.UseNovelAI.Value || string.IsNullOrEmpty(text))
            return;

        text = EmotionMetadata.Strip(text);

        // Empty action spans such as "Gareth: ** ** *He looks up.*" render
        // as an expanding blank gap. Remove them before Silverpine interprets
        // the asterisks as formatting.
        text = Regex.Replace(
            text,
            @"(?m)(^|\n)([^\r\n:]{1,80}:(?:\*)?[ \t]*)?(?:\*\*(?:[ \t]+|$))+",
            "$1$2");

        // Silverpine normally changes every adjacent action boundary from
        // "* *" to "* - *". Merge those action spans before its animator
        // can manufacture visible, cumulative dash separators.
        text = Regex.Replace(text, @"\*\s+\*", " ");

        // Clean separators already stored in an active conversation. This is
        // display-only; it does not rewrite the user's save data.
        text = Regex.Replace(
            text,
            @"^([^\r\n:]{1,80}:\s*)(?:[-\u2013\u2014]\s*)+",
            "$1");
    }
}

[HarmonyPatch(typeof(NeuralNPC), nameof(NeuralNPC.AddThingThatHappened))]
internal static class NovelAIHappeningMetadataPatch
{
    private static bool Prefix(ref string thingThatHappened)
    {
        if (!Plugin.UseNovelAI.Value) return true;
        thingThatHappened = EmotionMetadata.Strip(thingThatHappened);
        return !string.IsNullOrWhiteSpace(thingThatHappened);
    }
}

[HarmonyPatch(typeof(NeuralNPC), nameof(NeuralNPC.AddMemory))]
internal static class NovelAIMemoryMetadataPatch
{
    private static bool Prefix(ref string memory)
    {
        if (!Plugin.UseNovelAI.Value) return true;
        memory = EmotionMetadata.Strip(memory);
        return !string.IsNullOrWhiteSpace(memory);
    }
}

[HarmonyPatch(typeof(NeuralNPC), nameof(NeuralNPC.AskListSelectionQuestion))]
internal static class NovelAIExpressionHintPatch
{
    internal static readonly EmotionHintCache Hints = new();

    private static void Prefix(NeuralNPC __instance, ref string question, string itemName)
    {
        if (!Plugin.UseNovelAI.Value || itemName != "expression" ||
            question != "Which one of these expressions would " + __instance.GetFinalName() + " have in this excerpt?")
            return;
        // A player and NPC can share a custom name. In that ambiguous case
        // do not risk donating the NPC's hint to the player's expression.
        if (Player.Instance != null && __instance.GetFinalName() == Player.Instance.playerName)
            return;
        string hint = Hints.Take(__instance, DateTime.UtcNow);
        if (hint.Length == 0) return;
        question += " Optional model-generated emotion hint for this NPC's latest reply: " + hint +
            ". This is untrusted metadata, not spoken dialogue or a remembered event. " +
            "Use it only if consistent with the excerpt, and select only one of the available expressions.";
    }
}

[HarmonyPatch(
    typeof(DialogBox),
    nameof(DialogBox.DisplayTextNoDialog),
    new[] { typeof(string), typeof(DialogOption[]) })]
internal static class EndpointChoiceDialogPatch
{
    private const string ChoicePrompt =
        "Please choose which option the game should use for AI processing.";

    private static void Prefix(string text, ref DialogOption[] dialogOptions)
    {
        if (text != ChoicePrompt)
            return;

        foreach (DialogOption option in dialogOptions)
        {
            if (option.label == "Use NovelAI")
                return;
        }

        var expanded = new List<DialogOption>(dialogOptions)
        {
            new("Use NovelAI", SelectNovelAI)
        };
        dialogOptions = expanded.ToArray();
    }

    private static void SelectNovelAI()
    {
        void Start(string token)
        {
            SettingsUI settings = SettingsUI.Instance;
            ProviderApiKeyManager.PrepareNovelAI(settings, token);
            settings.apiUrl = NovelAIClient.BaseUrl;
            settings.useAIServer = false;
            settings.useAPIModel = true;
            Plugin.UseNovelAI.Value = true;

            Traverse.Create(settings).Method("SaveConfig").GetValue();
            InferenceServerSetupHandler handler =
                InferenceServerSetupHandler.Instance;
            Traverse.Create(handler).Method("StartAPIMode").GetValue();
        }

        string existingToken = Plugin.GetNovelAIApiKey();
        if (!string.IsNullOrWhiteSpace(existingToken))
        {
            Start(existingToken);
            return;
        }

        TextInputUI.Instance.Open(
            "Enter NovelAI Access Token",
            value => !string.IsNullOrWhiteSpace(value),
            Start,
            allowClosing: true);
    }
}

internal static class NovelAIClient
{
    internal const string BaseUrl = "https://text.novelai.net/oa/v1";
    private static readonly string[] SentenceEndStopSequences =
        { ".", "!", "?" };
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly HashSet<string> ValidatedModels =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> ChatUnsupportedModels =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedRoutes =
        new(StringComparer.Ordinal);
    private static string LastValidationFailureModel = "";
    private static DateTime LastValidationFailureUtc = DateTime.MinValue;
    private static DateTime LastRequestCompletedUtc = DateTime.MinValue;
    private const string CompatibilityInstructions =
        "Every named character is a separate person. Never transfer " +
        "appearance, species, anatomy, possessions, memories, " +
        "relationships, or personality traits between characters. " +
        "Attribute a detail only to the character explicitly named with " +
        "that detail. When uncertain, omit only that detail instead of " +
        "guessing, but always provide the requested answer.\n" +
        "A dialogue request is one turn centered on the active NPC. " +
        "Only the active NPC may speak. Other NPCs on the current conversation " +
        "roster may only make brief, nonverbal action-based reactions enclosed " +
        "in paired single asterisks. Do not write, quote, or paraphrase their " +
        "speech, even inside narration. Do not invent absent participants or write another " +
        "NPC's full independent turn. Leave the player's actions and decisions " +
        "to the player. Stop when the active NPC's turn is complete. Do not begin with " +
        "any character name or role label.\n" +
        "Never use underscores for emphasis or formatting. Use single " +
        "asterisks to separate narration from spoken dialogue, not to mark " +
        "individual sentences or emphasize words. Consecutive actions, body " +
        "language, and scene descriptions can share one continuous *...* " +
        "span, including permitted nonverbal reactions by another participant. " +
        "Do not close and reopen a span merely because the sentence or " +
        "described actor changes. Open the span before narration starts, " +
        "close it before spoken dialogue, and open a new span when narration " +
        "resumes. If the response ends in narration, close that span before " +
        "ending. Spoken words must remain plain text outside asterisks. " +
        "If a reply begins with an action, its first non-whitespace character " +
        "must be an asterisk. Example: *She looks up. Her ears twitch.* " +
        "Hello there. *She waits for your answer.*";
    private const string UtilityInstructions =
        "Follow the user's requested output format exactly. Return only the " +
        "requested answer, with no role label, explanation, commentary, or " +
        "Markdown code fence.";

    [Serializable]
    private sealed class CompletionRequest
    {
        public string model = "";
        public string prompt = "";
        public int max_tokens = 512;
        public float temperature = 0.3f;
        public int top_k = 64;
        public float top_p = 1f;
        public string[] stop = Array.Empty<string>();
    }

    [Serializable]
    private sealed class ChatRequest
    {
        public string model = "";
        public List<Message> messages = new();
        public int max_tokens = 512;
        public float temperature = 0.3f;
        public int top_k = 64;
        public float top_p = 1f;
        public int n = 1;
        public bool echo = false;
        public bool enable_thinking = false;
        public bool stream = true;
    }

    [Serializable]
    private sealed class ModelsResponse
    {
        public List<ModelInfo> data = new();
    }

    [Serializable]
    private sealed class ModelInfo
    {
        public string id = "";
    }

    [Serializable]
    private sealed class Message
    {
        public string role = "";
        public string content = "";
    }

    [Serializable]
    private sealed class Response
    {
        public List<Choice> choices = new();
    }

    [Serializable]
    private sealed class Choice
    {
        public string text = "";
        public ResponseMessage message = new();
        public ResponseMessage delta = new();
        public string finish_reason = "";
        public string matched_stop = "";
    }

    [Serializable]
    private sealed class ResponseMessage
    {
        public string content = "";
    }

    private sealed class NovelAIRequestException : Exception
    {
        internal long ResponseCode { get; }

        internal NovelAIRequestException(long responseCode, string message)
            : base(message)
        {
            ResponseCode = responseCode;
        }
    }

    internal static async Task<APIModelHandler.ChatCompletionsReponse> Generate(
        GenParams genParams)
    {
        // Capture the turn owner before any wait. Multi-dialog may advance the
        // game's global active NPC while a queued/network request is pending.
        NeuralNPC activeNpc = genParams.isDialog ? NeuralNPC.currentActiveDialogNeuralNPC : null;
        string activeSpeaker = activeNpc?.GetFinalName()?.Trim() ?? "";
        if (activeNpc != null)
            NovelAIExpressionHintPatch.Hints.Set(activeNpc, "", DateTime.UtcNow);
        string[] participants = genParams.isDialog
            ? GetConversationNpcNames(activeNpc)
            : Array.Empty<string>();
        string turnCue = genParams.isDialog ? DialogueTurnPolicy.Build(activeSpeaker, participants) : "";
        await RequestGate.WaitAsync();
        try
        {
            double cooldown = Math.Max(
                0.0,
                Plugin.RequestDelaySeconds.Value);
            double elapsed = (
                DateTime.UtcNow - LastRequestCompletedUtc).TotalSeconds;
            double remaining = cooldown - elapsed;
            if (remaining > 0.0)
            {
                await Task.Delay(TimeSpan.FromSeconds(remaining));
            }
            return await GenerateCore(genParams, activeSpeaker, activeNpc, turnCue);
        }
        finally
        {
            LastRequestCompletedUtc = DateTime.UtcNow;
            RequestGate.Release();
        }
    }

    private static async Task<APIModelHandler.ChatCompletionsReponse> GenerateCore(
        GenParams genParams,
        string activeSpeaker,
        NeuralNPC activeNpc,
        string turnCue)
    {
        if (string.IsNullOrWhiteSpace(Plugin.GetNovelAIApiKey()))
            throw new Exception(
                "Enter your NovelAI access token in the API Key field.");

        await EnsureConfiguredModelAvailable();

        List<Message> messages = ConvertMessages(genParams.prompt);
        bool hasAssistantPrefill = messages.Count > 0 &&
            messages[messages.Count - 1].role == "assistant";
        string assistantPrefill = hasAssistantPrefill
            ? messages[messages.Count - 1].content
            : "";
        string model = Plugin.Model.Value.Trim();
        string completionPrompt = BuildCompletionPrompt(
            messages,
            genParams.isDialog,
            genParams.grammar,
            turnCue);
        List<Message> chatMessages = BuildChatMessages(
            messages,
            genParams.isDialog,
            genParams.grammar,
            turnCue);
        bool isConversationBroadcast = IsConversationBroadcast(genParams);

        string[] silverpineStopSequences = (genParams.stop_sequence ??
                Array.Empty<string>())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct()
            .ToArray();
        string[] stopSequences = isConversationBroadcast
            ? SentenceEndStopSequences
                .Concat(silverpineStopSequences)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : silverpineStopSequences;

        string[] serverStopSequences = Plugin.SendStopSequences.Value
            ? BuildServerStopSequences(stopSequences)
            : Array.Empty<string>();
        int maxTokens = genParams.max_length > 0
            ? genParams.max_length
            : 512;
        float temperature = Mathf.Max(0.1f, genParams.temperature);
        int topK = genParams.top_k == 0 ? 64 : genParams.top_k;
        float topP = genParams.top_p <= 0f ? 1f : genParams.top_p;

        var completionPayload = new CompletionRequest
        {
            model = model,
            prompt = completionPrompt,
            max_tokens = maxTokens,
            temperature = temperature,
            top_k = topK,
            top_p = topP,
            stop = serverStopSequences
        };
        var chatPayload = new ChatRequest
        {
            model = model,
            messages = chatMessages,
            max_tokens = maxTokens,
            temperature = temperature,
            top_k = topK,
            top_p = topP
        };

        // Exact assistant prefixes (letters, summaries, labels, and other
        // continuation tasks) need the text-completion endpoint. Ordinary
        // role-based requests use native chat so the model receives real
        // system/user/assistant boundaries.
        bool useChatEndpoint = !hasAssistantPrefill &&
            !ChatUnsupportedModels.Contains(model);
        LogRouteOnce(model, useChatEndpoint);

        async Task<(Choice choice, string rawResponse)> SendSelected()
        {
            if (!useChatEndpoint)
                return await SendWithStopFallback(completionPayload);

            try
            {
                return await SendChat(chatPayload);
            }
            catch (NovelAIRequestException exception)
                when (IsChatCompatibilityFailure(exception.ResponseCode))
            {
                ChatUnsupportedModels.Add(model);
                useChatEndpoint = false;
                Plugin.Log.LogWarning(
                    $"NovelAI's chat endpoint rejected model '{model}' " +
                    $"(HTTP {exception.ResponseCode}; " +
                    $"{GetSafeErrorDetail(exception)}). " +
                    $"Message layout: {DescribeMessageLayout(chatMessages)}. " +
                    "Falling back to text completions for this model for " +
                    "the rest of this game session.");
                LogRouteOnce(model, false);
                return await SendWithStopFallback(completionPayload);
            }
        }

        void AddRetryCue(string cue)
        {
            completionPayload.prompt +=
                "\n\nUSER:\n" + cue + "\n\nASSISTANT:\n" + assistantPrefill;

            if (chatPayload.messages.Count > 0 &&
                chatPayload.messages[chatPayload.messages.Count - 1].role ==
                    "user")
            {
                chatPayload.messages[chatPayload.messages.Count - 1].content +=
                    "\n\n" + cue;
            }
            else
            {
                chatPayload.messages.Add(new Message
                {
                    role = "user",
                    content = cue
                });
            }
        }

        Choice choice;
        string rawResponse;
        try
        {
            (choice, rawResponse) = await SendSelected();
        }
        catch
        {
            Plugin.Log.LogError(
                "NovelAI rejected a request with " +
                $"the {(useChatEndpoint ? "chat" : "completion")} endpoint, " +
                $"{messages.Count} converted messages, " +
                $"{stopSequences.Length} local stop sequences, " +
                $"prefill length {GetPrefillLength(messages)}, " +
                $"and prompt length {genParams.prompt?.Length ?? 0}.");
            throw;
        }

        string[] localStopSequences = stopSequences
            .Concat(new[] { "USER:", "ASSISTANT:", "SYSTEM:" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string output = "";
        string emotionHint = "";
        bool isExpressionRequest = !genParams.isDialog && (genParams.prompt ?? "").IndexOf(
            "Which one of these expressions would ", StringComparison.OrdinalIgnoreCase) >= 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string rawText = GetChoiceText(choice);
            string withoutEmotion = EmotionMetadata.Strip(rawText);
            emotionHint = genParams.isDialog ? EmotionMetadata.ExtractHint(rawText, localStopSequences) : "";
            // Expression classification may itself use an emotion label to
            // identify the requested option. Other outputs must be clean before
            // Silverpine can display, summarize or save them.
            output = ApplyLocalStops(isExpressionRequest ? rawText : withoutEmotion, localStopSequences);
            if (genParams.isDialog)
                output = DialogueTextCleanup.Clean(output, activeSpeaker);
            if (!string.IsNullOrWhiteSpace(output))
                break;

            // No actor/formatting validation or retries: only recover a truly
            // empty result after metadata, role-stop and cosmetic cleanup.
            if (attempt == 1)
            {
                Plugin.Log.LogError("NovelAI returned an empty response after one retry.");
                throw new Exception("NovelAI returned no usable text after one retry.");
            }
            Plugin.Log.LogWarning("NovelAI returned no usable text after cleanup" +
                "; retrying once with an explicit answer/turn cue.");
            double retryDelay = Math.Max(
                0.0,
                Plugin.RequestDelaySeconds.Value);
            if (retryDelay > 0.0)
                await Task.Delay(TimeSpan.FromSeconds(retryDelay));
            AddRetryCue(BuildBoundaryRetryCue(genParams.isDialog, turnCue));
            (choice, rawResponse) = await SendSelected();
        }

        if (genParams.isDialog && activeNpc != null)
            NovelAIExpressionHintPatch.Hints.Set(activeNpc, emotionHint, DateTime.UtcNow);

        // KoboldClient removes this leading space when the source prompt
        // ended with a prefill space.
        if (hasAssistantPrefill && !output.StartsWith(" "))
            output = " " + output;

        return new APIModelHandler.ChatCompletionsReponse
        {
            provider = "NovelAI",
            choices = new List<APIModelHandler.Choice>
            {
                new()
                {
                    message = new APIModelHandler.Message(
                        APIModelHandler.APIDialogElementType.assistant,
                        output)
                }
            }
        };
    }

    private static string[] BuildServerStopSequences(string[] stopSequences)
    {
        // Silverpine can provide more than twenty character-specific stops.
        // Keep the server request bounded; all stops are still enforced
        // locally after generation.
        return stopSequences
            .Where(sequence => sequence.Length <= 256)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendWithStopFallback(CompletionRequest payload)
    {
        try
        {
            return await SendCompletion(payload);
        }
        catch (NovelAIRequestException exception)
            when (exception.ResponseCode == 400 && payload.stop.Length > 0)
        {
            Plugin.Log.LogWarning(
                "NovelAI rejected server-side stop sequences; retrying " +
                "this request once with local stops only.");
            payload.stop = Array.Empty<string>();
            return await SendCompletion(payload);
        }
    }

    private static async Task EnsureConfiguredModelAvailable()
    {
        string model = Plugin.Model.Value.Trim();
        if (ValidatedModels.Contains(model))
            return;
        if (LastValidationFailureModel == model &&
            DateTime.UtcNow - LastValidationFailureUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        using UnityWebRequest request = UnityWebRequest.Get(BaseUrl + "/models");
        request.timeout = 30;
        SetStandardHeaders(request);

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
        {
            // A temporary model-list outage should not block a generation
            // endpoint that may still be healthy.
            if (request.responseCode >= 500 || request.responseCode == 0)
            {
                Plugin.Log.LogWarning(
                    "Could not validate the configured NovelAI model; " +
                    "continuing with the generation request.");
                LastValidationFailureModel = model;
                LastValidationFailureUtc = DateTime.UtcNow;
                return;
            }
            throw CreateException(request);
        }

        ModelsResponse response = (ModelsResponse)
            StringSerializationAPI.Deserialize(
                typeof(ModelsResponse),
                request.downloadHandler.text);
        if (response?.data == null ||
            !response.data.Any(item => item.id == model))
        {
            throw new Exception(
                $"NovelAI model '{model}' is not available for this account.");
        }

        ValidatedModels.Add(model);
        LastValidationFailureModel = "";
        LastValidationFailureUtc = DateTime.MinValue;
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendCompletion(CompletionRequest payload)
    {
        return await SendGenerationRequest(
            "/completions",
            JsonUtility.ToJson(payload));
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendChat(ChatRequest payload)
    {
        // Use the same serializer Silverpine uses for its native OpenRouter
        // chat requests. Unity's JsonUtility is reliable for the flat text
        // completion payload, but the chat payload contains a nested message
        // list and must preserve every role/content object.
        string json = StringSerializationAPI.SerializeCompact(
            typeof(ChatRequest),
            payload);
        int serializedRoleCount = Regex.Matches(
            json,
            "\\\"role\\\"\\s*:").Count;
        if (serializedRoleCount != payload.messages.Count)
        {
            throw new Exception(
                "NovelAI chat payload serialization failed: expected " +
                $"{payload.messages.Count} messages but serialized " +
                $"{serializedRoleCount} roles.");
        }
        using var request = new UnityWebRequest(
            BaseUrl + "/chat/completions",
            UnityWebRequest.kHttpVerbPOST);
        request.timeout = 60;
        request.uploadHandler = new UploadHandlerRaw(
            Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        SetStandardHeaders(request, "text/event-stream");

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
            throw CreateException(request);

        string rawResponse = request.downloadHandler.text;
        return (ParseChatEventStream(rawResponse), rawResponse);
    }

    private static async Task<(Choice choice, string rawResponse)>
        SendGenerationRequest(string path, string json)
    {
        using var request = new UnityWebRequest(
            BaseUrl + path,
            UnityWebRequest.kHttpVerbPOST);
        request.timeout = 60;
        request.uploadHandler = new UploadHandlerRaw(
            Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        SetStandardHeaders(request);

        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
            throw CreateException(request);

        string rawResponse = request.downloadHandler.text;
        Response response = (Response)StringSerializationAPI.Deserialize(
            typeof(Response),
            rawResponse);
        if (response?.choices == null || response.choices.Count == 0)
            throw new Exception("NovelAI returned no completion choices.");

        return (response.choices[0], rawResponse);
    }

    private static bool IsChatCompatibilityFailure(long responseCode)
    {
        return responseCode == 400 || responseCode == 404 ||
               responseCode == 405 || responseCode == 422;
    }

    private static string GetChoiceText(Choice choice)
    {
        if (!string.IsNullOrEmpty(choice.text))
            return choice.text;
        if (!string.IsNullOrEmpty(choice.message?.content))
            return choice.message.content;
        return choice.delta?.content;
    }

    private static Choice ParseChatEventStream(string rawResponse)
    {
        var combinedText = new StringBuilder();
        var combinedChoice = new Choice();
        int eventCount = 0;

        foreach (string rawLine in Regex.Split(rawResponse ?? "", "\\r?\\n"))
        {
            if (!rawLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            string data = rawLine.Substring("data:".Length).Trim();
            if (data.Length == 0 || data == "[DONE]")
                continue;

            Response response;
            try
            {
                response = (Response)StringSerializationAPI.Deserialize(
                    typeof(Response),
                    data);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning(
                    "NovelAI returned an unreadable chat stream event of " +
                    $"length {data.Length}: {exception.Message}");
                continue;
            }

            if (response?.choices == null || response.choices.Count == 0)
                continue;

            eventCount++;
            Choice chunk = response.choices[0];
            string chunkText = GetChoiceText(chunk);
            if (!string.IsNullOrEmpty(chunkText))
                combinedText.Append(chunkText);
            if (!string.IsNullOrEmpty(chunk.finish_reason))
                combinedChoice.finish_reason = chunk.finish_reason;
            if (!string.IsNullOrEmpty(chunk.matched_stop))
                combinedChoice.matched_stop = chunk.matched_stop;
        }

        if (eventCount == 0)
            throw new Exception(
                "NovelAI returned no readable chat stream events.");

        string streamedText = combinedText.ToString();
        string withoutTransportPadding = streamedText.TrimStart('\r', '\n');
        if (withoutTransportPadding.Length != streamedText.Length)
        {
            Plugin.Log.LogDebug(
                "Removed leading SSE chat line breaks before applying " +
                "Silverpine's newline stop boundary.");
        }
        combinedChoice.text = withoutTransportPadding;
        Plugin.Log.LogDebug(
            $"NovelAI chat stream contained {eventCount} events and " +
            $"{combinedChoice.text.Length} text characters.");
        return combinedChoice;
    }

    private static string DescribeMessageLayout(List<Message> messages)
    {
        int totalCharacters = messages.Sum(
            message => message.content?.Length ?? 0);
        string sequence = string.Join(
            ">",
            messages.Take(32).Select(message =>
                $"{message.role}({message.content?.Length ?? 0})"));
        if (messages.Count > 32)
            sequence += $">...(+{messages.Count - 32})";
        return $"{messages.Count} messages, {totalCharacters} characters " +
               $"[{sequence}]";
    }

    private static string GetSafeErrorDetail(
        NovelAIRequestException exception)
    {
        Match match = Regex.Match(
            exception.Message ?? "",
            @"""message""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""");
        if (!match.Success)
            return "no structured server error message";

        string detail = match.Groups["value"].Value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\");
        detail = new string(detail
            .Where(character => !char.IsControl(character))
            .Take(400)
            .ToArray());
        return detail.Length == 0
            ? "empty structured server error message"
            : detail;
    }

    private static void LogRouteOnce(string model, bool useChatEndpoint)
    {
        string route = useChatEndpoint ? "chat" : "text completions";
        if (!LoggedRoutes.Add(model + "|" + route))
            return;

        Plugin.Log.LogInfo(
            $"NovelAI hybrid routing for '{model}' is using {route}.");
    }

    private static void SetStandardHeaders(
        UnityWebRequest request,
        string accept = "application/json")
    {
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", accept);
        request.SetRequestHeader(
            "Authorization",
            "Bearer " + Plugin.GetNovelAIApiKey());
        request.SetRequestHeader(
            "x-correlation-id",
            Guid.NewGuid().ToString("N").Substring(0, 6));
    }

    private static string ApplyLocalStops(
        string output,
        string[] localStopSequences)
    {
        output = StripLeadingAssistantMarker(output ?? "");
        var firstStop = localStopSequences
            .Select(sequence => new
            {
                Sequence = sequence,
                Index = output.IndexOf(
                    sequence,
                    StringComparison.OrdinalIgnoreCase)
            })
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .FirstOrDefault();
        if (firstStop != null)
        {
            if (firstStop.Index == 0 && output.Length > 0)
            {
                Plugin.Log.LogWarning(
                    "NovelAI output began with a Silverpine " +
                    $"{DescribeStopBoundary(firstStop.Sequence)} stop " +
                    "boundary.");
            }
            output = output.Substring(0, firstStop.Index);
        }
        return output;
    }

    private static string DescribeStopBoundary(string sequence)
    {
        if (sequence == "\n" || sequence == "\r\n")
            return "newline";
        if (sequence == NeuralNPC.USER_TAG)
            return "user-role";
        if (sequence == NeuralNPC.ASSISTANT_TAG)
            return "assistant-role";
        if (sequence.Equals("SYSTEM:", StringComparison.OrdinalIgnoreCase))
            return "system-role";
        if (sequence.EndsWith(":"))
            return "speaker-label";
        return $"length-{sequence.Length}";
    }

    private static string StripLeadingAssistantMarker(string output)
    {
        string trimmedStart = output.TrimStart();
        const string marker = "ASSISTANT:";
        if (!trimmedStart.StartsWith(
                marker,
                StringComparison.OrdinalIgnoreCase))
            return output;

        return trimmedStart.Substring(marker.Length).TrimStart();
    }

    private static string BuildBoundaryRetryCue(bool isDialog, string turnCue)
    {
        if (isDialog)
        {
            return DialogueTurnPolicy.EmptyRetryCue(turnCue);
        }

        return "Provide only the requested answer now. Do not include a " +
            "USER, ASSISTANT, or SYSTEM label.";
    }

    private static string[] GetConversationNpcNames(NeuralNPC activeNpc)
    {
        if (activeNpc == null) return Array.Empty<string>();
        var names = new List<string> { activeNpc.GetFinalName() };
        // A stale group from a different conversation must not introduce NPCs.
        if (NeuralNPC.multiDialogParticipants != null &&
            NeuralNPC.multiDialogParticipants.Contains(activeNpc))
            names.AddRange(NeuralNPC.multiDialogParticipants
                .Where(npc => npc != null).Select(npc => npc.GetFinalName()));
        return names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsConversationBroadcast(GenParams genParams)
    {
        string prompt = genParams.prompt ?? "";
        return prompt.IndexOf(
                   "Summarize this conversation between ",
                   StringComparison.OrdinalIgnoreCase) >= 0 &&
               prompt.IndexOf(
                   "had a conversation about ",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int GetPrefillLength(List<Message> messages)
    {
        if (messages.Count == 0 ||
            messages[messages.Count - 1].role != "assistant")
            return 0;
        return messages[messages.Count - 1].content?.Length ?? 0;
    }

    private static List<Message> BuildChatMessages(
        List<Message> messages,
        bool isDialog,
        string grammar,
        string turnCue)
    {
        var chatMessages = new List<Message>
        {
            new()
            {
                role = "system",
                content = BuildInstructionText(isDialog, grammar, turnCue)
            }
        };

        // Copy rather than reuse the converted messages. Empty-response retry
        // cues may extend the chat request and must not alter the completion
        // fallback's source data.
        chatMessages.AddRange(messages.Select(message => new Message
        {
            role = message.role,
            content = message.content
        }));

        if (isDialog)
        {
            // Silverpine embeds its turn cue in a user message, not necessarily
            // a system message. Put the reminder at the actual generation point.
            if (chatMessages[chatMessages.Count - 1].role == "user")
            {
                chatMessages[chatMessages.Count - 1].content += "\n\n" + turnCue;
            }
            else
                chatMessages.Add(new Message { role = "user", content = turnCue });
        }
        return chatMessages;
    }

    private static string BuildInstructionText(
        bool isDialog, string grammar, string turnCue)
    {
        var instructions = new StringBuilder(
            isDialog ? CompatibilityInstructions : UtilityInstructions);
        if (isDialog)
        {
            instructions.Append("\n\n");
            instructions.Append(turnCue);
        }
        if (!isDialog && !string.IsNullOrWhiteSpace(grammar))
        {
            instructions.Append(
                "\nThe response must strictly match this GBNF grammar; " +
                "do not repeat the grammar:\n");
            instructions.Append(grammar);
        }
        return instructions.ToString();
    }

    private static string BuildCompletionPrompt(
        List<Message> messages,
        bool isDialog,
        string grammar,
        string turnCue)
    {
        var prompt = new StringBuilder();
        prompt.Append("SYSTEM:\n");
        prompt.Append(BuildInstructionText(isDialog, grammar, turnCue));
        prompt.Append("\n\n");

        for (int index = 0; index < messages.Count; index++)
        {
            Message message = messages[index];
            if (isDialog && index == messages.Count - 1 &&
                message.role == "assistant")
            {
                prompt.Append("USER:\n");
                prompt.Append(turnCue);
                prompt.Append("\n\n");
            }
            prompt.Append(message.role.ToUpperInvariant());
            prompt.Append(":\n");
            prompt.Append(message.content);

            // A final assistant message is Silverpine's completion prefill
            // (for example, an overheard-conversation summary ending in
            // "had a conversation about "). Do not terminate that unfinished
            // turn with a blank line or Xialong may stop without answering.
            bool isFinalAssistantPrefill =
                index == messages.Count - 1 &&
                message.role == "assistant";
            if (!isFinalAssistantPrefill)
                prompt.Append("\n\n");
        }

        if (messages.Count == 0 ||
            messages[messages.Count - 1].role != "assistant")
        {
            if (isDialog)
            {
                prompt.Append("USER:\n");
                prompt.Append(turnCue);
                prompt.Append("\n\n");
            }
            prompt.Append("ASSISTANT:\n");
        }
        return prompt.ToString();
    }

    private static List<Message> ConvertMessages(string prompt)
    {
        var converted = new List<Message>();
        var original = (List<APIModelHandler.Message>)Traverse
            .Create(typeof(APIModelHandler))
            .Method("ConvertRawPromptToApiDialogElements", prompt)
            .GetValue();

        foreach (APIModelHandler.Message message in original)
        {
            if (message == null || message.content == null)
                continue;

            string content = SanitizeContent(message.content);
            // Strip metadata from copies of old memories/dialogue used as
            // context. Do not edit the user's existing save entries in place.
            content = EmotionMetadata.Strip(content);
            if (message.role.ToString() == "assistant")
                content = StripHistoricalDashSeparators(content);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            converted.Add(new Message
            {
                role = message.role.ToString(),
                content = content
            });
        }

        if (!converted.Any(message => message.role == "user"))
        {
            converted.Add(new Message
            {
                role = "user",
                content = string.IsNullOrEmpty(prompt)
                    ? "Continue."
                    : SanitizeContent(prompt)
            });
        }
        return converted;
    }

    private static string SanitizeContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return "";

        var clean = new StringBuilder(content.Length);
        foreach (char character in content)
        {
            if (character == '\r' ||
                character == '\n' ||
                character == '\t' ||
                !char.IsControl(character))
            {
                clean.Append(character);
            }
        }
        return clean.ToString();
    }

    private static string StripHistoricalDashSeparators(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        // Remove empty action spans already stored after a speaker label so
        // Xialong cannot imitate and lengthen them in later responses.
        content = Regex.Replace(
            content,
            @"(?m)(^|\n)([^\r\n:]{1,80}:(?:\*)?[ \t]*)?(?:\*\*(?:[ \t]+|$))+",
            "$1$2");

        // Multi-dialog responses are stored with the speaker label. Remove
        // separator runs already present after labels so the model cannot
        // imitate and lengthen them on every subsequent turn.
        content = Regex.Replace(
            content,
            @"(?m)(^|\n)([^\r\n:]{1,80}:\s*)(?:[-\u2013\u2014]\s*){1,}",
            "$1$2");
        return Regex.Replace(
            content,
            @"(?m)^[ \t]*(?:[-\u2013\u2014][ \t]*){1,}",
            "");
    }

    private static NovelAIRequestException CreateException(
        UnityWebRequest request)
    {
        string detail = request.downloadHandler?.text;
        string message = request.responseCode switch
        {
            401 => "The NovelAI access token is invalid or expired.",
            402 => "The NovelAI account cannot make this generation.",
            429 => "NovelAI rate-limited the request.",
            >= 500 => "NovelAI is temporarily unavailable.",
            _ => $"NovelAI request failed ({request.responseCode}): " +
                 (string.IsNullOrWhiteSpace(detail) ? request.error : detail)
        };
        return new NovelAIRequestException(request.responseCode, message);
    }
}
