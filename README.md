# OpenRamble for Windows and Linux

Windows and Linux port of
[OpenRamble](https://github.com/mikwiseman/openramble) — private, local
dictation. Hold a hotkey, speak, release it: the text appears at the cursor of
the focused application.

Both builds are one product: the dictation logic, the app policies and their
tests are shared, and each platform brings its own edges — WASAPI, Win32 hooks
and WPF on one side; PipeWire, `/dev/input`, `wtype` and Avalonia on the other.

> **Provenance.** This is an unofficial, **AI-written port**: the Swift
> codebase of the original macOS app by
> [Mik Wiseman](https://github.com/mikwiseman) was rewritten in C# for
> Windows and Linux by Claude (Anthropic), orchestrated by a human. It is not
> affiliated with or endorsed by the upstream project. The architecture, the
> test suite (ported 1:1), the privacy rules, the app icon and the product
> behavior all follow the original; deviations are documented in
> [docs/differences-from-macos.md](docs/differences-from-macos.md).
> Upstream code and icon are used under the [MIT license](LICENSE).

Recognition runs locally. On Linux it is Parakeet Ultra (Moondream's further
trained NVIDIA Parakeet TDT 0.6B v3) through transcribe.cpp — the model and the
runtime upstream ships, at the quantization its desktop builds use. On Windows,
for now, it is Parakeet TDT 0.6B v3 through sherpa-onnx, until a Windows build
of the transcribe.cpp runtime exists (see
[docs/differences-from-macos.md](docs/differences-from-macos.md#the-engine-transcribecpp-on-linux-sherpa-onnx-on-windows-for-now)).
Audio is never
uploaded, there are no accounts and no analytics. Recent dictations **are**
kept on this machine, with their audio, so you can replay, copy, re-recognize
or delete them: the last 5 by default, adjustable from 5 to 50 in Settings ▸
Dictation History, and a starred one stays outside that limit until you delete
it. Nothing is written to any log — neither the opt-in diagnostic log nor the
opt-in plain-text log file, which record what the app *did* (states, durations,
counts, error codes) and never what was said. The only network access is
downloading the model and nothing else. See
[docs/differences-from-macos.md](docs/differences-from-macos.md) for what this
port does and does not carry over from the macOS app.

## Usage

- Hold the dictation key, speak, release it: the text lands at the cursor.
- Double-press the key for hands-free dictation; press once more to finish, or
  turn on "Finish hands-free dictation on silence" and two seconds of quiet end
  it for you. While you hold the key a pause is never treated as the end.
- Escape while recording cancels the take.
- A second shortcut — any chord you press into the field in Settings — puts
  the last dictation back on the clipboard. Off until you choose one.
- Optional, off by default: leave every dictation on the clipboard as well,
  add a space after each dictation (never after "new line"), choose the
  microphone, put the dictation panel at the top or bottom of the screen, pick
  when an idle model leaves memory (never, immediately, 2 min … 1 hour; 5
  minutes by default), and a light, dark or system theme.
- Every changed setting shows a small arrow that puts just that one back.
- Long takes are recognized in pieces at your natural pauses while you are
  still speaking, so a two-minute dictation becomes text almost as soon as you
  let go. Cuts only ever land in silence; a take without pauses is recognized
  whole, as before.

## Local data

Under `%LOCALAPPDATA%\OpenRambleData` on Windows and
`~/.local/share/OpenRamble` on Linux:

| Data | Retention |
|---|---|
| Recognition model (`Models`) | Until deleted in Settings |
| Current take (`Takes`) | Deleted after success or explicit cancellation |
| Recovery audio after a technical failure (`RecoveredAudio`) | Up to 10 WAV files, seven days, 1 GiB |
| Dictation history: transcripts and their audio (`History`) | The last 5 by default (5–50 in Settings); starred entries are exempt; deleting an entry deletes its audio |
| Diagnostic journal and crash records (`SupportLogs`) | Seven days, 5 MB; on by default, cleared when "Keep local diagnostics" is turned off; technical events and numbers, never words |
| Settings and replacement dictionary (`settings.json`, `replacements.json`) | Until changed |

## Building (on Linux)

Requires the .NET 10 SDK. Both builds are produced on a Linux host: the WPF app
cross-compiles from here and can only *run* on Windows. The Linux build also
needs `cmake`, `ninja`, a C++17 compiler and `curl`: `scripts/build-linux.sh`
first runs `scripts/build-transcribe.sh`, which fetches the transcribe.cpp fork
at its pinned commit and builds the runtime into `artifacts/native/linux-x64`
(a few minutes once; skipped afterwards). The libraries link against the build
machine's glibc, so a build meant for other distributions should be made on the
oldest one it has to run on.

```bash
./scripts/build-win.sh            # runs all tests, then publishes → dist/win-x64
./scripts/build-linux.sh          # the same for Linux            → dist/linux-x64
```

Output: a self-contained folder, ~200 MB for Windows and ~240 MB for Linux
(the model is not included in either). Neither one is installed.

## Running and testing

Copy `dist/win-x64/` into a Windows machine or VM and run
`OpenRamble.App.exe`. The app lives in the notification area (tray); first
launch opens onboarding, which asks for microphone consent and downloads the
model (~720 MB).

Full manual test checklist and model-transfer shortcut (avoid the in-VM
download by importing the host's cached model): see
[docs/manual-run.md](docs/manual-run.md).

### On Linux

Run `dist/linux-x64/OpenRamble.App.Linux`. Same product, same tray, same
onboarding; the model it downloads is Parakeet Ultra (~740 MB). A machine that
already holds Parakeet v3 from an earlier build keeps dictating with it;
Settings ▸ Recognition offers Ultra, and the old model is deleted once Ultra is
downloaded, verified and loaded. What the machine has to provide:

- **The `input` group.** The dictation key is read straight from `/dev/input`,
  because no Wayland protocol reports a key being *held*:
  `sudo gpasswd -a "$USER" input`, then log in again. Without it the app starts,
  says so in the tray, and never sees the key — unless the compositor drives it
  instead, see [below](#hotkey-without-the-input-group).
- **PipeWire** (`pw-record`) to record, **wl-clipboard** (`wl-copy`,
  `wl-paste`) for the clipboard path, **wtype** to type into other windows.
- **keyd** users: keyd re-emits every key through its own virtual keyboard
  and folds Right Ctrl and Right Shift into the left codes, so pick **Left
  Ctrl** or **Left Shift** as the dictation key there (Right Alt survives as
  itself). `python3 scripts/keyd-keys.py` shows what actually arrives.
- **ydotool** where `wtype` cannot work — GNOME and X11 sessions do not offer
  the virtual-keyboard protocol it speaks — with its daemon running.
- Optional: `libcanberra` for the attention sound, `xdg-utils` for the
  "Show recordings" menu item.

#### Hotkey without the `input` group

Joining `input` is the answer that gives the whole product, and it is the one to
take if it is on offer. Where it is not — a managed laptop, a policy, a
preference — the compositor already sees every key and can be told to run a
command on its edges. The app listens on a Unix socket for exactly those:

```
$XDG_RUNTIME_DIR/openramble/ctl.sock        # owner-only; falls back to /tmp/openramble-<uid>/
```

One command per line, one reply line back:

| line | reply | what it does |
| --- | --- | --- |
| `hotkey press` | `ok down` | the configured key goes down — dictation starts |
| `hotkey release` | `ok up` | it comes up — dictation stops and the text is inserted |
| `hotkey toggle` | `ok down` / `ok up` | whichever edge the key was not already at |
| `status` | `ok state=Idle key=RightCtrl down=false` | is it running, and is the key stuck down |

The edges go into the same gesture machine the keyboard feeds, so hold
semantics, the overlay, insertion and the diagnostic log are identical; the
`hotkey.edge` line in the log carries `source=ctl` instead of `source=evdev`.
The socket is always on and needs no setting.

```bash
echo status | socat -t1 - UNIX-CONNECT:$XDG_RUNTIME_DIR/openramble/ctl.sock
echo 'hotkey toggle' | nc -U -N $XDG_RUNTIME_DIR/openramble/ctl.sock   # OpenBSD nc; -N matters
OpenRamble.App.Linux ctl hotkey press    # the built-in client: exit 0 ok, 1 refused, 3 no socket
```

**Which client.** `socat` and `nc -U -N` cost about **1 ms** per call. The built-in
`ctl` verb costs about **20 ms** warm — a whole .NET process start for one line —
measured on the machine this was written on. That is comfortably fast enough for
a press or a toggle, and it is 20 ms added to the gap between letting the key go
and seeing the text on a release. Use `ctl` to try the thing out and to script
around it; put `socat` in the keybind.

Two bind styles, both commented line by line in
[`packaging/linux/hyprland-example.conf`](packaging/linux/hyprland-example.conf):
**hold** needs a press bind and a release bind (`bindn` + `bindrn`, non-consuming
so applications still see the key), **toggle** needs one release bind. Binding a
*bare modifier* like Right Ctrl is the awkward part: the Hyprland wiki documents
only the release form and requires the target modmask for it
(`bindr = CTRL, Control_R, …`), and says nothing about press-edge modifier binds —
so the sample marks the press half as untested and offers toggle as the style
that is documented to work. The sample carries both the hyprlang and the Lua
spellings (Hyprland ≥ 0.55 deprecates hyprlang).

Both sources are live at once. On a machine that *can* read `/dev/input`, do not
bind the same key in the compositor as well: they move one shared key state, so a
dictation ends at whichever edge arrives first. Nothing hangs — it is two hands on
one switch.

`./scripts/package-linux.sh` produces
`releases/openramble-linux-x64-<version>.tar.zst` for distribution by hand;
`packaging/linux/PKGBUILD` is the AUR binary package that installs that same
archive, with an `.install` note about the group above.

What the Linux build does not do that the Windows one does is listed with the
rest of the deviations in
[docs/differences-from-macos.md](docs/differences-from-macos.md).

## Repository layout

- `src/DictationCore` — platform-independent dictation logic, ported 1:1
  from the Swift original with its full test suite.
- `src/LocalASR` — model install/verify/download lifecycle and the two
  engine adapters: transcribe.cpp (P/Invoke, Parakeet Ultra) and sherpa-onnx
  (Parakeet v3).
- `src/OpenRamble.AppCore` — app policies (hotkey state machine, insertion
  policy, tray/overlay state), testable on Linux.
- `src/OpenRamble.App` — WPF shell and Win32 adapters (keyboard hook,
  text insertion, tray).
- `src/OpenRamble.WindowsAudio` — the WASAPI capture, in a library of its own
  so the bench tool records through exactly the code the app records with.
- `src/OpenRamble.LinuxAudio` — the same capture against PipeWire, recording
  through a `pw-record` subprocess; the peer of the WASAPI one, same contract.
- `src/OpenRamble.LinuxInput` — the Linux halves of the keyboard hook and the
  text insertion: `/dev/input` for the hotkey, `wtype`/`wl-clipboard` for the
  words.
- `src/OpenRamble.App.Linux` — Avalonia shell and the remaining Linux adapters
  (tray, overlay, XDG autostart); the peer of `src/OpenRamble.App`.
- `src/AsrBench` — WER/CER measurement tool (`asr-bench`), and `asr-bench mic`
  to test microphone → capture → engine without the app (Windows and Linux).
- `packaging/linux` — the desktop entry both Linux distributions share, the AUR
  `PKGBUILD` and its post-install note, and the commented `hyprland-example.conf`
  for driving the hotkey from the compositor.
- `PORTING.md` — the Swift→C# conventions this port follows.

`src/OpenRamble.App/Assets/OpenRamble.ico` — the executable, window and tray
icon — is the macOS app's own `AppIcon.icns` art rendered to Windows sizes,
used under the upstream project's MIT licence (© 2026 Mik Wiseman).

The macOS original is expected as a sibling checkout at `../openramble`
(reference only; not needed to build).

## Third-party components

- [OpenRamble](https://github.com/mikwiseman/openramble) (MIT) — the
  original application this port rewrites, including its icon.
- [transcribe.cpp](https://github.com/handy-computer/transcribe.cpp) by CJ
  Pais and the Handy team (MIT) — the speech recognition runtime on Linux,
  built from upstream OpenRamble's fork
  ([mikwiseman/transcribe.cpp](https://github.com/mikwiseman/transcribe.cpp)
  at `dacdba09`: 0.4.1, the Parakeet long-form fix and phrase boosting). It
  vendors ggml, miniz and Silero VAD (all MIT); the four licence texts ship
  next to the libraries (`transcribe-cpp-MIT.txt`, `ggml-MIT.txt`,
  `miniz-MIT.txt`, `silero-vad-MIT.txt`).
- [Parakeet Ultra](https://huggingface.co/moondream/parakeet-ultra) by
  Moondream (CC-BY-4.0), a further trained NVIDIA Parakeet TDT 0.6B v3 — the
  Linux recognition model, downloaded on first run as handy-computer's
  [GGUF conversion](https://huggingface.co/handy-computer/parakeet-ultra-gguf)
  quantized to Q8_0 (revision `a1539be1`, one file of 740,363,168 bytes,
  SHA-256 verified; GitHub mirror `mikwiseman/openramble` `models-a1539be1`).
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0) —
  offline speech recognition runtime on Windows, and on Linux for a machine
  that still holds Parakeet v3.
- [NVIDIA Parakeet TDT 0.6b v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
  (CC-BY-4.0) — the recognition model, downloaded on first run as our own
  weight-only int4 build
  ([hmepas/parakeet-tdt-0.6b-v3-int4](https://huggingface.co/hmepas/parakeet-tdt-0.6b-v3-int4),
  made by `scripts/quantize-parakeet-int4.py` from the
  [k2-fsa ONNX export](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3)).
- [NAudio](https://github.com/naudio/NAudio) (MIT) — WASAPI capture.
- [Velopack](https://github.com/velopack/velopack) (MIT) — installer and
  in-place upgrades on Windows.
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) (MIT) — the Linux shell's
  interface toolkit.
