using OpenRamble.AppCore;

namespace OpenRamble.AppCore.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string root;
    private readonly AppPaths paths;

    public AppPathsTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"openramble-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(root);
        paths = new AppPaths(root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    // MARK: - Layout

    [Fact]
    public void Scenario001()
    {
        string support = paths.Support();

        Assert.Equal("OpenRamble", Path.GetFileName(support));
        Assert.StartsWith(support, paths.Takes(), StringComparison.Ordinal);
        Assert.StartsWith(support, paths.AudioRecovery(), StringComparison.Ordinal);
        Assert.StartsWith(support, paths.Models(), StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario002()
    {
        // Active recording, recovery audio and model do not overlap.
        string[] directories = [paths.Takes(), paths.AudioRecovery(), paths.Models()];
        Assert.Equal(3, directories.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Scenario003()
    {
        Assert.True(Directory.Exists(paths.Takes()));
        Assert.True(Directory.Exists(paths.AudioRecovery()));
    }

    // MARK: - Backups

    /// <summary>
    /// Speech recordings must not travel.
    ///
    /// The macOS original marks these folders <c>isExcludedFromBackup</c>, because Application
    /// Support goes into Time Machine and iCloud, and this folder holds a person's voice. Windows
    /// has no per-directory backup flag; what it has is the split between <c>%APPDATA%</c>, which
    /// roams and which OneDrive's Known Folder Move syncs, and <c>%LOCALAPPDATA%</c>, which does
    /// neither. Choosing the local root IS the exclusion, so it is checked here rather than
    /// commented.
    /// </summary>
    [Fact]
    public void Scenario004()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // On a machine where the two resolve to the same place the check would prove nothing;
        // it is Windows the promise is about.
        if (string.Equals(local, roaming, StringComparison.Ordinal))
        {
            return;
        }

        Assert.NotEqual(roaming, local);
    }

    // MARK: - Failures

    /// <summary>A file-system failure must reach the caller.</summary>
    [Fact]
    public void Scenario006()
    {
        string support = paths.Support();
        // A file where the folder should be. A directory with that name cannot be created.
        File.WriteAllBytes(Path.Combine(support, "RecoveredAudio"), []);

        Assert.ThrowsAny<IOException>(() => paths.AudioRecovery());
    }

    [Fact]
    public void Scenario007()
    {
        string support = paths.Support();
        File.WriteAllBytes(Path.Combine(support, "Takes"), []);

        Assert.ThrowsAny<IOException>(() => paths.Takes());
    }

    // MARK: - Process interruption

    [Fact]
    public void Scenario008()
    {
        string takes = paths.Takes();
        foreach (string name in new[] { "take-1.wav", "take-2.wav" })
        {
            File.WriteAllText(Path.Combine(takes, name), "sound");
        }

        paths.Takes();

        Assert.Equal(
            ["take-1.wav", "take-2.wav"],
            Directory.GetFiles(takes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scenario009()
    {
        string takes = paths.Takes();
        File.WriteAllText(Path.Combine(takes, "take.wav"), "sound");
        File.WriteAllText(Path.Combine(takes, "readme.txt"), "not ours");

        paths.Takes();

        Assert.Equal(
            ["readme.txt", "take.wav"],
            Directory.GetFiles(takes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scenario010()
    {
        string recovery = paths.AudioRecovery();
        string saved = Path.Combine(recovery, "recording-1.wav");
        File.WriteAllText(saved, "sound");

        paths.Takes();

        Assert.True(File.Exists(saved));
    }

    [Fact]
    public void Scenario011()
    {
        Assert.Empty(Directory.GetFileSystemEntries(paths.Takes()));
    }
}
