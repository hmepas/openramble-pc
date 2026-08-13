namespace OpenRamble.AppCore;

/// <summary>
/// Who says out loud what is drawn on the screen.
///
/// Behind the interface - because the real announcement goes to a screen reader, and there is
/// no asking it anything back. Without a substitute not a single announcement can be checked,
/// and announcements are the entire interface of this application for a blind person.
/// </summary>
/// <remarks>
/// Only the port is here. The Windows implementation - a UI Automation live region, the
/// counterpart of <c>NSAccessibility.post(.announcementRequested)</c> - is deferred with the
/// rest of the window layer.
/// </remarks>
public interface IAccessibilityAnnouncing
{
    /// <summary>Say it out loud. <paramref name="urgent"/> - interrupt what is being read now.</summary>
    void Announce(string message, bool urgent);
}
