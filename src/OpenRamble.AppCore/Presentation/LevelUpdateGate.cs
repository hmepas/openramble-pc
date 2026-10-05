namespace OpenRamble.AppCore;

/// <summary>
/// Keeps one level update in flight at a time.
/// </summary>
/// <remarks>
/// The meter fired per audio frame - about twenty times a second for the whole dictation - and
/// each one was a hop the interface thread had to service. The recognition path shares that
/// thread's attention, so the meter was competing with the dictation it was drawn for. Under
/// load that queue is where seconds went.
///
/// Dropping intermediate frames costs nothing visible: the waveform draws
/// <see cref="OverlayModel.WaveformSampleCount"/> samples and a display refresh cannot show more
/// than it is given. The newest peak always wins, so the meter still tracks the voice rather
/// than lagging.
/// </remarks>
public sealed class LevelUpdateGate
{
    private int inFlight;

    /// <summary>Claim the one slot. <c>false</c> - an update is already on its way; skip this frame.</summary>
    public bool Take() => Interlocked.CompareExchange(ref inFlight, 1, 0) == 0;

    /// <summary>The update landed; the next frame may go.</summary>
    public void Release() => Volatile.Write(ref inFlight, 0);
}
