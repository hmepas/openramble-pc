namespace OpenRamble.AppCore.Tests;

/// <summary>The log a tester can send - and the promise that it does not exist unasked.</summary>
public sealed class DictationLogFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"log-file-tests-{Guid.NewGuid()}");

    private string Logs => Path.Combine(root, "Logs");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Off means nothing is written, not "written but hidden".
    ///
    /// A file that appears without being asked for is a file nobody consented to, and this one
    /// is going to strangers' machines.
    /// </summary>
    [Fact]
    public void TestWritingIsOffUntilAskedFor()
    {
        Assert.False(SettingsDefaults.DetailedLogging);
        using var log = new DictationLogFile(Logs);
        Assert.False(log.IsEnabled);

        log.Write("this must not reach disk");
        log.Flush();

        Assert.False(Directory.Exists(Logs), "a disabled log created its folder");
        Assert.Null(log.NewestPath());
    }

    /// <summary>
    /// It goes where a person would look for it: beside the app's other files, under a folder
    /// called Logs, one file per day.
    /// </summary>
    [Fact]
    public void TestItLivesSomewhereAPersonWouldLook()
    {
        var paths = new AppPaths(root);
        string directory = DictationLogFile.DirectoryUnder(paths);
        Assert.Equal(Path.Combine(paths.Support(), "Logs"), directory);

        var day = new DateTimeOffset(2026, 8, 21, 9, 12, 33, 421, TimeSpan.FromHours(3));
        using var log = new DictationLogFile(Logs, () => day);
        Assert.Equal(Path.Combine(Logs, "dictation-2026-08-21.log"), log.CurrentPath);
    }

    /// <summary>On, every line lands in today's file with a time stamp and nothing else added.</summary>
    [Fact]
    public void TestALineIsStampedAndAppended()
    {
        var day = new DateTimeOffset(2026, 8, 21, 9, 12, 33, 421, TimeSpan.FromHours(3));
        using var log = new DictationLogFile(Logs, () => day);
        log.IsEnabled = true;

        log.Write("dictation total=1.00s");
        log.Write("engine loaded");
        log.Flush();

        string[] lines = File.ReadAllLines(log.CurrentPath);
        Assert.Equal(["09:12:33.421  dictation total=1.00s", "09:12:33.421  engine loaded"], lines);
        Assert.Equal(log.CurrentPath, log.NewestPath());
    }

    /// <summary>Switching it off takes effect on the next line, not at the next launch.</summary>
    [Fact]
    public void TestTheSwitchTakesEffectAtOnce()
    {
        using var log = new DictationLogFile(Logs);
        log.IsEnabled = true;
        log.Write("first");
        log.IsEnabled = false;
        log.Write("second");
        log.Flush();

        Assert.Equal(["first"], File.ReadAllLines(log.CurrentPath).Select(line => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..]));
    }

    /// <summary>"Off" that leaves files behind is not off: the person can take it all back.</summary>
    [Fact]
    public void TestRemoveAllTakesTheFolderWithIt()
    {
        using var log = new DictationLogFile(Logs);
        log.IsEnabled = true;
        log.Write("something");
        log.RemoveAll();
        log.Flush();

        Assert.False(Directory.Exists(Logs));
    }
}
