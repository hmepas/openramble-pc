namespace OpenRamble.AppCore.Tests;

/// <summary>
/// Six values a person set by hand, and what happens to them when the process dies mid-write.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"settings-tests-{Guid.NewGuid()}");

    private string Path_ => Path.Combine(root, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(root);

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

    [Fact]
    public void TestWhatWasSavedIsWhatComesBack()
    {
        var store = new SettingsStore(Path_);
        var settings = new AppSettings
        {
            Hotkey = nameof(DictationHotkey.RightShift),
            SoundsEnabled = false,
            LearnFromEdits = true,
            OnboardingCompletedRevision = OnboardingRevision.Current,
        };

        store.Write(settings);

        Assert.Equal(settings, store.Read());
    }

    /// <summary>
    /// The save leaves one file behind, not two.
    ///
    /// The staging copy is an implementation detail of writing atomically; a stray one next to the
    /// real file is a puzzle for whoever opens the folder, and a second thing to back up wrong.
    /// </summary>
    [Fact]
    public void TestWritingLeavesNoLeftovers()
    {
        var store = new SettingsStore(Path_);

        store.Write(new AppSettings { Hotkey = nameof(DictationHotkey.CapsLock) });
        store.Write(new AppSettings { Hotkey = nameof(DictationHotkey.RightAlt) });

        Assert.Equal([Path_], Directory.GetFiles(root));
    }

    /// <summary>
    /// A half-written file is what a killed process leaves, and it must not be readable as
    /// "the person never chose anything".
    /// </summary>
    /// <remarks>
    /// The defaults in memory are the right answer to an unreadable file - there is nothing else
    /// to run on. What must not happen is the file being overwritten with them, because that turns
    /// a recoverable accident into a permanent loss. The write path is now a rename, so the
    /// truncated state this test builds by hand is one a crash can no longer produce.
    /// </remarks>
    [Fact]
    public void TestATruncatedFileFallsBackWithoutBeingErased()
    {
        var store = new SettingsStore(Path_);
        store.Write(new AppSettings { Hotkey = nameof(DictationHotkey.RightShift) });
        byte[] whole = File.ReadAllBytes(Path_);
        File.WriteAllBytes(Path_, whole[..(whole.Length / 2)]);

        AppSettings read = store.Read();

        Assert.Null(read.Hotkey);
        Assert.True(read.SoundsEnabled);
        Assert.Equal(0, read.OnboardingCompletedRevision);
        // Still on disk, byte for byte: nothing about a failed read licences a write.
        Assert.Equal(whole.Length / 2, new FileInfo(Path_).Length);
    }

    /// <summary>
    /// A file without a schema version is from before the version existed, and is brought up to
    /// date once: the journal comes on, the removed unload picker goes, and the file is
    /// rewritten so none of it happens again.
    /// </summary>
    [Fact]
    public void TestAnUnversionedFileIsMigratedOnceAndWrittenBack()
    {
        File.WriteAllText(Path_, """
            {
              "hotkey": "CapsLock",
              "detailedLogging": false,
              "modelUnloadTimeout": "never"
            }
            """);
        var store = new SettingsStore(Path_);

        AppSettings read = store.Read();

        Assert.Equal(SettingsStore.CurrentSchemaVersion, read.SchemaVersion);
        Assert.True(read.DetailedLogging);
        Assert.Equal(nameof(DictationHotkey.CapsLock), read.Hotkey);
        string stored = File.ReadAllText(Path_);
        Assert.DoesNotContain("modelUnloadTimeout", stored, StringComparison.Ordinal);
        Assert.Equal(read, store.Read());

        // From here on a false is somebody's choice, and the migration does not run over it.
        store.Write(read with { DetailedLogging = false });
        Assert.False(store.Read().DetailedLogging);
    }

    /// <summary>
    /// A version 1 file still carrying the removed clipboard switch loses it once; nothing else
    /// in it moves - in particular a chosen <c>false</c> for the journal stays.
    /// </summary>
    [Fact]
    public void TestTheRemovedClipboardSwitchIsDroppedFromAVersionOneFile()
    {
        File.WriteAllText(Path_, """
            {
              "hotkey": "CapsLock",
              "copiesToClipboard": true,
              "detailedLogging": false,
              "schemaVersion": 1
            }
            """);
        var store = new SettingsStore(Path_);

        AppSettings read = store.Read();

        Assert.Equal(SettingsStore.CurrentSchemaVersion, read.SchemaVersion);
        Assert.False(read.DetailedLogging);
        Assert.Equal(nameof(DictationHotkey.CapsLock), read.Hotkey);
        Assert.DoesNotContain("copiesToClipboard", File.ReadAllText(Path_), StringComparison.Ordinal);
    }

    /// <summary>A settings file that was never written reads as the defaults, silently.</summary>
    [Fact]
    public void TestAMissingFileIsNotAProblem()
    {
        AppSettings read = new SettingsStore(Path.Combine(root, "nowhere", "settings.json")).Read();

        Assert.Null(read.Hotkey);
        Assert.Equal(0, read.OnboardingCompletedRevision);
    }
}
