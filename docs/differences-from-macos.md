# Differences from the macOS app

What this port deliberately does not (yet) do, why, and what each item would
give if it existed. Cosmetic differences (window chrome, fonts, menu art) are
not listed.

## No automatic updates

Not done. Local builds only, by decision. That's all.

## No live preview while recording

**What it would give:** partial text appearing in the overlay while you are
still speaking, so long dictations feel responsive and you can spot a wrong
start early.

**Why it's not done:** the mac app gets streaming partials from its Core ML
engine. The published ONNX export of Parakeet TDT v3 is offline-only —
sherpa-onnx's streaming recognizer accepts only models exported for
streaming, and no such export of v3 exists. The only way to fake it would be
re-running whole-utterance recognition on a growing buffer, which competes
for the exact CPU the final transcription is about to need and makes the
result *slower*. The interface seam (`ILivePreviewCapable`) is ported, so a
streaming export appearing upstream is a drop-in.

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

The panel also narrates an engine reload. Residency gives the model's memory
back under pressure, the next keypress starts a full reload, and until it lands
there is nothing to transcribe with — which is up to 90 seconds of
"Transcribing…" over a model still coming off disk. The mac HUD has no state
for this; here the existing `EnginePreparationState` line (which both apps
already show in Settings and onboarding) goes on the panel instead — silently,
so a screen reader is not made to read a seconds counter once per second.

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
real Windows hardware yet.

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
  dictation, and the shouting it turns on is the person's to turn off.
- **Reading the key needs the `input` group.** `/dev/input` is where a *held*
  key can be seen — no Wayland protocol reports one — and a user who is not in
  that group can list the keyboards and open none of them. The application says
  so in the tray instead of going quiet, and the remedy is
  `sudo gpasswd -a "$USER" input` plus a fresh login. On Windows a hook needs no
  permission at all.
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
- **Nothing is heard from the system.** Sleep, wake and audio-device changes are
  window messages on Windows; on Linux they are a `login1` D-Bus subscription
  and PipeWire graph events, and neither is wired yet. A dictation held across a
  suspend stays in "listening" until Escape — the release that happened while
  the machine was down reaches nobody — and a microphone unplugged mid-sentence
  is discovered as an empty recognition rather than as a sentence saying so.
  Memory pressure is missing with it, so the engine is released on its idle
  timer only, never early.
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
