# NovelAI Endpoint

Silverpine BepInEx mod that adds NovelAI as a hosted text-generation endpoint, including streaming responses, model selection, request pacing, and encrypted per-provider API-key storage.

Created by **Saelac and ChatGPT**.

**Current version:** 1.2.14

## Emotion metadata

Explicit `Emotion: happy`-style annotations are separated from generated dialogue and other non-expression answers before they can enter normal dialogue history. New NPC memory/happening writes and the dialogue-box display are also filtered. Inline and separate-line labels, common brackets/asterisks, quoted values, and short emotion phrases are supported; ordinary prose about feelings is preserved.

For dialogue, a short emotion hint from the accepted response is retained only in memory outside the game's save structures. It belongs to that NPC, is consumed by their next matching expression-selection question, expires after three minutes, and is cleared when a new dialogue generation starts. Labels beyond another speaker/role boundary do not supply a hint. The expression model still chooses among available sprites using the actual excerpt; the hint does not force a selection. This does not establish that hints improve expression accuracy.

Old saved memories are not rewritten on disk. Their emotion labels are stripped from copied prompt context and when shown in the dialogue box. Raw API diagnostics may still contain metadata. Other providers' behavior is unchanged when NovelAI is not selected.

## Dialogue turn guidance

The plugin snapshots the active NPC and the game's current multi-dialogue participant list before a request enters the queue. It repeats that conversation roster and turn instructions at the generation point. NPCs mentioned only in profiles, memories, relationships or nearby scenery are not added to the roster.

Version 1.2.14 promotes the latest tested generation-boundary wording: other participants are not speakers for the generated turn, speech belongs to the active NPC regardless of the most recently narrated actor, and nonverbal reactions are optional. The prompt says these are generation rules, not events to enforce through in-world interruptions. Its short example uses actual participant names and no fictional test-character occupations or shop facts; solo conversations do not gain an invented example participant. There is no instruction to physically return attention to the active NPC. Live synthetic testing was mixed (including ambiguous speaker switches and one compliance-narration leak); this is an in-game trial, not a proven fix.

Only the active NPC may speak during their turn. Other NPCs actually in the conversation may make brief, observable, nonverbal reactions only, within paired single asterisks. Consecutive actions and permitted reactions may share a single narration span, such as `*Kay waits for your reply. Mira nods.*` when Kay is active and Mira is a participant. A sentence boundary or change of described actor does not require closing and reopening the span. Close narration before spoken dialogue or the end of the reply; reopen it when narration resumes. An opening action still needs its opening asterisk. Instructions prohibit writing, quoting or paraphrasing another NPC's speech, including inside narration. They also discourage inventing absent participants or another NPC's independent turn and leave the player's actions and decisions to the player. This is prompt guidance, not a guarantee against invented details or participant mistakes.

There are **no actor or action-format quality checks and no retries for those issues**. Nonempty replies pass through ordinary text cleanup. A genuinely empty result after metadata/role-stop/cleanup removal gets one paced retry, then an error if still empty. API error handling and the game's ordinary expression/action/next-speaker questions are unchanged. Request delay and sampling settings are unchanged.

Speaker-label, leading dash/empty-star cleanup and role/stop boundaries remain. Valid single action markers are preserved, including immediately after the active character's speaker label. No first-meeting/odd-asterisk guessing is performed. Formatting mistakes can still appear or enter conversation history; existing saved conversations are not rewritten. The legacy `DialogueGuard.cs` is retained for offline regression comparisons but excluded from the installed assembly.

The model configuration lists `xialong-v1` and `glm-4-6` as the plugin's supported choices.

## Installation

Build the project and place `NovelAIEndpoint.dll` under `BepInEx/plugins/`. Configure the endpoint and API key through the in-game settings; do not store plaintext credentials in the repository.

## Building

The project targets `netstandard2.1` and requires the local Silverpine managed assemblies referenced by the project file.

`dotnet build -c Release` builds and installs the plugin into this workspace's BepInEx folder. Use `-p:SkipPluginInstall=true` for a build-only check. Restart Silverpine to load a newly installed build.

Run the isolated cleanup, metadata, prompt-policy and legacy validator regression checks with `dotnet run --project tests/DialogueGuard.Tests.csproj -c Release` (requires .NET 9). The legacy validator tests do not imply that runtime quality validation is enabled.

The opt-in `tests/LiveRoleplayProbe` project makes real, paid/allowance-consuming NovelAI requests using entirely synthetic Silverpine-style scenarios. Its optional `reinforced` argument retains an older, stricter experimental prompt overlay for comparison; it is not the installed reactive-participant policy.
