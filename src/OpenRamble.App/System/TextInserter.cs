using System.Diagnostics;
using System.Runtime.InteropServices;
using DictationCore;
using OpenRamble.AppCore;

namespace OpenRamble.App;

/// <summary>
/// Insert recognized text into the active application.
///
/// The structure follows <c>TextInserter.swift</c> - return focus, validate the target, wait for
/// the modifiers, then do the irreversible thing, then give the clipboard back - but the
/// irreversible thing itself differs, and for the better. macOS has no way to type arbitrary
/// Unicode into an arbitrary application, so everything there goes through the clipboard.
/// <c>SendInput</c> with <c>KEYEVENTF_UNICODE</c> does, so the common dictation never touches the
/// clipboard at all: nothing to borrow, nothing to lose, nothing for a clipboard manager to see.
/// The clipboard path stays for long text and carries the mandatory restrictions - see
/// <see cref="ClipboardRestrictions"/>.
///
/// <para>
/// What macOS calls secure input, Windows calls UIPI: a window owned by a process at a higher
/// integrity level discards synthesised input, and it does so <b>silently</b> -
/// <c>SendInput</c> returns zero with <c>ERROR_ACCESS_DENIED</c> and the person sees nothing
/// happen. That is exactly the failure the never-silently-discard rule forbids, so it is detected
/// and reported as <see cref="TextInsertionError.SecureInputActive"/>, never swallowed.
/// </para>
/// </summary>
public sealed partial class TextInserter : ITextInserting
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkReturn = 0x0D;

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private const int ErrorAccessDenied = 5;

    /// <summary>How long focus is given to settle after asking for it. The macOS value.</summary>
    private static readonly TimeSpan ActivationSettle = TimeSpan.FromMilliseconds(120);

    private readonly ClipboardSnapshotLedger ledger = new();
    private readonly TimeSpan restoreDelay;
    private readonly Action<TextInsertionError>? reportRestoreFailure;

    /// <param name="restoreDelay">
    /// How long the dictation stays on the clipboard after Ctrl+V. Slow applications consume the
    /// clipboard from a later message-loop turn.
    /// </param>
    /// <param name="reportRestoreFailure">
    /// Where a failed deferred restore is reported. The restore is fire-and-forget a second after
    /// the paste, so there is nobody left to throw to - and without this the person silently
    /// loses whatever they had copied, with the dictated text sitting in its place.
    /// </param>
    public TextInserter(
        TimeSpan? restoreDelay = null,
        Action<TextInsertionError>? reportRestoreFailure = null)
    {
        this.restoreDelay = restoreDelay ?? TimeSpan.FromSeconds(1);
        this.reportRestoreFailure = reportRestoreFailure;
    }

    /// <summary>
    /// The application that owns the foreground window.
    /// </summary>
    /// <remarks>
    /// <c>TargetApplication.BundleIdentifier</c> has no Windows counterpart; the executable name
    /// is the closest stable identity, and it is what the notice text would name.
    /// </remarks>
    public TargetApplication? FrontmostApplication()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new TargetApplication(process.ProcessName, (int)processId, process.ProcessName);
        }
        catch (ArgumentException)
        {
            // The process died between the two calls. No target is an honest answer.
            return null;
        }
    }

    /// <summary>
    /// A wrapper over a single implementation.
    ///
    /// There is exactly one body. Two bodies would drift, and the speed readout would show a path
    /// the product does not take.
    /// </summary>
    public async Task Insert(string text, TargetApplication? target) =>
        await InsertReportingMarks(text, target).ConfigureAwait(false);

    public async Task<InsertionMarks> InsertReportingMarks(string text, TargetApplication? target)
    {
        if (TextInsertionPolicy.IsNoOp(text))
        {
            return new InsertionMarks();
        }

        // Return focus BEFORE touching the clipboard. If the application is gone there is no
        // point going on, and at that moment the clipboard is still untouched - otherwise the
        // person would lose what they had copied to an insertion that never happened.
        bool alive = target is not null && Activate(target);
        if (alive)
        {
            await Task.Delay(ActivationSettle).ConfigureAwait(false);
        }

        if (TextInsertionPolicy.BeforeClipboard(target, alive, ReadEnvironment()) is InsertionRefusal refused)
        {
            throw TextInsertionPolicy.ToError(refused);
        }

        // Non-null: BeforeClipboard refuses a null target.
        TargetApplication destination = target!;

        await WaitForModifiersRelease().ConfigureAwait(false);

        return InsertionPathPolicy.Choose(text.Length) switch
        {
            InsertionPath.SynthesizedKeystrokes => TypeDirectly(text, destination),
            _ => PasteThroughClipboard(text, destination),
        };
    }

    public Task PressReturn()
    {
        if (ReadEnvironment().TargetIsProtected)
        {
            throw new TextInsertionError.SecureInputActive();
        }

        SendKeyStroke(VkReturn);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The direct path: every UTF-16 unit as its own key event, no clipboard involved.
    /// </summary>
    /// <remarks>
    /// Surrogate pairs need no special handling - each half is sent as its own
    /// <c>KEYEVENTF_UNICODE</c> unit and the destination recombines them, which is exactly what
    /// the API is specified to do.
    /// </remarks>
    private InsertionMarks TypeDirectly(string text, TargetApplication target)
    {
        // The last look, after the wait and immediately before the irreversible event.
        if (TextInsertionPolicy.Recheck(target, ReadEnvironment()) is InsertionRefusal refused)
        {
            throw TextInsertionPolicy.ToError(refused);
        }

        var inputs = new INPUT[text.Length * 2];
        for (int index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = UnicodeInput(text[index], up: false);
            inputs[(index * 2) + 1] = UnicodeInput(text[index], up: true);
        }

        Send(inputs);
        return new InsertionMarks(PasteDispatchedAt: MonotonicInstant.Now);
    }

    /// <summary>
    /// The clipboard path: borrow the clipboard, paste, give it back.
    ///
    /// Every write carries the three restriction formats. That is not a nicety: without them the
    /// dictation lands in Win+V history and on the person's other machines, which is precisely
    /// what a local dictation product promises never to do.
    /// </summary>
    private InsertionMarks PasteThroughClipboard(string text, TargetApplication target)
    {
        uint sequenceBefore = GetClipboardSequenceNumber();

        // A second dictation inside the restore window of the first lands on OUR text. Taking a
        // fresh snapshot there would capture the dictation instead of the person's clipboard, and
        // the person's own content would be gone for good.
        ClipboardClaim inherited = ledger.Inherit(sequenceBefore);
        ClipboardSnapshot snapshot;
        bool inheritsLostContents;
        switch (inherited)
        {
            case ClipboardClaim.Snapshot previous:
                snapshot = previous.Contents;
                inheritsLostContents = false;
                break;

            case ClipboardClaim.Expired:
                // The privacy budget already erased the person's contents; only the fact that the
                // clipboard is still ours survived. That fact is passed on: restore has to say it
                // out loud rather than leave the dictation sitting there.
                snapshot = ClipboardSnapshot.Empty;
                inheritsLostContents = true;
                break;

            default:
                snapshot = CaptureClipboard();
                inheritsLostContents = false;
                break;
        }

        WriteDictationToClipboard(text);
        uint owned = GetClipboardSequenceNumber();
        Guid transaction = inheritsLostContents
            ? ledger.InsertExpired(owned)
            : ledger.Insert(snapshot, owned);

        try
        {
            if (TextInsertionPolicy.Recheck(target, ReadEnvironment()) is InsertionRefusal refused)
            {
                throw TextInsertionPolicy.ToError(refused);
            }

            SendKeyStroke(VkV, VkControl);
        }
        catch
        {
            RestoreClipboard(transaction);
            throw;
        }

        // The headline mark: from here on the text is in someone else's window. Everything after
        // is clipboard protection, and that is waiting, not working.
        var dispatched = MonotonicInstant.Now;

        _ = Task.Run(async () =>
        {
            await Task.Delay(restoreDelay).ConfigureAwait(false);
            try
            {
                RestoreClipboard(transaction);
            }
            catch (TextInsertionError)
            {
                // The insertion itself succeeded, so this cannot be thrown - the caller is long
                // gone. Reporting it is the only honest option: what failed is putting the
                // person's own clipboard back.
                reportRestoreFailure?.Invoke(new TextInsertionError.InsertedButClipboardRestoreFailed());
            }
        });

        return new InsertionMarks(PasteDispatchedAt: dispatched);
    }

    private void RestoreClipboard(Guid transaction)
    {
        switch (ledger.Take(transaction))
        {
            case ClipboardClaim.Snapshot claim:
                if (GetClipboardSequenceNumber() != claim.OwnedSequence)
                {
                    // The person copied something of their own. Nothing to restore, and touching
                    // their write would be worse than doing nothing.
                    return;
                }

                WriteSnapshotToClipboard(claim.Contents);
                return;

            case ClipboardClaim.Expired expired:
                if (GetClipboardSequenceNumber() != expired.OwnedSequence)
                {
                    return;
                }

                // The clipboard is still ours, which means it still holds the dictation. It
                // cannot be left there, and calling this a success would silently lose the
                // person's contents.
                ClearClipboard();
                throw new TextInsertionError.ClipboardRestoreFailed();

            default:
                return;
        }
    }

    private static InsertionEnvironment ReadEnvironment()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return new InsertionEnvironment(0, TargetIsProtected: false);
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return new InsertionEnvironment((int)processId, IsAboveUs(processId));
    }

    /// <summary>
    /// Whether the foreground window would discard our synthesised input.
    /// </summary>
    /// <remarks>
    /// A medium-integrity process may not open a high-integrity one for query, so the failure to
    /// open IS the answer. This is the honest reading of UIPI available without elevating, and it
    /// is why the check is a failed <c>OpenProcess</c> rather than an integrity-level comparison.
    /// </remarks>
    private static bool IsAboveUs(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        const uint queryLimitedInformation = 0x1000;
        nint handle = OpenProcess(queryLimitedInformation, false, processId);
        if (handle != 0)
        {
            CloseHandle(handle);
            return false;
        }

        return Marshal.GetLastWin32Error() == ErrorAccessDenied;
    }

    private static bool Activate(TargetApplication target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessIdentifier);
            nint window = process.MainWindowHandle;
            if (window == 0)
            {
                return false;
            }

            if (GetForegroundWindow() == window)
            {
                return true;
            }

            return SetForegroundWindow(window);
        }
        catch (ArgumentException)
        {
            // The application closed while recognition was running.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitForModifiersRelease()
    {
        for (int attempt = 0; attempt < ModifierReleasePolicy.PollLimit; attempt++)
        {
            if (ModifierReleasePolicy.IsClear(HeldModifiers()))
            {
                return;
            }

            await Task.Delay(ModifierReleasePolicy.PollInterval).ConfigureAwait(false);
        }

        throw TextInsertionPolicy.ToError(InsertionRefusal.ModifiersStillHeld);
    }

    private static HotkeyModifiers HeldModifiers()
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        if (IsDown(0x11))
        {
            held |= HotkeyModifiers.Control;
        }

        if (IsDown(0x12))
        {
            held |= HotkeyModifiers.Alt;
        }

        if (IsDown(0x10))
        {
            held |= HotkeyModifiers.Shift;
        }

        if (IsDown(0x5B) || IsDown(0x5C))
        {
            held |= HotkeyModifiers.Windows;
        }

        return held;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void SendKeyStroke(ushort virtualKey, ushort? modifier = null)
    {
        var inputs = new List<INPUT>(4);
        if (modifier is ushort held)
        {
            inputs.Add(VirtualKeyInput(held, up: false));
        }

        inputs.Add(VirtualKeyInput(virtualKey, up: false));
        inputs.Add(VirtualKeyInput(virtualKey, up: true));
        if (modifier is ushort release)
        {
            inputs.Add(VirtualKeyInput(release, up: true));
        }

        Send(inputs.ToArray());
    }

    /// <summary>
    /// Hand the batch to the system, and refuse to pretend a partial injection worked.
    /// </summary>
    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == (uint)inputs.Length)
        {
            return;
        }

        // Zero with ERROR_ACCESS_DENIED is UIPI: the window in front is above us and the whole
        // batch was discarded. This is the Windows shape of secure input.
        if (Marshal.GetLastWin32Error() == ErrorAccessDenied)
        {
            throw new TextInsertionError.SecureInputActive();
        }

        // Anything else is a torn insertion. Half a sentence in someone's editor is worse than a
        // reported failure, and the recognized text stays recoverable either way.
        throw new TextInsertionError.TargetUnavailable();
    }

    private static INPUT UnicodeInput(char unit, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = 0,
                ScanCode = unit,
                Flags = KeyEventUnicode | (up ? KeyEventKeyUp : 0),
            },
        },
    };

    private static INPUT VirtualKeyInput(ushort virtualKey, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                ScanCode = 0,
                Flags = up ? KeyEventKeyUp : 0,
            },
        },
    };

    // MARK: - Clipboard

    private static ClipboardSnapshot CaptureClipboard()
    {
        var formats = new Dictionary<uint, byte[]>();
        if (!OpenClipboard(0))
        {
            // Another process holds the clipboard. Reading nothing is honest; the restore that
            // follows will find nothing of the person's to put back, which is the truth.
            return ClipboardSnapshot.Empty;
        }

        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                nint handle = GetClipboardData(format);
                if (handle == 0)
                {
                    // A delay-rendered format the owner declined to produce. Keep every
                    // representation that IS available; an unfamiliar format must never block a
                    // dictation.
                    continue;
                }

                nint pointer = GlobalLock(handle);
                if (pointer == 0)
                {
                    continue;
                }

                try
                {
                    int size = (int)GlobalSize(handle);
                    byte[] bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, size);
                    formats[format] = bytes;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        return new ClipboardSnapshot(formats);
    }

    /// <summary>
    /// Put text on the clipboard for the person, with the same restrictions the insertion uses.
    ///
    /// Exposed for "Copy Last as Spoken" and the recent-dictation menu. It is the same code path
    /// on purpose: a second clipboard writer would be a second chance to forget
    /// <see cref="ClipboardRestrictions"/> and leak a dictation into Clipboard History.
    /// </summary>
    public static void CopyHostOnly(string text) => WriteDictationToClipboard(text);

    private static void WriteDictationToClipboard(string text)
    {
        if (!OpenClipboard(0))
        {
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new TextInsertionError.ClipboardWriteFailed();
            }

            // The restrictions go on FIRST. A clipboard monitor watching the sequence number can
            // read the contents the moment the text lands, and a restriction registered after
            // that is a restriction registered too late.
            foreach (string name in ClipboardRestrictions.RequiredFormats)
            {
                PutBytes(RegisterClipboardFormatW(name), ClipboardRestrictions.PayloadFor(name));
            }

            PutBytes(CfUnicodeText, System.Text.Encoding.Unicode.GetBytes(text + '\0'));
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void WriteSnapshotToClipboard(ClipboardSnapshot snapshot)
    {
        if (!OpenClipboard(0))
        {
            throw new TextInsertionError.ClipboardRestoreFailed();
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new TextInsertionError.ClipboardRestoreFailed();
            }

            foreach ((uint format, byte[] bytes) in snapshot.Formats)
            {
                PutBytes(format, bytes);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void ClearClipboard()
    {
        if (!OpenClipboard(0))
        {
            return;
        }

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Hand one format to the clipboard. Ownership of the block transfers on success, so it is
    /// freed only when <c>SetClipboardData</c> refuses it.
    /// </summary>
    private static void PutBytes(uint format, byte[] bytes)
    {
        if (format == 0)
        {
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        // A zero-length marker still needs a block: the format's presence is its whole message.
        nint block = GlobalAlloc(GmemMoveable, (nuint)Math.Max(bytes.Length, 1));
        if (block == 0)
        {
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        nint pointer = GlobalLock(block);
        if (pointer == 0)
        {
            GlobalFree(block);
            throw new TextInsertionError.ClipboardWriteFailed();
        }

        try
        {
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
        }
        finally
        {
            GlobalUnlock(block);
        }

        if (SetClipboardData(format, block) == 0)
        {
            GlobalFree(block);
            throw new TextInsertionError.ClipboardWriteFailed();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        /// <summary>
        /// Never written. It is here because <c>INPUT</c> is a union and the struct must be the
        /// size of its largest member, or <c>SendInput</c> rejects the whole batch on 64-bit.
        /// </summary>
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);
}
