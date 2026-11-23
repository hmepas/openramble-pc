using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput;

/// <summary>
/// Hyprland's control socket - what <c>hyprctl</c> talks to - asked one question at a time.
/// </summary>
public static class HyprlandIpc
{
    /// <summary>Whether this session is Hyprland's, i.e. whether there is anyone to ask.</summary>
    public static string? Signature =>
        Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 } signature ? signature : null;

    /// <summary>Hyprland moved its sockets from /tmp into the runtime directory; either may be live.</summary>
    public static IReadOnlyList<string> Sockets(string signature)
    {
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
        return
        [
            Path.Combine(runtime, "hypr", signature, ".socket.sock"),
            Path.Combine("/tmp/hypr", signature, ".socket.sock"),
        ];
    }

    /// <summary>
    /// Ask the first socket that answers, all of it - connect, send, read - within one
    /// <paramref name="budget"/>. <c>null</c> when nobody answered in time.
    /// </summary>
    /// <remarks>
    /// One budget for the whole question, not one per call: a compositor that accepts the
    /// connection and then says nothing used to cost a receive timeout per socket, and a connect
    /// to a socket whose listener had stopped accepting had no bound at all.
    /// </remarks>
    public static string? Ask(IReadOnlyList<string> sockets, string command, TimeSpan budget)
    {
        long began = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => budget - Stopwatch.GetElapsedTime(began);

        foreach (string path in sockets)
        {
            if (!File.Exists(path)) { continue; }
            if (Remaining() <= TimeSpan.Zero) { break; }

            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using (var expiry = new CancellationTokenSource(Remaining()))
                {
                    socket.ConnectAsync(new UnixDomainSocketEndPoint(path), expiry.Token).AsTask().GetAwaiter().GetResult();
                }

                socket.SendTimeout = Milliseconds(Remaining());
                socket.Send(Encoding.UTF8.GetBytes(command));
                var answer = new MemoryStream();
                byte[] buffer = new byte[4096];
                while (true)
                {
                    socket.ReceiveTimeout = Milliseconds(Remaining());
                    int read = socket.Receive(buffer);
                    if (read <= 0) { break; }
                    answer.Write(buffer, 0, read);
                }

                return Encoding.UTF8.GetString(answer.ToArray());
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
                // A stale socket, or a compositor that did not answer in time: unknown.
            }
        }

        return null;
    }

    /// <summary>
    /// Every monitor with the space bars and panels have reserved on it, or <c>null</c> when this
    /// is not Hyprland or it did not answer within <paramref name="budget"/>.
    /// </summary>
    public static IReadOnlyList<CompositorMonitor>? Monitors(TimeSpan budget) =>
        Signature is { } signature && Ask(Sockets(signature), "j/monitors", budget) is { } answer
            ? ParseMonitors(answer)
            : null;

    /// <summary>The answer to <c>j/monitors</c>; <c>null</c> when it is not the expected shape.</summary>
    internal static IReadOnlyList<CompositorMonitor>? ParseMonitors(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) { return null; }

            var monitors = new List<CompositorMonitor>();
            foreach (JsonElement monitor in document.RootElement.EnumerateArray())
            {
                if (monitor.TryGetProperty("disabled", out JsonElement disabled) && disabled.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                JsonElement reserved = monitor.GetProperty("reserved");
                monitors.Add(new CompositorMonitor(
                    monitor.GetProperty("name").GetString() ?? "",
                    monitor.GetProperty("width").GetDouble(),
                    monitor.GetProperty("height").GetDouble(),
                    monitor.GetProperty("scale").GetDouble(),
                    monitor.TryGetProperty("transform", out JsonElement transform) ? transform.GetInt32() : 0,
                    new ReservedInsets(
                        reserved[0].GetDouble(),
                        reserved[1].GetDouble(),
                        reserved[2].GetDouble(),
                        reserved[3].GetDouble())));
            }

            return monitors;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException
            or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A socket timeout for what is left; zero would mean "wait forever".</summary>
    private static int Milliseconds(TimeSpan remaining) => Math.Max(1, (int)remaining.TotalMilliseconds);
}
