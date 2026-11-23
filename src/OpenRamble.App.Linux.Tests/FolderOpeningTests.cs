using OpenRamble.App.Linux;

namespace OpenRamble.App.Linux.Tests;

/// <summary>
/// Showing a file or folder: the freedesktop file manager over D-Bus, <c>xdg-open</c> only when
/// nothing answered.
/// </summary>
public sealed class FolderOpeningTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"openramble-reveal-{Guid.NewGuid():N}", "Запись 1");

    private readonly List<(string Tool, string[] Arguments)> ran = [];
    private readonly List<(string Tool, string[] Arguments)> started = [];

    public FolderOpeningTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);

    private FolderOpening Opening(params string[] succeeding) => new(
        (tool, arguments) =>
        {
            ran.Add((tool, arguments));
            return succeeding.Contains(tool);
        },
        (tool, arguments) => started.Add((tool, arguments)));

    [Fact]
    public void A_file_is_shown_selected_through_busctl()
    {
        string file = Path.Combine(directory, "audio.m4a");
        File.WriteAllText(file, "");

        Opening("busctl").Reveal(file);

        string uri = FolderOpening.FileUri(file);
        var call = Assert.Single(ran);
        Assert.Equal("busctl", call.Tool);
        Assert.Equal(
            ["--user", "call", "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
             "org.freedesktop.FileManager1", "ShowItems", "ass", "1", uri, ""],
            call.Arguments);
        Assert.Empty(started);
    }

    [Fact]
    public void A_folder_is_opened_with_ShowFolders_through_gdbus_when_busctl_fails()
    {
        Opening("gdbus").Reveal(directory);

        string uri = FolderOpening.FileUri(directory);
        Assert.Equal(["busctl", "gdbus"], ran.Select(call => call.Tool));
        Assert.Contains("ShowFolders", ran[0].Arguments);
        Assert.Equal(
            ["call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path",
             "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1.ShowFolders",
             $"['{uri}']", ""],
            ran[1].Arguments);
        Assert.Empty(started);
    }

    [Fact]
    public void Nothing_answering_opens_the_containing_folder_with_xdg_open()
    {
        string file = Path.Combine(directory, "take.wav");
        File.WriteAllText(file, "");

        Opening().Reveal(file);

        var call = Assert.Single(started);
        Assert.Equal("xdg-open", call.Tool);
        Assert.Equal([directory], call.Arguments);
    }

    [Fact]
    public void The_uri_is_percent_encoded_per_segment()
    {
        Assert.Equal(
            "file:///home/me/%D0%97%D0%B0%D0%BF%D0%B8%D1%81%D1%8C%201/it%27s%20%23a.wav",
            FolderOpening.FileUri("/home/me/Запись 1/it's #a.wav"));
    }
}
