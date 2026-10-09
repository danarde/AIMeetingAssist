# Meeting Assist — V1 Specification

> Name: **MeetingAssist** (confirmed by the user).
> Status: **V1 implemented.** Every requirement below is either built, deferred in writing, or
> marked in §11.1 as awaiting a real meeting. The PoC that validated the engine is complete.
> The PoC is complete and every decision rule that could have forced a redesign is settled;
> see `docs/poc-findings.md`. Requirements below marked with a measurement are no longer
> provisional.
> Audience: spec-driven development. Anything not in this document is out of scope for V1.

## Provenance legend

Every decision and requirement below carries a marker. **Read it before treating anything
here as settled.**

| Marker | Meaning | How to treat it |
|---|---|---|
| **`[A]`** | **Agreed** — decided explicitly during design. | Load-bearing. Do not change without discussion. |
| **`[D]`** | **Default** — conventional practice, filled in to make the spec buildable. | Low risk. Change freely if implementation suggests better. |
| **`[P]`** | **Provisional** — an invented placeholder or unverified number. | Expect it to change. Where a value, the rationale states *why the knob exists*, not why that value is right. |

Unmarked prose is descriptive context, not a requirement.

---

## 1. Purpose

A Windows desktop application that provides live, glanceable assistance to the user during
online business meetings (sales, consulting, marketing). It continuously transcribes both
sides of the conversation, and on a hotkey press sends the recent conversation plus
pre-configured context to an LLM, which returns short cue notes displayed in an overlay that
is invisible to screen-sharing software.

The product is a teleprompter for the user's own reference. It is not an autopilot: it
produces **support material**, not scripted answers.

### Primary quality attribute

**Latency `[A]`.** Hotkey press to first readable bullet on screen is the metric that defines
whether the product is usable. Every design decision below defers to it.

### Deployment context `[A]`

Single user (the developer), personal use, own API keys, single monitor, headphones assumed.
No distribution, packaging, code signing, or auto-update in V1.

---

## 2. V1 Scope

### In scope

| Area | V1 behaviour | |
|---|---|---|
| Session | Manual start/stop. Recording and transcription run continuously while a session is active. | `[A]` |
| Audio | Two independent streams: microphone (user) and system loopback (remote party). | `[A]` |
| Transcription | Groq Whisper, VAD-aligned chunks, flushable on demand. | `[A]` |
| Transcript | Labeled, timestamped, in-memory for the session + SQLite persistence. | `[A]` |
| AI assistance | On hotkey: recent transcript + static context profile → streamed cue notes. | `[A]` |
| Overlay | Always-on-top, excluded from screen capture, hotkey-only control, never focusable. | `[A]` |
| Hotkeys | Six global hotkeys, user-configurable. | `[A]` |
| Context | Manually authored text profiles (company, product, objections, vocabulary). | `[A]` |
| Secrets | API keys encrypted at rest with Windows DPAPI. | `[D]` |
| ~~Cost meter~~ | **Deferred past V1** at the user's decision — measured spend is ~$0.08 per hour-long meeting, so a live readout costs more surface than the number is worth. | `[A]` |
| Test mode | WAV file playback through the real pipeline. | `[A]` |
| Instrumentation | Per-stage latency logging. | `[D]` |

### Explicitly NOT in V1 (non-goals)

Deliberate deferrals. Do not implement them, but do not architect in a way that blocks them.

- Speaker diarization (voice identification). Channel-based labeling only. `[A]`
- RAG, embeddings, vector stores, or any retrieval. Context is stuffed directly. `[A]`
- Rolling conversation summarization. Raw transcript only. `[A]`
- Post-meeting summaries or action-item extraction. `[A]`
- Multi-monitor support and per-monitor DPI handling. `[A]`
- Per-process loopback capture (capturing only `Teams.exe`). `[A]`
  *Cheaper than assumed: NAudio 3 exposes `.WithProcessLoopback(pid, mode)` (+ `BuildAsync`),
  so this is a small change rather than custom P/Invoke if loopback noise proves disruptive.*
- Fallback AI provider or automatic provider failover. `[A]`
- Provider safety-filter configuration (§4.6). `[A]`
- Local Whisper transcription — considered only if it beat cloud latency; analysis says it
  does not (D11). `[A]`
- Per-stream language selection in the UI. `[A]`
- A hotkey for cycling answer styles. `[A]`
- Installer, MSIX, code signing, auto-update, telemetry upload. `[A]`
- Consent notices, recording indicators, retention policy enforcement. `[A]`
- Automatic meeting detection / auto-start. `[A]`
- Overlay auto-hide on a timer — removed; the toggle hotkey covers it and a timer risks
  hiding an answer mid-read. `[A]`
- Programmatically setting Windows Do-not-disturb / Focus Assist. `[A]`
- **Cost meter (§4.11, FR-11.1–11.3, D23).** `[A]` Cut from V1 after the PoC measured spend at
  ~$0.08 per hour-long meeting. Nothing depends on it — no acceptance criterion references cost
  — and the data it needs (`ChannelPipeline.AudioSecondsSent`, `IAssistant.LastUsage`) is
  already collected, so re-adding it later is wiring rather than rebuilding.

---

## 3. Decision Log

| # | Decision | | Rationale |
|---|---|---|---|
| D1 | WPF on .NET 10 | `[A]` | User requirement. |
| D1a | Built-in Fluent theme (`ThemeMode`); WPF-UI only if a control is genuinely missing | `[D]` | Modern look with no third-party dependency. |
| D2 | Screen-capture exclusion via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | `[A]` | Removes the window from Windows Graphics Capture and DXGI Desktop Duplication, which is what Teams/Zoom/Meet use. |
| D3 | Hotkey-only control; overlay never activated or clicked | `[A]` | The mouse cursor and focus changes are the two leaks display affinity cannot hide. Eliminating clicks eliminates both. |
| D4 | Headphones assumed | `[A]` | Removes speaker-to-mic bleed, keeping channel-to-speaker mapping clean. Documented as a usage requirement, not enforced. |
| D5 | Groq `whisper-large-v3-turbo` as the V1 STT provider | `[A]` | Already validated by the user. Cost is negligible at this volume, so accuracy and latency drive the choice. |
| D6 | STT interface is push-frames / raise-events, not transcribe-a-file | `[D]` | Keeps true streaming providers (Deepgram, AssemblyAI) viable as drop-in replacements without reshaping the pipeline. Costs nothing now. |
| D7 | Segmenter supports explicit `FlushAsync()` | `[A]` | Chunk duration is the dominant latency term. Flushing on hotkey removes it at exactly the moment it matters. Without this, the sentence being asked about is the one missing from the transcript. |
| D8 | Chunks are VAD-aligned; target length 2–3 s | `[P]` | ~2 s is believed to be the practical floor — Whisper is trained on 30 s windows and degrades on short, silence-heavy segments. **The value is unverified; tune from spike data (O2).** |
| D9 | Whisper `prompt` parameter is a **manually authored** vocabulary string | `[A]` | Vocabulary priming is the largest accuracy win on proper nouns. Manual authoring avoids an extraction subsystem. Hard cap ~224 tokens. |
| D10 | Never feed prior transcript into the Whisper `prompt` | `[A]` | Known failure mode: mis-transcriptions propagate and can spiral into repeated/hallucinated output. |
| D11 | Local Whisper excluded | `[A]` | It still batches — same 30 s-window model — so the dominant latency term is unchanged. It would be a privacy option, not a speed option, and privacy is not a V1 goal. |
| D12 | Speaker labels are semantic (`You` / `Customer`) | `[A]` | Better LLM output for zero cost. |
| D13 | No rolling summary; send raw transcript | `[A]` | A one-hour two-person meeting is ~11k tokens. Sending it whole costs cents. |
| D14 | Prompt ordered stable-prefix-first for implicit caching | `[A]` | Gemini caches on exact token prefixes automatically. Correct ordering is free latency. |
| D15 | Context is manually pasted text profiles; no RAG | `[A]` | Realistic context is 5k–50k tokens against a ~1M window. Stuffing beats retrieval whenever everything fits. |
| D16 | Hard output length constraint, enforced by prompt + few-shot | `[A]` | A user in a live meeting can read ~15 words. Verbosity is the primary failure mode of the product. |
| **D17** | **Gemini via the official `Google.GenAI` SDK, surfaced as `IChatClient` through `.AsIChatClient(modelId)`** | `[A]` | **Researched, see §9.1.** Official Google SDK; supports the Developer API with a plain API key (no GCP project); native streaming; native `SafetySetting`; and first-party `Microsoft.Extensions.AI` interop. Gives the swappable abstraction *and* full native feature access with no escape-hatch rewrite. |
| D18 | Model ID is a plain configurable string, never an enum | `[D]` | Model names change faster than the app. No code change to adopt a new model. |
| D19 | Safety-filter configuration deferred | `[A]` | Pure scope choice. **Note:** this is no longer a technical constraint — `Google.GenAI` exposes `SafetySetting` in `GenerateContentConfig` directly, so enabling it later is a few lines, not a rework. |
| D20 | No fallback provider, but blocked/empty responses handled gracefully | `[A]` | A blank overlay mid-meeting is the failure that actually hurts. One retry plus a visible status line is cheap. |
| D21 | Single session language in the UI, stored per-stream internally | `[A]` | Simple UI now; per-stream language later is a UI change rather than an architecture change. |
| D22 | API keys stored via Windows DPAPI (`CurrentUser` scope) | `[D]` | Correct weight for a local single-user app: no master password, tied to the Windows account. |
| D23 | Cost meter computed locally from provider-reported usage | `[A]` | Providers return real token/duration counts; only the price table is hand-maintained. |
| D24 | WAV playback mode feeds the real pipeline | `[A]` | The pipeline cannot be iterated on by scheduling live meetings. |
| D25 | Per-stage latency instrumentation from day one | `[D]` | Makes tuning empirical instead of speculative. Small effort, large payoff given D-primary-attribute. |
| **D26** | **Three projects, not eight; provider swappability comes from interfaces + DI** | `[A]` | Project boundaries were never what enabled provider swapping — the interface plus its DI registration is. Eight `.csproj` files buy compiler-enforced boundaries across a team, a problem a single developer does not have. |
| D27 | Whole-session transcript held in memory; no ring buffer | `[A]` | A full meeting is kilobytes of text. A ring buffer solved a memory problem that does not exist, at the cost of eviction logic and a size knob. |

---

## 4. Functional Requirements

### 4.1 Session lifecycle

- **FR-1.1** `[A]` A session is started and stopped explicitly by the user (UI button and hotkey).
- **FR-1.2** `[A]` While a session is active, both audio streams are captured and transcribed
  continuously, without user action.
- **FR-1.3** `[A]` The app must never raise a Windows toast notification while a session is
  active. A toast *is* captured by screen sharing, so this is a correctness requirement.
  Status is communicated only through the overlay's own indicator (FR-7.6).
  *The app does not touch the system Do-not-disturb setting.*
- **FR-1.4** `[A]` On session stop, the transcript is persisted and in-memory state cleared.
  **Amended in implementation** `[D]`: persistence happens continuously rather than at stop
  (FR-5.4), and the in-memory transcript is cleared on the *next* `Start`, not on `Stop`.
  Clearing at stop would discard the transcript the user may still want to Ask about in the
  minutes after a call ends, which is a real use and costs nothing to keep. The guarantee that
  matters — that no meeting can ever see another meeting's transcript — is unchanged, and is
  covered by a regression test.
- **FR-1.6** `[D]` Each session carries an id and a UTC start time, assigned at `Start`. Every
  persisted segment is keyed to it, so two meetings in one app run are never conflated.
- **FR-1.5** `[D]` Only one session may be active at a time.

### 4.2 Audio capture

- **FR-2.1** `[A]` Capture the default microphone via NAudio 3's `WasapiRecorderBuilder`,
  consumed through `CaptureAsync(ct)`. Use `.WithDefaultDeviceStreamRouting()` so a device
  change mid-session is handled by the API rather than by us.
- **FR-2.2** `[A]` Capture system output with the same builder plus `.WithLoopbackCapture()`.
- **FR-2.3** `[A]` The two streams are fully independent end-to-end: separate capture,
  segmentation, transcription, and channel label.
- **FR-2.4** `[A]` Both streams are captured **directly** at 16 kHz, mono, 16-bit PCM via
  `.WithFormat(new WaveFormat(16000, 16, 1))`. **No resampling stage exists.** Shared mode
  honours the requested format through the audio engine's AutoConvertPcm.
  *Verified on the target machine: negotiation succeeds on both mic and loopback even though
  every endpoint runs at 48 kHz stereo 32-bit float (`spike devices --check`).*
  Implementations must still assert the negotiated format and fail loudly, since a silent
  mismatch would make every downstream byte a misinterpretation.
- **FR-2.5** `[D]` Device selection is configurable; default devices used if unset.
  *Implemented in the settings window's Devices tab, backed by `Core/Audio/AudioDevices.cs`.
  Selections are stored as WASAPI endpoint ids, which survive reboots and driver renames.*
  **Trade-off, recorded because it is not reversible in the UI** `[D]`: pinning a device is
  mutually exclusive with `WithDefaultDeviceStreamRouting` (FR-2.1) — NAudio rejects the
  combination — so a pinned device **forfeits automatic recovery** when Windows switches
  devices. A pinned device that is not connected at session start falls back to the Windows
  default rather than failing, and says so in the log. Leaving both on default is the
  recommended configuration; the recovery FR-2.6 requires is what has to cover the rest.
- **FR-2.6** `[A]` A device disappearing or changing mid-session must not crash the app.
  Surface a degraded status and attempt to re-acquire.
  *Implemented in `MeetingSession.WatchAsync`: the failure is reported **immediately** and the
  rebuilding happens behind it, so the indicator never claims health the session does not have.
  Up to `DeviceRetryAttempts` rebuilds with doubling backoff from 1 s, capped at 15 s. A rebuilt
  channel counts as recovered only after it survives `DeviceSettleTime` — capture devices fail
  almost instantly when they fail, so surviving that window distinguishes a recovery from a
  retry loop. Each channel is tracked separately: one dead microphone is degraded, not stopped,
  and the far end keeps transcribing.*
- **FR-2.7** `[D]` Audio callbacks must never touch the UI thread. Inter-stage handoff uses
  `System.Threading.Channels`.
- **FR-2.9** `[A]` Consume capture via `CaptureAsync`, whose buffers are heap-allocated copies
  safe to retain. The zero-copy `DataAvailable` event hands out a span valid only for the
  callback and must not be used to feed the pipeline.
  **Gotcha:** calling `StartRecording()` without consuming buffers deadlocks — the capture
  thread blocks and `StopRecording()` never returns. Observed during the PoC.
- **FR-2.8** `[A]` Raw audio is **not** written to disk in V1.

**Known limitation (accepted `[A]`):** the loopback stream is the whole system output mix, so
non-meeting audio (media playback, system sounds) is attributed to `Customer`. Mitigated by
VAD gating only. Per-process loopback is the V2 fix.

### 4.3 Voice activity detection and segmentation

- **FR-3.1** `[A]` Each stream is segmented into chunks cut at VAD-detected silence boundaries.
- **FR-3.2** `[A]` Target chunk length **2 s**, with a hard maximum of 8 s.
  *Measured:* 2 s / 3 s / 4 s / 6 s minimum chunk gave 15.1% / 15.9% / 15.5% / 18.8% WER on a
  112-second accented reading of a proper-noun-dense script, with zero empty responses at any
  setting. 2–4 s are within noise of each other and 6 s is measurably worse, so the shortest
  useful chunk is also the most accurate — short chunks were the feared failure and were not.
  *Why the maximum exists:* without it, continuous speech produces an unboundedly long chunk,
  causing a large latency spike and degraded Whisper output. *The 8 s value itself is still a
  guess*, though it fired on only 1 of 24 segments in the fixture.
- **FR-3.3** `[A]` Segments containing only silence/noise are discarded, never sent to STT.
- **FR-3.4** `[A]` The segmenter exposes `FlushAsync()`, closing and emitting the open chunk
  immediately regardless of length.
- **FR-3.5** `[P]` A flushed chunk shorter than 300 ms is discarded rather than sent.
  *Why the floor exists:* Whisper hallucinates on very short fragments, so a sub-threshold
  flush would inject garbage into the transcript at the worst moment. *Value is a guess.*
- **FR-3.6** `[D]` V1 may use an energy-threshold VAD. The interface must allow substituting a
  model-based VAD (e.g. Silero via ONNX Runtime) without changing callers.

### 4.4 Transcription

- **FR-4.1** `[D]` Transcription is performed behind an interface (Appendix A) so the provider
  can be replaced by DI registration alone.
- **FR-4.2** `[A]` V1 provider: Groq `whisper-large-v3-turbo` via
  `POST https://api.groq.com/openai/v1/audio/transcriptions`. The model id is configuration,
  not code: `whisper-large-v3` is the more accurate alternative (10.3% vs 12% published WER)
  at identical free-tier quota, and the comparison is still open (findings, open item 1).
- **FR-4.3** `[A]` Each request sends: the audio chunk, model ID, session `language` as
  ISO-639-1, the profile's `Vocabulary` string as `prompt`, and `temperature=0`.
- **FR-4.4** `[A]` The `language` parameter is always sent explicitly. Auto-detection is not
  used — short chunks are unreliable to detect and detection can flip mid-session.
- **FR-4.5** `[A]` The `prompt` parameter carries **only** the manually authored static
  vocabulary from the active profile. Never prior transcript (D10).
- **FR-4.6** `[A]` Requests for the two streams are issued concurrently and independently.
- **FR-4.7** `[D]` Transient failures (timeout, 5xx) retry with exponential backoff and jitter,
  bounded so a chunk is abandoned rather than delaying the pipeline indefinitely. On **429 the
  server-stated delay is honoured** — `retry-after` header, falling back to parsing the delay
  from the error body. *Measured:* a fixed backoff waited ~1.4 s against a stated 3 s and gave
  up, losing the chunk.
- **FR-4.8** `[A]` A failing STT provider must not stop capture or segmentation, so the
  session survives a transient outage.
- **FR-4.11** `[D]` **A total STT outage must be visible.** `TranscribeAsync` returns null for
  both "nothing was said" and "the provider is unreachable", which makes an outage look exactly
  like a quiet room — a healthy indicator over an empty transcript. `ITranscriber` therefore
  raises `AttemptCompleted(bool)` per segment, and the session reports itself degraded after
  `TranscriptionFailureThreshold` consecutive failures (default 4, so one dropped chunk does not
  flicker the indicator) and recovers on the first success.
- **FR-4.9** `[D]` Out-of-order responses are reordered by chunk start timestamp before
  insertion into the transcript.
- **FR-4.10** `[A]` Requests are **paced client-side beneath the provider's rate limit**, with
  one budget shared across both channels and retries counted against it. Groq caps Whisper at
  **20 requests/minute on every plan** (2,000/day, 28,800 audio seconds/day), and a live
  two-channel meeting produces roughly 15–25 requests a minute on its own — so this is a
  standing constraint of the design, not a testing artifact. Waiting for a slot is strictly
  better than being refused: a 429 costs the round trip *and* loses the chunk.
  *Measured:* without pacing, a four-configuration sweep dropped 53 of ~106 requests. With it,
  zero. The cap is per key; a second concurrent user would exhaust it immediately.
  The per-request timeout starts **after** a slot is acquired, never before: it bounds the
  provider, not the queue. *Fixed 2026-10-06:* it used to start first, so a segment paced
  for longer than the 20 s timeout expired unsent, spending a slot and an attempt each time.
  Seen in a fixture replay (three to four segments needed a second attempt after a ~58 s
  wait); unlikely at live-meeting rates, but it fed the backlog that caused it.

### 4.5 Transcript store

- **FR-5.1** `[A]` The full session transcript is held in memory (D27).
- **FR-5.2** `[D]` Each segment records: id, channel, UTC start, UTC end, text.
- **FR-5.3** `[A]` The store returns a time window of segments in chronological order,
  interleaved across both channels by start time.
- **FR-5.4** `[A]` Segments are persisted to SQLite as they finalize, keyed by session.
  *Implemented in `Core/Persistence/SessionRepository.cs`: two tables (`sessions`, `segments`)
  at `%APPDATA%\MeetingAssist\sessions.db`, written by a single serializing consumer so the two
  channel pipelines never touch SQLite concurrently and never pay disk latency on the audio
  path. Storage failure degrades to "this meeting is not being saved" and never ends a session.*
  **Storage only** `[A]` — the user's decision for V1 is to keep transcripts, not to browse or
  export them, so there is no reading UI. The read methods on the repository exist for tests.
- **FR-5.5** `[A]` Channel-to-label mapping is configurable per profile; defaults
  `Mic → "You"`, `Loopback → "Customer"`.

### 4.6 AI assistance

- **FR-6.1** `[A]` On the Ask hotkey the orchestrator must, in order:
  1. Call `FlushAsync()` on both segmenters.
  2. Await the resulting transcription, bounded by a timeout `[P]` (proceed without it on
     expiry rather than blocking). *Why the timeout exists:* a hung STT request must not
     stall the Ask indefinitely. *Value TBD from spike data.*
  3. Build the prompt per §8.
  4. Stream the response to the overlay.
- **FR-6.2** `[A]` Two query windows: **narrow** (default ~60 s) and **wide** (~3 min), each
  on its own hotkey. Both durations configurable.
- **FR-6.3** `[A]` Response tokens are rendered as they arrive. The user must not wait for
  completion to begin reading.
- **FR-6.4** `[A]` V1 provider: Gemini via `Google.GenAI`, consumed as `IChatClient` (D17).
  Model ID is a configuration string (D18).
- **FR-6.9** `[A]` **Model thinking must be disabled.** Gemini 3.x reasons by default, which
  costs ~4 s of added latency and consumes `MaxOutputTokens`, truncating the answer. Set
  `ThinkingConfig.ThinkingLevel = Minimal`, passed through `ChatOptions.RawRepresentationFactory`
  so `IChatClient` remains the seam. Keep `MaxOutputTokens` generous anyway — suppression is
  not perfectly deterministic.
  *Measured: 5832 ms and a 5-token answer with thinking; 1704 ms and 5 clean bullets without.*
  *Amended 2026-10-06:* V1 originally sent `ThinkingBudget = 0`. Google documents
  `thinkingLevel` as the Gemini 3 control, and `3.5-flash-lite` rejected the budget on every
  new client, costing one failed request before the first answer. `Minimal` is accepted by
  both `3.5-flash-lite` and `3.6-flash`, with zero thinking tokens on `3.6-flash` across four
  live runs.
- **FR-6.10** `[A]` Model choice is the dominant latency lever, not STT.
  *Measured first-token: `gemini-3.5-flash-lite` ~870 ms, `gemini-3.1-flash-lite` ~900 ms,
  `gemini-3.5-flash` ~1.1-1.5 s, `gemini-3.7-flash` ~1.75 s, against a Groq STT stage of
  388 ms.* **Default is `gemini-3.6-flash`** (changed 2026-10-06; V1 shipped with
  `gemini-3.5-flash-lite`). Compare with `spike ask --transcript` before changing.
  *Why it changed:* on the same two-party transcript, ending in a question the profile only
  partly answers, both Flash-Lite models stated review timelines the profile never gave them
  ("takes days", "two weeks") in most runs; `3.6-flash` invented nothing in seven. The cost is
  ~0.5 s: first token 1.35–1.9 s against 0.84–1.3 s for `3.5-flash-lite`, both measured as a
  fresh process per run, so warm figures should be lower. `3.8-flash` is unsuitable: it
  rejects `Minimal`, and at `Low` still thought for ~490 tokens in some runs (2–7 s, answer
  truncated). A stored `geminiModel` overrides this default, so an existing install keeps its
  model until it is changed in Settings.
- **FR-6.11** `[A]` Thinking-config support varies **within** a provider's model family:
  `gemini-3.5-flash-lite` rejected `ThinkingBudget = 0`, and `3.7-flash` / `3.8-flash` reject
  `ThinkingLevel = Minimal`. Sending the config must therefore be recoverable — on an
  invalid-argument rejection, retry rather than failing the Ask. A rejected `Minimal` steps
  to `Low`; anything else rejected drops the config. Dropping `Minimal` outright would leave
  those models on their `medium` default, the behaviour FR-6.9 exists to prevent.
- **FR-6.12** `[A]` **Warm the assistant and STT clients at session start**, not on first
  Ask. *Measured cold-start penalty ~1.5 s* (3378 → 1704 ms over consecutive runs), which
  would otherwise land entirely on the user's first question of a meeting.
- **FR-6.5** `[A]` The prompt is assembled in the fixed block order of §8.1 so the stable
  prefix is byte-identical across requests, enabling implicit caching. No timestamp, session
  id, or other varying value may appear before the transcript block.
- **FR-6.6** `[A]` If a response is empty, truncated, or blocked, retry once. If the retry
  also fails, the overlay shows a single-line status (e.g. `No answer — press Ask again`).
  A blank overlay is never an acceptable outcome.
- **FR-6.7** `[A]` One default answer style in V1: short cue notes. The style is an editable
  prompt template on disk, not a hardcoded string, so it can be tuned without a rebuild.
  **Amended** `[A]` — the user's decision is that it is edited **in the settings window, as a
  field on the profile**, not as a separate hand-edited file. It therefore saves and switches
  with the profile, so a sales profile and a consulting profile can answer differently.
  **Blank means the built-in default** (`PromptBuilder.DefaultStyle`), which makes clearing the
  box the way out of a bad edit. It is captured once at session start, never per Ask, so
  FR-6.5's byte-identical prefix survives an edit made while a meeting is running — that edit
  takes effect at the next session.
- **FR-6.8** `[A]` Output is hard-capped: **maximum 5 bullets, target ≤8 words each**.
  Enforced by prompt instruction plus few-shot examples only — **amended**: the client-side
  truncation backstop originally specified here (`PromptBuilder.EnforceBulletCap`) was built and
  tested but never wired into the app, since `MeetingSession.AskAsync` streams chunks straight
  through. It was removed as dead code rather than connected, on the view that a model
  disregarding a five-bullet instruction with worked examples is not the risk worth building a
  second line of defence for. If it recurs in practice, re-add truncation as a streaming filter,
  not a post-hoc string cap — the removed version operated on the whole finished answer.

**Deferred (D19):** safety-filter configuration. If hedging or refusals prove to be a
practical problem, mitigations in order: (1) tighten prompt framing per §8.2, (2) add few-shot
examples demonstrating non-hedging output, (3) pass `SafetySetting` values through
`GenerateContentConfig`. All three are small changes — none is architectural.

### 4.7 Overlay window

- **FR-7.1** `[A]` Borderless, always-on-top, semi-transparent, positioned in a screen corner.
- **FR-7.2** `[A]` `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` applied on creation.
- **FR-7.3** `[A]` The return value of FR-7.2 **must be checked**. On failure the app shows a
  clear, persistent warning: the core promise of the product is broken and the user must know
  before sharing their screen.
- **FR-7.4** `[A]` Extended styles `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `WS_EX_TRANSPARENT`;
  `ShowInTaskbar = false`. The window must never take focus and must not appear in Alt+Tab.
- **FR-7.5** `[A]` Typography readable at a glance: large font, high contrast, generous line
  spacing. Font size configurable.
- **FR-7.6** `[A]` A compact status indicator shows session state:
  `Idle | Recording | Thinking | Degraded | Error`.
  *Decided in one place, `MeetingSession.RefreshState`. `Thinking` is a flag held for the
  duration of an Ask rather than a saved-and-restored previous value: the save/restore version
  pinned the indicator on `Thinking` after a failed Ask, and lost a health change that arrived
  while one was streaming. `Recording` versus `Degraded` is derived from live health — per-channel
  capture and consecutive transcription failures — so the indicator cannot disagree with the
  session.*
- **FR-7.7** `[A]` The overlay is shown and hidden **only** by explicit hotkey. It never hides
  itself on a timer.
- **FR-7.8** `[A]` Position and opacity cycle through presets via hotkey; the choice persists
  across sessions.

### 4.8 Hotkeys `[A]`

Registered globally via `RegisterHotKey`. All configurable, in the settings window or in
`%APPDATA%\MeetingAssist\hotkeys.json`.

| Action | Default | Behaviour |
|---|---|---|
| Ask (narrow) | `Ctrl+Alt+G` | Flush, query the narrow window (default 60 s), stream answer. |
| Ask (wide) | `Ctrl+Alt+Shift+G` | Same, wide window (default 3 min). |
| Toggle overlay / repeat last | `Ctrl+Alt+A` | Show last answer if hidden, hide if shown. |
| Panic hide | `Ctrl+Alt+X` | Immediately hide every window. Never toggles back. |
| Cycle position + opacity | `Ctrl+Alt+P` | Advance to next preset. |
| Start / stop session | `Ctrl+Alt+R` | Toggle session. |
| Quit | `Ctrl+Alt+Q` | Stop the session cleanly and exit. |

**Seven, not six** `[D]`. Quit was added during implementation: the overlay never takes focus,
has no chrome, and panic hide is deliberately one-way, so without it the only way to end the app
was Task Manager. The tray menu now offers it too, but the hotkey remains the one that works
when the overlay is hidden.

**Ask is `Ctrl+Alt+G`, not the `Ctrl+Alt+Space` this table originally specified** `[A]`.
`Ctrl+Alt+Space` fails to register on the author's machine with
`ERROR_HOTKEY_ALREADY_REGISTERED`; what claims it was not identified. This is what closes O5 in
the direction that matters — collisions are the normal case, not the edge case, which is why
every binding is configurable and every failure is surfaced rather than swallowed.

- **FR-8.1** `[A]` `RegisterHotKey` returns false when a combination is already claimed. Every
  registration is checked at startup and failures surfaced in the UI. Discovering a dead
  hotkey during a live meeting is unacceptable.
- **FR-8.2** `[D]` Defaults must avoid known Teams/Zoom/Meet shortcuts (**O5 — closed**: a
  collision was found and worked around, and rebinding is a first-class feature rather than a
  fallback).
- **FR-8.3** `[A]` Hotkeys must work while another application (PowerPoint, Teams) holds focus.
- **FR-8.4** `[A]` Panic hide is strictly one-way. It must never be the key that reveals the
  overlay.

### 4.9 Context profiles

- **FR-9.1** `[A]` A profile is a named, user-authored set of text fields:

  | Field | Purpose |
  |---|---|
  | `Name` | Identifier. |
  | `Company` | Who the user represents. |
  | `Product` | What is being sold or discussed. |
  | `Objections` | Known objections and preferred responses. |
  | `Notes` | Free-form meeting context, attendees, agenda. |
  | `Vocabulary` | **Manually written**, ≤224 tokens. Proper nouns, product names, jargon, attendee names. Fed verbatim to Whisper's `prompt`. |
  | `Language` | ISO-639-1 for the session (STT input and AI output). |
  | `Labels` | Channel-to-speaker-name overrides. |

- **FR-9.2** `[D]` Profiles are editable in-app and stored as JSON.
  *`App/Settings/SettingsWindow.xaml`, backed by `Core/Profiles/ProfileLibrary.cs`; one file per
  profile under `%APPDATA%\MeetingAssist\profiles`. A single pre-library `profile.json` is
  adopted as `default` on first run rather than replaced by a sample.*
- **FR-9.3** `[A]` One profile is active per session, selected before starting.
  *The picker sets the profile for the **next** session; changing it mid-meeting would change
  the prompt prefix underneath a running conversation, so a save during a session is applied to
  files but not to the live session.*
- **FR-9.4** `[A]` `Vocabulary` is a distinct field from the AI context blocks. It is *not*
  derived from them and is never sent to the LLM.

### 4.10 Secrets

- **FR-10.1** `[D]` API keys encrypted with
  `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`, stored under `%APPDATA%`.
  *Implemented in `Core/Configuration/SecretStore.cs`; the blob is
  `%APPDATA%\MeetingAssist\secrets.dat`. Because the scope is the Windows account, copying the
  file to another machine or user yields nothing.*
- **FR-10.1a** `[D]` **Migration.** An existing plaintext `secrets.local.json` is imported on
  first run, written encrypted, **read back and compared**, and only then deleted — deleting
  before verifying would trade a privacy problem for a data-loss one. If any step fails the
  plaintext file is left in place and the overlay says so, because a plaintext copy the user
  believes was removed is the worst of both outcomes. That path remains the supported way to
  enter a key until the settings window (FR-9.2) exists; environment variables still win over
  the store, so a one-off override needs no UI.
- **FR-10.1b** `[D]` An unreadable or corrupt store leaves the app **keyless, not dead**: it
  starts, reports the degradation, and can be repaired in place.
- **FR-10.2** `[A]` Keys are never written to `appsettings.json`, never committed, never
  logged. Development uses `dotnet user-secrets`.
- **FR-10.3** `[A]` All logging redacts key material. HTTP logging must not emit authorization
  headers.
- **FR-10.4** `[A]` Keys are passed in headers only, never in URLs or query strings.
- **FR-10.5** `[D]` The UI masks keys on display and offers a per-provider validity check.
  *The check itself is `Core/Configuration/ProviderCheck.cs` — a model listing per provider,
  the cheapest authenticated call each offers, which proves the key, the network and the
  endpoint together. Masking is `SecretStore.Mask`. The buttons that call these arrive with the
  settings window.*

### 4.11 Cost meter — **DEFERRED past V1** `[A]`

> Cut at the user's decision; see the non-goals list. The requirements below are kept as written
> so re-adding it does not mean re-deciding it. **Do not implement them in V1.**


- **FR-11.1** `[A]` Per-session estimate accumulated from provider-reported usage: token
  counts from the AI response, audio duration from STT.
- **FR-11.2** `[A]` Unit prices live in a config table, editable without a rebuild.
- **FR-11.3** `[A]` Displayed as an estimate. It is not billing data.

### 4.12 Playback test mode

- **FR-12.1** `[A]` The app can substitute a pair of WAV files for the two live capture
  devices, feeding the real pipeline unchanged from segmentation onward.
- **FR-12.2** `[D]` Playback runs in real time by default, with an option to run as fast as
  the pipeline allows for throughput testing.
- **FR-12.3** `[A]` Available headless (console) so pipeline work needs no WPF.

### 4.13 Instrumentation `[D]`

- **FR-13.1** Each chunk carries a correlation id, timestamped at every stage:
  captured → chunk closed → request sent → response received → transcript updated.
- **FR-13.2** Each Ask is timestamped: hotkey → flush complete → prompt built → first token
  → last token.
- **FR-13.3** Stage timings are logged structurally so p50/p95 can be computed from a session
  log.
- **FR-13.4** The latency budget (§5.1) is verified against these logs, not by feel.
- **FR-13.5** `[A]` Time spent queued behind a provider's rate limit (FR-4.10) is recorded as a
  **separate stage**, never folded into that provider's measured latency. Conflating them
  reports the opposite of what the number means and would corrupt §5.1.

---

## 5. Non-Functional Requirements

### 5.1 Latency budget — targets vs measurements

The target column was invented to give the spike an exit criterion. The measured column is
real, taken from `spike ask`, `spike transcribe` and `spike sweep` against the live APIs. The
end-to-end figure is composed from stage measurements; `spike live` confirmed it qualitatively
in real conversation but was not instrumented end to end.

| Stage | Target p50 | **Measured p50** |
|---|---|---|
| Flush → chunk closed | 20 ms | ~0 ms |
| Chunk → STT response | 400 ms | **268–388 ms** (p95 587 ms) |
| Prompt build | 30 ms | ~0 ms |
| LLM first token | 1000 ms | **~870 ms** (`3.5-flash-lite`) to **1750 ms** (`3.7-flash`); **1.35–1.9 s** (`3.6-flash`, default since 2026-10-06, cold process) |
| **Hotkey → first bullet visible** | **≤2000 ms** | **~1.26 s** with `3.5-flash-lite`; **~1.8–2.2 s estimated** with `3.6-flash`, at the target — composed from stages, not yet measured live |

Background transcript lag (word spoken → appears in transcript, absent a flush): p50 ≤2 s.

**Measured latency excludes rate-limit pacing.** Time queued behind the provider's cap
(FR-4.10) is not provider response time; recording it as such put `stt p95` at 59 s when the
request itself took 300 ms. See FR-13.5.

**Correction from the PoC:** this section assumed STT would dominate. It does not. Measured
Groq STT is 388 ms p50 / 395 ms p95, while LLM first token is 900-1750 ms depending on model.
Switching to a streaming STT provider cannot fix an end-to-end latency problem; **model
choice can, and roughly halves it.** The D6 interface still keeps a streaming provider
available, but for reducing *background transcript lag*, not the Ask path.
See `docs/poc-findings.md`.

### 5.2 Reliability

- **NFR-2.1** `[A]` The app must not crash, hang, or block the UI thread during a session. It
  runs while a customer is watching the user's screen.
- **NFR-2.2** `[A]` Network loss, provider errors, and device changes degrade visibly and
  recoverably. Capture continues regardless.
- **NFR-2.3** `[A]` All provider calls have bounded timeouts. Nothing waits indefinitely.
- **NFR-2.4** `[D]` Unhandled exceptions are logged and surfaced without tearing down the
  session.

### 5.3 Security and privacy

- **NFR-3.1** `[D]` Keys at rest are DPAPI-encrypted (§4.10).
- **NFR-3.2** `[A]` Audio is not persisted in V1.
- **NFR-3.3** `[A]` Transcripts stored locally only. Nothing is uploaded except to the
  configured STT and AI providers.
- **NFR-3.4** `[A]` Logs contain no key material.

### 5.4 Usability

- **NFR-4.1** `[A]` Zero mouse interaction required once a session is running.
- **NFR-4.2** `[A]` An answer must be readable in a glance of ~2 seconds.
- **NFR-4.3** `[A]` Session start must be a single action.

---

## 6. Architecture

### 6.1 Projects `[A]`

```
MeetingAssist.App      WPF executable — overlay, settings UI, DI composition root
MeetingAssist.Core     Class library — everything else
MeetingAssist.Spike    Console executable — headless pipeline harness, playback, latency report
MeetingAssist.Tests    Unit tests
```

`Core` internal folder layout `[D]`:

```
Core/
  Audio/          capture, resampling, VAD, flushable segmenter, WAV playback source
  Transcription/  ITranscriptionSession + Providers/Groq/
  Assistant/      IAssistant + Providers/Gemini/, prompt builder, style templates
  Session/        orchestrator, transcript store, cost meter
  Interop/        Win32 P/Invoke: display affinity, hotkeys, window styles
  Profiles/       context profile model + JSON persistence
```

**Provider swappability comes from the interfaces in `Transcription/` and `Assistant/` plus
their DI registration in `App`, not from project boundaries (D26).** Swapping Groq for
Deepgram, or Gemini for OpenAI, means adding a class under `Providers/` and changing one
registration line. A separate assembly would add no isolation that `internal` and code review
do not already provide for one developer.

`Spike` references `Core` and constructs the pipeline directly, with no WPF dependency
anywhere in `Core`.

### 6.2 Data flow `[D]`

```
 [Mic capture]  ──┐                                   ┌─→ [STT worker] ─┐
                  ├→ resample → VAD → Segmenter ─────┤                  ├→ [TranscriptStore]
 [Loopback cap] ──┘              (flushable)         └─→ [STT worker] ─┘        │
                                       ▲                                       │
                      hotkey ──→ [Orchestrator] ──flush──┘                     │
                                       │                                       │
                                       ├──── GetWindow(60s) ──────────────────┘
                                       │
                                       └──→ [PromptBuilder] → [IAssistant] ─stream─→ [Overlay]
```

Every arrow between stages is a bounded `Channel<T>`. Capture threads only write to channels.

### 6.3 Threading `[D]`

- Capture callbacks: WASAPI threads, write to channel, no allocation-heavy work.
- Segmentation and STT dispatch: dedicated worker task per stream.
- Transcript mutation: single-threaded via a serializing channel consumer.
- UI updates: marshalled to the dispatcher; overlay rendering only.

---

## 7. Prompt Design

### 7.1 Block order `[A]` — fixed, do not reorder

Ordering is a caching requirement (D14) and must be byte-identical across requests.

```
1. SYSTEM  — role framing + output contract + few-shot examples   [stable, cached]
2. CONTEXT — profile: company, product, objections, notes          [stable, cached]
3. HISTORY — transcript, labeled and chronological                 [grows; prefix cached]
4. FOCUS   — the last N seconds, marked as the subject of the ask   [volatile]
5. ASK     — the instruction                                       [volatile]
```

Nothing varying (timestamp, session id, counter) may appear in blocks 1–2, or the cache
prefix breaks silently and latency regresses with no visible error.

### 7.2 System block requirements `[A]`

- Frame the role **accurately and plainly**: a real-time briefing assistant producing concise
  cue notes for a professional in their own live business meeting. An accurate description of
  an ordinary tool is also the framing least likely to produce hedging.
- Do **not** describe the tool as secret or concealed anywhere in the prompt. That framing
  invites exactly the hedging we want to avoid, and it does not describe the product.
- State the output contract explicitly: bullets only, no preamble, no caveats, no
  meta-commentary, no restating the question.
- Include 2–3 **few-shot examples**. For format discipline these outperform instructions
  alone, and format discipline is what protects the 5-bullet budget.
- Specify the output language from the profile explicitly rather than letting the model infer.

### 7.3 Output contract `[A]`

- Maximum 5 bullets, target ≤8 words each.
- Key points and reminders, not a script to read aloud.
- No hedging, no disclaimers, no "I should note".
- Facts, numbers, prices and timelines only from the context or the conversation. Where the
  answer is not there, the cue is to follow up — never a guess. *Added 2026-10-06, after the
  Flash-Lite models were seen inventing review timelines; a glanced-at invented figure is
  one the user may repeat aloud.*
- Client-side truncation as a backstop if the model exceeds the cap.

### 7.4 Transcript rendering `[A]`

```
Customer: <text>
You: <text>
Customer: <text>
```

Chronological, interleaved across channels by start time. Labels from the profile (FR-5.5).
The FOCUS block repeats the final window verbatim under a marker such as `--- most recent ---`.

---

## 8. Technology Choices

| Concern | Choice | | Notes |
|---|---|---|---|
| Runtime | .NET 10 | `[A]` | |
| UI | WPF + built-in Fluent theme (`ThemeMode`) | `[D]` | WPF-UI only if a control is genuinely missing. |
| DI / config / logging host | `Microsoft.Extensions.Hosting` | `[D]` | |
| Audio | NAudio | `[D]` | `WasapiCapture`, `WasapiLoopbackCapture`, resampling. |
| AI — Gemini | `Google.GenAI` | `[A]` | Official Google SDK. See §8.1. |
| AI — abstraction | `Microsoft.Extensions.AI` `IChatClient`, obtained via `Google.GenAI`'s `.AsIChatClient(modelId)` | `[A]` | Swapping provider is a DI registration change. |
| STT | Groq `whisper-large-v3-turbo`, HTTP multipart | `[A]` | |
| Persistence | SQLite (`Microsoft.Data.Sqlite`) | `[D]` | Transcripts only. |
| Secrets | DPAPI (`System.Security.Cryptography.ProtectedData`) | `[D]` | Requires the NuGet package on .NET Core+. |
| Logging | Serilog, rolling file, structured, key-redacting | `[D]` | |
| Interop | Hand-written P/Invoke in `Core/Interop` | `[D]` | |

### 8.1 Gemini integration — resolved (D17)

Researched rather than assumed. Options considered:

| Option | Verdict |
|---|---|
| **`Google.GenAI`** — official Google Gen AI .NET SDK | **Chosen.** Supports the Gemini Developer API with a plain API key (no GCP project). Native `GenerateContentStreamAsync` for streaming, `SafetySetting` inside `GenerateContentConfig`, and first-party `Microsoft.Extensions.AI` interop via `.AsIChatClient(modelId)`. Targets `net8.0`/`netstandard2.0`, so it runs on .NET 10. |
| `Google.Cloud.VertexAI.Extensions` — official Vertex AI `IChatClient` implementation | Rejected: targets Vertex AI, which requires a GCP project and its auth. Heavier than a personal app with an AI Studio key needs. |
| `GeminiDotnet.Extensions.AI` — community `IChatClient` over the Developer API | Rejected: its own author notes Google has since shipped first-party C# support. No reason to prefer community over official. |
| Gemini's OpenAI-compatibility endpoint via `Microsoft.Extensions.AI.OpenAI` | Rejected: works, but the compatibility layer hides Gemini-specific features (safety settings, thinking config, explicit caching). This was the earlier plan; `Google.GenAI` strictly dominates it. |

Consequence worth noting: **the D19 rationale changed.** Safety-filter configuration was
previously deferred partly because the OpenAI-compatibility layer could not express it. With
`Google.GenAI` it is directly available, so D19 is now purely a scope decision and cheap to
reverse.

### 8.2 Win32 surface `[D]`

| API | Constants | Purpose |
|---|---|---|
| `SetWindowDisplayAffinity` | `WDA_EXCLUDEFROMCAPTURE = 0x11` | Screen-capture exclusion. Requires Win10 2004+ (build 19041). |
| `SetWindowLong` / `GetWindowLong` | `WS_EX_NOACTIVATE = 0x08000000`, `WS_EX_TOOLWINDOW = 0x00000080`, `WS_EX_TRANSPARENT = 0x00000020` | Non-focusable, hidden from Alt+Tab, click-through. |
| `RegisterHotKey` / `UnregisterHotKey` | — | Global hotkeys; check return value. |

Acoustic echo cancellation, if the headphones assumption (D4) is ever dropped, is also
available through NAudio 3: `.WithEchoCancellationReferenceEndpoint(renderDevice)` and
`WasapiRecorder.AcousticEchoCancellationControl`.

### 8.3 File locations `[D]`

```
%APPDATA%\MeetingAssist\config.json           non-secret settings
%APPDATA%\MeetingAssist\secrets.bin           DPAPI-encrypted API keys
%APPDATA%\MeetingAssist\profiles\*.json       context profiles
%APPDATA%\MeetingAssist\styles\*.txt          answer style templates
%APPDATA%\MeetingAssist\sessions.db           SQLite transcripts
%LOCALAPPDATA%\MeetingAssist\logs\*.log       rolling logs
```

---

## 9. Build Order `[D]`

Sequenced so the highest-risk work is validated before any UI exists.

1. **Spike (headless).** Dual WASAPI capture → resample → VAD segmenter with flush → Groq
   with vocabulary priming → labeled transcript to console. Plus WAV playback mode and
   latency instrumentation.
   **Exit criterion:** real measurements for background lag and flush latency, replacing
   the hypotheses in §5.1.
2. **Transcript store + persistence.** In-memory session transcript, window queries, SQLite.
3. **AI layer.** Prompt builder, style template, streaming `IAssistant` over `Google.GenAI`,
   blocked-response handling, usage capture.
4. **Overlay + interop.** Display affinity (with verified return), window styles, hotkeys,
   glanceable rendering.
5. **Settings UI.** Profiles, devices, keys, hotkeys, cost meter.
6. **End-to-end tuning** against §5.1 using the instrumentation from step 1.

---

## 10. Open Items

| # | Item | Resolution path |
|---|---|---|
| ~~O1~~ | ~~Exact Gemini model ID string~~ | **Resolved.** `spike models` enumerates them; `gemini-3.7-flash` confirmed present, `gemini-2.5-flash` retired. |
| ~~O2~~ | ~~Optimal chunk length~~ | **Resolved.** 2 s minimum chunk, measured across four settings. Max-chunk 8 s and min-flush 300 ms remain unvalidated but never misfired in the fixture. |
| ~~O3~~ | ~~Energy VAD sufficiency~~ | **Resolved for now.** 24/24 segments transcribed with zero empty responses and cuts on sentence boundaries. Silero stays available behind the interface if real meetings disagree. |
| O4 | Groq `whisper-large-v3` vs `-turbo` accuracy for an accented speaker | A/B on `fixtures/call1`, which already has a reference transcript. Free-tier quota is identical for both; paid difference is ~7¢/meeting. |
| ~~O5~~ | ~~Default hotkey collisions~~ | **Closed.** `Ctrl+Alt+Space` is already claimed on the author's machine (`0x581`); the shipped Ask default is `Ctrl+Alt+G`, all bindings are editable in the settings window, and failures are reported at startup with the combination named. |
| ~~O6~~ | ~~Capture exclusion verified per conferencing app~~ | **Resolved — confirmed working.** `WDA_EXCLUDEFROMCAPTURE` verified with the throwaway probe (`probe.cmd`) against a GDI `CopyFromScreen` capture and by the user in live conferencing, with the toggle used as a control case. Note the residual leaks affinity cannot cover: mouse cursor position and the user's gaze. |
| ~~O7~~ | ~~STT flush timeout value (FR-6.1.2)~~ | **Resolved.** Measured p95 of chunk → STT response is 587 ms; the current 1500 ms is generous. |
| O8 | Does vocabulary priming earn its place (FR-9.1)? | `spike transcribe --fixture call1 --no-vocab` and compare. Indirect evidence favours keeping it; no controlled A/B run. |
| O9 | Does loopback pick up disruptive non-meeting audio? | Needs a fixture with real far-end audio. If yes, pull per-process loopback forward from V2. |

---

## 11. Acceptance Criteria for V1 `[D]`

1. A session started before a screen-shared presentation records and transcribes both sides
   continuously, with no user interaction.
2. The overlay is provably absent from the shared view in Teams, Zoom, and Meet, while
   remaining visible to the user.
3. Pressing Ask during a meeting produces readable cue notes — ≤5 bullets — with the first
   bullet visible within the §5.1 target *as revised by measurement*.
4. The transcript includes the sentence that was being spoken at the moment Ask was pressed.
5. Speaker attribution is correct for both channels throughout a 45-minute meeting.
6. No mouse interaction is required, and no focus change occurs, at any point during a session.
7. Panic hide removes all app windows instantly and does not toggle back.
8. Network loss for 30 s degrades visibly, and the session recovers without restart.
9. API keys are absent from all files on disk in plaintext and from all log output.
10. The full pipeline can be exercised from WAV files with no live meeting.
11. A session sustains a 45-minute meeting without exceeding the provider rate limit (FR-4.10),
    and any pacing is visible in the logs as `rateLimitWait` rather than as STT latency.

**Discharged by the PoC** (engine-level, verified in `docs/poc-findings.md`): criteria 3, 4 and
10 are met by the spike harness already — cue notes within ~1.26 s, the just-spoken sentence
present after flush, and the whole pipeline runnable from fixtures. They are restated here
because V1 must not regress them, not because they are unproven.

### 11.1 Status at the end of V1 implementation `[D]`

Two evidence classes are distinguished deliberately, because conflating them is how a spec ends
up claiming more than it has earned:

* **Verified** — an automated test, an instrumented measurement, or a log line from a real run.
* **Built, awaiting the user** — implemented and reviewed, but the confirming evidence can only
  come from a person in front of a real meeting. The overlay and the Win32 interop cannot be
  tested any other way.

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Continuous dual-channel capture with no interaction | **Built, awaiting the user** | Both pipelines start from one `Start()`; covered by session tests on synthetic audio. Live dual-channel capture was verified during the PoC. |
| 2 | Overlay provably absent from Teams / Zoom / Meet | **Verified** | `CaptureProbe` showed the window absent from a GDI `CopyFromScreen` bitmap; the user confirmed it live in conferencing during the PoC. Every run logs `0x11 confirmed by read-back`, and a failed read-back raises the FR-7.3 banner. |
| 3 | ≤5 bullets, first bullet within the §5.1 target | **Verified (PoC)** | End-to-end ~1.26 s; Gemini first token ~870 ms. |
| 4 | The just-spoken sentence is in the transcript | **Verified (PoC)** | The flush-on-Ask path (D7), confirmed in live conversation. |
| 5 | Correct speaker attribution across 45 minutes | **Built, awaiting the user** | Attribution is per-channel by construction, so it cannot drift; the 45-minute claim is the part only a real meeting can settle. |
| 6 | No mouse interaction, no focus change during a session | **Verified** | `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`; every session action has a global hotkey. *Caveat: the settings window is an ordinary focusable window — it is a between-meetings surface, and it is capture-excluded.* |
| 7 | Panic hide removes all windows and does not toggle back | **Verified** | One-way flag in `OverlayWindow.Panic`; the settings window is force-closed without the unsaved-changes prompt. The settings window no longer uses `MessageBox`, which would have been a non-excluded top-level window able to block it. |
| 8 | 30 s network loss degrades visibly and recovers without restart | **Verified (unit)** | `ResilienceTests` cover device loss and recovery, provider outage and recovery, give-up after retries, and a health change arriving mid-Ask. *The adapter-disable run is the user's manual check.* |
| 9 | No plaintext keys on disk or in logs | **Verified** | `SecretStoreTests` assert the DPAPI file does not contain the key in UTF-8 or UTF-16, and that no key reaches a real Serilog sink. Confirmed live: the smoke run migrated 4 secrets and deleted the plaintext file. |
| 10 | The full pipeline runs from WAV files with no live meeting | **Verified (PoC)** | `spike` fixtures; also wired into the app's Playback tab. |
| 11 | 45 minutes without exceeding the rate limit, pacing logged as `rateLimitWait` | **Verified (PoC)** | Client-side pacer at 20 RPM with `retry-after`; `MarkExcluding` keeps pacing out of the STT latency figure. |

**FR-1.3 (no Windows toasts) — verified by inspection.** Nothing in the app raises one: there is
no `ShowBalloonTip`, no `ToastNotification`, and the tray icon deliberately does not use balloon
tips. A toast *is* captured by screen sharing, so this is a correctness property, not a
preference. Status reaches the user only through the overlay indicator, the tray icon and the
settings window's status bar — all three of which are either capture-excluded or a single
unlabelled dot.

---

## Appendix A — Interface sketches (NON-NORMATIVE `[P]`)

**Superseded by the PoC.** These sketches predate a working implementation; the real
signatures now live in `src/MeetingAssist.Core` and should be read there instead.

The buffer-ownership question they flagged is **resolved**: `WasapiRecorder.CaptureAsync`
yields `AudioBuffer` instances holding heap-allocated copies, so segments may retain them
freely and no pooling scheme is required. The sketches are kept only as a record of the
original intent.

```csharp
public enum AudioChannelKind { Mic, Loopback }

// Ownership of the payload is UNRESOLVED — see note above.
public sealed record AudioSegment(
    Guid Id,
    AudioChannelKind Channel,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    ReadOnlyMemory<byte> Pcm16Mono16k);

public interface ISegmenter
{
    ChannelReader<AudioSegment> Segments { get; }
    void Push(AudioFrame frame);

    /// Closes and emits the open chunk immediately, regardless of length (FR-3.4).
    Task FlushAsync(CancellationToken ct);
}

public sealed record TranscriptSegment(
    Guid Id,
    AudioChannelKind Channel,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Text);

// Push-frames / raise-events so batch and streaming providers both fit (D6).
public interface ITranscriptionSession : IAsyncDisposable
{
    event Action<TranscriptSegment> SegmentFinalized;
    Task StartAsync(TranscriptionOptions options, CancellationToken ct);
    void Submit(AudioSegment segment);
    Task DrainAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed record TranscriptionOptions(
    string ModelId,
    string Language,        // ISO-639-1, always explicit (FR-4.4)
    string Vocabulary);     // manual, <=224 tokens, static (D9/D10)

public interface IAssistant
{
    IAsyncEnumerable<string> AskAsync(AssistRequest request, CancellationToken ct);
    AssistUsage? LastUsage { get; }
}

public interface ICaptureShield
{
    /// Returns false if exclusion could not be applied (FR-7.3).
    bool ExcludeFromCapture(IntPtr hwnd);
}

public interface IHotkeyService
{
    /// Returns false if the combination is already claimed (FR-8.1).
    bool TryRegister(HotkeyId id, ModifierKeys mods, Key key, Action handler);
    void UnregisterAll();
}
```
