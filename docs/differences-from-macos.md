# Differences from the macOS app

What this port deliberately does not (yet) do, why, and what each item would
give if it existed. Cosmetic differences (window chrome, fonts, menu art) are
not listed.

## Where this port stands against upstream

The port was made from upstream 0.5.1 and then brought up to the behaviour of
upstream 0.27.0 (`f0cc1b7`): dictation history with audio, stars and
re-recognition (0.9.0, 0.18.0, 0.19.0); a second shortcut that copies the last
dictation and a setting that leaves every dictation on the clipboard (0.12.0,
0.13.0); per-setting revert arrows, a trailing-space option and a theme (0.13.0);
the microphone picker (0.14.0); no warm-up inference on every key press
(0.16.0); named stages for every step between key-up and text in the log
(0.17.1–0.22.0) and an opt-in plain-text log file (0.22.0); hands-free stop on
silence (0.21.0); the idle "Unload model" policy in place of memory-pressure
eviction (0.8.0, 0.9.0); a setup screen that never waits for work nobody is
doing and a preparation that retries with a backoff ladder instead of giving up
(0.8.1–0.8.3); "Waking the model…" instead of a false "Transcribing…" (0.9.0);
the panel at the top or bottom of the screen (0.6.0); the 120-second floor on
the recognition deadline, so a slow machine gets a slow result rather than a
failure (0.9.0); recognition of long takes in pieces at natural pauses while
the person is still speaking (0.25.0–0.25.1); the language hint and "Copy Last
as Spoken" removed with the verbatim copy they needed (0.24.4, 0.25.2).

What upstream did in that range that this port does not carry, and why:

- **Meetings and recordings (0.26.0, 0.27.0).** Upstream records the
  microphone and, with permission, what the Mac plays, and transcribes both
  live in a Recordings window. Upstream itself keeps it out of its shared core
  because it rests on Core Audio process taps. Windows has WASAPI loopback and
  PipeWire has monitor sources, so a counterpart is possible — it is several
  thousand lines of new capture, storage, export and window, and it is the one
  feature deliberately left for a release of its own.
- **Menu bar, Dock or both (0.20.0).** There is no Dock; the app lives in the
  notification area on both platforms.
- **The engine machinery.** The private worker process (0.7.0), Neural Engine
  residency and memory-pressure rules (0.8.x), the Rust shared core and the
  Tauri desktop app (0.10.0, 0.24.0, reverted in 0.24.1) are not ported; the
  behaviours those mechanisms bought — instant key press with a reload riding
  under the voice, bounded waits, a take that is never silently dropped — are
  what was ported, not the mechanisms. The engine itself now follows upstream
  on Linux (transcribe.cpp and Parakeet Ultra, 0.9.0–0.42.0); see the next
  section.
- **Session tokens and file dispositions (0.7.0).** Upstream fences every
  late callback with a session identity and a per-recording disposition so a
  timed-out stop cannot touch the next take. The PC captures are not AVAudio
  and do not wedge the same way; this port keeps its own `captureStopDeadline`
  (below) and its simpler session counter.
- **Liquid Glass, the sidebar settings window (0.11.x), the MCP server
  (0.6.0, removed in 0.7.0), Sparkle.** Cosmetic, or gone upstream too.
- **The recovery-storage-fault menu row (0.8.0).** The PC recovery store has
  no notion of a failed delete-intent lane; a kept recording is announced by
  its notice and lives in the recovery folder, as before.

## The engine: transcribe.cpp on Linux, sherpa-onnx on Windows (for now)

**Linux** recognizes the way upstream does: Parakeet Ultra (Moondream's further
trained Parakeet TDT 0.6B v3) through transcribe.cpp, built from upstream's fork
at the commit upstream 0.42 pins (`dacdba09`: 0.4.1, the Parakeet long-form fix,
phrase boosting). The model is the one upstream's Windows and Linux builds
download — handy-computer's GGUF at `a1539be1`, Q8_0, 740 MB; on a processor
Q4_K_M, the Mac's choice, is the slower of the two. `scripts/build-transcribe.sh`
builds the runtime as shared libraries with ggml's CPU backend split per x86
tier, so one binary picks AVX-512, AVX2 or plain SSE at start; the C# binding is
`TranscribeCppAdapter` (P/Invoke, every struct's size checked against the
library before use).

Where it differs from upstream's own adapters, on purpose:

- **Half the logical processors, 1 to 8 threads.** Upstream's desktop core
  passes one thread on Windows and Linux — a value chosen for an encoder on
  Metal or Vulkan. Its build has no GPU backend, and neither has this one, and
  on the processor one thread puts the whole encoder on one core: 0.66 s for a
  6 s take against 0.18 s at four (16-core desktop). Upstream's other CPU-only
  build, the Intel Mac, fixes four; here the encoder keeps scaling past that
  (a 45 s clip: 1.34 s at four, 0.82 s at eight), so the count follows the
  machine, capped at eight.
- **Word times on every run.** Upstream's dictation asks for text only; this
  port marks paragraph breaks from word times, so it asks for words. The runtime
  builds them either way.
- **No native cancellation callback.** The engine interface here takes no
  token; `LocalTranscriber` discards an answer it gave up on, as before.
- **A quiet microphone is levelled first.** Upstream hands the engine the
  samples as recorded. This port keeps its own `InputGain` (peak to 0.7, at
  most +30 dB, never down) in front of every engine call. Measured on a laptop
  take at −48 dBFS RMS: Ultra alone returns "Раз аптека пять пришлось гулять.",
  with the gain "Раз два, три, четыре, пять, вышел зал, как гуляет." — the same
  text it gives for the take raised by 20 dB by hand.

No engine call is longer than thirty seconds, which matters more here than it
did on sherpa-onnx: Parakeet Ultra segments a longer run itself and on 0.4.1
can drop the start of a piece without trace. Every path — the dictation tail,
the pieces cut while speaking, a take read back from disk, a History entry
recognized again — goes through `LocalTranscriber`, which sends anything over
thirty seconds as pause-cut pieces of at most twenty, as upstream does.

**Moving from v3.** A Linux machine updated from an earlier build holds Parakeet
v3 as ONNX files. It keeps dictating with them, through sherpa-onnx; Settings
offers Ultra beside the working model; Ultra is downloaded only when asked, used
once it verifies and loads, and only then is v3 deleted (`ModelLineup`, upstream
#79). That is why the Linux build still carries sherpa-onnx — for the length of
that transition and nothing else.

**Windows** stays on Parakeet v3 and sherpa-onnx, chosen at build time (the
Windows app composes the sherpa-onnx engine and names no previous model). The
transcribe.cpp runtime needs a native `transcribe.dll` with its ggml libraries;
the same CMake invocation as the Linux script builds it with MSVC on Windows
(`cmake -G Ninja -DTRANSCRIBE_BUILD_SHARED=ON -DTRANSCRIBE_GGML_BACKEND_DL=ON
-DGGML_CPU_ALL_VARIANTS=ON -DTRANSCRIBE_X86_CONSERVATIVE=ON`), but no such
build has been made or run yet, and shipping an untested native library to
Windows is worse than shipping the engine that works there. Once one exists
and passes the same bench, Windows composes `EngineByModelFiles` and the two
manifests exactly as Linux does, and sherpa-onnx leaves the project when the
last v3 install has moved.

## No automatic updates

Not done. Local builds only, by decision. That's all.

## No live preview while recording

**What it would give:** partial text appearing in the overlay while you are
still speaking, so you can spot a wrong start early.

**Why it's not done:** neither app shows partial text any more. Upstream
0.25.0 replaced the idea with something better for latency: the take is cut at
natural pauses and each piece is recognized *during* the recording, so at
key-up only the last few seconds are left to do. That part is ported
(`SpeechSegmenter`, `StreamedSegmentRecognizer`, the same measured
parameters: cuts only inside a pause of at least 700 ms, never before the
take passes fifteen seconds, never a piece under two seconds). Showing the
pieces as they arrive would be a small addition on top; it is not done because
the recognized pieces are not final text — the dictionary and typography run
once over the joined take — and a panel that shows words which then change
reads as a bug.

## No acoustic vocabulary boost

**What it would give:** the mac app runs a second small model
(Parakeet CTC 110M) over the audio to detect *your dictionary terms* by
sound, so a spoken alias (e.g. Cyrillic "комет") reliably comes out as the
canonical written form ("commit") even when the main model mishears it.

**Why it's not done:** no ONNX export of Parakeet CTC 110M exists anywhere.
sherpa-onnx's hotword biasing cannot substitute: it boosts a term's *own*
spelling in the beam, so a Cyrillic alias would surface as the alias itself —
the alias→canonical bridge is the entire point. Text-level dictionary
replacements (the primary mechanism) work fully; only the acoustic assist is
missing. Options if wanted later: export the CTC model from NeMo ourselves,
or wait for an upstream export. The pair-state plumbing is ported and tested,
ready for either.

**What Linux does have — taught words for the decoder (upstream 0.42.0).**
Upstream dropped the CTC model with Core ML and now passes the written forms
of the person's own dictionary entries to transcribe.cpp as the run's
vocabulary, which its phrase boosting (runtime default weight 8.0) uses to
complete a spelling the model has begun on its own — it never starts one. The
starter set and entries marked "no acoustic boost" stay out; with no taught
words nothing is sent and the run is what it was. This port does exactly that,
at launch and on every dictionary change. Checked on a FLEURS sentence: Ultra
writes "массив Винсен … пик Винсен"; taught "Винсон", it writes "Винсон" both
times and nothing else moves. sherpa-onnx (Windows, and v3 during the move)
ignores the list.

## "Learn from my corrections" is inert

**What it would give:** after you fix a dictated word in place, the app
notices the edit and proposes a dictionary entry, so recurring mishears fix
themselves once and for all.

**Why it's not done:** noticing the edit requires reading the focused text
field of a foreign application. On macOS that is the Accessibility API; the
Windows equivalent is driving UI Automation's TextPattern against arbitrary
controls, which is real engineering with real edge cases (Electron apps,
terminals, secure fields). Shipping a half-working reader would silently miss
or misread edits. The whole learning pipeline (schedule, watcher, span
extraction) is ported and tested against fakes; only the UIA reader is
missing, and the setting currently changes nothing.

## No Accessibility permission flow

**What it would give on the mac:** nothing to give here — Windows simply
does not gate global keyboard hooks or synthetic input behind a permission.
The onboarding therefore asks only for microphone consent. The flip side:
a low-level keyboard hook is signature-identical to a keylogger, so
antivirus heuristics may flag an unsigned local build. The answer is code
signing (when releases exist), not obfuscation.

## Single model download source

**What it would give:** the mac app downloads models from Hugging Face with
a GitHub Releases mirror as fallback, surviving an HF outage.

**Why it's not done:** upstream publishes the ONNX export as one tar.bz2 on
GitHub, which the per-file mirror scheme (per-file sha256 verification,
resumable per-file downloads) cannot address. A mirror would mean hosting the
unpacked files in our own GitHub release — straightforward once this project
has releases at all.

Parakeet Ultra (Linux) is the exception: it is the very file upstream mirrors
as a release asset (`mikwiseman/openramble`, `models-a1539be1`), so its
manifest names that mirror and the installer falls back to it as upstream's
does.

## Additions the Windows key path forced

Four things exist here with no counterpart in the mac app. All of them answer
the same platform fact: on Windows the dictation key can be eaten before the
process ever sees it — a virtual machine client, a remote desktop, an elevated
window in front, a low-level hook the system refuses to install — and every one
of those is indistinguishable from the outside from a product that does not
work at all.

- **Right Shift joins the key list.** The mac list is Fn / Right Command /
  Right Option / Left Control, none of which can be intercepted on the way in.
  Here the fallback when Right Ctrl does not arrive has to be a *different
  physical key* that is still free, still holdable and still on every keyboard;
  before this the only alternatives were AltGr (composes characters), Caps Lock
  (shouts) and F13 (most keyboards cannot send it).
- **Left Ctrl and Left Shift join it too.** Upstream 0.15.0 offered the
  left-hand keys on the Mac; here they answer a Linux fact as well. keyd, a
  common remapper, grabs the physical keyboards exclusively and re-emits every
  key through its own virtual keyboard — with Right Ctrl and Right Shift folded
  into the *left* codes. On such a machine the right keys never reach any
  application, and Left Ctrl is the only Ctrl there is. The shortcuts people
  type on the left keys are safe for the same reason the original shipped Left
  Control: a second key while the hotkey is held is a chord, and the hold ends
  instead of dictating over it. Left Alt is still not offered — a bare Alt tap
  reaches the menu bar on Windows. `scripts/keyd-keys.py` prints what keyd
  actually emits for a key, which is the question to ask before choosing one.
- **F13 is no longer offered.** It was in the picker and is out: sending it
  takes a remapper or a macro key, so it is the one candidate a person cannot
  try on the keyboard they are choosing it with. The enum case survives only so
  a settings file written by an earlier build still parses — a choice that is
  not on the offered list falls back to Right Ctrl.
- **The hotkey picker also lives in the try-out step.** The mac app does this
  too (`OnboardingView.swift`); the port had it only in Settings. Sending a
  person out of the first-launch flow to fix the very thing that flow just
  discovered is where a first launch ends.
- **"Hold to dictate" on the try-out page.** A press-and-hold button that
  drives the same `AppState` entry point the hotkey monitor drives — not a
  second dictation path — so the whole record → recognize → insert chain can be
  proved while the key is being swallowed. It deliberately does not move the
  key badge: that badge is the step's evidence about the keyboard, and a mouse
  press is not evidence about a keyboard.
- **A kept reason for tracking that never started.** The failure notice lives
  four seconds; a hook the system refused lives for the rest of the process, so
  the try-out step keeps its reason and shows it in place of "nothing has
  reached OpenRamble yet".

## Deadlines the original does not have

The mac `finalize` leaves `stopRecording()` uncovered by any timeout — every
later stretch (the engine reload, the recognition) is raced against one, but
the first is a bare `await`. On Windows that stop is a WASAPI call, and a call
into an audio service that died does not come back: the panel then sits on
"Transcribing…" with the microphone still open, no message and no sound, until
the process is killed. This port races it against its own budget
(`captureStopDeadline`, 20 s) on both the ordinary and the device-disconnected
path, abandons the wedged call, asks the capture to close anyway, and ends the
session with a failure notice.

The prepare deadline is also longer here. Upstream waits 25 seconds for a
cold engine at stop time before it keeps the take and says the model is still
getting ready; a Mac's reload is a third of a second, so 25 is generous there.
Loading the int4 model on a slow CPU is not, so this port passes 90 seconds
from the composition root. Until the load lands the panel says "Waking the
model… Your words are waiting, not lost." (upstream 0.9.0), announced only
once the wait has lasted a quarter of a second so the common warm path never
flickers.

## A gate the original does not need: punctuation-only transcripts

The mac asks one question of the finished text — is it empty — and that is
enough there, because its engine answers silence with an empty string. Its own
end-to-end test pins exactly that (`testSilentRecordingProducesNoInsertion`
asserts the *raw* model output is `""` after three seconds of quiet).

The ONNX Parakeet this port runs answers silence and room noise with
punctuation instead: a comma, an ellipsis, a run of dozens of them. That is not
an empty string, so the emptiness gate passed it and the hallucination arrived
at the cursor — in the field log that produced `insert.begin chars=51` in the
middle of a dictated sentence.

So the question the original asks of its engine is asked here of the text:
`RecognizedTextPolicy.CarriesWords` looks for one letter or one digit anywhere
in the pipeline's output, counting Unicode scalars rather than UTF-16 code
units. Without one, the session takes the same "Nothing was recognized —
nothing was inserted" path an empty result takes. Nothing else is filtered: a
transcript with a single letter or digit in it (`3`, `ok`) is a real dictation
and goes in untouched, punctuation and all.

## Engine warm-up copy is approximate

The readiness screen still promises "20–40 seconds" — a number measured on
Apple Silicon compiling for the Neural Engine. The Windows reality is ONNX
session initialization (reading ~670 MB of weights + one silent inference);
the state machine is identical but the figure has not been re-measured on
real Windows hardware yet. On Linux with transcribe.cpp the weights are
memory-mapped: the load measured 0.1 s and the warm-up inference 0.06 s.

## Local diagnostics and "Save Error Report…"

Ported from upstream 0.31.0–0.31.2: the opt-in plain-text log became the
allowlisted event journal (`events-YYYY-MM-DD.jsonl`, seven UTC dates, 5 MB),
on by default behind the same switch, now "Keep local diagnostics"; turning it
off clears it. It lives in `<Support>/SupportLogs` — `~/.local/share/OpenRamble/SupportLogs`
on Linux, `%LOCALAPPDATA%\OpenRambleData\SupportLogs` on Windows. Settings →
About → "Save Error Report…" writes one ZIP (`system.json`, `events.jsonl`,
`README.txt`, crash records) where the person chooses and opens its folder.

Where it differs:

- **An existing settings file turns the journal on once.** The settings file
  stores every value, so a `false` written by the old opt-in log cannot be told
  from a deliberate opt-out. Upstream reads a missing key as "on"; here a file
  without `schemaVersion` is migrated to version 1 on first launch, which turns
  the journal on (and drops the removed `modelUnloadTimeout`). An opt-out made
  after that is kept.
- **Crash records are the app's own.** macOS leaves `.ips` reports that
  upstream sanitizes. A managed process here leaves nothing comparable, so an
  unhandled exception writes `crash-*.json` beside the journal: exception
  types, HResults and the methods on the stack — no message, no file names, no
  core dump — and only while the journal is on. A native crash inside the
  speech engine ends the process without one.
- **Fewer fields.** The journal carries what this port measures. Total engine
  time, lost audio, the first read of a paste, disk reads and engine idle time
  (0.33.x–0.38.0) are not measured here and not written; neither is the
  allowlisted failure reason of a notice (`failureReason`, including the 0.39.0
  paste kinds), because this port's notices do not carry one yet. The stage
  list has no `readableFile`: this port has no separate wait for the WAV.
- **Old plain-text logs are not migrated or deleted.** `<Support>/Logs` from
  the opt-in log is left as it was.

## What the Linux build does not do

Everything above is about the port as a whole and holds on both platforms. This
section is the second column: what the Linux build gives up that the Windows one
has, and why. All of it comes from the same place — Wayland deliberately tells
an application nothing about the rest of the desktop, and what is left is
`/dev/input` below the compositor and command-line tools beside it.

- **The dictation key is never swallowed.** The Windows hook returns 1 and the
  keypress does not reach the application in front; the evdev equivalent is
  `EVIOCGRAB`, which takes the whole keyboard away from the compositor for as
  long as the key is held. Nothing here is worth risking a desktop with no
  keyboard, so the key is read and passed through. In practice this shows on one
  choice only: Caps Lock as the hotkey means the LED toggles with every
  dictation, and the shouting it turns on is the person's to turn off — the
  compositor can be asked to do it for them, see the Caps Lock note below.
- **Reading the key needs the `input` group.** `/dev/input` is where a *held*
  key can be seen — no Wayland protocol reports one — and a user who is not in
  that group can list the keyboards and open none of them. The application says
  so in the tray instead of going quiet, and the remedy is
  `sudo gpasswd -a "$USER" input` plus a fresh login. On Windows a hook needs no
  permission at all.

  There is a second remedy for the machine where the first one is not on offer,
  and the notice names it: the compositor already sees every key, so it can be
  bound to run a command on the key's press and release. Those commands reach a
  Unix socket at `$XDG_RUNTIME_DIR/openramble/ctl.sock` and are injected into the
  same gesture machine the keyboard feeds — same hold semantics, same overlay,
  same insertion, distinguishable in the log only by `source=ctl` on the
  `hotkey.edge` line. See the README and
  [`packaging/linux/hyprland-example.conf`](../packaging/linux/hyprland-example.conf).
  It is an alternative and not a replacement: the compositor delivers a keypress
  it has already decided to tell us about, which is a promise `/dev/input` does
  not need anybody to make.

- **Both key sources are live at once, and that is a warning.** The socket does
  not switch the evdev path off — there is no setting, and nothing to
  misconfigure. On a machine where `/dev/input` cannot be read only one of the two
  can deliver anything, so the overlap costs nothing. On a machine where it can,
  binding the same key in the compositor *and* holding it on the keyboard means
  two sources moving one shared key state: the dictation ends at whichever edge
  arrives first, and the second edge finds the key already where it was asked to
  put it and does nothing. Nothing hangs and nothing is lost — a hold ends under
  your finger instead of when you lift it, which is confusing enough to be worth
  not doing on purpose.

- **The compositor path *can* silence Caps Lock, but not with a bind.** Worth
  writing down because the obvious guess is wrong. A Hyprland bind consumes the
  key by default — the application in front never sees it — but the lock state and
  the LED are updated by the compositor's xkb layer before and independently of
  any bind decision, so Caps Lock still shouts with every dictation. What does
  stop it is the layout: `input { kb_options = caps:none }`, which is a thing only
  a compositor can do and the thing `/dev/input` fundamentally cannot — reading a
  device cannot unshout a key. (Read from Hyprland's source rather than its wiki,
  which says nothing about the interaction; untested here.)
- **The clipboard is cleared, not fenced off.** Windows carries three registered
  formats on every write that ask clipboard history and cloud sync to skip the
  dictation. Neither Wayland nor X11 has such a notion, so there is nothing to
  ask: what the insertion path promises here is the clearing — the text is
  offered, the paste consumes it, the previous contents go back a second later,
  and stopping `wl-copy` withdraws the offer. A clipboard manager (Klipper,
  GPaste, cliphist — installed by default on most desktops) that copied the text
  while it was on the selection keeps its copy, and no protocol on this platform
  can take it back. The one opt-out that exists is KDE's, and it is offered.
- **There is no target window, so two behaviours are gone.** Wayland gives a
  client no way to learn which surface has focus or what process owns it. The
  Windows build raises the window it inserted into if focus wandered, and
  refuses to insert when the target changed between the speaking and the
  landing; here the insertion policy is still asked, in the same two places, but
  the answer it gets can only be "the target is where it was". Change window
  while the text is being recognized and the text lands in the new window. There
  is also no secure-input flag: a password field looks like any other, and
  synthetic keystrokes are accepted in it.
- **The kind of app in front is known on Hyprland and plain X11 only.**
  Paragraphs in prose apps and "Send it." in terminals (upstream 0.33.0) depend
  on the focused app's kind. Hyprland names the focused window's class on its
  control socket, and an X11 session through `xprop`; the class is looked up in
  the same seed as macOS bundle ids, with Linux classes and Windows process
  names added to it. GNOME, KDE and Sway under Wayland say nothing, so every app
  there is "unknown": verbatim text, no paragraph breaks, and no Return. The
  window title is never read.
- **A line break is pasted, not typed, outside a terminal.** A typed line break
  is Return, and Return in a chat sends the message, so a take whose text has a
  paragraph or a "New line." in it goes through the clipboard even when it is
  short (on Windows too). In a known terminal it is still typed, because there
  a line break is Return and Ctrl+V is not a paste.
- **Nothing is heard from the system.** Sleep, wake and audio-device changes are
  window messages on Windows; on Linux they are a `login1` D-Bus subscription
  and PipeWire graph events, and neither is wired yet. A dictation held across a
  suspend stays in "listening" until Escape — the release that happened while
  the machine was down reaches nobody — and a microphone unplugged mid-sentence
  is discovered as an empty recognition rather than as a sentence saying so.
  (The engine's memory is released an hour after the last dictation on both
  platforms, as upstream 0.33.1 does; memory-pressure eviction went away with
  upstream 0.9.0.)
- **Our own windows take dictation directly.** Every window of this shell is
  drawn through XWayland, and `wtype` cannot type into an XWayland window: it
  names one keycode per character in a keymap of its own, and XWayland reads
  those keycodes under the seat's real layout instead — a sentence dictated into
  the try-out box came out as digits and punctuation. So the setup and settings
  windows are dictation sinks of their own: when one of them is in front the
  text goes straight into the field at the caret, and "send" is a line break.
  Every other window still goes through `wtype`, exactly as before.
- **The panel is an ordinary window, drawn through XWayland.** A dictation panel
  wants `zwlr_layer_shell_v1`: above every application, positioned by anchor,
  never focusable by construction. That is deferred. What ships is a borderless
  top-level window that asks not to be activated — weaker than the Windows trio
  of extended styles, which also stops a click from activating it and lets the
  click through to the document underneath. The panel is small, has nothing to
  click and sits clear of where people type, so the exposure is a mis-aimed
  click.
- **Nothing is said out loud.** The Windows panel raises a UI Automation
  notification, and Narrator reads it without the panel ever taking focus.
  Avalonia exposes no counterpart on this platform — AT-SPI has `Announce`, the
  toolkit does not surface it — and announcing by moving focus is exactly what
  this window must never do. The panel still writes every one of those messages
  on screen; it is the spoken copy that is missing, and the log records what a
  screen reader was owed.
- **"Does this layout use AltGr" is answered `true` without asking.** Windows
  asks the layout, key by key. The layout belongs to the compositor here, no
  protocol publishes it, and the X11 answer read through XWayland is stale on
  some desktops. Right Alt composes on every layout but plain `us`, so the
  decision is to warn: one warning too many on a screen a person visits twice
  costs less than a person silently losing their `@` and their `€`.
- **There is no microphone settings page to open.** Windows has one page that
  owns the permission and a switch for this application in it. Here whether the
  microphone opens is decided by the sound server, the device and the user's
  groups, and the only way to learn it is to open the device and see — so the
  app probes, and when the probe fails it says what to check and offers to try
  again, instead of promising a window that would never have opened.
