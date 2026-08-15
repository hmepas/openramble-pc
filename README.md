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

Recognition runs locally (NVIDIA Parakeet TDT 0.6b v3 via sherpa-onnx —
the same model the macOS app runs via Core ML). Audio is never uploaded,
there are no accounts, no analytics, and transcripts are never written to
disk or logs — including the opt-in diagnostic log, which records what the
app *did* (states, durations, counts, error codes) and never what was said.
The only network access is downloading the model and nothing else. See [docs/differences-from-macos.md](docs/differences-from-macos.md)
for what this port does and does not carry over.

## Building (on Linux)

Requires the .NET 10 SDK. Both builds are produced on a Linux host: the WPF app
cross-compiles from here and can only *run* on Windows.

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
model (~670 MB).

Full manual test checklist and model-transfer shortcut (avoid the in-VM
download by importing the host's cached model): see
[docs/manual-run.md](docs/manual-run.md).

### On Linux

Run `dist/linux-x64/OpenRamble.App.Linux`. Same product, same tray, same
onboarding. What the machine has to provide:

- **The `input` group.** The dictation key is read straight from `/dev/input`,
  because no Wayland protocol reports a key being *held*:
  `sudo gpasswd -a "$USER" input`, then log in again. Without it the app starts,
  says so in the tray, and never sees the key — unless the compositor drives it
  instead, see [below](#hotkey-without-the-input-group).
- **PipeWire** (`pw-record`) to record, **wl-clipboard** (`wl-copy`,
  `wl-paste`) for the clipboard path, **wtype** to type into other windows.
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
- [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0) —
  offline speech recognition runtime.
- [NVIDIA Parakeet TDT 0.6b v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
  (CC-BY-4.0) — the recognition model, downloaded on first run from the
  [k2-fsa ONNX export](https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8).
- [NAudio](https://github.com/naudio/NAudio) (MIT) — WASAPI capture.
- [Velopack](https://github.com/velopack/velopack) (MIT) — installer and
  in-place upgrades on Windows.
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) (MIT) — the Linux shell's
  interface toolkit.
