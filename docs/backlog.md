# Backlog — ideas past V1

> Status: **open list.** Nothing here is scheduled, and nothing here is a commitment.
>
> Relationship to the other documents: [spec-v1.md](spec-v1.md) is the contract for V1 and is
> closed — its §10 Open Items are questions *about V1*, and its non-goals list is what V1
> deliberately excluded. This file is the other thing: ideas that arrived *after* V1 shipped,
> including ones nobody has decided to build.

## Provenance legend

Same markers as the spec, for the same reason — so the next reader can tell what was actually
agreed from what someone merely suggested.

| Marker | Meaning | How to treat it |
|---|---|---|
| **`[A]`** | **Agreed** — decided explicitly in discussion. | Load-bearing. Do not reverse without discussion. |
| **`[D]`** | **Default** — conventional practice, filled in to make the entry actionable. | Low risk. Change freely. |
| **`[P]`** | **Provisional** — raised but not discussed, or an unverified guess. | Expect it to change. May be dropped entirely. |

An entry also carries a **state**: `Decided`, `Raised`, or `Done`.

---

## 1. Model selector as a dropdown from the live API

**State: Decided — not building it now.** Raised 2026-09-11.

### Decision `[A]`

**Keep the free-text model fields as they are.** `GeminiModel` and `SttModel` stay plain
`TextBox` controls in the settings window.

The requirement was never "a dropdown" — it was *use a newly released model without rebuilding
the app*. Free text already delivers that: `gemini-3.8-flash` ships, you type it, done. A
dropdown of forty entries is a worse control than a text box for a field that changes perhaps
twice a year, and it would trade a working property for a longer list.

### The idea, for whenever it earns its place `[P]`

An **editable** `ComboBox` — not a locked one — offering the live list while still accepting a
typed string. That keeps the no-rebuild property the current help text advertises and adds
discovery on top, rather than replacing one with the other.

The case for it is drift, not convenience. The spec's O1 recorded `gemini-3.7-flash` as
confirmed-present when V1 closed; a few weeks later the live list also carries `3.6-flash`,
`3.8-flash` and `3.5-transcribe`. A typo and a retired model id fail identically at runtime:
silently, mid-meeting.

### Already verified — do not re-investigate `[A]`

Checked live against both providers on 2026-09-11. Recorded here so a future session does not
repeat the work.

**Both lists are already being fetched and thrown away.**
[`ProviderCheck`](../src/MeetingAssist.Core/Configuration/ProviderCheck.cs) calls both providers
on every "Test" click:

- `GroqAsync` does a `GET` on `https://api.groq.com/openai/v1/models` and reads **only the
  status code**. The body is discarded.
- `GeminiAsync` calls `client.Models.ListAsync`, builds a `List<string> names`, and uses only
  `.Count` and a `.Contains()` check. The list is discarded.

So this feature is mostly *returning* data the app already asks for, not acquiring new data.

**Gemini** — `client.Models.ListAsync(new ListModelsConfig { QueryBase = true })`. Returned 40
models. Filter on `SupportedActions.Contains("generateContent")`, as
[`ModelsCommand`](../src/MeetingAssist.Spike/Commands/ModelsCommand.cs) already does; the
unfiltered list includes TTS, image (Nano Banana), music (Lyria) and robotics models.

**Groq** — `GET https://api.groq.com/openai/v1/models`, key in an `Authorization: Bearer`
header. Returned 14 models. Each record carries structured modality fields:

```json
{"id":"whisper-large-v3-turbo","input_modalities":["audio"],
 "output_modalities":["transcription"],"owned_by":"OpenAI","active":true}
```

Filter on `output_modalities` containing `transcription` — this reduces the 14 to exactly
`whisper-large-v3` and `whisper-large-v3-turbo`. Use that, **not** a `name.Contains("whisper")`
heuristic, which would break the first time Groq ships a non-Whisper STT model.

### Constraints for whoever builds it `[D]`

- **Editable, never locked.** A locked dropdown that fails to load is a field you cannot set.
- **Cache the last good list.** Offline, an empty dropdown is strictly worse than a text box.
- **It needs a valid key and a network** before it can populate, so the no-key path must still
  be usable.
- Confirm the `generateContent` filter is *sufficient*, not merely necessary — it may still
  admit models that make no sense as an answer model.

---

## 2. Raised in the 2026-09-11 code review, not yet discussed

**State: Raised.** All `[P]` — these came out of a review pass, not a conversation. Recorded so
they are not lost; none has been triaged, and any of them may be dropped.

| # | Idea | Why it came up |
|---|---|---|
| 2.1 | **Transcripts are write-only.** *(Viewer, export and delete done in §6; retention still open.)* `ReadSessions()` / `ReadSegments()` have no callers outside tests. | Every meeting accumulates in `%APPDATA%\MeetingAssist\sessions.db` forever with no viewer, export, delete, or retention policy. The write half shipped; the read half did not. |
| 2.2 | **The two network providers are untested.** `GroqTranscriber` (206 lines) and `GeminiAssistant` (185 lines) have no test file. | The network calls are not worth mocking, but the response parsing and error paths are where a provider's API change bites silently. |

---

## 3. Mock mode: an AI plays the other party

**State: Built** 2026-10-06. Raised the same day, to rehearse a meeting with the real
pipeline running.

### What was asked `[A]`

- Once enabled, **another AI acts as the other person in the call.**
- It gets **the same profile context** the cue-note assistant gets.
- An **extra description** for it is allowed: sample questions, style of questions. This is
  the profile's `mockBrief`.

### Decided `[A]`

- **Heard through a Windows voice for now, designed so a better voice can replace it.** The
  voice sits behind `IVoice`. `WindowsVoice` (System.Speech, SAPI) is the first
  implementation; a Gemini or other cloud voice is a new class, not a change to `MockCall`.
- **Turns are taken on a hotkey only** (`MockNext`, default `Ctrl+Alt+N`). There is no silence
  timer, so a pause to think is never talked over.

### Defaults filled in to build it `[D]`

- **The voice goes through the real pipeline.** It plays on the Windows default output, where
  the loopback channel captures and transcribes it like any caller. Nothing is injected into
  the transcript directly. This needs headphones: through speakers, the mic transcribes the
  voice as the user. A pinned loopback device that is not the default output will not hear it.
- **One counterpart line per press.** It reads the settled transcript (the same flush as an
  Ask), then speaks. A press mid-line stops that line and writes a new one. Stopping the
  session stops the line.
- **Its own Gemini client:** same model as the cue notes, `minimal` thinking, temperature
  0.9 (cue notes use 0.3).
- **The brief is per profile.** The voice is app-wide (`MockVoiceEngine`, `MockGeminiVoice`
  and `MockVoice` in `settings.json`). Since §6, whether a meeting is a rehearsal is chosen
  each time on Home, not stored. The brief is never sent to the cue-note assistant.
- **Off while playback mode runs.** The loopback channel is reading a WAV file then, so the
  voice could never reach the transcript.

### Found while building it `[A]`

- **The counterpart answered its own questions.** The user's side is labelled "You", which
  the model read as itself. Given a transcript ending on its own question, it gave
  the user's answer, using the prepared answers in the profile.
  - Relabelling the transcript `YOU`/`THEM` was not enough: 5 of 9 runs still failed.
  - What fixed it (12/12): `MockPromptBuilder` detects in code that the last line is the
    counterpart's, and tells the model the line is unanswered.
- **Measured** with `gemini-3.6-flash` and a real profile:
  - A line takes 1.2 to 2.1 s to generate, with each run a cold process.
  - The captured voice transcribed almost word for word; only product names came back
    misspelled.
  - Cancelling stops the voice at once, and the same voice speaks again afterwards.

### A better voice `[A]` / `[D]`

Asked the same day `[A]`: the Windows voice was hard to understand. What was built `[D]`:

- **`GeminiVoice`, now the default.** It uses `gemini-3.8-flash-lite-tts` with the prebuilt
  voice `Charon`; all 30 prebuilt voices are offered. The Windows voices remain a choice.
- **Streamed.** Audio plays as it arrives, through NAudio's `WasapiPlayer` on the default
  output, after 250 ms of pre-roll.
- **No language code is sent.** The model speaks the language of the line; a Spanish line
  came out in Spanish.
- **Fallback.** A line Gemini cannot speak at all goes to the Windows voice. That covers no
  key, quota, network, or no audio for 8 s. The overlay says so.
  - A failure after audio has started fails the turn instead: half a line and then the whole
    line in another voice is worse.

Measured 2026-10-06 `[A]`, for a two-sentence line (first audio / whole stream):

| Model | First audio | Whole stream |
|---|---|---|
| 3.8-flash-lite-tts | 0.65–1.1 s | 1.8–2.5 s |
| 3.8-flash-tts | 1.1–1.4 s | 4.2–4.6 s |
| 3.1-flash-tts-preview | 0.9 s | 3.4 s |
| 2.5-flash-preview-tts | 5–6 s | (does not stream) |

- **Played, captured on loopback and transcribed by Whisper,** the lines came back word for
  word.
  - That includes imperative lines like "Walk me through the pricing.": the model read them and
    did not answer them.
- **Free-tier quota.** About 11 TTS requests in under a minute hit a per-minute quota; it had
  cleared a minute later. One line per turn stays well under it. A burst of presses falls
  back to the Windows voice.

### Not done `[P]`

- Warming the counterpart's client at session start, as the cue notes do. Its first line
  pays the ~1.5 s cold start.
- A transcript or score of the rehearsal afterwards. The transcript is already saved like any
  session's (backlog 2.1: nothing reads it back yet).

---

## 4. Pause, overlay buttons, easier shortcuts

**State: Built** 2026-10-06.

### What was asked `[A]`

- **A pause**, to leave the PC and resume later with the context kept.
- **Easier shortcuts.** The user suggested Ctrl+key, or anything simpler that does not clash.
  On hearing why Ctrl+key would clash, they left the scheme to me: "set the shortcuts that
  you think are most convenient. Keep the existing ones if you think we can't improve them".
- **Buttons on the overlay for each action**, to click instead.

### Decided `[D]`

- **Pause stops capture and keeps the session.** The transcript, the session id and the
  stored record are kept. The capture time base is kept too, so segments after the pause sort
  after the ones before it.
  - Stop still ends the session, and Start still clears it.
  - Ask works while paused, on what was said before.
  - The mock's next line is refused while paused, because nothing would hear its voice.
  - Pause is refused in playback mode: a WAV source restarts from the beginning, so resuming
    would replay the meeting.
- **Why pause rather than leaving it listening.** Silence costs nothing, and Ask's window is
  anchored to the last segment, not the clock. But anything audible while away (the room, a
  notification, a video) was transcribed into the context.
- **Shortcuts stay on Ctrl+Alt.** These are global hotkeys, so Ctrl+letter would take
  select-all, refresh, print and so on away from every app.
  - Ask moved to `S` and Ask wide to `Shift+S`. Mock next line moved to `D`, and the new
    pause is `F`. All three sit next to the modifiers.
  - The overlay toggle moved from `A` to `W`, because AltGr+A types á on the UK layout.
  - Avoided because they type a character with AltGr on the installed layouts: Spanish `E`
    and `1`–`6`; UK `A`, `E`, `I`, `O`, `U` and `4`. Checked with `ToUnicodeEx`.
  - All the new combinations registered free on this machine.
- **Migration.** `hotkeys.json` gained a `version`. A binding still on a version-1 default
  moves to the new default; one the user changed is kept.
- **The buttons,** grouped by purpose `[D]`, with icons where one says it faster:
  - Session: Start/Stop (play/stop icon) and Pause/Resume (pause/play icon).
  - Cue notes: Ask | Wide as one segmented pair, in the accent colour, as it is pressed most.
  - Mock: Next line, shown in mock mode only.
  - Hide and Settings are icon-only, top right, beside the state.
  - Move was dropped `[D]`: the window is dragged anywhere on its body anyway.
  - They are not focusable, so a click never takes the keyboard from the meeting app.
  - Each tooltip names its hotkey.
- **The hotkey hint** names the shared modifiers once (`Ctrl+Alt + R start · S ask · …`) and
  wraps instead of being cut off `[A]`.
  - Left out: Quit (the tray has it, and a misclick mid-meeting would end the session) and
    Panic hide (it is one-way).

### Found while building it `[A]`

- **The flush could return before the flushed segment was picked up.** It checked
  "in flight" right after a single yield, and a segment not yet taken by the reader is not in
  flight. So an Ask could go out without the last sentence.
  - The pause test caught it: the sentence was lost to the cancellation that follows.
  - The fix: `Segmenter.Emitted` and the pipeline's count of taken segments; the drain waits
    for both to match.

### Verified `[A]`

- 150 tests pass, five runs in a row.
- On the running app, over UI Automation, Start, Pause, Ask while paused, Resume and Stop
  went through with the labels and enabled states following.
- Not verified: a real mouse click on the non-activating overlay. Posted mouse messages were
  ignored by WPF, so only UI Automation's invoke was exercised.

---

## 5. The transcript fell behind in a real meeting

State: **Decided**, built 2026-10-07.

### What happened `[A]`

A 56-minute meeting on 2026-10-07, on Groq's free plan. From the log:

- The transcript started falling behind 3 minutes in: 35 s by minute 5, 5 min by minute 25,
  about 20 min by the end.
- Groq's free plan allows 20 Whisper requests a minute, and the app sends one per speech
  segment. The segments were short (median 2.2 s on the mic, 3.7 s on the loopback), so the
  two channels needed about 17–18 a minute.
- Pacing at exactly 20 still drew 429s, and each retry used up part of the budget, so the
  real throughput was about 13 a minute. The backlog grew without limit.
- Every Ask waited its 1.5 s flush, gave up, and answered on whatever text it had. That text
  was minutes old, so the notes were about a topic long finished.
- The indicator stayed on Recording the whole time.
- Stopping cancelled everything still queued, so about 200 segments (the last 15–20 minutes)
  are missing from the saved transcript.

### Decided `[A]`

- **Stay on Groq and use the paid Developer plan.** It is about $0.04 per audio hour with a
  10 s minimum per request, so roughly $0.10–0.15 per meeting hour (my estimate). A streaming
  provider would not make the notes fresher: they only need the finished sentence. Switching
  would also mean a new connection and new failure modes.
- **A Groq plan setting** (Settings → Keys): Free is paced at 18 a minute, Developer at 360.
  - The 400-a-minute Developer cap comes from two independent write-ups, not from Groq's own
    page, which lists only the free plan `[P]`.
  - Groq's headers report the daily limit, not the per-minute one, so the plan cannot be
    detected; hence a setting.
- **Never answer on stale text silently.**
  - When the oldest untranscribed segment is more than 10 s old `[D]`, the session goes to
    Degraded. The status row says by how much ("Degraded · transcript 2 min behind").
  - An Ask still answers, but leads with a note saying how many lines are not in yet.
- **Newest first.** When behind, a freed request slot goes to the most recent segment, so the
  last minutes (what an Ask is about) stay current and older speech fills in afterwards.
- **Pause and stop finish the transcript.** Capture stops at once; what was already heard gets
  20 s `[D]` to be transcribed before it is given up on. The overlay says how many lines were
  lost, if any.

### Not done `[P]`

- **Joining short segments into fewer, longer requests.** It is the fix if the free plan has
  to stay. On the paid plan it would save cents and would delay the notes slightly.
- **A second provider as failover.** Revisit only if Groq itself proves unreliable.
- **Viewing past transcripts in the app.** They are stored in `sessions.db`, but nothing reads
  them back yet. The cue notes themselves are not stored.

## 6. A main window instead of a settings window

State: **Decided**, built 2026-10-07.

### What was asked `[A]`

- A window the app starts in, to choose the profile, start a meeting, and reach the settings.
- The settings inside that same window, looking the same, rather than a separate window with
  a different look.
- A list of past meetings: date and a name (the profile name).
- A display of warnings.
- Exporting a meeting's transcript to a text file: later.
- Then: a history of meetings where each transcript can be exported, in a format easy for a
  person or an AI chat to read (e.g. to ask how the meeting went and what the key points were).
  The app writes only the transcript, no report or summary.

### Decided `[A]`

- The app opens on the main window. Its **Home** section has:
  - the profile picker, with a link to edit the profile;
  - **Start meeting** and **Rehearse with mock call**. A rehearsal is chosen per meeting; mock
    mode is no longer a setting. The Rehearsal section keeps the voice and the brief;
  - the warnings: missing keys, the free Groq plan, hotkeys another app owns, storage problems,
    leftover plaintext keys;
  - past meetings: when, profile, length, lines. Meetings where nothing was transcribed are
    left out.
- The settings are sections in the sidebar of the same window, saved with one Save button.
- Starting a meeting hides the window and shows the overlay; stopping does the reverse. The
  overlay is hidden between meetings.
- The close button hides the window to the tray. Quit is in the tray menu, and the tray opens
  the window again. The window is hidden from screen sharing like the overlay.

### Defaults filled in to build it `[D]`

- Starting from the hotkey, the tray or the overlay is always a real meeting. A rehearsal is
  only started from Home.
- Choosing a profile makes it the one the next meeting uses at once; there is no Save on Home.
- Settings saved during a meeting apply when it ends.
- Starting with unsaved profile edits asks to save them first.
- After panic hide the window stays down when the meeting ends, until it is opened from the
  tray or the overlay.
- Playback mode being on is a warning too, so it is never on by surprise.
- A meeting whose end was never recorded (a crash) shows the length of its transcript.

### Export, defaults filled in to build it `[D]`

- History is a section of the main window, not a separate window, like the settings. Home
  shows the five most recent meetings and "See all" when there are more.
- Export writes `Documents\MeetingAssist\Transcripts\yyyy-MM-dd HHmm profile.txt` and opens
  the folder with the file selected. Exporting again replaces the file.
- The file is light Markdown: a header (date, length, profile, who the two speakers are), a
  note that it is an automatic transcription that can mishear, then one time-stamped turn per
  paragraph. Lines from the same speaker less than 10 s apart are joined into one turn.
- Speaker names are the profile's current labels; "You" and "Other side" if the profile is
  gone.
- Export is off during a meeting: Explorer is an ordinary window and would show in a screen
  share.

### Delete and meeting type, added 2026-10-08

- History deletes a meeting on request. `[D]` (asked for: "they grow indefinitely")
- Each meeting records whether it was a real meeting, a rehearsal or a playback run, shown as
  a TYPE column. `[D]` (asked for: rehearsals and real meetings told apart)
- Defaults filled in to build it `[A]`:
  - one meeting at a time, with a bin button on each row, after a confirmation naming the
    meeting; no multi-select, no "delete all";
  - deletion is permanent, with no undo or recycle bin; an exported file is left alone;
  - Delete is off during a meeting, like Export;
  - "Playback" is a third type, because a run on WAV files is neither of the other two;
  - meetings stored before 2026-10-08 show no type rather than a guessed one. An existing
    database gets the column on first open.

### Not done `[P]`

- Any retention policy (deleting old meetings automatically).
- Deleting several meetings at once, or filtering History by type.
- Copying a transcript to the clipboard instead of a file.

---

## 7. A generic meeting assistant, and profiles written with AI

State: **Decided**, built 2026-10-07.

### What was asked `[A]`

- The app is a generic meeting assistant, not a sales tool. Sales examples may stay in tests.
- Fewer profile fields where possible: simplicity is preferred.
- Profiles can be prepared with any AI, from a general document any AI can read, plus a
  Claude skill for the Claude app.
- The app will be published on GitHub as open source later; for now only the author uses it.

### Decided `[A]`

- Three content fields instead of four sales-shaped ones:
  - **This meeting**: the goal, who is attending and who they are, the agenda, a good outcome.
  - **About me**: what the user brings: role, experience, stories, figures.
  - **Likely questions, with my answers**: replaces Known objections.
- Vocabulary, language, speaker labels, answer style and mock brief are unchanged.
- The default answer style, the sample profile and the "Customer" label become generic.

### Defaults filled in to build it `[D]`

- Old profiles load into the new fields: Notes becomes This meeting; Company and Product,
  which described who the user represents, become About me; Objections becomes the questions.
  Saving writes only the new names.
- The default other-speaker label is "Other side", the same fallback the export uses.
- The default answer style's examples are a project check-in and a client call.
- `profile.sample.json` keeps its sales content, because `docs/test-script.txt` is read against
  it for the vocabulary A/B; only its field names changed.

### The guide and the skill, defaults filled in `[D]`

- One guide, `skills/meeting-profile/profile-guide.md`, written for any AI. The Claude skill
  (`SKILL.md` beside it) only adds how to use Claude's tools, so there is one source of truth.
- The AI hands back JSON in one code block. **Paste from AI** on the Profile page reads it from
  the clipboard, tolerating the code fence and chatter around it, and makes a new profile. It
  never overwrites one: a taken name is asked for again.
- The guide says: ask instead of inventing facts; leave decisions such as a salary figure to the
  user; keep vocabulary under 800 characters; leave the answer style out.
- A test parses every example in the guide with the app's own reader, so the guide cannot drift
  from the code.
- The skill description is under 200 characters: the Claude help center gives 200 as the
  limit and the platform docs 1024, so it fits both.

### Not done `[P]`

- Updating an existing profile from a paste; today a paste only adds.
- A packaged skill zip in a release, once the project is on GitHub.

---

## 8. Transcription accuracy: trying other providers

State: **On trial** in the spike, 2026-10-07. The app still uses Groq only.

### What was asked `[A]`

- Improve accuracy in meetings with non-native speakers of different nationalities. Switching
  provider is acceptable, as a last resort.
- Test the newer providers before deciding.

### Why `[A]`

The real 55-minute meeting of 2026-10-07 transcribed the other side well apart from names and
jargon, and the user's own side badly: "Thank you" 55 times, lone full stops, vocabulary
echoed back as speech. On the Artificial Analysis leaderboard (checked 2026-10-07) Whisper v3
Turbo scores 4.6% WER and the newest models about half that.

### Defaults filled in to build it `[D]`

- Two candidates: **xAI Grok Voice Transcribe 2.0** (2.3%, $0.10 an hour, released 2026-09-18)
  and **ElevenLabs Scribe v2** (2.2%, about $0.26 an hour with key terms, released 2026-01-09).
  StepAudio 3 ASR scores 1.7% but is built for Chinese and English; MAI-Transcribe-2 matches
  xAI's price but needs Azure. Neither was tried.
- `spike compare --fixture NAME` runs one fixture through every provider whose key is set and
  prints WER and how many of the profile's vocabulary terms came out exactly right.
- Their keys come from `XAI_API_KEY` and `ELEVENLABS_API_KEY` only, until one is adopted.
- The vocabulary is sent as separate key terms, split on commas; a term over 50 characters is
  dropped, as both providers would refuse it.
- xAI is asked for formatted numbers, as Whisper and Scribe write them, so the WER compares
  like with like. Scribe's audio-event tags are off.
- The retry, pacing and timeout rules moved from the Groq class into a shared base, so all
  providers behave alike.

### Open `[P]`

- How ElevenLabs takes a list of key terms in a multipart form is not in its reference; one
  field per term is assumed. A 422 on the first run would mean it is wrong.
- `fixtures/call1` is the user reading a script alone. It says how each provider does on the
  user's voice and microphone, not on accented speakers talking freely.
- If one is adopted: a provider choice and its key on the Keys page, provider-neutral warnings,
  and the Whisper-specific vocabulary counter.

---

## Done

Entries move here with the commit that closed them, so the reasoning stays findable after
the code stops showing it.

- **§7 Generic meetings, profiles written with AI**: built 2026-10-07.
- **§6 A main window**: built 2026-10-07, History and export the same day.
- **§5 The transcript fell behind**: built 2026-10-07.
- **§4 Pause, overlay buttons, easier shortcuts**: built 2026-10-06.
- **§3 Mock mode**: built 2026-10-06. The entry above stays where it is, because its "Not
  done" list is still open.
