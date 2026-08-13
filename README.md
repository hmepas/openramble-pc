# OpenRamble for Windows

Windows port of [OpenRamble](https://github.com/mikwiseman/openramble) —
private, local dictation. Hold a hotkey, speak, release it: the text appears
at the cursor of the focused application.

Recognition runs locally (NVIDIA Parakeet TDT 0.6b v3 via sherpa-onnx —
the same model the macOS app runs via Core ML). Audio is never uploaded,
there are no accounts, no analytics, and transcripts are never written to
disk or logs. The only network access is downloading the model and nothing
else. See [docs/differences-from-macos.md](docs/differences-from-macos.md)
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
  text insertion, WASAPI capture, tray).
- `src/AsrBench` — WER/CER measurement tool (`asr-bench`).
- `PORTING.md` — the Swift→C# conventions this port follows.

The macOS original is expected as a sibling checkout at `../openramble`
(reference only; not needed to build).
