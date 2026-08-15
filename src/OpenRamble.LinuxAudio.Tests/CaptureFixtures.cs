using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxAudio.Tests;

/// <summary>
/// Every test that drives a <see cref="PipeWireCapture"/> joins this collection.
/// </summary>
/// <remarks>
/// Two reasons, and either one alone would be enough. The stop budgets are measured in seconds of
/// real time, and xUnit's default parallelism turns a four-second deadline on a small runner into
/// a lottery. And the diagnostic log is ambient by design - one static sink for the whole process
/// - so two capture tests running at once would read each other's events.
/// </remarks>
[CollectionDefinition(LinuxCaptureCollection.Name, DisableParallelization = true)]
public sealed class LinuxCaptureCollection
{
    public const string Name = "LinuxCapture";
}

/// <summary>
/// A recorder that does what the test tells it to, including nothing.
/// </summary>
/// <remarks>
/// The failures worth testing are the ones a real recorder only produces on a broken machine: a
/// process that ignores SIGTERM, a process that survives SIGKILL long enough to matter, a stream
/// that ends while the dictation is still running. Each is one property here.
/// </remarks>
internal sealed class FakeRecorderProcess : IRecorderProcess
{
    private readonly SampleFeed feed = new();

    /// <summary>Does SIGTERM end the stream? A wedged recorder answers no.</summary>
    public bool HonoursStop { get; init; } = true;

    /// <summary>Does SIGKILL end it? Nothing real answers no; a test needs to.</summary>
    public bool HonoursKill { get; init; } = true;

    /// <summary>What a dead recorder says about itself.</summary>
    public string ExitReport { get; init; } = "pw-record exited with status 1";

    public int StopRequests { get; private set; }

    public int Kills { get; private set; }

    public bool Disposed { get; private set; }

    public bool HasExited { get; private set; }

    public Stream Output => feed;

    /// <summary>Hand the capture some audio, as the pipe would.</summary>
    public void Feed(params float[] samples) => feed.Write(samples);

    /// <summary>Hand over bytes that stop in the middle of a sample, as a pipe does.</summary>
    public void FeedBytes(byte[] bytes) => feed.WriteBytes(bytes);

    /// <summary>The recorder dies on its own - the audio server went away.</summary>
    public void Die()
    {
        HasExited = true;
        feed.End();
    }

    public void RequestStop()
    {
        StopRequests += 1;
        if (HonoursStop) { Die(); }
    }

    public void Kill()
    {
        Kills += 1;
        if (HonoursKill) { Die(); }
    }

    public bool WaitForExit(TimeSpan budget)
    {
        if (HasExited) { return true; }

        // A real wait blocks for the whole budget and then reports failure, which is what the
        // release path is bounded against.
        Thread.Sleep(budget);
        return HasExited;
    }

    public void Dispose()
    {
        Disposed = true;
        feed.End();
    }
}

/// <summary>
/// A pipe, as far as a reader can tell: blocking reads, an end that arrives when it arrives.
/// </summary>
/// <remarks>
/// A <see cref="MemoryStream"/> would end the moment the test stopped writing, which is the one
/// thing a real recorder's output never does - and the stop deadline exists entirely for the case
/// where the end never comes.
/// </remarks>
internal sealed class SampleFeed : Stream
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

    public void Write(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * 4];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), samples[index]);
        }

        WriteBytes(bytes);
    }

    public void WriteBytes(byte[] bytes)
    {
        try
        {
            pending.Add(bytes);
        }
        catch (InvalidOperationException)
        {
            // The feed has ended. A test that writes after the end is describing a recorder that
            // spoke after it died, and the stream is right to drop it.
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
            // Ended twice - a stop after a death, which is exactly what a stop after a death does.
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

    public double? Number(string name, string key) =>
        double.TryParse(Field(name, key), CultureInfo.InvariantCulture, out double value) ? value : null;

    public void Dispose()
    {
        installed.Dispose();
        log.Dispose();
    }
}
