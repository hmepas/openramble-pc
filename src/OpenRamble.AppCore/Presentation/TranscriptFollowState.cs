namespace OpenRamble.AppCore;

/// <summary>
/// Only an explicit scroll or return-to-latest changes whether new text follows.
///
/// Content growth is not a user scroll: the window must report only scrolls the person made,
/// otherwise each appended paragraph turns following off.
/// </summary>
public sealed class TranscriptFollowState
{
    public bool FollowsLatest { get; private set; } = true;

    /// <param name="distanceFromBottom">Device-independent pixels below the visible area.</param>
    public void UserScrolled(double distanceFromBottom) => FollowsLatest = distanceFromBottom <= 24;

    public void ReturnToLatest() => FollowsLatest = true;
}
