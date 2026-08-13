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

## What to check by hand

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

## Known gaps vs the mac app (by design, this build)

- No updater (local builds only, user decision).
- No live preview during recording (offline-only ONNX export).
- No acoustic vocabulary boost (parakeet-ctc-110m has no ONNX export);
  text replacements work fully.
- "Learn from my corrections" is inert until a UIA TextPattern reader lands.
