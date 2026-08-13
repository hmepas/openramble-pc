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

## Engine warm-up copy is approximate

The readiness screen still promises "20–40 seconds" — a number measured on
Apple Silicon compiling for the Neural Engine. The Windows reality is ONNX
session initialization (reading ~670 MB of weights + one silent inference);
the state machine is identical but the figure has not been re-measured on
real Windows hardware yet.
