namespace OpenRamble.App;

/// <summary>
/// Play a kept recording back, for the History tab.
/// </summary>
/// <remarks>
/// <para>
/// <c>System.Media.SoundPlayer</c>, nothing more: it plays a WAV on a thread of its own, stops
/// on request, and the audio kept beside history is exactly the 16 kHz mono WAV it understands.
/// The product's other sound - <see cref="SoundPlayer"/>, the attention tone - is a different
/// thing and stays a different type.
/// </para>
/// <para>
/// One playback at a time: starting a second stops the first, which is what a list of
/// recordings with one play button per row wants. Untested on this host - the WPF app only
/// cross-compiles here.
/// </para>
/// </remarks>
public sealed class WavPlayback : IDisposable
{
    private readonly Lock gate = new();
    private System.Media.SoundPlayer? player;

    /// <summary>Start playing the file; whatever was playing stops first.</summary>
    public void Play(string path)
    {
        var next = new System.Media.SoundPlayer(path);
        System.Media.SoundPlayer? previous;
        lock (gate)
        {
            previous = player;
            player = next;
        }

        previous?.Stop();
        previous?.Dispose();
        next.Play();
    }

    public void Stop()
    {
        System.Media.SoundPlayer? current;
        lock (gate)
        {
            current = player;
            player = null;
        }

        current?.Stop();
        current?.Dispose();
    }

    public void Dispose() => Stop();
}
