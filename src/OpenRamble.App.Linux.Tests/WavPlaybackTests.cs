using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>Which player a machine gets, and what happens on one with none.</summary>
public sealed class WavPlaybackTests
{
    [Fact]
    public void PipeWires_player_comes_first()
    {
        Assert.Equal("pw-play", WavPlayback.ChoosePlayer(_ => true));
    }

    [Fact]
    public void Falls_back_down_the_list()
    {
        Assert.Equal("paplay", WavPlayback.ChoosePlayer(tool => tool != "pw-play"));
        Assert.Equal("aplay", WavPlayback.ChoosePlayer(tool => tool == "aplay"));
    }

    [Fact]
    public void A_machine_with_no_player_plays_nothing_and_does_not_throw()
    {
        using var playback = new WavPlayback(_ => false);

        Assert.Null(WavPlayback.ChoosePlayer(_ => false));
        Assert.False(playback.Play("/nonexistent/take.wav"));
        Assert.False(playback.IsPlaying);
        playback.Stop();
    }
}
