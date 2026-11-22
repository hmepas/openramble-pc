# Running a local build on Windows

Build on the Linux host:

```bash
./scripts/build-win.sh            # → dist/win-x64 (~200 MB, model not included)
```

Copy `dist/win-x64/` into the Windows VM (virtiofs/samba share or plain copy)
and run `OpenRamble.App.exe`. The app is tray-only: look for the icon in the
notification area. First launch opens onboarding.

## Model

Onboarding downloads Parakeet TDT 0.6b v3 - our weight-only int4 build,
[hmepas/parakeet-tdt-0.6b-v3-int4](https://huggingface.co/hmepas/parakeet-tdt-0.6b-v3-int4)
(~720 MB; see `scripts/quantize-parakeet-int4.py` for why not the stock int8) - into
`%LOCALAPPDATA%\OpenRambleData\Models` (the installer owns `%LOCALAPPDATA%\OpenRamble` and
wipes it on reinstall, so data lives next door). To skip the in-VM download, copy the four
files (`~/.cache/openramble-win/parakeet-int4/` on the host) into the VM and either import
them:

```
tools\asr-bench.exe import <path-to-copied-folder>
```

or drop them into a `model\` folder next to `OpenRamble.App.exe` - the app installs from
there by itself on the next start, no network involved (both paths end in the same verified
install).

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

For an installed copy there is `OpenRamble-diag.bat` (in `releases/`, next to
`OpenRamble-win-Setup-<version>.exe`): it stops the running app and restarts it with the
log written next to the `.bat` itself - put both on a share, install from it,
double-click the `.bat`, and read the log from any machine that sees the share.

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
10. Copy shortcut (Settings → General → "Copy last dictation"): record a
    chord such as Ctrl+Alt+C, dictate once, press the chord in another app and
    paste — the last dictation must arrive; Ctrl+Alt+Shift+C must not fire it,
    and holding the chord must copy once, not on every key repeat. On Linux the
    chord is read from the same `/dev/input` devices as the dictation key, so
    it needs the `input` group too; a compositor keybind that drives the
    control socket cannot press it — bind the copy in the compositor instead.
    Caps Lock on or off makes no difference; on Windows AltGr counts as
    Ctrl+Alt. The log line is `shortcut.press`, with no key in it.
11. Microphone picker (Settings → General → "Microphone"): the list must show
    every capture source the desktop's own sound settings show (on Linux it is
    `pw-dump`'s `Audio/Source` nodes; the list is read when the window opens
    and each time it comes back to the front). Pick one, dictate, then unplug it: the
    settings row must say the chosen microphone isn't connected and dictation
    must continue through the default without a failure notice. The
    `capture.start` line carries `device=chosen` or `device=default`, never
    the device's name.
12. History playback: play a take from the History tab; it must stop when the
    next one starts. On Linux playback runs through `pw-play` (or `paplay`,
    then `aplay`); a machine with none of them logs `history.play ok=false
    error=no-player` and "Show in folder" still opens the audio.
13. Settings → Dictation History: dictate three times with "Keep" at 5, star
    one, dictate six more — the starred one must survive, the others age out
    newest-first to five unstarred. "Recognize again" on a kept take after
    adding a dictionary replacement must rewrite the text and keep the star;
    the button is disabled while a re-run is in flight. "Delete All" asks
    first.
14. Settings → General: every row shows a ↺ arrow only once its value differs
    from the default, and the arrow puts back that one setting only. Check
    "Add a space after each dictation" (a dictation ending in "new line" gets
    no space), "Also copy dictations to the clipboard" (the text is on the
    clipboard after a paste, and not in Win+V history), "Finish hands-free
    dictation on silence" (double-press, speak, stay quiet two seconds — it
    ends by itself; a held key never does), "Dictation panel" Top/Bottom (the
    panel moves on the screen holding the focused window), "Unload model"
    Immediately (the Recognition tab says the model rests after a dictation;
    the next key press reloads it under the voice, the panel says "Waking the
    model…" if the take ends first).
15. Settings → About: Theme Light/Dark/System recolours every open window at
    once; System follows the Windows app theme live. "Keep local diagnostics"
    (on by default) writes `SupportLogs/events-<date>.jsonl` under the app data
    folder; turning it off empties that folder. "Save Error Report…" saves a ZIP
    where chosen and opens its folder.

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
