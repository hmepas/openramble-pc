using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// Every test that drives an <see cref="EvdevKeyboardMonitor"/> or a
/// <see cref="LinuxTextInserter"/> joins this collection.
/// </summary>
/// <remarks>
/// The diagnostic log is ambient by design - one static sink for the whole process - so two of
/// these running at once would read each other's events. The monitor's tests also wait on real
/// gesture timings, and xUnit's default parallelism turns a 350 ms double-tap window on a small
/// runner into a lottery.
/// </remarks>
[CollectionDefinition(LinuxInputCollection.Name, DisableParallelization = true)]
public sealed class LinuxInputCollection
{
    public const string Name = "LinuxInput";
}

/// <summary>Evdev keycodes the tests press, from <c>linux/input-event-codes.h</c>.</summary>
internal static class Keys
{
    public const int Escape = 1;
    public const int C = 46;
    public const int LeftShift = 42;
    public const int RightShift = 54;
    public const int LeftCtrl = 29;
    public const int RightCtrl = 97;
    public const int CapsLock = 58;
}

/// <summary>Building the bytes a keyboard would have produced.</summary>
internal static class EvdevBytes
{
    public const ushort SynchronizationType = 0;

    /// <summary>One event, laid out exactly as the kernel writes it.</summary>
    /// <remarks>
    /// Written with <see cref="EvdevEventStream.EventSize"/> rather than a hard-coded 24 so that
    /// the fixture and the parser cannot disagree about the word size they are on - if they could,
    /// this file would pass while the product read every field from the wrong offset.
    /// </remarks>
    public static byte[] Event(ushort type, ushort code, int value)
    {
        byte[] raw = new byte[EvdevEventStream.EventSize];
        int at = nint.Size * 2;
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(at + 2, 2), code);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(at + 4, 4), value);
        return raw;
    }

    /// <summary>A key going down (1), coming up (0) or repeating (2).</summary>
    public static byte[] Key(int code, int value) =>
        Event(EvdevEvent.TypeKey, (ushort)code, value);

    public static byte[] Concat(params byte[][] events)
    {
        var all = new List<byte>();
        foreach (byte[] one in events) { all.AddRange(one); }

        return [.. all];
    }
}

/// <summary>
/// A keyboard the test types on.
/// </summary>
/// <remarks>
/// A <see cref="MemoryStream"/> would end the moment the test stopped writing, which is the one
/// thing a real device never does: reading one blocks until somebody presses a key, and every
/// question about stopping exists because of that.
/// </remarks>
internal sealed class KeyFeed : Stream
{
    private readonly BlockingCollection<byte[]> pending = new();
    private byte[] head = [];
    private int consumed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Press(int code) => WriteBytes(EvdevBytes.Key(code, 1));

    public void Release(int code) => WriteBytes(EvdevBytes.Key(code, 0));

    public void Repeat(int code) => WriteBytes(EvdevBytes.Key(code, 2));

    public void WriteBytes(byte[] bytes)
    {
        try
        {
            pending.Add(bytes);
        }
        catch (InvalidOperationException)
        {
            // The device is closed. A test writing after that is describing a keyboard that spoke
            // after it was unplugged, and the stream is right to drop it.
        }
    }

    public void End()
    {
        try
        {
            pending.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // Closed twice, which is what a stop after an unplug does.
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (consumed == head.Length)
        {
            if (!pending.TryTake(out byte[]? next, Timeout.Infinite))
            {
                return 0;
            }

            head = next;
            consumed = 0;
        }

        int taken = Math.Min(count, head.Length - consumed);
        Array.Copy(head, consumed, buffer, offset, taken);
        consumed += taken;
        return taken;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { End(); }

        base.Dispose(disposing);
    }
}

/// <summary>The machine's keyboards, as the test says they are.</summary>
internal sealed class FakeKeyboardDeviceSource : IKeyboardDeviceSource
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, KeyFeed> feeds = [];
    private readonly List<KeyboardDevice> devices = [];
    private readonly HashSet<string> denied = [];

    public int Opens { get; private set; }

    public List<string> Closed { get; } = [];

    /// <summary>Add a keyboard that can be read.</summary>
    public KeyFeed Add(string node = "event0", string name = "Test Keyboard")
    {
        var device = new KeyboardDevice(name, node);
        var feed = new KeyFeed();
        lock (gate)
        {
            devices.Add(device);
            feeds[device.Path] = feed;
        }

        return feed;
    }

    /// <summary>Add a keyboard the process may not open - the whole point of the input group.</summary>
    public void AddUnreadable(string node = "event0", string name = "Forbidden Keyboard")
    {
        var device = new KeyboardDevice(name, node);
        lock (gate)
        {
            devices.Add(device);
            denied.Add(device.Path);
        }
    }

    public IReadOnlyList<KeyboardDevice> Enumerate()
    {
        lock (gate) { return [.. devices]; }
    }

    public IKeyboardDeviceReader Open(KeyboardDevice device)
    {
        lock (gate)
        {
            Opens += 1;
            if (denied.Contains(device.Path))
            {
                throw new UnauthorizedAccessException($"Access to the path '{device.Path}' is denied.");
            }

            if (!feeds.TryGetValue(device.Path, out KeyFeed? feed))
            {
                throw new IOException($"{device.Path} is gone");
            }

            return new Reader(this, device.Path, feed);
        }
    }

    private sealed class Reader : IKeyboardDeviceReader
    {
        private readonly FakeKeyboardDeviceSource source;
        private readonly KeyFeed feed;

        public Reader(FakeKeyboardDeviceSource source, string path, KeyFeed feed)
        {
            this.source = source;
            Path = path;
            this.feed = feed;
        }

        public string Path { get; }

        public Stream Events => feed;

        public void Dispose()
        {
            lock (source.gate) { source.Closed.Add(Path); }

            // A real close does not wake a blocked read; this one does, which is the one place the
            // fake is kinder than the kernel. The monitor is written not to need either.
            feed.End();
        }
    }
}

/// <summary>
/// A Wayland desktop with a clipboard and two typing tools, none of which exist.
/// </summary>
/// <remarks>
/// Behaviour copied from the real tools as they were observed, not as they are documented:
/// <c>wl-paste</c> exits 1 with "Nothing is copied" on an empty clipboard, exits 1 on a clipboard
/// that does not offer the requested type, and <c>wl-copy --sensitive</c> is refused by a
/// wl-clipboard older than 2.3.
/// </remarks>
internal sealed class FakeWaylandDesktop : ICommandRunner
{
    private readonly Lock gate = new();

    /// <summary>Every run, in order, for the tests that assert the command lines.</summary>
    public List<CommandRequest> Runs { get; } = [];

    /// <summary>What the tools typed, in order. Nothing else may ever hold the dictation.</summary>
    public List<string> Typed { get; } = [];

    /// <summary>How many times Ctrl+V was synthesised.</summary>
    public int Pastes { get; private set; }

    /// <summary>How many times Return was pressed.</summary>
    public int Returns { get; private set; }

    /// <summary>Tools that are not installed on this imaginary machine.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    /// <summary>Tools that are installed and refuse to work - no compositor support, no daemon.</summary>
    public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether this machine's wl-clipboard knows <c>--sensitive</c>.</summary>
    public bool SupportsSensitive { get; set; } = true;

    /// <summary>The clipboard's text, or <c>null</c> when it holds nothing or nothing textual.</summary>
    public string? ClipboardText { get; private set; }

    /// <summary>Whether the clipboard holds something that is not text - an image, say.</summary>
    public bool ClipboardHasImage { get; set; }

    /// <summary>Whether the last write asked for the do-not-record marker.</summary>
    public bool LastWriteWasSensitive { get; private set; }

    /// <summary>Put something on the clipboard the way another application would.</summary>
    public void SomebodyCopies(string text)
    {
        lock (gate)
        {
            ClipboardText = text;
            ClipboardHasImage = false;
        }
    }

    public CommandResult Run(CommandRequest request)
    {
        lock (gate)
        {
            Runs.Add(request);

            if (Missing.Contains(request.Executable))
            {
                return CommandResult.Missing($"{request.Executable} could not be started");
            }

            return request.Executable switch
            {
                WaylandClipboard.Copy => Copy(request),
                WaylandClipboard.Paste => Paste(request),
                SynthesizedKeyboard.Wtype or SynthesizedKeyboard.Ydotool => Type(request),
                _ => new CommandResult(true, 127, string.Empty, "no such tool"),
            };
        }
    }

    private CommandResult Copy(CommandRequest request)
    {
        if (request.Arguments.Contains("--clear"))
        {
            ClipboardText = null;
            ClipboardHasImage = false;
            return Ok();
        }

        bool sensitive = request.Arguments.Contains("--sensitive");
        if (sensitive && !SupportsSensitive)
        {
            return new CommandResult(true, 1, string.Empty, "wl-copy: unknown option '--sensitive'");
        }

        ClipboardText = request.Input;
        ClipboardHasImage = false;
        LastWriteWasSensitive = sensitive;
        return Ok();
    }

    private CommandResult Paste(CommandRequest request)
    {
        if (ClipboardText is null && !ClipboardHasImage)
        {
            return new CommandResult(true, 1, string.Empty, "Nothing is copied");
        }

        if (request.Arguments.Contains("--list-types"))
        {
            return new CommandResult(
                true,
                0,
                ClipboardHasImage ? "image/png\n" : "text/plain\ntext/plain;charset=utf-8\n",
                string.Empty);
        }

        if (ClipboardHasImage)
        {
            return new CommandResult(true, 1, string.Empty, "not available as requested type");
        }

        return new CommandResult(true, 0, ClipboardText ?? string.Empty, string.Empty);
    }

    private CommandResult Type(CommandRequest request)
    {
        if (Broken.Contains(request.Executable))
        {
            return new CommandResult(
                true,
                1,
                string.Empty,
                $"{request.Executable}: refused (no compositor support)");
        }

        if (request.Input is string text)
        {
            Typed.Add(text);
        }
        else if (request.Arguments.Contains("-k") && request.Arguments.Contains("Return"))
        {
            Returns += 1;
        }
        else if (request.Arguments.Contains("key") && request.Arguments.Contains("28:1"))
        {
            Returns += 1;
        }
        else
        {
            Pastes += 1;
        }

        return Ok();
    }

    private static CommandResult Ok() => new(true, 0, string.Empty, string.Empty);
}

/// <summary>The diagnostic events a test provoked, read back the way the log file reads.</summary>
/// <remarks>
/// Against the formatted line rather than a captured tuple: the promise being kept is that the
/// existing debugging tooling works unchanged on Linux, and that tooling greps the file.
/// </remarks>
internal sealed class DiagnosticRecorder : IDisposable
{
    private readonly StringWriter sink = new();
    private readonly DiagnosticLog log;
    private readonly IDisposable installed;

    public DiagnosticRecorder()
    {
        log = new DiagnosticLog(sink);
        installed = DiagnosticLog.Install(log);
    }

    public IReadOnlyList<string> Lines =>
        sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The first event of that name, or <c>null</c> - the whole line, fields and all.</summary>
    public string? Event(string name) =>
        Lines.FirstOrDefault(line => line.Split(' ') is [_, string written, ..] && written == name);

    /// <summary>One field of one event, exactly as it was written.</summary>
    public string? Field(string name, string key)
    {
        string? line = Event(name);
        if (line is null) { return null; }

        foreach (string token in line.Split(' '))
        {
            if (token.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return token[(key.Length + 1)..];
            }
        }

        return null;
    }

    public void Dispose()
    {
        installed.Dispose();
        log.Dispose();
    }
}

/// <summary>Waiting for something another thread is about to do.</summary>
/// <remarks>
/// Every gesture here crosses a thread - a reader delivers, a timer releases - so a test that
/// asserted immediately would be asserting the scheduler. Polling with a deadline keeps the fast
/// case fast and the slow runner honest.
/// </remarks>
internal static class Until
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static bool True(Func<bool> condition)
    {
        long began = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(began) < Deadline)
        {
            if (condition()) { return true; }

            Thread.Sleep(5);
        }

        return condition();
    }
}
