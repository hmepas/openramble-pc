using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

/// <summary>
/// How often to ask the system about permissions.
///
/// The only work the application does while nobody touches it.
/// </summary>
public sealed class PermissionPollPolicyTests
{
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    [Fact]
    public void Scenario001()
    {
        Assert.Equal(Seconds(1), PermissionPollPolicy.Interval(microphoneGranted: false, Seconds(1)));
        Assert.Equal(Seconds(1), PermissionPollPolicy.Interval(microphoneGranted: true, Seconds(1)));
    }

    /// <summary>A revoke delivers no event: the interval is always at most two seconds.</summary>
    [Fact]
    public void Scenario002()
    {
        TimeSpan interval = PermissionPollPolicy.Interval(microphoneGranted: true, Seconds(1));

        Assert.Equal(Seconds(1), interval);
        Assert.True(interval <= Seconds(2));
    }

    /// <summary>
    /// But we do not stop entirely: the permission is revoked in the system settings, and with
    /// it gone the hotkey silently stops working.
    /// </summary>
    [Fact]
    public void Scenario003()
    {
        Assert.True(PermissionPollPolicy.Interval(microphoneGranted: true, Seconds(1)) > TimeSpan.Zero);
    }

    /// <summary>
    /// The main decision of this type, recorded as a check: a granted permission does not affect
    /// the polling frequency at all. Slowing down "when everything is fine" sounds reasonable and
    /// would be a mistake - a revoke sends no event, and an app whose microphone was taken away
    /// looks intact and silently fails to work. Anyone who brings the flag back into the
    /// calculation fails here.
    /// </summary>
    [Fact]
    public void Scenario004()
    {
        TimeSpan[] intervals =
        [
            PermissionPollPolicy.Interval(microphoneGranted: true, Seconds(1)),
            PermissionPollPolicy.Interval(microphoneGranted: false, Seconds(1)),
        ];

        Assert.Single(intervals.Distinct());
    }

    /// <summary>
    /// A base above the ceiling is trimmed: the promise "no later than two seconds" holds even
    /// when the outside asks to poll once a minute.
    /// </summary>
    [Fact]
    public void Scenario005()
    {
        Assert.Equal(Seconds(2), PermissionPollPolicy.Interval(microphoneGranted: true, Seconds(60)));
    }

    [Fact]
    public void Scenario006()
    {
        Assert.Equal(TimeSpan.Zero, PermissionPollPolicy.Interval(microphoneGranted: true, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, PermissionPollPolicy.Interval(microphoneGranted: false, TimeSpan.Zero));
    }
}
