namespace OpenRamble.AppCore;

/// <summary>Where the non-activating dictation panel sits on the screen being used.</summary>
public enum DictationOverlayPlacement
{
    Top,
    Bottom,
}

public static class DictationOverlayPlacements
{
    public static IReadOnlyList<DictationOverlayPlacement> AllCases { get; } =
        [DictationOverlayPlacement.Top, DictationOverlayPlacement.Bottom];

    /// <summary>Unknown or absent stored values read as the default.</summary>
    public static DictationOverlayPlacement Parse(string? stored)
    {
        foreach (DictationOverlayPlacement candidate in AllCases)
        {
            if (string.Equals(candidate.RawValue, stored, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return SettingsDefaults.OverlayPlacement;
    }

    extension(DictationOverlayPlacement placement)
    {
        public string Title => placement switch
        {
            DictationOverlayPlacement.Top => "Top",
            DictationOverlayPlacement.Bottom => "Bottom",
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };

        /// <summary>The stored spelling, shared with the macOS defaults value.</summary>
        public string RawValue => placement switch
        {
            DictationOverlayPlacement.Top => "top",
            DictationOverlayPlacement.Bottom => "bottom",
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
    }
}

/// <summary>A rectangle in screen units. No UI type, so the policy is testable anywhere.</summary>
/// <remarks>Y grows downward, as on every PC display API - see <see cref="OverlayPlacementPolicy"/>.</remarks>
public readonly record struct OverlayFrame(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + (Width / 2);
}

public readonly record struct OverlaySize(double Width, double Height);

public readonly record struct OverlayPoint(double X, double Y);

/// <summary>
/// Pure geometry for the panel. <c>visibleFrame</c> already excludes the taskbar or panel,
/// so both choices keep the feedback out of system UI.
/// </summary>
/// <remarks>
/// The macOS original works in AppKit's flipped space, where Y grows upward and "top" is
/// <c>maxY</c>. WPF, Win32 and Avalonia all put the origin in the top-left corner, so the same
/// rule reads the other way round here: top is <c>minY + inset</c>, bottom is
/// <c>maxY - height - inset</c>. The numbers the tests pin are therefore this platform's,
/// not a transcription of the Swift ones.
/// </remarks>
public static class OverlayPlacementPolicy
{
    public const double EdgeInset = 24;

    public static OverlayPoint Origin(
        DictationOverlayPlacement placement,
        OverlayFrame visibleFrame,
        OverlaySize panelSize) =>
        new(
            visibleFrame.MidX - (panelSize.Width / 2),
            placement == DictationOverlayPlacement.Top
                ? visibleFrame.MinY + EdgeInset
                : visibleFrame.MaxY - panelSize.Height - EdgeInset);
}
