# PoC findings

**Status: COMPLETE. Verdict: proceed to V1.**

Every decision rule that could have forced a redesign has been settled against real audio and
live APIs. The engine works: dual-channel Windows capture, chunked transcription, flush-on-Ask,
and streamed cue notes, end to end, fast enough to use mid-conversation.

What remains open is tuning and comparison, not viability. Those items are listed honestly at
the bottom rather than quietly dropped.

---

## Verdict against the decision rules

These were agreed *before* measuring, which is what makes them worth anything.

| Finding | Agreed consequence | Outcome |
|---|---|---|
| 16 kHz mono cannot be forced | Add a resampling stage | **Resolved — not needed.** AutoConvertPcm negotiates it on every endpoint tested. |
| Transcript garbled at 2–3 s chunks | Raise chunk length or move to streaming STT | **Resolved — 2 s is the best setting measured.** The feared failure did not occur. |
| Flush misses the just-ended sentence | **Stop and rethink the interaction model** | **Resolved — flush works in live conversation.** No redesign needed. |
| End-to-end latency too slow | ~~Streaming STT~~ → **switch to a lighter model** | **Rule corrected and applied.** `gemini-3.5-flash-lite`, ~1.26 s end to end. |
| Energy VAD cuts badly | Substitute Silero ONNX | **Not triggered.** 24/24 segments transcribed, zero empty, cuts landed on sentence boundaries. |
| Loopback picks up disruptive audio | Pull per-process loopback forward | **Untested** — no fixture yet with real far-end audio. |
| Vocabulary priming shows no effect | Drop the field | **Untested** — see open items. Indirect evidence favours keeping it. |

---

## The central question, answered

**Is Whisper output at 2–3 s chunks good enough to feed a model?** Yes, and short chunks are
*better*, which is the opposite of the concern that motivated the PoC.

A 112-second read of `docs/test-script.txt` — deliberately dense in proper nouns, acronyms and
numbers — transcribed at four chunk lengths against a hand-checked reference:

| min-chunk | segments | empty | audio sent | stt p50 | **WER** |
|---|---|---|---|---|---|
| **2.0 s** ← chosen | 24 | 0 | 88.3 s | 267 ms | **15.1%** |
| 3.0 s | 20 | 0 | 89.1 s | 285 ms | 15.9% |
| 4.0 s | 19 | 0 | 89.9 s | 325 ms | 15.5% |
| 6.0 s | 14 | 0 | 95.7 s | 359 ms | 18.8% |

2–4 s are within noise of each other; 6 s is measurably worse. **The design's existing default
was already correct, and there is no reason to lengthen chunks.**

### The 15.1% is a pessimistic figure, deliberately

Three things inflate it, all chosen on purpose so the number is a floor rather than a boast:

- **The speaker has a strong Spanish accent.** This is the intended operating condition, not a
  flaw in the test.
- **The script is hostile by design** — `VSAQ`, `dbt`, `SOC 2 Type Two`, `ad hoc`, spelled-out
  numbers.
- **Number formatting counts as error.** The reference says "twenty nine dollars"; Whisper
  writes "$29". Correct transcription, counted wrong.

Errors were confined to sound-alike confusions: `webinar→webbinar`, `ad hoc→AdHop`,
`audit logs→Audi locks`, `VSAQ→PSAQ`, `nothing scales→noting scales`. **No sentence lost
coherence, and no chunk hallucinated** — which is the failure mode that would have mattered,
since the model consumes this text rather than a human reading it.

Every hard proper noun in the vocabulary prompt landed: *Acme Pulse, Power BI, Snowflake, dbt,
SOC 2 Type 2, semantic layer, governed data models*. Suggestive that priming earns its place,
though not the controlled A/B that would prove it.

### Flush-on-Ask works in live conversation

Verified by the user in `spike live` with real speech: pressing Ask immediately after
finishing a sentence produced a transcript containing that sentence, with notes returned fast
enough to read mid-conversation. Reported qualitatively ("works flawlessly, so quick"), not
instrumented — the instrumented figure remains the ~1.26 s composed estimate below.

**This was the one finding that could have forced a redesign of the interaction model.** It
did not.

---

## Measured latency — replaces the hypotheses in spec §5.1

| Stage | Hypothesised p50 | **Measured p50** | Note |
|---|---|---|---|
| Flush → chunk closed | 20 ms | ~0 ms | in-memory, below timer resolution |
| Chunk → STT response (Groq) | 400 ms | **268–388 ms** (p95 587 ms) | p50 guess was close; the p95 guess of 1200 ms was far too pessimistic |
| Prompt build | 30 ms | ~0 ms | |
| LLM first token | 1000 ms | **~870 ms** | the real cost centre — see below |

LLM first token, same prompt (702 input tokens), warm connection:

| Model | First token | Thinks by default? | Answer quality |
|---|---|---|---|
| **`gemini-3.5-flash-lite`** ← chosen | **788–1036 ms** (median ~870) | no | good; 4–5 clean bullets |
| `gemini-3.1-flash-lite` | 880–954 ms | no | good |
| `gemini-3.5-flash` | 1060–1535 ms | no | good |
| `gemini-3.7-flash` | 1704–1753 ms | **yes** | good |

**Estimated end to end (hotkey → first bullet): ~1.26 s**, against the §5.1 target of ≤2000 ms.
`gemini-3.7-flash` would sit at ~2.2 s, just over.

### STT is not the bottleneck. The LLM is

By a factor of three or more. This inverted a decision rule in the PoC plan, which assumed a
latency disappointment would be fixed by a streaming STT provider. It would not: replacing a
~300 ms stage cannot help when the adjacent stage costs 870 ms.

The lever for latency is **model choice**, and it is a large one. Streaming STT remains
relevant for *background transcript lag*, not for the Ask path.

### Cold start lands on the first Ask of a meeting

First call in a process costs ~1.5 s extra (3378 → 2880 → 1704 ms across three runs). In a live
session that penalty falls on the user's *first* Ask — the worst possible moment. **V1 must warm
both clients at session start** with a throwaway one-token request.

---

## The provider rate limit is a design constraint, not a test artifact

The most consequential *new* finding of the session, and the one most likely to have been
discovered painfully in production instead.

Groq caps Whisper at **20 requests/minute — identical on the free and Developer plans.** Paying
does not raise it. It also caps 2,000 requests/day and 28,800 audio seconds/day (~8 hours),
again identically across both Whisper models.

A live two-channel meeting produces roughly **15–25 requests a minute on its own**, which sits
directly on that ceiling. The first four-configuration sweep fired ~106 requests in two minutes,
starved itself, and **dropped 53 of them** — producing a WER table that measured the rate limit
rather than chunk length (96.7% at 6 s, from a single surviving segment).

Resolved by pacing rather than by paying:

- `RateLimiter` — sliding-window pacer, shared across channels through the transcriber instance
  so the budget is enforced per session rather than per channel. Retries claim slots too, since
  a retry is equally chargeable.
- `retry-after` is now honoured on 429 (header, falling back to parsing Groq's *"try again in
  3s"* body). The previous fixed backoff waited ~1.4 s total against a stated 3 s and gave up.
- Pacing wait is recorded **separately** from STT latency as `rateLimitWait`. Folding it in put
  `stt p95` at 59 s when the request itself took 300 ms, which would have misreported §5.1.

After the fix the same sweep completes all four configurations with **zero dropped segments**.

**Waiting is strictly better than being refused:** a 429 costs the round trip *and* loses the
chunk; pacing delays the chunk and keeps it.

---

## Cost

Per one-hour meeting, on paid tiers, using the chosen models:

| | Volume | Cost |
|---|---|---|
| Groq STT (`whisper-large-v3-turbo`, $0.04/hr) | ~1 hr of speech across both channels | $0.04 |
| Gemini `3.5-flash-lite` ($0.30/M in, $2.50/M out) | ~20 Asks, ~120k input tokens as the transcript grows, ~1.3k output | $0.04 |
| **Total** | | **~$0.08** |

Twenty meetings a month is under **$2**. On the free tiers it is **$0** until 8 hours of audio a
day. **Cost is not a constraint on this design** and does not need to influence any decision.

The constraint that *is* real is the 20 RPM ceiling, which is per key — fine for one user, an
immediate wall for a second.

---

## Also verified

- **Gemini 3.x thinks by default and it must be turned off.** `gemini-3.7-flash` returned a
  truncated answer after five output tokens at 5832 ms: `in=702 out=5 total=998` — ~291 tokens
  of hidden reasoning both blowing the latency budget and consuming `MaxOutputTokens`. Fixed
  with `ThinkingConfig.ThinkingBudget = 0` via `ChatOptions.RawRepresentationFactory`, which
  **keeps `IChatClient` as the seam (D17)** rather than forcing a drop to the native client.
  Not fully deterministic — one run in three still showed ~199 thought tokens despite a zero
  budget, so `MaxOutputTokens` stays generous (512).
- **Thinking-config support varies within one model family.** `gemini-3.5-flash-lite` *rejects*
  `ThinkingBudget = 0` outright, because it does not think at all. `GeminiAssistant` detects the
  rejection, disables the config for the instance's life, and retries. Provider-specific options
  need a fallback path rather than being assumed — this matters for model swapping generally.
- **Model ids — O1 resolved.** `gemini-2.5-flash` is retired. `gemini-3.7-flash` does exist;
  my knowledge was stale and the user was right. `spike models` makes this checkable rather
  than guessable. All text models carry a 1,048,576-token input window, comfortably confirming
  **D15** (stuff the context, no RAG).
- **16 kHz mono capture needs no resampling — FR-2.4 settled.** Negotiated on every endpoint
  despite all of them running 48 kHz stereo float. One stage removed from the design. The
  implementation still asserts the format and throws on mismatch.
- **Buffer ownership resolved — Appendix A closed.** `CaptureAsync` yields heap-allocated
  copies, safe to retain. No pooling scheme needed.
- **Out-of-order transcription reorders correctly.** Completion order `#3 #5 #4 #2 #6 #1`
  produced a chronological, correctly attributed transcript — FR-4.9 exercised under real
  concurrency.
- **Segmentation behaves as specified.** 40 unit tests: silence never segments; leading silence
  trimmed to a pre-roll; continuous speech cut at `MaxChunk`; flush emits before `MinChunk`;
  flush with too little speech returns null and *retains* the audio; adaptive VAD floor rejects
  steady noise but fires on speech; prompt system block byte-identical across calls (the caching
  requirement, D14); bullet cap strips conversational preamble.
- **Secrets handling.** Keys live in `%APPDATA%\MeetingAssist\secrets.local.json`, outside the
  working tree by construction. Never on a command line, since command lines are visible to any
  process listing.

### Gotchas worth not rediscovering

- **`Build()` throws when `WithDefaultDeviceStreamRouting()` is configured** — routing activates
  asynchronously and needs `BuildAsync()`. Only the mic channel uses routing, so loopback
  captured fine while the mic silently failed. For every other configuration `BuildAsync` simply
  wraps `Build`, so it is the safe call for both.
- **Starting capture without consuming buffers deadlocks.** `StartRecording()` without draining
  hangs the process and `StopRecording()` never returns. Recorded as FR-2.9.
- **With no audio playing, WASAPI loopback delivers no packets at all** — not silent packets.
  A mic-only fixture therefore contains a 44-byte header-only `loopback.wav`. The pipeline
  handles this correctly; it is not a fault.

---

## Still open — tuning, not viability

None of these block V1. Each is a comparison that was not run, stated plainly rather than
folded into the conclusions above.

| # | Question | How to settle it |
|---|---|---|
| 1 | Does `whisper-large-v3` (10.3% WER) beat `whisper-large-v3-turbo` (12%) for an accented speaker? | `$env:GROQ_STT_MODEL = "whisper-large-v3"` then `spike transcribe --fixture call1`; compare against 15.1%. Free-tier quota is identical for both models, and the paid difference is ~7¢/meeting. |
| 2 | Does vocabulary priming earn its place (FR-9.1)? | `spike transcribe --fixture call1 --no-vocab` and compare. Indirect evidence favours keeping it; no controlled A/B was run. |
| 3 | Does mic quality dominate the accent effect? | Re-read the same script on a headset as `call2`, reusing `call1`'s `truth.txt`. Hardware unavailable at time of writing. |
| 4 | Does loopback pick up disruptive non-meeting audio? | Needs a fixture with real far-end audio. Consequence if yes: pull per-process loopback forward. |
| 5 | Spanish transcription and answer quality | Deferred by decision — English only for the PoC. |
| 6 | Overlay, capture exclusion, global hotkeys | Out of PoC scope by design. **This is the exact surface where competing Windows products visibly fail**, so V1 should treat it as risk, not plumbing. See spec O6. |

---

## Spec amendments applied

- **FR-2.1 / FR-2.2** — NAudio 3 `WasapiRecorderBuilder` replaces the legacy classes
- **FR-2.4** — no resampling stage; capture directly at 16 kHz mono (verified)
- **FR-2.9** — new: use `CaptureAsync`, never `DataAvailable`; plus `BuildAsync` and the
  deadlock gotcha
- **FR-3.2** — 2 s minimum chunk confirmed by measurement, no longer provisional
- **FR-4.7** — rewritten: pace under the provider cap and honour `retry-after`
- **FR-4.10** — new: the 20 RPM cap and its consequences
- **FR-6.4** — thinking must be disabled; note the `RawRepresentationFactory` mechanism
- **FR-6.12** — warm both clients at session start to absorb cold start
- **FR-13.5** — new: pacing wait must be reported separately from provider latency
- **D17** — validated in practice: the abstraction survived its first provider-specific need
- **D18** — vindicated; the default model id originally chosen was already retired
- **§5.1** — replaced by the measured table above
- **O2** — resolved: 2 s chunks
- **O7** — resolved: flush timeout derived from measured p95 (587 ms → 1500 ms is generous)
- **Appendix A** — superseded; buffer-ownership question closed
