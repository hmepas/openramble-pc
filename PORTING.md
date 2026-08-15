# Swift → C# porting conventions

Reference sources live in `../openramble` (macOS original). This file is the
contract every porting task follows. Deviate only with a written note in the
task result.

## Project mapping

| Swift | C# |
|---|---|
| `Packages/DictationCore/Sources/DictationCore` | `src/DictationCore/` (namespace `DictationCore`) |
| `Packages/DictationCore/Sources/DictationAudio` | `src/DictationCore/Audio/` (namespace `DictationCore.Audio`) |
| `Packages/DictationCore/Tests/*` | `src/DictationCore.Tests/` |
| `Packages/LocalASR` | `src/LocalASR/` (namespace `LocalASR`) |
| `apps/macos` | `src/OpenRamble.App/` (WPF, namespace `OpenRamble.App`) |

Dependency direction is law: App → LocalASR → DictationCore, never backwards.

## Language mapping

- Target: `net10.0` for libraries/tests, `net10.0-windows` for the app.
  Nullable reference types enabled everywhere, warnings as errors.
- Swift `struct` (value semantics) → C# `readonly record struct` or `record`
  (immutable `sealed record` preferred). Mutable Swift structs used locally →
  plain `struct` or a small class; keep semantics, not syntax.
- Swift `enum` with associated values → C# discriminated-union style:
  `abstract sealed record Base` with nested `sealed record` cases, or
  `OneOf`-free hand-rolled hierarchy. Plain enums → C# `enum`.
- Swift `protocol` for ports → C# `interface` (`IClock`, `ITextInserter`, …
  add the `I` prefix, otherwise keep the original name).
- Swift `actor` → sealed class guarding state with `SemaphoreSlim(1,1)` or a
  lock; `async` methods return `Task`/`Task<T>`. Preserve the original's
  reentrancy/ordering semantics — read the Swift tests for what they pin down.
- Swift optionals → C# nullable (`T?`). `guard let` → early return.
- `String` handling: Swift operates on `Character` (grapheme clusters). Where
  the original logic is grapheme-aware (spans, phonetics, typography!), use
  `System.Globalization.StringInfo`/`TextElementEnumerator` — do NOT silently
  switch to UTF-16 `char` indexing. Where the original uses `unicodeScalars`,
  use runes (`Rune`, `EnumerateRunes`). Every span/index type must state in a
  doc comment which unit it counts. This is the #1 source of port bugs.
- `Foundation` bits: `URL` (file) → `string` path + `System.IO`; `Data` →
  `byte[]`/`ReadOnlyMemory<byte>`; `Date` → `DateTimeOffset`; `TimeInterval` →
  `TimeSpan`; `Codable` → `System.Text.Json` with explicit
  `JsonSerializerOptions` (no reflection surprises; camelCase to match any
  on-disk formats only if the original serialized that way — check first).
- `UncheckedBox` exists only to appease Swift Sendable checking — do not port;
  use the value directly.
- Do not introduce third-party packages in DictationCore. Tests use xUnit.

## Porting rules

1. **Tests are the spec.** Port the module's test file(s) first, then make
   them pass. Every Swift test method becomes an xUnit `[Fact]`/`[Theory]`.
   Do not drop a test because it is inconvenient; if a test is genuinely
   macOS-only (AVFoundation formats, NSPasteboard...), skip-port it and list
   it in the task result under "not ported, reason".
2. **Behavior over beauty.** Keep algorithms line-for-line recognizable where
   possible; same names for types, methods, and test methods (C# casing:
   PascalCase methods). Keep the original doc comments (translate to English
   if any slipped into Russian).
3. **Privacy invariants carry over verbatim:** never log dictated text,
   individual words, keystrokes, or user file names. No telemetry. Network
   access only in LocalASR model downloads and the updater.
4. **No speculative additions.** No extra config, no extra abstractions.
5. Every file compiles with zero warnings (`TreatWarningsAsErrors`).

## Build/test commands (Linux host)

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
dotnet build openramble.sln            # the WPF app builds via EnableWindowsTargeting
dotnet test src/DictationCore.Tests
dotnet test src/LocalASR.Tests
```
