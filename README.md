# OpenRamble for Windows

Windows port of [OpenRamble](https://github.com/mikwiseman/openramble) —
private, local dictation. Hold a hotkey, speak, release it: the text appears
at the cursor of the focused application.

Recognition runs locally (NVIDIA Parakeet TDT 0.6b v3 via sherpa-onnx —
the same model the macOS app runs via Core ML). Audio is never uploaded,
there are no accounts, no analytics, and transcripts are never written to
disk or logs — including the opt-in diagnostic log, which records what the
app *did* (states, durations, counts, error codes) and never what was said.
The only network access is downloading the model and nothing else. See [docs/differences-from-macos.md](docs/differences-from-macos.md)
for what this port does and does not carry over.

## Building (on Linux)

Requires the .NET 10 SDK. The WPF app cross-compiles from Linux; it can only
*run* on Windows.

```bash
./scripts/build-win.sh            # runs all tests, then publishes
```

Output: `dist/win-x64/` — a self-contained folder (~200 MB, model not
included). No installation required on Windows.

## Running and testing

Copy `dist/win-x64/` into a Windows machine or VM and run
`OpenRamble.App.exe`. The app lives in the notification area (tray); first
launch opens onboarding, which asks for microphone consent and downloads the
model (~670 MB).

Full manual test checklist and model-transfer shortcut (avoid the in-VM
download by importing the host's cached model): see
[docs/manual-run.md](docs/manual-run.md).

## Repository layout

- `src/DictationCore` — platform-independent dictation logic, ported 1:1
  from the Swift original with its full test suite.
- `src/LocalASR` — model install/verify/download lifecycle and the
  sherpa-onnx engine adapter.
- `src/OpenRamble.AppCore` — app policies (hotkey state machine, insertion
  policy, tray/overlay state), testable on Linux.
- `src/OpenRamble.App` — WPF shell and Win32 adapters (keyboard hook,
  text insertion, tray).
- `src/OpenRamble.WindowsAudio` — the WASAPI capture, in a library of its own
  so the bench tool records through exactly the code the app records with.
- `src/AsrBench` — WER/CER measurement tool (`asr-bench`), and `asr-bench mic`
  to test microphone → capture → engine without the app.
- `PORTING.md` — the Swift→C# conventions this port follows.

`src/OpenRamble.App/Assets/OpenRamble.ico` — the executable, window and tray
icon — is the macOS app's own `AppIcon.icns` art rendered to Windows sizes,
used under the upstream project's MIT licence (© 2026 Mik Wiseman).

The macOS original is expected as a sibling checkout at `../openramble`
(reference only; not needed to build).
