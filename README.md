# OpenRamble for Windows

Windows port of [OpenRamble](https://github.com/mikwiseman/openramble) —
private, local dictation. Hold a hotkey, speak, release it: the text appears
at the cursor of the focused application.

> **Provenance.** This is an unofficial, **AI-written port**: the Swift
> codebase of the original macOS app by
> [Mik Wiseman](https://github.com/mikwiseman) was rewritten in C# for
> Windows by Claude (Anthropic), orchestrated by a human. It is not
> affiliated with or endorsed by the upstream project. The architecture, the
> test suite (ported 1:1), the privacy rules, the app icon and the product
> behavior all follow the original; deviations are documented in
> [docs/differences-from-macos.md](docs/differences-from-macos.md).
> Upstream code and icon are used under the [MIT license](LICENSE).

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
- `src/OpenRamble.LinuxAudio` — the same capture against PipeWire, recording
  through a `pw-record` subprocess; the peer of the WASAPI one, same contract.
- `src/AsrBench` — WER/CER measurement tool (`asr-bench`), and `asr-bench mic`
  to test microphone → capture → engine without the app (Windows and Linux).
- `PORTING.md` — the Swift→C# conventions this port follows.

`src/OpenRamble.App/Assets/OpenRamble.ico` — the executable, window and tray
icon — is the macOS app's own `AppIcon.icns` art rendered to Windows sizes,
used under the upstream project's MIT licence (© 2026 Mik Wiseman).

The macOS original is expected as a sibling checkout at `../openramble`
(reference only; not needed to build).

## Third-party components

- [OpenRamble](https://github.com/mikwiseman/openramble) (MIT) — the
  original application this port rewrites, including its icon.
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0) —
  offline speech recognition runtime.
- [NVIDIA Parakeet TDT 0.6b v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
  (CC-BY-4.0) — the recognition model, downloaded on first run from the
  [k2-fsa ONNX export](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8).
- [NAudio](https://github.com/naudio/NAudio) (MIT) — WASAPI capture.
- [Velopack](https://github.com/velopack/velopack) (MIT) — installer and
  in-place upgrades.
