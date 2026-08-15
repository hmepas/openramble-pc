using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// The autostart entry, which is the one thing the Linux shell writes into somebody else's
/// directory - and the one whose failure is invisible until the next reboot.
/// </summary>
public sealed class LaunchAtLoginTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"openramble-autostart-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Off_until_it_is_switched_on()
    {
        var service = new LaunchAtLogin(directory, () => "/opt/openramble/OpenRamble");

        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void Switching_on_writes_the_entry_and_creates_the_directory()
    {
        var service = new LaunchAtLogin(directory, () => "/opt/openramble/OpenRamble");

        service.SetEnabled(true);

        Assert.True(service.IsEnabled);
        string entry = File.ReadAllText(Path.Combine(directory, LaunchAtLogin.EntryFileName));
        Assert.StartsWith("[Desktop Entry]", entry, StringComparison.Ordinal);
        Assert.Contains("Type=Application", entry, StringComparison.Ordinal);
        Assert.Contains("Exec=\"/opt/openramble/OpenRamble\"", entry, StringComparison.Ordinal);
        Assert.Contains("Terminal=false", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_off_removes_it()
    {
        var service = new LaunchAtLogin(directory, () => "/opt/openramble/OpenRamble");
        service.SetEnabled(true);

        service.SetEnabled(false);

        Assert.False(service.IsEnabled);
    }

    /// <summary>
    /// Switching it off twice - or off having never switched it on - is not an error. A person who
    /// deleted the file from their desktop's own startup list has already done the second one.
    /// </summary>
    [Fact]
    public void Switching_off_what_was_never_on_is_quiet()
    {
        var service = new LaunchAtLogin(directory, () => "/opt/openramble/OpenRamble");

        service.SetEnabled(false);

        Assert.False(service.IsEnabled);
    }

    /// <summary>
    /// The directory is the truth, not a setting of ours: a file deleted behind our back reads as
    /// off, which is what the person just asked for.
    /// </summary>
    [Fact]
    public void The_directory_wins_over_what_we_believe()
    {
        var service = new LaunchAtLogin(directory, () => "/opt/openramble/OpenRamble");
        service.SetEnabled(true);

        File.Delete(Path.Combine(directory, LaunchAtLogin.EntryFileName));

        Assert.False(service.IsEnabled);
    }

    /// <summary>
    /// A home directory with a space in it is ordinary, and an unquoted <c>Exec</c> would autostart
    /// the first word of the path.
    /// </summary>
    [Fact]
    public void A_path_with_spaces_is_quoted()
    {
        Assert.Contains(
            "Exec=\"/home/a person/apps/OpenRamble\"",
            LaunchAtLogin.Entry("/home/a person/apps/OpenRamble"),
            StringComparison.Ordinal);
    }

    /// <summary>The two characters that mean something inside the quotes are escaped.</summary>
    [Fact]
    public void A_quote_and_a_backslash_in_the_path_are_escaped()
    {
        Assert.Contains(
            @"Exec=""/home/od\\d\""name/OpenRamble""",
            LaunchAtLogin.Entry(@"/home/od\d""name/OpenRamble"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The entry ends with a newline. Some session managers parse the file line by line and drop a
    /// last line that has none, which would lose whichever key happened to be written last.
    /// </summary>
    [Fact]
    public void The_entry_ends_with_a_newline()
    {
        Assert.EndsWith("\n", LaunchAtLogin.Entry("/opt/openramble/OpenRamble"), StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_on_without_knowing_our_own_path_says_so()
    {
        var service = new LaunchAtLogin(directory, () => null);

        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true));
    }

    /// <summary>
    /// A person who moved their configuration has moved this with it; writing to
    /// <c>~/.config</c> anyway would leave an entry nothing reads.
    /// </summary>
    [Fact]
    public void The_autostart_directory_follows_XDG_CONFIG_HOME()
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/somebodys-config");

            Assert.Equal("/tmp/somebodys-config/autostart", LaunchAtLogin.AutostartDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }

    /// <summary>
    /// The specification says a relative value is to be ignored, and so is an empty one - both
    /// would otherwise put the entry somewhere relative to whatever directory the app was started
    /// from.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("relative/config")]
    public void An_unusable_XDG_CONFIG_HOME_falls_back_to_the_default(string value)
    {
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", value);

            Assert.Equal(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config",
                    "autostart"),
                LaunchAtLogin.AutostartDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }
}
