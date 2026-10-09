# Development

The [README](../README.md) covers using the app. This page covers working on it.

The design contract is [spec-v1.md](spec-v1.md), with a per-criterion status table in §11.1
that separates what is machine-verified from what still needs a real meeting. The engine was
validated first by a proof of concept ([poc-findings.md](poc-findings.md)). Ideas that arrived
later live in the [backlog](backlog.md); nothing there is scheduled.

## Build and test

```bash
dotnet build MeetingAssist.slnx
dotnet test MeetingAssist.slnx
./app.cmd
```

## Layout

```
src/MeetingAssist.Core          the engine: audio, transcription, assistant, session, storage
src/MeetingAssist.App           WPF: main window, overlay, tray icon, Win32 interop
src/MeetingAssist.Spike         console harness (throwaway; the Core code is not)
src/MeetingAssist.CaptureProbe  throwaway capture-exclusion probe
src/MeetingAssist.Tests         unit tests
skills/meeting-profile          the profile guide, also packaged as a Claude skill
tools/render-icon.cs            renders the app icon
tools/release.ps1               builds the installer for a release
```

The split is deliberate. WPF is the one part that cannot be tested automatically, so it holds as
little logic as possible: `MeetingSession` orchestrates a whole meeting with no dependency on
WPF at all, which is why the overlay's behaviour can be covered by tests that touch no window.

Provider swapping comes from the interfaces, not from project boundaries (spec D26). Replacing
the transcription provider means a new class under `Core/Transcription/` and one changed line.

## Where things live

Everything user-specific sits under `%APPDATA%\MeetingAssist\`, outside the working tree by
construction, so none of it can be committed:

| Path | What |
|---|---|
| `secrets.dat` | API keys, encrypted with DPAPI against your Windows account |
| `profiles\*.json` | One file per named profile |
| `settings.json` | Active profile, Ask windows, devices, playback |
| `overlay.json` | Overlay corner, opacity, size |
| `hotkeys.json` | Key bindings |
| `sessions.db` | Transcripts, written as they finalize |
| `logs\app-*.jsonl` | Structured logs, including per-stage latency |

In PowerShell use `$env:APPDATA`, not `%APPDATA%`; the latter is cmd syntax and is passed
through literally.

**Keys** are encrypted at rest and never written to the log. Environment variables
(`GROQ_API_KEY`, `GEMINI_API_KEY`, `GEMINI_MODEL`, `GROQ_STT_MODEL`) override the stored values,
which is the convenient way to A/B a setting for one run. A plaintext `secrets.local.json` left
in that folder is imported on the next start: written encrypted, read back, compared, and only
then deleted.

**Transcripts** are stored in `sessions.db`, a plain SQLite file. Export writes to
`Documents\MeetingAssist\Transcripts`.

## Screen sharing

The overlay, the main window and its dialogs all set
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, which removes them from Windows Graphics
Capture and DXGI Desktop Duplication, the APIs Teams, Zoom and Meet use. The return value is
read back and verified, and a failure raises a banner on the overlay, because a call that
reports success without taking effect would give false confidence at the wrong moment.

Two things are not hidden: the tray icon (the shell owns that area, so it is a small unlabelled
dot) and the mouse cursor (which is why no session action needs the mouse). The app never
raises a Windows toast notification, because a toast is captured by screen sharing.

`./probe.cmd` opens a throwaway window for re-testing capture exclusion on a new machine: SPACE
toggles it so you can see the difference in a share, ESC quits.

## Test mode

You do not need a meeting to exercise the app. **Settings → Playback** substitutes a pair of WAV
files for the two live channels, and the session behaves identically; only where the audio
comes from changes.

For anything lower-level, the headless harness from the proof of concept is the fastest way to
work:

```bash
./spike.cmd --help
```

| Command | Purpose |
|---|---|
| `devices --check` | List audio devices with their ids, verify 16 kHz mono negotiation |
| `synth --out NAME` | Generate a synthetic fixture: no mic, no key |
| `record --out NAME --seconds 60` | Record real mic + loopback fixtures |
| `transcribe --fixture NAME` | Transcribe a fixture, print the transcript |
| `sweep --fixture NAME --chunks 2,3,4,6` | Compare chunk lengths side by side (paced; states its ETA) |
| `live` | Live capture; SPACE = Ask, W = wide Ask, Q = quit |
| `models` | List the models your Gemini key can actually use |

Useful flags: `--fake-stt` (offline stub, no key), `--no-vocab` (A/B vocabulary priming),
`--min-chunk`, `--max-chunk`, `--hangover`, `--realtime`, `--verbose`.

### Recording a fixture worth testing against

Ground truth is free if you write the script before recording rather than transcribing
afterwards. [test-script.txt](test-script.txt) is ready to use: about 90 seconds, dense in
proper nouns and numbers, matching `profile.sample.json` so the vocabulary A/B means something.

```bash
./spike.cmd record --out call1 --seconds 120
```

Read the script aloud while it records, then make it the reference with
`cp docs/test-script.txt fixtures/call1/truth.txt`. `sweep` then reports WER per chunk length
instead of asking you to eyeball transcripts.

Mic-only tests your own voice, which is the easy case. The real difficulty is the remote side:
low-bitrate Opus, packet loss, a cheap laptop mic. To capture that, set a headset as the default
playback device (on speakers the mic picks up the other side and both channels end up
polluted), play a recorded call through it, and talk over it. `loopback.wav` is then the other
side and `mic.wav` is you, cleanly separated.

## The profile skill

[skills/meeting-profile/profile-guide.md](../skills/meeting-profile/profile-guide.md) works
pasted into any AI chat. In the Claude app the same guide is a skill: zip the folder and upload
it under **Customize > Skills** (needs a paid plan with code execution on):

```powershell
tar -a -cf meeting-profile.zip -C skills meeting-profile
```

(`tar` rather than `Compress-Archive`: Windows PowerShell's Compress-Archive writes backslashes
into the zip, which other systems may not read as folders.)

For Claude Code, copy `skills\meeting-profile` into `~\.claude\skills\`.

## The icon

```powershell
dotnet run tools/render-icon.cs -- out
copy out\app.ico src\MeetingAssist.App\app.ico
```

The main window's sidebar draws the same icon in XAML; keep the two alike.

## Releases

The installer and updates come from [Velopack](https://velopack.io). `Program.Main` hands
control to Velopack first, because the installer and the updater start the exe with their own
arguments. `Updates.cs` checks this repository's GitHub Releases after start, downloads in the
background and installs on quit. A build run from the source tree is not installed and skips
all of it.

To publish a version, write its release notes to a file, then build and pack it. The version is
given on the command line; nothing in the code changes:

```powershell
dotnet tool install -g vpk --version 1.2.161
.\tools\release.ps1 -Version 1.1.0 -Notes notes.md
```

The script publishes a self-contained build and packs it into `releases\1.1.0`. It downloads the
previous release first, so `vpk` also builds a delta package and updates stay small. Then create
the GitHub release with everything in that folder except `assets.win.json`:

```powershell
gh release create v1.1.0 --title "MeetingAssist 1.1.0" --notes-file notes.md (Get-ChildItem releases\1.1.0 -Exclude assets.win.json).FullName
```

Keep the `vpk` version equal to the Velopack package version in `MeetingAssist.App.csproj`.
