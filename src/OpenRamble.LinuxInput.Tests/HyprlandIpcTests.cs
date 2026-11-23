using OpenRamble.AppCore;

namespace OpenRamble.LinuxInput.Tests;

public sealed class HyprlandIpcTests
{
    [Fact]
    public void MonitorsCarryTheirModeScaleAndReservation()
    {
        IReadOnlyList<CompositorMonitor>? monitors = HyprlandIpc.ParseMonitors(
            """
            [{"id":0,"name":"HDMI-A-1","width":3840,"height":2160,"x":1080,"y":96,
              "reserved":[0,36,0,0],"scale":1.25,"transform":0,"disabled":false},
             {"id":1,"name":"DP-9","width":1920,"height":1080,"reserved":[0,0,0,0],"scale":1,"disabled":true}]
            """);

        Assert.NotNull(monitors);
        CompositorMonitor monitor = Assert.Single(monitors);
        Assert.Equal(new CompositorMonitor("HDMI-A-1", 3840, 2160, 1.25, 0, new ReservedInsets(0, 36, 0, 0)), monitor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown request")]
    [InlineData("{}")]
    [InlineData("""[{"name":"x"}]""")]
    public void AnythingElseIsUnknown(string answer) => Assert.Null(HyprlandIpc.ParseMonitors(answer));
}
