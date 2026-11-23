using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// Which cursor theme and size get filled in when the environment does not name them - and that a
/// value somebody did set is never replaced.
/// </summary>
public sealed class CursorEnvironmentTests
{
    private static CursorEnvironment.Fill Resolve(
        Dictionary<string, string>? environment = null,
        Dictionary<string, string>? gsettings = null,
        string? ini = null) =>
        CursorEnvironment.Resolve(
            name => environment?.GetValueOrDefault(name),
            key => gsettings?.GetValueOrDefault(key),
            () => ini);

    [Fact]
    public void Variables_already_set_are_left_alone_and_nothing_else_is_asked()
    {
        var fill = CursorEnvironment.Resolve(
            name => name switch { "XCURSOR_SIZE" => "32", "XCURSOR_THEME" => "Bibata", _ => null },
            _ => throw new InvalidOperationException("gsettings must not run"),
            () => throw new InvalidOperationException("settings.ini must not be read"));

        Assert.Equal(new CursorEnvironment.Fill(null, null), fill);
    }

    [Fact]
    public void Empty_variables_count_as_unset()
    {
        var fill = Resolve(
            new() { ["XCURSOR_SIZE"] = "", ["XCURSOR_THEME"] = "" },
            new() { ["cursor-size"] = "uint32 30\n", ["cursor-theme"] = "'Adwaita'\n" });

        Assert.Equal(new CursorEnvironment.Fill("30", "Adwaita"), fill);
    }

    [Fact]
    public void Hyprcursor_size_comes_before_gsettings()
    {
        var fill = Resolve(new() { ["HYPRCURSOR_SIZE"] = "28" }, new() { ["cursor-size"] = "uint32 24" });

        Assert.Equal("28", fill.Size);
    }

    [Theory]
    [InlineData("uint32 24\n", "24")]
    [InlineData("24", "24")]
    [InlineData("'Adwaita'\n", "Adwaita")]
    [InlineData("'Bibata Modern Ice'", "Bibata Modern Ice")]
    public void Gsettings_output_is_read_bare(string output, string expected) =>
        Assert.Equal(expected, CursorEnvironment.GsettingsValue(output));

    [Fact]
    public void Gtk_settings_file_is_the_last_desktop_source()
    {
        const string ini = """
            [Settings]
            # gtk-cursor-theme-name=Commented
            gtk-theme-name=Adwaita-dark
            gtk-cursor-theme-name = "Breeze"
            gtk-cursor-theme-size=36
            """;

        Assert.Equal(new CursorEnvironment.Fill("36", "Breeze"), Resolve(ini: ini));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("big")]
    [InlineData("24px")]
    public void A_size_that_is_not_a_positive_number_falls_through_to_the_next_source(string bad)
    {
        var fill = Resolve(
            new() { ["HYPRCURSOR_SIZE"] = bad },
            new() { ["cursor-size"] = bad, ["cursor-theme"] = "''" },
            "gtk-cursor-theme-size=40\n");

        Assert.Equal("40", fill.Size);
        Assert.Null(fill.Theme);
    }

    [Fact]
    public void Nothing_anywhere_gives_the_default_size_and_no_theme() =>
        Assert.Equal(new CursorEnvironment.Fill("24", null), Resolve());
}
