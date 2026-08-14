# Running a local build on Windows

Build on the Linux host:

```bash
./scripts/build-win.sh            # → dist/win-x64 (~200 MB, model not included)
```

Copy `dist/win-x64/` into the Windows VM (virtiofs/samba share or plain copy)
and run `OpenRamble.App.exe`. The app is tray-only: look for the icon in the
notification area. First launch opens onboarding.

## Model

Onboarding downloads Parakeet TDT 0.6b v3 int8 (~670 MB) from Hugging Face
into `%LOCALAPPDATA%\OpenRamble`. To skip the in-VM download, copy a cached
model from the host (`~/.cache/openramble-win/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/`)
into the VM and import it:

```
tools\asr-bench.exe import <path-to-copied-folder>
```

(or let onboarding download; both paths end in the same verified install).

## Diagnostic log

Off by default and on nowhere else: the app writes nothing about a dictation to
disk unless this is switched on for the run. Nothing in it is ever the person's
words — see the rules on `DiagnosticLog`.

Switch it on with an environment variable, in the shell that starts the app:

```
set OPENRAMBLE_DIAG=%USERPROFILE%\Desktop\openramble-diag.txt
OpenRamble.App.exe
```

or with a flag, which is what fits in a shortcut's *Target* field:

```
OpenRamble.App.exe --diag C:\Users\me\Desktop\openramble-diag.txt
```

The flag wins over the variable. The file is appended to, flushed line by line,
and shared for reading while the app holds it — so it can be watched from
another window while the hang it is recording is still going on:

```
powershell -Command "Get-Content -Wait $env:USERPROFILE\Desktop\openramble-diag.txt"
```

One line per event: an instant, an event name, then `key=value`. Everything one
dictation does carries the same `session=` — the recording, the recognition and
the insertion alike — so one session can be read out of a file holding a dozen
of them with `findstr "session=7"`. Events that belong to no dictation (an app
notice, a failed download) say `session=none` or carry no session at all.

What to grep for, in the order a dictation goes through them:

| Event | Says |
|---|---|
| `app.start` / `app.exit` | the run's boundaries, pid, version |
| `app.unhandled` / `app.unobserved` | an exception that escaped, with its stack |
| `hook.install` | `ok=false error=<win32>` means the key is dead for this run |
| `hotkey.configured` | which key is armed, and whether it is swallowed |
| `hotkey.edge` | the configured key physically went down/up |
| `hotkey.gesture` | what the gesture machine made of it |
| `dictation.begin` | `started=` whether a session actually began, `state=` what the press landed on, or `refused=` why it was not even offered |
| `dictation.stop` | a release asked the session to finish |
| `dictation.state` | `Preparing → Listening → Transcribing → Inserting → Idle` |
| `capture.start` | the device opened, its rate and channels |
| `capture.first-frame` | real audio arrived, and how long it took |
| `capture.failed` | the stream died mid-sentence |
| `capture.stop.begin` / `capture.stop.end` | frames, samples, and how long the stop took |
| `capture.stop.drain-abandoned` | the queue did not reach disk inside its budget |
| `capture.stop.device-abandoned` | **the audio stack never let go of its thread** |
| `capture.pull-budget-spent` | the provider kept answering in full — the old wedge |
| `engine.load.begin` / `engine.load.end` | model load and warm-up durations |
| `transcribe.begin` / `transcribe.end` | recognition duration, `chars=` of the result |
| `insert.begin` / `insert.end` | `path=SynthesizedKeystrokes` or the clipboard, and the outcome |
| `insert.refused` / `insert.failed` | why the words did not land, with the Win32 code |
| `dictation.speed` | release → text, → paste, → clipboard restored |
| `dictation.notice` / `app.notice` | every message the person was shown |

## Testing the microphone without the app

`asr-bench mic` records through the very same WASAPI capture the app records
with, then runs the take through the installed model — no hook, no tray, no
overlay in the way:

```
tools\asr-bench.exe mic --seconds 5 --out take.wav
```

It reports the frame and sample counts, the peak input level, the microphone
startup latency, **how long the stop took**, then the recognized text and its
timings. `--no-transcribe` records and reports only. A peak of `0.000` means
the wrong input device or a muted one; zero samples means the device opened and
delivered nothing.

## What to check by hand

0. `tools\asr-bench.exe mic --seconds 5` first. If the microphone does not work
   here, nothing in the app can work either, and this says which half is at
   fault in one command.
1. Onboarding: microphone consent, model download progress, hotkey tryout.
2. Hold the hotkey (default Right Ctrl), speak, release — text appears at the
   cursor of the focused app. Try Notepad, a browser text area, a terminal.
3. Overlay appears while recording and never steals focus.
4. Long dictation (>2 sentences) goes through the clipboard path: verify
   clipboard content is restored afterwards and the dictated text does NOT
   appear in Win+V history.
5. Escape during recording cancels quietly.
6. Failure surfacing: revoke microphone access in Windows Settings while the
   app runs — next dictation must show a notice, not fail silently.
7. Settings: change hotkey, edit the replacements dictionary, export/import
   the dictionary (files must round-trip with the mac app).
8. Sleep/wake the VM: dictation must still work afterwards.
9. Two dictations in a row, and a third after a deliberately hard one: the
   `capture.stop.end` lines must all report back in well under a second, and
   `capture.stop.device-abandoned` must not appear. If it does, the audio path
   is not releasing its thread — the session survives it by design, but say so.

### VM keyboard quirk

In a QEMU/spice VM on a Wayland host, the physical **Right** Ctrl, Right Shift
and Right Alt may never reach the guest at all — spice-gtk's keycode mapping
drops them, so the app sees no key event and the hotkey looks dead. Physical
Caps Lock does come through, which is the quickest way to tell a broken hook
from a swallowed key: pick Caps Lock in Settings and try again.

To test the right-hand keys anyway, inject them from the QEMU monitor on the
host (1500 ms is long enough for a short dictation):

```
sendkey ctrl_r 1500
```

Caps Lock brings its own quirk with it. The client re-synchronises the guest's
LED by sending a compensating pair of key events, both stamped with the same
millisecond — visible in the log as `hotkey.edge down=true` and `down=false` on
one instant. The app cannot tell that from a person brushing the key, so it
treats it as the shortest possible hold: the microphone opens, the gesture
machine holds the release for the rest of the double-tap window, and the
resulting ~290 ms take is dropped without reaching the engine
(`DictationDurationPolicy.Minimum` is 350 ms). In the log such a session runs
`capture.start` → `capture.stop.end audio_ms≈290` → `dictation.state to=Idle`
with no `transcribe.*` and no `insert.*` between them. That is the expected
shape; a phantom pair that ends in an insertion is a bug.

None of this applies on real hardware — it is the VM's input path, not the
app's.

## Known gaps vs the mac app (by design, this build)

- No updater (local builds only, user decision).
- No live preview during recording (offline-only ONNX export).
- No acoustic vocabulary boost (parakeet-ctc-110m has no ONNX export);
  text replacements work fully.
- "Learn from my corrections" is inert until a UIA TextPattern reader lands.
